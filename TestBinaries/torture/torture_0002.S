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
	mulhu x7, x10, x27
	div x20, x26, x12
	slli x4, x12, 28
	srli x10, x22, 13
	blt x14, x13, .Lsk_0
	nop
.Lsk_0:
	addi x11, x24, -1692
	add  x11, x11, x11
	slli x11, x11, 1
	lb x28, 250(s0)
	addi x26, x21, 780
	add  x26, x26, x26
	slli x26, x26, 1
	remu x24, x22, x21
	addi x7, x16, 1255
	add  x7, x7, x7
	slli x7, x7, 1
	bne x5, x11, .Lsk_1
	nop
.Lsk_1:
	bge x10, x22, .Lsk_2
	nop
.Lsk_2:
	lbu x25, 1(s0)
	andi x5, x9, -183
	beq x28, x10, .Lsk_3
	nop
.Lsk_3:
	addi x27, x18, 1945
	add  x27, x27, x27
	slli x27, x27, 1
	addi x31, x3, 1105
	add  x31, x31, x31
	slli x31, x31, 1
	addi x12, x5, 22
	add  x12, x12, x12
	slli x12, x12, 1
	mul x3, x29, x15
	mul x14, x16, x11
	rem x20, x18, x7
	auipc x11, 353312
	addi x24, x17, -30
	add  x24, x24, x24
	slli x24, x24, 1
	flw f13, 152(s0)
	flw f17, 60(s0)
	fsub.s f9, f13, f17
	fsw f9, 8(s0)
	fcvt.w.s x11, f13, rtz
	xori x14, x28, 58
	ori x13, x23, 1732
	div x14, x12, x27
	lw x13, 28(s0)
	srai x24, x20, 31
	sh x18, 146(s0)
	lui x24, 413468
	flw f16, 220(s0)
	flw f14, 108(s0)
	fmax.s f8, f16, f14
	fsw f8, 248(s0)
	fcvt.w.s x26, f16, rtz
	lbu x24, 134(s0)
	sb x23, 131(s0)
	lh x5, 214(s0)
	sh x23, 116(s0)
	addi x18, x26, 140
	add  x18, x18, x18
	slli x18, x18, 1
	addi x27, x7, 769
	add  x27, x27, x27
	slli x27, x27, 1
	addi x7, x9, 1801
	add  x7, x7, x7
	slli x7, x7, 1
	flw f18, 104(s0)
	flw f1, 192(s0)
	fadd.s f3, f18, f1
	fsw f3, 100(s0)
	fcvt.w.s x31, f18, rtz
	sw x19, 112(s0)
	sra x17, x14, x21
	sra x10, x16, x18
	xori x3, x12, 621
	lui x20, 413077
	lui x23, 76138
	mulhu x5, x19, x15
	xori x14, x16, 2
	slli x12, x18, 0
	lhu x24, 90(s0)
	addi x5, x28, 40
	add  x5, x5, x5
	slli x5, x5, 1
	sb x6, 141(s0)
	addi x13, x6, 6
	add  x13, x13, x13
	slli x13, x13, 1
	addi x26, x14, 114
	add  x26, x26, x26
	slli x26, x26, 1
	sltu x24, x24, x23
	srl x13, x14, x5
	sll x22, x19, x9
	flw f27, 20(s0)
	flw f20, 116(s0)
	fmul.s f20, f27, f20
	fsw f20, 168(s0)
	fcvt.w.s x3, f27, rtz
	sltu x12, x24, x18
	auipc x7, 875178
	lhu x9, 176(s0)
	remu x10, x17, x28
	mulhu x20, x10, x4
	flw f6, 208(s0)
	flw f25, 88(s0)
	fmul.s f13, f6, f25
	fsw f13, 200(s0)
	fcvt.w.s x3, f6, rtz
	srai x14, x28, 29
	lhu x22, 148(s0)
	flw f16, 184(s0)
	flw f18, 204(s0)
	fsub.s f23, f16, f18
	fsw f23, 152(s0)
	fcvt.w.s x13, f16, rtz
	lh x12, 78(s0)
	bne x9, x20, .Lsk_4
	nop
