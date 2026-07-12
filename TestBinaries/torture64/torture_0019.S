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
	addi x23, x9, 1115
	add  x23, x23, x23
	slli x23, x23, 1
	fld f21, 176(s0)
	fld f26, 128(s0)
	fadd.d f25, f21, f26
	fsd f25, 224(s0)
	fcvt.l.d x5, f21, rtz
	mulhu x4, x10, x5
	bge x18, x17, .Lsk_0
	nop
.Lsk_0:
	bgeu x27, x14, .Lsk_1
	nop
.Lsk_1:
	bgeu x6, x25, .Lsk_2
	nop
.Lsk_2:
	sb x30, 146(s0)
	addi x28, x15, -1282
	add  x28, x28, x28
	slli x28, x28, 1
	addi x27, x12, 787
	add  x27, x27, x27
	slli x27, x27, 1
	ld x6, 72(s0)
	lw x30, 24(s0)
	andi x3, x5, -1611
	addiw x11, x20, -1593
	sb x31, 199(s0)
	srli x29, x5, 15
	slti x18, x20, 772
	addi x9, x31, -1643
	add  x9, x9, x9
	slli x9, x9, 1
	fld f1, 56(s0)
	fld f26, 104(s0)
	fadd.d f24, f1, f26
	fsd f24, 48(s0)
	fcvt.l.d x20, f1, rtz
	sllw x3, x20, x19
	sltiu x3, x23, -874
	lwu x6, 60(s0)
	addi x29, x5, -342
	add  x29, x29, x29
	slli x29, x29, 1
	auipc x4, 654313
	or x5, x10, x24
	lwu x17, 168(s0)
	sltiu x23, x13, -8
	bge x20, x13, .Lsk_3
	nop
.Lsk_3:
	slti x14, x28, -180
	lhu x15, 196(s0)
	addi x10, x3, 306
	add  x10, x10, x10
	slli x10, x10, 1
	slt x16, x29, x4
	addiw x21, x23, -223
	flw f21, 8(s0)
	flw f0, 132(s0)
	fsub.s f3, f21, f0
	fsw f3, 116(s0)
	fcvt.w.s x19, f21, rtz
	fld f20, 200(s0)
	fld f0, 16(s0)
	fadd.d f23, f20, f0
	fsd f23, 224(s0)
	fcvt.l.d x21, f20, rtz
	addiw x11, x27, 2023
	addiw x30, x15, 1714
	andi x31, x19, 1026
	sh x17, 218(s0)
	sw x22, 252(s0)
	flw f21, 244(s0)
	flw f15, 8(s0)
	fmin.s f6, f21, f15
	fsw f6, 108(s0)
	fcvt.w.s x17, f21, rtz
	sraw x27, x31, x28
	flw f19, 24(s0)
	flw f27, 124(s0)
	fsub.s f14, f19, f27
	fsw f14, 84(s0)
	fcvt.w.s x12, f19, rtz
	ld x4, 0(s0)
	bltu x15, x17, .Lsk_4
	nop
.Lsk_4:
	sw x5, 24(s0)
	sb x16, 72(s0)
	lwu x5, 12(s0)
	sh x7, 252(s0)
	lui x18, 723191
	sb x9, 1(s0)
	flw f16, 188(s0)
	flw f21, 244(s0)
	fmax.s f4, f16, f21
	fsw f4, 216(s0)
	fcvt.w.s x5, f16, rtz
	and x24, x16, x23
	mulhu x31, x18, x7
	sb x24, 123(s0)
	addi x7, x27, -1157
	add  x7, x7, x7
	slli x7, x7, 1
	addi x12, x21, 1568
	add  x12, x12, x12
	slli x12, x12, 1
	bne x30, x17, .Lsk_5
	nop
