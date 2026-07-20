#region

using Mechanism;

#endregion

namespace Orrery.Devices;

/// <summary>
///     Memory-mapped Core Local Interruptor (CLINT) compatible with the RISC-V virt machine.
///     Register layout (from CLINT base address):
///     +0x0000       msip[0]        — hart 0 machine software interrupt pending (4 bytes, per hart)
///     +0x0004       msip[1]        — hart 1 machine software interrupt pending
///     …
///     +0x4000       mtimecmp[0]_lo — hart 0 timer compare low word
///     +0x4004       mtimecmp[0]_hi — hart 0 timer compare high word
///     +0x4008       mtimecmp[1]_lo — hart 1 timer compare low word
///     …
///     +0xBFF8       mtime_lo       — global real-time counter low word
///     +0xBFFC       mtime_hi       — global real-time counter high word
///     <para>
///         mtime advances by <see cref="TicksPerInstruction" /> on every <see cref="Advance" /> call.
///         The ISA-specific trap controller (e.g. <c>RvTrapController</c>) calls <see cref="Advance" />
///         and then queries <see cref="TimerPending" /> / <see cref="SoftwarePending" /> to update
///         the hart's interrupt-pending register before checking for pending interrupts.
///     </para>
/// </summary>
public sealed class ClintDevice(int maxHarts = 1) : IMemory {
    public const ulong DefaultBase = 0x02000000UL;
    public const ulong RegionSize = 0x00010000UL; // 64 KiB covers msip, mtimecmp, mtime

    private const ulong MtimeLo = 0xBFF8;
    private const ulong MtimeHi = 0xBFFC;
    private const ulong MtimecmpBase = 0x4000; // mtimecmp[h] at +0x4000 + h*8
    private readonly uint[] _msip = new uint[maxHarts];
    private readonly ulong[] _mtimecmp = Enumerable.Repeat(ulong.MaxValue, maxHarts).ToArray();

    private ulong _mtime;


    // How many mtime ticks advance per Advance() call (one call per instruction boundary).
    // With a DTB timebase-frequency of 1_000_000, each tick = 1 µs of simulated time.
    public ulong TicksPerInstruction { get; init; } = 1;

    public ulong Read(ulong address, int bytes) {
        ulong offset = address - ClintDevice.DefaultBase;
        return offset switch {
            ClintDevice.MtimeLo                    => (uint)_mtime,
            ClintDevice.MtimeHi                    => (uint)(_mtime >> 32),
            _ when IsMtimecmpLo(offset, out int h) => (uint)_mtimecmp[h],
            _ when IsMtimecmpHi(offset, out int h) => (uint)(_mtimecmp[h] >> 32),
            _ when IsMsip(offset, out int h)       => _msip[h],
            _                                      => 0,
        };
    }

    public void Write(ulong address, ulong value, int bytes) {
        ulong offset = address - ClintDevice.DefaultBase;

        if (IsMtimecmpLo(offset, out int h))
            // Write low 32 bits; preserve high.
            _mtimecmp[h] = (_mtimecmp[h] & 0xFFFF_FFFF_0000_0000UL) | (uint)value;
        else if (IsMtimecmpHi(offset, out h))
            _mtimecmp[h] = (_mtimecmp[h] & 0x0000_0000_FFFF_FFFFUL) | ((ulong)(uint)value << 32);
        else if (offset == ClintDevice.MtimeLo)
            _mtime = (_mtime & 0xFFFF_FFFF_0000_0000UL) | (uint)value;
        else if (offset == ClintDevice.MtimeHi)
            _mtime = (_mtime & 0x0000_0000_FFFF_FFFFUL) | ((ulong)(uint)value << 32);
        else if (IsMsip(offset, out h)) _msip[h] = (uint)value & 1;
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) { }

    /// <summary>Advances mtime by <see cref="TicksPerInstruction" />.</summary>
    public void Advance() => _mtime += TicksPerInstruction;

    /// <summary>
    ///     Fast-forward mtime to mtimecmp[0] so the timer fires on the next <see cref="Advance" /> call.
    ///     Used by the WFI handler to avoid burning millions of ticks spinning in the idle loop.
    ///     No-op if no timer is armed (mtimecmp == ulong.MaxValue) or timer is already pending.
    /// </summary>
    public void SkipToTimer() {
        ulong cmp = _mtimecmp[0];
        if (cmp != ulong.MaxValue && _mtime < cmp) _mtime = cmp;
    }

    /// <summary>True when mtime ≥ mtimecmp for <paramref name="hartId" />.</summary>
    public bool TimerPending(int hartId = 0) => _mtime >= _mtimecmp[hartId];

    /// <summary>True when msip is set for <paramref name="hartId" />.</summary>
    public bool SoftwarePending(int hartId = 0) => _msip[hartId] != 0;

    private bool IsMtimecmpLo(ulong offset, out int hart) {
        if (offset >= ClintDevice.MtimecmpBase && (offset - ClintDevice.MtimecmpBase) % 8 == 0) {
            hart = (int)((offset - ClintDevice.MtimecmpBase) / 8);
            return hart < maxHarts;
        }

        hart = 0;
        return false;
    }

    private bool IsMtimecmpHi(ulong offset, out int hart) {
        if (offset >= ClintDevice.MtimecmpBase + 4 && (offset - ClintDevice.MtimecmpBase - 4) % 8 == 0) {
            hart = (int)((offset - ClintDevice.MtimecmpBase - 4) / 8);
            return hart < maxHarts;
        }

        hart = 0;
        return false;
    }

    private static bool IsMsip(ulong offset, out int hart) {
        if (offset < ClintDevice.MtimecmpBase && offset % 4 == 0) {
            hart = (int)(offset / 4);
            return true;
        }

        hart = 0;
        return false;
    }
}