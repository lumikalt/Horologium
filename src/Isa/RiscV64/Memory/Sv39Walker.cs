using Mechanism;
using RiscV32;

namespace RiscV64.Memory;

/// <summary>
///     Sv39 three-level page table walker for RV64.
///     Reference: RISC-V Privileged Specification §4.4.
///     A/D bits are not set on access (fault-on-access model): A=0 or (store and D=0) raises a page fault.
///     SUM (sstatus bit 18): when set, S-mode data accesses to U-pages are permitted; instruction fetches
///     are never subject to SUM — S-mode can never execute from U-pages regardless.
///     satp.MODE occupies bits 63:60 (0 = Bare, 8 = Sv39); only Bare and Sv39 are recognised here.
/// </summary>
internal static class Sv39Walker {
    private const ulong PageSize = 4096;
    private const ulong PteV = 1UL << 0;
    private const ulong PteR = 1UL << 1;
    private const ulong PteW = 1UL << 2;
    private const ulong PteX = 1UL << 3;
    private const ulong PteU = 1UL << 4;
    private const ulong PteA = 1UL << 6;
    private const ulong PteD = 1UL << 7;
    private const ulong Satp44BitPpnMask = 0xFFFFFFFFFFFUL; // satp.PPN[43:0]

    /// <summary>
    ///     Translates a virtual address to physical via a three-level Sv39 walk.
    ///     Returns (paddr, 0) on success, (0, faultCause) on page fault.
    ///     When satp.MODE=0 (bare), returns (vaddr, 0) immediately with no walk.
    /// </summary>
    public static (ulong paddr, int faultCause) Translate(
        IMemory memory,
        ulong satp,
        ulong vaddr,
        bool isWrite,
        bool isExec,
        PrivilegeLevel privilege,
        bool sum = false
    ) {
        if (satp >> 60 == 0) return (vaddr, 0); // Bare mode — no translation

        int fault = FaultCause(isWrite, isExec);

        // Sv39 VA[63:39] must equal VA[38] (sign extension of the 39-bit address space).
        var signExtended = (ulong)((long)(vaddr << 25) >> 25);
        if (signExtended != vaddr) return (0, fault);

        var pl = (uint)privilege;
        bool umode = pl == 0;
        bool smode = pl == 1;

        ulong rootPpn = satp & Sv39Walker.Satp44BitPpnMask;
        var vpn2 = (uint)((vaddr >> 30) & 0x1FF);
        var vpn1 = (uint)((vaddr >> 21) & 0x1FF);
        var vpn0 = (uint)((vaddr >> 12) & 0x1FF);
        ulong pgOff = vaddr & 0xFFF;

        ulong ppn = rootPpn;
        ulong pte = 0;
        var level = 2;
        for (; level >= 0; level--) {
            uint vpn = level switch { 2 => vpn2, 1 => vpn1, _ => vpn0, };
            ulong pteAddr = ppn * Sv39Walker.PageSize + vpn * 8UL;
            pte = memory.Read(pteAddr, 8);
            if (!PteValid(pte)) return (0, fault);

            bool isLeaf = (pte & (Sv39Walker.PteR | Sv39Walker.PteX)) != 0;
            if (isLeaf) break;
            if (level == 0) return (0, fault); // Non-leaf at the lowest level

            ppn = pte >> 10;
        }

        // Check page permissions
        bool pteU = (pte & Sv39Walker.PteU) != 0;
        if (umode && !pteU) return (0, fault);                    // U-mode accessing kernel page
        if (smode && pteU && (!sum || isExec)) return (0, fault); // S-mode/U-page: deny unless SUM=1 and data access

        if (isExec && (pte & Sv39Walker.PteX) == 0) return (0, fault);
        if (isWrite && (pte & Sv39Walker.PteW) == 0) return (0, fault);
        if (!isWrite && !isExec && (pte & Sv39Walker.PteR) == 0) return (0, fault);

        // A/D bit check (fault-on-access model)
        if ((pte & Sv39Walker.PteA) == 0) return (0, fault);
        if (isWrite && (pte & Sv39Walker.PteD) == 0) return (0, fault);

        ulong ppnFull = pte >> 10; // PPN[2]:PPN[1]:PPN[0], 26+9+9 = 44 bits
        ulong ppn2 = (ppnFull >> 18) & 0x3FFFFFF;
        ulong ppn1 = (ppnFull >> 9) & 0x1FF;
        ulong ppn0 = ppnFull & 0x1FF;

        // Superpage misalignment: any PPN field below the leaf's level must be zero in the PTE.
        if (level >= 1 && ppn0 != 0) return (0, fault);
        if (level == 2 && ppn1 != 0) return (0, fault);

        ulong paPpn1 = level <= 1 ? ppn1 : vpn1;
        ulong paPpn0 = level == 0 ? ppn0 : vpn0;
        ulong paddr = (ppn2 << 30) | (paPpn1 << 21) | (paPpn0 << 12) | pgOff;

        return (paddr, 0);
    }

    private static bool PteValid(ulong pte) =>
        (pte & Sv39Walker.PteV) != 0 && ((pte & Sv39Walker.PteR) != 0 || (pte & Sv39Walker.PteW) == 0);

    private static int FaultCause(bool isWrite, bool isExec) =>
        isExec  ? RvTrapCause.InstructionPageFault :
        isWrite ? RvTrapCause.StorePageFault :
                  RvTrapCause.LoadPageFault;
}