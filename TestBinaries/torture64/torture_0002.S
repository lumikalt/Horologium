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
	subw x7, x10, x27
	sraw x20, x26, x25
	or x12, x27, x14
	sraw x18, x10, x21
	srliw x10, x25, 18
	addi x11, x24, -1692
	add  x11, x11, x11
	slli x11, x11, 1
	lb x28, 250(s0)
	addi x26, x21, 780
	add  x26, x26, x26
	slli x26, x26, 1
	srlw x24, x22, x21
	addi x7, x16, 1255
	add  x7, x7, x7
	slli x7, x7, 1
	bne x5, x11, .Lsk_0
	nop
.Lsk_0:
	bge x10, x22, .Lsk_1
	nop
.Lsk_1:
	lbu x25, 1(s0)
	sraiw x5, x9, 14
	beq x28, x10, .Lsk_2
	nop
.Lsk_2:
	addi x27, x18, 1945
	add  x27, x27, x27
	slli x27, x27, 1
	addi x31, x3, 1105
	add  x31, x31, x31
	slli x31, x31, 1
	addi x12, x5, 22
	add  x12, x12, x12
	slli x12, x12, 1
	addw x3, x29, x15
	addw x14, x16, x11
	srlw x20, x18, x7
	addiw x11, x20, 1725
	addi x20, x24, 1456
	add  x20, x20, x20
	slli x20, x20, 1
	mulhsu x28, x10, x11
	sb x6, 11(s0)
	addi x10, x15, -1577
	add  x10, x10, x10
	slli x10, x10, 1
	addi x28, x26, -1122
	add  x28, x28, x28
	slli x28, x28, 1
	flw f30, 236(s0)
	flw f16, 16(s0)
	fmul.s f3, f30, f16
	fsw f3, 128(s0)
	fcvt.w.s x22, f30, rtz
	addiw x4, x22, 71
	bne x5, x13, .Lsk_3
	nop
.Lsk_3:
	srai x3, x24, 62
	sd x18, 144(s0)
	addiw x24, x11, -951
	fld f16, 216(s0)
	fld f14, 104(s0)
	fmax.d f8, f16, f14
	fsd f8, 248(s0)
	fcvt.l.d x26, f16, rtz
	lbu x24, 134(s0)
	sb x16, 247(s0)
	mulhu x17, x23, x9
	lbu x23, 136(s0)
	addi x27, x7, 769
	add  x27, x27, x27
	slli x27, x27, 1
	addi x7, x9, 1801
	add  x7, x7, x7
	slli x7, x7, 1
	flw f18, 192(s0)
	flw f1, 100(s0)
	fmax.s f3, f18, f1
	fsw f3, 0(s0)
	fcvt.w.s x31, f18, rtz
	lhu x11, 114(s0)
	ori x14, x12, -927
	srli x16, x7, 57
	srliw x3, x12, 20
	addiw x20, x25, -1806
	remu x18, x23, x11
	addi x21, x4, -1851
	add  x21, x21, x21
	slli x21, x21, 1
	addi x19, x16, 209
	add  x19, x19, x19
	slli x19, x19, 1
	auipc x7, 835801
	divw x12, x18, x3
	lhu x24, 90(s0)
	addi x5, x28, 40
	add  x5, x5, x5
	slli x5, x5, 1
	sw x12, 152(s0)
	srliw x31, x27, 15
	sraw x24, x3, x15
	lb x6, 192(s0)
	divw x15, x22, x9
	fld f27, 16(s0)
	fld f20, 112(s0)
	fmul.d f20, f27, f20
	fsd f20, 168(s0)
	fcvt.l.d x3, f27, rtz
	sltu x12, x24, x18
	auipc x7, 875178
	lhu x9, 176(s0)
	srlw x10, x17, x28
	mulhu x20, x10, x4
	flw f6, 200(s0)
	flw f25, 148(s0)
	fmul.s f13, f6, f25
	fsw f13, 80(s0)
	fcvt.w.s x3, f6, rtz
	addi x28, x20, 543
	add  x28, x28, x28
	slli x28, x28, 1
	sd x19, 144(s0)
	lb x30, 72(s0)
	auipc x26, 625851
	sltiu x16, x27, -317
	lh x12, 78(s0)
	bne x9, x20, .Lsk_4
	nop
