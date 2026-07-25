namespace RiscV32.Decode;

// ── Zvkned extension (Vector AES Block Cipher, RISC-V Cryptography Extensions Volume II) ──────
// OPMVV (opcode=0x57, funct3=2), funct6=0x28 (.vv form), vs1 field repurposed as a fixed
// sub-opcode selector (2=middle-round encrypt, 3=final-round encrypt) rather than a register.
// EGW=128, EGS=4, EEW=SEW=32 — each element group is one full 128-bit AES block, and (since
// VLEN=128 in this codebase) always occupies exactly one physical vector register regardless of
// LMUL. vd is both the round-state source and the new-round-state destination.
public record RvVaesEmVv(int Vd, int Vs2) : RvOp; // vaesem.vv: SubBytes+ShiftRows+MixColumns+ARK

public record RvVaesEfVv(int Vd, int Vs2) : RvOp; // vaesef.vv: SubBytes+ShiftRows+ARK (final round)