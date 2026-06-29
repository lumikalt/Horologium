using Mechanism;
using RiscV32.Config;
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
    /// <param name="extensions">
    /// The set of ISA extensions this hart implements. Used to generate the
    /// correct ISA string for Spike co-simulation and other tooling.
    /// Defaults to <see cref="RvExtension.All"/> (every implemented extension).
    /// </param>
    public Rv32Mechanism(ulong? htifTohost = null, RvExtension extensions = RvExtension.All) {
        Extensions = extensions;
        Executor = new Rv32Executor { HtifTohostAddress = htifTohost, };
    }

    public RvExtension Extensions { get; }
    public string Name => "RV32I";
    public IDecoder Decoder { get; } = new Rv32Decoder();
    public IExecutor Executor { get; }
    public IImpulseCracker? UopCracker => null; // added in Phase 7
    public ITrapController TrapController { get; } = new RvTrapController();

    public IArchState CreateArchState() => new Rv32ArchState();

    public IFetchTranslator? CreateFetchTranslator(IArchState state, IMemory memory) =>
        new RvFetchTranslator(state, memory);
}