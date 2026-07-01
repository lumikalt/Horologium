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
	lui x14, 134319
	sw x4, 76(s0)
	bgeu x20, x24, .Lsk_0
	nop
.Lsk_0:
	sh x16, 204(s0)
	auipc x17, 666548
	addi x10, x24, 904
	add  x10, x10, x10
	slli x10, x10, 1
	add x20, x23, x14
	mulhu x11, x31, x13
	lh x31, 16(s0)
	flw f5, 48(s0)
	flw f14, 0(s0)
	fmul.s f28, f5, f14
	fsw f28, 108(s0)
	fcvt.w.s x17, f5, rtz
	srai x16, x3, 5
	lw x7, 192(s0)
	rem x10, x12, x12
	mulhu x31, x28, x4
	mulhsu x10, x5, x3
	or x29, x3, x25
	flw f11, 112(s0)
	flw f13, 68(s0)
	fsub.s f23, f11, f13
	fsw f23, 220(s0)
	fcvt.w.s x23, f11, rtz
	flw f13, 104(s0)
	flw f23, 160(s0)
	fsub.s f25, f13, f23
	fsw f25, 248(s0)
	fcvt.w.s x31, f13, rtz
	flw f24, 76(s0)
	flw f26, 172(s0)
	fmax.s f12, f24, f26
	fsw f12, 248(s0)
	fcvt.w.s x25, f24, rtz
	slti x3, x26, 1265
	rem x15, x17, x4
	sub x31, x29, x30
	sw x14, 36(s0)
	or x4, x18, x16
	addi x16, x12, 560
	add  x16, x16, x16
	slli x16, x16, 1
	bne x6, x4, .Lsk_1
	nop
.Lsk_1:
	auipc x7, 829793
	sh x28, 192(s0)
	lhu x20, 124(s0)
	bne x19, x22, .Lsk_2
	nop
.Lsk_2:
	xori x7, x24, -730
	addi x11, x6, -1478
	add  x11, x11, x11
	slli x11, x11, 1
	lbu x3, 138(s0)
	lb x3, 85(s0)
	lbu x4, 155(s0)
	sub x22, x30, x6
	sltiu x17, x27, 941
	sll x7, x29, x3
	bltu x4, x30, .Lsk_3
	nop
.Lsk_3:
	bgeu x7, x13, .Lsk_4
	nop
.Lsk_4:
	lb x17, 68(s0)
	lui x29, 912841
	rem x28, x12, x10
	slli x16, x23, 27
	mulhsu x9, x22, x13
	and x5, x20, x3
	bge x25, x13, .Lsk_5
	nop
.Lsk_5:
	beq x30, x18, .Lsk_6
	nop
.Lsk_6:
	sw x21, 108(s0)
	lbu x7, 18(s0)
	flw f15, 136(s0)
	flw f3, 192(s0)
	fmax.s f10, f15, f3
	fsw f10, 180(s0)
	fcvt.w.s x30, f15, rtz
	sh x20, 146(s0)
	flw f29, 132(s0)
	flw f13, 96(s0)
	fmax.s f21, f29, f13
	fsw f21, 224(s0)
	fcvt.w.s x14, f29, rtz
	slli x18, x11, 3
	lui x23, 55064
	flw f8, 116(s0)
	flw f3, 204(s0)
	fadd.s f29, f8, f3
	fsw f29, 20(s0)
	fcvt.w.s x31, f8, rtz
	lui x29, 723692
	auipc x5, 610606
	srai x19, x24, 24
	mulhsu x21, x14, x12
	bgeu x25, x4, .Lsk_7
	nop
.Lsk_7:
	sra x4, x27, x26
	auipc x29, 523883
	lui x12, 163398
	slli x21, x9, 24
	flw f7, 136(s0)
	flw f28, 64(s0)
	fsub.s f3, f7, f28
	fsw f3, 104(s0)
	fcvt.w.s x29, f7, rtz
	sltiu x16, x14, -1458
	addi x27, x15, -642
	add  x27, x27, x27
	slli x27, x27, 1
	lh x13, 128(s0)
	addi x29, x10, -476
	add  x29, x29, x29
	slli x29, x29, 1
	flw f31, 108(s0)
	flw f26, 204(s0)
	fmul.s f25, f31, f26
	fsw f25, 172(s0)
	fcvt.w.s x14, f31, rtz
	srli x19, x31, 18
	slli x21, x21, 22
	divu x14, x31, x15
	lb x23, 91(s0)
	mulhsu x19, x26, x15
	mulh x15, x26, x4
	srai x12, x26, 0
	and x29, x25, x6
	slli x18, x25, 5
	mulhsu x6, x22, x12
	lui x7, 859476
	flw f6, 180(s0)
	flw f5, 20(s0)
	fmin.s f4, f6, f5
	fsw f4, 232(s0)
	fcvt.w.s x23, f6, rtz
	flw f17, 48(s0)
	flw f28, 236(s0)
	fsub.s f11, f17, f28
	fsw f11, 144(s0)
	fcvt.w.s x11, f17, rtz
	divu x16, x31, x16
	lhu x11, 56(s0)
	lui x25, 814947
	lh x19, 34(s0)
	addi x22, x15, -212
	add  x22, x22, x22
	slli x22, x22, 1
	div x27, x20, x22
	sb x18, 128(s0)
	lw x18, 112(s0)
	remu x23, x26, x3
	addi x6, x24, 379
	add  x6, x6, x6
	slli x6, x6, 1
	slti x15, x16, -1320
	sh x6, 120(s0)
	addi x28, x16, -554
	add  x28, x28, x28
	slli x28, x28, 1
	addi x10, x10, 1666
	add  x10, x10, x10
	slli x10, x10, 1
	sll x14, x22, x22
	auipc x21, 477269
	sltu x12, x9, x29
	addi x22, x9, 580
	add  x22, x22, x22
	slli x22, x22, 1
	bge x10, x22, .Lsk_8
	nop
