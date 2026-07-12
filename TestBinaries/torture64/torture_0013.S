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
	mulhu x30, x28, x14
	srai x29, x30, 25
	flw f26, 64(s0)
	flw f4, 124(s0)
	fadd.s f16, f26, f4
	fsw f16, 108(s0)
	fcvt.w.s x25, f26, rtz
	blt x12, x10, .Lsk_0
	nop
.Lsk_0:
	bne x28, x26, .Lsk_1
	nop
.Lsk_1:
	sh x26, 74(s0)
	sw x30, 244(s0)
	divw x23, x20, x23
	bltu x30, x19, .Lsk_2
	nop
.Lsk_2:
	addi x5, x18, 730
	add  x5, x5, x5
	slli x5, x5, 1
	addi x26, x22, -1708
	add  x26, x26, x26
	slli x26, x26, 1
	slt x25, x25, x6
	fld f23, 80(s0)
	fld f9, 176(s0)
	fmax.d f8, f23, f9
	fsd f8, 200(s0)
	fcvt.l.d x18, f23, rtz
	fld f11, 184(s0)
	fld f0, 216(s0)
	fmin.d f16, f11, f0
	fsd f16, 104(s0)
	fcvt.l.d x3, f11, rtz
	addw x31, x13, x7
	bltu x14, x13, .Lsk_3
	nop
.Lsk_3:
	ld x20, 232(s0)
	bltu x3, x6, .Lsk_4
	nop
.Lsk_4:
	sltu x7, x12, x25
	lw x23, 200(s0)
	ori x24, x27, -1212
	sd x29, 232(s0)
	addiw x11, x7, 191
	addi x27, x26, 914
	add  x27, x27, x27
	slli x27, x27, 1
	lh x5, 78(s0)
	srai x19, x30, 37
	slli x28, x10, 15
	addi x28, x17, 1232
	add  x28, x28, x28
	slli x28, x28, 1
	lh x3, 248(s0)
	addi x11, x16, -1545
	add  x11, x11, x11
	slli x11, x11, 1
	slt x25, x17, x26
	lw x12, 8(s0)
	addi x22, x12, 1531
	add  x22, x22, x22
	slli x22, x22, 1
	addi x15, x5, -225
	add  x15, x15, x15
	slli x15, x15, 1
	sh x6, 156(s0)
	addi x14, x10, 1513
	add  x14, x14, x14
	slli x14, x14, 1
	addi x9, x4, 1395
	add  x9, x9, x9
	slli x9, x9, 1
	sraiw x25, x7, 22
	ld x14, 64(s0)
	beq x4, x6, .Lsk_5
	nop
.Lsk_5:
	lb x20, 71(s0)
	addi x25, x6, 1311
	add  x25, x25, x25
	slli x25, x25, 1
	fld f6, 32(s0)
	fld f14, 136(s0)
	fmin.d f6, f6, f14
	fsd f6, 0(s0)
	fcvt.l.d x22, f6, rtz
	sw x21, 176(s0)
	addi x31, x9, -790
	add  x31, x31, x31
	slli x31, x31, 1
	sh x9, 32(s0)
	and x22, x18, x23
	blt x29, x27, .Lsk_6
	nop
.Lsk_6:
	sll x24, x28, x12
	bltu x25, x27, .Lsk_7
	nop
.Lsk_7:
	lbu x15, 225(s0)
	sw x14, 80(s0)
	lbu x18, 77(s0)
	addi x28, x29, 389
	add  x28, x28, x28
	slli x28, x28, 1
	bne x24, x18, .Lsk_8
	nop
.Lsk_8:
	addi x6, x10, -1825
	add  x6, x6, x6
	slli x6, x6, 1
	sd x21, 64(s0)
	fld f13, 144(s0)
	fld f10, 176(s0)
	fsub.d f6, f13, f10
	fsd f6, 184(s0)
	fcvt.l.d x23, f13, rtz
	lh x30, 172(s0)
	sll x15, x30, x16
	mulhsu x5, x9, x12
	lhu x26, 8(s0)
	addi x20, x24, -1291
	add  x20, x20, x20
	slli x20, x20, 1
	sub x25, x24, x24
	slt x17, x31, x9
	bltu x13, x22, .Lsk_9
	nop
