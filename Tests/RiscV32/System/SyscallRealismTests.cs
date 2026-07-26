#region

using System.Text;
using Mechanism;
using RiscV32.Memory;
using RiscV32.State;
using RiscV32.Syscalls;

#endregion

namespace Tests.RiscV32.System;

/// <summary>
///     Unit tests for the syscall-realism additions to <see cref="LinuxSyscallEmulator" />: real
///     host file I/O, an anonymous-mmap bump allocator, deterministic clock/getrandom, and
///     fcntl. Calls <see cref="LinuxSyscallEmulator.Handle" /> directly (no ELF/Train needed) —
///     these are unit tests of the emulator itself, complementing the ELF-driven SE-mode tests
///     in <see cref="SyscallEmulationTests" />.
/// </summary>
public class SyscallRealismTests {
    private const ulong AtFdcwd = 0xFFFF_FF9CUL; // -100 two's complement

    private static void WriteCString(FlatMemory memory, ulong addr, string s) =>
        memory.Load(addr, Encoding.UTF8.GetBytes(s + "\0"));

    /// <summary>
    ///     Invokes Handle() with a0..a5 set from <paramref name="args" />, applies the SideEffect,
    ///     and returns the resulting a0 — sign-extended from 32 bits, matching how a real RV32 hart
    ///     (and <see cref="Rv32ArchState" />'s 32-bit-wide register file, used here) represents a
    ///     negative syscall return in a0. Callers comparing against a legitimate large unsigned
    ///     address (e.g. an mmap result) must sign-extend the expected value the same way — see
    ///     <see cref="Sext32" />.
    /// </summary>
    private static long Call(LinuxSyscallEmulator handler, ulong num, IMemory memory, params ulong[] args) {
        var state = new Rv32ArchState();
        for (var i = 0; i < args.Length; i++) state.IntegerRegisters.Write(10 + i, args[i]);
        ExecuteResult result = handler.Handle(num, state, memory, 0, 0);
        result.SideEffect?.Invoke(state);
        return Sext32(state.IntegerRegisters.Read(10));
    }

    private static long Sext32(ulong v) => unchecked((int)(uint)v);

    [Fact]
    public void FileIo_OpenWriteCloseReopenReadLseek_RoundTrips() {
        string path = Path.Combine(Path.GetTempPath(), $"horologium_test_{Guid.NewGuid():N}.bin");
        try {
            var memory = new FlatMemory(0x10000, 0x8000_0000UL);
            var handler = new LinuxSyscallEmulator(0x8000_0000UL);
            const ulong pathAddr = 0x8000_1000UL;
            const ulong bufAddr = 0x8000_2000UL;
            WriteCString(memory, pathAddr, path);

            // O_WRONLY|O_CREAT|O_TRUNC = 1 | 0x40 | 0x200
            long fdWrite = Call(handler, 56, memory, SyscallRealismTests.AtFdcwd, pathAddr, 0x241, 0x1A4);
            Assert.True(fdWrite >= 3);

            byte[] payload = "hello file\n"u8.ToArray();
            memory.Load(bufAddr, payload);
            long written = Call(handler, 64, memory, (ulong)fdWrite, bufAddr, (ulong)payload.Length);
            Assert.Equal(payload.Length, written);
            Assert.Equal(0, Call(handler, 57, memory, (ulong)fdWrite)); // close

            long fdRead = Call(handler, 56, memory, SyscallRealismTests.AtFdcwd, pathAddr, 0, 0); // O_RDONLY
            Assert.True(fdRead >= 3);

            const ulong readBufAddr = 0x8000_3000UL;
            long readN = Call(handler, 63, memory, (ulong)fdRead, readBufAddr, (ulong)payload.Length);
            Assert.Equal(payload.Length, readN);
            var readBack = new byte[payload.Length];
            for (var i = 0; i < payload.Length; i++) readBack[i] = (byte)memory.Read(readBufAddr + (ulong)i, 1);
            Assert.Equal(payload, readBack);

            Assert.Equal(0, Call(handler, 62, memory, (ulong)fdRead, 0, 0)); // lseek SEEK_SET -> 0
            long readN2 = Call(handler, 63, memory, (ulong)fdRead, readBufAddr, 5);
            Assert.Equal(5, readN2);

            Assert.Equal(0, Call(handler, 57, memory, (ulong)fdRead));
        }
        finally {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Read_UnknownFd_ReturnsEbadf() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        Assert.Equal(-9, Call(handler, 63, memory, 42, 0x8000_0100UL, 8));
    }

    [Fact]
    public void Fstat_ReturnsSizeAndRegularFileMode() {
        string path = Path.Combine(Path.GetTempPath(), $"horologium_test_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[100]);
        try {
            var memory = new FlatMemory(0x10000, 0x8000_0000UL);
            var handler = new LinuxSyscallEmulator(0x8000_0000UL);
            const ulong pathAddr = 0x8000_1000UL;
            const ulong statAddr = 0x8000_2000UL;
            WriteCString(memory, pathAddr, path);

            long fd = Call(handler, 56, memory, SyscallRealismTests.AtFdcwd, pathAddr, 0, 0);
            Assert.True(fd >= 3);

            Assert.Equal(0, Call(handler, 80, memory, (ulong)fd, statAddr));
            Assert.Equal(100L, unchecked((long)memory.Read(statAddr + 48, 8))); // st_size
            Assert.Equal(0x81A4UL, memory.Read(statAddr + 16, 4));              // st_mode: S_IFREG|0644
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1UL)] // F_GETFD
    [InlineData(2UL)] // F_SETFD
    [InlineData(3UL)] // F_GETFL
    [InlineData(4UL)] // F_SETFL
    public void Fcntl_KnownCommands_ReturnBenignSuccess(ulong cmd) {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        Assert.Equal(0, Call(handler, 25, memory, 3, cmd, 0));
    }

    [Fact]
    public void Fcntl_UnknownCommand_ReturnsEnosys() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        Assert.Equal(-38, Call(handler, 25, memory, 3, 0, 0)); // F_DUPFD, unimplemented
    }

    [Fact]
    public void Mmap_Unconfigured_ReturnsEnomem() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        long result = Call(handler, 222, memory, 0, 4096, 3, 0x22, unchecked((ulong)-1L), 0);
        Assert.Equal(-12, result);
    }

