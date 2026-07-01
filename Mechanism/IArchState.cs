namespace Mechanism;

/// <summary>
/// The complete architectural state of a running hart (hardware thread).
/// <para>
/// This is the live mutable state the executor reads and writes.
/// The ISA plugin owns the concrete implementation — the pipeline
/// only ever sees this interface.
/// </para>
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
    void OnCycle() { }

    /// <summary>
    /// Called by the pipeline once per instruction retired (committed, not flushed).
    /// ISA implementations that maintain a retired-instruction counter (e.g. Zicntr minstret)
    /// override this to increment it. Default: no-op.
    /// </summary>
    void OnRetire() { }
}

/// <summary>
/// The privilege level of the executing hart.
/// ISA-specific named levels (e.g. Machine, Supervisor) are defined by the ISA plugin.
/// </summary>
public readonly record struct PrivilegeLevel(int Level) : IComparable<PrivilegeLevel> {
    /// <summary>The least-privileged mode; applicable to any ISA.</summary>
    public static readonly PrivilegeLevel User = new(0);

    /// <inheritdoc />
    public int CompareTo(PrivilegeLevel other) => Level.CompareTo(other.Level);

    /// <summary>
    /// Equality operator.
    /// </summary>
    /// <param name="a">
    /// 1st operand.
    /// </param>
    /// <param name="b">
    /// 2nd operand.
    /// </param>
    /// <returns>
    /// True if <paramref name="a"/> and <paramref name="b"/> are equal.
    /// </returns>
    public static bool operator <(PrivilegeLevel a, PrivilegeLevel b) => a.Level < b.Level;

    /// <summary>
    /// Inequality operator.
    /// </summary>
    /// <param name="a">
    /// 1st operand.
    /// </param>
    /// <param name="b">
    /// 2nd operand.
    /// </param>
    /// <returns>
    /// True if <paramref name="a"/> and <paramref name="b"/> are not equal.
    /// </returns>
    public static bool operator >(PrivilegeLevel a, PrivilegeLevel b) => a.Level > b.Level;

    /// <summary>
    /// Less-than or equal-to operator.
    /// </summary>
    /// <param name="a">
    /// 1st operand.
    /// </param>
    /// <param name="b">
    /// 2nd operand.
    /// </param>
    /// <returns>
    /// True if <paramref name="a"/> is less than or equal to <paramref name="b"/>.
    /// </returns>
    public static bool operator <=(PrivilegeLevel a, PrivilegeLevel b) => a.Level <= b.Level;

    /// <summary>
    /// Greater-than or equal-to operator.
    /// </summary>
    /// <param name="a">
    /// 1st operand.
    /// </param>
    /// <param name="b">
    /// 2nd operand.
    /// </param>
    /// <returns>
    /// True if <paramref name="a"/> is greater than or equal to <paramref name="b"/>.
    /// </returns>
    public static bool operator >=(PrivilegeLevel a, PrivilegeLevel b) => a.Level >= b.Level;

    /// <summary>
    /// Conversion from int to PrivilegeLevel.
    /// </summary>
    /// <param name="v">Value.</param>
    /// <returns>
    /// PrivilegeLevel(v).
    /// </returns>
    public static explicit operator PrivilegeLevel(int v) => new(v);

    /// <summary>
    /// Conversion from uint to PrivilegeLevel.
    /// </summary>
    /// <param name="v">Value.</param>
    /// <returns>
    /// PrivilegeLevel(v).
    /// </returns>
    public static explicit operator PrivilegeLevel(uint v) => new((int)v);

    /// <summary>
    /// Conversion from PrivilegeLevel to int.
    /// </summary>
    /// <param name="p">Level.</param>
    /// <returns>
    /// p.Level.
    /// </returns>
    public static explicit operator int(PrivilegeLevel p) => p.Level;

    /// <summary>
    /// Conversion from PrivilegeLevel to uint.
    /// </summary>
    /// <param name="p">Level.</param>
    /// <returns>
    /// p.Level.
    /// </returns>
    public static explicit operator uint(PrivilegeLevel p) => (uint)p.Level;
}