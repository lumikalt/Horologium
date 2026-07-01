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
	addi x23, x9, 1115
	add  x23, x23, x23
	slli x23, x23, 1
	flw f21, 160(s0)
	flw f26, 180(s0)
	fmin.s f25, f21, f26
	fsw f25, 132(s0)
	fcvt.w.s x5, f21, rtz
	slt x18, x4, x5
	bge x18, x17, .Lsk_0
	nop
.Lsk_0:
	bgeu x27, x14, .Lsk_1
	nop
.Lsk_1:
	bgeu x6, x25, .Lsk_2
	nop
.Lsk_2:
	sh x30, 146(s0)
	addi x28, x15, -1282
	add  x28, x28, x28
	slli x28, x28, 1
	addi x27, x12, 787
	add  x27, x27, x27
	slli x27, x27, 1
	lw x7, 212(s0)
	mul x30, x4, x5
	bgeu x4, x15, .Lsk_3
	nop
.Lsk_3:
	srai x20, x31, 22
	addi x15, x23, -1480
	add  x15, x15, x15
	slli x15, x15, 1
	sh x6, 122(s0)
	lbu x30, 129(s0)
	ori x31, x27, -677
	addi x4, x28, -1905
	add  x4, x4, x4
	slli x4, x4, 1
	lbu x24, 59(s0)
	add x25, x6, x20
	lb x19, 25(s0)
	flw f17, 48(s0)
	flw f9, 60(s0)
	fadd.s f27, f17, f9
	fsw f27, 248(s0)
	fcvt.w.s x25, f17, rtz
	xori x15, x4, 412
	or x7, x5, x24
	lhu x31, 170(s0)
	sltiu x23, x13, -8
	bge x20, x13, .Lsk_4
	nop
.Lsk_4:
	slti x14, x28, -180
	lhu x15, 196(s0)
	addi x10, x3, 306
	add  x10, x10, x10
	slli x10, x10, 1
	slt x16, x29, x4
	lui x21, 264441
	mul x4, x19, x12
	addi x28, x11, 551
	add  x28, x28, x28
	slli x28, x28, 1
	slli x21, x21, 28
	sltiu x23, x11, 2023
	auipc x30, 648904
	auipc x19, 443727
	lhu x23, 58(s0)
	sh x28, 172(s0)
	lui x17, 442537
	lbu x27, 213(s0)
	flw f19, 216(s0)
	flw f27, 48(s0)
	fsub.s f14, f19, f27
	fsw f14, 24(s0)
	fcvt.w.s x12, f19, rtz
	addi x10, x17, -1950
	bltu x15, x17, .Lsk_5
	nop
.Lsk_5:
	sw x5, 24(s0)
	sh x16, 72(s0)
	lw x3, 124(s0)
	addi x23, x22, -2014
	addi x24, x6, -97
	add  x24, x24, x24
	slli x24, x24, 1
	mul x23, x23, x29
	sll x20, x14, x16
	sh x28, 218(s0)
	sb x24, 194(s0)
	srai x28, x22, 28
	or x21, x30, x12
	bgeu x26, x29, .Lsk_6
	nop
.Lsk_6:
	ori x28, x7, 47
	sb x5, 79(s0)
	flw f3, 164(s0)
	flw f23, 116(s0)
	fmin.s f16, f3, f23
	fsw f16, 16(s0)
	fcvt.w.s x12, f3, rtz
	addi x25, x3, 189
	add  x25, x25, x25
	slli x25, x25, 1
	sw x24, 220(s0)
	auipc x29, 539831
	flw f8, 68(s0)
	flw f8, 40(s0)
	fmin.s f22, f8, f8
	fsw f22, 248(s0)
	fcvt.w.s x20, f8, rtz
	slli x25, x7, 1
	lui x30, 807708
	xori x30, x24, 1550
	bge x15, x4, .Lsk_7
	nop
