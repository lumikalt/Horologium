#region

using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level tests for the full DelayExecute+STT prediction-based implicit-channel slice
///     (Yu et al., MICRO 2019, §6.4.2 "Implicit branch with prediction") on <see cref="OooTrain" />'s
///     <c>enableSttMemDepGating</c> constructor parameter. The paper: "we eliminate its
///     predictor-based channel by requiring that the relevant predictor (e.g., a store set
///     predictor) be updated only by untainted data, i.e., only after the implicit branch predicate
///     becomes untainted" — for memory-dependence speculation that predicate is a function of the
///     PRODUCING STORE's own address (whether it aliases the load), not the load's own address or
///     loaded value. <see cref="SmbPredictor" />'s cold-start/ongoing-seeding <c>Train</c> call (an
///     ordinary forwarded load teaching the predictor a fresh SSN distance) is the one call site not
///     already commit-time-safe — the other three <c>Train</c>/<c>TrainNoBypass</c> calls all fire
///     inside <c>StepCommit</c>'s retire loop, strictly later than any visibility point, same as
///     <c>_predictor.Update</c>. <see cref="StoreSetPredictor" />'s only persistent-state writer,
///     <c>RecordViolation</c>, is likewise already commit-time-safe — there is no
///     <c>StoreSetPredictor.Train</c>; the <c>OnStoreDispatch</c>/<c>OnLoadDispatch</c>/
///     <c>OnStoreIssued</c> calls are ephemeral per-SSID LFST scheduling state, not learned
///     persistence, and belong to the separate store-to-load-forwarding-resolution TODO item, not
///     this one.
/// </summary>
public class SttMemDepGatingTests {
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint Sw(int rs2, int rs1, int imm) {
        var u = (uint)imm;
        uint imm11_5 = (u >> 5) & 0x7F;
        uint imm4_0 = u & 0x1F;
        return (imm11_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b010u << 12) | (imm4_0 << 7) | 0b0100011u;
    }

