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
	lui x14, 134319
	sh x4, 78(s0)
	bgeu x20, x24, .Lsk_0
	nop
.Lsk_0:
	sb x16, 205(s0)
	andi x17, x30, 912
	addi x24, x14, -452
	add  x24, x24, x24
	slli x24, x24, 1
	bne x28, x20, .Lsk_1
	nop
.Lsk_1:
	sd x4, 168(s0)
	subw x11, x31, x13
	lh x31, 16(s0)
	flw f5, 0(s0)
	flw f14, 108(s0)
	fmul.s f28, f5, f14
	fsw f28, 128(s0)
	fcvt.w.s x17, f5, rtz
	lw x20, 40(s0)
	lw x7, 192(s0)
	srlw x10, x12, x12
	divw x31, x28, x4
	mulhsu x10, x5, x3
	or x29, x3, x25
	fld f11, 64(s0)
	fld f13, 216(s0)
	fadd.d f23, f11, f13
	fsd f23, 88(s0)
	fcvt.l.d x23, f11, rtz
	andi x31, x22, -375
	sd x10, 192(s0)
	lwu x7, 172(s0)
	lb x17, 2(s0)
	bge x30, x7, .Lsk_2
	nop
.Lsk_2:
	lw x15, 216(s0)
	subw x24, x20, x26
	addi x29, x10, -187
	add  x29, x29, x29
	slli x29, x29, 1
	flw f6, 28(s0)
	flw f13, 236(s0)
	fadd.s f6, f6, f13
	fsw f6, 92(s0)
	fcvt.w.s x14, f6, rtz
	sh x16, 134(s0)
	blt x26, x28, .Lsk_3
	nop
.Lsk_3:
	fld f8, 200(s0)
	fld f6, 240(s0)
	fmax.d f2, f8, f6
	fsd f2, 200(s0)
	fcvt.l.d x10, f8, rtz
	flw f24, 124(s0)
	flw f30, 68(s0)
	fmax.s f12, f24, f30
	fsw f12, 240(s0)
	fcvt.w.s x31, f24, rtz
	srliw x7, x24, 10
	addi x11, x6, -1478
	add  x11, x11, x11
	slli x11, x11, 1
	lbu x3, 138(s0)
	lb x3, 85(s0)
	lbu x4, 155(s0)
	sub x22, x30, x6
	srliw x17, x27, 23
	sll x7, x29, x3
	bltu x4, x30, .Lsk_4
	nop
.Lsk_4:
	bgeu x7, x13, .Lsk_5
	nop
.Lsk_5:
	lb x17, 68(s0)
	addiw x29, x19, -424
	beq x25, x28, .Lsk_6
	nop
.Lsk_6:
	rem x6, x17, x23
	lh x17, 22(s0)
	mulhsu x22, x16, x13
	and x5, x20, x3
	bge x25, x13, .Lsk_7
	nop
.Lsk_7:
	beq x30, x18, .Lsk_8
	nop
.Lsk_8:
	sw x21, 108(s0)
	ld x21, 72(s0)
	andi x11, x11, -737
	fld f17, 160(s0)
	fld f24, 144(s0)
	fsub.d f22, f17, f24
	fsd f22, 232(s0)
	fcvt.l.d x23, f17, rtz
	blt x26, x12, .Lsk_9
	nop
.Lsk_9:
	bgeu x27, x10, .Lsk_10
	nop
.Lsk_10:
	sw x4, 232(s0)
	addi x20, x13, -660
	add  x20, x20, x20
	slli x20, x20, 1
	addi x4, x10, -1983
	add  x4, x4, x4
	slli x4, x4, 1
	addi x30, x7, -1649
	add  x30, x30, x30
	slli x30, x30, 1
	addi x18, x31, -176
	add  x18, x18, x18
	slli x18, x18, 1
	fld f2, 208(s0)
	fld f4, 40(s0)
	fsub.d f0, f2, f4
	fsd f0, 176(s0)
	fcvt.l.d x27, f2, rtz
	flw f25, 156(s0)
	flw f18, 196(s0)
	fmin.s f12, f25, f18
	fsw f12, 84(s0)
	fcvt.w.s x19, f25, rtz
	sd x3, 80(s0)
	slliw x22, x26, 17
	mulw x3, x27, x24
	flw f0, 180(s0)
	flw f16, 116(s0)
	fadd.s f15, f0, f16
	fsw f15, 124(s0)
	fcvt.w.s x3, f0, rtz
	slli x21, x9, 49
	flw f7, 64(s0)
	flw f28, 104(s0)
	fadd.s f3, f7, f28
	fsw f3, 96(s0)
	fcvt.w.s x29, f7, rtz
	mulh x14, x27, x31
	flw f23, 128(s0)
	flw f10, 108(s0)
	fmin.s f25, f23, f10
	fsw f25, 96(s0)
	fcvt.w.s x6, f23, rtz
	flw f25, 16(s0)
	flw f21, 240(s0)
	fmul.s f13, f25, f21
	fsw f13, 184(s0)
	fcvt.w.s x16, f25, rtz
	slli x21, x21, 45
	sllw x14, x31, x15
	lb x23, 91(s0)
	mulhsu x19, x26, x15
	addw x15, x26, x4
	srai x12, x26, 1
	and x29, x25, x6
	slli x18, x25, 11
	subw x6, x22, x12
	lui x7, 859476
	flw f6, 20(s0)
	flw f5, 232(s0)
	fmul.s f4, f6, f5
	fsw f4, 224(s0)
	fcvt.w.s x23, f6, rtz
	fld f28, 232(s0)
	fld f11, 144(s0)
	fmul.d f14, f28, f11
	fsd f14, 64(s0)
	fcvt.l.d x25, f28, rtz
	srl x31, x14, x16
	bne x6, x13, .Lsk_11
	nop