.Lsk_7:
	addi x20, x14, 1720
	add  x20, x20, x20
	slli x20, x20, 1
	bgeu x15, x12, .Lsk_8
	nop
.Lsk_8:
	lhu x3, 74(s0)
	add x27, x9, x26
	flw f13, 88(s0)
	flw f18, 120(s0)
	fmin.s f8, f13, f18
	fsw f8, 192(s0)
	fcvt.w.s x7, f13, rtz
	addi x5, x23, -941
	add  x5, x5, x5
	slli x5, x5, 1
	srai x19, x19, 11
	sll x31, x29, x13
	bltu x24, x23, .Lsk_9
	nop
.Lsk_9:
	sltu x5, x25, x15
	srli x30, x10, 26
	addi x21, x31, 214
	add  x21, x21, x21
	slli x21, x21, 1
	sh x7, 12(s0)
	mul x6, x3, x5
	rem x3, x27, x18
	sb x10, 35(s0)
	srai x23, x10, 7
	sra x7, x16, x17
	mul x18, x14, x20
	auipc x29, 217907
	blt x29, x29, .Lsk_10
	nop
.Lsk_10:
	lhu x25, 108(s0)
	addi x26, x14, -127
	add  x26, x26, x26
	slli x26, x26, 1
	slti x11, x27, 1797
	sh x19, 30(s0)
	srli x25, x10, 16
	sw x18, 72(s0)
	srai x31, x19, 0
	auipc x30, 851693
	addi x30, x6, 677
	add  x30, x30, x30
	slli x30, x30, 1
	sw x25, 112(s0)
	sb x6, 90(s0)
	addi x28, x18, -541
	add  x28, x28, x28
	slli x28, x28, 1
	lui x19, 3599
	mul x24, x16, x12
	addi x28, x14, -1207
	add  x28, x28, x28
	slli x28, x28, 1
	flw f19, 212(s0)
	flw f9, 124(s0)
	fadd.s f15, f19, f9
	fsw f15, 4(s0)
	fcvt.w.s x19, f19, rtz
	flw f11, 124(s0)
	flw f29, 252(s0)
	fmul.s f0, f11, f29
	fsw f0, 168(s0)
	fcvt.w.s x20, f11, rtz
	auipc x16, 64276
	bltu x11, x25, .Lsk_11
	nop
.Lsk_11:
	bge x25, x27, .Lsk_12
	nop
.Lsk_12:
	slli x18, x3, 9
	flw f24, 188(s0)
	flw f6, 72(s0)
	fmax.s f2, f24, f6
	fsw f2, 92(s0)
	fcvt.w.s x3, f24, rtz
	srli x16, x19, 15
	addi x4, x23, -1745
	remu x5, x20, x24
	or x21, x26, x3
	mulh x15, x4, x30
	addi x15, x31, -1624
	bltu x24, x13, .Lsk_13
	nop
.Lsk_13:
	bgeu x11, x4, .Lsk_14
	nop
.Lsk_14:
	lbu x19, 5(s0)
	lui x30, 403378
	addi x31, x23, -308
	add  x31, x31, x31
	slli x31, x31, 1
	slt x21, x3, x19
	ori x12, x29, 1722
	remu x10, x29, x6
	sw x11, 212(s0)
	mulhu x29, x3, x19
	sll x11, x10, x23
	addi x23, x5, -1928
	add  x23, x23, x23
	slli x23, x23, 1
	add x24, x13, x6
	sh x21, 64(s0)
	slli x11, x22, 4
	sh x21, 128(s0)
	bne x28, x26, .Lsk_15
	nop
