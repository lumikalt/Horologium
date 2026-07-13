using Mechanism;

namespace Orrery.Devices;

/// <summary>
///     VirtIO 1.2 MMIO transport for a block device (device type 2).
///     <para>
///         Register layout (offsets from device base, §4.2.2 of the VirtIO 1.2 spec):
///         0x000  MagicValue         R   0x74726976 ("virt")
///         0x004  Version            R   2
///         0x008  DeviceID           R   2 (block)
///         0x00C  VendorID           R   0x554D4551 ("QEMU")
///         0x010  DeviceFeatures     R   word 0 or 1 (indexed by DeviceFeaturesSel)
///         0x014  DeviceFeaturesSel  W
///         0x020  DriverFeatures     W   word 0 or 1 (indexed by DriverFeaturesSel)
///         0x024  DriverFeaturesSel  W
///         0x030  QueueSel           W
///         0x034  QueueNumMax        R   64
///         0x038  QueueNum           W
///         0x044  QueueReady         RW
///         0x050  QueueNotify        W   triggers I/O
///         0x060  InterruptStatus    R
///         0x064  InterruptACK       W
///         0x070  Status             RW
///         0x080  QueueDescLow       W
///         0x084  QueueDescHigh      W
///         0x090  QueueAvailLow      W
///         0x094  QueueAvailHigh     W
///         0x0A0  QueueUsedLow       W
///         0x0A4  QueueUsedHigh      W
///         0x0FC  ConfigGeneration   R
///         0x100+ Config             R   block device config (capacity, blk_size, ...)
///     </para>
///     <para>
///         Block device config space (§5.2.4):
///         +0x00  capacity   (le64)  total sectors (512-byte each)
///         +0x14  blk_size   (le32)  512
///     </para>
///     <para>
///         I/O is synchronous: on QueueNotify the device drains the entire available
///         ring before returning control.  <c>guestRam</c> must be the raw
///         backing memory (e.g. <c>FlatMemory</c>), not the
///         <see cref="PeripheralBus" />, so that <c>Load()</c> can be used for bulk
///         DMA writes.
///     </para>
/// </summary>
public sealed class VirtioMmioDevice : IMemory {
    public const ulong DefaultBase = 0x10001000UL;
    public const ulong RegionSize = 0x1000UL;

    private const uint MagicValue = 0x74726976u; // "virt"
    private const uint DevVer = 2u;
    private const uint DeviceId = 2u;          // block
    private const uint VendorId = 0x554D4551u; // "QEMU"
    private const uint QueueMaxSize = 64u;
    private const int SectorSize = 512;

    // Feature bits: VIRTIO_BLK_F_BLK_SIZE (6) | VIRTIO_F_VERSION_1 (bit 32 → word-1 bit 0)
    private const uint Features0 = 1u << 6; // BLK_SIZE valid
    private const uint Features1 = 1u << 0; // VERSION_1

    private const ulong OffMagicValue = 0x000;
    private const ulong OffVersion = 0x004;
    private const ulong OffDeviceId = 0x008;
    private const ulong OffVendorId = 0x00C;
    private const ulong OffDeviceFeatures = 0x010;
    private const ulong OffDeviceFeatsSel = 0x014;
    private const ulong OffDriverFeatures = 0x020;
    private const ulong OffDriverFeatsSel = 0x024;
    private const ulong OffQueueSel = 0x030;
    private const ulong OffQueueNumMax = 0x034;
    private const ulong OffQueueNum = 0x038;
    private const ulong OffQueueReady = 0x044;
    private const ulong OffQueueNotify = 0x050;
    private const ulong OffInterruptStatus = 0x060;
    private const ulong OffInterruptAck = 0x064;
    private const ulong OffStatus = 0x070;
    private const ulong OffQueueDescLow = 0x080;
    private const ulong OffQueueDescHigh = 0x084;
    private const ulong OffQueueAvailLow = 0x090;
    private const ulong OffQueueAvailHigh = 0x094;
    private const ulong OffQueueUsedLow = 0x0A0;
    private const ulong OffQueueUsedHigh = 0x0A4;
    private const ulong OffConfig = 0x100;

    // Descriptor flags
    private const uint DescFNext = 1;
    private const uint DescFWrite = 2;

    // Block request types
    private const uint BlkTIn = 0;
    private const uint BlkTOut = 1;
    private const uint BlkTFlush = 4;

    // Block status codes
    private const byte BlkSOk = 0;
    private const byte BlkSIoerr = 1;
    private const byte BlkSUnsupp = 2;
    private readonly ulong _base;
    private readonly byte[] _config;

    private readonly Stream _disk;
    private readonly IMemory _guestRam;
    private readonly PlicDevice? _plic;
    private readonly int _sourceId;

    // MMIO register state
    private uint _deviceFeatsSel;
    private uint _interruptStatus;
    private ushort _lastAvailIdx;
    private ulong _queueAvailAddr;
    private ulong _queueDescAddr;
    private uint _queueNum;
    private uint _queueReady;
    private ulong _queueUsedAddr;
    private uint _status;

