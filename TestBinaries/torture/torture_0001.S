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
	addi x15, x14, 983
	add  x15, x15, x15
	slli x15, x15, 1
	addi x4, x16, -1431
	add  x4, x4, x4
	slli x4, x4, 1
	flw f17, 100(s0)
	flw f29, 12(s0)
	fmul.s f27, f17, f29
	fsw f27, 176(s0)
	fcvt.w.s x30, f17, rtz
	blt x5, x3, .Lsk_0
	nop
.Lsk_0:
	sb x26, 249(s0)
	bltu x28, x28, .Lsk_1
	nop
.Lsk_1:
	beq x30, x20, .Lsk_2
	nop
.Lsk_2:
	sb x19, 62(s0)
	sw x20, 36(s0)
	blt x26, x25, .Lsk_3
	nop
.Lsk_3:
	srai x27, x4, 27
	flw f19, 156(s0)
	flw f8, 0(s0)
	fmul.s f6, f19, f8
	fsw f6, 8(s0)
	fcvt.w.s x11, f19, rtz
	addi x18, x16, -663
	add  x18, x18, x18
	slli x18, x18, 1
	beq x7, x27, .Lsk_4
	nop
.Lsk_4:
	addi x6, x15, -1339
	add  x6, x6, x6
	slli x6, x6, 1
	flw f27, 128(s0)
	flw f11, 100(s0)
	fmin.s f19, f27, f11
	fsw f19, 92(s0)
	fcvt.w.s x30, f27, rtz
	srl x27, x16, x24
	addi x23, x19, -1476
	add  x23, x23, x23
	slli x23, x23, 1
	flw f30, 244(s0)
	flw f0, 140(s0)
	fmul.s f21, f30, f0
	fsw f21, 28(s0)
	fcvt.w.s x12, f30, rtz
	xor x22, x13, x11
	xori x28, x25, 234
	addi x9, x29, 610
	add  x9, x9, x9
	slli x9, x9, 1
	srl x12, x12, x15
	bge x27, x30, .Lsk_5
	nop
.Lsk_5:
	addi x26, x4, -145
	add  x26, x26, x26
	slli x26, x26, 1
	bltu x11, x30, .Lsk_6
	nop
.Lsk_6:
	divu x23, x28, x16
	lui x4, 217574
	lui x3, 615092
	lw x3, 140(s0)
	slli x11, x28, 23
	blt x11, x3, .Lsk_7
	nop
.Lsk_7:
	bne x28, x26, .Lsk_8
	nop
.Lsk_8:
	bge x20, x22, .Lsk_9
	nop
.Lsk_9:
	mulhu x29, x13, x22
	srai x22, x15, 28
	addi x4, x14, 1353
	bne x22, x25, .Lsk_10
	nop
.Lsk_10:
	lui x19, 130379
	div x13, x29, x29
	addi x13, x23, 348
	add  x13, x13, x13
	slli x13, x13, 1
	sub x24, x11, x13
	andi x19, x25, -1424
	auipc x21, 832382
	addi x10, x15, 1751
	add  x10, x10, x10
	slli x10, x10, 1
	sb x30, 30(s0)
	sw x10, 204(s0)
	blt x18, x24, .Lsk_11
	nop
.Lsk_11:
	srai x24, x21, 19
	beq x18, x14, .Lsk_12
	nop
.Lsk_12:
	beq x5, x13, .Lsk_13
	nop
.Lsk_13:
	addi x12, x19, -1953
	add  x12, x12, x12
	slli x12, x12, 1
	slli x28, x20, 30
	ori x21, x6, 971
	xori x10, x17, -1903
	sh x11, 58(s0)
	flw f31, 244(s0)
	flw f1, 136(s0)
	fmax.s f19, f31, f1
	fsw f19, 36(s0)
	fcvt.w.s x21, f31, rtz
	srl x18, x16, x7
	andi x15, x6, -515
	sw x23, 112(s0)
	addi x3, x13, -537
	add  x3, x3, x3
	slli x3, x3, 1
	blt x11, x30, .Lsk_14
	nop
.Lsk_14:
	bne x30, x27, .Lsk_15
	nop
.Lsk_15:
	bltu x19, x24, .Lsk_16
	nop
.Lsk_16:
	lb x26, 7(s0)
	lbu x11, 239(s0)
	xor x20, x13, x20
	flw f7, 32(s0)
	flw f2, 144(s0)
	fadd.s f7, f7, f2
	fsw f7, 4(s0)
	fcvt.w.s x7, f7, rtz
	addi x31, x13, 511
	add  x31, x31, x31
	slli x31, x31, 1
	blt x23, x30, .Lsk_17
	nop
