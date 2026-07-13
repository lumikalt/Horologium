using Orrery.Devices;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
///     Unit tests for <see cref="VirtioMmioDevice" />.
///     Tests drive the device entirely through its MMIO register interface,
///     planting descriptor tables and rings directly in a <see cref="FlatMemory" />,
///     and verify the used ring and DMA buffers after QueueNotify.
///     Memory layout used by all tests (base 0x1000):
///     0x1000  Descriptor table  (QueueNum=4, 4×16 = 64 bytes)
///     0x1040  Available ring    (2+2+4×2 = 12 bytes)
///     0x1060  Used ring         (2+2+4×8 = 36 bytes)
///     0x1100  Request header    (16 bytes: type/reserved/sector)
///     0x1110  Status byte       (1 byte, device-writable)
///     0x1200  Data buffer       (1 or more 512-byte sectors)
/// </summary>
public class VirtioBlockDeviceTests {
    private const ulong RamBase = 0x1000UL;
    private const int RamSize = 0x4000;
    private const uint QueueNum = 4u;

    private const ulong DescBase = 0x1000UL;
    private const ulong AvailBase = 0x1040UL;
    private const ulong UsedBase = 0x1060UL;
    private const ulong HdrBase = 0x1100UL;
    private const ulong StatusOff = 0x1110UL;
    private const ulong DataBase = 0x1200UL;

    private const uint DescFNext = 1u;
    private const uint DescFWrite = 2u;

    // ── helpers ───────────────────────────────────────────────────────────────

    private static (VirtioMmioDevice dev, FlatMemory ram) Make(Stream disk) {
        var ram = new FlatMemory(VirtioBlockDeviceTests.RamSize, VirtioBlockDeviceTests.RamBase);
        var dev = new VirtioMmioDevice(disk, ram);
        return (dev, ram);
    }

    private static void InitDevice(VirtioMmioDevice dev) {
        ulong b = VirtioMmioDevice.DefaultBase;
        dev.Write(b + 0x070, 0u, 4);  // reset
        dev.Write(b + 0x070, 15u, 4); // ACKNOWLEDGE | DRIVER | FEATURES_OK | DRIVER_OK
        dev.Write(b + 0x030, 0u, 4);  // QueueSel = 0
        dev.Write(b + 0x038, VirtioBlockDeviceTests.QueueNum, 4);
        dev.Write(b + 0x080, VirtioBlockDeviceTests.DescBase, 4);  // QueueDescLow
        dev.Write(b + 0x084, 0u, 4);                               // QueueDescHigh
        dev.Write(b + 0x090, VirtioBlockDeviceTests.AvailBase, 4); // QueueAvailLow
        dev.Write(b + 0x094, 0u, 4);
        dev.Write(b + 0x0A0, VirtioBlockDeviceTests.UsedBase, 4); // QueueUsedLow
        dev.Write(b + 0x0A4, 0u, 4);
        dev.Write(b + 0x044, 1u, 4); // QueueReady
    }

    // Plant a 3-descriptor chain for a single-sector I/O:
    //   desc 0: header  (device-readable, 16 bytes at HdrBase)
    //   desc 1: data    (device-writable for IN, device-readable for OUT; 512 bytes at DataBase)
    //   desc 2: status  (device-writable, 1 byte at StatusOff)
    private static void PlantChain(FlatMemory ram, uint reqType, ulong sector, bool isRead) {
        // Request header: type(4) | reserved(4) | sector(8)
        ram.Write(VirtioBlockDeviceTests.HdrBase, reqType, 4);
        ram.Write(VirtioBlockDeviceTests.HdrBase + 4, 0u, 4);
        ram.Write(VirtioBlockDeviceTests.HdrBase + 8, sector, 8);

        // Descriptor 0: header (device-readable)
        WriteDesc(ram, 0, VirtioBlockDeviceTests.HdrBase, 16, VirtioBlockDeviceTests.DescFNext, 1);
        // Descriptor 1: data (WRITE for IN, no WRITE for OUT)
        uint dataFlags = (isRead ? VirtioBlockDeviceTests.DescFWrite : 0u) | VirtioBlockDeviceTests.DescFNext;
        WriteDesc(ram, 1, VirtioBlockDeviceTests.DataBase, 512, dataFlags, 2);
        // Descriptor 2: status (device-writable)
        WriteDesc(ram, 2, VirtioBlockDeviceTests.StatusOff, 1, VirtioBlockDeviceTests.DescFWrite, 0);

        // Available ring: idx = 1, ring[0] = 0 (head descriptor index)
        ram.Write(VirtioBlockDeviceTests.AvailBase, 0u, 2);     // flags
        ram.Write(VirtioBlockDeviceTests.AvailBase + 2, 1u, 2); // idx
        ram.Write(VirtioBlockDeviceTests.AvailBase + 4, 0u, 2); // ring[0] = head 0
    }

