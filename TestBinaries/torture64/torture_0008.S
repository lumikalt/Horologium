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
	bgeu x10, x28, .Lsk_0
	nop
.Lsk_0:
	bge x16, x22, .Lsk_1
	nop
.Lsk_1:
	addi x10, x11, 265
	add  x10, x10, x10
	slli x10, x10, 1
	bltu x21, x22, .Lsk_2
	nop
.Lsk_2:
	lhu x3, 174(s0)
	mul x16, x15, x13
	addi x12, x11, -777
	add  x12, x12, x12
	slli x12, x12, 1
	xor x17, x31, x5
	sb x11, 170(s0)
	blt x15, x21, .Lsk_3
	nop
.Lsk_3:
	lw x12, 96(s0)
	addiw x14, x18, -1601
	addi x15, x19, -329
	add  x15, x15, x15
	slli x15, x15, 1
	slli x28, x6, 35
	addi x30, x15, -1111
	add  x30, x30, x30
	slli x30, x30, 1
	addi x7, x26, 1583
	add  x7, x7, x7
	slli x7, x7, 1
	addi x23, x22, -807
	add  x23, x23, x23
	slli x23, x23, 1
	addi x26, x11, 1976
	add  x26, x26, x26
	slli x26, x26, 1
	addi x24, x25, -795
	add  x24, x24, x24
	slli x24, x24, 1
	lb x5, 131(s0)
	addi x14, x13, -183
	add  x14, x14, x14
	slli x14, x14, 1
	sd x11, 216(s0)
	blt x18, x26, .Lsk_4
	nop
.Lsk_4:
	xor x7, x3, x10
	sltiu x27, x12, -565
	addi x23, x21, 1916
	add  x23, x23, x23
	slli x23, x23, 1
	bgeu x15, x4, .Lsk_5
	nop
.Lsk_5:
	srliw x11, x30, 1
	addi x5, x17, -977
	add  x5, x5, x5
	slli x5, x5, 1
	sraiw x25, x25, 23
	flw f11, 136(s0)
	flw f19, 64(s0)
	fmax.s f26, f11, f19
	fsw f26, 160(s0)
	fcvt.w.s x13, f11, rtz
	srli x28, x26, 14
	srliw x17, x25, 27
	addi x19, x9, 1644
	add  x19, x19, x19
	slli x19, x19, 1
	slt x19, x12, x7
	addw x17, x10, x27
	addi x10, x22, -329
	add  x10, x10, x10
	slli x10, x10, 1
	auipc x25, 257482
	sraiw x22, x25, 26
	addi x19, x19, 1102
	add  x19, x19, x19
	slli x19, x19, 1
	sw x7, 232(s0)
	sllw x5, x30, x15
	andi x28, x17, 880
	sraw x3, x16, x5
	divw x23, x17, x20
	beq x3, x11, .Lsk_6
	nop
.Lsk_6:
	sll x30, x10, x26
	flw f0, 188(s0)
	flw f17, 120(s0)
	fmul.s f6, f0, f17
	fsw f6, 32(s0)
	fcvt.w.s x3, f0, rtz
	lhu x19, 82(s0)
	lh x21, 88(s0)
	addi x13, x15, 1163
	add  x13, x13, x13
	slli x13, x13, 1
	lb x20, 215(s0)
	addi x27, x12, 1856
	add  x27, x27, x27
	slli x27, x27, 1
	remu x11, x17, x27
	addi x17, x9, 183
	add  x17, x17, x17
	slli x17, x17, 1
	addi x21, x20, -1478
	add  x21, x21, x21
	slli x21, x21, 1
	addi x21, x28, 1236
	add  x21, x21, x21
	slli x21, x21, 1
	lw x10, 240(s0)
	rem x30, x23, x27
	div x9, x19, x9
	addi x16, x15, 1816
	add  x16, x16, x16
	slli x16, x16, 1
	addi x13, x9, -203
	add  x13, x13, x13
	slli x13, x13, 1
	sraiw x11, x16, 17
	flw f30, 68(s0)
	flw f7, 84(s0)
	fmul.s f11, f30, f7
	fsw f11, 60(s0)
	fcvt.w.s x22, f30, rtz
	addiw x9, x22, 127
	lw x14, 200(s0)
	bne x30, x12, .Lsk_7
	nop
.Lsk_7:
	subw x25, x7, x18
	addi x28, x31, -577
	add  x28, x28, x28
	slli x28, x28, 1
	addi x9, x30, -1348
	add  x9, x9, x9
	slli x9, x9, 1
	xori x18, x23, -394
	fld f19, 64(s0)
	fld f13, 104(s0)
	fsub.d f10, f19, f13
	fsd f10, 104(s0)
	fcvt.l.d x11, f19, rtz
	lh x20, 248(s0)
	bne x27, x24, .Lsk_8
	nop
.Lsk_8:
	lui x21, 1021675
	remu x13, x16, x19
	flw f30, 28(s0)
	flw f14, 144(s0)
	fsub.s f22, f30, f14
	fsw f22, 228(s0)
	fcvt.w.s x3, f30, rtz
	auipc x29, 1022361
	sb x24, 211(s0)
	ld x3, 152(s0)
	or x23, x19, x17
	sw x26, 148(s0)
	lbu x6, 119(s0)
	fld f2, 24(s0)
	fld f26, 16(s0)
	fmul.d f13, f2, f26
	fsd f13, 112(s0)
	fcvt.l.d x22, f2, rtz
	sra x14, x15, x14
	xor x29, x15, x29
	bne x3, x5, .Lsk_9
	nop
.Lsk_9:
	andi x18, x20, -1699
	divuw x28, x17, x6
	ori x24, x6, -1406
	addw x17, x13, x16
	sra x26, x24, x5
	lui x21, 657105
	bgeu x22, x15, .Lsk_10
	nop
