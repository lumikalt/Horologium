using Mechanism;
using RiscV.Decode;
using RiscV.Execute;
using RiscV.State;
using RiscV.Trap;

namespace RiscV;

/// <summary>
/// The RV32I ISA plugin. Implements IMechanism — the top-level factory
/// for all RISC-V ISA components.
/// Swap this for RV64GC or a custom ISA without touching any Train code.
/// </summary>
public sealed class RvMechanism : IMechanism {
    public string Name => "RV32I";
    public IDecoder Decoder { get; } = new RvDecoder();
    public IExecutor Executor { get; } = new RvExecutor();
    public IUopCracker? UopCracker => null; // added in Phase 7
    public ITrapController TrapController { get; } = new RvTrapController();

    public IArchState CreateArchState() => new RvArchState();
}