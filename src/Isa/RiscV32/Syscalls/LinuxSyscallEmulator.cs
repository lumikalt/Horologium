#region

using System.Text;
using Mechanism;

#endregion

namespace RiscV32.Syscalls;

/// <summary>
///     gem5 SE-style Linux syscall emulator for statically linked RV32/RV64 binaries.
///     Wire into <see cref="Rv32Mechanism" />/<c>Rv64Mechanism</c> via the <c>syscallHandler</c>
///     parameter; the executor will intercept every ECALL and route it here instead of trapping.
///     <para>
///         Syscall ABI: a7 (x17) = syscall number; a0–a5 (x10–x15) = args.
///         Return value written to a0 via <see cref="ExecuteResult.SideEffect" />.
///         SYS_exit / SYS_exit_group set <see cref="ExecuteResult.RequestHalt" /> instead.
///     </para>
///     <para>
///         File I/O is unrestricted host passthrough (the gem5-SE/Spike-pk convention): paths
///         are opened exactly as given, resolved against the simulator process's own working
///         directory. There is no sandboxing — the guest binary is the user's own, already
///         compiled, locally run program, not untrusted code. <c>dirfd</c> is always treated as
///         <c>AT_FDCWD</c> (ignored); there is no support for opening relative to an arbitrary
///         already-open directory fd. stdin (fd 0) is separate from that passthrough — it reads
///         from the optional <paramref name="input" /> stream instead of a real file, so a caller
///         can redirect a fixed buffer/file without the guest ever opening anything.
///     </para>
///     <para>
///         <c>mmap</c> is a bump allocator over a caller-supplied <c>[mmapBase, mmapLimit)</c>
///         arena (anonymous mappings only; a file-backed mapping eagerly reads the backing file
///         into the region instead of tracking a real mapping). <c>munmap</c> never reclaims —
///         acceptable for the single-process, non-forking benchmark runs this targets; a caller
///         that exhausts the arena gets ENOMEM, same as real mmap under memory pressure, and
///         most mallocs fall back to <c>brk</c> when that happens. <c>clock_gettime</c> and
///         <c>getrandom</c> are deterministic (a synthetic incrementing clock; a seeded xorshift
///         PRNG) rather than reflecting real host time/entropy, matching this project's
///         reproducibility precedent (see <see cref="Mechanism.InitialStackBuilder" />'s fixed
///         AT_RANDOM bytes).
///     </para>
///     <para>
///         <c>fstat</c>'s struct layout was verified against the real RISC-V ABI by compiling
///         field-store probes with <c>riscv32-none-elf-gcc</c>/<c>riscv64-none-elf-gcc</c> and
///         reading the emitted store offsets — not reconstructed from memory. RV32's <c>fstat</c>
///         syscall (number 80) is <c>sys_fstat64</c>, filling the 104-byte <c>stat64</c> layout;
///         RV64's is <c>sys_newfstat</c>, filling the 128-byte <c>stat</c> layout — genuinely
///         different struct layouts under the same syscall number, selected here by
///         <paramref name="wordSize" />. <c>clock_gettime</c>'s <c>struct __kernel_timespec</c>
///         is 16 bytes (two 8-byte fields) on <em>both</em> RV32 and RV64 — RISC-V never
///         implemented the legacy 32-bit-time_t syscalls, so there is no wordSize dependency
///         there.
///     </para>
///     <para>
///         Validated against real linked musl binaries (see flake.nix's
///         riscv64-unknown-linux-musl-gcc), hand-assembled probes with independent offset
///         arithmetic (the same discipline as <c>abi_probe64.s</c>), and byte-level unit tests
///         calling <see cref="Handle" /> directly.
///     </para>
///     <para>
///         <c>clone()</c> (syscall 220) spawns a new hart via <see cref="Spawner" /> rather than
///         forking a new OS process/thread — it needs a driver that supports dynamic hart
///         activation (e.g. <c>MultiHartKernel</c>) wired up post-construction; with no
///         <see cref="Spawner" /> set, it returns <c>ENOSYS</c>, same as every other unimplemented
///         syscall here. Every spawned hart's syscalls should route to the <em>same</em>
///         <see cref="LinuxSyscallEmulator" /> instance (share it across every mechanism in the
///         pool), not one per hart — real threads share one fd table/brk/mmap arena
///         (<c>CLONE_FILES</c>/<c>CLONE_VM</c>), and this class's mutable state already models
///         exactly that if every hart's mechanism is wired to the one instance.
///         <c>CLONE_CHILD_CLEARTID</c> (the futex wake <c>pthread_join</c> blocks on) is not
///         implemented — a documented gap for the <c>futex()</c> syscall to close, not a silent
///         one; a real <c>pthread_join</c> on a hart spawned here will block forever until that
///         lands.
///     </para>
/// </summary>
/// <param name="initialBreak">Initial <c>brk</c> value — see <c>Rv32ElfWorkload.InitialBreak</c>.</param>
/// <param name="output">Captures SYS_write output for fd 0–2; discarded when null.</param>
/// <param name="wordSize">4 for RV32, 8 for RV64 — selects the <c>fstat</c> struct layout.</param>
/// <param name="mmapBase">Start of the anonymous-mmap bump-allocation arena; 0 disables mmap (ENOMEM).</param>
/// <param name="mmapLimit">Exclusive end of the mmap arena.</param>
/// <param name="input">
///     Redirected stdin for SYS_read on fd 0. Read sequentially, never rewound; returns 0 (EOF)
///     once exhausted. Not disposed by this class — the caller owns its lifecycle, matching
///     <paramref name="output" />. Null (the default) means stdin is always EOF, as before.
/// </param>
public sealed class LinuxSyscallEmulator(
    ulong initialBreak,
    TextWriter? output = null,
    int wordSize = 4,
    ulong mmapBase = 0,
    ulong mmapLimit = 0,
    Stream? input = null
) : ICheckpointableSyscallHandler, IDisposable {
    private const long ENoEnt = -2;
    private const long EBadF = -9;
    private const long EAcces = -13;
    private const long EIo = -5;
    private const long ESpipe = -29;
    private const long ENoSys = -38;

    // Regular file / character device st_mode bits (S_IFREG|0644, S_IFCHR|0620).
    private const int SIfregMode = 0x81A4;
    private const int SIfchrMode = 0x2190;

    // Path + access mode per open fd, alongside _files — needed by WriteState/ReadState to
    // reopen a file at checkpoint-restore time (a FileStream alone doesn't retain its own
    // open path/mode).
    private readonly Dictionary<int, (string Path, FileAccess Access)> _fileMeta = new();

    private readonly Dictionary<int, FileStream> _files = new();
    private ulong _brk = initialBreak;
    private ulong _fakeNanos;
    private ulong _mmapNext = mmapBase;
    private int _nextFd = 3;
    private uint _randState = 0x9E37_79B9;

    // Bytes delivered to the guest via fd-0 reads so far — WriteState/ReadState's only handle
    // on "stdin position", since `input` is a caller-owned Stream this class doesn't seek freely.
    private ulong _stdinConsumed;

    /// <summary>
    ///     Lets <c>clone()</c> (syscall 220) actually spawn a new hart — set by whatever is
    ///     driving the simulation (e.g. <c>MultiHartKernel</c>), after construction (this
    ///     instance, and the mechanisms wired to it, must already exist before the driver that
    ///     steps them can be built). Null (the default) makes <c>clone()</c> return <c>ENOSYS</c>,
    ///     matching the single-hart behavior every other caller of this class already relies on.
    /// </summary>
    public IHartSpawner? Spawner { get; set; }

    /// <summary>
    ///     Serializes brk/mmap cursors, the fd table (path + access mode + current position per
    ///     open fd), the stdin byte-position, and the deterministic clock/PRNG cursors. Open
    ///     stdout/stderr capture (<see cref="output" />) is a host-side sink, not emulator state,
    ///     and isn't part of this — a checkpoint-and-measure interval doesn't compare captured
    ///     output, only guest-visible behavior.
    /// </summary>
    public void WriteState(BinaryWriter writer) {
        writer.Write(_brk);
        writer.Write(_mmapNext);
        writer.Write(_nextFd);
        writer.Write(_randState);
        writer.Write(_fakeNanos);
        writer.Write(_stdinConsumed);

        writer.Write(_files.Count);
        foreach ((int fd, FileStream fs) in _files) {
            (string path, FileAccess access) = _fileMeta[fd];
            writer.Write(fd);
            writer.Write(path);
            writer.Write((int)access);
            writer.Write(fs.Position);
        }
    }

    /// <summary>
    ///     Restores state written by <see cref="WriteState" />. Any files currently open on this
    ///     instance are closed first; restored fds are reopened at their recorded path (never
    ///     truncating — <see cref="FileMode.Open" /> regardless of how the fd was originally
    ///     created) and seeked to their recorded position. Stdin is fast-forwarded to its recorded
    ///     byte position by seeking (if <see cref="input" /> is seekable) or by discarding bytes.
    /// </summary>
    public void ReadState(BinaryReader reader) {
        _brk = reader.ReadUInt64();
        _mmapNext = reader.ReadUInt64();
        _nextFd = reader.ReadInt32();
        _randState = reader.ReadUInt32();
        _fakeNanos = reader.ReadUInt64();
        ulong stdinConsumed = reader.ReadUInt64();

        foreach (FileStream fs in _files.Values) fs.Dispose();
        _files.Clear();
        _fileMeta.Clear();

        int fileCount = reader.ReadInt32();
        for (var i = 0; i < fileCount; i++) {
            int fd = reader.ReadInt32();
            string path = reader.ReadString();
            var access = (FileAccess)reader.ReadInt32();
            long position = reader.ReadInt64();

            var fs = new FileStream(path, FileMode.Open, access);
            fs.Position = position;
            _files[fd] = fs;
            _fileMeta[fd] = (path, access);
        }

        SkipStdinTo(stdinConsumed);
    }

    public ExecuteResult Handle(ulong num, IArchState state, IMemory memory, ulong pc) {
        IRegisterFile regs = state.IntegerRegisters;
        ulong a0 = regs.Read(10);
        ulong a1 = regs.Read(11);
        ulong a2 = regs.Read(12);
        ulong a3 = regs.Read(13);
        ulong a4 = regs.Read(14);
        ulong a5 = regs.Read(15);

        if (num is 93 or 94) // SYS_exit, SYS_exit_group
            return new ExecuteResult { RequestHalt = true, };

        if (num == 220) return Clone(a0, a1, a2, a3, state, pc, memory); // SYS_clone

        long result = num switch {
            64   => Write(a0, a1, a2, memory),    // SYS_write
            66   => Writev(a0, a1, a2, memory),   // SYS_writev
            63   => Read(a0, a1, a2, memory),     // SYS_read
            57   => Close(a0),                    // SYS_close
            62   => Lseek(a0, a1, a2),            // SYS_lseek
            80   => Fstat(a0, a1, memory),        // SYS_fstat
            1024 => LinuxSyscallEmulator.ENoEnt,  // SYS_open (not a real RV syscall; openat only)
            56   => Openat(a1, a2, memory),       // SYS_openat
            214  => Brk(a0),                      // SYS_brk
            222  => Mmap(a1, a3, a4, a5, memory), // SYS_mmap (a0 addr-hint ignored, no MAP_FIXED)
            215  => 0,                            // SYS_munmap → ok (arena never reclaims)
            226  => 0,                            // SYS_mprotect → ok
            113  => ClockGettime(a1, memory),     // SYS_clock_gettime
            278  => GetRandom(a0, a1, memory),    // SYS_getrandom
            25   => Fcntl(a1),                    // SYS_fcntl
            134  => 0,                            // SYS_rt_sigaction → ok
            135  => 0,                            // SYS_rt_sigprocmask → ok
            96   => 1L,                           // SYS_set_tid_address → tid=1
            172  => 1L,                           // SYS_getpid → 1
            178  => 1L,                           // SYS_gettid → 1
            29   => -25L,                         // SYS_ioctl → ENOTTY
            160  => -1L,                          // SYS_uname → EFAULT (no struct)
            _    => LinuxSyscallEmulator.ENoSys,
        };

        var ret = (ulong)result;
        return new ExecuteResult { SideEffect = s => s.IntegerRegisters.Write(10, ret), };
    }

    // RISC-V's raw sys_clone(flags, newsp, ptid, tls, ctid) — a0..a4 — confirmed against the
    // actual compiled musl 1.2.5 __clone (riscv64-unknown-linux-musl-gcc): a2=ptid, a3=tls,
    // a4=ctid (not the generic-Linux-ABI ptid/ctid/tls order some other architectures use).
    // Real clone() semantics: parent and child both resume at the same next instruction (the one
    // right after this ecall) with every register identical to the parent's at the moment of the
    // call, except the child's sp is forced to newsp and (if CLONE_SETTLS is set) tp to tls, with
    // a0 = new hart id in the parent and 0 in the child — so the new hart's initial state is the
    // parent's own Snapshot() with exactly those overrides, not a from-scratch entry point.
    // CLONE_CHILD_CLEARTID (the wake-on-exit futex musl's pthread_join blocks on) is not
    // implemented — a documented gap for the futex() TODO item, not silently ignored.
    private ExecuteResult Clone(ulong flags, ulong newSp, ulong ptid, ulong tls, IArchState state, ulong pc, IMemory memory) {
        const ulong cloneSetTls = 0x0008_0000;
        const ulong cloneParentSetTid = 0x0010_0000;

        if (Spawner is null) {
            var noSys = unchecked((ulong)LinuxSyscallEmulator.ENoSys);
            return new ExecuteResult { SideEffect = s => s.IntegerRegisters.Write(10, noSys), };
        }

        IArchState child = state.Snapshot();
        child.Pc = pc + 4; // ECALL is always 4 bytes wide, even under RVC — no compressed form exists
        child.IntegerRegisters.Write(2, newSp);
        if ((flags & cloneSetTls) != 0) child.IntegerRegisters.Write(4, tls);
        child.IntegerRegisters.Write(10, 0); // child's own return value: 0

        int newHartId = Spawner.SpawnHart(child);

        if ((flags & cloneParentSetTid) != 0) memory.Write(ptid, (ulong)newHartId, 4);

        var ret = (ulong)newHartId;
        return new ExecuteResult { SideEffect = s => s.IntegerRegisters.Write(10, ret), };
    }

    public void Dispose() {
        foreach (FileStream fs in _files.Values) fs.Dispose();
        _files.Clear();
        _fileMeta.Clear();
    }

    private void SkipStdinTo(ulong consumed) {
        _stdinConsumed = 0;
        if (input is null || consumed == 0) return;

        if (input.CanSeek) {
            input.Seek((long)consumed, SeekOrigin.Begin);
            _stdinConsumed = consumed;
            return;
        }

        var discard = new byte[8192];
        ulong remaining = consumed;
        while (remaining > 0) {
            int n = input.Read(discard, 0, (int)Math.Min((ulong)discard.Length, remaining));
            if (n <= 0) break; // stream shorter than the recorded position — nothing more to skip
            remaining -= (ulong)n;
        }

        _stdinConsumed = consumed - remaining;
    }

    private long Write(ulong fd, ulong bufPtr, ulong count, IMemory memory) {
        if (fd <= 2) {
            if (output is null) return (long)count;
            var sb = new StringBuilder((int)count);
            for (ulong i = 0; i < count; i++) sb.Append((char)memory.Read(bufPtr + i, 1));
            output.Write(sb.ToString());
            return (long)count;
        }

        if (!_files.TryGetValue((int)fd, out FileStream? fs)) return LinuxSyscallEmulator.EBadF;
        var buf = new byte[count];
        for (ulong i = 0; i < count; i++) buf[i] = (byte)memory.Read(bufPtr + i, 1);
        fs.Write(buf, 0, (int)count);
        return (long)count;
    }

    // musl's buffered stdio (fwrite/printf's flush path) writes via SYS_writev, not plain
    // SYS_write — a real libc detail this project only discovered once a real linked binary
    // could finally be run (see project memory): without this, printf silently produced no
    // output at all (ENOSYS from the fallback case, which musl's stdio swallows as a write
    // error rather than surfacing it). struct iovec is { void *iov_base; size_t iov_len; } —
    // two wordSize-wide fields, so entry i starts at iov + i·(2·wordSize).
    private long Writev(ulong fd, ulong iovPtr, ulong iovCount, IMemory memory) {
        long total = 0;
        for (ulong i = 0; i < iovCount; i++) {
            ulong entry = iovPtr + i * (ulong)(2 * wordSize);
            ulong iovBase = memory.Read(entry, wordSize);
            ulong iovLen = memory.Read(entry + (ulong)wordSize, wordSize);
            if (iovLen == 0) continue;

            long n = Write(fd, iovBase, iovLen, memory);
            if (n < 0) return total > 0 ? total : n; // first entry fails → propagate the error
            total += n;
            if (n < (long)iovLen) break; // short write — matches real writev's stop-on-short-write
        }

        return total;
    }

    private long Read(ulong fd, ulong bufPtr, ulong count, IMemory memory) {
        if (fd == 0) {
            if (input is null) return 0; // no stdin redirected: always EOF
            var stdinBuf = new byte[count];
            int stdinN = input.Read(stdinBuf, 0, (int)count);
            if (stdinN > 0) {
                memory.Load(bufPtr, stdinBuf.AsSpan(0, stdinN));
                _stdinConsumed += (ulong)stdinN;
            }

            return stdinN;
        }

        if (!_files.TryGetValue((int)fd, out FileStream? fs)) return LinuxSyscallEmulator.EBadF;
        var buf = new byte[count];
        int n = fs.Read(buf, 0, (int)count);
        if (n > 0) memory.Load(bufPtr, buf.AsSpan(0, n));
        return n;
    }

    private long Close(ulong fd) {
        if (fd <= 2) return 0;
        if (!_files.Remove((int)fd, out FileStream? fs)) return LinuxSyscallEmulator.EBadF;
        _fileMeta.Remove((int)fd);
        fs.Dispose();
        return 0;
    }

    private long Lseek(ulong fd, ulong offset, ulong whence) {
        if (!_files.TryGetValue((int)fd, out FileStream? fs)) return LinuxSyscallEmulator.ESpipe;
        SeekOrigin origin = whence switch {
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin,
        };
        return fs.Seek((long)offset, origin);
    }

    private long Openat(ulong pathPtr, ulong flags, IMemory memory) {
        const ulong oAccmode = 3, oCreat = 0x40, oExcl = 0x80, oTrunc = 0x200, oAppend = 0x400;
        string path = ReadCString(memory, pathPtr);
        var accMode = (int)(flags & oAccmode);
        bool creat = (flags & oCreat) != 0;
        bool excl = (flags & oExcl) != 0;
        bool trunc = (flags & oTrunc) != 0;
        bool append = (flags & oAppend) != 0;

        FileMode fileMode = (creat, excl, trunc) switch {
            (true, true, _) => FileMode.CreateNew,
            (true, _, true) => FileMode.Create,
            (true, _, _)    => FileMode.OpenOrCreate,
            (_, _, true)    => FileMode.Truncate,
            _               => FileMode.Open,
        };
        FileAccess access = accMode switch {
            0 => FileAccess.Read,
            1 => FileAccess.Write,
            _ => FileAccess.ReadWrite,
        };
        if (append) {
            fileMode = FileMode.Append;
            access = FileAccess.Write;
        }

        try {
            var fs = new FileStream(path, fileMode, access);
            int fd = _nextFd++;
            _files[fd] = fs;
            _fileMeta[fd] = (path, access);
            return fd;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
            return LinuxSyscallEmulator.ENoEnt;
        }
        catch (UnauthorizedAccessException) { return LinuxSyscallEmulator.EAcces; }
        catch (IOException) { return LinuxSyscallEmulator.EIo; }
    }

    private long Fstat(ulong fd, ulong statPtr, IMemory memory) {
        long size;
        int mode;
        if (fd <= 2) {
            size = 0;
            mode = LinuxSyscallEmulator.SIfchrMode;
        }
        else if (_files.TryGetValue((int)fd, out FileStream? fs)) {
            size = fs.Length;
            mode = LinuxSyscallEmulator.SIfregMode;
        }
        else { return LinuxSyscallEmulator.EBadF; }

        WriteStat(memory, statPtr, size, mode);
        return 0;
    }

    private void WriteStat(IMemory memory, ulong ptr, long size, int mode) {
        // Offsets through st_blocks (64) are identical for RV32's stat64 and RV64's stat — see
        // the class doc comment for how they were verified. Only the timestamp tail (from 72)
        // differs: 4-byte int fields on stat64, 8-byte long fields on stat.
        memory.Write(ptr + 0, 0, 8); // st_dev
        memory.Write(ptr + 8, 1, 8); // st_ino
        memory.Write(ptr + 16, (ulong)mode, 4);
        memory.Write(ptr + 20, 1, 4);                           // st_nlink
        memory.Write(ptr + 24, 0, 4);                           // st_uid
        memory.Write(ptr + 28, 0, 4);                           // st_gid
        memory.Write(ptr + 32, 0, 8);                           // st_rdev
        memory.Write(ptr + 48, (ulong)size, 8);                 // st_size
        memory.Write(ptr + 56, 4096, 4);                        // st_blksize
        memory.Write(ptr + 64, (ulong)((size + 511) / 512), 8); // st_blocks

        int ts = wordSize == 8 ? 8 : 4;
        for (var i = 0; i < 6; i++)
            memory.Write(ptr + 72 + (ulong)(i * ts), 0, ts); // atime/mtime/ctime (sec,nsec) — fixed epoch
    }

    private long Brk(ulong requested) {
        if (requested == 0 || requested < _brk) return (long)_brk;
        _brk = requested;
        return (long)_brk;
    }

    private long Mmap(ulong length, ulong flags, ulong fd, ulong offset, IMemory memory) {
        const ulong mapAnonymous = 0x20;
        if (mmapLimit == 0) return -12; // ENOMEM: no arena configured

        ulong len = (length + 0xFFFUL) & ~0xFFFUL;
        if (len == 0) len = 4096;
        if (_mmapNext + len > mmapLimit) return -12; // ENOMEM: arena exhausted

        ulong addr = _mmapNext;
        _mmapNext += len;

        if ((flags & mapAnonymous) == 0 && _files.TryGetValue((int)fd, out FileStream? fs)) {
            fs.Position = (long)offset;
            var buf = new byte[len];
            int n = fs.Read(buf, 0, (int)len);
            if (n > 0) memory.Load(addr, buf.AsSpan(0, n));
        }

        return (long)addr;
    }

    private long ClockGettime(ulong tsPtr, IMemory memory) {
        _fakeNanos += 1_000_000;                                // 1 ms per call — deterministic, not real host time
        memory.Write(tsPtr, _fakeNanos / 1_000_000_000, 8);     // tv_sec
        memory.Write(tsPtr + 8, _fakeNanos % 1_000_000_000, 8); // tv_nsec
        return 0;
    }

    private long GetRandom(ulong bufPtr, ulong buflen, IMemory memory) {
        for (ulong i = 0; i < buflen; i++) {
            _randState ^= _randState << 13;
            _randState ^= _randState >> 17;
            _randState ^= _randState << 5;
            memory.Write(bufPtr + i, _randState & 0xFF, 1);
        }

        return (long)buflen;
    }

    private static long Fcntl(ulong cmd) => cmd switch {
        1 or 2 or 3 or 4 => 0,                           // F_GETFD/F_SETFD/F_GETFL/F_SETFL → benign success
        _                => LinuxSyscallEmulator.ENoSys, // F_DUPFD and everything else unimplemented
    };

    private static string ReadCString(IMemory memory, ulong address) {
        var bytes = new List<byte>();
        ulong a = address;
        while (true) {
            var b = (byte)memory.Read(a, 1);
            if (b == 0) break;
            bytes.Add(b);
            a++;
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}