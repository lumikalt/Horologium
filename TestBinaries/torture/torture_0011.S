// Automatically generated RV32 torture test — do not edit.
// Generator: TestBinaries/gen_torture.py
// Run under Spike co-simulation (TortureCoSimTests.cs); Spike is the oracle.

#include "riscv_test.h"

RVTEST_CODE_BEGIN
	la s0, _torture_data
	la t0, _torture_init
	lw t1, 0(t0)
	lw t2, 4(t0)
	lw t3, 8(t0)
	lw t4, 12(t0)
	lw t5, 16(t0)
	lw t6, 20(t0)
	lw a0, 24(t0)
	lw a1, 28(t0)
	bgeu x29, x28, .Lsk_0
	nop
.Lsk_0:
	lw x9, 192(s0)
	sb x7, 41(s0)
	mulhsu x21, x4, x14
	lhu x30, 236(s0)
	flw f10, 176(s0)
	flw f28, 44(s0)
	fmin.s f23, f10, f28
	fsw f23, 180(s0)
	fcvt.w.s x17, f10, rtz
	addi x17, x30, 1852
	add  x17, x17, x17
	slli x17, x17, 1
	addi x20, x19, -606
	add  x20, x20, x20
	slli x20, x20, 1
	auipc x5, 839085
	sb x28, 156(s0)
	andi x23, x4, -166
	add x19, x18, x18
	sb x12, 142(s0)
	bne x6, x23, .Lsk_1
	nop
.Lsk_1:
	remu x28, x5, x10
	lhu x20, 94(s0)
	flw f17, 32(s0)
	flw f10, 172(s0)
	fmul.s f19, f17, f10
	fsw f19, 68(s0)
	fcvt.w.s x18, f17, rtz
	sh x6, 30(s0)
	xor x23, x7, x13
	blt x21, x26, .Lsk_2
	nop
.Lsk_2:
	addi x10, x28, -1078
	add  x10, x10, x10
	slli x10, x10, 1
	bgeu x26, x11, .Lsk_3
	nop
.Lsk_3:
	ori x25, x29, 189
	bne x22, x19, .Lsk_4
	nop
.Lsk_4:
	flw f18, 208(s0)
	flw f29, 184(s0)
	fadd.s f21, f18, f29
	fsw f21, 252(s0)
	fcvt.w.s x14, f18, rtz
	andi x15, x7, 1728
	sb x4, 203(s0)
	sb x10, 38(s0)
	auipc x14, 468893
	sltu x30, x31, x27
	lh x29, 192(s0)
	sh x7, 72(s0)
	mulhsu x28, x30, x14
	sb x30, 181(s0)
	addi x11, x5, -1320
	add  x11, x11, x11
	slli x11, x11, 1
	rem x19, x26, x23
	addi x6, x4, -145
	sw x18, 16(s0)
	rem x22, x12, x14
	slli x11, x24, 21
	lb x26, 244(s0)
	flw f23, 24(s0)
	flw f1, 32(s0)
	fsub.s f19, f23, f1
	fsw f19, 212(s0)
	fcvt.w.s x29, f23, rtz
	addi x29, x21, -1445
	add  x29, x29, x29
	slli x29, x29, 1
	addi x23, x14, 1215
	add  x23, x23, x23
	slli x23, x23, 1
	addi x18, x22, -1633
	add  x18, x18, x18
	slli x18, x18, 1
	blt x18, x9, .Lsk_5
	nop
.Lsk_5:
	addi x21, x20, -819
	add  x21, x21, x21
	slli x21, x21, 1
	beq x9, x23, .Lsk_6
	nop
.Lsk_6:
	flw f22, 188(s0)
	flw f18, 228(s0)
	fsub.s f28, f22, f18
	fsw f28, 48(s0)
	fcvt.w.s x17, f22, rtz
	addi x5, x25, -372
	add  x5, x5, x5
	slli x5, x5, 1
	addi x10, x28, -2021
	add  x10, x10, x10
	slli x10, x10, 1
	addi x15, x5, 1965
	add  x15, x15, x15
	slli x15, x15, 1
	lhu x22, 204(s0)
	blt x29, x12, .Lsk_7
	nop
