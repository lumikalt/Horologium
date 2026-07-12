// Automatically generated RV64 torture test — do not edit.
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
	addiw x19, x23, -733
	sh x30, 6(s0)
	sd x27, 136(s0)
	auipc x27, 625504
	addi x13, x14, 861
	addi x12, x6, 157
	add  x12, x12, x12
	slli x12, x12, 1
	sltiu x16, x6, -1592
	sb x29, 115(s0)
	fld f18, 104(s0)
	fld f13, 144(s0)
	fadd.d f9, f18, f13
	fsd f9, 0(s0)
	fcvt.l.d x15, f18, rtz
	bgeu x31, x24, .Lsk_0
	nop
.Lsk_0:
	flw f12, 240(s0)
	flw f11, 120(s0)
	fsub.s f5, f12, f11
	fsw f5, 200(s0)
	fcvt.w.s x30, f12, rtz
	addiw x31, x20, 1817
	slli x25, x3, 38
	addi x9, x27, -1066
	add  x9, x9, x9
	slli x9, x9, 1
	beq x24, x15, .Lsk_1
	nop
.Lsk_1:
	addi x9, x16, -1369
	add  x9, x9, x9
	slli x9, x9, 1
	div x4, x6, x17
	and x28, x27, x10
	srli x11, x18, 27
	sllw x17, x9, x10
	lb x11, 156(s0)
	beq x19, x27, .Lsk_2
	nop
.Lsk_2:
	sb x15, 155(s0)
	addi x11, x27, 1168
	add  x11, x11, x11
	slli x11, x11, 1
	sh x18, 116(s0)
	lb x25, 79(s0)
	addi x20, x23, -1275
	add  x20, x20, x20
	slli x20, x20, 1
	sh x11, 26(s0)
	remu x14, x16, x10
	lhu x31, 180(s0)
	lhu x10, 106(s0)
	sllw x13, x10, x17
	addi x24, x12, 1177
	add  x24, x24, x24
	slli x24, x24, 1
	ld x25, 72(s0)
	slti x19, x21, 1945
	sraiw x31, x24, 23
	addi x7, x18, -570
	add  x7, x7, x7
	slli x7, x7, 1
	addiw x14, x30, 1411
	bge x29, x14, .Lsk_3
	nop
.Lsk_3:
	add x26, x19, x15
	addi x15, x15, -1388
	add  x15, x15, x15
	slli x15, x15, 1
	fld f11, 8(s0)
	fld f31, 216(s0)
	fmax.d f26, f11, f31
	fsd f26, 152(s0)
	fcvt.l.d x30, f11, rtz
	ori x23, x10, 674
	addi x28, x23, 506
	add  x28, x28, x28
	slli x28, x28, 1
	addi x5, x4, -785
	add  x5, x5, x5
	slli x5, x5, 1
	sb x6, 23(s0)
	sw x25, 96(s0)
	sd x19, 72(s0)
	flw f18, 164(s0)
	flw f12, 244(s0)
	fmax.s f5, f18, f12
	fsw f5, 200(s0)
	fcvt.w.s x20, f18, rtz
	slti x6, x24, -1219
	lbu x7, 197(s0)
	sw x30, 88(s0)
	sd x22, 72(s0)
	andi x16, x7, 125
	ld x4, 64(s0)
	lw x29, 8(s0)
	sd x5, 112(s0)
	sra x16, x13, x23
	addi x3, x23, -600
	add  x3, x3, x3
	slli x3, x3, 1
	bge x13, x27, .Lsk_4
	nop
.Lsk_4:
	addi x9, x23, 1133
	add  x9, x9, x9
	slli x9, x9, 1
	lbu x28, 29(s0)
	andi x30, x14, -1273
	flw f19, 184(s0)
	flw f21, 252(s0)
	fmin.s f3, f19, f21
	fsw f3, 156(s0)
	fcvt.w.s x7, f19, rtz
	addi x20, x24, 329
	add  x20, x20, x20
	slli x20, x20, 1
	bgeu x13, x14, .Lsk_5
	nop
