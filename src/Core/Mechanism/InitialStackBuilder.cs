namespace Mechanism;

/// <summary>
///     Builds a RISC-V psABI-compliant initial process stack (argc/argv/envp/auxv) so a real
///     compiled binary's C runtime <c>_start</c> can run past its own prologue — bare-metal
///     entry (PC = ELF entry point, registers otherwise untouched) is not enough for anything
///     linked against a real libc, which reads its command line and environment straight off the
///     initial stack. ISA-agnostic: the layout is structurally identical for RV32/RV64, differing
///     only in pointer/word width (<c>wordSize</c> below), so one function serves
///     both; only RV64 callers exist so far (see <c>Tests/RiscV64/System/InitialStackTests.cs</c>),
///     RV32 gets the same capability "for free" but untested.
///     <para>
///         Layout, highest to lowest address: a string blob (argv strings, then envp strings,
///         then a 16-byte AT_RANDOM block — all NUL-terminated, byte-packed, no alignment
///         requirement between them); padding down to a 16-byte boundary; the auxv array
///         (<c>(a_type, a_val)</c> word-pairs, terminated by <c>(AT_NULL, 0)</c>, appended
///         automatically); the envp pointer array, NULL-terminated; the argv pointer array,
///         NULL-terminated; one <c>argc</c> word. The returned SP is the address of that
///         <c>argc</c> word, 16-byte aligned per the RISC-V calling convention (verified by an
///         assertion here — a cheap defensive check against a packing-math bug, not a
///         reproduction of any external spec test).
///     </para>
/// </summary>
public static class InitialStackBuilder {
    // AT_* auxv type constants (Linux/RISC-V psABI).
    /// <summary>Terminates the auxv array.</summary>
    public const ulong AtNull = 0;

    /// <summary>Address of the ELF program header table.</summary>
    public const ulong AtPhdr = 3;

    /// <summary>Size of one program header table entry.</summary>
    public const ulong AtPhent = 4;

    /// <summary>Number of program header table entries.</summary>
    public const ulong AtPhnum = 5;

    /// <summary>System page size.</summary>
    public const ulong AtPagesz = 6;

    /// <summary>Base address of the interpreter/dynamic linker; 0 when there is none.</summary>
    public const ulong AtBase = 7;

    /// <summary>The ELF entry point.</summary>
    public const ulong AtEntry = 9;

    /// <summary>Real user ID.</summary>
    public const ulong AtUid = 11;

    /// <summary>Effective user ID.</summary>
    public const ulong AtEuid = 12;

    /// <summary>Real group ID.</summary>
    public const ulong AtGid = 13;

    /// <summary>Effective group ID.</summary>
    public const ulong AtEgid = 14;

    /// <summary>CPU capability bitmask; 0 since extension-probing isn't modeled.</summary>
    public const ulong AtHwcap = 16;

    /// <summary>Nonzero if the binary should run in "secure" (e.g. setuid) mode.</summary>
    public const ulong AtSecure = 23;

    /// <summary>Address of 16 bytes of pseudo-random data, for the stack-protector canary.</summary>
    public const ulong AtRandom = 25;