.Lsk_9:
	flw f23, 124(s0)
	flw f10, 156(s0)
	fmul.s f29, f23, f10
	fsw f29, 156(s0)
	fcvt.w.s x11, f23, rtz
	addi x27, x6, 379
	add  x27, x27, x27
	slli x27, x27, 1
	srlw x19, x22, x23
	flw f31, 228(s0)
	flw f29, 188(s0)
	fmin.s f0, f31, f29
	fsw f0, 240(s0)
	fcvt.w.s x17, f31, rtz
	xor x6, x10, x15
	bltu x16, x28, .Lsk_10
	nop
.Lsk_10:
	sraiw x24, x9, 27
	divw x21, x23, x11
	lh x6, 130(s0)
	fld f21, 208(s0)
	fld f10, 48(s0)
	fmin.d f4, f21, f10
	fsd f4, 128(s0)
	fcvt.l.d x4, f21, rtz
	addi x5, x27, -1681
	add  x5, x5, x5
	slli x5, x5, 1
	lwu x24, 44(s0)
	addw x9, x3, x14
	sllw x25, x9, x21
	divu x13, x10, x14
	bgeu x3, x13, .Lsk_11
	nop
.Lsk_11:
	sh x20, 86(s0)
	sh x15, 102(s0)
	lwu x20, 152(s0)
	flw f24, 244(s0)
	flw f30, 72(s0)
	fmin.s f12, f24, f30
	fsw f12, 76(s0)
	fcvt.w.s x27, f24, rtz
	bne x16, x19, .Lsk_12
	nop
.Lsk_12:
	mulhsu x11, x11, x28
	fld f13, 176(s0)
	fld f8, 96(s0)
	fsub.d f9, f13, f8
	fsd f9, 192(s0)
	fcvt.l.d x13, f13, rtz
	mulw x23, x14, x29
	lbu x9, 42(s0)
	sll x7, x29, x3
	srai x25, x7, 41
	mulhsu x18, x20, x23
	fld f17, 232(s0)
	fld f27, 80(s0)
	fsub.d f14, f17, f27
	fsd f14, 64(s0)
	fcvt.l.d x15, f17, rtz
	sw x31, 124(s0)
	addi x14, x19, 1115
	lui x11, 125664
	flw f13, 24(s0)
	flw f30, 16(s0)
	fsub.s f28, f13, f30
	fsw f28, 24(s0)
	fcvt.w.s x6, f13, rtz
	flw f22, 8(s0)
	flw f21, 140(s0)
	fadd.s f13, f22, f21
	fsw f13, 196(s0)
	fcvt.w.s x31, f22, rtz
	slliw x29, x31, 22
	addi x19, x5, -1859
	add  x19, x19, x19
	slli x19, x19, 1
	lh x17, 202(s0)
	sra x11, x21, x27
	addi x18, x16, 1026
	add  x18, x18, x18
	slli x18, x18, 1
	lb x26, 134(s0)
	lwu x13, 64(s0)
	mulw x31, x20, x6
	sw x4, 252(s0)
	mulhu x14, x12, x17
	lw x13, 104(s0)
	srliw x25, x24, 2
	fld f6, 160(s0)
	fld f11, 96(s0)
	fmin.d f16, f6, f11
	fsd f16, 128(s0)
	fcvt.l.d x20, f6, rtz
	bge x31, x20, .Lsk_13
	nop
