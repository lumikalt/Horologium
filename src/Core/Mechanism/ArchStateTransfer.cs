#region

using System.Text;

#endregion

namespace Mechanism;

/// <summary>
///     Copies architectural state directly from one <see cref="IArchState" /> instance into
///     another — PC, privilege, integer registers, and the ISA-specific blob (CSRs, VRF, UVE)
///     via <see cref="IArchState.WriteState" />/<see cref="IArchState.ReadState" />. No memory is
///     involved, unlike <see cref="ArchitecturalCheckpoint" />.
///     <para>
///         For handoffs between two live <c>Train</c> instances that already share the same
///         backing <c>IMemory</c> (e.g. alternating a functional fast-forward train with a
///         detailed pipeline train, as in SMARTS sampling) — the shared memory means only the
///         register-level state needs to move, so this avoids the full-memory snapshot cost of
///         <see cref="ArchitecturalCheckpoint" />, which would be prohibitive across thousands of
///         switches.
///     </para>
/// </summary>
public static class ArchStateTransfer {
    /// <summary>Copies <paramref name="from" />'s architectural state into <paramref name="to" />.</summary>
    public static void CopyInto(IArchState from, IArchState to) {
        to.Pc = from.Pc;
        to.PrivilegeLevel = from.PrivilegeLevel;

        int count = Math.Min(from.IntegerRegisters.Count, to.IntegerRegisters.Count);
        for (var i = 0; i < count; i++) to.IntegerRegisters.Write(i, from.IntegerRegisters.Read(i));

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true)) { from.WriteState(bw); }

        ms.Position = 0;
        using var br = new BinaryReader(ms, Encoding.UTF8, true);
        to.ReadState(br);
    }
}