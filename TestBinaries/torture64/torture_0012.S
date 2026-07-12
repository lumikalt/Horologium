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
	sub x27, x23, x11
	ld x27, 48(s0)
	lbu x6, 239(s0)
	and x3, x10, x13
	ld x10, 160(s0)
	bltu x31, x10, .Lsk_0
	nop
.Lsk_0:
	mulw x5, x18, x15
	sw x4, 12(s0)
	xori x6, x26, 1187
	auipc x15, 911639
	fld f16, 168(s0)
	fld f6, 88(s0)
	fadd.d f20, f16, f6
	fsd f20, 200(s0)
	fcvt.l.d x7, f16, rtz
	flw f3, 164(s0)
	flw f20, 112(s0)
	fmin.s f30, f3, f20
	fsw f30, 232(s0)
	fcvt.w.s x10, f3, rtz
	bne x31, x3, .Lsk_1
	nop
.Lsk_1:
	sraiw x15, x22, 17
	xori x19, x11, -116
	addi x15, x30, -305
	add  x15, x15, x15
	slli x15, x15, 1
	fld f28, 176(s0)
	fld f2, 192(s0)
	fmin.d f10, f28, f2
	fsd f10, 64(s0)
	fcvt.l.d x9, f28, rtz
	blt x18, x27, .Lsk_2
	nop
.Lsk_2:
	addi x12, x12, -1334
	add  x12, x12, x12
	slli x12, x12, 1
	addi x24, x12, 1289
	add  x24, x24, x24
	slli x24, x24, 1
	flw f9, 176(s0)
	flw f21, 80(s0)
	fmax.s f23, f9, f21
	fsw f23, 32(s0)
	fcvt.w.s x7, f9, rtz
	sraiw x18, x3, 17
	srai x24, x6, 2
	sh x17, 128(s0)
	flw f28, 244(s0)
	flw f9, 68(s0)
	fsub.s f12, f28, f9
	fsw f12, 24(s0)
	fcvt.w.s x6, f28, rtz
	addi x3, x25, -712
	add  x3, x3, x3
	slli x3, x3, 1
	mul x27, x17, x7
	bne x7, x27, .Lsk_3
	nop
.Lsk_3:
	addi x31, x3, 84
	add  x31, x31, x31
	slli x31, x31, 1
	sllw x21, x6, x23
	flw f27, 136(s0)
	flw f18, 176(s0)
	fmul.s f5, f27, f18
	fsw f5, 168(s0)
	fcvt.w.s x17, f27, rtz
	bne x31, x23, .Lsk_4
	nop
.Lsk_4:
	auipc x25, 960944
	remu x21, x9, x19
	sb x6, 230(s0)
	fld f27, 224(s0)
	fld f21, 232(s0)
	fsub.d f22, f27, f21
	fsd f22, 208(s0)
	fcvt.l.d x7, f27, rtz
	bgeu x28, x29, .Lsk_5
	nop
.Lsk_5:
	sraw x20, x6, x24
	addi x13, x7, 345
	add  x13, x13, x13
	slli x13, x13, 1
	lh x22, 52(s0)
	slli x7, x19, 32
	sw x16, 0(s0)
	addw x17, x4, x18
	bltu x18, x15, .Lsk_6
	nop
.Lsk_6:
	flw f31, 172(s0)
	flw f22, 228(s0)
	fadd.s f26, f31, f22
	fsw f26, 228(s0)
	fcvt.w.s x4, f31, rtz
	fld f26, 88(s0)
	fld f18, 48(s0)
	fadd.d f6, f26, f18
	fsd f6, 128(s0)
	fcvt.l.d x17, f26, rtz
	sw x11, 216(s0)
	blt x24, x31, .Lsk_7
	nop
.Lsk_7:
	sraiw x27, x7, 22
	sd x22, 144(s0)
	addi x7, x13, -1655
	add  x7, x7, x7
	slli x7, x7, 1
	sh x12, 60(s0)
	ori x4, x13, 1411
	addiw x29, x12, -347
	addi x19, x24, 2039
	add  x19, x19, x19
	slli x19, x19, 1
	sh x15, 48(s0)
	xori x20, x14, -439
	addi x13, x25, 1816
	add  x13, x13, x13
	slli x13, x13, 1
	fld f8, 120(s0)
	fld f3, 160(s0)
	fsub.d f19, f8, f3
	fsd f19, 160(s0)
	fcvt.l.d x14, f8, rtz
	xor x19, x17, x31
	sh x19, 232(s0)
	addi x20, x29, 1602
	add  x20, x20, x20
	slli x20, x20, 1
	addi x25, x23, 1578
	add  x25, x25, x25
	slli x25, x25, 1
	mul x23, x10, x18
	divuw x17, x5, x9
	srai x25, x24, 42
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
	sb x13, 119(s0)
	sd x25, 104(s0)
	add x16, x5, x5
	sd x17, 128(s0)
	sltiu x19, x14, 1787
	addiw x9, x12, -328
	beq x23, x16, .Lsk_8
	nop
