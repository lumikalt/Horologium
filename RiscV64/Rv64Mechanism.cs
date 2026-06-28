using Mechanism;
using RiscV32.Memory;
using RiscV32.Trap;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

namespace RiscV64;

/// <summary>
/// The RV64I ISA plugin. Extends RV32I via inheritance — all RV32IMAFCV
/// instructions are available; RV64I adds W-suffix ops, LD/LWU/SD, and
/// corrects shift/comparison/LW semantics for 64-bit.
/// </summary>
public sealed class Rv64Mechanism : IMechanism {
    public string Name => "RV64I";
    public IDecoder Decoder { get; } = new Rv64Decoder();
    public IExecutor Executor { get; } = new Rv64Executor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new RvTrapController();

    public IArchState CreateArchState() => new Rv64ArchState();

    public IFetchTranslator? CreateFetchTranslator(IArchState state, IMemory memory) =>
        new RvFetchTranslator(state, memory);
}