.Lsk_7:
	flw f3, 160(s0)
	flw f12, 16(s0)
	fmax.s f28, f3, f12
	fsw f28, 40(s0)
	fcvt.w.s x13, f3, rtz
	lbu x3, 198(s0)
	lbu x6, 201(s0)
	slli x4, x6, 5
	srli x24, x20, 12
	bge x21, x12, .Lsk_8
	nop
.Lsk_8:
	sw x22, 240(s0)
	sw x5, 232(s0)
	beq x28, x7, .Lsk_9
	nop
.Lsk_9:
	flw f1, 108(s0)
	flw f0, 120(s0)
	fmul.s f0, f1, f0
	fsw f0, 84(s0)
	fcvt.w.s x5, f1, rtz
	addi x30, x20, -14
	add  x30, x30, x30
	slli x30, x30, 1
	add x26, x4, x5
	blt x16, x19, .Lsk_10
	nop
.Lsk_10:
	sb x14, 96(s0)
	sh x19, 126(s0)
	remu x25, x20, x12
	lhu x19, 154(s0)
	lbu x27, 223(s0)
	lh x17, 30(s0)
	ori x12, x12, -471
	bge x28, x28, .Lsk_11
	nop
.Lsk_11:
	addi x31, x13, 1011
	add  x31, x31, x31
	slli x31, x31, 1
	sb x14, 109(s0)
	mul x11, x21, x11
	div x23, x16, x12
	bltu x28, x16, .Lsk_12
	nop
.Lsk_12:
	lw x3, 232(s0)
	xori x24, x20, 417
	ori x28, x19, -706
	addi x29, x7, -1916
	add  x29, x29, x29
	slli x29, x29, 1
	and x10, x10, x20
	bgeu x21, x14, .Lsk_13
	nop
.Lsk_13:
	lw x26, 252(s0)
	sb x16, 233(s0)
	sb x22, 100(s0)
	andi x24, x6, -3
	bge x17, x7, .Lsk_14
	nop
.Lsk_14:
	sh x29, 214(s0)
	sub x3, x21, x9
	bne x12, x26, .Lsk_15
	nop
.Lsk_15:
	sltiu x3, x9, -1556
	addi x10, x10, -1239
	add  x10, x10, x10
	slli x10, x10, 1
	bne x10, x25, .Lsk_16
	nop
.Lsk_16:
	sw x17, 196(s0)
	addi x4, x28, 1918
	add  x4, x4, x4
	slli x4, x4, 1
	auipc x3, 197968
	flw f4, 104(s0)
	flw f3, 244(s0)
	fmin.s f19, f4, f3
	fsw f19, 232(s0)
	fcvt.w.s x24, f4, rtz
	srl x21, x31, x15
	sw x7, 4(s0)
	blt x14, x27, .Lsk_17
	nop
.Lsk_17:
	addi x28, x23, 332
	add  x28, x28, x28
	slli x28, x28, 1
	lh x26, 106(s0)
	addi x10, x17, -1782
	add  x10, x10, x10
	slli x10, x10, 1
	and x7, x11, x5
	lb x21, 242(s0)
	flw f17, 8(s0)
	flw f5, 228(s0)
	fmax.s f18, f17, f5
	fsw f18, 96(s0)
	fcvt.w.s x20, f17, rtz
	sll x7, x4, x3
	flw f1, 60(s0)
	flw f7, 92(s0)
	fmax.s f10, f1, f7
	fsw f10, 224(s0)
	fcvt.w.s x23, f1, rtz
	bgeu x10, x31, .Lsk_18
	nop
.Lsk_18:
	addi x28, x21, 1967
	add  x28, x28, x28
	slli x28, x28, 1
	lui x9, 733690
	addi x4, x16, 1282
	add  x4, x4, x4
	slli x4, x4, 1
	xori x15, x4, 101
	lh x7, 122(s0)
	flw f18, 244(s0)
	flw f6, 252(s0)
	fmul.s f20, f18, f6
	fsw f20, 12(s0)
	fcvt.w.s x20, f18, rtz
	slt x19, x19, x18
	lui x20, 1030523
	addi x23, x27, -731
	add  x23, x23, x23
	slli x23, x23, 1
	srli x17, x30, 13
	addi x29, x12, 383
	add  x29, x29, x29
	slli x29, x29, 1
	lw x19, 32(s0)
	lui x23, 47275
	bge x25, x6, .Lsk_19
	nop
