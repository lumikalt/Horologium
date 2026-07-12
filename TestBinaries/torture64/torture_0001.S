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
	addi x15, x14, 983
	add  x15, x15, x15
	slli x15, x15, 1
	addi x4, x16, -1431
	add  x4, x4, x4
	slli x4, x4, 1
	flw f17, 176(s0)
	flw f29, 140(s0)
	fmul.s f27, f17, f29
	fsw f27, 232(s0)
	fcvt.w.s x30, f17, rtz
	srliw x23, x12, 0
	fld f31, 144(s0)
	fld f7, 136(s0)
	fadd.d f10, f31, f7
	fsd f10, 24(s0)
	fcvt.l.d x20, f31, rtz
	sd x29, 120(s0)
	sb x24, 69(s0)
	bne x13, x3, .Lsk_0
	nop
.Lsk_0:
	sw x15, 224(s0)
	lbu x9, 9(s0)
	subw x23, x6, x22
	bne x13, x30, .Lsk_1
	nop
.Lsk_1:
	xori x21, x10, -1764
	addiw x16, x10, 1885
	srliw x19, x25, 16
	lwu x25, 28(s0)
	addiw x4, x22, -376
	addi x14, x11, 196
	add  x14, x14, x14
	slli x14, x14, 1
	bgeu x28, x26, .Lsk_2
	nop
.Lsk_2:
	lb x9, 166(s0)
	srl x12, x12, x15
	bge x27, x30, .Lsk_3
	nop
.Lsk_3:
	addi x26, x4, -145
	add  x26, x26, x26
	slli x26, x26, 1
	bltu x11, x30, .Lsk_4
	nop
.Lsk_4:
	divuw x23, x28, x16
	addiw x4, x13, -1187
	addi x26, x6, -1127
	add  x26, x26, x26
	slli x26, x26, 1
	div x7, x31, x19
	lw x12, 156(s0)
	addi x28, x21, -1804
	add  x28, x28, x28
	slli x28, x28, 1
	slt x13, x11, x28
	bne x17, x20, .Lsk_5
	nop
.Lsk_5:
	sh x29, 152(s0)
	slli x22, x10, 47
	sd x11, 248(s0)
	addi x14, x28, -1876
	add  x14, x14, x14
	slli x14, x14, 1
	lb x9, 44(s0)
	sb x28, 27(s0)
	flw f4, 156(s0)
	flw f14, 144(s0)
	fsub.s f18, f4, f14
	fsw f18, 148(s0)
	fcvt.w.s x29, f4, rtz
	addi x24, x11, -229
	add  x24, x24, x24
	slli x24, x24, 1
	lb x3, 255(s0)
	bge x24, x5, .Lsk_6
	nop
.Lsk_6:
	ori x21, x25, 971
	lbu x10, 186(s0)
	lh x27, 30(s0)
	sw x10, 204(s0)
	blt x18, x24, .Lsk_7
	nop
.Lsk_7:
	srai x24, x21, 38
	beq x18, x14, .Lsk_8
	nop
.Lsk_8:
	beq x5, x13, .Lsk_9
	nop
.Lsk_9:
	addi x12, x19, -1953
	add  x12, x12, x12
	slli x12, x12, 1
	mulw x28, x20, x19
	sraiw x21, x6, 23
	xori x10, x17, -1903
	sb x11, 58(s0)
	fld f31, 240(s0)
	fld f1, 136(s0)
	fmax.d f19, f31, f1
	fsd f19, 32(s0)
	fcvt.l.d x21, f31, rtz
	srl x18, x16, x7
	andi x15, x6, -515
	sh x23, 114(s0)
	addi x3, x13, -537
	add  x3, x3, x3
	slli x3, x3, 1
	blt x11, x30, .Lsk_10
	nop
.Lsk_10:
	bne x30, x27, .Lsk_11
	nop
.Lsk_11:
	bltu x19, x24, .Lsk_12
	nop