    private static void WriteDesc(FlatMemory ram, uint idx, ulong addr, uint len, uint flags, ushort next) {
        ulong b = VirtioBlockDeviceTests.DescBase + idx * 16;
        ram.Write(b, addr, 8);
        ram.Write(b + 8, len, 4);
        ram.Write(b + 12, flags, 2);
        ram.Write(b + 14, next, 2);
    }

    // ── MMIO identity tests ───────────────────────────────────────────────────

    [Fact]
    public void MagicValue_Read_Returns_Virt() {
        (VirtioMmioDevice dev, _) = Make(Stream.Null);
        Assert.Equal(0x74726976UL, dev.Read(VirtioMmioDevice.DefaultBase + 0x000, 4));
    }

    [Fact]
    public void Version_Read_Returns_2() {
        (VirtioMmioDevice dev, _) = Make(Stream.Null);
        Assert.Equal(2UL, dev.Read(VirtioMmioDevice.DefaultBase + 0x004, 4));
    }

    [Fact]
    public void DeviceID_Read_Returns_2_Block() {
        (VirtioMmioDevice dev, _) = Make(Stream.Null);
        Assert.Equal(2UL, dev.Read(VirtioMmioDevice.DefaultBase + 0x008, 4));
    }

    [Fact]
    public void Status_Reset_Clears_State() {
        (VirtioMmioDevice dev, _) = Make(Stream.Null);
        dev.Write(VirtioMmioDevice.DefaultBase + 0x070, 15u, 4);
        dev.Write(VirtioMmioDevice.DefaultBase + 0x070, 0u, 4);
        Assert.Equal(0UL, dev.Read(VirtioMmioDevice.DefaultBase + 0x070, 4));
    }

    [Fact]
    public void Config_Capacity_Matches_DiskSize() {
        using var disk = new MemoryStream(new byte[1024]); // 2 sectors
        (VirtioMmioDevice dev, _) = Make(disk);
        ulong lo = dev.Read(VirtioMmioDevice.DefaultBase + 0x100, 4);
        ulong hi = dev.Read(VirtioMmioDevice.DefaultBase + 0x104, 4);
        ulong capacity = lo | (hi << 32);
        Assert.Equal(2UL, capacity);
    }

    [Fact]
    public void Config_BlkSize_Is_512() {
        (VirtioMmioDevice dev, _) = Make(Stream.Null);
        ulong blkSize = dev.Read(VirtioMmioDevice.DefaultBase + 0x114, 4);
        Assert.Equal(512UL, blkSize);
    }

    // ── Queue init tests ──────────────────────────────────────────────────────

    [Fact]
    public void QueueReady_AfterInit_ReadsBack_One() {
        (VirtioMmioDevice dev, _) = Make(Stream.Null);
        InitDevice(dev);
        Assert.Equal(1UL, dev.Read(VirtioMmioDevice.DefaultBase + 0x044, 4));
    }

