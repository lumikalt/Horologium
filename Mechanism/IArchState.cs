namespace Mechanism;

/// <summary>
/// The complete architectural state of a running hart (hardware thread).
///
/// This is the live mutable state the executor reads and writes.
/// The ISA plugin owns the concrete implementation — the pipeline
/// only ever sees this interface.
/// </summary>
public interface IArchState {
    /// <summary>The program counter.</summary>
    ulong Pc { get; set; }

    /// <summary>The privilege level the hart is currently executing at.</summary>
    PrivilegeLevel PrivilegeLevel { get; set; }

    /// <summary>The integer register file.</summary>
    IRegisterFile IntegerRegisters { get; }

    /// <summary>
    /// The system register file. Null if the ISA does not define system registers.
    /// </summary>
    ISystemRegisters SystemRegisters { get; }

    /// <summary>
    /// Creates a deep copy of this state.
    /// Used by the pipeline to checkpoint state for precise exceptions.
    /// </summary>
    IArchState Snapshot();

    /// <summary>Resets the state to its power-on default.</summary>
    void Reset();

    /// <summary>
    /// UVE scalar accumulator registers. Null for ISAs that do not implement UVE.
    /// The pipeline uses this to inject stream element values before executor dispatch
    /// and to sync stream-exhaustion state for branch ops.
    /// </summary>
    IUveScalars? UveScalars => null;

    /// <summary>
    /// Called by the pipeline once per elapsed clock cycle (including stall cycles).
    /// ISA implementations that maintain a hardware cycle counter (e.g. Zicntr mcycle)
    /// override this to increment it. Default: no-op.
    /// </summary>
    void OnCycle() {}

    /// <summary>
    /// Called by the pipeline once per instruction retired (committed, not flushed).
    /// ISA implementations that maintain a retired-instruction counter (e.g. Zicntr minstret)
    /// override this to increment it. Default: no-op.
    /// </summary>
    void OnRetire() {}
}

/// <summary>
/// The privilege level of the executing hart.
/// ISA-specific named levels (e.g. Machine, Supervisor) are defined by the ISA plugin.
/// </summary>
public readonly record struct PrivilegeLevel(int Level) : IComparable<PrivilegeLevel> {
    /// <summary>The least-privileged mode; applicable to any ISA.</summary>
    public static readonly PrivilegeLevel User = new(0);

    public int CompareTo(PrivilegeLevel other) => Level.CompareTo(other.Level);

    public static bool operator <(PrivilegeLevel a, PrivilegeLevel b) => a.Level < b.Level;
    public static bool operator >(PrivilegeLevel a, PrivilegeLevel b) => a.Level > b.Level;
    public static bool operator <=(PrivilegeLevel a, PrivilegeLevel b) => a.Level <= b.Level;
    public static bool operator >=(PrivilegeLevel a, PrivilegeLevel b) => a.Level >= b.Level;

    public static explicit operator PrivilegeLevel(int v) => new(v);
    public static explicit operator PrivilegeLevel(uint v) => new((int)v);
    public static explicit operator int(PrivilegeLevel p) => p.Level;
    public static explicit operator uint(PrivilegeLevel p) => (uint)p.Level;
}