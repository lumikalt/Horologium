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
	lb x7, 178(s0)
	sll x12, x3, x15
	slli x9, x14, 31
	bge x19, x26, .Lsk_0
	nop
.Lsk_0:
	addi x10, x14, 1866
	add  x10, x10, x10
	slli x10, x10, 1
	flw f27, 148(s0)
	flw f0, 136(s0)
	fadd.s f3, f27, f0
	fsw f3, 216(s0)
	fcvt.w.s x14, f27, rtz
	lui x17, 302919
	sltiu x18, x16, 226
	bge x22, x30, .Lsk_1
	nop
.Lsk_1:
	auipc x23, 512038
	rem x7, x4, x11
	slti x18, x10, -491
	lb x25, 59(s0)
	lbu x6, 77(s0)
	addi x14, x3, 1084
	add  x14, x14, x14
	slli x14, x14, 1
	sw x28, 152(s0)
	blt x4, x3, .Lsk_2
	nop
.Lsk_2:
	or x23, x19, x16
	addi x10, x26, -961
	add  x10, x10, x10
	slli x10, x10, 1
	flw f24, 244(s0)
	flw f27, 4(s0)
	fmul.s f10, f24, f27
	fsw f10, 128(s0)
	fcvt.w.s x7, f24, rtz
	lh x4, 52(s0)
	flw f2, 168(s0)
	flw f4, 36(s0)
	fmax.s f4, f2, f4
	fsw f4, 8(s0)
	fcvt.w.s x14, f2, rtz
	addi x14, x7, -630
	add  x14, x14, x14
	slli x14, x14, 1
	addi x19, x6, 852
	add  x19, x19, x19
	slli x19, x19, 1
	beq x21, x26, .Lsk_3
	nop
.Lsk_3:
	auipc x15, 264023
	bge x22, x5, .Lsk_4
	nop
.Lsk_4:
	addi x26, x22, -821
	add  x26, x26, x26
	slli x26, x26, 1
	flw f16, 192(s0)
	flw f17, 4(s0)
	fsub.s f0, f16, f17
	fsw f0, 84(s0)
	fcvt.w.s x23, f16, rtz
	flw f11, 224(s0)
	flw f10, 244(s0)
	fmul.s f10, f11, f10
	fsw f10, 52(s0)
	fcvt.w.s x24, f11, rtz
	ori x23, x23, -1383
	lh x26, 74(s0)
	flw f21, 228(s0)
	flw f23, 64(s0)
	fadd.s f3, f21, f23
	fsw f3, 116(s0)
	fcvt.w.s x15, f21, rtz
	lui x18, 240208
	sub x14, x15, x19
	addi x21, x5, 1065
	add  x21, x21, x21
	slli x21, x21, 1
	sh x13, 82(s0)
	flw f8, 76(s0)
	flw f18, 8(s0)
	fmax.s f30, f8, f18
	fsw f30, 148(s0)
	fcvt.w.s x13, f8, rtz
	slli x16, x6, 21
	addi x5, x31, -27
	add  x5, x5, x5
	slli x5, x5, 1
	flw f10, 48(s0)
	flw f14, 80(s0)
	fsub.s f15, f10, f14
	fsw f15, 92(s0)
	fcvt.w.s x26, f10, rtz
	flw f7, 228(s0)
	flw f20, 228(s0)
	fmax.s f27, f7, f20
	fsw f27, 128(s0)
	fcvt.w.s x27, f7, rtz
	lhu x9, 186(s0)
	srli x12, x25, 27
	bne x12, x26, .Lsk_5
	nop
.Lsk_5:
	lhu x30, 248(s0)
	sb x9, 193(s0)
	flw f22, 140(s0)
	flw f20, 164(s0)
	fmin.s f30, f22, f20
	fsw f30, 88(s0)
	fcvt.w.s x3, f22, rtz
	bne x20, x25, .Lsk_6
	nop