.Lsk_4:
	addi x14, x14, -1660
	add  x14, x14, x14
	slli x14, x14, 1
	flw f7, 240(s0)
	flw f21, 0(s0)
	fmul.s f20, f7, f21
	fsw f20, 184(s0)
	fcvt.w.s x18, f7, rtz
	addi x9, x4, -328
	add  x9, x9, x9
	slli x9, x9, 1
	sub x23, x26, x10
	flw f8, 148(s0)
	flw f15, 228(s0)
	fmul.s f10, f8, f15
	fsw f10, 156(s0)
	fcvt.w.s x15, f8, rtz
	sh x24, 224(s0)
	lwu x26, 168(s0)
	addi x29, x25, 68
	add  x29, x29, x29
	slli x29, x29, 1
	auipc x9, 654306
	sltiu x29, x31, 88
	remw x13, x20, x17
	sra x15, x4, x9
	sd x28, 8(s0)
	ori x31, x28, -1162
	beq x31, x9, .Lsk_5
	nop
.Lsk_5:
	add x6, x30, x13
	sd x22, 248(s0)
	mulhsu x24, x13, x22
	addi x6, x24, -909
	add  x6, x6, x6
	slli x6, x6, 1
	addi x25, x20, 1123
	add  x25, x25, x25
	slli x25, x25, 1
	slli x17, x10, 4
	lbu x28, 220(s0)
	flw f4, 232(s0)
	flw f20, 236(s0)
	fmin.s f14, f4, f20
	fsw f14, 84(s0)
	fcvt.w.s x25, f4, rtz
	lbu x30, 124(s0)
	lwu x24, 76(s0)
	remu x23, x22, x5
	div x15, x27, x16
	or x23, x16, x29
	lwu x24, 168(s0)
	sraw x22, x16, x11
	bne x31, x13, .Lsk_6
	nop
.Lsk_6:
	lw x28, 184(s0)
	lb x17, 155(s0)
	divuw x9, x4, x28
	auipc x4, 914060
	sb x28, 65(s0)
	addi x11, x24, -1899
	add  x11, x11, x11
	slli x11, x11, 1
	slli x20, x11, 17
	xor x11, x24, x28
	addi x6, x20, 840
	add  x6, x6, x6
	slli x6, x6, 1
	addi x25, x22, -1638
	add  x25, x25, x25
	slli x25, x25, 1
	bne x12, x18, .Lsk_7
	nop
.Lsk_7:
	bge x3, x16, .Lsk_8
	nop
.Lsk_8:
	lbu x12, 23(s0)
	fld f25, 184(s0)
	fld f5, 168(s0)
	fmax.d f10, f25, f5
	fsd f10, 216(s0)
	fcvt.l.d x25, f25, rtz
	remw x9, x24, x23
	fld f6, 0(s0)
	fld f11, 232(s0)
	fmul.d f14, f6, f11
	fsd f14, 40(s0)
	fcvt.l.d x25, f6, rtz
	sb x20, 3(s0)
	fld f3, 192(s0)
	fld f1, 96(s0)
	fmin.d f11, f3, f1
	fsd f11, 40(s0)
	fcvt.l.d x21, f3, rtz
	lwu x25, 184(s0)
	andi x15, x22, 1858
	lbu x15, 117(s0)
	flw f14, 16(s0)
	flw f8, 88(s0)
	fsub.s f28, f14, f8
	fsw f28, 120(s0)
	fcvt.w.s x10, f14, rtz
	addi x13, x28, 333
	add  x13, x13, x13
	slli x13, x13, 1
	addi x23, x19, 474
	add  x23, x23, x23
	slli x23, x23, 1
	blt x21, x7, .Lsk_9
	nop
