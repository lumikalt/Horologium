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
	xori x19, x20, -1451
	div x24, x29, x10
	sraw x22, x31, x25
	lwu x4, 104(s0)
	lbu x10, 232(s0)
	sh x28, 182(s0)
	lh x24, 32(s0)
	sw x17, 148(s0)
	divu x12, x31, x3
	divu x20, x12, x12
	addi x19, x27, 2044
	add  x19, x19, x19
	slli x19, x19, 1
	fld f31, 240(s0)
	fld f23, 72(s0)
	fmin.d f27, f31, f23
	fsd f27, 144(s0)
	fcvt.l.d x19, f31, rtz
	lh x20, 152(s0)
	addi x15, x23, -860
	add  x15, x15, x15
	slli x15, x15, 1
	sb x16, 208(s0)
	fld f8, 56(s0)
	fld f28, 176(s0)
	fsub.d f30, f8, f28
	fsd f30, 216(s0)
	fcvt.l.d x26, f8, rtz
	sraw x13, x20, x9
	slli x7, x27, 13
	addi x20, x10, 1027
	add  x20, x20, x20
	slli x20, x20, 1
	divuw x11, x28, x22
	bne x6, x29, .Lsk_0
	nop
.Lsk_0:
	lbu x6, 37(s0)
	lw x20, 212(s0)
	lwu x14, 20(s0)
	fld f25, 0(s0)
	fld f5, 0(s0)
	fadd.d f6, f25, f5
	fsd f6, 104(s0)
	fcvt.l.d x13, f25, rtz
	auipc x17, 266247
	addiw x7, x19, -1675
	bltu x27, x16, .Lsk_1
	nop
.Lsk_1:
	slliw x3, x30, 26
	lwu x25, 4(s0)
	sd x26, 16(s0)
	sllw x21, x28, x27
	lhu x28, 248(s0)
	rem x22, x4, x19
	sb x25, 69(s0)
	addi x7, x13, 469
	add  x7, x7, x7
	slli x7, x7, 1
	remw x7, x22, x30
	beq x18, x5, .Lsk_2
	nop
.Lsk_2:
	div x4, x13, x13
	div x13, x27, x19
	addi x6, x24, -1239
	add  x6, x6, x6
	slli x6, x6, 1
	sraw x11, x17, x25
	addw x13, x9, x19
	flw f26, 4(s0)
	flw f6, 132(s0)
	fmin.s f27, f26, f6
	fsw f27, 88(s0)
	fcvt.w.s x12, f26, rtz
	and x13, x19, x14
	mulw x19, x10, x30
	sw x26, 216(s0)
	flw f24, 220(s0)
	flw f29, 216(s0)
	fsub.s f18, f24, f29
	fsw f18, 140(s0)
	fcvt.w.s x20, f24, rtz
	lb x7, 213(s0)
	addi x20, x16, -1252
	add  x20, x20, x20
	slli x20, x20, 1
	sltiu x11, x30, 653
	bltu x28, x23, .Lsk_3
	nop
.Lsk_3:
	sd x19, 72(s0)
	addi x5, x16, -541
	add  x5, x5, x5
	slli x5, x5, 1
	bge x23, x28, .Lsk_4
	nop
.Lsk_4:
	lw x11, 64(s0)
	bne x30, x3, .Lsk_5
	nop
.Lsk_5:
	sra x22, x3, x4
	blt x21, x16, .Lsk_6
	nop
.Lsk_6:
	and x29, x29, x3
	bgeu x28, x27, .Lsk_7
	nop
