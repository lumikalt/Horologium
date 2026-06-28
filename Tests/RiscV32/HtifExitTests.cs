using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Verifies HTIF tohost-exit termination across all three trains <em>without</em>
/// Spike, so the halt paths stay covered in CI where the co-sim harness can't run.
///
/// htif.elf does real RV32IM work, then exits via the tohost register (writing
/// (0 &lt;&lt; 1) | 1 == 1 for a clean exit-0) and spins in <c>j .</c>:
///
/// - <b>RequestHalt</b> (tohost address configured): the executor flags the exit
///   store with <see cref="Mechanism.ExecuteResult.RequestHalt"/> and the train
///   stops at the store itself — the first-class HTIF terminator.
/// - <b>Jump-to-self</b> (tohost not configured): the train falls back to the
///   unconditional jump-to-self halt on the following spin.
///
/// Both must terminate well before maxTicks and leave the tohost low word == 1.
/// </summary>
public class HtifExitTests {
    private static string HtifElf => Path.Combine(AppContext.BaseDirectory, "htif.elf");
    private const int MemoryBytes = 0x100000;
    private const long MaxTicks = 1_000_000;

    public static IEnumerable<object[]> Trains() => [
        ["single_cycle"], ["five_stage"], ["ooo"],
    ];

    [Theory]
    [MemberData(nameof(Trains))]
    public void RequestHalt_StopsAtTohostExit(string train) => RunAndAssert(train, configureTohost: true);

    [Theory]
    [MemberData(nameof(Trains))]
    public void JumpToSelf_StopsAtSpinLoop(string train) => RunAndAssert(train, configureTohost: false);

    /// <summary>
    /// Proves <c>RequestHalt</c> actually fires rather than being dead code masked
    /// by the jump-to-self backstop. With tohost configured the train halts AT the
    /// exit store, so the following spin <c>j</c> never retires; without it the
    /// train falls through to jump-to-self, retiring that <c>j</c> exactly once.
    /// The two paths therefore retire identically up to the store, differing by the
    /// single spin jump.
    /// </summary>
    [Theory]
    [MemberData(nameof(Trains))]
    public void RequestHalt_RetiresOneFewerThanJumpToSelf(string train) {
        long withTohost = Run(train, configureTohost: true).Retired;
        long without = Run(train, configureTohost: false).Retired;
        Assert.Equal(without - 1, withTohost);
    }

    private static void RunAndAssert(string train, bool configureTohost) {
        (RevolutionResult result, long _, uint tohostLow) = Run(train, configureTohost);

        // Halted (did not exhaust the tick budget) and exited 0 (tohost low word == 1).
        Assert.True(result.TotalTicks < MaxTicks, $"{train} did not halt — ran the full {MaxTicks} ticks");
        Assert.Equal(1u, tohostLow);
    }

    private static (RevolutionResult Result, long Retired, uint TohostLow) Run(string train, bool configureTohost) {
        var workload = new Rv32ElfWorkload(HtifElf, MemoryBytes);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        ulong tohost = workload.FindSymbol("tohost");
        var mech = new Rv32Mechanism(configureTohost ? tohost : null);

        (RevolutionResult result, string ownerPath) = train switch {
            "single_cycle" => (new SingleCycleTrain(mech, mem, workload.EntryPoint).Run(MaxTicks), "single_cycle.core"),
            "five_stage"   => (new FiveStageTrain(mech, mem, workload.EntryPoint).Run(MaxTicks), "five_stage.pipeline"),
            "ooo"          => (new OooeTrain(mech, mem, workload.EntryPoint).Run(MaxTicks), "ooo.pipeline"),
            _              => throw new ArgumentOutOfRangeException(nameof(train)),
        };

        long retired = result.Find(ownerPath)?.Counters.GetValueOrDefault("retired") ?? -1;
        return (result, retired, (uint)mem.Read(tohost, 4));
    }
}
