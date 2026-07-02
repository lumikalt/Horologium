using Orrery.Cache;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Integration tests for <see cref="SmtTrain"/>: barrel-processor SMT that
/// distributes <c>issueWidth</c> issue slots round-robin across N hart contexts.
/// <para>
/// Encoded instructions:
///   addi x1, x0, 10  = 0x00A00093
///   addi x1, x0, 20  = 0x01400093
///   addi x1, x0, 30  = 0x01E00093
///   addi x1, x0, 42  = 0x02A00093
///   addi x1, x0, 99  = 0x06300093
///   ebreak            = 0x00100073
/// </para>
/// </summary>
public class SmtTrainTests {
    private const uint Ebreak = 0x00100073;

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    [Fact]
    public void TwoHarts_IssueWidth2_BothProduceCorrectResult() {
        const uint addi42 = 0x02A00093; // addi x1, x0, 42
        const uint addi99 = 0x06300093; // addi x1, x0, 99

        var mem0 = new FlatMemory(0x100);
        var mem1 = new FlatMemory(0x100);
        mem0.Load(0x00, ToBytes(addi42, SmtTrainTests.Ebreak));
        mem1.Load(0x00, ToBytes(addi99, SmtTrainTests.Ebreak));

        var smt = new SmtTrain(
            [new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem0, mem1,],
            issueWidth: 2
        );
        smt.Run(1_000);

        Assert.Equal(42UL, smt.StateOf(0).IntegerRegisters.Read(1));
        Assert.Equal(99UL, smt.StateOf(1).IntegerRegisters.Read(1));
    }

    [Fact]
    public void HartCount_IsCorrect() {
        var mem = new FlatMemory(0x100);
        mem.Load(0x00, ToBytes(SmtTrainTests.Ebreak));

        var smt = new SmtTrain(
            [new Rv32Mechanism(), new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem, mem, mem,],
            issueWidth: 2
        );

        Assert.Equal(3, smt.HartCount);
    }

    [Fact]
    public void ThreeHarts_IssueWidth2_AllProduceCorrectResult() {
        const uint addi10 = 0x00A00093; // addi x1, x0, 10
        const uint addi20 = 0x01400093; // addi x1, x0, 20
        const uint addi30 = 0x01E00093; // addi x1, x0, 30

        var mem0 = new FlatMemory(0x100);
        var mem1 = new FlatMemory(0x100);
        var mem2 = new FlatMemory(0x100);
        mem0.Load(0x00, ToBytes(addi10, SmtTrainTests.Ebreak));
        mem1.Load(0x00, ToBytes(addi20, SmtTrainTests.Ebreak));
        mem2.Load(0x00, ToBytes(addi30, SmtTrainTests.Ebreak));

        // issueWidth=2 with 3 harts: each cycle 2 harts advance; all 3 still complete.
        var smt = new SmtTrain(
            [new Rv32Mechanism(), new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem0, mem1, mem2,],
            issueWidth: 2
        );
        smt.Run(1_000);

        Assert.Equal(10UL, smt.StateOf(0).IntegerRegisters.Read(1));
        Assert.Equal(20UL, smt.StateOf(1).IntegerRegisters.Read(1));
        Assert.Equal(30UL, smt.StateOf(2).IntegerRegisters.Read(1));
    }

    [Fact]
    public void PerHartMoesifCaches_WriteByHart0_ReadByHart1_SeesCoherentValue() {
        // Hart 0 at 0x00: sw x1, 0(x2)  →  writes 0xCAFE to 0x200 via cache0 (→ M)
        //                 ebreak
        // Hart 1 at 0x40: lw x3, 0(x4)  →  reads from 0x200 via cache1
        //                 ebreak
        //
        // With issueWidth=2, both instructions issue in the same cycle:
        //   slot 0 → H0 sw:  cache0 line 0x200 → Modified
        //   slot 1 → H1 lw:  BusRead: cache0 M→writeback+S, cache1 installs S, reads 0xCAFE
        //
        // After run: StateOf(1).IntegerRegisters.Read(3) == 0xCAFE and both caches Shared.

        const uint swX1 = 0x00112023; // sw x1, 0(x2)
        const uint lwX3 = 0x00022183; // lw x3, 0(x4)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(swX1, SmtTrainTests.Ebreak));
        flat.Load(0x40, ToBytes(lwX3, SmtTrainTests.Ebreak));

        var bus = new MoesifBus(flat);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var smt = new SmtTrain(
            [new Rv32Mechanism(), new Rv32Mechanism(),],
            [cache0, cache1,],
            [0x00, 0x40,]
        );

        smt.StateOf(0).IntegerRegisters.Write(1, 0xCAFE); // value to store
        smt.StateOf(0).IntegerRegisters.Write(2, 0x200);  // store address
        smt.StateOf(1).IntegerRegisters.Write(4, 0x200);  // load address

        smt.Run(1_000);

        Assert.Equal(0xCAFEUL, smt.StateOf(1).IntegerRegisters.Read(3));

        Assert.Equal(MoesifState.Owned, cache0.StateOf(0x200)); // writer keeps dirty ownership (MOESIF)
        Assert.Equal(MoesifState.Shared, cache1.StateOf(0x200));
    }

    [Fact]
    public void EntryPoints_PerHart_Respected() {
        // Hart 0 starts at 0x00, hart 1 starts at 0x10 (4 instructions apart).
        // Place addi x1,x0,7 at 0x10 and addi x1,x0,3 at 0x00; verify each hart
        // reads from its own entry point.
        const uint addi3 = 0x00300093; // addi x1, x0, 3
        const uint addi7 = 0x00700093; // addi x1, x0, 7

        var mem = new FlatMemory(0x100);
        mem.Load(0x00, ToBytes(addi3, SmtTrainTests.Ebreak));
        mem.Load(0x10, ToBytes(addi7, SmtTrainTests.Ebreak));

        var smt = new SmtTrain(
            [new Rv32Mechanism(), new Rv32Mechanism(),],
            [mem, mem,],
            [0x00, 0x10,]
        );
        smt.Run(1_000);

        Assert.Equal(3UL, smt.StateOf(0).IntegerRegisters.Read(1));
        Assert.Equal(7UL, smt.StateOf(1).IntegerRegisters.Read(1));
    }
}