    [Fact]
    public void QueueNotify_BeforeQueueReady_DoesNothing() {
        using var disk = new MemoryStream(new byte[512]);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);
        dev.Write(VirtioMmioDevice.DefaultBase + 0x044, 0u, 4); // QueueReady = 0
        PlantChain(ram, 0, 0, true);
        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4); // QueueNotify
        // used.idx should still be 0
        Assert.Equal(0UL, ram.Read(VirtioBlockDeviceTests.UsedBase + 2, 2));
    }

    // ── Block read (VIRTIO_BLK_T_IN = 0) ─────────────────────────────────────

    [Fact]
    public void BlockRead_Sector0_WritesDataToGuestRam() {
        var diskData = new byte[512];
        for (var i = 0; i < 512; i++) diskData[i] = (byte)(i & 0xFF);
        using var disk = new MemoryStream(diskData);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);
        PlantChain(ram, 0, 0, true);

        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4); // QueueNotify

        // Verify first 4 bytes of data buffer
        ulong actual = ram.Read(VirtioBlockDeviceTests.DataBase, 4);
        ulong expected = diskData[0] | ((ulong)diskData[1] << 8)
                                     | ((ulong)diskData[2] << 16) | ((ulong)diskData[3] << 24);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BlockRead_StatusByte_IsZero_OnSuccess() {
        using var disk = new MemoryStream(new byte[512]);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);
        PlantChain(ram, 0, 0, true);

        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4);

        Assert.Equal(0UL, ram.Read(VirtioBlockDeviceTests.StatusOff, 1));
    }

    [Fact]
    public void BlockRead_UsedRing_IndexAdvanced() {
        using var disk = new MemoryStream(new byte[512]);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);
        PlantChain(ram, 0, 0, true);

        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4);

        Assert.Equal(1UL, ram.Read(VirtioBlockDeviceTests.UsedBase + 2, 2)); // used.idx = 1
        Assert.Equal(0UL, ram.Read(VirtioBlockDeviceTests.UsedBase + 4, 4)); // used.ring[0].id = 0 (head desc)
    }

    [Fact]
    public void BlockRead_InterruptStatus_SetAfterNotify() {
        using var disk = new MemoryStream(new byte[512]);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);
        PlantChain(ram, 0, 0, true);

        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4);

        Assert.Equal(1UL, dev.Read(VirtioMmioDevice.DefaultBase + 0x060, 4));
        Assert.True(dev.InterruptPending);
    }

    [Fact]
    public void BlockRead_InterruptAck_ClearsInterruptStatus() {
        using var disk = new MemoryStream(new byte[512]);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);
        PlantChain(ram, 0, 0, true);
        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4);

        dev.Write(VirtioMmioDevice.DefaultBase + 0x064, 1u, 4); // InterruptACK

        Assert.Equal(0UL, dev.Read(VirtioMmioDevice.DefaultBase + 0x060, 4));
        Assert.False(dev.InterruptPending);
    }

    // ── Block write (VIRTIO_BLK_T_OUT = 1) ───────────────────────────────────

    [Fact]
    public void BlockWrite_Sector0_PersistsToDisk() {
        using var disk = new MemoryStream(new byte[512]);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);

        // Plant known data in the DMA buffer before posting the write.
        const ulong sentinel = 0xDEADBEEFCAFEBABEUL;
        ram.Write(VirtioBlockDeviceTests.DataBase, sentinel, 8);

        PlantChain(ram, 1, 0, false);
        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4);

        Assert.Equal(0UL, ram.Read(VirtioBlockDeviceTests.StatusOff, 1)); // status OK
        // Verify the disk received the bytes.
        disk.Seek(0, SeekOrigin.Begin);
        var result = new byte[8];
        disk.ReadExactly(result);
        ulong got = 0;
        for (var i = 0; i < 8; i++) got |= (ulong)result[i] << (i * 8);
        Assert.Equal(sentinel, got);
    }

    // ── Unsupported request type ──────────────────────────────────────────────

    [Fact]
    public void UnknownRequestType_StatusByte_IsUnsupp() {
        using var disk = new MemoryStream(new byte[512]);
        (VirtioMmioDevice dev, FlatMemory ram) = Make(disk);
        InitDevice(dev);
        PlantChain(ram, 99u, 0, true); // type 99 is unsupported

        dev.Write(VirtioMmioDevice.DefaultBase + 0x050, 0u, 4);

        Assert.Equal(2UL, ram.Read(VirtioBlockDeviceTests.StatusOff, 1)); // VIRTIO_BLK_S_UNSUPP
    }
}