.Lsk_10:
	sllw x23, x4, x26
	bgeu x11, x5, .Lsk_11
	nop
.Lsk_11:
	flw f18, 252(s0)
	flw f19, 120(s0)
	fadd.s f30, f18, f19
	fsw f30, 104(s0)
	fcvt.w.s x27, f18, rtz
	flw f6, 180(s0)
	flw f4, 184(s0)
	fadd.s f1, f6, f4
	fsw f1, 24(s0)
	fcvt.w.s x3, f6, rtz
	flw f24, 72(s0)
	flw f26, 116(s0)
	fadd.s f3, f24, f26
	fsw f3, 108(s0)
	fcvt.w.s x7, f24, rtz
	addi x11, x30, -661
	add  x11, x11, x11
	slli x11, x11, 1
	flw f13, 160(s0)
	flw f11, 8(s0)
	fsub.s f25, f13, f11
	fsw f25, 16(s0)
	fcvt.w.s x29, f13, rtz
	bge x5, x5, .Lsk_12
	nop
.Lsk_12:
	srlw x16, x3, x14
	srlw x29, x3, x27
	srai x6, x13, 24
	addi x17, x13, -891
	add  x17, x17, x17
	slli x17, x17, 1
	srl x3, x16, x27
	fld f12, 168(s0)
	fld f8, 152(s0)
	fadd.d f9, f12, f8
	fsd f9, 168(s0)
	fcvt.l.d x10, f12, rtz
	srai x4, x28, 51
	sh x10, 130(s0)
	sltiu x27, x19, -405
	bge x31, x3, .Lsk_13
	nop
.Lsk_13:
	or x7, x22, x13
	srai x13, x5, 10
	bge x27, x7, .Lsk_14
	nop
.Lsk_14:
	sw x7, 40(s0)
	divw x30, x27, x4
	sh x30, 210(s0)
	addi x15, x5, -830
	add  x15, x15, x15
	slli x15, x15, 1
	sw x6, 116(s0)
	auipc x24, 224262
	flw f21, 116(s0)
	flw f26, 20(s0)
	fmin.s f1, f21, f26
	fsw f1, 188(s0)
	fcvt.w.s x4, f21, rtz
	div x10, x25, x21
	bge x26, x12, .Lsk_15
	nop
.Lsk_15:
	remu x7, x25, x21
	addi x19, x10, 420
	add  x19, x19, x19
	slli x19, x19, 1
	srl x13, x15, x4
	slli x17, x27, 55
	blt x26, x10, .Lsk_16
	nop
.Lsk_16:
	mulhsu x4, x18, x16
	slliw x22, x17, 29
	sb x9, 82(s0)
	addiw x12, x27, 420
	lwu x7, 224(s0)
	bne x12, x15, .Lsk_17
	nop
.Lsk_17:
	or x16, x27, x9
	xori x22, x5, 1578
	addiw x30, x25, -156
	flw f29, 196(s0)
	flw f11, 248(s0)
	fmin.s f16, f29, f11
	fsw f16, 232(s0)
	fcvt.w.s x10, f29, rtz
	ori x18, x13, -1318
	bgeu x21, x5, .Lsk_18
	nop
.Lsk_18:
	sw x24, 12(s0)
	mul x29, x31, x13
	or x22, x29, x18
	addi x7, x19, 508
	add  x7, x7, x7
	slli x7, x7, 1
	slt x24, x9, x16
	addiw x19, x11, -1264
	addi x16, x23, 1723
	add  x16, x16, x16
	slli x16, x16, 1
	bge x27, x11, .Lsk_19
	nop
.Lsk_19:
	srliw x31, x28, 17
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
	.word 0xE9663473
	.word 0x86890260
	.word 0xA37C94FC
	.word 0x7DD9DC47
	.word 0xC90CE3F4
	.word 0x08773BFB
	.word 0x8738E7E1
	.word 0x29556802
	.word 0x4BA63021
	.word 0x04347EA4
	.word 0xBD0588EC
	.word 0xDFEF4FCF
	.word 0x73C971CB
	.word 0xEE25E410
	.word 0x9259B06D
	.word 0x49863714
	.word 0x4DC2BD76
	.word 0x9257C894
	.word 0xACF9BAF9
	.word 0xC36FF25B
	.word 0x693A48B4
	.word 0x3D5F831F
	.word 0xBBA5E84A
	.word 0xEFAF072D
	.word 0xEF2D2296
	.word 0xEEF10B38
	.word 0xB07D594A
	.word 0x8DDA13BB
	.word 0x51AEDA25
	.word 0x9933C530
	.word 0xA0A478A0
	.word 0x755F7326
	.word 0xD2DBF4C9
	.word 0x63A83190
	.word 0x3007D465
	.word 0xB5E8C882
	.word 0x21D87C62
	.word 0x11E6DCFD
	.word 0x36BBF64E
	.word 0x3AF5BE6B
	.word 0xDB0E728F
	.word 0xD70A1E8D
	.word 0x11A61F41
	.word 0xB70118D1
	.word 0x009C50F8
	.word 0x55645F9D
	.word 0xF3FD3005
	.word 0x6D799E82
	.word 0x5397B177
	.word 0xDF45BFBA
	.word 0x1570597D
	.word 0x35046498
	.word 0x73EFF339
	.word 0x696B48C9
	.word 0x861B6CA1
	.word 0x9C50D627
	.word 0xCCA3E429
	.word 0xDEEC298A
	.word 0x4428CE87
	.word 0x8F5C470A
	.word 0x06D33692
	.word 0xA035BA79
	.word 0x44540121
	.word 0xEC7B5DC5