.Lsk_5:
	sraw x15, x15, x25
	and x23, x15, x24
	srli x15, x10, 15
	addi x7, x27, -489
	add  x7, x7, x7
	slli x7, x7, 1
	lhu x13, 18(s0)
	lhu x27, 140(s0)
	slti x3, x27, -695
	auipc x20, 548232
	addi x25, x31, -283
	add  x25, x25, x25
	slli x25, x25, 1
	srli x14, x26, 3
	lhu x30, 10(s0)
	addi x26, x9, 603
	add  x26, x26, x26
	slli x26, x26, 1
	addw x13, x22, x24
	lwu x12, 184(s0)
	flw f0, 160(s0)
	flw f18, 192(s0)
	fmax.s f19, f0, f18
	fsw f19, 248(s0)
	fcvt.w.s x21, f0, rtz
	addi x5, x6, 1525
	add  x5, x5, x5
	slli x5, x5, 1
	lwu x9, 164(s0)
	lwu x13, 208(s0)
	flw f13, 128(s0)
	flw f3, 84(s0)
	fmax.s f2, f13, f3
	fsw f2, 124(s0)
	fcvt.w.s x21, f13, rtz
	lbu x28, 243(s0)
	flw f31, 36(s0)
	flw f13, 0(s0)
	fsub.s f10, f31, f13
	fsw f10, 124(s0)
	fcvt.w.s x29, f31, rtz
	sh x21, 14(s0)
	mul x11, x14, x19
	lb x12, 209(s0)
	lwu x3, 80(s0)
	sraw x22, x12, x15
	srlw x29, x17, x22
	srlw x26, x23, x30
	sraiw x19, x21, 18
	sh x28, 220(s0)
	bltu x19, x31, .Lsk_6
	nop
.Lsk_6:
	bne x11, x19, .Lsk_7
	nop
.Lsk_7:
	srai x4, x14, 51
	bne x10, x21, .Lsk_8
	nop
.Lsk_8:
	sll x15, x23, x7
	srai x14, x16, 29
	sllw x4, x20, x21
	lh x11, 120(s0)
	add x15, x30, x28
	bge x21, x20, .Lsk_9
	nop
.Lsk_9:
	slt x3, x22, x7
	sra x27, x15, x25
	mulh x3, x9, x12
	flw f6, 152(s0)
	flw f3, 20(s0)
	fadd.s f4, f6, f3
	fsw f4, 252(s0)
	fcvt.w.s x26, f6, rtz
	sll x6, x30, x31
	sd x6, 128(s0)
	auipc x26, 883874
	subw x5, x30, x13
	addiw x14, x24, -652
	mulh x5, x21, x22
	sll x11, x27, x22
	sraw x9, x23, x30
	bltu x17, x28, .Lsk_10
	nop
.Lsk_10:
	mulhu x10, x6, x23
	lw x15, 28(s0)
	remw x4, x17, x22
	or x21, x10, x4
	addi x30, x11, -1862
	add  x30, x30, x30
	slli x30, x30, 1
	beq x25, x21, .Lsk_11
	nop
.Lsk_11:
	sraw x19, x15, x5
	addi x29, x20, -1494
	add  x29, x29, x29
	slli x29, x29, 1
	addi x12, x22, 1993
	add  x12, x12, x12
	slli x12, x12, 1
	fld f9, 224(s0)
	fld f17, 200(s0)
	fadd.d f0, f9, f17
	fsd f0, 176(s0)
	fcvt.l.d x24, f9, rtz
	sh x31, 228(s0)
	sllw x13, x30, x12
	and x25, x11, x18
	bne x25, x17, .Lsk_12
	nop
