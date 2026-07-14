using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for NoSQ speculative memory bypassing (Sha, Martin
///     &amp; Roth, MICRO 2006; Tyson &amp; Austin, MICRO 1997) as wired into
///     <see cref="OooeTrain" /> via <c>enableSmbBypass</c> (<see cref="SmbPredictor" />).
///     <para>
///         A bypassed load still executes for real through the ordinary pipeline in the
///         background; the early bypass only supplies a speculative early result. Every test
///         here runs the same program twice (feature off vs on) and asserts identical final
///         architectural register state, regardless of whether the bypass predictions made
///         during the run were correct, wrong, or never attempted.
///     </para>
/// </summary>
public class SmbBypassTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        bool enableSmbBypass,
        int issueWidth = 2,
        int robCapacity = 32,
        int iqCapacity = 16,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity,
            enableSmbBypass: enableSmbBypass
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

    private static void AssertIdenticalArchState(OooeTrain off, OooeTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    /// <summary>
    ///     Store/reload loop at a fixed SSN distance every iteration (assembled from:
    ///     <c>
    ///         addi x1,x0,100; addi x2,x0,0; addi x3,x0,20; loop: sw x2,0(x1); lw x4,0(x1);
    ///         addi x2,x4,1; addi x3,x3,-1; bne x3,x0,loop; ebreak
    ///     </c>
    ///     ). The predictor is cold at
    ///     first (no prediction has ever been trained), so the first couple of iterations
    ///     forward ordinarily and teach it the distance; once confident, later iterations
    ///     take the early-bypass path. Every bypass here is address- and width-consistent
    ///     with the producing store, so none should ever mispredict.
    /// </summary>
    [Fact]
    public void FixedDistanceStoreReloadLoop_ArchStateIdenticalToWithout_AndBypassesOccur() {
        uint[] program = [
            0x06400093, // addi x1, x0, 100
            0x00000113, // addi x2, x0, 0
            0x01400193, // addi x3, x0, 20
            0x0020A023, // loop: sw x2, 0(x1)
            0x0000A203, // lw x4, 0(x1)
            0x00120113, // addi x2, x4, 1
            0xFFF18193, // addi x3, x3, -1
            0xFE0198E3, // bne x3, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "smb_bypasses"));
        Assert.True(Counter(onResult, "smb_bypasses") > 0, "no SMB bypass was ever attempted");
        Assert.Equal(0L, Counter(onResult, "smb_mispredicts"));
    }

    /// <summary>
    ///     A single loop, closed by the same plain "mostly-taken" pattern proven in the
    ///     previous test, always executes <c>sw x2,0(x1); lw x4,0(x5)</c> — same store and load
    ///     PC every iteration, training the predictor at SSN distance 1. x5 (the load's address
    ///     register) starts equal to x1 (100) and is redirected to a never-written scratch
    ///     address (104) exactly one iteration before the loop ends, via a rare <c>beq</c>
    ///     positioned *after* the load — so it can never delay the load's own completion (any
    ///     dependency chain feeding the load's address directly, even a single extra
    ///     instruction, was empirically found to push the load's shadow completion past the
    ///     producing store's commit, permanently starving both ordinary forwarding and SMB
    ///     training; likewise a second branch or an indirect call/return *between* producer and
    ///     load was found to have the same effect). Because dispatch-time bypass eligibility
    ///     never depends on either side's address (that's the whole point of NoSQ's
    ///     SSN-distance prediction), the by-then-confident predictor still bypasses the last
    ///     iteration's load using the store at 100, while its real (shadow) execution reads the
    ///     untouched zero at 104 — a guaranteed mismatch (the accumulator is non-zero by then),
    ///     exercising mispredict detection, commit-time squash + retrain, and re-execution
    ///     recovery.
    /// </summary>
    [Fact]
    public void MidLoopDistanceShift_ArchStateIdenticalToWithout_AndMispredictOccurs() {
        uint[] program = [
            0x06400093, // addi x1, x0, 100    — store's address, always fixed
            0x00000113, // addi x2, x0, 0      — accumulator
            0x06400293, // addi x5, x0, 100    — load's address register, starts matching the store
            0x00800193, // addi x3, x0, 8      — iteration counter, counts down from 8
            0x00200493, // addi x9, x0, 2      — trigger: redirect x5 one iteration before the last
            0x0020A023, // loop: sw x2, 0(x1)
            0x0002A203, // lw x4, 0(x5)
            0x00120113, // addi x2, x4, 1
            0x00918463, // beq x3, x9, do_redirect
            0x0080006F, // jal x0, cont
            0x06800293, // do_redirect: addi x5, x0, 104
            0xFFF18193, // cont: addi x3, x3, -1
            0xFE0192E3, // bne x3, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "smb_mispredicts"));
        Assert.True(Counter(onResult, "smb_bypasses") > 0, "no SMB bypass was ever attempted");
        Assert.True(Counter(onResult, "smb_mispredicts") > 0, "the mid-loop distance shift never mispredicted");
    }

    /// <summary>
    ///     Store word / load byte from the same address every iteration (assembled from:
    ///     <c>
    ///         addi x1,x0,100; addi x2,x0,0; addi x3,x0,20; loop: sw x2,0(x1); lb x4,0(x1);
    ///         addi x2,x4,1; addi x3,x3,-1; bne x3,x0,loop; ebreak
    ///     </c>
    ///     ). The store fully
    ///     contains the load's byte range, so ordinary store-to-load forwarding succeeds and
    ///     trains the predictor with a real distance — but the v1 full-word/zero-offset
    ///     restriction (predicted producer's static width must equal the load's) must still
    ///     block every bypass attempt, since a byte load never has the same static width as
    ///     a word store.
    /// </summary>
    [Fact]
    public void WidthMismatch_NeverBypasses_ArchStateIdenticalToWithout() {
        uint[] program = [
            0x06400093, // addi x1, x0, 100
            0x00000113, // addi x2, x0, 0
            0x01400193, // addi x3, x0, 20
            0x0020A023, // loop: sw x2, 0(x1)
            0x00008203, // lb x4, 0(x1)
            0x00120113, // addi x2, x4, 1
            0xFFF18193, // addi x3, x3, -1
            0xFE0198E3, // bne x3, x0, loop
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "smb_bypasses"));
        Assert.Equal(0L, Counter(onResult, "smb_bypasses"));
    }
}