.Lsk_6:
	srli x23, x3, 21
	lbu x17, 200(s0)
	xori x18, x4, 327
	add x15, x13, x25
	flw f29, 40(s0)
	flw f16, 4(s0)
	fmul.s f24, f29, f16
	fsw f24, 12(s0)
	fcvt.w.s x31, f29, rtz
	flw f2, 124(s0)
	flw f5, 156(s0)
	fmul.s f31, f2, f5
	fsw f31, 12(s0)
	fcvt.w.s x14, f2, rtz
	addi x5, x25, -1409
	add  x5, x5, x5
	slli x5, x5, 1
	addi x6, x20, 1435
	add  x6, x6, x6
	slli x6, x6, 1
	sw x11, 192(s0)
	flw f21, 124(s0)
	flw f28, 228(s0)
	fmin.s f15, f21, f28
	fsw f15, 4(s0)
	fcvt.w.s x24, f21, rtz
	slli x25, x4, 14
	lbu x20, 51(s0)
	sw x31, 228(s0)
	lhu x26, 20(s0)
	lw x16, 208(s0)
	slti x4, x22, 1831
	addi x12, x18, 1699
	add  x12, x12, x12
	slli x12, x12, 1
	addi x6, x5, -226
	add  x6, x6, x6
	slli x6, x6, 1
	flw f5, 8(s0)
	flw f18, 104(s0)
	fadd.s f10, f5, f18
	fsw f10, 20(s0)
	fcvt.w.s x23, f5, rtz
	lb x21, 51(s0)
	addi x7, x23, -1308
	add  x7, x7, x7
	slli x7, x7, 1
	bge x29, x10, .Lsk_7
	nop
.Lsk_7:
	addi x17, x29, -581
	add  x17, x17, x17
	slli x17, x17, 1
	bgeu x31, x10, .Lsk_8
	nop
.Lsk_8:
	lb x13, 53(s0)
	mulh x7, x24, x30
	addi x3, x7, 1866
	add  x3, x3, x3
	slli x3, x3, 1
	bltu x20, x17, .Lsk_9
	nop
.Lsk_9:
	lw x27, 204(s0)
	addi x31, x19, -2021
	add  x31, x31, x31
	slli x31, x31, 1
	addi x3, x28, -1663
	add  x3, x3, x3
	slli x3, x3, 1
	auipc x23, 568979
	div x28, x30, x9
	div x18, x27, x4
	flw f14, 32(s0)
	flw f14, 60(s0)
	fsub.s f13, f14, f14
	fsw f13, 108(s0)
	fcvt.w.s x13, f14, rtz
	sh x16, 6(s0)
	addi x10, x28, 1502
	add  x10, x10, x10
	slli x10, x10, 1
	flw f28, 220(s0)
	flw f18, 156(s0)
	fmul.s f30, f28, f18
	fsw f30, 212(s0)
	fcvt.w.s x15, f28, rtz
	sltu x12, x22, x16
	div x28, x18, x5
	auipc x25, 375192
	sb x24, 175(s0)
	mulhu x9, x30, x18
	beq x16, x5, .Lsk_10
	nop
