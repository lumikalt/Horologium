using System.Globalization;
using System.Text.Json;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Analysis;
using RiscV32.Memory;

namespace Tests.RiscV32.Analysis;

/// <summary>
///     Self-validation for the Olympia JSON instruction-trace output (no Olympia
///     build required): confirms the trace is well-formed and matches Olympia's
///     schema (raw <c>opcode</c> + optional <c>mnemonic</c> + <c>vaddr</c> for
///     loads/stores), and that it is an exact functional instruction stream (one
///     entry per retired instruction).
/// </summary>
public class OlympiaTraceTests {
    // riscv mnemonics that carry an effective address in the Olympia schema.
    private static readonly HashSet<string> LoadStore = new(StringComparer.Ordinal) {
        "lb", "lh", "lw", "lbu", "lhu", "lwu", "ld", "sb", "sh", "sw", "sd", "flw", "fsw",
    };

    private static string ElfPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private static (string Json, int Count) Trace(string elf) {
        var workload = new Rv32ElfWorkload(ElfPath(elf));
        var sw = new StringWriter();
        int count = Experiment.WriteOlympiaTrace(workload, new Rv32Mechanism(workload.HtifTohostAddress), sw);
        return (sw.ToString(), count);
    }

    [Theory]
    [InlineData("test.elf")]
    [InlineData("isa/rv32um-p-mul.elf")]
    public void Trace_MatchesOlympiaSchema(string elf) {
        (string json, _) = Trace(elf);
        using JsonDocument doc = JsonDocument.Parse(json); // must be a valid JSON array
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0);

        foreach (JsonElement e in root.EnumerateArray()) {
            // opcode is the required field Mavis decodes — "0x" + hex.
            string op = e.GetProperty("opcode").GetString()!;
            Assert.StartsWith("0x", op);
            Assert.True(
                uint.TryParse(op.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _),
                $"opcode '{op}' should be hex"
            );

            // mnemonic is optional/best-effort; lowercase when present.
            if (e.TryGetProperty("mnemonic", out JsonElement mn)) {
                string m = mn.GetString()!;
                Assert.Equal(m.ToLowerInvariant(), m);

                bool hasVaddr = e.TryGetProperty("vaddr", out JsonElement va);
                if (OlympiaTraceTests.LoadStore.Contains(m)) {
                    Assert.True(hasVaddr, $"load/store '{m}' should carry a vaddr");
                    Assert.StartsWith("0x", va.GetString());
                }
                else { Assert.False(hasVaddr, $"non-memory op '{m}' should not carry a vaddr"); }
            }
        }
    }

    [Fact]
    public void Trace_HasExpectedInstructions() {
        (string json, _) = Trace("test.elf");
        using JsonDocument doc = JsonDocument.Parse(json);
        HashSet<string?> mnemonics = doc.RootElement.EnumerateArray()
                                        .Select(e => e.GetProperty("mnemonic").GetString())
                                        .ToHashSet();

        // test.elf is fib + array sum: must contain stores, loads, a jump, and arithmetic.
        Assert.Contains("sw", mnemonics);
        Assert.Contains("lw", mnemonics);
        Assert.Contains("jal", mnemonics);
        Assert.Contains("addi", mnemonics);
    }

    [Fact]
    public void Trace_EntryCount_MatchesRetiredDial() {
        (string json, int count) = Trace("test.elf");

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(count, doc.RootElement.GetArrayLength());

        // Independent functional run: the retired dial must equal the trace length
        // (one entry per committed instruction; EBREAK does not commit).
        var workload = new Rv32ElfWorkload(ElfPath("test.elf"));
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);
        RevolutionResult r =
            new SingleCycleTrain(new Rv32Mechanism(workload.HtifTohostAddress), mem, workload.EntryPoint).Run();

        long retired = r.Find("single_cycle.core")!.Counters["retired"];
        Assert.Equal(retired, count);
    }
}