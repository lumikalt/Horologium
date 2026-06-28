using Mechanism;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;
using RiscV32.Trap;

namespace RiscV32;

/// <summary>
/// The RV32I ISA plugin. Implements IMechanism — the top-level factory
/// for all RISC-V ISA components.
/// Swap this for RV64GC or a custom ISA without touching any Train code.
/// </summary>
public sealed class Rv32Mechanism : IMechanism {
    /// <param name="htifTohost">
    /// Address of the HTIF <c>tohost</c> register, if the workload exits via HTIF.
    /// When supplied, a tohost exit-code store terminates the run at the write
    /// itself (see <see cref="Rv32Executor.HtifTohostAddress"/>). Null for the
    /// common EBREAK-terminated case.
    /// </param>
    public Rv32Mechanism(ulong? htifTohost = null) =>
        Executor = new Rv32Executor { HtifTohostAddress = htifTohost };

    public string Name => "RV32I";
    public IDecoder Decoder { get; } = new Rv32Decoder();
    public IExecutor Executor { get; }
    public IImpulseCracker? UopCracker => null; // added in Phase 7
    public ITrapController TrapController { get; } = new RvTrapController();

    public IArchState CreateArchState() => new Rv32ArchState();

    public IFetchTranslator? CreateFetchTranslator(IArchState state, IMemory memory) =>
        new RvFetchTranslator(state, memory);
}