.Lsk_10:
	flw f22, 56(s0)
	flw f25, 12(s0)
	fmin.s f9, f22, f25
	fsw f9, 56(s0)
	fcvt.w.s x14, f22, rtz
	sb x15, 232(s0)
	slti x7, x16, 577
	flw f12, 56(s0)
	flw f17, 28(s0)
	fmin.s f29, f12, f17
	fsw f29, 36(s0)
	fcvt.w.s x17, f12, rtz
	addi x24, x27, -2030
	add  x24, x24, x24
	slli x24, x24, 1
	addi x24, x14, 1639
	add  x24, x24, x24
	slli x24, x24, 1
	flw f6, 4(s0)
	flw f4, 248(s0)
	fmul.s f7, f6, f4
	fsw f7, 116(s0)
	fcvt.w.s x25, f6, rtz
	flw f3, 116(s0)
	flw f9, 248(s0)
	fmax.s f15, f3, f9
	fsw f15, 4(s0)
	fcvt.w.s x7, f3, rtz
	lb x27, 162(s0)
	lb x24, 43(s0)
	addi x14, x25, -750
	add  x14, x14, x14
	slli x14, x14, 1
	lhu x5, 52(s0)
	slli x13, x31, 13
	addi x9, x20, -229
	add  x9, x9, x9
	slli x9, x9, 1
	flw f0, 0(s0)
	flw f9, 132(s0)
	fmax.s f27, f0, f9
	fsw f27, 64(s0)
	fcvt.w.s x15, f0, rtz
	slti x15, x29, -1404
	flw f7, 244(s0)
	flw f6, 160(s0)
	fsub.s f18, f7, f6
	fsw f18, 216(s0)
	fcvt.w.s x24, f7, rtz
	addi x26, x31, -645
	add  x26, x26, x26
	slli x26, x26, 1
	lw x24, 204(s0)
	lbu x23, 135(s0)
	addi x17, x7, 273
	add  x17, x17, x17
	slli x17, x17, 1
	srl x21, x6, x24
	sw x31, 144(s0)
	divu x9, x10, x19
	flw f22, 84(s0)
	flw f3, 132(s0)
	fmax.s f7, f22, f3
	fsw f7, 40(s0)
	fcvt.w.s x19, f22, rtz
	xori x6, x14, -1482
	sw x22, 252(s0)
	flw f4, 92(s0)
	flw f22, 188(s0)
	fadd.s f11, f4, f22
	fsw f11, 124(s0)
	fcvt.w.s x16, f4, rtz
	bge x21, x24, .Lsk_11
	nop
.Lsk_11:
	bge x3, x7, .Lsk_12
	nop
.Lsk_12:
	addi x6, x18, 531
	add  x6, x6, x6
	slli x6, x6, 1
	mulh x3, x31, x12
	bne x31, x19, .Lsk_13
	nop
.Lsk_13:
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
	.word 0xEC9BCB08
	.word 0x0BF074CA
	.word 0x128F280E
	.word 0xDDD951D5
	.word 0x8EFBF77D
	.word 0x53A84EF2
	.word 0x30B8043A
	.word 0x63C1A7E0
	.word 0x11087165
	.word 0xC3AF4033
	.word 0x87838B42
	.word 0x5DF955BD
	.word 0xF1D39F45
	.word 0x9D622D88
	.word 0xE3A0AAA4
	.word 0xA9E2F473
	.word 0x9DAA9CA0
	.word 0x1E2DF6DB
	.word 0x1FC663F5
	.word 0x96B6D5CA
	.word 0x4D27E06F
	.word 0x40699A1C
	.word 0xA26127CE
	.word 0x03061448
	.word 0xB79AB26E
	.word 0x7CA49776
	.word 0x7CFD22F2
	.word 0x2E4053C6
	.word 0xD30972AC
	.word 0x82708311
	.word 0xBA9B0A5B
	.word 0x95640FCD
	.word 0x403382A7
	.word 0xED0034E1
	.word 0xF07DBFA9
	.word 0x48571F26
	.word 0xE678A9B4
	.word 0xC2BCF09C
	.word 0xA2D30C53
	.word 0x14B054D3
	.word 0xF38E6E87
	.word 0x09A71801
	.word 0xDD23A90A
	.word 0x7C4E58BD
	.word 0x54B81136
	.word 0x3F343E9A
	.word 0x4E8B121C
	.word 0x435292A1
	.word 0x74F5B14F
	.word 0x325F0736
	.word 0x1F7BCFDC
	.word 0xF91E21A3
	.word 0x7DE481C3
	.word 0xF3B7AAD7
	.word 0x8DC7BD1D
	.word 0x485AEC71
	.word 0x2781B468
	.word 0x5C10579B
	.word 0x06C8CD55
	.word 0xB9CAD7A8
	.word 0x92AE028C
	.word 0x7AA8324B
	.word 0x6EB140D1
	.word 0x70403316