.Lsk_4:
	addi x14, x14, -1660
	add  x14, x14, x14
	slli x14, x14, 1
	flw f7, 168(s0)
	flw f21, 240(s0)
	fmul.s f20, f7, f21
	fsw f20, 0(s0)
	fcvt.w.s x18, f7, rtz
	bltu x9, x4, .Lsk_5
	nop
.Lsk_5:
	addi x3, x30, -1142
	add  x3, x3, x3
	slli x3, x3, 1
	lui x10, 335564
	divu x20, x13, x21
	flw f28, 104(s0)
	flw f30, 132(s0)
	fsub.s f21, f28, f30
	fsw f21, 0(s0)
	fcvt.w.s x31, f28, rtz
	addi x10, x15, 507
	add  x10, x10, x10
	slli x10, x10, 1
	sltiu x29, x31, 88
	rem x13, x20, x17
	sra x15, x4, x9
	sb x30, 251(s0)
	flw f1, 52(s0)
	flw f11, 208(s0)
	fsub.s f25, f1, f11
	fsw f25, 44(s0)
	fcvt.w.s x31, f1, rtz
	add x6, x30, x13
	sh x22, 248(s0)
	sll x24, x13, x22
	addi x6, x24, -909
	add  x6, x6, x6
	slli x6, x6, 1
	addi x25, x20, 1123
	add  x25, x25, x25
	slli x25, x25, 1
	slli x17, x10, 2
	lbu x28, 220(s0)
	flw f4, 180(s0)
	flw f20, 232(s0)
	fsub.s f14, f4, f20
	fsw f14, 236(s0)
	fcvt.w.s x25, f4, rtz
	lbu x30, 124(s0)
	lbu x7, 121(s0)
	xori x22, x14, -1430
	and x15, x27, x12
	lbu x16, 34(s0)
	sh x6, 170(s0)
	mulhu x22, x16, x26
	mulh x31, x13, x28
	lbu x15, 232(s0)
	mulhsu x17, x13, x4
	lb x28, 6(s0)
	auipc x12, 914060
	sb x20, 137(s0)
	addi x27, x21, -1004
	add  x27, x27, x27
	slli x27, x27, 1
	addi x11, x24, -1899
	add  x11, x11, x11
	slli x11, x11, 1
	slli x20, x11, 8
	xor x11, x24, x28
	addi x6, x20, 840
	add  x6, x6, x6
	slli x6, x6, 1
	addi x25, x22, -1638
	add  x25, x25, x25
	slli x25, x25, 1
	bne x12, x18, .Lsk_6
	nop
.Lsk_6:
	bge x3, x16, .Lsk_7
	nop
.Lsk_7:
	lbu x12, 23(s0)
	flw f25, 184(s0)
	flw f5, 172(s0)
	fmax.s f10, f25, f5
	fsw f10, 216(s0)
	fcvt.w.s x25, f25, rtz
	rem x9, x24, x23
	flw f6, 0(s0)
	flw f11, 236(s0)
	fmul.s f14, f6, f11
	fsw f14, 40(s0)
	fcvt.w.s x25, f6, rtz
	sb x14, 3(s0)
	flw f3, 196(s0)
	flw f1, 96(s0)
	fmin.s f11, f3, f1
	fsw f11, 44(s0)
	fcvt.w.s x21, f3, rtz
	lb x10, 189(s0)
	sh x24, 244(s0)
	lbu x15, 117(s0)
	flw f14, 112(s0)
	flw f8, 16(s0)
	fmax.s f28, f14, f8
	fsw f28, 88(s0)
	fcvt.w.s x10, f14, rtz
	addi x11, x11, 281
	add  x11, x11, x11
	slli x11, x11, 1
	flw f19, 108(s0)
	flw f31, 164(s0)
	fmul.s f19, f19, f31
	fsw f19, 64(s0)
	fcvt.w.s x26, f19, rtz
	or x3, x17, x29
	lw x22, 224(s0)
	xori x23, x18, 647
	lhu x9, 0(s0)
	flw f10, 176(s0)
	flw f20, 72(s0)
	fmin.s f29, f10, f20
	fsw f29, 192(s0)
	fcvt.w.s x23, f10, rtz
	blt x30, x23, .Lsk_8
	nop