.Lsk_11:
	addi x18, x7, -1670
	add  x18, x18, x18
	slli x18, x18, 1
	fld f24, 32(s0)
	fld f29, 176(s0)
	fmul.d f21, f24, f29
	fsd f21, 112(s0)
	fcvt.l.d x9, f24, rtz
	lbu x15, 151(s0)
	sd x12, 216(s0)
	addi x6, x18, -233
	add  x6, x6, x6
	slli x6, x6, 1
	remu x23, x26, x3
	addi x6, x24, 379
	add  x6, x6, x6
	slli x6, x6, 1
	slliw x15, x16, 5
	sb x6, 120(s0)
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
	bge x10, x22, .Lsk_12
	nop
.Lsk_12:
	fld f3, 64(s0)
	fld f17, 144(s0)
	fmul.d f21, f3, f17
	fsd f21, 168(s0)
	fcvt.l.d x31, f3, rtz
	slliw x4, x20, 24
	bne x4, x5, .Lsk_13
	nop
.Lsk_13:
	lwu x9, 16(s0)
	fld f0, 80(s0)
	fld f13, 56(s0)
	fadd.d f25, f0, f13
	fsd f25, 208(s0)
	fcvt.l.d x17, f0, rtz
	flw f23, 124(s0)
	flw f21, 244(s0)
	fmax.s f19, f23, f21
	fsw f19, 220(s0)
	fcvt.w.s x31, f23, rtz
	mulhu x12, x12, x29
	sraiw x23, x15, 21
	addi x16, x6, 1771
	add  x16, x16, x16
	slli x16, x16, 1
	sub x27, x5, x24
	sd x18, 24(s0)
	lwu x22, 96(s0)
	lh x26, 90(s0)
	lui x4, 443127
	addi x15, x11, -937
	add  x15, x15, x15
	slli x15, x15, 1
	srliw x18, x3, 15
	srliw x24, x28, 27
	lbu x31, 226(s0)
	addi x23, x3, 1578
	add  x23, x23, x23
	slli x23, x23, 1
	andi x10, x17, 1461
	srai x18, x7, 43
	ld x14, 144(s0)
	sb x11, 28(s0)
	remw x13, x12, x23
	or x10, x14, x31
	sraiw x3, x23, 6
	bgeu x7, x15, .Lsk_14
	nop
.Lsk_14:
	addi x17, x29, 459
	add  x17, x17, x17
	slli x17, x17, 1
	sd x30, 32(s0)
	lh x22, 60(s0)
	lwu x23, 132(s0)
	flw f15, 52(s0)
	flw f13, 188(s0)
	fadd.s f18, f15, f13
	fsw f18, 236(s0)
	fcvt.w.s x29, f15, rtz
	srlw x14, x11, x21
	lui x13, 488477
	srl x16, x31, x12
	addi x25, x5, -951
	add  x25, x25, x25
	slli x25, x25, 1
	blt x30, x7, .Lsk_15
	nop
.Lsk_15:
	xor x21, x12, x27
	auipc x20, 279190
	addi x14, x5, -1573
	add  x14, x14, x14
	slli x14, x14, 1
	slt x7, x13, x26
	bne x18, x25, .Lsk_16
	nop
.Lsk_16:
	bgeu x4, x21, .Lsk_17
	nop
.Lsk_17:
	sb x25, 229(s0)
	srai x25, x13, 60
	beq x29, x26, .Lsk_18
	nop
.Lsk_18:
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
	.word 0x4BE27156
	.word 0xAC2C2E28
	.word 0x18B4978F
	.word 0xCB24B496
	.word 0x495A3BCB
	.word 0xAAF3556A
	.word 0x794B3FA1
	.word 0x1D711581
	.word 0x28A974F5
	.word 0xA251F5ED
	.word 0x66D36066
	.word 0x94257DDF
	.word 0xC9C483EC
	.word 0x98AF5055
	.word 0xFD501B4B
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
