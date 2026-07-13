using System.Buffers.Binary;
using System.Text;
using Mechanism;

namespace RiscV32.Memory;

/// <summary>
///     An <see cref="IWorkload" /> that loads a bare-metal ELF32 RISC-V binary.
///     Entry point and minimum memory size are derived from the ELF headers so
///     callers need only supply the file path.
/// </summary>
public sealed class Rv32ElfWorkload : IWorkload {
    private readonly byte[] _elfBytes;

    public Rv32ElfWorkload(string path, int? memorySizeBytes = null)
        : this(File.ReadAllBytes(path), memorySizeBytes) { }

    public Rv32ElfWorkload(byte[] elfBytes, int? memorySizeBytes = null) {
        _elfBytes = elfBytes;
        EntryPoint = ParseEntryPoint(elfBytes);
        BaseAddress = ComputeBaseAddress(elfBytes);
        MemorySize = memorySizeBytes ?? ComputeMinMemorySize(elfBytes, BaseAddress);
        HtifTohostAddress = TryFindSymbol("tohost", out ulong tohost) ? tohost : null;
        InitialBreak = ComputeInitialBreak(elfBytes);
    }

    /// <summary>
    ///     Address just past the last PT_LOAD segment (i.e. the initial program break).
    ///     Pass to <see cref="RiscV32.Syscalls.LinuxSyscallEmulator" /> as <c>initialBreak</c>
    ///     so SYS_brk starts from the correct address.
    /// </summary>
    public ulong InitialBreak { get; }

    public ulong EntryPoint { get; }
    public int MemorySize { get; }
    public int CodeSize => _elfBytes.Length;

    /// <summary>
    ///     The physical base address of the first PT_LOAD segment, e.g. 0x80000000
    ///     for Spike-compatible ELFs. Pass this to FlatMemory's constructor so that
    ///     the backing array covers only the actual code/data range.
    /// </summary>
    public ulong BaseAddress { get; }

    /// <summary>The HTIF <c>tohost</c> exit register address if the ELF exports it; null otherwise.</summary>
    public ulong? HtifTohostAddress { get; }

    public void Load(IMemory memory) => Rv32ElfLoader.Load(memory, _elfBytes);

    /// <summary>
    ///     Wraps <paramref name="memory" /> with <see cref="HtifMemory" /> when the ELF contains
    ///     a <c>tohost</c> symbol, executing fesvr magic-mem syscalls and ACK-ing fromhost.
    ///     <paramref name="output" /> receives SYS_write output; when null, output is discarded.
    /// </summary>
    public IMemory WrapMemory(IMemory memory, TextWriter? output = null) =>
        TryFindSymbol("tohost", out ulong tohost) ? new HtifMemory(memory, tohost, output) : memory;

    // The interface method has no output parameter; route it through the concrete overload.
    IMemory IWorkload.WrapMemory(IMemory memory) => WrapMemory(memory);

    /// <summary>
    ///     Returns the virtual address of a named ELF symbol, or throws if not found.
    /// </summary>
    public ulong FindSymbol(string name) {
        ReadOnlySpan<byte> elf = _elfBytes;
        uint shoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[32..]);
        ushort shentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[46..]);
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[48..]);

        for (var i = 0; i < shnum; i++) {
            var shdr = (int)(shoff + (uint)(i * shentsz));
            uint shType = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 4)..]);
            if (shType != 2) continue; // SHT_SYMTAB

            uint symOff = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 16)..]);
            uint symSz = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 20)..]);
            uint strtabIdx = BinaryPrimitives.ReadUInt32LittleEndian(elf[(shdr + 24)..]);

            var strtabHdr = (int)(shoff + strtabIdx * shentsz);
            uint strtabOff = BinaryPrimitives.ReadUInt32LittleEndian(elf[(strtabHdr + 16)..]);

            for (uint s = 0; s < symSz / 16; s++) {
                var sym = (int)(symOff + s * 16);
                uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(elf[sym..]);
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(elf[(sym + 4)..]);

                var start = (int)(strtabOff + nameOff);
                int end = start;
                while (end < _elfBytes.Length && _elfBytes[end] != 0) end++;
                if (Encoding.ASCII.GetString(_elfBytes, start, end - start) == name) return value;
            }
        }

        throw new KeyNotFoundException($"ELF symbol '{name}' not found");
    }

    /// <summary>Like <see cref="FindSymbol" /> but returns false instead of throwing.</summary>
    public bool TryFindSymbol(string name, out ulong address) {
        try {
            address = FindSymbol(name);
            return true;
        }
        catch (KeyNotFoundException) {
            address = 0;
            return false;
        }
    }

    // ── ELF header parsing ────────────────────────────────────────────────────

    private static ulong ParseEntryPoint(ReadOnlySpan<byte> elf) =>
        BinaryPrimitives.ReadUInt32LittleEndian(elf[24..]);

    private static ulong ComputeBaseAddress(ReadOnlySpan<byte> elf) {
        uint phoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[28..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[42..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[44..]);

        var minBase = uint.MaxValue;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (uint)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            uint paddr = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 12)..]);
            if (paddr < minBase) minBase = paddr;
        }

        return minBase == uint.MaxValue ? 0u : minBase;
    }

    private static int ComputeMinMemorySize(ReadOnlySpan<byte> elf, ulong baseAddress) {
        uint phoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[28..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[42..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[44..]);

        uint maxEnd = 0;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (uint)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            uint paddr = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 12)..]);
            uint memsz = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 20)..]);
            maxEnd = Math.Max(maxEnd, paddr + memsz);
        }

        // Size relative to the base address, rounded to next 64 KB + 64 KB for stack/heap.
        uint relativeEnd = maxEnd - (uint)baseAddress;
        return (int)((relativeEnd + 0xFFFF) & ~0xFFFFU) + 0x10000;
    }

    private static ulong ComputeInitialBreak(ReadOnlySpan<byte> elf) {
        uint phoff = BinaryPrimitives.ReadUInt32LittleEndian(elf[28..]);
        ushort phentsz = BinaryPrimitives.ReadUInt16LittleEndian(elf[42..]);
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[44..]);

        uint maxEnd = 0;
        for (var i = 0; i < phnum; i++) {
            var ph = (int)(phoff + (uint)(i * phentsz));
            if (BinaryPrimitives.ReadUInt32LittleEndian(elf[ph..]) != 1) continue; // PT_LOAD = 1
            uint paddr = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 12)..]);
            uint memsz = BinaryPrimitives.ReadUInt32LittleEndian(elf[(ph + 20)..]);
            maxEnd = Math.Max(maxEnd, paddr + memsz);
        }

        // Round up to the next page boundary (4 KiB).
        return (maxEnd + 0xFFFU) & ~0xFFFUL;
    }
}