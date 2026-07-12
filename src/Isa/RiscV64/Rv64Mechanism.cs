using Mechanism;
using Orrery.Cache;
using Orrery.Devices;
using RiscV32.Execute;
using RiscV32.Trap;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.Memory;
using RiscV64.State;

namespace RiscV64;

/// <summary>
/// The RV64I ISA plugin. Extends RV32I via inheritance — all RV32IMAFCV
/// instructions are available; RV64I adds W-suffix ops, LD/LWU/SD, and
/// corrects shift/comparison/LW semantics for 64-bit.
/// </summary>
public sealed class Rv64Mechanism : IMechanism {
    /// <param name="htifTohost">
    /// Address of the HTIF <c>tohost</c> register, if the workload exits via HTIF.
    /// When supplied, a tohost exit-code store terminates the run at the write
    /// itself (see <see cref="Rv32Executor.HtifTohostAddress"/>). Null for the
    /// common EBREAK-terminated case.
    /// </param>
    /// <param name="reservationTable">
    /// Shared LR/SC reservation tracker for multi-hart simulation.
    /// When non-null, LR.D/LR.W and SC.D/SC.W route through this table instead of
    /// the real single-hart private reservation. Pass the same instance to all
    /// harts that share a memory bus.
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
    /// <param name="ebreakAlwaysHalts">See <see cref="Rv32Executor.EbreakAlwaysHalts"/>.</param>
    /// <param name="wfiNeverHalts">See <see cref="Rv32Executor.WfiNeverHalts"/>.</param>
    public Rv64Mechanism(
        ulong? htifTohost = null,
        ReservationTable? reservationTable = null,
        int hartId = 0,
        ClintDevice? clint = null,
        PlicDevice? plic = null,
        bool ebreakAlwaysHalts = false,
        bool wfiNeverHalts = false
    ) {
        Executor = new Rv64Executor {
            HtifTohostAddress = htifTohost,
            ReservationTable = reservationTable,
            HartId = hartId,
            Clint = clint,
            EbreakAlwaysHalts = ebreakAlwaysHalts,
            WfiNeverHalts = wfiNeverHalts,
        };
        TrapController = new RvTrapController(clint, plic);
    }

    public string Name => "RV64I";
    public IDecoder Decoder { get; } = new Rv64Decoder();
    public IExecutor Executor { get; }
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; }

    public IArchState CreateArchState() => new Rv64ArchState();

    public IFetchTranslator CreateFetchTranslator(IArchState state, IMemory memory) =>
        new Rv64FetchTranslator(state, memory);
}