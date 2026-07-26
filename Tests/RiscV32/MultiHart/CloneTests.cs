#region

using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.Syscalls;

#endregion

namespace Tests.RiscV32.MultiHart;

/// <summary>
///     End-to-end proof of <c>clone()</c> (syscall 220) + <see cref="MultiHartKernel" />'s dynamic
///     hart activation, using a hand-assembled RV32I program that invokes the raw syscall directly
///     (no musl/pthread_create involved) with arguments empirically confirmed against the actual
///     compiled musl 1.2.5 <c>__clone</c> (<c>riscv64-unknown-linux-musl-gcc</c>, disassembled):
///     <c>a0</c>=flags, <c>a1</c>=newsp, <c>a2</c>=ptid, <c>a3</c>=tls, <c>a4</c>=ctid.
///     <para>
///         Encoded instructions:
///         lui a0, 0x180         = 0x00180537  (flags = CLONE_SETTLS|CLONE_PARENT_SETTID)
///         addi a1, x0, 0x300    = 0x30000593  (newsp)
///         addi a2, x0, 0x104    = 0x10400613  (ptid address)
///         lui a3, 0x123         = 0x001236B7  (tls)
///         addi a4, x0, 0        = 0x00000713  (ctid, unused)
///         addi a7, x0, 220      = 0x0DC00893  (SYS_clone)
///         ecall                 = 0x00000073
///         bne a0, x0, +0x20     = 0x0A051063  (parent skips the child-only block)
///         addi t0, tp, 0        = 0x00020293  (child: t0 = tp)
///         sw t0, 0x108(x0)      = 0x10502423  (child: store tp)
///         addi t1, x2, 0        = 0x00010313  (child: t1 = sp)
///         sw t1, 0x10C(x0)      = 0x10602623  (child: store sp)
///         addi t2, x0, 777      = 0x30900393  (child: marker)
///         sw t2, 0x110(x0)      = 0x11002023  (child: store marker)
///         ebreak                = 0x00100073  (child halts)
///         sw a0, 0x100(x0)      = 0x10A02023  (parent: store clone()'s return value)
///         ebreak                = 0x00100073  (parent halts)
///     </para>
/// </summary>
public class CloneTests {
    private const ulong ParentResultAddr = 0x100;
    private const ulong PtidAddr = 0x104;
    private const ulong ChildTpAddr = 0x108;
    private const ulong ChildSpAddr = 0x10C;
    private const ulong ChildMarkerAddr = 0x110;
    private const ulong ChildGettidAddr = 0x114;
    private const uint Ecall = 0x0000_0073;
    private const uint Ebreak = 0x0010_0073;

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Lui(int rd, int imm20) => (uint)(((imm20 & 0xFFFFF) << 12) | (rd << 7) | 0b0110111);

