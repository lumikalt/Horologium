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
	sub x27, x23, x11
	lbu x20, 53(s0)
	lbu x6, 239(s0)
	and x3, x10, x13
	lh x14, 104(s0)
	mulh x5, x18, x15
	sw x4, 12(s0)
	xori x6, x26, 1187
	auipc x15, 911639
	flw f16, 168(s0)
	flw f6, 92(s0)
	fadd.s f20, f16, f6
	fsw f20, 204(s0)
	fcvt.w.s x7, f16, rtz
	flw f3, 52(s0)
	flw f20, 228(s0)
	fmax.s f30, f3, f20
	fsw f30, 164(s0)
	fcvt.w.s x10, f3, rtz
	addi x23, x18, 1250
	add  x23, x23, x23
	slli x23, x23, 1
	bne x31, x3, .Lsk_0
	nop
.Lsk_0:
	andi x15, x22, 215
	auipc x19, 494804
	addi x15, x30, -305
	add  x15, x15, x15
	slli x15, x15, 1
	flw f28, 160(s0)
	flw f2, 176(s0)
	fsub.s f10, f28, f2
	fsw f10, 196(s0)
	fcvt.w.s x9, f28, rtz
	addi x16, x24, -140
	add  x16, x16, x16
	slli x16, x16, 1
	sltu x27, x18, x5
	addi x24, x12, 1289
	add  x24, x24, x24
	slli x24, x24, 1
	flw f9, 36(s0)
	flw f21, 176(s0)
	fmax.s f23, f9, f21
	fsw f23, 80(s0)
	fcvt.w.s x7, f9, rtz
	div x10, x6, x6
	addi x6, x20, -1897
	add  x6, x6, x6
	slli x6, x6, 1
	sw x17, 128(s0)
	flw f28, 224(s0)
	flw f9, 244(s0)
	fadd.s f12, f28, f9
	fsw f12, 68(s0)
	fcvt.w.s x6, f28, rtz
	addi x15, x3, -712
	add  x15, x15, x15
	slli x15, x15, 1
	mul x27, x17, x7
	bne x7, x27, .Lsk_1
	nop
.Lsk_1:
	addi x31, x3, 84
	add  x31, x31, x31
	slli x31, x31, 1
	div x21, x6, x23
	flw f27, 168(s0)
	flw f18, 24(s0)
	fmul.s f5, f27, f18
	fsw f5, 136(s0)
	fcvt.w.s x17, f27, rtz
	lui x25, 515426
	addi x14, x18, 1705
	add  x14, x14, x14
	slli x14, x14, 1
	remu x21, x9, x19
	sh x6, 230(s0)
	flw f27, 192(s0)
	flw f21, 224(s0)
	fmin.s f22, f27, f21
	fsw f22, 232(s0)
	fcvt.w.s x7, f27, rtz
	sltiu x7, x26, -588
	lb x12, 209(s0)
	mulh x13, x20, x13
	sh x21, 56(s0)
	srl x19, x25, x27
	srl x4, x17, x3
	lbu x30, 238(s0)
	sltiu x20, x19, 1380
	addi x11, x25, 766
	add  x11, x11, x11
	slli x11, x11, 1
	lhu x4, 60(s0)
	lw x17, 164(s0)
	bltu x9, x6, .Lsk_2
	nop
.Lsk_2:
	slti x23, x18, 1469
	blt x24, x31, .Lsk_3
	nop
.Lsk_3:
	srai x27, x7, 22
	sb x17, 149(s0)
	addi x7, x13, -1655
	add  x7, x7, x7
	slli x7, x7, 1
	sb x7, 140(s0)
	mul x7, x4, x20
	bltu x17, x9, .Lsk_4
	nop
.Lsk_4:
	lb x31, 4(s0)
	addi x31, x28, -347
	add  x31, x31, x31
	slli x31, x31, 1
	addi x19, x24, 2039
	add  x19, x19, x19
	slli x19, x19, 1
	sb x25, 89(s0)
	xor x6, x6, x30
	sw x29, 156(s0)
	bltu x26, x19, .Lsk_5
	nop
