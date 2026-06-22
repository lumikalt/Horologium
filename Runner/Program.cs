using Mechanism;
using RiscV;
using RiscV.Analysis;
using RiscV.Config;

// ── Workload: a 100-iteration countdown loop with a tight branch ─────────────
//
//   addi  x1, x0, 100    # x1 = 100
// loop:
//   beq   x1, x0, done   # if x1 == 0 → exit
//   addi  x1, x1, -1     # x1--
//   jal   x0, -8         # back to beq
// done:
//   ebreak
//
// The branch is taken 100 times (back-edge) and not-taken once (exit).
// A decent predictor learns "almost always taken" quickly.
//
// All instructions are encoded for RV32I at address 0x0.
uint[] words = [
    0x06400093,  // addi  x1, x0, 100
    0x00008663,  // beq   x1, x0, +12
    0xFFF08093,  // addi  x1, x1, -1
    0xFF9FF06F,  // jal   x0, -8
    0x00100073,  // ebreak
];
var bytes = new byte[words.Length * 4];
for (var i = 0; i < words.Length; i++) {
    bytes[i * 4 + 0] = (byte)words[i];
    bytes[i * 4 + 1] = (byte)(words[i] >> 8);
    bytes[i * 4 + 2] = (byte)(words[i] >> 16);
    bytes[i * 4 + 3] = (byte)(words[i] >> 24);
}
IWorkload workload = new ByteArrayWorkload(bytes);

// ── Hardware configurations to compare ───────────────────────────────────────
(string, TrainConfig)[] configs = [
    ("always_not_taken", new TrainConfig(Predictor: BranchPredictorConfig.AlwaysNotTaken())),
    ("always_taken",     new TrainConfig(Predictor: BranchPredictorConfig.AlwaysTaken())),
    ("one_bit",          new TrainConfig(Predictor: BranchPredictorConfig.OneBit())),
    ("two_bit",          new TrainConfig(Predictor: BranchPredictorConfig.TwoBit())),
    ("no_forwarding",    new TrainConfig(
        ForwardingEnabled: false,
        Predictor: BranchPredictorConfig.TwoBit()
    )),
];

// ── Run ───────────────────────────────────────────────────────────────────────
ExperimentResult result = Experiment.Run(workload, configs, new RvMechanism());

// ── Print results ─────────────────────────────────────────────────────────────
Console.WriteLine("=== Branch predictor comparison — 100-iteration countdown loop ===");
Console.WriteLine();
Console.WriteLine(result.ToMarkdownTable());
Console.WriteLine();
Console.WriteLine("=== CSV ===");
Console.WriteLine(result.ToCsv());