    [Fact]
    public void Mmap_AnonymousBumpAllocator_ReturnsDistinctRegionsThenExhausts() {
        const ulong mmapBase = 0x9000_0000UL;
        const ulong mmapLimit = mmapBase + 8192; // exactly two 4 KiB pages
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL, mmapBase: mmapBase, mmapLimit: mmapLimit);

        long first = Call(handler, 222, memory, 0, 4096, 3, 0x22, unchecked((ulong)-1L), 0);
        Assert.Equal(Sext32(mmapBase), first);

        long second = Call(handler, 222, memory, 0, 4096, 3, 0x22, unchecked((ulong)-1L), 0);
        Assert.Equal(Sext32(mmapBase + 4096), second);

        long third = Call(handler, 222, memory, 0, 4096, 3, 0x22, unchecked((ulong)-1L), 0);
        Assert.Equal(-12, third);
    }

    [Fact]
    public void Munmap_ReturnsSuccess() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        Assert.Equal(0, Call(handler, 215, memory, 0x9000_0000UL, 4096));
    }

    [Fact]
    public void ClockGettime_MonotonicallyIncreasesEachCall() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        const ulong tsAddr = 0x8000_0100UL;

        Assert.Equal(0, Call(handler, 113, memory, 0, tsAddr));
        ulong total1 = memory.Read(tsAddr, 8) * 1_000_000_000UL + memory.Read(tsAddr + 8, 8);

        Assert.Equal(0, Call(handler, 113, memory, 0, tsAddr));
        ulong total2 = memory.Read(tsAddr, 8) * 1_000_000_000UL + memory.Read(tsAddr + 8, 8);

        Assert.True(total2 > total1);
    }

    [Fact]
    public void GetRandom_FillsBufferWithNonZeroVaryingBytes() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        const ulong bufAddr = 0x8000_0100UL;

        Assert.Equal(16, Call(handler, 278, memory, bufAddr, 16, 0));
        var first = new byte[16];
        for (var i = 0; i < 16; i++) first[i] = (byte)memory.Read(bufAddr + (ulong)i, 1);
        Assert.Contains(first, b => b != 0);

        Assert.Equal(16, Call(handler, 278, memory, bufAddr, 16, 0));
        var second = new byte[16];
        for (var i = 0; i < 16; i++) second[i] = (byte)memory.Read(bufAddr + (ulong)i, 1);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Read_Fd0_WithNoInputStream_IsAlwaysEof() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL); // input: null (default)
        const ulong bufAddr = 0x8000_0100UL;

        Assert.Equal(0, Call(handler, 63, memory, 0, bufAddr, 64)); // SYS_read, fd=0
    }

    [Fact]
    public void Read_Fd0_WithInjectedStream_ReturnsBytesThenEof() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        using var stdin = new MemoryStream("hi\n"u8.ToArray());
        var handler = new LinuxSyscallEmulator(0x8000_0000UL, input: stdin);
        const ulong bufAddr = 0x8000_0100UL;

        long n = Call(handler, 63, memory, 0, bufAddr, 64); // SYS_read, fd=0, count=64
        Assert.Equal(3, n);
        var read = new byte[3];
        for (var i = 0; i < 3; i++) read[i] = (byte)memory.Read(bufAddr + (ulong)i, 1);
        Assert.Equal("hi\n"u8.ToArray(), read);

        // Exhausted: further reads are EOF, not an error or a repeat of the same bytes.
        Assert.Equal(0, Call(handler, 63, memory, 0, bufAddr, 64));
    }

    [Fact]
    public void Read_Fd0_WithInjectedStream_ShorterCountThanAvailable_ReadsSequentially() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        using var stdin = new MemoryStream("abcdef"u8.ToArray());
        var handler = new LinuxSyscallEmulator(0x8000_0000UL, input: stdin);
        const ulong bufAddr = 0x8000_0100UL;

        Assert.Equal(3, Call(handler, 63, memory, 0, bufAddr, 3));
        var firstThree = new byte[3];
        for (var i = 0; i < 3; i++) firstThree[i] = (byte)memory.Read(bufAddr + (ulong)i, 1);
        Assert.Equal("abc"u8.ToArray(), firstThree);

        // Never rewound — the next read continues from where the last one left off.
        Assert.Equal(3, Call(handler, 63, memory, 0, bufAddr, 3));
        var nextThree = new byte[3];
        for (var i = 0; i < 3; i++) nextThree[i] = (byte)memory.Read(bufAddr + (ulong)i, 1);
        Assert.Equal("def"u8.ToArray(), nextThree);
    }

    // musl's buffered stdio writes via SYS_writev, not plain SYS_write — discovered only once a
    // real linked binary could be run (RealLinkedBinaryTests): unimplemented, it silently produced
    // no output at all. iovec is { void *iov_base; size_t iov_len } — 2 wordSize-wide fields per
    // entry (wordSize=4 here, the default, so 8 bytes/entry).
    [Fact]
    public void Writev_Fd1_ConcatenatesAllIovecsInOrder() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(0x8000_0000UL, sw);
        const ulong iovAddr = 0x8000_0100UL;
        const ulong buf1 = 0x8000_0200UL;
        const ulong buf2 = 0x8000_0300UL;
        WriteCString(memory, buf1, "hello ");
        WriteCString(memory, buf2, "world");

        // struct iovec[0] = { buf1, 6 }, iovec[1] = { buf2, 5 } (excluding each NUL terminator)
        memory.Write(iovAddr + 0, buf1, 4);
        memory.Write(iovAddr + 4, 6, 4);
        memory.Write(iovAddr + 8, buf2, 4);
        memory.Write(iovAddr + 12, 5, 4);

        long n = Call(handler, 66, memory, 1, iovAddr, 2); // SYS_writev, fd=1, iovcnt=2
        Assert.Equal(11, n);
        Assert.Equal("hello world", sw.ToString());
    }

    [Fact]
    public void Writev_ZeroLengthIovec_IsSkippedNotTreatedAsError() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(0x8000_0000UL, sw);
        const ulong iovAddr = 0x8000_0100UL;
        const ulong buf = 0x8000_0200UL;
        WriteCString(memory, buf, "ok");

        // iovec[0] = { buf, 0 } (empty), iovec[1] = { buf, 2 }
        memory.Write(iovAddr + 0, buf, 4);
        memory.Write(iovAddr + 4, 0, 4);
        memory.Write(iovAddr + 8, buf, 4);
        memory.Write(iovAddr + 12, 2, 4);

        long n = Call(handler, 66, memory, 1, iovAddr, 2);
        Assert.Equal(2, n);
        Assert.Equal("ok", sw.ToString());
    }

    [Fact]
    public void Writev_Fd3_WritesToRealFileInOrder() {
        string path = Path.Combine(Path.GetTempPath(), $"horologium_test_{Guid.NewGuid():N}.bin");
        try {
            var memory = new FlatMemory(0x10000, 0x8000_0000UL);
            var handler = new LinuxSyscallEmulator(0x8000_0000UL);
            const ulong pathAddr = 0x8000_1000UL;
            const ulong buf1 = 0x8000_2000UL;
            const ulong buf2 = 0x8000_2100UL;
            const ulong iovAddr = 0x8000_2200UL;
            WriteCString(memory, pathAddr, path);
            WriteCString(memory, buf1, "AB");
            WriteCString(memory, buf2, "CD");
            memory.Write(iovAddr + 0, buf1, 4);
            memory.Write(iovAddr + 4, 2, 4);
            memory.Write(iovAddr + 8, buf2, 4);
            memory.Write(iovAddr + 12, 2, 4);

            // O_WRONLY|O_CREAT|O_TRUNC = 1 | 0x40 | 0x200
            long fd = Call(handler, 56, memory, SyscallRealismTests.AtFdcwd, pathAddr, 0x241, 0x1A4);
            Assert.True(fd >= 3);

            Assert.Equal(4, Call(handler, 66, memory, (ulong)fd, iovAddr, 2));
            Assert.Equal(0, Call(handler, 57, memory, (ulong)fd));

            Assert.Equal("ABCD", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}