    private static uint Mul(int rd, int rs1, int rs2) =>
        (uint)((0b0000001 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0110011);

    private static uint Beq(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b000u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private const uint Ebreak = 0x00100073;

    private static void AssertIdenticalArchState(OooTrain off, OooTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static OooTrain Make(FlatMemory mem, bool enableSttMemDepGating) =>
        new(
            new Rv32Mechanism(), mem,
            enableSmbBypass: true,
            enableSttMemDepGating: enableSttMemDepGating
        );

    private static void LoadWords(FlatMemory mem, ulong address, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(address, bytes);
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    /// <summary>
    ///     Program: an older branch RootB depends on a 3× chained-mul (9-cycle) dependency, so it
    ///     resolves long after dispatch even though always correctly predicted (timing-only, same
    ///     trick as <c>SttImplicitBranchTests</c>). Before the loop, L0 (<c>lw x1,0(x9)</c>) loads the
    ///     store/reload address — tainted, rooted at L0's own InstrId — and is resolved once, well
    ///     before the loop body, so per-iteration timing inside the loop is identical to
    ///     <c>SmbBypassTests</c>' own proven-working fixed-distance loop (confirmed empirically: an
    ///     earlier design that re-loaded the tainted address fresh <em>every</em> iteration hit the
    ///     exact "extra latency on the address chain" gotcha already documented in
    ///     <c>project_nosq</c> memory — the producing store's own address wasn't known yet by the
    ///     time that iteration's reload completed, so <c>FindForwardingProducerSeqNo</c> matched a
    ///     stale earlier-iteration store instead, training on an inconsistent distance that never
    ///     accumulated confidence). The loop body itself (<c>sw x2,0(x1); lw x4,0(x1)</c>) is
    ///     otherwise identical to the working pattern. Three iterations: with the gate off, the first
    ///     iteration's cold-start <c>Train</c> call applies immediately; with it on, that first call is
    ///     deferred until RootB resolves — but is still applied before the second iteration's own
    ///     training call, so confidence still reaches the bypass threshold by the third iteration,
    ///     proving the deferred update was not silently dropped.
    /// </summary>
    private static uint[] TaintedStoreAddressLoop() => [
        Addi(5, 0, 1), Mul(6, 5, 5), Mul(6, 6, 5), Mul(6, 6, 5), // RootB dependency (9-cycle chain)
        Beq(6, 0, 4), // RootB: timing-only, correctly predicted, resolves late
        Addi(9, 0, 100), // pointer for L0
        Lw(1, 9, 0), // L0: x1 = mem[100] = 200 -- tainted once, before the loop
        Addi(2, 0, 42), // store value (constant)
        Addi(3, 0, 3), // loop counter = 3
        Sw(2, 1, 0), // loop: Store -- tainted address (rooted at L0), fixed every iteration
        Lw(4, 1, 0), // Reload -- ordinary forward, cold-start-trains SmbPredictor
        Addi(3, 3, -1),
        Bne(3, 0, -12), // back to loop
        Ebreak,
    ];

    [Fact]
    public void DefersColdStartTrainingUntilProducingStoreTaintClears_MeasurableAndNotDropped() {
        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, TaintedStoreAddressLoop());
        LoadWords(memOn, 0, TaintedStoreAddressLoop());
        LoadWords(memOff, 100, 200);
        LoadWords(memOn, 100, 200);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(0L, Counter(offResult, "stt_memdep_training_deferrals"));
        Assert.True(
            Counter(onResult, "stt_memdep_training_deferrals") > 0,
            "the cold-start SmbPredictor training call was never actually deferred by the STT gate"
        );
        Assert.Equal(0L, Counter(onResult, "smb_mispredicts"));

        // The deferred training call must still land before the loop's own second training call —
        // otherwise confidence never reaches the bypass threshold within these 3 iterations, proving
        // the update was silently dropped rather than merely delayed.
        Assert.True(
            Counter(onResult, "smb_bypasses") > 0,
            "no bypass occurred -- the deferred cold-start training was dropped, not just delayed"
        );
    }

    /// <summary>
    ///     Same shape but the store's address comes from an independent <c>addi</c>, not a load — no
    ///     taint root reaches it, so the gate must never fire and behavior must be identical to the
    ///     feature-off run (mirrors <c>SttExpOnlyTests</c>' own no-op baseline check).
    /// </summary>
    [Fact]
    public void UntaintedStoreAddress_ProducesZeroDeferrals() {
        uint[] program = [
            Addi(5, 0, 1), Mul(6, 5, 5), Mul(6, 6, 5), Mul(6, 6, 5), // RootB dependency (timing only)
            Beq(6, 0, 4), // RootB
            Addi(1, 0, 200), // store/reload address -- untainted (plain immediate, not a load)
            Addi(2, 0, 42), // store value
            Addi(3, 0, 3), // loop counter = 3
            Sw(2, 1, 0), // loop: Store -- untainted address
            Lw(4, 1, 0), // Reload -- ordinary forward, cold-start-trains SmbPredictor
            Addi(3, 3, -1),
            Bne(3, 0, -12),
            Ebreak,
        ];

        var memOff = new FlatMemory(4096);
        var memOn = new FlatMemory(4096);
        OooTrain off = Make(memOff, false);
        OooTrain on = Make(memOn, true);
        LoadWords(memOff, 0, program);
        LoadWords(memOn, 0, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(0L, Counter(offResult, "stt_memdep_training_deferrals"));
        Assert.Equal(0L, Counter(onResult, "stt_memdep_training_deferrals"));
        Assert.True(Counter(onResult, "smb_bypasses") > 0, "no bypass occurred in the untainted baseline");
        Assert.Equal(Counter(offResult, "smb_bypasses"), Counter(onResult, "smb_bypasses"));
    }
}
