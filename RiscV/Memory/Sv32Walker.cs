using Mechanism;
using RiscV.Registers;

namespace RiscV.Memory;

/// <summary>
/// Sv32 two-level page table walker for RV32.
/// Reference: RISC-V Privileged Specification §4.3.
/// A/D bits are not set on access (fault-on-access model): A=0 or (store and D=0) raises a page fault.
/// SUM (permit Supervisor access to User pages) is not implemented; S-mode always faults on U-pages.
/// </summary>
internal static class Sv32Walker {
    private const uint PageSize = 4096;
    private const uint PteV = 1u << 0;
    private const uint PteR = 1u << 1;
    private const uint PteW = 1u << 2;
    private const uint PteX = 1u << 3;
    private const uint PteU = 1u << 4;
    private const uint PteA = 1u << 6;
    private const uint PteD = 1u << 7;

    /// <summary>
    /// Translates a virtual address to physical via a two-level Sv32 walk.
    /// Returns (paddr, 0) on success, (0, faultCause) on page fault.
    /// When satp.MODE=0 (bare), returns (vaddr, 0) immediately with no walk.
    /// </summary>
    public static (ulong paddr, int faultCause) Translate(
        IMemory memory,
        uint satp,
        ulong vaddr,
        bool isWrite,
        bool isExec,
        PrivilegeLevel privilege
    ) {
        if ((satp >> 31) == 0) return (vaddr, 0); // Bare mode — no translation

        int fault = FaultCause(isWrite, isExec);
        uint pl = (uint)privilege;
        bool umode = pl == 0;
        bool smode = pl == 1;

        uint rootPpn = satp & 0x003FFFFF;
        uint vpn1    = (uint)((vaddr >> 22) & 0x3FF);
        uint vpn0    = (uint)((vaddr >> 12) & 0x3FF);
        uint pgOff   = (uint)(vaddr & 0xFFF);

        // Level 1 lookup: root PT at rootPpn × 4096
        ulong pteAddr = (ulong)rootPpn * PageSize + vpn1 * 4UL;
        uint pte = (uint)memory.Read(pteAddr, 4);
        if (!PteValid(pte)) return (0, fault);

        bool level1Leaf = (pte & (PteR | PteX)) != 0;
        if (!level1Leaf) {
            // Pointer PTE — descend to level 0
            uint ppn  = pte >> 10;
            pteAddr   = (ulong)ppn * PageSize + vpn0 * 4UL;
            pte       = (uint)memory.Read(pteAddr, 4);
            if (!PteValid(pte)) return (0, fault);
            if ((pte & (PteR | PteX)) == 0) return (0, fault); // Non-leaf at level 0
        }

        // Check page permissions
        bool pteU = (pte & PteU) != 0;
        if (umode && !pteU) return (0, fault);  // U-mode accessing kernel page
        if (smode &&  pteU) return (0, fault);  // S-mode accessing user page (no SUM)

        if (isExec  && (pte & PteX) == 0) return (0, fault);
        if (isWrite && (pte & PteW) == 0) return (0, fault);
        if (!isWrite && !isExec && (pte & PteR) == 0) return (0, fault);

        // A/D bit check (fault-on-access model)
        if ((pte & PteA) == 0) return (0, fault);
        if (isWrite && (pte & PteD) == 0) return (0, fault);

        // Superpage misalignment: level-1 leaf requires PPN[0] field (bits 19:10 of PTE) == 0
        if (level1Leaf && ((pte >> 10) & 0x3FF) != 0) return (0, fault);

        // Compute physical address
        uint ppnFull = pte >> 10;
        ulong paddr;
        if (level1Leaf) {
            // 4 MiB superpage: PA.PPN[1] from PTE, PA.PPN[0] from VA.VPN[0]
            uint ppn1 = ppnFull >> 10; // 12-bit field PPN[1]
            paddr = ((ulong)ppn1 << 22) | ((ulong)vpn0 << 12) | pgOff;
        } else {
            // 4 KiB page
            paddr = ((ulong)ppnFull << 12) | pgOff;
        }

        return (paddr, 0);
    }

    private static bool PteValid(uint pte) =>
        (pte & PteV) != 0 && ((pte & PteR) != 0 || (pte & PteW) == 0);

    private static int FaultCause(bool isWrite, bool isExec) =>
        isExec  ? RvTrapCause.InstructionPageFault :
        isWrite ? RvTrapCause.StorePageFault :
                  RvTrapCause.LoadPageFault;
}
