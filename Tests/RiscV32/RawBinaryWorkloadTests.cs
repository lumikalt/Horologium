using Mechanism;
using Orrery.Cache;
using RiscV32;
using RiscV32.Memory;
using Pipeline;

namespace Tests.RiscV32;

public class RawBinaryWorkloadTests {
    private const ulong RamBase = 0x80000000UL;

    // ── RawBinaryWorkload ─────────────────────────────────────────────────────

    [Fact]
    public void EntryPoint_DefaultsToBaseAddress() {
        var w = new RawBinaryWorkload([1, 2, 3,], RawBinaryWorkloadTests.RamBase);
        Assert.Equal(RawBinaryWorkloadTests.RamBase, w.EntryPoint);
    }

    [Fact]
    public void EntryPoint_CustomOverride() {
        var w = new RawBinaryWorkload(
            [1, 2, 3,], RawBinaryWorkloadTests.RamBase, RawBinaryWorkloadTests.RamBase + 0x20
        );
        Assert.Equal(RawBinaryWorkloadTests.RamBase + 0x20, w.EntryPoint);
    }

    [Fact]
    public void BaseAddress_IsPreserved() {
        var w = new RawBinaryWorkload([0xAA,], RawBinaryWorkloadTests.RamBase);
        Assert.Equal(RawBinaryWorkloadTests.RamBase, w.BaseAddress);
    }

    [Fact]
    public void DtbAddress_ZeroWhenNoDtb() {
        var w = new RawBinaryWorkload([0x00,], RawBinaryWorkloadTests.RamBase);
        Assert.Equal(0UL, w.DtbAddress);
    }

    [Fact]
    public void DtbAddress_AlignedBeyondBinary() {
        // binary = 100 bytes; aligned = round(0x80000064 up to 4KB) + 4KB guard
        var w = new RawBinaryWorkload(new byte[100], RawBinaryWorkloadTests.RamBase, dtb: new byte[16]);
        Assert.True(w.DtbAddress > RawBinaryWorkloadTests.RamBase + 100);
        Assert.Equal(0UL, w.DtbAddress & 0xFFFUL); // 4 KiB aligned
    }

    [Fact]
    public void DtbAddress_CustomOverride() {
        var w = new RawBinaryWorkload(
            [0x00,], RawBinaryWorkloadTests.RamBase, dtb: new byte[16],
            dtbAddress: RawBinaryWorkloadTests.RamBase + 0x10000
        );
        Assert.Equal(RawBinaryWorkloadTests.RamBase + 0x10000, w.DtbAddress);
    }

    [Fact]
    public void MemorySize_AtLeastBinary() {
        var w = new RawBinaryWorkload(new byte[4096], RawBinaryWorkloadTests.RamBase);
        Assert.True(w.MemorySize >= 4096);
    }

    [Fact]
    public void MemorySize_CoversNoBinaryAndDtb() {
        var dtb = new byte[128];
        var w = new RawBinaryWorkload(new byte[4096], RawBinaryWorkloadTests.RamBase, dtb: dtb);
        // DtbAddress + 128 must be within [BaseAddress, BaseAddress + MemorySize)
        Assert.True(w.DtbAddress + (ulong)dtb.Length <= RawBinaryWorkloadTests.RamBase + (ulong)w.MemorySize);
    }

    [Fact]
    public void Load_WritesBinaryAtBaseAddress() {
        byte[] binary = [0x11, 0x22, 0x33, 0x44,];
        var w = new RawBinaryWorkload(binary, RawBinaryWorkloadTests.RamBase);
        var mem = new FlatMemory(w.MemorySize, RawBinaryWorkloadTests.RamBase);
        w.Load(mem);
        Assert.Equal(0x44332211UL, mem.Read(RawBinaryWorkloadTests.RamBase, 4));
    }

    [Fact]
    public void Load_WritesDtbAtDtbAddress() {
        byte[] binary = [0xAA, 0xBB,];
        byte[] dtb = [0xD0, 0x0D, 0xFE, 0xED,]; // FDT magic
        var w = new RawBinaryWorkload(
            binary, RawBinaryWorkloadTests.RamBase, dtb: dtb, dtbAddress: RawBinaryWorkloadTests.RamBase + 0x10000
        );
        var mem = new FlatMemory(w.MemorySize, RawBinaryWorkloadTests.RamBase);
        w.Load(mem);
        // DTB magic is big-endian; memory.Read returns little-endian word — check bytes individually
        Assert.Equal((ulong)0xD0, mem.Read(RawBinaryWorkloadTests.RamBase + 0x10000, 1));
        Assert.Equal((ulong)0x0D, mem.Read(RawBinaryWorkloadTests.RamBase + 0x10001, 1));
    }

    // ── VirtDtb ───────────────────────────────────────────────────────────────

    [Fact]
    public void VirtDtb_Loads_NonEmpty() { Assert.NotEmpty(VirtDtb.Bytes); }

    [Fact]
    public void VirtDtb_HasFdtMagic() {
        byte[] dtb = VirtDtb.Bytes;
        // FDT magic = 0xD00DFEED in big-endian at offset 0
        Assert.Equal(0xD0, dtb[0]);
        Assert.Equal(0x0D, dtb[1]);
        Assert.Equal(0xFE, dtb[2]);
        Assert.Equal(0xED, dtb[3]);
    }

    [Fact]
    public void VirtDtb_SameInstanceReturnedOnSubsequentCalls() { Assert.Same(VirtDtb.Bytes, VirtDtb.Bytes); }

    // ── Integration: RawBinaryWorkload + VirtDtb in a SingleCycleTrain ────────

    [Fact]
    public void Registers_A0_A1_SurviveWind_And_Are_Readable() {
        // Minimal program: read a0 into t0, ebreak
        //   mv t0, a0   →  addi t0, a0, 0  =  (0 << 20)|(10 << 15)|(0 << 12)|(5 << 7)|0x13 = 0x00050293
        //   ebreak           = 0x00100073
        uint[] instrs = [0x00050293u, 0x00100073u,];
        var prog = new byte[8];
        Buffer.BlockCopy(instrs, 0, prog, 0, 8);

        ulong dtbAddr = RawBinaryWorkloadTests.RamBase + 0x10000;
        var workload = new RawBinaryWorkload(
            prog, RawBinaryWorkloadTests.RamBase, dtb: VirtDtb.Bytes, dtbAddress: dtbAddr
        );
        var mem = new FlatMemory(workload.MemorySize, RawBinaryWorkloadTests.RamBase);
        workload.Load(mem);

        var mechanism = new Rv32Mechanism();
        var train = new SingleCycleTrain(mechanism, mem, workload.EntryPoint);

        // Set boot registers (a0 = hartid = 0, a1 = DTB addr)
        train.ArchState.IntegerRegisters.Write(10, 0);
        train.ArchState.IntegerRegisters.Write(11, dtbAddr);

        train.Run(100);

        // a0 was copied to t0 (x5) before ebreak
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(5));      // t0 = a0 = 0
        Assert.Equal(dtbAddr, train.ArchState.IntegerRegisters.Read(11)); // a1 untouched
    }
}