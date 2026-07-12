#!/usr/bin/env python3
"""
RV32/RV64 torture test generator for Horologium co-simulation.

Generates random RV32IMAF / RV64IMAFD instruction sequences that exercise
diverse instruction combinations, hazard patterns, and data-dependent
control flow. Tests are compiled with riscv32-none-elf-gcc /
riscv64-none-elf-gcc and run under Spike lock-step co-simulation
(TortureCoSimTests.cs, both RiscV32/CoSim and RiscV64/CoSim); Spike is the
correctness oracle — no self-check is embedded.

Register conventions (preserved across the test body):
    s0 (x8)  — scratch data base pointer  (read-only after init)
    sp (x2)  — stack pointer               (untouched)
    x0        — always zero                (untouched)
    ra (x1)  — return address              (untouched)

All other registers are fair game for random reads and writes.

With --xlen 64: adds W-suffix ops (addw/subw/sllw/srlw/sraw, addiw/slliw/
srliw/sraiw, mulw/divw/divuw/remw/remuw), LWU/LD/SD, and D-extension FP
blocks (fld/fsd/fadd.d/.../fcvt.l.d) alongside the RV32-shared instruction
mix. --xlen 32 (the default) reproduces the original generator's RNG call
sequence exactly — regenerating the existing seed-42 torture/ ELFs must
still be reproducible byte-for-byte.

Usage:
    python3 gen_torture.py <count> <output_dir> [--seed N] [--insts N] [--xlen {32,64}]
"""
import argparse
import os
import random

# ---------------------------------------------------------------------------
# Instruction tables
# ---------------------------------------------------------------------------

# R-type: rd, rs1, rs2
R_INT = [
    "add", "sub", "sll", "slt", "sltu", "xor", "srl", "sra", "or", "and",
]
R_MUL = [
    "mul", "mulh", "mulhsu", "mulhu", "div", "divu", "rem", "remu",
]

# I-type: rd, rs1, imm12
I_INT = ["addi", "slti", "sltiu", "xori", "ori", "andi"]
I_SHF = ["slli", "srli", "srai"]

# Branches: rs1, rs2, label
BRANCHES = ["beq", "bne", "blt", "bge", "bltu", "bgeu"]

# Loads/stores — widths in bytes
LOADS  = [("lw",  4), ("lh",  2), ("lb",  1), ("lhu", 2), ("lbu", 1)]
STORES = [("sw",  4), ("sh",  2), ("sb",  1)]

# Floating-point (RV32F) arithmetic
F_ARITH = ["fadd.s", "fsub.s", "fmul.s", "fmin.s", "fmax.s"]

# ── RV64-only additions ──────────────────────────────────────────────────────

# W-suffix 32-bit-result R-type/I-type ops (RV64I/RV64M)
RW_INT  = ["addw", "subw", "sllw", "srlw", "sraw"]
RW_MUL  = ["mulw", "divw", "divuw", "remw", "remuw"]
IW_SHF  = ["slliw", "srliw", "sraiw"]

# RV64-only loads/stores — widths in bytes
LOADS64  = [("lwu", 4), ("ld", 8)]
STORES64 = [("sd", 8)]

# Double-precision (RV64D) arithmetic
D_ARITH = ["fadd.d", "fsub.d", "fmul.d", "fmin.d", "fmax.d"]

# Scratch area words (must fit in 16-bit signed offset from s0)
MEM_WORDS = 64
MEM_BYTES = MEM_WORDS * 4    # 256 bytes

# Registers available for random use
# Protected: x0, x1 (ra), x2 (sp), x8 (s0 = data ptr)
XREGS_ALL  = [r for r in range(3, 32) if r not in (8,)]   # x3..x31 \ {x8}
XREGS_DEST = XREGS_ALL   # same pool for destinations
FREGS      = list(range(32))   # f0..f31

def xn(r):  return f"x{r}"
def fn(r):  return f"f{r}"

# ---------------------------------------------------------------------------
# Generator
# ---------------------------------------------------------------------------

