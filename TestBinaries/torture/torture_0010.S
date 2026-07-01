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
	flw f7, 140(s0)
	flw f26, 4(s0)
	fmin.s f9, f7, f26
	fsw f9, 252(s0)
	fcvt.w.s x18, f7, rtz
	blt x9, x10, .Lsk_0
	nop
.Lsk_0:
	addi x7, x18, -1535
	add  x7, x7, x7
	slli x7, x7, 1
	lb x30, 52(s0)
	blt x23, x13, .Lsk_1
	nop
.Lsk_1:
	flw f20, 192(s0)
	flw f21, 60(s0)
	fmax.s f4, f20, f21
	fsw f4, 192(s0)
	fcvt.w.s x14, f20, rtz
	flw f12, 188(s0)
	flw f11, 12(s0)
	fmin.s f27, f12, f11
	fsw f27, 44(s0)
	fcvt.w.s x10, f12, rtz
	flw f14, 228(s0)
	flw f16, 12(s0)
	fmin.s f13, f14, f16
	fsw f13, 16(s0)
	fcvt.w.s x12, f14, rtz
	addi x10, x28, -1455
	add  x10, x10, x10
	slli x10, x10, 1
	flw f15, 144(s0)
	flw f19, 84(s0)
	fmax.s f13, f15, f19
	fsw f13, 60(s0)
	fcvt.w.s x29, f15, rtz
	remu x14, x21, x20
	divu x22, x16, x13
	sw x25, 224(s0)
	lhu x22, 150(s0)
	sh x10, 254(s0)
	slli x10, x19, 12
	auipc x20, 875308
	addi x7, x10, 1238
	add  x7, x7, x7
	slli x7, x7, 1
	lw x31, 164(s0)
	lb x18, 42(s0)
	flw f16, 20(s0)
	flw f11, 0(s0)
	fsub.s f9, f16, f11
	fsw f9, 16(s0)
	fcvt.w.s x6, f16, rtz
	divu x18, x30, x6
	div x28, x31, x7
	slli x26, x11, 2
	lui x23, 235130
	flw f15, 172(s0)
	flw f10, 0(s0)
	fsub.s f17, f15, f10
	fsw f17, 180(s0)
	fcvt.w.s x14, f15, rtz
	bltu x9, x18, .Lsk_2
	nop
.Lsk_2:
	lw x19, 28(s0)
	lh x7, 86(s0)
	sb x5, 103(s0)
	lw x4, 220(s0)
	lh x13, 234(s0)
	addi x21, x12, 982
	add  x21, x21, x21
	slli x21, x21, 1
	lb x15, 17(s0)
	auipc x21, 187934
	or x10, x14, x29
	slti x5, x26, -484
	sb x6, 252(s0)
	auipc x22, 11716
	xori x25, x28, 404
	lui x12, 5283
	slt x10, x19, x17
	srai x10, x28, 24
	sw x30, 216(s0)
	sh x22, 216(s0)
	sb x12, 170(s0)
	flw f16, 112(s0)
	flw f13, 12(s0)
	fsub.s f5, f16, f13
	fsw f5, 92(s0)
	fcvt.w.s x21, f16, rtz
	srli x6, x24, 17
	srai x5, x27, 8
	addi x26, x9, -817
	add  x26, x26, x26
	slli x26, x26, 1
	and x25, x15, x19
	bge x14, x4, .Lsk_3
	nop