.Lsk_8:
	srl x24, x20, x29
	addi x11, x4, 973
	add  x11, x11, x11
	slli x11, x11, 1
	bge x20, x31, .Lsk_9
	nop
.Lsk_9:
	slliw x3, x12, 14
	srai x9, x9, 23
	flw f19, 120(s0)
	flw f9, 52(s0)
	fadd.s f20, f19, f9
	fsw f20, 64(s0)
	fcvt.w.s x25, f19, rtz
	sllw x16, x23, x14
	slti x27, x29, 179
	srliw x15, x25, 13
	flw f23, 124(s0)
	flw f26, 100(s0)
	fadd.s f9, f23, f26
	fsw f9, 224(s0)
	fcvt.w.s x19, f23, rtz
	sd x5, 16(s0)
	sb x4, 126(s0)
	sw x6, 80(s0)
	sd x31, 16(s0)
	addi x21, x10, -1830
	add  x21, x21, x21
	slli x21, x21, 1
	blt x10, x30, .Lsk_10
	nop
.Lsk_10:
	lui x22, 367453
	bgeu x5, x24, .Lsk_11
	nop
.Lsk_11:
	slti x17, x31, -304
	fld f3, 80(s0)
	fld f1, 176(s0)
	fsub.d f11, f3, f1
	fsd f11, 88(s0)
	fcvt.l.d x10, f3, rtz
	lh x24, 50(s0)
	srliw x12, x13, 3
	beq x14, x6, .Lsk_12
	nop
.Lsk_12:
	addi x17, x3, -93
	add  x17, x17, x17
	slli x17, x17, 1
	auipc x6, 360552
	addi x10, x19, -1216
	add  x10, x10, x10
	slli x10, x10, 1
	addi x10, x24, 1045
	sraiw x11, x20, 11
	bge x27, x6, .Lsk_13
	nop
.Lsk_13:
	lb x12, 55(s0)
	ori x3, x6, -1653
	flw f23, 56(s0)
	flw f30, 0(s0)
	fmin.s f16, f23, f30
	fsw f16, 228(s0)
	fcvt.w.s x3, f23, rtz
	sb x17, 93(s0)
	addi x4, x12, 425
	add  x4, x4, x4
	slli x4, x4, 1
	beq x31, x17, .Lsk_14
	nop
.Lsk_14:
	addi x23, x19, 1320
	add  x23, x23, x23
	slli x23, x23, 1
	addw x10, x29, x28
	sb x30, 128(s0)
	lhu x6, 30(s0)
	sd x28, 8(s0)
	flw f13, 224(s0)
	flw f18, 0(s0)
	fadd.s f20, f13, f18
	fsw f20, 20(s0)
	fcvt.w.s x19, f13, rtz
	addi x25, x3, 1264
	add  x25, x25, x25
	slli x25, x25, 1
	beq x28, x3, .Lsk_15
	nop
.Lsk_15:
	lb x27, 28(s0)
	slliw x4, x20, 31
	add x4, x17, x30
	sw x22, 224(s0)
	flw f1, 216(s0)
	flw f9, 160(s0)
	fmul.s f17, f1, f9
	fsw f17, 24(s0)
	fcvt.w.s x5, f1, rtz
	fld f1, 72(s0)
	fld f10, 32(s0)
	fmin.d f0, f1, f10
	fsd f0, 80(s0)
	fcvt.l.d x23, f1, rtz
	addiw x23, x26, -1019
	addi x15, x16, -949
	add  x15, x15, x15
	slli x15, x15, 1
	addw x16, x17, x7
	fld f22, 88(s0)
	fld f24, 160(s0)
	fadd.d f20, f22, f24
	fsd f20, 40(s0)
	fcvt.l.d x23, f22, rtz
	mulw x21, x14, x20
	sb x21, 119(s0)
	addiw x25, x6, 1009
	addi x31, x28, 1569
	add  x31, x31, x31
	slli x31, x31, 1
	flw f12, 92(s0)
	flw f28, 48(s0)
	fsub.s f11, f12, f28
	fsw f11, 0(s0)
	fcvt.w.s x7, f12, rtz
	mulh x23, x28, x18
	rem x26, x10, x24
	slti x29, x22, 430
	beq x24, x26, .Lsk_16
	nop
.Lsk_16:
	div x11, x19, x15
	mulw x22, x21, x4
	addi x5, x20, -986
	add  x5, x5, x5
	slli x5, x5, 1
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
	.word 0xBA824979
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
