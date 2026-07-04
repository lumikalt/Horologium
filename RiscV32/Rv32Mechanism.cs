using Mechanism;
using Orrery.Cache;
using Orrery.Devices;
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
    /// <param name="reservationTable">
    /// Shared LR/SC reservation tracker for multi-hart simulation.
    /// When non-null, LR.W and SC.W route through this table instead of the
    /// real single-hart private reservation. Pass the same instance to all harts
    /// that share a memory bus.
    /// </param>
    /// <param name="hartId">
    /// The hart identifier used as the key in <paramref name="reservationTable"/>.
    /// Ignored when <paramref name="reservationTable"/> is null.
    /// </param>
    /// <param name="clint">
    /// CLINT device to attach to this hart's trap controller. When provided,
    /// mtime advances and MTIP/MSIP are refreshed on every interrupt poll.
    /// </param>
    /// <param name="plic">
    /// PLIC device to attach to this hart's trap controller. When provided,
    /// MEIP/SEIP in mip reflect external interrupt state from the PLIC each poll.
    /// </param>
    public Rv32Mechanism(
        ulong? htifTohost = null,
        ReservationTable? reservationTable = null,
        int hartId = 0,
        ClintDevice? clint = null,
        PlicDevice? plic = null
    ) {
        Executor = new Rv32Executor {
            HtifTohostAddress = htifTohost,
            ReservationTable = reservationTable,
            HartId = hartId,
            Clint = clint,
        };
        TrapController = new RvTrapController(clint, plic);
    }

    public string Name => "RV32I";
    public IDecoder Decoder { get; } = new Rv32Decoder();
    public IExecutor Executor { get; }
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; }

    public IArchState CreateArchState() => new Rv32ArchState();

    public IFetchTranslator CreateFetchTranslator(IArchState state, IMemory memory) =>
        new RvFetchTranslator(state, memory);
}