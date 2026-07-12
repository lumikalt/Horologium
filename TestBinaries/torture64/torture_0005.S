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
	addi x3, x9, -402
	beq x9, x6, .Lsk_0
	nop
.Lsk_0:
	lh x20, 252(s0)
	srlw x18, x25, x7
	blt x15, x27, .Lsk_1
	nop
.Lsk_1:
	and x29, x27, x30
	mul x3, x21, x10
	flw f22, 140(s0)
	flw f16, 108(s0)
	fmin.s f7, f22, f16
	fsw f7, 204(s0)
	fcvt.w.s x24, f22, rtz
	mulhsu x17, x18, x7
	sh x6, 204(s0)
	sw x20, 64(s0)
	auipc x6, 235690
	sd x26, 24(s0)
	remu x24, x12, x27
	bgeu x15, x26, .Lsk_2
	nop
.Lsk_2:
	lbu x3, 118(s0)
	auipc x4, 43354
	blt x16, x14, .Lsk_3
	nop
.Lsk_3:
	slli x18, x7, 52
	lhu x6, 12(s0)
	mulhu x26, x17, x14
	bne x11, x28, .Lsk_4
	nop
.Lsk_4:
	sw x13, 100(s0)
	srai x20, x29, 51
	remw x21, x6, x18
	add x4, x29, x3
	xor x12, x23, x5
	lh x9, 166(s0)
	bgeu x31, x26, .Lsk_5
	nop
.Lsk_5:
	flw f8, 76(s0)
	flw f20, 192(s0)
	fmul.s f11, f8, f20
	fsw f11, 12(s0)
	fcvt.w.s x14, f8, rtz
	addi x3, x27, 1492
	add  x3, x3, x3
	slli x3, x3, 1
	sltiu x3, x21, -2045
	flw f29, 28(s0)
	flw f5, 244(s0)
	fmin.s f15, f29, f5
	fsw f15, 16(s0)
	fcvt.w.s x7, f29, rtz
	lwu x11, 72(s0)
	sw x23, 80(s0)
	auipc x4, 516851
	lwu x31, 116(s0)
	mulw x29, x3, x15
	mulhu x26, x30, x23
	addiw x4, x13, -893
	lwu x3, 132(s0)
	lwu x28, 192(s0)
	auipc x3, 875386
	blt x20, x26, .Lsk_6
	nop
.Lsk_6:
	flw f19, 32(s0)
	flw f21, 36(s0)
	fsub.s f28, f19, f21
	fsw f28, 192(s0)
	fcvt.w.s x12, f19, rtz
	sb x21, 85(s0)
	sw x28, 44(s0)
	addi x31, x23, 1575
	add  x31, x31, x31
	slli x31, x31, 1
	bge x16, x23, .Lsk_7
	nop
.Lsk_7:
	bltu x24, x20, .Lsk_8
	nop
.Lsk_8:
	sub x17, x24, x20
	sraw x3, x18, x13
	divu x28, x12, x28
	sw x21, 108(s0)
	addi x25, x3, -92
	add  x25, x25, x25
	slli x25, x25, 1
	ld x19, 136(s0)
	sltiu x27, x14, 1981
	flw f5, 24(s0)
	flw f9, 252(s0)
	fmax.s f27, f5, f9
	fsw f27, 152(s0)
	fcvt.w.s x24, f5, rtz
	sra x18, x6, x26
	slti x18, x13, -443
	sd x31, 96(s0)
	fld f19, 120(s0)
	fld f15, 80(s0)
	fmax.d f2, f19, f15
	fsd f2, 40(s0)
	fcvt.l.d x12, f19, rtz
	lhu x11, 202(s0)
	lbu x22, 41(s0)
	lwu x29, 76(s0)
	addi x3, x7, -390
	add  x3, x3, x3
	slli x3, x3, 1
	blt x24, x20, .Lsk_9
	nop