.Lsk_15:
	lbu x18, 110(s0)
	flw f14, 216(s0)
	flw f29, 64(s0)
	fmax.s f19, f14, f29
	fsw f19, 108(s0)
	fcvt.w.s x30, f14, rtz
	sll x16, x6, x9
	lbu x22, 139(s0)
	addi x20, x29, 68
	add  x20, x20, x20
	slli x20, x20, 1
	flw f9, 164(s0)
	flw f29, 64(s0)
	fmin.s f8, f9, f29
	fsw f8, 132(s0)
	fcvt.w.s x24, f9, rtz
	auipc x29, 294454
	flw f20, 72(s0)
	flw f6, 160(s0)
	fmax.s f15, f20, f6
	fsw f15, 56(s0)
	fcvt.w.s x21, f20, rtz
	flw f19, 0(s0)
	flw f29, 132(s0)
	fadd.s f7, f19, f29
	fsw f7, 224(s0)
	fcvt.w.s x7, f19, rtz
	flw f5, 28(s0)
	flw f12, 224(s0)
	fsub.s f25, f5, f12
	fsw f25, 56(s0)
	fcvt.w.s x31, f5, rtz
	slt x11, x12, x26
	addi x14, x16, -1706
	add  x14, x14, x14
	slli x14, x14, 1
	addi x27, x28, -5
	add  x27, x27, x27
	slli x27, x27, 1
	flw f19, 196(s0)
	flw f31, 224(s0)
	fmul.s f5, f19, f31
	fsw f5, 72(s0)
	fcvt.w.s x20, f19, rtz
	addi x18, x22, 1502
	add  x18, x18, x18
	slli x18, x18, 1
	flw f9, 124(s0)
	flw f25, 216(s0)
	fadd.s f6, f9, f25
	fsw f6, 96(s0)
	fcvt.w.s x31, f9, rtz
	sh x3, 68(s0)
	lw x26, 204(s0)
	lhu x19, 2(s0)
	flw f27, 208(s0)
	flw f4, 204(s0)
	fsub.s f10, f27, f4
	fsw f10, 100(s0)
	fcvt.w.s x3, f27, rtz
	bne x15, x31, .Lsk_16
	nop
.Lsk_16:
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
	.word 0x15279C6B
	.word 0x4C797300
	.word 0x4AAD1252
	.word 0xD4BB11D5
	.word 0x38D6C42F
	.word 0xCF95FF21
	.word 0x36F0E973
	.word 0x99E3AF2C
	.word 0xA05D2E63
	.word 0x40B99D63
	.word 0x98B8A244
	.word 0x53954186
	.word 0x68F11DDE
	.word 0x098B0EBC
	.word 0xEAF0D407
	.word 0xF74F9020
	.word 0x4C022239
	.word 0x344EBE6D
	.word 0xD5812987
	.word 0xA61F45A8
	.word 0xF5DC60DE
	.word 0xD668C654
	.word 0xAEF3C97A
	.word 0x2B384577
	.word 0x30FE72A5
	.word 0xAB477424
	.word 0xED317608
	.word 0x856EE9A2
	.word 0x94268016
	.word 0x94F17952
	.word 0x10E16B0B
	.word 0xB8C02CBD
	.word 0x22DE67CB
	.word 0x6650531A
	.word 0x982E54BB
	.word 0x8577A2B1
	.word 0x3C0D8013
	.word 0x0A8775C3
	.word 0xEB940611
	.word 0xB311ED7A
	.word 0xFFDB34BE
	.word 0xA12C5E62
	.word 0x96133720
	.word 0x1535BD52
	.word 0xD1F53780
	.word 0x058CEAE5
	.word 0x9C46B33A
	.word 0xECED00F7
	.word 0xC1AE33B0
	.word 0xA04B654F
	.word 0x50269677
	.word 0x93C9F00F
	.word 0xA61A2E41
	.word 0xADC85AB4
	.word 0xD941FF4A
	.word 0xF03F6D11
	.word 0x03623513
	.word 0xC0A1D74A
	.word 0x7C17EBE2
	.word 0x15A948C3
	.word 0x9C10A689
	.word 0xA3A384E0
	.word 0xBDB2A826
	.word 0x06477931