.Lsk_5:
	ori x12, x12, 578
	lui x4, 572761
	sw x24, 220(s0)
	auipc x29, 539831
	fld f8, 64(s0)
	fld f8, 40(s0)
	fmin.d f22, f8, f8
	fsd f22, 248(s0)
	fcvt.l.d x20, f8, rtz
	slli x25, x7, 2
	lui x30, 807708
	xori x30, x24, 1550
	bge x15, x4, .Lsk_6
	nop
.Lsk_6:
	addi x20, x14, 1720
	add  x20, x20, x20
	slli x20, x20, 1
	bgeu x15, x12, .Lsk_7
	nop
.Lsk_7:
	lwu x17, 12(s0)
	beq x16, x27, .Lsk_8
	nop
.Lsk_8:
	lui x4, 619418
	lwu x20, 68(s0)
	rem x25, x26, x18
	addi x5, x23, -941
	add  x5, x5, x5
	slli x5, x5, 1
	srai x19, x19, 22
	sll x31, x29, x13
	bltu x24, x23, .Lsk_9
	nop
.Lsk_9:
	sltu x5, x25, x15
	srli x30, x10, 52
	addi x21, x31, 214
	add  x21, x21, x21
	slli x21, x21, 1
	sb x7, 13(s0)
	addw x6, x3, x5
	remuw x3, x27, x17
	lbu x22, 103(s0)
	lui x30, 246296
	remu x7, x16, x17
	remuw x18, x14, x21
	andi x12, x21, 849
	addi x24, x26, 797
	add  x24, x24, x24
	slli x24, x24, 1
	flw f24, 104(s0)
	flw f24, 120(s0)
	fmin.s f13, f24, f24
	fsw f13, 100(s0)
	fcvt.w.s x26, f24, rtz
	sb x19, 30(s0)
	srli x25, x10, 33
	sh x18, 74(s0)
	srai x31, x19, 1
	auipc x30, 851693
	addi x30, x6, 677
	add  x30, x30, x30
	slli x30, x30, 1
	sh x25, 112(s0)
	sw x9, 232(s0)
	addi x6, x23, 1801
	add  x6, x6, x6
	slli x6, x6, 1
	lwu x9, 0(s0)
	mul x24, x16, x12
	addi x28, x14, -1207
	add  x28, x28, x28
	slli x28, x28, 1
	fld f19, 208(s0)
	fld f9, 120(s0)
	fadd.d f15, f19, f9
	fsd f15, 0(s0)
	fcvt.l.d x19, f19, rtz
	fld f11, 120(s0)
	fld f29, 248(s0)
	fmul.d f0, f11, f29
	fsd f0, 168(s0)
	fcvt.l.d x20, f11, rtz
	xori x16, x28, -1797
	bltu x11, x25, .Lsk_10
	nop
.Lsk_10:
	bge x25, x27, .Lsk_11
	nop
.Lsk_11:
	mulw x18, x3, x7
	fld f24, 72(s0)
	fld f6, 88(s0)
	fmin.d f2, f24, f6
	fsd f2, 136(s0)
	fcvt.l.d x3, f24, rtz
	lwu x30, 200(s0)
	sb x24, 23(s0)
	sw x4, 52(s0)
	bltu x14, x25, .Lsk_12
	nop
.Lsk_12:
	addi x25, x18, 784
	add  x25, x25, x25
	slli x25, x25, 1
	addi x26, x9, 1210
	add  x26, x26, x26
	slli x26, x26, 1
	lw x6, 180(s0)
	addi x14, x24, -1319
	add  x14, x14, x14
	slli x14, x14, 1
	flw f23, 144(s0)
	flw f0, 120(s0)
	fmin.s f3, f23, f0
	fsw f3, 28(s0)
	fcvt.w.s x26, f23, rtz
	sll x19, x26, x10
	addi x31, x23, -308
	add  x31, x31, x31
	slli x31, x31, 1
	slt x21, x3, x19
	ori x12, x29, 1722
	srlw x10, x29, x6
	sw x11, 212(s0)
	divw x29, x3, x19
	sll x11, x10, x23
	addi x23, x5, -1928
	add  x23, x23, x23
	slli x23, x23, 1
	add x24, x13, x6
	sb x21, 65(s0)
	slliw x11, x22, 4
	sd x21, 128(s0)
	bne x28, x26, .Lsk_13
	nop
