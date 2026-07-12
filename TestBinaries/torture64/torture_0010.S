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
	flw f7, 4(s0)
	flw f26, 252(s0)
	fmul.s f9, f7, f26
	fsw f9, 244(s0)
	fcvt.w.s x18, f7, rtz
	lui x31, 945533
	auipc x15, 608391
	fld f20, 56(s0)
	fld f21, 192(s0)
	fsub.d f4, f20, f21
	fsd f4, 96(s0)
	fcvt.l.d x14, f20, rtz
	lh x15, 12(s0)
	mulhu x28, x20, x12
	lw x4, 192(s0)
	addi x10, x28, -1455
	add  x10, x10, x10
	slli x10, x10, 1
	flw f15, 84(s0)
	flw f19, 60(s0)
	fmax.s f13, f15, f19
	fsw f13, 136(s0)
	fcvt.w.s x29, f15, rtz
	addi x21, x13, 1261
	add  x21, x21, x21
	slli x21, x21, 1
	addi x18, x20, -789
	add  x18, x18, x18
	slli x18, x18, 1
	srli x16, x16, 38
	sh x25, 224(s0)
	lwu x18, 148(s0)
	sb x10, 255(s0)
	divw x10, x19, x22
	srlw x4, x20, x24
	ld x7, 96(s0)
	lbu x5, 167(s0)
	lb x18, 42(s0)
	flw f16, 0(s0)
	flw f11, 16(s0)
	fadd.s f9, f16, f11
	fsw f9, 80(s0)
	fcvt.w.s x6, f16, rtz
	bge x30, x15, .Lsk_0
	nop
.Lsk_0:
	srl x14, x6, x9
	addi x7, x4, -173
	add  x7, x7, x7
	slli x7, x7, 1
	sw x28, 16(s0)
	lui x23, 235130
	fld f15, 168(s0)
	fld f10, 0(s0)
	fsub.d f17, f15, f10
	fsd f17, 176(s0)
	fcvt.l.d x14, f15, rtz
	bltu x9, x18, .Lsk_1
	nop
.Lsk_1:
	lwu x4, 252(s0)
	or x6, x9, x23
	flw f12, 224(s0)
	flw f29, 120(s0)
	fmul.s f14, f12, f29
	fsw f14, 80(s0)
	fcvt.w.s x3, f12, rtz
	lwu x21, 128(s0)
	flw f23, 16(s0)
	flw f31, 32(s0)
	fmax.s f30, f23, f31
	fsw f30, 156(s0)
	fcvt.w.s x13, f23, rtz
	addi x15, x17, -1314
	add  x15, x15, x15
	slli x15, x15, 1
	or x10, x14, x29
	slti x5, x26, -484
	sw x19, 176(s0)
	addi x15, x10, 1668
	add  x15, x15, x15
	slli x15, x15, 1
	xori x25, x28, 404
	ori x12, x4, -1570
	remw x27, x10, x10
	lhu x10, 196(s0)
	sw x30, 216(s0)
	sb x22, 216(s0)
	sb x27, 170(s0)
	fld f16, 8(s0)
	fld f13, 88(s0)
	fadd.d f5, f16, f13
	fsd f5, 120(s0)
	fcvt.l.d x21, f16, rtz
	sh x19, 138(s0)
	srai x5, x27, 16
	addi x26, x9, -817
	add  x26, x26, x26
	slli x26, x26, 1
	remu x25, x15, x26
	lhu x14, 16(s0)
	fld f29, 104(s0)
	fld f7, 80(s0)
	fadd.d f12, f29, f7
	fsd f12, 136(s0)
	fcvt.l.d x9, f29, rtz
	addiw x18, x14, -384
	addi x20, x28, 384
	add  x20, x20, x20
	slli x20, x20, 1
	srlw x25, x25, x15
	addi x23, x17, -1685
	sd x22, 104(s0)
	lwu x25, 68(s0)
	or x6, x6, x21
	flw f20, 176(s0)
	flw f26, 240(s0)
	fmax.s f24, f20, f26
	fsw f24, 196(s0)
	fcvt.w.s x9, f20, rtz
	addi x17, x6, -1125
	add  x17, x17, x17
	slli x17, x17, 1
	mulhsu x31, x11, x4
	beq x26, x3, .Lsk_2
	nop
.Lsk_2:
	divuw x6, x20, x27
	addi x12, x28, -691
	add  x12, x12, x12
	slli x12, x12, 1
	flw f1, 48(s0)
	flw f23, 164(s0)
	fmin.s f4, f1, f23
	fsw f4, 140(s0)
	fcvt.w.s x20, f1, rtz
	mulhu x25, x16, x9
	sd x7, 32(s0)
	srli x7, x30, 3
	blt x10, x3, .Lsk_3
	nop
.Lsk_3:
	mulh x29, x26, x17
	lwu x28, 220(s0)
	beq x19, x11, .Lsk_4
	nop