.Lsk_8:
	flw f3, 92(s0)
	flw f17, 68(s0)
	fmul.s f21, f3, f17
	fsw f21, 148(s0)
	fcvt.w.s x31, f3, rtz
	blt x15, x4, .Lsk_9
	nop
.Lsk_9:
	addi x24, x30, -319
	add  x24, x24, x24
	slli x24, x24, 1
	lh x10, 16(s0)
	lui x25, 68689
	flw f0, 84(s0)
	flw f13, 60(s0)
	fadd.s f25, f0, f13
	fsw f25, 212(s0)
	fcvt.w.s x17, f0, rtz
	flw f23, 44(s0)
	flw f21, 124(s0)
	fmin.s f19, f23, f21
	fsw f19, 244(s0)
	fcvt.w.s x31, f23, rtz
	lbu x12, 132(s0)
	and x10, x29, x15
	bltu x14, x28, .Lsk_10
	nop
.Lsk_10:
	mulh x23, x25, x5
	srli x6, x24, 28
	addi x4, x20, 625
	add  x4, x4, x4
	slli x4, x4, 1
	bgeu x10, x20, .Lsk_11
	nop
.Lsk_11:
	addi x26, x9, -552
	add  x26, x26, x26
	slli x26, x26, 1
	lw x21, 84(s0)
	addi x11, x10, 366
	add  x11, x11, x11
	slli x11, x11, 1
	mul x7, x12, x25
	addi x13, x11, 0
	add  x13, x13, x13
	slli x13, x13, 1
	rem x28, x24, x18
	bgeu x21, x18, .Lsk_12
	nop
.Lsk_12:
	addi x23, x3, 1578
	add  x23, x23, x23
	slli x23, x23, 1
	andi x10, x17, 1461
	srai x18, x7, 21
	lb x13, 161(s0)
	auipc x4, 1043256
	xor x23, x15, x3
	add x20, x31, x23
	bgeu x6, x25, .Lsk_13
	nop
.Lsk_13:
	addi x7, x15, 303
	add  x7, x7, x7
	slli x7, x7, 1
	lb x23, 205(s0)
	flw f4, 144(s0)
	flw f8, 132(s0)
	fsub.s f7, f4, f8
	fsw f7, 28(s0)
	fcvt.w.s x16, f4, rtz
	addi x29, x16, 1003
	lw x3, 220(s0)
	lui x16, 599452
	flw f25, 200(s0)
	flw f14, 56(s0)
	fmul.s f14, f25, f14
	fsw f14, 212(s0)
	fcvt.w.s x6, f25, rtz
	addi x25, x5, -951
	add  x25, x25, x25
	slli x25, x25, 1
	blt x30, x7, .Lsk_14
	nop
.Lsk_14:
	xor x21, x12, x27
	auipc x20, 279190
	addi x14, x5, -1573
	add  x14, x14, x14
	slli x14, x14, 1
	slt x7, x13, x26
	bne x18, x25, .Lsk_15
	nop
.Lsk_15:
	bgeu x4, x21, .Lsk_16
	nop
.Lsk_16:
	sh x25, 228(s0)
	srai x25, x13, 30
	beq x29, x26, .Lsk_17
	nop
.Lsk_17:
	add x25, x17, x23
	bgeu x12, x26, .Lsk_18
	nop
.Lsk_18:
	sltiu x25, x17, 1833
	lhu x9, 42(s0)
	bge x3, x22, .Lsk_19
	nop
.Lsk_19:
	slli x12, x13, 8
	bltu x21, x19, .Lsk_20
	nop
.Lsk_20:
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
	.word 0xA730D291
	.word 0x2A2E0358
	.word 0x012569C2
	.word 0xACE5AB0A
	.word 0x7D035AF8
	.word 0xD5CA0483
	.word 0x6C82AC1D
	.word 0x7F3B8F72
	.word 0xE5DA2172
	.word 0x9B5624AE
	.word 0x26882C80
	.word 0x53B38FE4
	.word 0x2002D507
	.word 0x90D5D3E1
	.word 0x2100ECE9
	.word 0xCC8AED18
	.word 0x90488140
	.word 0x454A0939
	.word 0xCDE532EC
	.word 0x5104BC3F
	.word 0xB0A53CFD
	.word 0xA3769179
	.word 0x5189971D
	.word 0x74DE05BC
	.word 0x34535BD8
	.word 0x65DB9EC2
	.word 0x74292F9B
	.word 0x7C5D3B90
	.word 0xD6709C75
	.word 0xE32FB01D
	.word 0x8F072B2F
	.word 0x1045A5EE
	.word 0x8164A434
	.word 0x03D18DFB
	.word 0xBA418E98
	.word 0x72AA4EB1
	.word 0x0FC223AB
	.word 0xE302D695
	.word 0x778A7BE4
	.word 0xEE26D6F2
	.word 0xB96A083A
	.word 0x93376FB4
	.word 0xD6010121
	.word 0x04001805
	.word 0x3FA80BED
	.word 0xB9905FC7
	.word 0x5912AE74
	.word 0x1EDD4206
	.word 0xB9B68DCE
	.word 0x07248B58
	.word 0x31CF2B3F
	.word 0x841B2FE3
	.word 0x3B9CC911
	.word 0xC85D0DE7
	.word 0x20B1016D
	.word 0x4B06C8C7
	.word 0x77256056
	.word 0xFE82A7D3
	.word 0xD8D954B4
	.word 0xE16E8957
	.word 0x7E23A36F
	.word 0xDE685A11
	.word 0xE48E1DD9
	.word 0x143DFCB2
