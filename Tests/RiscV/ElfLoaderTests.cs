using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using RiscV;
using RiscV.Memory;
using RiscV.Trains;

namespace Tests.RiscV;

public class ElfLoaderTests {
    private static string TestElfPath =>
        Path.Combine(AppContext.BaseDirectory, "test.elf");

    private static uint Reg(FiveStageTrain t, int r) =>
        (uint)t.ArchState.IntegerRegisters.Read(r);

    // ── Loader unit tests ─────────────────────────────────────────────────────

    [Fact]
    public void LoadFile_ReturnsEntryPoint() {
        var mem = new FlatMemory(0x10000);
        ulong entry = ElfLoader.LoadFile(mem, TestElfPath);
        Assert.Equal(0UL, entry); // linker script places _start at 0x0
    }

    [Fact]
    public void LoadFile_FirstInstructionIsStart() {
        // _start begins with: lui sp, 0x10  (encoding 0x00010137)
        var mem = new FlatMemory(0x10000);
        ElfLoader.LoadFile(mem, TestElfPath);
        var firstWord = (uint)mem.Read(0, 4);
        Assert.Equal(0x00010137u, firstWord);
    }

    [Fact]
    public void Load_BadMagic_Throws() {
        var bad = new byte[52];
        bad[0] = 0x7F;
        bad[1] = (byte)'X';
        bad[2] = (byte)'X';
        bad[3] = (byte)'X';
        Assert.Throws<ElfException>(() => ElfLoader.Load(new FlatMemory(64), bad));
    }

    [Fact]
    public void Load_WrongArch_Throws() {
        // Craft a minimal header with valid magic but e_machine = EM_ARM (0x28)
        var fake = new byte[52];
        fake[0] = 0x7F;
        fake[1] = (byte)'E';
        fake[2] = (byte)'L';
        fake[3] = (byte)'F';
        fake[4] = 1; // ELFCLASS32
        fake[5] = 1; // ELFDATA2LSB
        fake[18] = 0x28;
        fake[19] = 0x00; // EM_ARM
        Assert.Throws<ElfException>(() => ElfLoader.Load(new FlatMemory(64), fake));
    }

    [Fact]
    public void Load_TooSmall_Throws() {
        Assert.Throws<ElfException>(() => ElfLoader.Load(new FlatMemory(64), new byte[10]));
    }

    // ── End-to-end execution tests ────────────────────────────────────────────

    [Fact]
    public void RunElf_SumLoop_A0Equals55() {
        // main() computes sum(1..10) = 55 and returns it.
        // After ebreak, a0 (x10) holds the return value.
        var mem = new FlatMemory(0x10000);
        ulong entry = ElfLoader.LoadFile(mem, TestElfPath);

        var train = new FiveStageTrain(new RvMechanism(), mem, entry);
        train.Run();

        Assert.Equal(55u, Reg(train, 10));
    }

    [Fact]
    public void RunElf_Fibonacci_A1Equals13() {
        // main() computes fib(7) = 13 and stores it in a1 before returning.
        var mem = new FlatMemory(0x10000);
        ulong entry = ElfLoader.LoadFile(mem, TestElfPath);

        var train = new FiveStageTrain(new RvMechanism(), mem, entry);
        train.Run();

        Assert.Equal(13u, Reg(train, 11));
    }

    [Fact]
    public void RunElf_ArraySumViaMemory_A2Equals136() {
        // main() stores [1..16] on the stack then sums them; a2 = 1+2+…+16 = 136.
        var mem = new FlatMemory(0x10000);
        ulong entry = ElfLoader.LoadFile(mem, TestElfPath);

        var train = new FiveStageTrain(new RvMechanism(), mem, entry);
        train.Run();

        Assert.Equal(136u, Reg(train, 12));
    }

    [Fact]
    public void RunElf_RetiredCountReflectsActualWork() {
        // A non-trivial program should retire more than a handful of instructions.
        var mem = new FlatMemory(0x10000);
        ulong entry = ElfLoader.LoadFile(mem, TestElfPath);

        var train = new FiveStageTrain(new RvMechanism(), mem, entry);
        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("five_stage.pipeline");
        Assert.NotNull(snap);
        Assert.True(
            snap.Counters["retired"] > 50,
            $"Expected >50 retired instructions, got {snap.Counters["retired"]}"
        );
    }

    [Fact]
    public void RunElf_WithICache_SameResult() {
        // Attaching an I-cache should not change functional output.
        var iCfg = new MemoryConfig(
            512, 4, 16, 5
        );

        var mem = new FlatMemory(0x10000);
        ulong entry = ElfLoader.LoadFile(mem, TestElfPath);

        var train = new FiveStageTrain(new RvMechanism(), mem, entry, iMemConfig: iCfg);
        train.Run();

        Assert.Equal(55u, Reg(train, 10));
        Assert.NotNull(train.ICache);
        Assert.True(train.ICache!.Hits > 0, "Repeated instruction fetches should hit after the first miss");
    }
}