    /// <summary>
    ///     Fixed, deterministic 16-byte pattern used for AT_RANDOM. Real glibc/musl dereference
    ///     AT_RANDOM unconditionally for the stack-protector canary — it must never be null/zero
    ///     — but this is a simulator, not a security context, so determinism (reproducible test
    ///     runs) matters more than actual randomness.
    /// </summary>
    private static readonly byte[] AtRandomBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,];

    /// <summary>
    ///     Builds the standards-minimal auxv set for a <em>statically-linked</em> binary (no
    ///     dynamic linker/interpreter): AT_PAGESZ, AT_PHDR/AT_PHENT/AT_PHNUM (needed by real
    ///     libcs for static-TLS setup even in non-PIE binaries), AT_BASE=0 (no interpreter),
    ///     AT_ENTRY, AT_UID/AT_EUID/AT_GID/AT_EGID=0, AT_HWCAP=0 (no extension-probing support
    ///     modeled), AT_SECURE=0. AT_RANDOM is not included here — <see cref="BuildInitialStack" />
    ///     appends it itself, since its value (a stack address) isn't known until the string blob
    ///     is placed. Not chased further than this: whether it's sufficient for a specific real
    ///     libc's <c>_start</c> is unanswerable without a real linked binary to fault-test
    ///     against, which is out of reach without a riscv64-*-linux-* userspace toolchain.
    /// </summary>
    public static IReadOnlyList<(ulong Type, ulong Value)> BuildStandardAuxv(
        ulong phdrAddr, ulong phEntrySize, ulong phNum, ulong entryPoint, ulong pageSize = 4096
    ) => [
        (InitialStackBuilder.AtPagesz, pageSize),
        (InitialStackBuilder.AtPhdr, phdrAddr),
        (InitialStackBuilder.AtPhent, phEntrySize),
        (InitialStackBuilder.AtPhnum, phNum),
        (InitialStackBuilder.AtBase, 0),
        (InitialStackBuilder.AtEntry, entryPoint),
        (InitialStackBuilder.AtUid, 0),
        (InitialStackBuilder.AtEuid, 0),
        (InitialStackBuilder.AtGid, 0),
        (InitialStackBuilder.AtEgid, 0),
        (InitialStackBuilder.AtHwcap, 0),
        (InitialStackBuilder.AtSecure, 0),
    ];

    /// <param name="memory">Backing memory to write into.</param>
    /// <param name="stackTop">Highest usable address (exclusive) — the stack grows down from here.</param>
    /// <param name="wordSize">4 for RV32, 8 for RV64.</param>
    /// <param name="argv">Command-line arguments; <c>argv[0]</c> is the program name.</param>
    /// <param name="envp">Environment strings, each in <c>NAME=value</c> form.</param>
    /// <param name="auxv">
    ///     Auxiliary vector entries (see <see cref="BuildStandardAuxv" />). AT_RANDOM and the
    ///     terminating AT_NULL are appended automatically — do not include them here.
    /// </param>
    /// <returns>The initial SP: the address of the <c>argc</c> word, 16-byte aligned.</returns>
    public static ulong BuildInitialStack(
        IMemory memory,
        ulong stackTop,
        int wordSize,
        IReadOnlyList<string> argv,
        IReadOnlyList<string> envp,
        IReadOnlyList<(ulong Type, ulong Value)> auxv
    ) {
        ulong cursor = stackTop;

        // ── String blob (highest addresses): AT_RANDOM block, then argv strings, then envp
        // strings. Byte-packed, NUL-terminated, no inter-string alignment requirement. ──
        cursor -= 16;
        ulong atRandomAddr = cursor;
        memory.Load(atRandomAddr, InitialStackBuilder.AtRandomBytes);

        var argvAddrs = new ulong[argv.Count];
        for (int i = argv.Count - 1; i >= 0; i--) cursor = WriteCString(memory, cursor, argv[i], out argvAddrs[i]);

        var envpAddrs = new ulong[envp.Count];
        for (int i = envp.Count - 1; i >= 0; i--) cursor = WriteCString(memory, cursor, envp[i], out envpAddrs[i]);

        ulong stringBlobBase = cursor;

        // ── Fixed-size region (argc, argv[], envp[], auxv[]) — placed so its low end (the
        // returned SP) is 16-byte aligned, with any leftover padding sitting harmlessly between
        // its high end and the string blob above. ──
        var fixedRegionSize = (ulong)(
            wordSize // argc
            + (argv.Count + 1) * wordSize // argv[] + NULL
            + (envp.Count + 1) * wordSize // envp[] + NULL
            + (auxv.Count + 2) * 2 * wordSize // caller's auxv pairs + AT_RANDOM pair + AT_NULL pair
        );

        ulong sp = (stringBlobBase - fixedRegionSize) & ~0xFUL;

        ulong argcAddr = sp;
        ulong argvArrayAddr = argcAddr + (ulong)wordSize;
        ulong envpArrayAddr = argvArrayAddr + (ulong)((argv.Count + 1) * wordSize);
        ulong auxvArrayAddr = envpArrayAddr + (ulong)((envp.Count + 1) * wordSize);

        memory.Write(argcAddr, (ulong)argv.Count, wordSize);

        for (var i = 0; i < argv.Count; i++)
            memory.Write(argvArrayAddr + (ulong)(i * wordSize), argvAddrs[i], wordSize);
        memory.Write(argvArrayAddr + (ulong)(argv.Count * wordSize), 0, wordSize);

        for (var i = 0; i < envp.Count; i++)
            memory.Write(envpArrayAddr + (ulong)(i * wordSize), envpAddrs[i], wordSize);
        memory.Write(envpArrayAddr + (ulong)(envp.Count * wordSize), 0, wordSize);

        for (var i = 0; i < auxv.Count; i++) {
            ulong pairAddr = auxvArrayAddr + (ulong)(i * 2 * wordSize);
            memory.Write(pairAddr, auxv[i].Type, wordSize);
            memory.Write(pairAddr + (ulong)wordSize, auxv[i].Value, wordSize);
        }

        ulong atRandomPairAddr = auxvArrayAddr + (ulong)(auxv.Count * 2 * wordSize);
        memory.Write(atRandomPairAddr, InitialStackBuilder.AtRandom, wordSize);
        memory.Write(atRandomPairAddr + (ulong)wordSize, atRandomAddr, wordSize);

        ulong atNullPairAddr = atRandomPairAddr + (ulong)(2 * wordSize);
        memory.Write(atNullPairAddr, InitialStackBuilder.AtNull, wordSize);
        memory.Write(atNullPairAddr + (ulong)wordSize, 0, wordSize);

        if (sp % 16 != 0) throw new InvalidOperationException($"InitialStackBuilder produced a misaligned SP (0x{sp:X}) — packing-math bug.");

        return sp;
    }

    /// <summary>Writes a NUL-terminated string just below <paramref name="cursor" />, returning the new cursor.</summary>
    private static ulong WriteCString(IMemory memory, ulong cursor, string s, out ulong address) {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        cursor -= (ulong)bytes.Length;
        memory.Load(cursor, bytes);
        address = cursor;
        return cursor;
    }
}
