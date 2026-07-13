using Pipeline;
using RiscV32.Memory;
using RiscV64;

namespace Tests.RiscV64.System;

/// <summary>
///     RV64 counterpart of <see cref="Tests.RiscV32.System.RawBinaryWorkloadTests" />.
///     <c>RawBinaryWorkload</c> and <c>VirtDtb</c> are already ISA-agnostic and fully
///     covered by the RV32 suite — this file only exercises the one case that needs a
///     live mechanism, deliberately with a DTB address above 4 GiB (a case the RV32
///     version, whose address space tops out under 4 GiB, could never exercise).
/// </summary>
public class RawBinaryWorkloadTests {
    // Base address itself sits above 4 GiB — a1 must carry a full 64-bit DTB
    // pointer, unlike the RV32 version whose entire address space tops out
    // under 4 GiB.
    private const ulong RamBase = 0x1_0000_0000UL;

    [Fact]
    public void Registers_A0_A1_SurviveWind_And_Are_Readable() {
        // Minimal program: read a0 into t0, ebreak
        //   mv t0, a0   →  addi t0, a0, 0  =  (0 << 20)|(10 << 15)|(0 << 12)|(5 << 7)|0x13 = 0x00050293
        //   ebreak           = 0x00100073
        uint[] instrs = [0x00050293u, 0x00100073u,];
        var prog = new byte[8];
        Buffer.BlockCopy(instrs, 0, prog, 0, 8);

        var workload = new RawBinaryWorkload(
            prog, RawBinaryWorkloadTests.RamBase, dtb: VirtDtb.Bytes
        );
        ulong dtbAddr = workload.DtbAddress;
        var mem = new FlatMemory(workload.MemorySize, RawBinaryWorkloadTests.RamBase);
        workload.Load(mem);

        var mechanism = new Rv64Mechanism();
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