    private static uint Sw(int rs2, int rs1, int imm) {
        var immU = (uint)imm & 0xFFF;
        uint imm11_5 = (immU >> 5) & 0x7F;
        uint imm4_0 = immU & 0x1F;
        return (imm11_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b010u << 12) | (imm4_0 << 7) | 0b0100011u;
    }

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static void Load(FlatMemory mem, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    private static FlatMemory BuildProgram() {
        var mem = new FlatMemory(0x400);
        const int cloneSetTls = 0x0008_0000;
        const int cloneParentSetTid = 0x0010_0000;
        Load(
            mem,
            Lui(10, (cloneSetTls | cloneParentSetTid) >> 12), // 0x00: a0 = flags
            Addi(11, 0, 0x300),                               // 0x04: a1 = newsp
            Addi(12, 0, (int)CloneTests.PtidAddr),            // 0x08: a2 = ptid
            Lui(13, 0x123),                                   // 0x0C: a3 = tls
            Addi(14, 0, 0),                                   // 0x10: a4 = ctid (unused)
            Addi(17, 0, 220),                                 // 0x14: a7 = SYS_clone
            CloneTests.Ecall,                                 // 0x18
            Bne(10, 0, 0x2C),                                 // 0x1C: parent (a0!=0) skips ahead to 0x48
            Addi(5, 4, 0),                                    // 0x20: child: t0 = tp
            Sw(5, 0, (int)CloneTests.ChildTpAddr),            // 0x24
            Addi(6, 2, 0),                                    // 0x28: child: t1 = sp
            Sw(6, 0, (int)CloneTests.ChildSpAddr),            // 0x2C
            Addi(7, 0, 777),                                  // 0x30: child: marker
            Sw(7, 0, (int)CloneTests.ChildMarkerAddr),        // 0x34
            Addi(17, 0, 178),                                 // 0x38: child: a7 = SYS_gettid
            CloneTests.Ecall,                                 // 0x3C
            Sw(10, 0, (int)CloneTests.ChildGettidAddr),       // 0x40: child: store gettid()'s return
            CloneTests.Ebreak,                                // 0x44: child halts
            Sw(10, 0, (int)CloneTests.ParentResultAddr),      // 0x48: parent: store clone()'s return
            CloneTests.Ebreak                                 // 0x4C: parent halts
        );
        return mem;
    }

    [Fact]
    public void Clone_SpawnsDormantHart_WithCorrectSpAndTp() {
        FlatMemory mem = CloneTests.BuildProgram();
        var handler = new LinuxSyscallEmulator(0x400);
        var mech0 = new Rv32Mechanism(syscallHandler: handler, hartId: 0);
        // hartId must match the dormant slot this mechanism will be spawned into (1) — clone()'s
        // returned tid (hartId + 1, from the MultiHartKernel slot index) and a later gettid() call
        // from the spawned hart (hartId + 1, from Rv32Executor.HartId) are two independently
        // computed values that only agree if the caller keeps mechanism.hartId == slot index;
        // MultiHartKernel does not enforce this itself.
        var mech1 = new Rv32Mechanism(syscallHandler: handler, hartId: 1);
        var kernel = new MultiHartKernel(mem, 1, mech0, mech1);
        handler.Spawner = kernel;
        kernel.SetEntryPoint(0, 0x00);

        Assert.True(kernel.IsDormant(1));

        kernel.Run(200);

        Assert.False(kernel.IsDormant(1));
        // clone()'s return value is a tid (hartId + 1, never 0), not the raw 0-based MultiHartKernel
        // slot index — the new hart landed in slot 1, so its tid is 2.
        Assert.Equal(2UL, mem.Read(CloneTests.ParentResultAddr, 4)); // parent saw the new hart's tid
        Assert.Equal(2UL, mem.Read(CloneTests.PtidAddr, 4));         // CLONE_PARENT_SETTID wrote the same tid
        Assert.Equal(0x1230_00UL, mem.Read(CloneTests.ChildTpAddr, 4));  // tls (a3) became the child's tp
        Assert.Equal(0x300UL, mem.Read(CloneTests.ChildSpAddr, 4));      // newsp (a1) became the child's sp
        Assert.Equal(777UL, mem.Read(CloneTests.ChildMarkerAddr, 4));    // child actually executed
        // The decisive consistency check: the child's own gettid() must reproduce the exact tid
        // clone() handed the parent — proving the two independently-computed values actually agree
        // for this setup, not just individually looking plausible.
        Assert.Equal(2UL, mem.Read(CloneTests.ChildGettidAddr, 4));
    }

    [Fact]
    public void Clone_WithNoSpawnerWired_ReturnsENoSys() {
        // Single-hart mode (no MultiHartKernel involved at all) must be unaffected by clone()'s
        // existence — it should behave exactly like any other unimplemented syscall.
        FlatMemory mem = CloneTests.BuildProgram();
        var handler = new LinuxSyscallEmulator(0x400);
        var mech = new Rv32Mechanism(syscallHandler: handler);
        var train = new SingleCycleTrain(mech, mem, 0x00);

        train.Run(200);

        // ENOSYS (-38) sign-extends to 0xFFFFFFDA in the 32-bit result the program stored.
        Assert.Equal(0xFFFF_FFDAUL, mem.Read(CloneTests.ParentResultAddr, 4));
    }
}