.Lsk_7:
	flw f29, 84(s0)
	flw f27, 16(s0)
	fmin.s f4, f29, f27
	fsw f4, 232(s0)
	fcvt.w.s x9, f29, rtz
	slt x18, x6, x3
	addi x25, x24, -1424
	add  x25, x25, x25
	slli x25, x25, 1
	sh x17, 88(s0)
	lw x10, 60(s0)
	sb x3, 133(s0)
	lbu x16, 235(s0)
	lw x15, 36(s0)
	flw f8, 164(s0)
	flw f21, 60(s0)
	fmin.s f9, f8, f21
	fsw f9, 236(s0)
	fcvt.w.s x6, f8, rtz
	sb x22, 32(s0)
	lwu x28, 224(s0)
	bltu x9, x5, .Lsk_8
	nop
.Lsk_8:
	addi x18, x15, -225
	add  x18, x18, x18
	slli x18, x18, 1
	subw x21, x22, x13
	beq x14, x5, .Lsk_9
	nop
.Lsk_9:
	lhu x20, 90(s0)
	lhu x3, 140(s0)
	flw f9, 92(s0)
	flw f19, 36(s0)
	fmul.s f8, f9, f19
	fsw f8, 184(s0)
	fcvt.w.s x17, f9, rtz
	and x10, x12, x18
	ld x7, 168(s0)
	flw f5, 228(s0)
	flw f21, 108(s0)
	fmax.s f9, f5, f21
	fsw f9, 76(s0)
	fcvt.w.s x12, f5, rtz
	fld f28, 120(s0)
	fld f25, 152(s0)
	fsub.d f5, f28, f25
	fsd f5, 8(s0)
	fcvt.l.d x19, f28, rtz
	addi x23, x15, 394
	add  x23, x23, x23
	slli x23, x23, 1
	sh x31, 238(s0)
	lb x3, 89(s0)
	lhu x15, 44(s0)
	lui x10, 177907
	lb x6, 121(s0)
	sh x12, 66(s0)
	lui x23, 141008
	lh x18, 86(s0)
	sw x18, 188(s0)
	lwu x18, 76(s0)
	addw x16, x30, x14
	srli x24, x12, 15
	xori x26, x16, 219
	sraw x27, x3, x15
	fld f7, 224(s0)
	fld f30, 120(s0)
	fadd.d f6, f7, f30
	fsd f6, 80(s0)
	fcvt.l.d x23, f7, rtz
	addi x12, x28, -409
	add  x12, x12, x12
	slli x12, x12, 1
	srl x18, x23, x15
	addiw x27, x30, -1440
	srl x12, x3, x11
	sh x5, 196(s0)
	lh x9, 74(s0)
	srl x14, x24, x12
	lh x17, 70(s0)
	addi x19, x17, -407
	add  x19, x19, x19
	slli x19, x19, 1
	andi x29, x20, 766
	bltu x10, x16, .Lsk_10
	nop
.Lsk_10:
	remu x22, x15, x21
	mulhu x25, x14, x12
	beq x16, x14, .Lsk_11
	nop
.Lsk_11:
	addi x19, x7, 446
	add  x19, x19, x19
	slli x19, x19, 1
	srl x28, x28, x14
	bltu x24, x22, .Lsk_12
	nop
.Lsk_12:
	sb x17, 228(s0)
	fld f27, 160(s0)
	fld f0, 56(s0)
	fmax.d f23, f27, f0
	fsd f23, 152(s0)
	fcvt.l.d x25, f27, rtz
	lh x6, 58(s0)
	auipc x20, 187578
	blt x3, x25, .Lsk_13
	nop
.Lsk_13:
	lw x11, 124(s0)
	fld f26, 144(s0)
	fld f21, 16(s0)
	fmin.d f26, f26, f21
	fsd f26, 208(s0)
	fcvt.l.d x18, f26, rtz
	addi x13, x20, -847
	add  x13, x13, x13
	slli x13, x13, 1
	divw x26, x29, x28
	bne x3, x29, .Lsk_14
	nop
