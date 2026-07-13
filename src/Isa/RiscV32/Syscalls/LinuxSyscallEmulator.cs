using System.Text;
using Mechanism;

namespace RiscV32.Syscalls;

/// <summary>
/// gem5 SE-style Linux syscall emulator for statically linked RV32 binaries.
/// Wire into <see cref="Rv32Mechanism"/> via the <c>syscallHandler</c> parameter;
/// the executor will intercept every ECALL and route it here instead of trapping.
/// <para>
/// Syscall ABI: a7 (x17) = syscall number; a0–a5 (x10–x15) = args.
/// Return value written to a0 via <see cref="ExecuteResult.SideEffect"/>.
/// SYS_exit / SYS_exit_group set <see cref="ExecuteResult.RequestHalt"/> instead.
/// </para>
/// </summary>
public sealed class LinuxSyscallEmulator(ulong initialBreak, TextWriter? output = null) : ISyscallHandler {
    private ulong _brk = initialBreak;

    public ExecuteResult Handle(ulong num, IRegisterFile regs, IMemory memory, ulong pc) {
        ulong a0 = regs.Read(10);
        ulong a1 = regs.Read(11);
        ulong a2 = regs.Read(12);
        // a3-a5 reserved for future use

        if (num is 93 or 94) // SYS_exit, SYS_exit_group
            return new ExecuteResult { RequestHalt = true, };

        long result = num switch {
            64   => Write(a0, a1, a2, memory), // SYS_write
            63   => 0,                         // SYS_read   → EOF
            57   => 0,                         // SYS_close  → ok
            62   => -29L,                      // SYS_lseek  → ESPIPE
            80   => -9L,                       // SYS_fstat  → EBADF
            1024 => -2L,                       // SYS_open   → ENOENT
            56   => -2L,                       // SYS_openat → ENOENT
            214  => Brk(a0),                   // SYS_brk
            226  => 0,                         // SYS_mprotect → ok
            134  => 0,                         // SYS_rt_sigaction → ok
            135  => 0,                         // SYS_rt_sigprocmask → ok
            96   => 1L,                        // SYS_set_tid_address → tid=1
            172  => 1L,                        // SYS_getpid → 1
            178  => 1L,                        // SYS_gettid → 1
            29   => -25L,                      // SYS_ioctl  → ENOTTY
            160  => -1L,                       // SYS_uname  → EFAULT (no struct)
            _    => -38L,                      // ENOSYS
        };

        var ret = (ulong)result;
        return new ExecuteResult { SideEffect = s => s.IntegerRegisters.Write(10, ret), };
    }

    private long Write(ulong fd, ulong bufPtr, ulong count, IMemory memory) {
        if (fd > 2) return -9L; // EBADF
        if (output is null) return (long)count;
        var sb = new StringBuilder((int)count);
        for (ulong i = 0; i < count; i++) sb.Append((char)memory.Read(bufPtr + i, 1));
        output.Write(sb.ToString());
        return (long)count;
    }

    private long Brk(ulong requested) {
        if (requested == 0 || requested < _brk) return (long)_brk;
        _brk = requested;
        return (long)_brk;
    }
}