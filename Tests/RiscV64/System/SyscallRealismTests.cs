#region

using System.Text;
using Mechanism;
using RiscV32.Memory;
using RiscV32.Syscalls;
using RiscV64.State;

#endregion

namespace Tests.RiscV64.System;

/// <summary>
///     Proves <see cref="LinuxSyscallEmulator" />'s <c>wordSize</c> parameter actually selects a
///     different <c>fstat</c> struct layout — RV64's 128-byte <c>struct stat</c> extends further
///     than RV32's 104-byte <c>stat64</c> (see the class doc comment for how both were verified
///     against real toolchain codegen). Complements
///     <see cref="Tests.RiscV32.System.SyscallRealismTests" />, which covers the shared-layout
///     prefix and every other syscall in detail; this file only needs the one distinguishing case.
/// </summary>
public class SyscallRealismTests {
    private static long Call(LinuxSyscallEmulator handler, ulong num, IMemory memory, params ulong[] args) {
        var state = new Rv64ArchState();
        for (var i = 0; i < args.Length; i++) state.IntegerRegisters.Write(10 + i, args[i]);
        ExecuteResult result = handler.Handle(num, state, memory, 0, 0);
        result.SideEffect?.Invoke(state);
        return unchecked((long)state.IntegerRegisters.Read(10));
    }

    [Fact]
    public void Fstat_WordSize8_WritesFurtherIntoTheTimestampTailThanWordSize4() {
        string path = Path.Combine(Path.GetTempPath(), $"horologium_test_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[10]);
        try {
            const ulong statAddr = 0x8000_2000UL;

            var mem32 = new FlatMemory(0x10000, 0x8000_0000UL);
            for (ulong i = 0; i < 128; i++) mem32.Write(statAddr + i, 0xFF, 1); // sentinel-fill
            var handler32 = new LinuxSyscallEmulator(0x8000_0000UL, wordSize: 4);
            long fd32 = Call(handler32, 56, mem32, 0xFFFF_FF9CUL, WritePath(mem32, path), 0, 0);
            Assert.Equal(0, Call(handler32, 80, mem32, (ulong)fd32, statAddr));
            Assert.Equal(0xFFUL, mem32.Read(statAddr + 100, 1)); // past RV32's 104-byte stat64: untouched

            var mem64 = new FlatMemory(0x10000, 0x8000_0000UL);
            for (ulong i = 0; i < 128; i++) mem64.Write(statAddr + i, 0xFF, 1);
            var handler64 = new LinuxSyscallEmulator(0x8000_0000UL, wordSize: 8);
            long fd64 = Call(handler64, 56, mem64, 0xFFFF_FF9CUL, WritePath(mem64, path), 0, 0);
            Assert.Equal(0, Call(handler64, 80, mem64, (ulong)fd64, statAddr));
            Assert.Equal(
                0UL, mem64.Read(statAddr + 100, 1)
            ); // within RV64's 128-byte stat: zeroed by the timestamp tail
        }
        finally { File.Delete(path); }
    }

    private static ulong WritePath(FlatMemory memory, string path) {
        const ulong pathAddr = 0x8000_1000UL;
        memory.Load(pathAddr, Encoding.UTF8.GetBytes(path + "\0"));
        return pathAddr;
    }
}