.Lsk_9:
	flw f25, 44(s0)
	flw f5, 232(s0)
	fadd.s f29, f25, f5
	fsw f29, 192(s0)
	fcvt.w.s x20, f25, rtz
	sllw x11, x26, x11
	mulhu x19, x29, x4
	blt x20, x15, .Lsk_10
	nop
.Lsk_10:
	ld x5, 72(s0)
	blt x13, x25, .Lsk_11
	nop
.Lsk_11:
	mulhsu x25, x7, x13
	addi x17, x10, -1102
	add  x17, x17, x17
	slli x17, x17, 1
	srai x22, x27, 20
	or x22, x11, x31
	lh x5, 42(s0)
	sb x13, 193(s0)
	bltu x26, x26, .Lsk_12
	nop
.Lsk_12:
	sd x20, 208(s0)
	sltiu x14, x17, -663
	addi x27, x30, -896
	add  x27, x27, x27
	slli x27, x27, 1
	lb x6, 118(s0)
	flw f21, 88(s0)
	flw f23, 12(s0)
	fsub.s f23, f21, f23
	fsw f23, 36(s0)
	fcvt.w.s x24, f21, rtz
	sra x31, x24, x4
	sltu x18, x9, x25
	mul x11, x11, x24
	sllw x9, x6, x14
	addi x3, x25, -1558
	add  x3, x3, x3
	slli x3, x3, 1
	blt x17, x15, .Lsk_13
	nop
.Lsk_13:
	bge x29, x15, .Lsk_14
	nop
.Lsk_14:
	sd x16, 96(s0)
	slti x26, x30, 990
	add x22, x9, x18
	flw f13, 32(s0)
	flw f22, 72(s0)
	fmax.s f21, f13, f22
	fsw f21, 56(s0)
	fcvt.w.s x12, f13, rtz
	lb x20, 254(s0)
	sraiw x29, x16, 29
	remuw x24, x22, x20
	fld f14, 48(s0)
	fld f29, 144(s0)
	fadd.d f24, f14, f29
	fsd f24, 72(s0)
	fcvt.l.d x6, f14, rtz
	rem x13, x12, x18
	addi x26, x24, 980
	lwu x20, 100(s0)
	addi x20, x5, -2013
	add  x20, x20, x20
	slli x20, x20, 1
	auipc x10, 907643
	lhu x14, 124(s0)
	sraiw x5, x22, 16
	lbu x25, 161(s0)
	flw f23, 176(s0)
	flw f6, 148(s0)
	fadd.s f9, f23, f6
	fsw f9, 236(s0)
	fcvt.w.s x26, f23, rtz
	addi x12, x26, 1253
	add  x12, x12, x12
	slli x12, x12, 1
	addi x14, x4, -113
	add  x14, x14, x14
	slli x14, x14, 1
	sub x24, x6, x9
	sb x23, 44(s0)
	addiw x4, x16, 1041
	beq x23, x9, .Lsk_15
	nop
.Lsk_15:
	addi x15, x21, 319
	add  x15, x15, x15
	slli x15, x15, 1
	fld f23, 168(s0)
	fld f29, 0(s0)
	fmul.d f5, f23, f29
	fsd f5, 24(s0)
	fcvt.l.d x4, f23, rtz
	ld x22, 144(s0)
	ori x10, x17, 1272
	slli x5, x12, 15
	lh x31, 228(s0)
	sltu x22, x29, x18
	flw f1, 116(s0)
	flw f9, 16(s0)
	fadd.s f4, f1, f9
	fsw f4, 248(s0)
	fcvt.w.s x26, f1, rtz
	addi x16, x5, 1355
	add  x16, x16, x16
	slli x16, x16, 1
	mulw x23, x24, x10
	sh x24, 188(s0)
	lbu x19, 107(s0)
	div x3, x20, x29
	lhu x26, 58(s0)
	beq x9, x30, .Lsk_16
	nop
.Lsk_16:
	sw x17, 108(s0)
	addiw x31, x16, 557
	addi x27, x14, 192
	add  x27, x27, x27
	slli x27, x27, 1
	lbu x4, 127(s0)
	blt x25, x16, .Lsk_17
	nop