.Lsk_8:
	sw x5, 120(s0)
	flw f0, 204(s0)
	flw f31, 144(s0)
	fadd.s f3, f0, f31
	fsw f3, 168(s0)
	fcvt.w.s x24, f0, rtz
	lb x20, 162(s0)
	addi x3, x3, 963
	add  x3, x3, x3
	slli x3, x3, 1
	addi x10, x29, 1367
	add  x10, x10, x10
	slli x10, x10, 1
	sw x27, 132(s0)
	bge x11, x4, .Lsk_9
	nop
.Lsk_9:
	flw f9, 136(s0)
	flw f29, 216(s0)
	fadd.s f15, f9, f29
	fsw f15, 180(s0)
	fcvt.w.s x26, f9, rtz
	sub x21, x7, x7
	addi x9, x26, 512
	add  x9, x9, x9
	slli x9, x9, 1
	bltu x12, x13, .Lsk_10
	nop
.Lsk_10:
	xori x16, x7, 1589
	sra x4, x5, x25
	slli x12, x27, 14
	divu x11, x4, x19
	lui x16, 616508
	srli x25, x16, 9
	addi x31, x27, 1249
	add  x31, x31, x31
	slli x31, x31, 1
	lh x12, 96(s0)
	bgeu x11, x20, .Lsk_11
	nop
.Lsk_11:
	or x4, x14, x12
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
	.word 0x6D2BFA4F
	.word 0x354B9845
	.word 0x1C305D82
	.word 0xB6156021
	.word 0x8C5E22B6
	.word 0xDCF5BFF5
	.word 0xD27A5F75
	.word 0x0937B5B6
	.word 0xF7569EE1
	.word 0x1AE183F4
	.word 0x5DF1679F
	.word 0xA0B49D0D
	.word 0x99C0CFF9
	.word 0x895110C3
	.word 0x9FD49E94
	.word 0x52A83DA2
	.word 0xA161A136
	.word 0xACEB4161
	.word 0x81CE6180
	.word 0x7A71DA0E
	.word 0xFDC2B967
	.word 0x211CA1D0
	.word 0x36231283
	.word 0xDCD8AA85
	.word 0x900B36CE
	.word 0xA18B81F7
	.word 0x246F71B6
	.word 0x15F01AEE
	.word 0x3D3A8E73
	.word 0xA0503EEC
	.word 0x15910675
	.word 0xB46BCB3E
	.word 0x372878B6
	.word 0xC4DC6C57
	.word 0x74392CF6
	.word 0x1059A11B
	.word 0xFB8221F1
	.word 0xFB663B27
	.word 0xDBC256ED
	.word 0xE341443F
	.word 0xB65E14E1
	.word 0xEF49D424
	.word 0x762BB521
	.word 0x33EFFFC5
	.word 0x4461EFB5
	.word 0x294E04DC
	.word 0xBE0392BB
	.word 0xB6ACF3A4
	.word 0xD9B64E5D
	.word 0x225EC585
	.word 0xB977A5E0
	.word 0x39C69D17
	.word 0xF87592F4
	.word 0xDB28816A
	.word 0x0315DC64
	.word 0x6CEF1E7D
	.word 0xD4B8A11F
	.word 0xA783C83C
	.word 0x095F5BFC
	.word 0x7AB08C8B
	.word 0x053228C5
	.word 0xD53E0B9E
	.word 0x7629DD15
	.word 0xDD832355
