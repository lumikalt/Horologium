#region

using System.Text;
using Mechanism;
using RiscV32.Memory;
using RiscV32.State;
using RiscV32.Syscalls;

#endregion

namespace Tests.RiscV32.System;

/// <summary>
///     Unit tests for <see cref="LinuxSyscallEmulator" />'s <see cref="ICheckpointableSyscallHandler" />
///     implementation — <c>WriteState</c>/<c>ReadState</c> round-tripping the emulator's mutable
///     host-side state (brk cursor, fd table, stdin position, deterministic clock/PRNG cursors)
///     into a fresh instance, independent of any ELF/Train. This is what SimPoint checkpoint-and-measure
///     sampling (<c>Experiment.CaptureSimPointCheckpoints</c>/<c>MeasureSimPointCheckpoints</c>) relies
///     on to measure a simulation point whose window includes a syscall correctly — see
///     <c>RealLinkedSimPointTests</c> for the ELF-driven integration coverage of the startup phase.
/// </summary>
public class SyscallCheckpointTests {
    private const ulong AtFdcwd = 0xFFFF_FF9CUL; // -100 two's complement

    private static void WriteCString(FlatMemory memory, ulong addr, string s) =>
        memory.Load(addr, Encoding.UTF8.GetBytes(s + "\0"));

    /// <summary>Same calling convention as <see cref="SyscallRealismTests" />'s helper of the same name.</summary>
    private static long Call(LinuxSyscallEmulator handler, ulong num, IMemory memory, params ulong[] args) {
        var state = new Rv32ArchState();
        for (var i = 0; i < args.Length; i++) state.IntegerRegisters.Write(10 + i, args[i]);
        ExecuteResult result = handler.Handle(num, state, memory, 0, 0);
        result.SideEffect?.Invoke(state);
        return Sext32(state.IntegerRegisters.Read(10));
    }

    private static long Sext32(ulong v) => unchecked((int)(uint)v);