.Lsk_17:
	addi x19, x23, -941
	add  x19, x19, x19
	slli x19, x19, 1
	or x3, x16, x14
	auipc x4, 947576
	addi x10, x22, 43
	add  x10, x10, x10
	slli x10, x10, 1
	sh x24, 38(s0)
	remu x7, x24, x10
	sw x21, 176(s0)
	sh x19, 196(s0)
	bne x19, x15, .Lsk_18
	nop
.Lsk_18:
	bgeu x25, x7, .Lsk_19
	nop
.Lsk_19:
	lb x17, 36(s0)
	addi x9, x9, -621
	add  x9, x9, x9
	slli x9, x9, 1
	srai x5, x18, 20
	sw x28, 220(s0)
	lw x28, 124(s0)
	lhu x11, 64(s0)
	addi x21, x25, -1625
	add  x21, x21, x21
	slli x21, x21, 1
	beq x12, x25, .Lsk_20
	nop
.Lsk_20:
	mul x29, x9, x27
	srli x4, x28, 28
	mul x24, x15, x6
	flw f20, 244(s0)
	flw f13, 0(s0)
	fmul.s f0, f20, f13
	fsw f0, 12(s0)
	fcvt.w.s x6, f20, rtz
	andi x7, x3, 159
	flw f3, 112(s0)
	flw f19, 96(s0)
	fadd.s f8, f3, f19
	fsw f8, 140(s0)
	fcvt.w.s x31, f3, rtz
	lw x22, 100(s0)
	mul x13, x4, x20
	slti x3, x10, -1427
	addi x27, x13, 1045
	add  x27, x27, x27
	slli x27, x27, 1
	sb x22, 70(s0)
	addi x21, x28, 1087
	add  x21, x21, x21
	slli x21, x21, 1
	mulh x23, x5, x11
	auipc x14, 912421
	flw f22, 156(s0)
	flw f0, 68(s0)
	fadd.s f19, f22, f0
	fsw f19, 152(s0)
	fcvt.w.s x22, f22, rtz
	auipc x28, 414798
	flw f20, 252(s0)
	flw f29, 12(s0)
	fsub.s f11, f20, f29
	fsw f11, 20(s0)
	fcvt.w.s x5, f20, rtz
	lh x18, 190(s0)
	addi x9, x16, -1719
	add  x9, x9, x9
	slli x9, x9, 1
	sb x30, 138(s0)
	srli x27, x9, 10
	lui x12, 924415
	addi x3, x12, -313
	add  x3, x3, x3
	slli x3, x3, 1
	addi x24, x26, -1502
	addi x25, x6, 1759
	add  x25, x25, x25
	slli x25, x25, 1
	bge x29, x31, .Lsk_21
	nop
.Lsk_21:
	divu x9, x18, x23
	addi x18, x25, 1047
	lb x13, 241(s0)
	lw x12, 172(s0)
	lh x28, 4(s0)
	slti x30, x29, -1881
	slti x20, x6, 1499
	addi x22, x28, -217
	add  x22, x22, x22
	slli x22, x22, 1
	flw f28, 164(s0)
	flw f25, 248(s0)
	fadd.s f0, f28, f25
	fsw f0, 96(s0)
	fcvt.w.s x22, f28, rtz
	srli x9, x21, 29
	addi x22, x14, 877
	lb x5, 86(s0)
	addi x25, x6, 89
	add  x25, x25, x25
	slli x25, x25, 1
	lbu x25, 217(s0)
	auipc x5, 682047
	xor x27, x22, x7
	xor x3, x25, x12
	auipc x25, 93051
	or x18, x22, x18
	addi x17, x7, -863
	add  x17, x17, x17
	slli x17, x17, 1
	sra x26, x29, x29
	flw f21, 184(s0)
	flw f31, 80(s0)
	fmin.s f2, f21, f31
	fsw f2, 68(s0)
	fcvt.w.s x12, f21, rtz
	rem x18, x5, x3
	addi x31, x27, 380
	add  x31, x31, x31
	slli x31, x31, 1
	srai x23, x31, 28
	bltu x22, x17, .Lsk_22
	nop
.Lsk_22:
	sh x28, 240(s0)
	sltu x14, x5, x13
	addi x7, x15, 1857
	add  x7, x7, x7
	slli x7, x7, 1
	auipc x31, 356877
	srai x20, x20, 12
	rem x15, x12, x20
	flw f28, 176(s0)
	flw f25, 4(s0)
	fmin.s f22, f28, f25
	fsw f22, 160(s0)
	fcvt.w.s x14, f28, rtz
	beq x22, x18, .Lsk_23
	nop
.Lsk_23:
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
	.word 0xBD5E0938
	.word 0xB7B01288
	.word 0x827A08ED
	.word 0x0B2E2CC4
	.word 0x4B835DD4
	.word 0x95134C8C
	.word 0xE5BF7A97
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