.Lsk_5:
	flw f8, 216(s0)
	flw f3, 124(s0)
	fmul.s f19, f8, f3
	fsw f19, 164(s0)
	fcvt.w.s x14, f8, rtz
	lui x17, 622380
	xori x22, x29, 1671
	addi x20, x29, 1602
	add  x20, x20, x20
	slli x20, x20, 1
	addi x25, x23, 1578
	add  x25, x25, x25
	slli x25, x25, 1
	add x23, x10, x18
	srli x17, x5, 11
	srai x25, x24, 21
	addi x23, x15, 985
	add  x23, x23, x23
	slli x23, x23, 1
	addi x6, x21, -1873
	add  x6, x6, x6
	slli x6, x6, 1
	sub x31, x22, x9
	addi x29, x31, 1928
	add  x29, x29, x29
	slli x29, x29, 1
	sltiu x21, x5, -1309
	sh x13, 118(s0)
	sh x25, 108(s0)
	add x16, x5, x5
	sh x17, 132(s0)
	sltiu x19, x14, 1787
	lui x9, 894938
	or x23, x16, x3
	lui x16, 116436
	srli x28, x18, 0
	sub x24, x11, x22
	addi x9, x9, -1356
	add  x9, x9, x9
	slli x9, x9, 1
	bne x30, x30, .Lsk_6
	nop
.Lsk_6:
	sh x7, 160(s0)
	blt x11, x6, .Lsk_7
	nop
.Lsk_7:
	auipc x7, 589675
	lui x27, 570278
	sltiu x15, x25, -317
	flw f23, 244(s0)
	flw f26, 32(s0)
	fsub.s f9, f23, f26
	fsw f9, 124(s0)
	fcvt.w.s x19, f23, rtz
	lw x21, 208(s0)
	sll x21, x21, x31
	sb x27, 143(s0)
	lui x23, 339925
	sb x24, 230(s0)
	addi x24, x4, -455
	add  x24, x24, x24
	slli x24, x24, 1
	beq x24, x23, .Lsk_8
	nop
.Lsk_8:
	mulh x30, x13, x9
	bgeu x5, x24, .Lsk_9
	nop
.Lsk_9:
	lui x17, 446556
	flw f3, 84(s0)
	flw f1, 176(s0)
	fsub.s f11, f3, f1
	fsw f11, 88(s0)
	fcvt.w.s x10, f3, rtz
	lh x24, 50(s0)
	sltiu x12, x13, -1607
	beq x14, x6, .Lsk_10
	nop
.Lsk_10:
	addi x17, x3, -93
	add  x17, x17, x17
	slli x17, x17, 1
	auipc x6, 360552
	addi x10, x19, -1216
	add  x10, x10, x10
	slli x10, x10, 1
	addi x10, x24, 1045
	andi x11, x20, -627
	bge x27, x6, .Lsk_11
	nop
.Lsk_11:
	lb x12, 55(s0)
	ori x3, x6, -1653
	flw f23, 192(s0)
	flw f30, 136(s0)
	fadd.s f16, f23, f30
	fsw f16, 56(s0)
	fcvt.w.s x3, f23, rtz
	lhu x23, 138(s0)
	lw x12, 152(s0)
	beq x31, x17, .Lsk_12
	nop
.Lsk_12:
	addi x23, x19, 1320
	add  x23, x23, x23
	slli x23, x23, 1
	mulh x10, x29, x28
	sh x30, 128(s0)
	lhu x6, 30(s0)
	sh x28, 10(s0)
	flw f13, 184(s0)
	flw f18, 24(s0)
	fadd.s f20, f13, f18
	fsw f20, 224(s0)
	fcvt.w.s x19, f13, rtz
	srl x7, x25, x25
	lw x17, 160(s0)
	blt x27, x4, .Lsk_13
	nop