    private static byte[] Snapshot(LinuxSyscallEmulator handler) {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true)) { handler.WriteState(bw); }

        return ms.ToArray();
    }

    private static void Restore(LinuxSyscallEmulator handler, byte[] state) {
        using var ms = new MemoryStream(state);
        using var br = new BinaryReader(ms);
        handler.ReadState(br);
    }

    private static byte[] ReadBytes(FlatMemory memory, ulong addr, int count) {
        var buf = new byte[count];
        for (var i = 0; i < count; i++) buf[i] = (byte)memory.Read(addr + (ulong)i, 1);
        return buf;
    }

    [Fact]
    public void Brk_AfterRestore_ContinuesFromCheckpointedValue() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);

        long advanced = Call(handler, 214, memory, 0x8000_2000UL); // SYS_brk, extend
        Assert.Equal(Sext32(0x8000_2000UL), advanced);

        byte[] state = Snapshot(handler);

        // Fresh instance constructed exactly as it would be for a new detailed-measurement pass —
        // ReadState must override its own initialBreak-derived _brk, not merely leave it alone.
        var restored = new LinuxSyscallEmulator(0x8000_0000UL);
        Restore(restored, state);

        // brk(0) queries the current break without moving it.
        Assert.Equal(Sext32(0x8000_2000UL), Call(restored, 214, memory, 0));
    }

    [Fact]
    public void OpenFile_AfterRestore_ReopensAtCheckpointedPosition() {
        string path = Path.Combine(Path.GetTempPath(), $"horologium_test_{Guid.NewGuid():N}.bin");
        try {
            var memory = new FlatMemory(0x10000, 0x8000_0000UL);
            var handler = new LinuxSyscallEmulator(0x8000_0000UL);
            const ulong pathAddr = 0x8000_1000UL;
            const ulong bufAddr = 0x8000_2000UL;
            WriteCString(memory, pathAddr, path);

            // O_RDWR|O_CREAT|O_TRUNC = 2 | 0x40 | 0x200
            long fd = Call(handler, 56, memory, SyscallCheckpointTests.AtFdcwd, pathAddr, 0x242, 0x1A4);
            Assert.True(fd >= 3);

            byte[] payload = "abcdefgh"u8.ToArray();
            memory.Load(bufAddr, payload);
            Assert.Equal(payload.Length, Call(handler, 64, memory, (ulong)fd, bufAddr, (ulong)payload.Length));

            // Checkpoint right after the write, fd at position 8. The capture-pass handler owns this
            // FileStream for the rest of its own run — dispose it here so the restored instance below
            // gets a clean, independent handle, matching how a detailed-measurement pass never shares
            // a mechanism/handler instance with the capture pass that produced its checkpoint.
            byte[] state = Snapshot(handler);
            handler.Dispose();

            var restored = new LinuxSyscallEmulator(0x8000_0000UL);
            Restore(restored, state);

            byte[] more = "IJ"u8.ToArray();
            memory.Load(bufAddr, more);
            Assert.Equal(2, Call(restored, 64, memory, (ulong)fd, bufAddr, 2));
            Assert.Equal(0, Call(restored, 57, memory, (ulong)fd));

            Assert.Equal("abcdefghIJ", File.ReadAllText(path));
        }
        finally {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Stdin_AfterRestore_ContinuesFromCheckpointedPosition_SeekableStream() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        byte[] content = "abcdefgh"u8.ToArray();
        using var stdin1 = new MemoryStream(content);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL, input: stdin1);
        const ulong bufAddr = 0x8000_0100UL;

        Assert.Equal(3, Call(handler, 63, memory, 0, bufAddr, 3)); // consumes "abc"

        byte[] state = Snapshot(handler);

        // Restore contract: a fresh stream over the same content from position 0 — ReadState itself
        // fast-forwards to the checkpointed byte position (here, via Seek since this stream is seekable).
        using var stdin2 = new MemoryStream(content);
        var restored = new LinuxSyscallEmulator(0x8000_0000UL, input: stdin2);
        Restore(restored, state);

        long n = Call(restored, 63, memory, 0, bufAddr, 5);
        Assert.Equal(5, n);
        Assert.Equal("defgh"u8.ToArray(), ReadBytes(memory, bufAddr, 5));
    }

    [Fact]
    public void Stdin_AfterRestore_ContinuesFromCheckpointedPosition_NonSeekableStream() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        byte[] content = "abcdefgh"u8.ToArray();
        using var stdin1 = new ForwardOnlyStream(content);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL, input: stdin1);
        const ulong bufAddr = 0x8000_0100UL;

        Assert.Equal(3, Call(handler, 63, memory, 0, bufAddr, 3)); // consumes "abc"

        byte[] state = Snapshot(handler);

        // Non-seekable: ReadState must fast-forward by discarding bytes instead of Seek.
        using var stdin2 = new ForwardOnlyStream(content);
        var restored = new LinuxSyscallEmulator(0x8000_0000UL, input: stdin2);
        Restore(restored, state);

        long n = Call(restored, 63, memory, 0, bufAddr, 5);
        Assert.Equal(5, n);
        Assert.Equal("defgh"u8.ToArray(), ReadBytes(memory, bufAddr, 5));
    }

    [Fact]
    public void ClockAndRandom_AfterRestore_ContinueTheOriginalHandlersSequence() {
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL);
        const ulong randBuf = 0x8000_0100UL;
        const ulong tsBuf = 0x8000_0200UL;

        Call(handler, 278, memory, randBuf, 16, 0); // getrandom
        Call(handler, 113, memory, 0, tsBuf);       // clock_gettime
        byte[] firstRand = ReadBytes(memory, randBuf, 16);
        byte[] firstTs = ReadBytes(memory, tsBuf, 16);

        byte[] state = Snapshot(handler);

        // Independent reference: keep stepping the *original* handler forward — its next
        // getrandom/clock_gettime outputs are the ground truth the restored instance must match.
        Call(handler, 278, memory, randBuf, 16, 0);
        Call(handler, 113, memory, 0, tsBuf);
        byte[] continuedRand = ReadBytes(memory, randBuf, 16);
        byte[] continuedTs = ReadBytes(memory, tsBuf, 16);

        var restored = new LinuxSyscallEmulator(0x8000_0000UL);
        Restore(restored, state);
        Call(restored, 278, memory, randBuf, 16, 0);
        Call(restored, 113, memory, 0, tsBuf);
        byte[] restoredRand = ReadBytes(memory, randBuf, 16);
        byte[] restoredTs = ReadBytes(memory, tsBuf, 16);

        Assert.Equal(continuedRand, restoredRand);
        Assert.Equal(continuedTs, restoredTs);
        // Sanity: this genuinely tests continuation, not two handlers coincidentally agreeing —
        // a fresh, never-advanced handler would reproduce the *first* call's bytes instead.
        Assert.NotEqual(firstRand, restoredRand);
        Assert.NotEqual(firstTs, restoredTs);
    }

    [Fact]
    public void ChildCleartid_AfterRestore_StillClearsTheRecordedAddressOnThatHartsExit() {
        // Multi-hart checkpoint/restore's own correctness depends on this: WriteState/ReadState must
        // round-trip clone()'s per-hart CLONE_CHILD_CLEARTID bookkeeping, or a restored checkpoint
        // reproduces the exact __thread_list_lock hang class PthreadProbeTests originally found
        // (a later thread's exit fails to clear/wake the lock it should).
        var memory = new FlatMemory(0x1000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL) { Spawner = new FakeSpawner(1), };

        const ulong ctidAddr = 0x8000_0100UL;
        memory.Write(ctidAddr, 0xDEADBEEF, 4); // sentinel — must become exactly 0 after the hart exits

        const ulong cloneChildCleartid = 0x0020_0000;
        Call(handler, 220, memory, cloneChildCleartid, 0, 0, 0, ctidAddr); // SYS_clone -> spawns hart 1

        byte[] state = Snapshot(handler);

        var restored = new LinuxSyscallEmulator(0x8000_0000UL) { Spawner = new FakeSpawner(1), };
        Restore(restored, state);

        // Hart 1 (the spawned hart) exits — SYS_exit, hartId=1 (the same id clone() assigned above).
        var exitingHartState = new Rv32ArchState();
        ExecuteResult result = restored.Handle(93, exitingHartState, memory, 0, 1);

        Assert.True(result.RequestHalt);
        Assert.Equal(0UL, memory.Read(ctidAddr, 4)); // cleared -> _childCleartid[1] survived the round trip
    }

    private sealed class FakeSpawner(int nextHartId) : IHartSpawner {
        public int SpawnHart(IArchState initialState) => nextHartId;
    }

    // Wraps a byte buffer as a strictly forward-only, non-seekable Stream — exercises
    // LinuxSyscallEmulator's discard-bytes stdin fast-forward path instead of Seek.
    private sealed class ForwardOnlyStream(byte[] data) : Stream {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            int n = Math.Min(count, data.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}