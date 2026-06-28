using Mechanism;

namespace RiscV32.Memory;

/// <summary>
/// Intercepts writes to the HTIF tohost register and auto-acknowledges
/// syscall requests by writing 1 to fromhost (tohost+8).
///
/// HTIF benchmarks write a syscall pointer (even non-zero value) to tohost and then
/// spin on fromhost waiting for acknowledgement. Without an ACK, the program stalls
/// in the polling loop forever. This wrapper provides the minimal auto-ACK so the
/// benchmark can progress to tohost_exit normally.
///
/// Exit-code writes ((code&lt;&lt;1)|1, always odd) are passed through without ACK so
/// callers can detect completion by reading tohost after the run.
/// </summary>
public sealed class HtifMemory(IMemory inner, ulong tohostAddr) : IMemory {
    public ulong Read(ulong address, int bytes) => inner.Read(address, bytes);

    public void Write(ulong address, ulong value, int bytes) {
        inner.Write(address, value, bytes);
        if (address == tohostAddr && bytes == 4 && value != 0 && (value & 1) == 0) inner.Write(tohostAddr + 8, 1, 4);
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) => inner.Load(address, data);
}