.Lsk_3:
	flw f29, 164(s0)
	flw f7, 108(s0)
	fmul.s f12, f29, f7
	fsw f12, 84(s0)
	fcvt.w.s x9, f29, rtz
	mul x11, x18, x10
	addi x20, x28, 384
	add  x20, x20, x20
	slli x20, x20, 1
	remu x25, x25, x15
	addi x23, x17, -1685
	sh x22, 106(s0)
	lbu x21, 69(s0)
	or x6, x6, x21
	flw f20, 228(s0)
	flw f26, 204(s0)
	fmax.s f24, f20, f26
	fsw f24, 176(s0)
	fcvt.w.s x9, f20, rtz
	flw f24, 72(s0)
	flw f27, 116(s0)
	fadd.s f6, f24, f27
	fsw f6, 128(s0)
	fcvt.w.s x6, f24, rtz
	andi x24, x29, -1957
	srli x6, x20, 0
	xor x9, x30, x22
	flw f8, 164(s0)
	flw f8, 140(s0)
	fmul.s f6, f8, f8
	fsw f6, 240(s0)
	fcvt.w.s x31, f8, rtz
	slli x16, x12, 11
	sh x7, 34(s0)
	srli x7, x30, 1
	blt x10, x3, .Lsk_4
	nop
.Lsk_4:
	or x29, x26, x21
	rem x17, x16, x26
	auipc x19, 189221
	add x26, x6, x27
	sw x25, 80(s0)
	sw x22, 244(s0)
	flw f10, 100(s0)
	flw f21, 120(s0)
	fadd.s f4, f10, f21
	fsw f4, 236(s0)
	fcvt.w.s x27, f10, rtz
	lbu x19, 199(s0)
	sb x16, 15(s0)
	addi x28, x21, -1457
	add  x28, x28, x28
	slli x28, x28, 1
	srli x30, x25, 22
	or x21, x15, x12
	auipc x19, 894550
	lbu x12, 100(s0)
	addi x16, x28, -1139
	add  x16, x16, x16
	slli x16, x16, 1
	rem x18, x23, x3
	sltiu x18, x16, 1856
	sw x5, 84(s0)
	srai x15, x7, 11
	addi x14, x17, 962
	add  x14, x14, x14
	slli x14, x14, 1
	bltu x24, x27, .Lsk_5
	nop
.Lsk_5:
	slli x26, x15, 3
	addi x16, x18, 1962
	add  x16, x16, x16
	slli x16, x16, 1
	addi x12, x19, 1579
	add  x12, x12, x12
	slli x12, x12, 1
	lw x4, 112(s0)
	flw f1, 84(s0)
	flw f12, 176(s0)
	fmax.s f8, f1, f12
	fsw f8, 176(s0)
	fcvt.w.s x24, f1, rtz
	bge x25, x17, .Lsk_6
	nop
.Lsk_6:
	lhu x23, 120(s0)
	bne x14, x16, .Lsk_7
	nop
.Lsk_7:
	ori x9, x22, 897
	addi x30, x15, 1748
	add  x30, x30, x30
	slli x30, x30, 1
	sll x5, x16, x23
	srli x21, x16, 24
	div x22, x31, x30
	flw f6, 244(s0)
	flw f27, 28(s0)
	fmin.s f24, f6, f27
	fsw f24, 80(s0)
	fcvt.w.s x18, f6, rtz
	rem x30, x16, x15
	addi x9, x4, -1797
	addi x9, x26, -1551
	add  x9, x9, x9
	slli x9, x9, 1
	slli x19, x9, 8
	or x24, x3, x25
	sb x29, 227(s0)
	flw f7, 236(s0)
	flw f9, 120(s0)
	fmax.s f0, f7, f9
	fsw f0, 144(s0)
	fcvt.w.s x18, f7, rtz
	sll x15, x25, x6
	slti x10, x16, -1634
	flw f3, 236(s0)
	flw f11, 172(s0)
	fmax.s f29, f3, f11
	fsw f29, 72(s0)
	fcvt.w.s x5, f3, rtz
	bge x13, x7, .Lsk_8
	nop
.Lsk_8:
	sb x6, 176(s0)
	bltu x21, x13, .Lsk_9
	nop
.Lsk_9:
	srli x10, x24, 29
	flw f19, 140(s0)
	flw f2, 200(s0)
	fmin.s f29, f19, f2
	fsw f29, 204(s0)
	fcvt.w.s x27, f19, rtz
	bne x9, x21, .Lsk_10
	nop