.Lsk_13:
	addi x20, x23, -1739
	add  x20, x20, x20
	slli x20, x20, 1
	flw f31, 44(s0)
	flw f14, 168(s0)
	fmax.s f2, f31, f14
	fsw f2, 28(s0)
	fcvt.w.s x17, f31, rtz
	addi x4, x22, 1566
	add  x4, x4, x4
	slli x4, x4, 1
	flw f1, 112(s0)
	flw f9, 216(s0)
	fadd.s f17, f1, f9
	fsw f17, 160(s0)
	fcvt.w.s x5, f1, rtz
	flw f19, 4(s0)
	flw f1, 72(s0)
	fsub.s f10, f19, f1
	fsw f10, 32(s0)
	fcvt.w.s x23, f19, rtz
	lb x23, 200(s0)
	bgeu x15, x16, .Lsk_14
	nop
.Lsk_14:
	mul x27, x16, x7
	flw f22, 92(s0)
	flw f24, 160(s0)
	fadd.s f20, f22, f24
	fsw f20, 40(s0)
	fcvt.w.s x23, f22, rtz
	mul x21, x14, x20
	sh x21, 118(s0)
	auipc x25, 926037
	flw f12, 156(s0)
	flw f28, 4(s0)
	fadd.s f11, f12, f28
	fsw f11, 92(s0)
	fcvt.w.s x7, f12, rtz
	addi x11, x13, -1757
	add  x11, x11, x11
	slli x11, x11, 1
	sw x27, 52(s0)
	lh x12, 200(s0)
	addi x24, x6, 2010
	add  x24, x24, x24
	slli x24, x24, 1
	sltu x26, x21, x13
	divu x10, x20, x14
	sb x18, 198(s0)
	srai x31, x5, 13
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
	.word 0xBD708DF1
	.word 0x8A17BEA7
	.word 0x1F25F919
	.word 0x89B7AFAF
	.word 0x10C93892
	.word 0x6DB8EC94
	.word 0x22692A21
	.word 0x27F9FCE1
	.word 0x4E12C8F6
	.word 0x661AAA17
	.word 0x492AB3A4
	.word 0x1FABD0CF
	.word 0x848FB72E
	.word 0x1BE91A19
	.word 0x09243067
	.word 0x25DAC8BC
	.word 0x3F950565
	.word 0x107BFDE4
	.word 0xFFC2B4D5
	.word 0xA8332461
	.word 0xEC70C272
	.word 0xFE71F51C
	.word 0x96A8E5FF
	.word 0x5A9D3903
	.word 0xE90C2C2A
	.word 0x5F9A63BE
	.word 0xACCDA5C3
	.word 0x7840C6D5
	.word 0x87015722
	.word 0x5B7F5BC8
	.word 0x72E47B45
	.word 0x8053C726
	.word 0x6BDCEE8A
	.word 0xDAD79BCC
	.word 0x37BEDBE5
	.word 0x6CC0BA07
	.word 0x6E7816C1
	.word 0x0B8B297C
	.word 0xC1644925
	.word 0x94D07649
	.word 0xBCF72CAF
	.word 0x131B4B0F
	.word 0x8C6B5A6A
	.word 0xD63ECD81
	.word 0x715BDA27
	.word 0x5B1B4C22
	.word 0xF9E5EBA6
	.word 0xA49BEA29
	.word 0x8C71218B
	.word 0x83023F78
	.word 0x59E68756
	.word 0xF0A36C23
	.word 0x43DFD9E5
	.word 0x13E7E5B3
	.word 0x7BE2616F
	.word 0x64BC3652
	.word 0x1FD90B57
	.word 0x785B4328
	.word 0xB84E8A3F
	.word 0x01492DA5
	.word 0xC2FF9C70
	.word 0x38C04778
	.word 0x1CC8CAD6
	.word 0x25AAB3EF
