#region

using Mechanism;
using Mechanism.ValuePred;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level tests for the STT/InvisiSpec Futuristic-model visibility point (Yan et al.,
///     MICRO 2018, §V-A1, Table I; Yu et al., MICRO 2019), selectable via <c>sttFuturisticModel</c>
///     as an alternative to the Spectre model shared by every other STT-ExpOnly/InvisiSpec test in
///     this directory. The Spectre model (<c>Pipeline.Ooo.SpectreVisibilityTracker</c>) only tracks
///     branches; the Futuristic model (<c>Pipeline.Ooo.FuturisticVisibilityTracker</c>) additionally
///     tracks stores (address-alias risk), traps, SMB-bypass verification, and value-prediction
///     verification as squash sources — an instruction stays unsafe while ANY older in-flight
///     instruction of any of these types is still unresolved, not just an unresolved branch.
/// </summary>
public class SttFuturisticModelTests {
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

    private static OooTrain Make(
        FlatMemory mem,
        bool enableSttExpOnly,
        bool sttFuturisticModel,
        bool enableSmbBypass = false,
        IValuePredictor? valuePredictor = null
    ) =>
        new(
            new Rv32Mechanism(), mem,
            robCapacity: 64,
            iqCapacity: 64,
            enableSmbBypass: enableSmbBypass,
            valuePredictor: valuePredictor,
            enableSttExpOnly: enableSttExpOnly,
            sttFuturisticModel: sttFuturisticModel
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

    private static void AssertIdenticalArchState(OooTrain off, OooTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    /// <summary>
    ///     No branch anywhere in this program — the Spectre-model tracker never gets a single entry
    ///     registered, so its <c>IsSafe</c> is unconditionally true throughout. A slow store (address
    ///     = x7's mul chain plus a fixed offset, ~18 cycles until known) sits ahead of an untainted
    ///     anchor load (<c>lw x2,0(x1)</c>, own InstrId roots SourceYrot) and a target load
    ///     (<c>lw x4,0(x2)</c>, SourceYrot = anchor's InstrId) gated by STT-ExpOnly. The anchor resolves
    ///     in a handful of cycles (no dependency on x7 at all), well before the slow store's address
    ///     does — so under the Futuristic model, the target's gate stays blocked on the store's
    ///     still-pending <c>StoreAddr</c> bit (Table I's "address alias between a load and an earlier
    ///     store") for several cycles after the anchor itself is otherwise ready. All three addresses
    ///     (2000 for the slow store, 300/2100 for the anchor/target) are scratch, well clear of the
    ///     ~40-byte program itself.
    /// </summary>
    private static uint[] SlowStoreAheadOfUntaintedAnchorAndTarget() {
        List<uint> program = [
            Addi(7, 0, 1), // seed for the slow store's address chain -- kept off the address
            // register itself so the store's target stays a plain, aligned scratch address
        ];
        for (var i = 0; i < 6; i++) program.Add(Mul(7, 7, 7)); // ~18-cycle chain; x7 stays 1
        program.Add(Addi(6, 7, 2000)); // x6 = 2001 -- depends on x7, so still gated by the chain
        program.Add(Sw(0, 6, 0)); // SLOW STORE: address = x6, not known until the chain resolves
        program.Add(Addi(1, 0, 300)); // x1 = 300: anchor load address, independent of x7/x6
        program.Add(Lw(2, 1, 0)); // anchor load: x2 = mem[300] = 2100 -- untainted address, roots
        // SourceYrot at its own InstrId; resolves in a handful of cycles
        program.Add(Lw(4, 2, 0)); // TARGET load: address = x2 = 2100; SourceYrot = anchor's InstrId
        program.Add(Ebreak);
        return program.ToArray();
    }

    /// <summary>
    ///     Confirmed to fail (both counters equal, both zero) if <c>FuturisticVisibilityTracker</c>'s
    ///     dispatch-time store registration is temporarily disabled — the whole point of this test is
    ///     that the Futuristic model catches a squash source (a slow store) the Spectre model
    ///     structurally cannot see at all in a branch-free program.
    /// </summary>
    [Fact]
    public void FuturisticModel_BlocksOnSlowStore_SpectreModelDoesNot() {
        var memOff = new FlatMemory(4096);
        var memSpectre = new FlatMemory(4096);
        var memFuturistic = new FlatMemory(4096);
        OooTrain off = Make(memOff, false, false);
        OooTrain spectre = Make(memSpectre, true, false);
        OooTrain futuristic = Make(memFuturistic, true, true);
        LoadWords(memOff, 0, SlowStoreAheadOfUntaintedAnchorAndTarget());
        LoadWords(memSpectre, 0, SlowStoreAheadOfUntaintedAnchorAndTarget());
        LoadWords(memFuturistic, 0, SlowStoreAheadOfUntaintedAnchorAndTarget());
        LoadWords(memOff, 300, 2100);
        LoadWords(memSpectre, 300, 2100);
        LoadWords(memFuturistic, 300, 2100);

        RevolutionResult offResult = off.Run();
        RevolutionResult spectreResult = spectre.Run();
        RevolutionResult futuristicResult = futuristic.Run();

        AssertIdenticalArchState(off, spectre);
        AssertIdenticalArchState(off, futuristic);

        Assert.Equal(0L, Counter(offResult, "stt_load_issue_stalls"));
        Assert.Equal(0L, Counter(spectreResult, "stt_load_issue_stalls"));
        Assert.True(
            Counter(futuristicResult, "stt_load_issue_stalls") > 0,
            "Futuristic model never gated the target load on the still-unresolved slow store"
        );
    }

    /// <summary>
    ///     RootB (a 6x chained-mul dependency, ~18 cycles) and the anchor load (<c>lw x2,0(x1)</c>,
    ///     untainted address) both sit once, before the loop, exactly matching
    ///     <c>SttStoreForwardTests.TaintedReloadWithBypassEligibleLoop</c>'s own proven shape (RootB
    ///     kept short so it retires before the loop's own per-iteration mispredict-flush would
    ///     otherwise discard it — see that test's doc comment). The loop's target reload
    ///     (<c>lw x4,0(x2)</c>) stores and reloads the SAME constant value (42) at the SAME address
    ///     every iteration: SAME PC, fixed SSN distance (trains <c>SmbPredictor</c>, same as
    ///     <c>SttStoreForwardTests</c>) AND a constant value with zero stride (trains
    ///     <c>StrideVp</c>, which reaches its confident "Steady" state after just two matching
    ///     observations) — making this one load simultaneously SMB-bypass-eligible and
    ///     value-prediction-eligible, i.e. carrying two independent Futuristic squash-source bits
    ///     (<c>Smb</c> and <c>Vp</c>) at once.
    /// </summary>
    private static uint[] DualSourceLoadLoop() {
        List<uint> program = [
            Addi(7, 0, 1), // seed for RootB's mul chain
        ];
        for (var i = 0; i < 6; i++) program.Add(Mul(6, 7, 7)); // RootB: ~6*3 = 18-cycle window
        program.Add(Bne(6, 0, 4)); // RootB: always taken, targets its own fall-through -- timing-only
        program.Add(Addi(1, 0, 300)); // x1 = 300: fixed anchor-load address
        program.Add(Lw(2, 1, 0)); // anchor load: x2 = mem[300] = 500 -- untainted address (x1),
        // taints x2 with the anchor's own InstrId, unsafe while RootB above is unresolved
        program.Add(Addi(5, 0, 42)); // x5 = 42: fixed store value -- constant, so the reload below
        // trains StrideVp to a zero stride (confident after 2 matching observations)
        program.Add(Addi(3, 0, 6)); // loop counter
        int loopStart = program.Count * 4;
        program.Add(Sw(5, 2, 0)); // producing store: mem[500] = 42, SAME PC every iteration
        program.Add(Lw(4, 2, 0)); // DUAL-SOURCE load: SourceYrot = anchor's InstrId; SAME PC/address
        // every iteration -- bypass-eligible (SMB) AND value-prediction-eligible (StrideVp)
        program.Add(Addi(3, 3, -1));
        int loopCtrlPc = program.Count * 4;
        program.Add(Bne(3, 0, loopStart - loopCtrlPc)); // back to the store at loop start
        program.Add(Ebreak);
        return program.ToArray();
    }

    /// <summary>
    ///     Proves the bitmask design handles a single load carrying two independent pending squash
    ///     sources (SMB-bypass verification and value-prediction verification) without either bit
    ///     masking the other: both mechanisms actually fire in this run (coexistence, same honest
    ///     framing as <c>SttStoreForwardTests</c>/<c>SttValuePredictionTests</c> — not a dynamic proof
    ///     that the exact same instance is gated, bypassed, AND predicted, only that none of the three
    ///     properties is perturbed by the others), final architectural state matches the undefended
    ///     baseline, and the Futuristic-model run still exercises ExpOnly's gate at least as much as
    ///     the Spectre-model run (RootB's branch alone already gates early iterations under either
    ///     model; Futuristic has strictly more to track, never less).
    /// </summary>
    [Fact]
    public void DualSourceLoad_SmbAndVpBothPending_CoexistWithoutDeadlockOrDroppedGating() {
        var memOff = new FlatMemory(4096);
        var memSpectre = new FlatMemory(4096);
        var memFuturistic = new FlatMemory(4096);
        OooTrain off = Make(memOff, false, false);
        OooTrain spectre = Make(memSpectre, true, false, enableSmbBypass: true, valuePredictor: new StrideVp());
        OooTrain futuristic = Make(
            memFuturistic, true, true, enableSmbBypass: true, valuePredictor: new StrideVp()
        );
        LoadWords(memOff, 0, DualSourceLoadLoop());
        LoadWords(memSpectre, 0, DualSourceLoadLoop());
        LoadWords(memFuturistic, 0, DualSourceLoadLoop());
        LoadWords(memOff, 300, 500);
        LoadWords(memSpectre, 300, 500);
        LoadWords(memFuturistic, 300, 500);

        RevolutionResult offResult = off.Run();
        RevolutionResult spectreResult = spectre.Run();
        RevolutionResult futuristicResult = futuristic.Run();

        AssertIdenticalArchState(off, spectre);
        AssertIdenticalArchState(off, futuristic);

        Assert.Equal(0L, Counter(offResult, "stt_load_issue_stalls"));
        Assert.True(
            Counter(futuristicResult, "stt_load_issue_stalls") > 0,
            "Futuristic model's gate was never exercised in this run"
        );
        Assert.True(
            Counter(futuristicResult, "stt_load_issue_stalls") >= Counter(spectreResult, "stt_load_issue_stalls"),
            "Futuristic model gated strictly less than Spectre model despite tracking strictly more squash sources"
        );

        Assert.True(Counter(futuristicResult, "smb_bypasses") > 0, "no SMB bypass occurred under the Futuristic model");
        Assert.True(Counter(futuristicResult, "vp_predictions") > 0, "no value prediction occurred under the Futuristic model");
        Assert.Equal(0L, Counter(futuristicResult, "vp_mispredicts"));
    }
}