.Lsk_19:
	or x23, x30, x20
	sw x21, 120(s0)
	addi x9, x5, 1393
	add  x9, x9, x9
	slli x9, x9, 1
	bltu x3, x30, .Lsk_20
	nop
.Lsk_20:
	addi x26, x10, -1012
	add  x26, x26, x26
	slli x26, x26, 1
	sltu x15, x11, x6
	auipc x17, 471562
	sw x19, 104(s0)
	sw x23, 72(s0)
	sltu x16, x5, x13
	addi x14, x31, 630
	add  x14, x14, x14
	slli x14, x14, 1
	addi x5, x22, 1439
	add  x5, x5, x5
	slli x5, x5, 1
	srai x31, x4, 11
	flw f16, 192(s0)
	flw f20, 112(s0)
	fadd.s f25, f16, f20
	fsw f25, 204(s0)
	fcvt.w.s x21, f16, rtz
	bgeu x5, x3, .Lsk_21
	nop
.Lsk_21:
	flw f30, 76(s0)
	flw f9, 188(s0)
	fmax.s f9, f30, f9
	fsw f9, 136(s0)
	fcvt.w.s x6, f30, rtz
	srli x18, x6, 22
	sltiu x21, x27, -2040
	lb x14, 221(s0)
	addi x13, x30, 1145
	sltiu x27, x17, -1499
	lh x3, 150(s0)
	lui x17, 144023
	ori x18, x9, 977
	sb x5, 81(s0)
	RVTEST_PASS

RVTEST_CODE_END
RVTEST_DATA_BEGIN
RVTEST_DATA_END
.section .data
.align 4
_torture_init:
	.word 0x12345678
	.word 0xABCDEF01
	.word 0xDEADBEEF
	.word 0x00000000
	.word 0x7FFFFFFF
	.word 0x80000000
	.word 0x00000001
	.word 0xFFFFFFFF
_torture_data:
	.word 0x13818B68
	.word 0xCAE43B5D
	.word 0xFFF236D6
	.word 0x9CA9AAD6
	.word 0x4A34B023
	.word 0x9CCE08C7
	.word 0x3E20D46A
	.word 0xC8143CC4
	.word 0x1B7DBD55
	.word 0xE6169FEE
	.word 0xEA99A306
	.word 0x17CB872E
	.word 0x319DFDA1
	.word 0x9636C744
	.word 0x8533B5E3
	.word 0xD2B7A6BD
	.word 0x745BCDF8
	.word 0xF7988024
	.word 0xEDAAE058
	.word 0xF6766E17
	.word 0x7EB02C2E
	.word 0x7747FCCB
	.word 0x06CC115E
	.word 0xC0EC8225
	.word 0xF5904A30
	.word 0x092F55DE
	.word 0x794523A2
	.word 0x038858C0
	.word 0x957BD9D6
	.word 0xFC15EFB7
	.word 0x93EE5FDC
	.word 0x33BF25A7
	.word 0xF1748F68
	.word 0x4FCC0537
	.word 0x0E6D05E2
	.word 0xEF38BEE2
	.word 0xCF166813
	.word 0xC0D20BD5
	.word 0x9B51F464
	.word 0x1D6CD7E4
	.word 0x4C044C20
	.word 0xDBD4E7D0
	.word 0xB3AA386D
	.word 0x49FDA435
	.word 0xB315719E
	.word 0x8B61A805
	.word 0xE5210762
	.word 0x6D33648A
	.word 0xF3280FC3
	.word 0x52906E94
	.word 0xD419382E
	.word 0xE897C344
	.word 0x0E3C9EF1
	.word 0x69901E40
	.word 0x8FCE6F15
	.word 0x1B238BD0
	.word 0x5E4B7AC5
	.word 0xD219784E
	.word 0xD9DA1A20
	.word 0x1C52EE82
	.word 0x8581BEB3
	.word 0x44B011CA
	.word 0xCAE56871
	.word 0xCFC357A0
