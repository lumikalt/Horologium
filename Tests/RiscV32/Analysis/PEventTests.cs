#region

using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Analysis;

public class PEventTests {
    // addi x1, x0, 10 / addi x2, x0, 20 / add x3, x1, x2 / ebreak
    private static readonly uint[] SimpleProgram = [0x00A00093u, 0x01400113u, 0x002081B3u, 0x00100073u,];

    // addi x1, x0, 1 / beq x1, x1, +8 (taken) / addi x2, x0, 99 (wrong path) / ebreak
    private static readonly uint[] BranchMispredictProgram = [0x00100093u, 0x00108463u, 0x06300113u, 0x00100073u,];

    private static FlatMemory LoadProgram(uint[] words) {
        var mem = new FlatMemory(4096);
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
        return mem;
    }

    // ── FiveStage ─────────────────────────────────────────────────────────────

    [Fact]
    public void FiveStage_SimpleProgram_RecordsEvents() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        Assert.NotEmpty(plog.Events);
        Assert.NotEmpty(plog.OfKind(PEventKind.Fetch));
        Assert.NotEmpty(plog.OfKind(PEventKind.Execute));
        Assert.NotEmpty(plog.OfKind(PEventKind.Retire));
    }

    [Fact]
    public void FiveStage_NoLog_DoesNotThrow() {
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem);
        train.Run(); // no exception when plog is null
    }

    [Fact]
    public void FiveStage_RetiredInstructions_HaveFetchBeforeRetire() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        // Every instruction that has a RETIRE must also have a FETCH at an earlier or equal cycle.
        foreach (PEvent retire in plog.OfKind(PEventKind.Retire)) {
            PEvent? fetch = plog.ForInstruction(retire.InstrId).FirstOrDefault(e => e.Kind == PEventKind.Fetch);
            Assert.True(fetch.HasValue, $"InstrId={retire.InstrId} has RETIRE but no FETCH");
            Assert.True(
                fetch.Value.Cycle <= retire.Cycle,
                $"InstrId={retire.InstrId}: FETCH cycle {fetch.Value.Cycle} > RETIRE cycle {retire.Cycle}"
            );
        }
    }

    [Fact]
    public void FiveStage_RetiredInstructions_HaveExecuteBeforeRetire() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        foreach (PEvent retire in plog.OfKind(PEventKind.Retire)) {
            PEvent? execute = plog.ForInstruction(retire.InstrId).FirstOrDefault(e => e.Kind == PEventKind.Execute);
            Assert.True(execute.HasValue, $"InstrId={retire.InstrId} has RETIRE but no EXECUTE");
            Assert.True(
                execute.Value.Cycle <= retire.Cycle,
                $"InstrId={retire.InstrId}: EXECUTE cycle {execute.Value.Cycle} > RETIRE cycle {retire.Cycle}"
            );
        }
    }

    [Fact]
    public void FiveStage_BranchMispredict_ProducesFlushEvents() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.BranchMispredictProgram);
        // AlwaysNotTakenPredictor (default) will mispredict the beq x1,x1 branch (always taken).
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        // There must be at least one FLUSH event.
        Assert.NotEmpty(plog.OfKind(PEventKind.Flush));
    }

    [Fact]
    public void FiveStage_WrongPathInstruction_FlushesButDoesNotRetire() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.BranchMispredictProgram);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        // The wrong-path instruction at PC=8 (addi x2, x0, 99) should appear in FLUSH
        // events but not RETIRE events.
        List<PEvent> wrongPathFetch = plog.OfKind(PEventKind.Fetch).Where(e => e.Pc == 8).ToList();
        Assert.NotEmpty(wrongPathFetch);

        // At least one fetch of PC=8 must have a corresponding FLUSH with no RETIRE.
        bool anyFlushedNoRetire = wrongPathFetch.Any(fetch => {
                IEnumerable<PEvent> forInstr = plog.ForInstruction(fetch.InstrId);
                IEnumerable<PEvent> pEvents = forInstr as PEvent[] ?? forInstr.ToArray();
                return pEvents.Any(e => e.Kind == PEventKind.Flush) && pEvents.All(e => e.Kind != PEventKind.Retire);
            }
        );
        Assert.True(anyFlushedNoRetire, "Wrong-path instruction at PC=8 should be flushed without retiring");
    }

    [Fact]
    public void FiveStage_InstrIdIsUnique() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new FiveStageTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        // No two distinct FETCH events should share an InstrId.
        List<ulong> fetchIds = plog.OfKind(PEventKind.Fetch).Select(e => e.InstrId).ToList();
        Assert.Equal(fetchIds.Count, fetchIds.Distinct().Count());
    }

    // ── OoO ───────────────────────────────────────────────────────────────────

    [Fact]
    public void OoO_SimpleProgram_RecordsAllLifecycleKinds() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new OooTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        Assert.NotEmpty(plog.OfKind(PEventKind.Fetch));
        Assert.NotEmpty(plog.OfKind(PEventKind.Dispatch));
        Assert.NotEmpty(plog.OfKind(PEventKind.Issue));
        Assert.NotEmpty(plog.OfKind(PEventKind.Execute));
        Assert.NotEmpty(plog.OfKind(PEventKind.Retire));
    }

    [Fact]
    public void OoO_RetiredInstructions_HaveFullLifecycle() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new OooTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        foreach ((ulong id, _, long retireCycle, _) in plog.OfKind(PEventKind.Retire)) {
            IEnumerable<PEvent> lifecycle = plog.ForInstruction(id).ToList();

            long fetchCycle = lifecycle.First(e => e.Kind == PEventKind.Fetch).Cycle;
            long dispatchCycle = lifecycle.First(e => e.Kind == PEventKind.Dispatch).Cycle;
            long issueCycle = lifecycle.First(e => e.Kind == PEventKind.Issue).Cycle;
            long executeCycle = lifecycle.First(e => e.Kind == PEventKind.Execute).Cycle;

            Assert.True(
                fetchCycle <= dispatchCycle,
                $"InstrId={id}: FETCH {fetchCycle} > DISPATCH {dispatchCycle}"
            );
            Assert.True(
                dispatchCycle <= issueCycle,
                $"InstrId={id}: DISPATCH {dispatchCycle} > ISSUE {issueCycle}"
            );
            Assert.True(
                issueCycle <= executeCycle,
                $"InstrId={id}: ISSUE {issueCycle} > EXECUTE {executeCycle}"
            );
            Assert.True(
                executeCycle <= retireCycle,
                $"InstrId={id}: EXECUTE {executeCycle} > RETIRE {retireCycle}"
            );
        }
    }

    [Fact]
    public void OoO_BranchMispredict_ProducesFlushEvents() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.BranchMispredictProgram);
        var train = new OooTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        Assert.NotEmpty(plog.OfKind(PEventKind.Flush));
    }

    [Fact]
    public void OoO_InstrIdIsUnique() {
        var plog = new PEventLog();
        FlatMemory mem = LoadProgram(PEventTests.SimpleProgram);
        var train = new OooTrain(new Rv32Mechanism(), mem, pEventLog: plog);
        train.Run();

        List<ulong> fetchIds = plog.OfKind(PEventKind.Fetch).Select(e => e.InstrId).ToList();
        Assert.Equal(fetchIds.Count, fetchIds.Distinct().Count());
    }
}