.Lsk_12:
	lb x26, 7(s0)
	lbu x11, 239(s0)
	xor x20, x13, x20
	flw f7, 144(s0)
	flw f2, 4(s0)
	fadd.s f7, f7, f2
	fsw f7, 48(s0)
	fcvt.w.s x7, f7, rtz
	addi x13, x25, 1846
	add  x13, x13, x13
	slli x13, x13, 1
	divw x30, x13, x15
	flw f25, 28(s0)
	flw f8, 4(s0)
	fmul.s f23, f25, f8
	fsw f23, 160(s0)
	fcvt.w.s x20, f25, rtz
	addi x18, x24, -457
	add  x18, x18, x18
	slli x18, x18, 1
	sb x21, 154(s0)
	subw x5, x10, x24
	lwu x19, 104(s0)
	sh x21, 178(s0)
	sd x19, 192(s0)
	bne x19, x15, .Lsk_13
	nop
.Lsk_13:
	bgeu x25, x7, .Lsk_14
	nop
.Lsk_14:
	lwu x31, 184(s0)
	lbu x5, 231(s0)
	sub x26, x27, x18
	sb x23, 17(s0)
	addi x30, x7, 1503
	add  x30, x30, x30
	slli x30, x30, 1
	lw x28, 124(s0)
	lhu x11, 64(s0)
	addi x21, x25, -1625
	add  x21, x21, x21
	slli x21, x21, 1
	beq x12, x25, .Lsk_15
	nop
.Lsk_15:
	mulw x29, x9, x27
	srli x4, x28, 57
	sraw x24, x15, x3
	mul x24, x14, x6
	fld f0, 0(s0)
	fld f1, 48(s0)
	fmul.d f18, f0, f1
	fsd f18, 136(s0)
	fcvt.l.d x12, f0, rtz
	bgeu x13, x29, .Lsk_16
	nop
.Lsk_16:
	sltu x11, x21, x29
	srli x10, x3, 38
	addi x13, x13, -1871
	add  x13, x13, x13
	slli x13, x13, 1
	sd x3, 104(s0)
	flw f12, 192(s0)
	flw f4, 68(s0)
	fmax.s f26, f12, f4
	fsw f26, 220(s0)
	fcvt.w.s x27, f12, rtz
	flw f24, 32(s0)
	flw f11, 116(s0)
	fmul.s f3, f24, f11
	fsw f3, 116(s0)
	fcvt.w.s x23, f24, rtz
	srlw x6, x26, x29
	lb x26, 4(s0)
	mulh x13, x7, x14
	lw x27, 132(s0)
	fld f12, 232(s0)
	fld f2, 88(s0)
	fmin.d f20, f12, f2
	fsd f20, 32(s0)
	fcvt.l.d x30, f12, rtz
	divw x28, x11, x18
	sb x14, 91(s0)
	addi x30, x4, 138
	add  x30, x30, x30
	slli x30, x30, 1
	sb x15, 137(s0)
	addi x9, x20, 1053
	add  x9, x9, x9
	slli x9, x9, 1
	remu x4, x22, x3
	sd x3, 128(s0)
	slliw x19, x24, 4
	addi x25, x6, 1759
	add  x25, x25, x25
	slli x25, x25, 1
	bge x29, x31, .Lsk_17
	nop
.Lsk_17:
	remuw x9, x18, x30
	bltu x6, x9, .Lsk_18
	nop
.Lsk_18:
	addi x25, x28, -1777
	add  x25, x25, x25
	slli x25, x25, 1
	fld f24, 240(s0)
	fld f26, 72(s0)
	fmul.d f3, f24, f26
	fsd f3, 48(s0)
	fcvt.l.d x14, f24, rtz
	sd x11, 80(s0)
	flw f0, 8(s0)
	flw f6, 112(s0)
	fmax.s f7, f0, f6
	fsw f7, 208(s0)
	fcvt.w.s x28, f0, rtz
	addw x9, x7, x22
	fld f14, 0(s0)
	fld f1, 160(s0)
	fsub.d f28, f14, f1
	fsd f28, 248(s0)
	fcvt.l.d x24, f14, rtz
	fld f3, 64(s0)
	fld f17, 144(s0)
	fmul.d f14, f3, f17
	fsd f14, 232(s0)
	fcvt.l.d x9, f3, rtz
	blt x24, x20, .Lsk_19
	nop