.Lsk_12:
	slti x24, x22, -1564
	and x10, x26, x4
	sb x17, 206(s0)
	fld f9, 248(s0)
	fld f26, 64(s0)
	fmul.d f1, f9, f26
	fsd f1, 104(s0)
	fcvt.l.d x19, f9, rtz
	lhu x24, 142(s0)
	addi x21, x5, 33
	add  x21, x21, x21
	slli x21, x21, 1
	fld f15, 112(s0)
	fld f26, 232(s0)
	fadd.d f27, f15, f26
	fsd f27, 104(s0)
	fcvt.l.d x9, f15, rtz
	addiw x16, x30, -1736
	addi x3, x7, 1243
	add  x3, x3, x3
	slli x3, x3, 1
	sw x4, 92(s0)
	srl x17, x9, x27
	flw f6, 120(s0)
	flw f18, 108(s0)
	fsub.s f0, f6, f18
	fsw f0, 168(s0)
	fcvt.w.s x15, f6, rtz
	lhu x29, 118(s0)
	sb x21, 246(s0)
	sraw x4, x13, x26
	flw f27, 56(s0)
	flw f28, 156(s0)
	fmin.s f6, f27, f28
	fsw f6, 244(s0)
	fcvt.w.s x4, f27, rtz
	sb x6, 34(s0)
	flw f24, 40(s0)
	flw f0, 164(s0)
	fmul.s f24, f24, f0
	fsw f24, 112(s0)
	fcvt.w.s x10, f24, rtz
	srliw x12, x3, 19
	sw x7, 176(s0)
	lh x16, 252(s0)
	addi x4, x13, -1073
	add  x4, x4, x4
	slli x4, x4, 1
	lbu x29, 1(s0)
	auipc x22, 539031
	sw x16, 44(s0)
	blt x27, x31, .Lsk_13
	nop
.Lsk_13:
	flw f0, 228(s0)
	flw f3, 148(s0)
	fsub.s f4, f0, f3
	fsw f4, 28(s0)
	fcvt.w.s x12, f0, rtz
	sllw x13, x19, x15
	lbu x21, 200(s0)
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
	.word 0xC6845A16
	.word 0x80BC0207
	.word 0x41730EF5
	.word 0xF3AC082D
	.word 0x3D22C902
	.word 0x50778504
	.word 0xD5E70E46
	.word 0xC4642375
	.word 0x1CA97D35
	.word 0x7FAD07D6
	.word 0x706FDD61
	.word 0xC3F9A9DD
	.word 0x7B3ACA23
	.word 0x9DD3F14E
	.word 0xCAAE40EA
	.word 0xA327DF3E
	.word 0xC0AF3AE2
	.word 0x235552E7
	.word 0x914276CB
	.word 0xA47E76AB
	.word 0x76319584
	.word 0x87B58A64
	.word 0x1921BAC4
	.word 0x9C3809F3
	.word 0xEE21966B
	.word 0xDD96C4EC
	.word 0x923B2A0E
	.word 0xC1547F3C
	.word 0xEE4BF275
	.word 0x22B0E62A
	.word 0x642BD303
	.word 0x45AB3A20
	.word 0xD7CFA36E
	.word 0xA9078ADD
	.word 0x72F0C2FC
	.word 0xC761D786
	.word 0x8DA0C29B
	.word 0x2191364F
	.word 0xD37004A8
	.word 0x3FB8472E
	.word 0x71F071B9
	.word 0xBC4E1054
	.word 0x49E743E7
	.word 0xEE8439FA
	.word 0x4F1C58E6
	.word 0xFE269DB0
	.word 0x4D515471
	.word 0x33F158D8
	.word 0xB320D1EB
	.word 0x9A93C8BC
	.word 0xFFA6C17D
	.word 0xEBACAA89
	.word 0x5C0C0EFC
	.word 0x6EB07D86
	.word 0x713DFDC1
	.word 0xACB283D8
	.word 0xEDBEC0B3
	.word 0x771ADA36
	.word 0x61F30715
	.word 0x9FCAC108
	.word 0xAA57618A
	.word 0x0E15DBDC
	.word 0x53CF31A5
	.word 0x853837B6
