using System.Text;
using Mechanism;

namespace RiscV32.Memory;

/// <summary>
/// Intercepts writes to the HTIF tohost register and executes fesvr magic-mem
/// syscalls, writing the return value back to magic_mem[0] before ACK-ing fromhost.
/// <para>
/// HTIF protocol: tohost = (code&lt;&lt;1)|1 → exit (odd, passed through); tohost =
/// even-nonzero ptr → magic_mem syscall request.  fromhost = 1 → ACK.
/// </para>
/// <para>
/// magic_mem layout: 8×uint64_t (64-byte aligned). Slot i is at ptr + i*8; on
/// RV32 the value is in the low 4 bytes, so each slot is read as 4 bytes.
/// magic_mem[0] = syscall number (input) / return value (output); [1..3] = args.
/// </para>
/// </summary>
public sealed class HtifMemory(IMemory inner, ulong tohostAddr, TextWriter? output = null) : IMemory {
    public ulong Read(ulong address, int bytes) => inner.Read(address, bytes);

    public void Write(ulong address, ulong value, int bytes) {
        inner.Write(address, value, bytes);
        if (address == tohostAddr && bytes == 4 && value != 0 && (value & 1) == 0) ServiceSyscall(value);
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) => inner.Load(address, data);

    private void ServiceSyscall(ulong ptr) {
        ulong num = inner.Read(ptr, 4);
        ulong arg0 = inner.Read(ptr + 8, 4);
        ulong arg1 = inner.Read(ptr + 16, 4);
        ulong arg2 = inner.Read(ptr + 24, 4);

        long result = num switch {
            64       => SyscallWrite(arg0, arg1, arg2), // SYS_write
            63       => SyscallRead(),                  // SYS_read
            57       => 0,                              // SYS_close
            62       => -29L,                           // SYS_lseek  → ESPIPE
            80       => -9L,                            // SYS_fstat  → EBADF
            1024     => -2L,                            // SYS_open   → ENOENT
            56       => -2L,                            // SYS_openat → ENOENT
            93 or 94 => 0,                              // SYS_exit / SYS_exit_group
            _        => -38L,                           // ENOSYS
        };

        inner.Write(ptr, (ulong)result, 4); // return value → magic_mem[0]
        inner.Write(tohostAddr + 8, 1, 4);  // fromhost ACK
    }

    private long SyscallWrite(ulong fd, ulong bufPtr, ulong count) {
        if (fd > 2) return -9L; // EBADF
        if (output is null) return (long)count;
        var sb = new StringBuilder((int)count);
        for (ulong i = 0; i < count; i++) sb.Append((char)inner.Read(bufPtr + i, 1));
        output.Write(sb.ToString());
        return (long)count;
    }

    private static long SyscallRead() => 0; // EOF
}