.Lsk_14:
	srai x21, x12, 51
	fld f12, 80(s0)
	fld f28, 104(s0)
	fsub.d f1, f12, f28
	fsd f1, 120(s0)
	fcvt.l.d x4, f12, rtz
	slt x18, x6, x13
	srli x23, x17, 19
	srliw x30, x19, 3
	lwu x26, 112(s0)
	addi x25, x25, 1893
	add  x25, x25, x25
	slli x25, x25, 1
	divu x19, x28, x20
	divu x14, x12, x20
	addi x22, x12, -1705
	add  x22, x22, x22
	slli x22, x22, 1
	addi x26, x10, -1367
	add  x26, x26, x26
	slli x26, x26, 1
	srai x12, x21, 43
	sw x25, 76(s0)
	lh x26, 100(s0)
	addi x9, x10, -726
	add  x9, x9, x9
	slli x9, x9, 1
	divu x31, x5, x10
	addi x27, x5, -1445
	add  x27, x27, x27
	slli x27, x27, 1
	xori x27, x15, 378
	fld f12, 152(s0)
	fld f8, 136(s0)
	fadd.d f25, f12, f8
	fsd f25, 224(s0)
	fcvt.l.d x25, f12, rtz
	srli x3, x9, 12
	srlw x12, x3, x10
	srli x16, x25, 29
	addi x13, x5, -944
	add  x13, x13, x13
	slli x13, x13, 1
	bne x7, x21, .Lsk_15
	nop
.Lsk_15:
	slli x26, x28, 50
	sll x5, x20, x13
	addiw x19, x18, -1870
	addi x13, x7, -877
	add  x13, x13, x13
	slli x13, x13, 1
	lwu x19, 60(s0)
	divu x28, x5, x23
	sb x10, 243(s0)
	and x28, x26, x4
	sb x17, 166(s0)
	addiw x10, x21, 178
	xori x26, x23, -1220
	sw x6, 180(s0)
	remu x22, x12, x5
	srai x25, x15, 50
	ori x3, x3, 1443
	lh x5, 32(s0)
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
	.word 0x37990392
	.word 0x86AE9902
	.word 0xF1A1E966
	.word 0xE625C3B1
	.word 0x5DE5520B
	.word 0xF7FC8592
	.word 0x1C12E721
	.word 0x33057CCC
	.word 0xE1DE373F
	.word 0x1DE708E6
	.word 0xC7DE51C0
	.word 0x93491FCF
	.word 0xDB413101
	.word 0x0D9D306E
	.word 0xB1B275DA
	.word 0x79255EED
	.word 0xE8EB05E4
	.word 0x4E98F325
	.word 0xD1D673C6
	.word 0x21DC6FD3
	.word 0x669B6FE8
	.word 0x660F779E
	.word 0xFA317092
	.word 0xCCBB3BD6
	.word 0x9078F826
	.word 0x5CC1E0E6
	.word 0x785CF73E
	.word 0x4A8B1276
	.word 0x53EFD9EC
	.word 0xA75F75AC
	.word 0x946AB4FE
	.word 0x26FC354B
	.word 0x4DF73E46
	.word 0x05DB4E2F
	.word 0x2C295D2A
	.word 0x330F7F2A
	.word 0x6202714E
	.word 0x20063B2F
	.word 0xF74CA10A
	.word 0x45A2780B
	.word 0x60C97E5E
	.word 0x3B9E7A6C
	.word 0xF3AFCE2C
	.word 0xA9F944D5
	.word 0xAF774364
	.word 0x20CFD1C6
	.word 0xDBE0F64B
	.word 0x60C79DA9
	.word 0x542B59DE
	.word 0x8E55C747
	.word 0x5EDF89F7
	.word 0x86643045
	.word 0x55F9F7D6
	.word 0x15890D09
	.word 0xAC26BACB
	.word 0xC8B4FBCF
	.word 0x3D9D5716
	.word 0x93D471BC
	.word 0xCD627A7C
	.word 0xEA269E1D
	.word 0x733B493F
	.word 0x4B046FF3
	.word 0xF936683E
	.word 0x866BDC24