    /// <param name="disk">Host stream backing the virtual disk.</param>
    /// <param name="guestRam">Guest physical memory — used for DMA reads/writes.</param>
    /// <param name="base">MMIO base address.</param>
    /// <param name="plic">Optional PLIC; when provided, I/O completions assert <paramref name="sourceId" />.</param>
    /// <param name="sourceId">PLIC source ID to assert on completion (matches the DTS interrupts property).</param>
    public VirtioMmioDevice(
        Stream disk,
        IMemory guestRam,
        ulong @base = VirtioMmioDevice.DefaultBase,
        PlicDevice? plic = null,
        int sourceId = 1
    ) {
        _disk = disk;
        _guestRam = guestRam;
        _base = @base;
        _plic = plic;
        _sourceId = sourceId;

        // Build config space: capacity (le64 at +0) and blk_size (le32 at +0x14).
        _config = new byte[0x40];
        var sectors = (ulong)(disk.Length / VirtioMmioDevice.SectorSize);
        WriteLeBytes(_config, 0, sectors, 8);
        WriteLeBytes(_config, 0x14, 512, 4);
    }

    /// <summary>True while <c>InterruptACK</c> has not cleared the used-buffer interrupt.</summary>
    public bool InterruptPending => _interruptStatus != 0;

    // ── IMemory ───────────────────────────────────────────────────────────────

    public ulong Read(ulong address, int bytes) {
        ulong off = address - _base;
        if (off >= VirtioMmioDevice.OffConfig && off < VirtioMmioDevice.OffConfig + (ulong)_config.Length)
            return ReadLeBytes(_config, (int)(off - VirtioMmioDevice.OffConfig), bytes);
        return off switch {
            VirtioMmioDevice.OffMagicValue => VirtioMmioDevice.MagicValue,
            VirtioMmioDevice.OffVersion    => VirtioMmioDevice.DevVer,
            VirtioMmioDevice.OffDeviceId   => VirtioMmioDevice.DeviceId,
            VirtioMmioDevice.OffVendorId   => VirtioMmioDevice.VendorId,
            VirtioMmioDevice.OffDeviceFeatures => _deviceFeatsSel == 0
                ? VirtioMmioDevice.Features0
                : VirtioMmioDevice.Features1,
            VirtioMmioDevice.OffQueueNumMax     => VirtioMmioDevice.QueueMaxSize,
            VirtioMmioDevice.OffQueueReady      => _queueReady,
            VirtioMmioDevice.OffInterruptStatus => _interruptStatus,
            VirtioMmioDevice.OffStatus          => _status,
            _                                   => 0u,
        };
    }

    public void Write(ulong address, ulong value, int bytes) {
        ulong off = address - _base;
        switch (off) {
            case VirtioMmioDevice.OffDeviceFeatsSel: _deviceFeatsSel = (uint)value; break;
            case VirtioMmioDevice.OffDriverFeatures: break; // accepted as-is
            case VirtioMmioDevice.OffDriverFeatsSel: break;
            case VirtioMmioDevice.OffQueueSel: break;
            case VirtioMmioDevice.OffQueueNum: _queueNum = Math.Min((uint)value, VirtioMmioDevice.QueueMaxSize); break;
            case VirtioMmioDevice.OffQueueReady: _queueReady = (uint)value; break;
            case VirtioMmioDevice.OffQueueNotify: ProcessQueue(); break;
            case VirtioMmioDevice.OffInterruptAck: {
                _interruptStatus &= ~(uint)value;
                if (_interruptStatus == 0) _plic?.Deassert(_sourceId);
                break;
            }
            case VirtioMmioDevice.OffStatus: HandleStatus((uint)value); break;
            case VirtioMmioDevice.OffQueueDescLow:
                _queueDescAddr = (_queueDescAddr & ~0xFFFF_FFFFul) | (uint)value;
                break;
            case VirtioMmioDevice.OffQueueDescHigh:
                _queueDescAddr = (_queueDescAddr & 0xFFFF_FFFFul) | ((ulong)(uint)value << 32);
                break;
            case VirtioMmioDevice.OffQueueAvailLow:
                _queueAvailAddr = (_queueAvailAddr & ~0xFFFF_FFFFul) | (uint)value;
                break;
            case VirtioMmioDevice.OffQueueAvailHigh:
                _queueAvailAddr = (_queueAvailAddr & 0xFFFF_FFFFul) | ((ulong)(uint)value << 32);
                break;
            case VirtioMmioDevice.OffQueueUsedLow:
                _queueUsedAddr = (_queueUsedAddr & ~0xFFFF_FFFFul) | (uint)value;
                break;
            case VirtioMmioDevice.OffQueueUsedHigh:
                _queueUsedAddr = (_queueUsedAddr & 0xFFFF_FFFFul) | ((ulong)(uint)value << 32);
                break;
        }
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) { }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void HandleStatus(uint value) {
        if (value != 0) {
            _status = value;
            return;
        }

        // Reset: clear all queue state
        _queueNum = _queueReady = _interruptStatus = _status = 0;
        _queueDescAddr = _queueAvailAddr = _queueUsedAddr = 0;
        _lastAvailIdx = 0;
        _plic?.Deassert(_sourceId);
    }