.Lsk_13:
	addi x28, x4, 1797
	add  x28, x28, x28
	slli x28, x28, 1
	lw x11, 68(s0)
	fld f12, 184(s0)
	fld f12, 240(s0)
	fadd.d f11, f12, f12
	fsd f11, 104(s0)
	fcvt.l.d x3, f12, rtz
	sh x29, 22(s0)
	sb x12, 1(s0)
	slt x3, x14, x13
	lui x5, 814331
	bgeu x24, x15, .Lsk_14
	nop
.Lsk_14:
	lw x29, 4(s0)
	addi x4, x3, 1225
	add  x4, x4, x4
	slli x4, x4, 1
	ld x30, 248(s0)
	sltiu x14, x19, -85
	addi x11, x11, 126
	add  x11, x11, x11
	slli x11, x11, 1
	bge x12, x9, .Lsk_15
	nop
.Lsk_15:
	lw x26, 28(s0)
	lw x25, 128(s0)
	lb x18, 164(s0)
	addiw x25, x4, 226
	sd x22, 104(s0)
	lhu x17, 138(s0)
	addi x31, x29, 1396
	add  x31, x31, x31
	slli x31, x31, 1
	ori x31, x31, 287
	ori x23, x12, -1940
	sraw x16, x20, x4
	sraw x14, x19, x20
	auipc x10, 68592
	addi x10, x29, -1676
	add  x10, x10, x10
	slli x10, x10, 1
	addi x17, x9, -1111
	add  x17, x17, x17
	slli x17, x17, 1
	sub x3, x12, x11
	addi x19, x17, -192
	add  x19, x19, x19
	slli x19, x19, 1
	or x30, x9, x5
	lh x19, 224(s0)
	flw f0, 48(s0)
	flw f31, 40(s0)
	fmin.s f30, f0, f31
	fsw f30, 200(s0)
	fcvt.w.s x15, f0, rtz
	remw x21, x11, x21
	addiw x18, x17, -160
	lui x24, 618013
	mulh x6, x15, x25
	sltu x5, x25, x27
	srliw x22, x20, 1
	addi x5, x5, -982
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
	.word 0x98950229
	.word 0x62AB9053
	.word 0x7FEFC5E9
	.word 0x445D1357
	.word 0xDE8CB51C
	.word 0xB09D7F48
	.word 0xDEEE032C
	.word 0x915940EE
	.word 0xE251F771
	.word 0xBB6BC1D6
	.word 0x3663CBA5
	.word 0x90ACCB94
	.word 0xB6905DA0
	.word 0x03E9A982
	.word 0xE7E0426B
	.word 0xB676D568
	.word 0x2AFAF4D0
	.word 0x21E6CC1E
	.word 0xB98A64C3
	.word 0x0EB5C394
	.word 0x6740273A
	.word 0x4C6551DE
	.word 0x2848F1CC
	.word 0x56FEBCC2
	.word 0x300AACC3
	.word 0x6635B952
	.word 0x998BD745
	.word 0x4F172C4F
	.word 0xB8C34ED2
	.word 0xB190CA27
	.word 0x8B563242
	.word 0x608F934D
	.word 0xC5D25E34
	.word 0xE88456E5
	.word 0xC0155CDE
	.word 0x9080D2E1
	.word 0x28EE9097
	.word 0x267A3A0B
	.word 0xE6BF15C1
	.word 0xC06923EB
	.word 0xC8D8F84F
	.word 0x02B71B00
	.word 0x41F34182
	.word 0x5C086CEF
	.word 0xB4172A1A
	.word 0x1AE1A1E9
	.word 0x4D050602
	.word 0x16EFA49A
	.word 0x83CC4AB2
	.word 0x8B142136
	.word 0xBED6FD6C
	.word 0xC846609A
	.word 0x409DCB3E
	.word 0x0BBD0616
	.word 0x73C90576
	.word 0x29D53C1C
	.word 0xC663CCE4
	.word 0x37060540
	.word 0xAE7B4AD8
	.word 0x99C5237C
	.word 0x2460A45E
	.word 0x91D67C4D
	.word 0xBB91191F
	.word 0x8DEB7BD8