.Lsk_17:
	div x18, x30, x6
	divuw x7, x18, x31
	flw f4, 100(s0)
	flw f27, 16(s0)
	fadd.s f28, f4, f27
	fsw f28, 8(s0)
	fcvt.w.s x30, f4, rtz
	flw f5, 208(s0)
	flw f19, 112(s0)
	fsub.s f20, f5, f19
	fsw f20, 36(s0)
	fcvt.w.s x23, f5, rtz
	sll x11, x25, x19
	lw x11, 32(s0)
	mulw x23, x7, x26
	bltu x24, x5, .Lsk_18
	nop
.Lsk_18:
	srlw x4, x19, x26
	mulhsu x28, x12, x19
	lb x14, 80(s0)
	flw f6, 152(s0)
	flw f27, 156(s0)
	fadd.s f17, f6, f27
	fsw f17, 208(s0)
	fcvt.w.s x23, f6, rtz
	beq x12, x13, .Lsk_19
	nop
.Lsk_19:
	flw f8, 120(s0)
	flw f29, 8(s0)
	fsub.s f11, f8, f29
	fsw f11, 148(s0)
	fcvt.w.s x31, f8, rtz
	addi x14, x21, 1594
	add  x14, x14, x14
	slli x14, x14, 1
	addiw x24, x19, 732
	flw f15, 84(s0)
	flw f3, 56(s0)
	fadd.s f18, f15, f3
	fsw f18, 120(s0)
	fcvt.w.s x26, f15, rtz
	lhu x26, 136(s0)
	addi x29, x26, -235
	add  x29, x29, x29
	slli x29, x29, 1
	flw f11, 40(s0)
	flw f4, 224(s0)
	fmax.s f3, f11, f4
	fsw f3, 156(s0)
	fcvt.w.s x17, f11, rtz
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
	.word 0xCD8485A9
	.word 0xA03B91A7
	.word 0x2C2026F9
	.word 0x01AF2B0B
	.word 0xCA09549B
	.word 0xE8D7A2D1
	.word 0xACECF6C6
	.word 0x20E47E8A
	.word 0x0776CF2A
	.word 0x0328EC62
	.word 0xA7570A44
	.word 0xD2D06D32
	.word 0x44BF84B5
	.word 0xBF1D90E4
	.word 0x53483770
	.word 0xF73EADE3
	.word 0xCA4C1531
	.word 0x72B0AAC4
	.word 0x27E59CAE
	.word 0x92216E2B
	.word 0x320DB6E9
	.word 0xB9F2C1F6
	.word 0x0F82372D
	.word 0xB43EEB75
	.word 0x6396BD39
	.word 0x669487A8
	.word 0x12BD2B58
	.word 0x812E0725
	.word 0xF0B6BEDF
	.word 0x103D063D
	.word 0x0E874C45
	.word 0x86078324
	.word 0x95E7611B
	.word 0x73B2C52D
	.word 0x298ED2F0
	.word 0x12672F57
	.word 0x468F515C
	.word 0x9FF46602
	.word 0x6DBDA559
	.word 0x6D5F9502
	.word 0x56E9C34C
	.word 0x90C16DEB
	.word 0xD4A74606
	.word 0x8AAB53BB
	.word 0xCBF82276
	.word 0x9D873983
	.word 0x8B4ED6CF
	.word 0x1D66DF7B
	.word 0x7056A32E
	.word 0x7754AD93
	.word 0x5C6C8E37
	.word 0x6FA475EB
	.word 0x813A0785
	.word 0x9882ECA2
	.word 0x6DC42A93
	.word 0x2750A0D8
	.word 0xC60B4392
	.word 0xC3275D8A
	.word 0x8B7A0E83
	.word 0x6B387F4A
	.word 0x554165DE
	.word 0x9FB78F0F
	.word 0x78E5D491
	.word 0x6BB997D0