.Lsk_10:
	divu x23, x13, x15
	lui x14, 353557
	sb x5, 254(s0)
	slt x25, x5, x22
	remu x27, x27, x23
	addi x9, x18, 42
	add  x9, x9, x9
	slli x9, x9, 1
	flw f23, 40(s0)
	flw f8, 192(s0)
	fmax.s f22, f23, f8
	fsw f22, 124(s0)
	fcvt.w.s x28, f23, rtz
	xor x21, x27, x9
	sltiu x11, x20, -361
	addi x9, x5, 1357
	add  x9, x9, x9
	slli x9, x9, 1
	flw f11, 36(s0)
	flw f31, 52(s0)
	fadd.s f25, f11, f31
	fsw f25, 164(s0)
	fcvt.w.s x4, f11, rtz
	blt x11, x20, .Lsk_11
	nop
.Lsk_11:
	div x9, x16, x5
	sltu x26, x20, x14
	lw x26, 196(s0)
	addi x5, x20, -111
	add  x5, x5, x5
	slli x5, x5, 1
	addi x17, x17, 316
	add  x17, x17, x17
	slli x17, x17, 1
	mulhsu x18, x23, x14
	sltu x4, x17, x11
	srli x9, x15, 19
	lw x25, 60(s0)
	lh x19, 184(s0)
	ori x23, x27, -103
	bne x4, x13, .Lsk_12
	nop
.Lsk_12:
	or x27, x23, x10
	div x11, x4, x27
	sb x7, 199(s0)
	flw f10, 196(s0)
	flw f6, 208(s0)
	fmin.s f13, f10, f6
	fsw f13, 240(s0)
	fcvt.w.s x26, f10, rtz
	beq x7, x23, .Lsk_13
	nop
.Lsk_13:
	ori x6, x4, -48
	addi x15, x28, -1823
	add  x15, x15, x15
	slli x15, x15, 1
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
	.word 0x14E73FD8
	.word 0xC0E817D8
	.word 0xA7D02FEC
	.word 0xE2C6F779
	.word 0x170EA1AF
	.word 0xE71F6FD7
	.word 0x14079AB7
	.word 0x0F397761
	.word 0x314959EE
	.word 0x81075DB3
	.word 0xE0269CAB
	.word 0xC4042DCC
	.word 0x1D8F020E
	.word 0x709D8EC3
	.word 0xC014F7F7
	.word 0x3B8B984D
	.word 0x97EF8470
	.word 0xB8E5C602
	.word 0xEA128EB4
	.word 0x72D83174
	.word 0xDBE0A472
	.word 0xCA6276E5
	.word 0xAB13B33F
	.word 0x8C1BF862
	.word 0xDA12B8A5
	.word 0x7E0D630B
	.word 0x21635837
	.word 0x791CBEBC
	.word 0x79CEB2CD
	.word 0x407274A9
	.word 0xDC0C4FCF
	.word 0xBE3E965F
	.word 0x61729003
	.word 0x30A5FF95
	.word 0x207FE70E
	.word 0xD288CB5F
	.word 0x54A443B5
	.word 0x6E11C219
	.word 0x40B5BD9D
	.word 0x71C31EF8
	.word 0xBA7A0C07
	.word 0x41442F2E
	.word 0x81E7AD31
	.word 0xA2F3DBF6
	.word 0xF7E24378
	.word 0x3D3F04B0
	.word 0x3C57E4DF
	.word 0x82CE1003
	.word 0x80777374
	.word 0xFC99C7CB
	.word 0x9665626C
	.word 0x08E25C06
	.word 0x86D39BFE
	.word 0x91A66103
	.word 0x086C37B5
	.word 0x53ABAE45
	.word 0xDDA0FF52
	.word 0xED88655E
	.word 0x80DDB133
	.word 0x13014E5E
	.word 0x2B169C37
	.word 0xD108663E
	.word 0xAA5D2C59
	.word 0x3D29376A