.Lsk_19:
	addiw x17, x19, 584
	lui x23, 547075
	lbu x25, 217(s0)
	auipc x5, 682047
	xor x27, x22, x7
	xor x3, x25, x12
	auipc x25, 93051
	remu x18, x22, x17
	slti x11, x18, 1782
	flw f25, 184(s0)
	flw f21, 80(s0)
	fmin.s f31, f25, f21
	fsw f31, 68(s0)
	fcvt.w.s x4, f25, rtz
	srlw x18, x5, x3
	addi x31, x27, 380
	add  x31, x31, x31
	slli x31, x31, 1
	srai x23, x31, 56
	bltu x22, x17, .Lsk_20
	nop
.Lsk_20:
	sd x28, 240(s0)
	div x14, x5, x13
	addi x7, x15, 1857
	add  x7, x7, x7
	slli x7, x7, 1
	auipc x31, 356877
	remuw x20, x20, x10
	srlw x15, x12, x20
	flw f28, 4(s0)
	flw f25, 160(s0)
	fsub.s f22, f28, f25
	fsw f22, 204(s0)
	fcvt.w.s x14, f28, rtz
	auipc x22, 121752
	addi x18, x23, 1061
	add  x18, x18, x18
	slli x18, x18, 1
	fld f7, 144(s0)
	fld f2, 56(s0)
	fsub.d f26, f7, f2
	fsd f26, 232(s0)
	fcvt.l.d x17, f7, rtz
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
	.word 0x68CCA1CD
	.word 0x1F41D15F
	.word 0xD6AAC3DD
	.word 0xDE8C98DC
	.word 0x7FF2BDCC
	.word 0xE48A2906
	.word 0xC7CA0344
	.word 0xFCE2B77A
	.word 0x8FE88195
	.word 0x4A83A32C
	.word 0xAF2C9570
	.word 0x1574B17E
	.word 0x01DC7DEA
	.word 0x4B074A29
	.word 0x950F036D
	.word 0x9F78900E
	.word 0x5361AD61
	.word 0x4C364479
	.word 0xD6A94F53
	.word 0x6B6A9D51
	.word 0x74F6DC79
	.word 0xC808929A
	.word 0xC0687515
	.word 0x61E6F2EB
	.word 0xF2D97701
	.word 0xC83B3D29
	.word 0x6900822D
	.word 0x353EC18C
	.word 0x67FE9667
	.word 0x906B8B2B
	.word 0xAA4530BC
	.word 0x824177F1
	.word 0x8D1B147A
	.word 0x0D10AE12
	.word 0x0C6F2519
	.word 0x0E6C4BE2
	.word 0x5CF4BF7E
	.word 0xA6F9B1E4
	.word 0x9F0B36DC
	.word 0x024481A9
	.word 0xFE225178
	.word 0x7977EFBE
	.word 0x9D56846E
	.word 0xD3AB64D9
	.word 0x7D916C91
	.word 0x96AD3E44
	.word 0x53C47177
	.word 0x2EB9A2B5
	.word 0x946CD14B
	.word 0x560D45F2
	.word 0x7BE9A0A8
	.word 0x2B2353EC
	.word 0x035620F3
	.word 0xFA5897CF
	.word 0xC0834C26
	.word 0x6A4D7C07
	.word 0xECA32233
	.word 0x90075C51
	.word 0x14F92EF5
	.word 0xB18F3D56
	.word 0x028490E2
	.word 0xBF85889D
	.word 0xDC19E547
	.word 0xAA595B11