.Lsk_13:
	lbu x18, 110(s0)
	fld f14, 64(s0)
	fld f29, 104(s0)
	fmin.d f19, f14, f29
	fsd f19, 168(s0)
	fcvt.l.d x30, f14, rtz
	sraw x9, x9, x22
	flw f17, 68(s0)
	flw f16, 164(s0)
	fmax.s f9, f17, f16
	fsw f9, 64(s0)
	fcvt.w.s x31, f17, rtz
	mulhsu x25, x17, x31
	addi x18, x7, 590
	add  x18, x18, x18
	slli x18, x18, 1
	xor x11, x21, x27
	lbu x29, 155(s0)
	beq x7, x21, .Lsk_14
	nop
.Lsk_14:
	xori x26, x12, -1954
	fld f5, 24(s0)
	fld f12, 224(s0)
	fsub.d f25, f5, f12
	fsd f25, 56(s0)
	fcvt.l.d x31, f5, rtz
	mulhu x11, x12, x26
	addi x14, x16, -1706
	add  x14, x14, x14
	slli x14, x14, 1
	addi x27, x28, -5
	add  x27, x27, x27
	slli x27, x27, 1
	flw f19, 224(s0)
	flw f31, 72(s0)
	fmin.s f5, f19, f31
	fsw f5, 156(s0)
	fcvt.w.s x20, f19, rtz
	sh x16, 52(s0)
	addi x17, x10, -1776
	add  x17, x17, x17
	slli x17, x17, 1
	sb x3, 68(s0)
	lw x26, 204(s0)
	lhu x19, 2(s0)
	fld f27, 200(s0)
	fld f4, 96(s0)
	fmax.d f10, f27, f4
	fsd f10, 72(s0)
	fcvt.l.d x3, f27, rtz
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
	.word 0x2B4FAE9E
	.word 0xD94726F3
	.word 0x9AFAA055
	.word 0x21D18040
	.word 0x3E57AC99
	.word 0x4B5CFC1C
	.word 0xADC8ADD5
	.word 0x94513012
	.word 0x3B8FC76B
	.word 0x142E5D7E
	.word 0x1C607D0F
	.word 0x3657EF24
	.word 0x0454CED0
	.word 0x6612BFA0
	.word 0x30D723D6
	.word 0x2808E02F
	.word 0xBC064890
	.word 0xABDDDF3E
	.word 0xD485347F
	.word 0x5FFC1CED
	.word 0x3A2B2A91
	.word 0x37AA65BC
	.word 0x58DDD14F
	.word 0x6A86E96F
	.word 0x9499B287
	.word 0x901AF57F
	.word 0x606600DA
	.word 0xDA8FDA87
	.word 0xF2A20888
	.word 0x3D2704CA
	.word 0x193FA59B
	.word 0x188523C1
	.word 0xE5685F75
	.word 0xD3B14E5A
	.word 0xB5278DCD
	.word 0x47D3480C
	.word 0x665F5151
	.word 0xA08653F9
	.word 0x9EF76836
	.word 0x07A30754
	.word 0xC0FD89DD
	.word 0x19585876
	.word 0x62A5DE27
	.word 0xAE231375
	.word 0x14EC0F31
	.word 0x6ADAC4BB
	.word 0x8FFBE223
	.word 0x43B0B3CE
	.word 0x54055890
	.word 0xD93FB0AD
	.word 0x93C1330C
	.word 0x439FC1FC
	.word 0xC4BF987A
	.word 0x55F26914
	.word 0xB198A237
	.word 0x611A60C2
	.word 0x1E671039
	.word 0x8EF7AB04
	.word 0x3B997CD5
	.word 0xBC9A5049
	.word 0xC9CE06B7
	.word 0xA40F3EDC
	.word 0x9CDB6821
	.word 0x32C8259C
