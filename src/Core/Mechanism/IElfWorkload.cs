namespace Mechanism;

/// <summary>
///     An <see cref="IWorkload" /> loaded from an ELF image, exposing symbol lookup. ISA-agnostic —
///     any ELF-based workload (RV32, RV64, …) can implement this; it lets callers that need a
///     symbol address (e.g. Region-of-Interest fast-forward) accept any ELF workload without
///     matching on a specific ISA's concrete workload type.
/// </summary>
public interface IElfWorkload : IWorkload {
    /// <summary>
    ///     Address just past the last PT_LOAD segment (i.e. the initial program break). Pass to
    ///     <c>RiscV32.Syscalls.LinuxSyscallEmulator</c> as <c>initialBreak</c> so SYS_brk starts
    ///     from the correct address.
    /// </summary>
    ulong InitialBreak { get; }

    /// <summary>
    ///     Address of the ELF program header table in loaded memory. Pass to
    ///     <c>InitialStackBuilder.BuildStandardAuxv</c> as <c>phdrAddr</c> (AT_PHDR) — real libcs
    ///     (musl included) walk the program headers themselves at startup to find PT_TLS/PT_GNU_STACK
    ///     and set up the thread pointer, so a zero/placeholder AT_PHDR makes that walk read from
    ///     address 0 and crash on any binary that declares thread-local data.
    /// </summary>
    ulong PhdrAddress { get; }

    /// <summary>Size of one program header table entry (AT_PHENT).</summary>
    ulong PhEntrySize { get; }

    /// <summary>Number of program header table entries (AT_PHNUM).</summary>
    ulong PhNum { get; }

    /// <summary>Returns the virtual address of a named ELF symbol, or throws if not found.</summary>
    ulong FindSymbol(string name);
}