    private void ProcessQueue() {
        if (_queueReady == 0 || _queueNum == 0) return;

        // Read driver's write index from the available ring (offset +2).
        var availIdx = (ushort)_guestRam.Read(_queueAvailAddr + 2, 2);
        // Read device's current used index (offset +2) to know where to write next.
        var usedIdx = (uint)_guestRam.Read(_queueUsedAddr + 2, 2);

        while (_lastAvailIdx != availIdx) {
            var slot = (ushort)(_lastAvailIdx % _queueNum);
            var headIdx = (ushort)_guestRam.Read(_queueAvailAddr + 4 + (ulong)(slot * 2), 2);

            uint bytesWritten = ProcessRequest(headIdx);

            // Append to used ring and advance its index.
            ulong elemAddr = _queueUsedAddr + 4 + usedIdx % _queueNum * 8;
            _guestRam.Write(elemAddr, headIdx, 4);          // id
            _guestRam.Write(elemAddr + 4, bytesWritten, 4); // len
            usedIdx++;
            _guestRam.Write(_queueUsedAddr + 2, usedIdx, 4);

            _lastAvailIdx++;
        }

        _interruptStatus |= 1; // VIRTQ used-buffer notification
        _plic?.Assert(_sourceId);
    }

    private uint ProcessRequest(ushort headIdx) {
        // Parse the descriptor chain: header → data... → status
        // Header descriptor (device-readable, 16 bytes: type/reserved/sector)
        ulong hdrDescBase = _queueDescAddr + (ulong)(headIdx * 16);
        ulong hdrAddr = _guestRam.Read(hdrDescBase, 8);
        _ = (uint)_guestRam.Read(hdrDescBase + 12, 2);
        var dataCur = (ushort)_guestRam.Read(hdrDescBase + 14, 2);

        var reqType = (uint)_guestRam.Read(hdrAddr, 4);
        ulong sector = _guestRam.Read(hdrAddr + 8, 8);

        // Collect data + status descriptors
        var chain = new List<(ulong Addr, uint Len, bool Write)>();
        ushort cur = dataCur;
        while (true) {
            ulong db = _queueDescAddr + (ulong)(cur * 16);
            ulong dAddr = _guestRam.Read(db, 8);
            var dLen = (uint)_guestRam.Read(db + 8, 4);
            var dFlags = (uint)_guestRam.Read(db + 12, 2);
            var dNext = (ushort)_guestRam.Read(db + 14, 2);

            chain.Add((dAddr, dLen, (dFlags & VirtioMmioDevice.DescFWrite) != 0));
            if ((dFlags & VirtioMmioDevice.DescFNext) == 0) break;
            cur = dNext;
        }

        // Last element is always the 1-byte status descriptor (device-writable).
        ulong statusAddr = chain[^1].Addr;
        int ioCount = chain.Count - 1;

        byte status;
        uint totalData = 0;
        try {
            switch (reqType) {
                case VirtioMmioDevice.BlkTIn: {
                    // Read sectors from disk into guest RAM buffers.
                    _disk.Seek((long)(sector * VirtioMmioDevice.SectorSize), SeekOrigin.Begin);
                    for (var i = 0; i < ioCount; i++) {
                        (ulong addr, uint len, _) = chain[i];
                        var buf = new byte[len];
                        _disk.ReadExactly(buf);
                        _guestRam.Load(addr, buf);
                        totalData += len;
                    }

                    status = VirtioMmioDevice.BlkSOk;
                    break;
                }
                case VirtioMmioDevice.BlkTOut: {
                    // Write guest RAM buffers to disk.
                    _disk.Seek((long)(sector * VirtioMmioDevice.SectorSize), SeekOrigin.Begin);
                    for (var i = 0; i < ioCount; i++) {
                        (ulong addr, uint len, _) = chain[i];
                        byte[] buf = ReadGuestBytes(addr, len);
                        _disk.Write(buf);
                        totalData += len;
                    }

                    status = VirtioMmioDevice.BlkSOk;
                    break;
                }
                case VirtioMmioDevice.BlkTFlush: {
                    _disk.Flush();
                    status = VirtioMmioDevice.BlkSOk;
                    break;
                }
                default: status = VirtioMmioDevice.BlkSUnsupp; break;
            }
        }
        catch (Exception) { status = VirtioMmioDevice.BlkSIoerr; }

        _guestRam.Write(statusAddr, status, 1);
        return totalData;
    }

    private byte[] ReadGuestBytes(ulong addr, uint len) {
        var buf = new byte[len];
        for (uint i = 0; i < len; i++) buf[i] = (byte)_guestRam.Read(addr + i, 1);
        return buf;
    }

    private static ulong ReadLeBytes(byte[] arr, int offset, int count) {
        ulong v = 0;
        for (var i = 0; i < count && offset + i < arr.Length; i++) v |= (ulong)arr[offset + i] << (i * 8);
        return v;
    }

    private static void WriteLeBytes(byte[] arr, int offset, ulong value, int count) {
        for (var i = 0; i < count; i++) arr[offset + i] = (byte)(value >> (i * 8));
    }
}