.Lsk_4:
	addi x13, x25, -1309
	add  x13, x13, x13
	slli x13, x13, 1
	add x26, x6, x27
	sw x25, 80(s0)
	sh x22, 246(s0)
	flw f10, 120(s0)
	flw f21, 236(s0)
	fmin.s f4, f10, f21
	fsw f4, 0(s0)
	fcvt.w.s x27, f10, rtz
	subw x22, x19, x24
	ld x9, 32(s0)
	remw x30, x25, x15
	div x21, x15, x12
	addi x10, x6, 297
	add  x10, x10, x10
	slli x10, x10, 1
	addi x18, x16, 51
	add  x18, x18, x18
	slli x18, x18, 1
	addi x21, x29, -439
	add  x21, x21, x21
	slli x21, x21, 1
	addi x16, x28, -1139
	add  x16, x16, x16
	slli x16, x16, 1
	remw x18, x23, x3
	srliw x18, x16, 30
	sh x5, 84(s0)
	divw x15, x7, x23
	addw x15, x26, x26
	addi x20, x30, -56
	add  x20, x20, x20
	slli x20, x20, 1
	divuw x10, x16, x19
	bge x17, x26, .Lsk_5
	nop
.Lsk_5:
	addi x4, x11, -477
	slli x28, x20, 44
	addiw x25, x20, 1498
	lhu x23, 120(s0)
	bne x14, x16, .Lsk_6
	nop
.Lsk_6:
	ori x9, x22, 897
	addi x30, x15, 1748
	add  x30, x30, x30
	slli x30, x30, 1
	mulhsu x5, x16, x23
	srli x21, x16, 49
	div x22, x31, x30
	flw f6, 28(s0)
	flw f27, 80(s0)
	fsub.s f24, f6, f27
	fsw f24, 220(s0)
	fcvt.w.s x18, f6, rtz
	addi x16, x17, -677
	add  x16, x16, x16
	slli x16, x16, 1
	lh x19, 90(s0)
	sub x27, x4, x9
	bgeu x4, x4, .Lsk_7
	nop
.Lsk_7:
	flw f10, 12(s0)
	flw f30, 68(s0)
	fmax.s f4, f10, f30
	fsw f4, 248(s0)
	fcvt.w.s x11, f10, rtz
	bltu x29, x18, .Lsk_8
	nop
.Lsk_8:
	flw f7, 144(s0)
	flw f9, 64(s0)
	fmul.s f0, f7, f9
	fsw f0, 148(s0)
	fcvt.w.s x18, f7, rtz
	bgeu x7, x6, .Lsk_9
	nop
.Lsk_9:
	slti x10, x16, -1634
	flw f3, 172(s0)
	flw f11, 72(s0)
	fmul.s f29, f3, f11
	fsw f29, 244(s0)
	fcvt.w.s x5, f3, rtz
	addi x3, x31, 776
	bltu x21, x13, .Lsk_10
	nop
.Lsk_10:
	srli x10, x24, 58
	fld f19, 200(s0)
	fld f2, 200(s0)
	fsub.d f29, f19, f2
	fsd f29, 200(s0)
	fcvt.l.d x27, f19, rtz
	srliw x13, x4, 26
	sb x15, 103(s0)
	and x14, x17, x28
	sd x3, 32(s0)
	rem x10, x22, x3
	bge x23, x31, .Lsk_11
	nop
.Lsk_11:
	addi x18, x31, 42
	add  x18, x18, x18
	slli x18, x18, 1
	flw f23, 192(s0)
	flw f8, 124(s0)
	fsub.s f22, f23, f8
	fsw f22, 8(s0)
	fcvt.w.s x28, f23, rtz
	sh x15, 80(s0)
	srliw x11, x20, 13
	addi x9, x5, 1357
	add  x9, x9, x9
	slli x9, x9, 1
	flw f11, 52(s0)
	flw f31, 164(s0)
	fmul.s f25, f11, f31
	fsw f25, 28(s0)
	fcvt.w.s x4, f11, rtz
	srl x15, x9, x22
	beq x13, x30, .Lsk_12
	nop
.Lsk_12:
	bne x23, x13, .Lsk_13
	nop
.Lsk_13:
	srliw x28, x6, 5
	lbu x17, 222(s0)
	sb x10, 225(s0)
	sh x22, 68(s0)
	srlw x26, x4, x13
	subw x10, x9, x12
	slliw x25, x4, 29
	addw x11, x19, x23
	bge x23, x11, .Lsk_14
	nop
.Lsk_14:
	bne x4, x13, .Lsk_15
	nop
.Lsk_15:
	or x27, x23, x10
	divuw x11, x4, x27
	sh x22, 198(s0)
	flw f10, 240(s0)
	flw f6, 212(s0)
	fadd.s f13, f10, f6
	fsw f13, 88(s0)
	fcvt.w.s x26, f10, rtz
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
	.word 0x1BB936EB
	.word 0xA6D5BB15
	.word 0x82BBF36E
	.word 0xE40A1422
	.word 0xF99B464E
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