.Lsk_9:
	sraw x29, x3, x29
	lw x22, 224(s0)
	xori x23, x18, 647
	lhu x9, 0(s0)
	fld f10, 176(s0)
	fld f20, 72(s0)
	fmin.d f29, f10, f20
	fsd f29, 192(s0)
	fcvt.l.d x23, f10, rtz
	blt x30, x23, .Lsk_10
	nop
.Lsk_10:
	sh x5, 122(s0)
	flw f0, 168(s0)
	flw f31, 12(s0)
	fmul.s f3, f0, f31
	fsw f3, 248(s0)
	fcvt.w.s x24, f0, rtz
	lb x3, 12(s0)
	sb x31, 148(s0)
	andi x17, x22, 71
	bge x11, x4, .Lsk_11
	nop
.Lsk_11:
	fld f9, 136(s0)
	fld f29, 216(s0)
	fadd.d f15, f9, f29
	fsd f15, 176(s0)
	fcvt.l.d x26, f9, rtz
	sub x21, x7, x7
	addi x9, x26, 512
	add  x9, x9, x9
	slli x9, x9, 1
	bltu x12, x13, .Lsk_12
	nop
.Lsk_12:
	xori x16, x7, 1589
	remu x4, x5, x25
	slli x12, x27, 29
	sllw x11, x4, x19
	andi x16, x30, -962
	srlw x18, x25, x7
	addi x31, x27, 1249
	add  x31, x31, x31
	slli x31, x31, 1
	lh x12, 96(s0)
	bgeu x11, x20, .Lsk_13
	nop
.Lsk_13:
	or x4, x14, x12
	lhu x21, 106(s0)
	lw x30, 68(s0)
	lbu x19, 174(s0)
	addi x11, x5, -207
	add  x11, x11, x11
	slli x11, x11, 1
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
	.word 0x3A8B005B
	.word 0xC9091AAB
	.word 0x78BCA3D3
	.word 0x69AABEA1
	.word 0x2C03549E
	.word 0xE5ED37DF
	.word 0x919E34DF
	.word 0xF197572C
	.word 0xA0DDAC28
	.word 0x82815A70
	.word 0xE2FF85BF
	.word 0x494E29AA
	.word 0x02A3B09A
	.word 0x342ED680
	.word 0x8C585D92
	.word 0x0BC9B723
	.word 0x23DBC5D1
	.word 0xA6DEC4CA
	.word 0xC6CB28B9
	.word 0x1621840C
	.word 0xF5B07F55
	.word 0xD07ADDD9
	.word 0xEF04B0E9
	.word 0x208F41A9
	.word 0x5ADFA157
	.word 0x1207BCBA
	.word 0x5D7AFE2E
	.word 0x33A5AA79
	.word 0x11B8AC71
	.word 0x18B7BD7C
	.word 0x722C09F0
	.word 0xE30E95E0
	.word 0x6016420F
	.word 0x45BA3ABD
	.word 0x4F85A993
	.word 0x957EF4F2
	.word 0xF82622EF
	.word 0x9E281690
	.word 0xE9BB7879
	.word 0x95E6B431
	.word 0xEC837978
	.word 0x16E713BB
	.word 0x43F2A68F
	.word 0xBFC14198
	.word 0xDEF21D8D
	.word 0xBCD0DD0B
	.word 0x589A5009
	.word 0x2EE47741
	.word 0x59155D3D
	.word 0xA339863C
	.word 0x29CDF344
	.word 0x611FFCBE
	.word 0x0D46DF70
	.word 0x8EB26070
	.word 0x5F293446
	.word 0xD1303DC5
	.word 0x4364A7F1
	.word 0xC80990E3
	.word 0xA8E0B56D
	.word 0xE89682C8
	.word 0xD819328A
	.word 0xB46196B3
	.word 0xC7F20D6E
	.word 0x2B0EB859
