using RiscV32.Memory;
using RiscV64.Memory;

namespace Tests.Isa.RiscV64;

/// <summary>
/// Tests for the ELF64 loader. Unlike the RV32 loader tests, there is no RV64 cross-compiler
/// in the project devshell (only riscv32-embedded), so these tests hand-craft minimal ELF64
/// byte layouts rather than loading a pre-built binary.
/// </summary>
public class Rv64ElfLoaderTests {
    // Builds a minimal well-formed ELF64 header + a single PT_LOAD program header describing
    // `payload` loaded at `paddr`, with `bssExtra` zero bytes appended via memsz > filesz.
    private static byte[] BuildElf64(ulong entry, ulong paddr, byte[] payload, ulong bssExtra = 0) {
        const int ehdrSize = 64;
        const int phdrSize = 56;
        var buf = new byte[ehdrSize + phdrSize + payload.Length];

        buf[0] = 0x7F;
        buf[1] = (byte)'E';
        buf[2] = (byte)'L';
        buf[3] = (byte)'F';
        buf[4] = 2; // ELFCLASS64
        buf[5] = 1; // ELFDATA2LSB
        BitConverter.GetBytes((ushort)0xF3).CopyTo(buf, 18); // e_machine = EM_RISCV
        BitConverter.GetBytes(entry).CopyTo(buf, 24);        // e_entry
        BitConverter.GetBytes((ulong)ehdrSize).CopyTo(buf, 32); // e_phoff
        BitConverter.GetBytes((ushort)phdrSize).CopyTo(buf, 54); // e_phentsize
        BitConverter.GetBytes((ushort)1).CopyTo(buf, 56);     // e_phnum

        int ph = ehdrSize;
        BitConverter.GetBytes((uint)1).CopyTo(buf, ph + 0);              // p_type = PT_LOAD
        BitConverter.GetBytes((ulong)(ehdrSize + phdrSize)).CopyTo(buf, ph + 8);  // p_offset
        BitConverter.GetBytes(paddr).CopyTo(buf, ph + 24);                // p_paddr
        BitConverter.GetBytes((ulong)payload.Length).CopyTo(buf, ph + 32); // p_filesz
        BitConverter.GetBytes((ulong)payload.Length + bssExtra).CopyTo(buf, ph + 40); // p_memsz

        payload.CopyTo(buf, ehdrSize + phdrSize);
        return buf;
    }

    [Fact]
    public void Load_ReturnsEntryPoint() {
        byte[] elf = BuildElf64(0x80000000UL, 0x80000000UL, [0x37, 0x01, 0x01, 0x80,]);
        var mem = new FlatMemory(0x10000, 0x80000000);
        ulong entry = Rv64ElfLoader.Load(mem, elf);
        Assert.Equal(0x80000000UL, entry);
    }

    [Fact]
    public void Load_LoadsSegmentBytesAtPaddr() {
        byte[] payload = [0x37, 0x01, 0x01, 0x80,]; // lui sp, 0x80010
        byte[] elf = BuildElf64(0x80000000UL, 0x80000000UL, payload);
        var mem = new FlatMemory(0x10000, 0x80000000);
        Rv64ElfLoader.Load(mem, elf);
        var firstWord = (uint)mem.Read(0x80000000, 4);
        Assert.Equal(0x80010137u, firstWord);
    }

    [Fact]
    public void Load_ZeroFillsBssBeyondFilesz() {
        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD,];
        byte[] elf = BuildElf64(0x80000000UL, 0x80000000UL, payload, bssExtra: 4);
        var mem = new FlatMemory(0x10000, 0x80000000);
        Rv64ElfLoader.Load(mem, elf);
        Assert.Equal(0UL, mem.Read(0x80000004, 4));
    }

    [Fact]
    public void Load_EntryAbove4GiB_RoundTrips() {
        // Exercises the 8-byte e_entry/p_paddr fields that ELF32 couldn't represent.
        const ulong highAddr = 0x1_0000_0000UL;
        byte[] elf = BuildElf64(highAddr, highAddr, [0x13, 0x00, 0x00, 0x00,]);
        var mem = new FlatMemory(0x10000, highAddr);
        ulong entry = Rv64ElfLoader.Load(mem, elf);
        Assert.Equal(highAddr, entry);
    }

    [Fact]
    public void Load_BadMagic_Throws() {
        var bad = new byte[64];
        bad[0] = 0x7F;
        bad[1] = (byte)'X';
        bad[2] = (byte)'X';
        bad[3] = (byte)'X';
        Assert.Throws<ElfException>(() => Rv64ElfLoader.Load(new FlatMemory(64), bad));
    }

    [Fact]
    public void Load_WrongClass_Throws() {
        // Valid magic but ELFCLASS32 (1) instead of ELFCLASS64 (2).
        var fake = new byte[64];
        fake[0] = 0x7F;
        fake[1] = (byte)'E';
        fake[2] = (byte)'L';
        fake[3] = (byte)'F';
        fake[4] = 1; // ELFCLASS32
        fake[5] = 1;
        Assert.Throws<ElfException>(() => Rv64ElfLoader.Load(new FlatMemory(64), fake));
    }

    [Fact]
    public void Load_WrongArch_Throws() {
        byte[] elf = BuildElf64(0x80000000UL, 0x80000000UL, [0x00, 0x00, 0x00, 0x00,]);
        elf[18] = 0x28; // EM_ARM
        elf[19] = 0x00;
        Assert.Throws<ElfException>(() => Rv64ElfLoader.Load(new FlatMemory(64), elf));
    }

    [Fact]
    public void Load_TooSmall_Throws() {
        Assert.Throws<ElfException>(() => Rv64ElfLoader.Load(new FlatMemory(64), new byte[10]));
    }
}
