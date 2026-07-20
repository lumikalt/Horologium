#region

using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Syscalls;

#endregion

namespace Tests.RiscV32.System;

/// <summary>
///     Tests for the two new syscall-execution paths:
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>HTIF proxy</b> — <see cref="HtifMemory" /> decodes the fesvr magic-mem
///                 struct written to tohost and services SYS_write instead of auto-ACK-ing.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>SE mode</b> — <see cref="LinuxSyscallEmulator" /> intercepts ECALL
///                 instructions so bare-metal RISC-V programs can call Linux syscalls
///                 without a kernel.
///             </description>
///         </item>
///     </list>
/// </summary>
public class SyscallEmulationTests {
    private static string SeHelloElf => Path.Combine(AppContext.BaseDirectory, "se_hello.elf");

    // ── HTIF syscall proxy ────────────────────────────────────────────────────

    /// <summary>
    ///     Constructs a HtifMemory over a flat backing store, writes a magic_mem
    ///     syscall struct (SYS_write, fd=1, buf, len=13) to a 64-byte-aligned
    ///     address, then writes that address to tohost.  Verifies the handler
    ///     executes the write and captures output.
    /// </summary>
    [Fact]
    public void HtifProxy_ServesWriteSyscall_OutputCaptured() {
        const ulong @base = 0x80000000UL;
        const ulong toHost = @base + 0x1000UL;   // page-aligned
        const ulong magicMem = @base + 0x2000UL; // 64-byte-aligned magic_mem block
        const ulong bufAddr = @base + 0x3000UL;

        var backing = new FlatMemory(0x10000, @base);
        var sw = new StringWriter();
        var htif = new HtifMemory(backing, toHost, sw);

        // Write the text into backing memory at BufAddr.
        const string text = "hello htif\n\0";
        for (var i = 0; i < text.Length; i++) backing.Write(bufAddr + (ulong)i, (byte)text[i], 1);

        // Build magic_mem: [0]=syscall(64), [1]=fd(1), [2]=bufPtr, [3]=len
        backing.Write(magicMem, 64, 4);           // magic_mem[0] = SYS_write
        backing.Write(magicMem + 8, 1, 4);        // magic_mem[1] = fd=1
        backing.Write(magicMem + 16, bufAddr, 4); // magic_mem[2] = buf
        backing.Write(magicMem + 24, 11, 4);      // magic_mem[3] = len=11

        // Trigger syscall: write even, non-zero pointer to tohost.
        htif.Write(toHost, magicMem, 4);

        Assert.Equal("hello htif\n", sw.ToString());
        // fromhost must have been ACK'd (== 1).
        Assert.Equal(1u, (uint)backing.Read(toHost + 8, 4));
        // Return value written back to magic_mem[0] (== 11, bytes written).
        Assert.Equal(11u, (uint)backing.Read(magicMem, 4));
    }

    [Fact]
    public void HtifProxy_UnknownSyscall_ReturnsEnosys() {
        const ulong @base = 0x80000000UL;
        const ulong toHost = @base + 0x1000UL;
        const ulong magicMem = @base + 0x2000UL;

        var backing = new FlatMemory(0x10000, @base);
        var htif = new HtifMemory(backing, toHost);

        backing.Write(magicMem, 9999UL, 4); // unknown syscall
        htif.Write(toHost, magicMem, 4);

        // ENOSYS = -38; stored as two's-complement uint in 4 bytes.
        var ret = (uint)backing.Read(magicMem, 4);
        Assert.Equal(unchecked((uint)-38L), ret);
        Assert.Equal(1u, (uint)backing.Read(toHost + 8, 4)); // still ACK'd
    }

    [Fact]
    public void HtifProxy_NullOutput_DiscardsWrite() {
        const ulong @base = 0x80000000UL;
        const ulong toHost = @base + 0x1000UL;
        const ulong magicMem = @base + 0x2000UL;
        const ulong bufAddr = @base + 0x3000UL;

        var backing = new FlatMemory(0x10000, @base);
        var htif = new HtifMemory(backing, toHost);

        backing.Write(bufAddr, (byte)'X', 1);
        backing.Write(magicMem, 64, 4);
        backing.Write(magicMem + 8, 1, 4);
        backing.Write(magicMem + 16, bufAddr, 4);
        backing.Write(magicMem + 24, 1, 4);
        htif.Write(toHost, magicMem, 4);

        // No exception; write still ACK'd and return value set.
        Assert.Equal(1u, (uint)backing.Read(toHost + 8, 4));
        Assert.Equal(1u, (uint)backing.Read(magicMem, 4));
    }

    // ── SE mode (ECALL interception) ─────────────────────────────────────────

    [Fact]
    public void SyscallEmulation_HelloWorld_OutputCaptured() {
        var workload = new Rv32ElfWorkload(SeHelloElf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        var sw = new StringWriter();
        var handler = new LinuxSyscallEmulator(workload.InitialBreak, sw);
        var mech = new Rv32Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, workload.EntryPoint);

        train.Run();
        Assert.Equal("Hello, SE mode!\n", sw.ToString());
    }

    [Fact]
    public void SyscallEmulation_ExitHalts() {
        var workload = new Rv32ElfWorkload(SeHelloElf);
        var mem = new FlatMemory(workload.MemorySize, workload.BaseAddress);
        workload.Load(mem);

        var handler = new LinuxSyscallEmulator(workload.InitialBreak);
        var mech = new Rv32Mechanism(syscallHandler: handler);
        RevolutionResult result = new SingleCycleTrain(mech, mem, workload.EntryPoint).Run();

        Assert.True(result.TotalTicks < 100_000, "SYS_exit should have halted before budget");
    }
}