class TortureGen:
    def __init__(self, rng, mem_words=MEM_WORDS, xlen=32):
        self.rng       = rng
        self.mem_words = mem_words
        self.xlen      = xlen

    def rand_imm12(self):
        return self.rng.randint(-2048, 2047)

    def rand_shamt(self):
        return self.rng.randint(0, 31)

    # 6-bit shamt for RV64 native-width (non-W) shifts
    def rand_shamt64(self):
        return self.rng.randint(0, 63)

    def rand_xreg(self, pool=None):
        return xn(self.rng.choice(pool or XREGS_ALL))

    def rand_xdest(self):
        return xn(self.rng.choice(XREGS_DEST))

    def rand_freg(self):
        return fn(self.rng.choice(FREGS))

    # Aligned offset within scratch area
    def rand_offset(self, width):
        max_word = self.mem_words - (width // 4 or 1)
        step = max(1, width // 4)
        return self.rng.randint(0, max_word) * 4 + (0 if width >= 4 else 0)

    def safe_offset(self, width):
        # Byte offset, aligned to width, within [0, MEM_BYTES - width]
        max_off = MEM_BYTES - width
        steps   = max_off // width
        return self.rng.randint(0, steps) * width

    # ── Emission helpers ──────────────────────────────────────────────────

    def emit_alu(self, lines):
        if self.xlen == 64:
            return self.emit_alu64(lines)

        rd  = self.rand_xdest()
        rs1 = self.rand_xreg()
        r   = self.rng.random()

        if r < 0.28:
            op  = self.rng.choice(R_INT)
            rs2 = self.rand_xreg()
            lines.append(f"\t{op} {rd}, {rs1}, {rs2}")

        elif r < 0.48:
            op  = self.rng.choice(R_MUL)
            rs2 = self.rand_xreg()
            lines.append(f"\t{op} {rd}, {rs1}, {rs2}")

        elif r < 0.63:
            op  = self.rng.choice(I_SHF)
            imm = self.rand_shamt()
            lines.append(f"\t{op} {rd}, {rs1}, {imm}")

        elif r < 0.80:
            op  = self.rng.choice(I_INT)
            imm = self.rand_imm12()
            lines.append(f"\t{op} {rd}, {rs1}, {imm}")

        else:
            op  = self.rng.choice(["lui", "auipc"])
            imm = self.rng.randint(0, (1 << 20) - 1)
            lines.append(f"\t{op} {rd}, {imm}")

        return 1

    # RV64: same base-ISA mix as emit_alu, plus W-suffix 32-bit-result ops
    # (addw/subw/sllw/srlw/sraw, mulw/divw/divuw/remw/remuw, slliw/srliw/
    # sraiw, addiw) and 6-bit shamts for the native-width shifts.
    def emit_alu64(self, lines):
        rd  = self.rand_xdest()
        rs1 = self.rand_xreg()
        r   = self.rng.random()

        if r < 0.20:
            op  = self.rng.choice(R_INT)
            rs2 = self.rand_xreg()
            lines.append(f"\t{op} {rd}, {rs1}, {rs2}")

        elif r < 0.34:
            op  = self.rng.choice(R_MUL)
            rs2 = self.rand_xreg()
            lines.append(f"\t{op} {rd}, {rs1}, {rs2}")

        elif r < 0.44:
            op  = self.rng.choice(RW_INT)
            rs2 = self.rand_xreg()
            lines.append(f"\t{op} {rd}, {rs1}, {rs2}")

        elif r < 0.52:
            op  = self.rng.choice(RW_MUL)
            rs2 = self.rand_xreg()
            lines.append(f"\t{op} {rd}, {rs1}, {rs2}")

        elif r < 0.62:
            op  = self.rng.choice(I_SHF)
            imm = self.rand_shamt64()
            lines.append(f"\t{op} {rd}, {rs1}, {imm}")

        elif r < 0.70:
            op  = self.rng.choice(IW_SHF)
            imm = self.rand_shamt()
            lines.append(f"\t{op} {rd}, {rs1}, {imm}")

        elif r < 0.84:
            op  = self.rng.choice(I_INT)
            imm = self.rand_imm12()
            lines.append(f"\t{op} {rd}, {rs1}, {imm}")

        elif r < 0.90:
            imm = self.rand_imm12()
            lines.append(f"\taddiw {rd}, {rs1}, {imm}")

        else:
            op  = self.rng.choice(["lui", "auipc"])
            imm = self.rng.randint(0, (1 << 20) - 1)
            lines.append(f"\t{op} {rd}, {imm}")

        return 1

    def emit_load(self, lines):
        pool = LOADS + LOADS64 if self.xlen == 64 else LOADS
        op, width = self.rng.choice(pool)
        rd  = self.rand_xdest()
        off = self.safe_offset(width)
        lines.append(f"\t{op} {rd}, {off}(s0)")
        return 1

    def emit_store(self, lines):
        pool = STORES + STORES64 if self.xlen == 64 else STORES
        op, width = self.rng.choice(pool)
        rs2 = self.rand_xreg()
        off = self.safe_offset(width)
        lines.append(f"\t{op} {rs2}, {off}(s0)")
        return 1

    # Forward branch over one NOP — no register state clobbered, safe
    def emit_branch(self, lines, label_id):
        op  = self.rng.choice(BRANCHES)
        rs1 = self.rand_xreg()
        rs2 = self.rand_xreg()
        lbl = f".Lsk_{label_id}"
        lines += [
            f"\t{op} {rs1}, {rs2}, {lbl}",
            f"\tnop",
            f"{lbl}:",
        ]
        return 3

    # Two FP loads → arithmetic → FP store + int conversion
    def emit_fp_block(self, lines):
        if self.xlen == 64:
            return self.emit_fp_block64(lines)

        fs1 = self.rand_freg()
        fs2 = self.rand_freg()
        fd  = self.rand_freg()
        rd  = self.rand_xdest()
        off1 = self.safe_offset(4)
        off2 = self.safe_offset(4)
        off3 = self.safe_offset(4)
        op   = self.rng.choice(F_ARITH)
        lines += [
            f"\tflw {fs1}, {off1}(s0)",
            f"\tflw {fs2}, {off2}(s0)",
            f"\t{op} {fd}, {fs1}, {fs2}",
            f"\tfsw {fd}, {off3}(s0)",
            f"\tfcvt.w.s {rd}, {fs1}, rtz",
        ]
        return 5

    # RV64: 50/50 single- vs double-precision block; the double block uses
    # fld/fsd, D_ARITH, and fcvt.l.d (64-bit int conversion) in place of
    # the RV32 block's fcvt.w.s.
    def emit_fp_block64(self, lines):
        fs1 = self.rand_freg()
        fs2 = self.rand_freg()
        fd  = self.rand_freg()
        rd  = self.rand_xdest()

        if self.rng.random() < 0.5:
            off1 = self.safe_offset(4)
            off2 = self.safe_offset(4)
            off3 = self.safe_offset(4)
            op   = self.rng.choice(F_ARITH)
            lines += [
                f"\tflw {fs1}, {off1}(s0)",
                f"\tflw {fs2}, {off2}(s0)",
                f"\t{op} {fd}, {fs1}, {fs2}",
                f"\tfsw {fd}, {off3}(s0)",
                f"\tfcvt.w.s {rd}, {fs1}, rtz",
            ]
        else:
            off1 = self.safe_offset(8)
            off2 = self.safe_offset(8)
            off3 = self.safe_offset(8)
            op   = self.rng.choice(D_ARITH)
            lines += [
                f"\tfld {fs1}, {off1}(s0)",
                f"\tfld {fs2}, {off2}(s0)",
                f"\t{op} {fd}, {fs1}, {fs2}",
                f"\tfsd {fd}, {off3}(s0)",
                f"\tfcvt.l.d {rd}, {fs1}, rtz",
            ]
        return 5

    # Back-to-back RAW dependency chain
    def emit_raw_chain(self, lines):
        rd  = self.rand_xdest()
        rs  = self.rand_xreg()
        imm = self.rand_imm12()
        lines += [
            f"\taddi {rd}, {rs}, {imm}",
            f"\tadd  {rd}, {rd}, {rd}",
            f"\tslli {rd}, {rd}, 1",
        ]
        return 3

    # ── Full test body ────────────────────────────────────────────────────

    def body(self, n_target=300):
        lines     = []
        bid       = 0
        emitted   = 0

        # Set up the scratch pointer s0 = &_torture_data
        lines.append("\tla s0, _torture_data")

        # Seed registers from the init table (via a temporary pointer in t0)
        lines.append("\tla t0, _torture_init")
        for i, reg in enumerate(["t1", "t2", "t3", "t4", "t5", "t6", "a0", "a1"]):
            lines.append(f"\tlw {reg}, {i*4}(t0)")

        while emitted < n_target:
            r = self.rng.random()
            if   r < 0.38:  emitted += self.emit_alu(lines)
            elif r < 0.53:  emitted += self.emit_load(lines)
            elif r < 0.63:  emitted += self.emit_store(lines)
            elif r < 0.74:
                emitted += self.emit_branch(lines, bid)
                bid += 1
            elif r < 0.84:  emitted += self.emit_fp_block(lines)
            else:           emitted += self.emit_raw_chain(lines)

        return lines

    def data_section(self):
        inits = [
            0x12345678, 0xABCDEF01, 0xDEADBEEF, 0x00000000,
            0x7FFFFFFF, 0x80000000, 0x00000001, 0xFFFFFFFF,
        ]
        lines = [
            ".section .data",
            ".align 4",
            "_torture_init:",
        ]
        for v in inits:
            lines.append(f"\t.word 0x{v:08X}")

        lines.append("_torture_data:")
        for _ in range(self.mem_words):
            v = self.rng.randint(0, 0xFFFFFFFF)
            lines.append(f"\t.word 0x{v:08X}")
        return lines


# ---------------------------------------------------------------------------
# Assembly template
# ---------------------------------------------------------------------------

def preamble(xlen):
    return f"""\
// Automatically generated RV{xlen} torture test — do not edit.
// Generator: TestBinaries/gen_torture.py
// Run under Spike co-simulation (TortureCoSimTests.cs); Spike is the oracle.

#include "riscv_test.h"

RVTEST_CODE_BEGIN
"""


POSTAMBLE = """\

\tRVTEST_PASS

RVTEST_CODE_END
RVTEST_DATA_BEGIN
RVTEST_DATA_END
"""


def make_test(gen, n_insts):
    body  = gen.body(n_insts)
    data  = gen.data_section()
    return (preamble(gen.xlen)
            + "\n".join(body)
            + POSTAMBLE
            + "\n".join(data)
            + "\n")


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("count",      type=int, help="Number of tests to generate")
    ap.add_argument("output_dir",           help="Output directory for .S files")
    ap.add_argument("--seed",  type=int, default=42, help="Master PRNG seed")
    ap.add_argument("--insts", type=int, default=300, help="Instructions per test")
    ap.add_argument("--xlen",  type=int, default=32, choices=(32, 64),
                     help="RV32 or RV64 instruction mix (default: 32)")
    args = ap.parse_args()

    os.makedirs(args.output_dir, exist_ok=True)

    master = random.Random(args.seed)
    for i in range(1, args.count + 1):
        rng = random.Random(master.randint(0, 2**32 - 1))
        gen = TortureGen(rng, xlen=args.xlen)
        src = make_test(gen, args.insts)
        path = os.path.join(args.output_dir, f"torture_{i:04d}.S")
        with open(path, "w") as f:
            f.write(src)
        print(f"  {path}")

    print(f"Generated {args.count} test(s) in '{args.output_dir}'.")


if __name__ == "__main__":
    main()
