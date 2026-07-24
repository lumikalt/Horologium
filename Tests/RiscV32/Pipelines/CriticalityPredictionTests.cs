#region

using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for critical-path prediction (Fields, Rubin &amp; Bodík,
///     ISCA 2001) as wired into <see cref="OooTrain" /> via <c>enableCriticalityPrediction</c>.
///     <para>
///         The predictor only re-prioritizes <em>issue order</em> among already-ready instructions
///         under functional-unit/port contention — it must never change committed architectural
///         results. Each test here runs the same program twice (feature off vs on) and asserts
///         identical final register state and identical branch-misprediction counts.
///     </para>
/// </summary>
public class CriticalityPredictionTests {
    private static (OooTrain train, FlatMemory mem) Make(
        bool enableCriticalityPrediction,
        int issueWidth = 2,
        int robCapacity = 16,
        int iqCapacity = 8,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity,
            enableCriticalityPrediction: enableCriticalityPrediction
        );
        return (train, mem);
    }

    private static void Load(FlatMemory mem, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    private static void AssertIdenticalArchState(OooTrain off, OooTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    [Fact]
    public void WideIndependentAluOps_ArchStateIdenticalToWithout() {
        // Eight independent ALU ops feeding two dependency chains, well beyond issueWidth=2 and
        // iqCapacity=8 headroom, so multiple ready instructions genuinely compete for issue slots
        // every cycle — exactly the scheduling-priority decision critical-path prediction affects.
        uint[] program = [
            0x00A00093, // addi x1, x0, 10
            0x01400113, // addi x2, x0, 20
            0x001081B3, // add  x3, x1, x1   -- depends on x1
            0x00110213, // addi x4, x2, 1    -- depends on x2
            0x00318133, // add  x2, x3, x3   -- depends on x3 (chain A continues)
            0x00420293, // addi x5, x4, 4    -- depends on x4 (chain B continues)
            0x00518337, // add  x6, x3, x5   -- joins both chains
            0x00100073, // ebreak
        ];

        (OooTrain off, FlatMemory memOff) = Make(false);
        (OooTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        on.Run();

        AssertIdenticalArchState(off, on);
    }

    [Fact]
    public void ExecuteTimeSquash_WithInFlightMultiCycleDiv_ArchStateIdenticalToWithout() {
        // A taken branch mispredicts at execute while an older multi-cycle divide is still in
        // flight (partial squash, not full flush) — exercises the ED (branch-redirect) and EE
        // (multi-cycle producer) critical-path edges alongside ordinary commits.
        uint[] program = [
            0x06400093, // 0:  addi x1, x0, 100
            0x00700113, // 4:  addi x2, x0, 7
            0x0220C1B3, // 8:  div  x3, x1, x2
            0x00100213, // 12: addi x4, x0, 1
            0x00021463, // 16: bne  x4, x0, +8   -- mispredicted taken
            0x3E700293, // 20: addi x5, x0, 999  -- wrong path
            0x02A00313, // 24: addi x6, x0, 42   -- correct-path target
            0x00100073, // 28: ebreak
        ];

        (OooTrain off, FlatMemory memOff) = Make(false);
        (OooTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        DialBoardSnapshot? offSnap = offResult.Find("ooo.pipeline");
        DialBoardSnapshot? onSnap = onResult.Find("ooo.pipeline");
        Assert.NotNull(offSnap);
        Assert.NotNull(onSnap);
        Assert.Equal(offSnap.Counters["branch_misses"], onSnap.Counters["branch_misses"]);
    }

    [Fact]
    public void CallReturnLoop_ArchStateIdenticalToWithout() {
        // Call/loop/return program: exercises JAL, a BLT-driven loop (repeated ROB-stall / commit
        // cadence, CD and CC critical-path edges), and JALR return.
        uint[] program = [
            0x010000EF, // addr  0: jal  x1, 16
            0x00100073, // addr  4: ebreak
            0x00000013, // addr  8: nop
            0x00000013, // addr 12: nop
            0x00000113, // addr 16: addi x2, x0, 0
            0x00500193, // addr 20: addi x3, x0, 5
            0x00110113, // addr 24: addi x2, x2, 1
            0xFE314EE3, // addr 28: blt  x2, x3, -4
            0x00008067, // addr 32: jalr x0, x1, 0
        ];

        (OooTrain off, FlatMemory memOff) = Make(false);
        (OooTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        on.Run();

        AssertIdenticalArchState(off, on);
    }
}