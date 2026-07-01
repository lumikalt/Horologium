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
	add x16, x15, x13
	addi x12, x11, -777
	add  x12, x12, x12
	slli x12, x12, 1
	xor x17, x31, x5
	sh x11, 170(s0)
	blt x15, x21, .Lsk_3
	nop
.Lsk_3:
	lw x12, 96(s0)
	lui x14, 282744
	sub x10, x10, x22
	sh x26, 182(s0)
	xori x24, x7, 1947
	sb x7, 125(s0)
	addi x19, x25, -795
	add  x19, x19, x19
	slli x19, x19, 1
	lb x5, 131(s0)
	addi x14, x13, -183
	add  x14, x14, x14
	slli x14, x14, 1
	sb x20, 238(s0)
	sltiu x27, x17, 1561
	bgeu x7, x3, .Lsk_4
	nop
.Lsk_4:
	sltu x15, x10, x26
	sw x13, 244(s0)
	bgeu x15, x4, .Lsk_5
	nop
.Lsk_5:
	srli x11, x30, 1
	addi x5, x17, -977
	add  x5, x5, x5
	slli x5, x5, 1
	ori x25, x25, 930
	flw f11, 28(s0)
	flw f19, 136(s0)
	fmul.s f26, f11, f19
	fsw f26, 64(s0)
	fcvt.w.s x13, f11, rtz
	sh x28, 252(s0)
	addi x10, x27, 1405
	add  x10, x10, x10
	slli x10, x10, 1
	bltu x12, x17, .Lsk_6
	nop
.Lsk_6:
	addi x19, x9, 1644
	add  x19, x19, x19
	slli x19, x19, 1
	slt x19, x12, x7
	mul x17, x10, x27
	addi x10, x22, -329
	add  x10, x10, x10
	slli x10, x10, 1
	auipc x25, 257482
	andi x22, x25, 1388
	addi x19, x19, 1102
	add  x19, x19, x19
	slli x19, x19, 1
	sw x7, 232(s0)
	divu x5, x30, x15
	auipc x28, 484248
	srai x16, x17, 5
	srai x23, x17, 12
	lw x3, 124(s0)
	sll x30, x10, x26
	flw f0, 212(s0)
	flw f17, 188(s0)
	fmax.s f6, f0, f17
	fsw f6, 120(s0)
	fcvt.w.s x3, f0, rtz
	remu x17, x28, x26
	bgeu x9, x18, .Lsk_7
	nop
.Lsk_7:
	lbu x26, 89(s0)
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
	and x30, x23, x28
	sb x5, 85(s0)
	lh x12, 90(s0)
	addi x16, x15, 1816
	add  x16, x16, x16
	slli x16, x16, 1
	addi x13, x9, -203
	add  x13, x13, x13
	slli x13, x13, 1
	andi x11, x16, 148
	flw f30, 120(s0)
	flw f7, 68(s0)
	fadd.s f11, f30, f7
	fsw f11, 84(s0)
	fcvt.w.s x22, f30, rtz
	auipc x9, 1039988
	slti x14, x16, 16
	mulhu x25, x7, x18
	addi x28, x31, -577
	add  x28, x28, x28
	slli x28, x28, 1
	addi x9, x30, -1348
	add  x9, x9, x9
	slli x9, x9, 1
	xori x18, x23, -394
	flw f19, 68(s0)
	flw f13, 108(s0)
	fsub.s f10, f19, f13
	fsw f10, 108(s0)
	fcvt.w.s x11, f19, rtz
	lh x20, 248(s0)
	bne x27, x24, .Lsk_8
	nop
.Lsk_8:
	lui x21, 1021675
	remu x13, x16, x19
	flw f30, 148(s0)
	flw f14, 28(s0)
	fmin.s f22, f30, f14
	fsw f22, 144(s0)
	fcvt.w.s x3, f30, rtz
	sltiu x7, x29, 1945
	sh x24, 210(s0)
	lw x22, 156(s0)
	or x23, x19, x17
	sw x26, 148(s0)
	lbu x6, 119(s0)
	flw f2, 28(s0)
	flw f26, 16(s0)
	fmul.s f13, f2, f26
	fsw f13, 112(s0)
	fcvt.w.s x22, f2, rtz
	sra x14, x15, x14
	xor x29, x15, x29
	bne x3, x5, .Lsk_9
	nop
.Lsk_9:
	lui x18, 111954
	mulh x17, x19, x7
	mulh x6, x28, x10
	sh x15, 18(s0)
	lb x26, 50(s0)
	sltiu x5, x13, -961
	sb x18, 185(s0)
	divu x23, x4, x26
	bgeu x11, x5, .Lsk_10
	nop
.Lsk_10:
	flw f18, 224(s0)
	flw f19, 252(s0)
	fsub.s f30, f18, f19
	fsw f30, 120(s0)
	fcvt.w.s x27, f18, rtz
	beq x6, x5, .Lsk_11
	nop
.Lsk_11:
	mul x4, x20, x5
	flw f24, 8(s0)
	flw f26, 72(s0)
	fsub.s f3, f24, f26
	fsw f3, 116(s0)
	fcvt.w.s x7, f24, rtz
	lui x24, 457341
	bge x29, x15, .Lsk_12
	nop
.Lsk_12:
	lb x3, 16(s0)
	flw f22, 20(s0)
	flw f24, 204(s0)
	fmin.s f4, f22, f24
	fsw f4, 4(s0)
	fcvt.w.s x5, f22, rtz
	lhu x14, 116(s0)
	beq x17, x26, .Lsk_13
	nop
.Lsk_13:
	lb x7, 60(s0)
	srai x5, x26, 27
	srli x20, x20, 1
	addi x6, x12, 1455
	add  x6, x6, x6
	slli x6, x6, 1
	bgeu x22, x10, .Lsk_14
	nop
.Lsk_14:
	srai x7, x10, 21
	add x14, x3, x28
	sb x16, 81(s0)
	auipc x12, 756704
	addi x10, x24, -1146
	add  x10, x10, x10
	slli x10, x10, 1
	lw x12, 204(s0)
	divu x4, x21, x13
	sub x14, x24, x18
	bltu x15, x24, .Lsk_15
	nop
.Lsk_15:
	flw f4, 84(s0)
	flw f9, 112(s0)
	fmax.s f5, f4, f9
	fsw f5, 20(s0)
	fcvt.w.s x14, f4, rtz
	auipc x30, 186230
	lui x7, 242093
	lw x24, 76(s0)
	addi x17, x29, -1172
	add  x17, x17, x17
	slli x17, x17, 1
	flw f21, 112(s0)
	flw f26, 116(s0)
	fmul.s f1, f21, f26
	fsw f1, 20(s0)
	fcvt.w.s x4, f21, rtz
	lbu x10, 157(s0)
	sb x26, 245(s0)
	bne x18, x7, .Lsk_16
	nop
.Lsk_16:
	bge x21, x30, .Lsk_17
	nop
.Lsk_17:
	lbu x13, 184(s0)
	auipc x15, 73937
	slli x17, x27, 27
	blt x26, x10, .Lsk_18
	nop
.Lsk_18:
	and x4, x18, x7
	lbu x17, 22(s0)
	lui x18, 338871
	auipc x12, 978817
	lui x7, 444302
	mul x13, x18, x3
	lb x22, 40(s0)
	flw f27, 116(s0)
	flw f28, 232(s0)
	fmul.s f6, f27, f28
	fsw f6, 88(s0)
	fcvt.w.s x30, f27, rtz
	auipc x30, 950369
	lhu x18, 148(s0)
	bltu x5, x27, .Lsk_19
	nop
.Lsk_19:
	flw f4, 12(s0)
	flw f31, 56(s0)
	fadd.s f5, f4, f31
	fsw f5, 124(s0)
	fcvt.w.s x24, f4, rtz
	blt x10, x4, .Lsk_20
	nop
.Lsk_20:
	sw x3, 224(s0)
	addi x7, x19, 508
	add  x7, x7, x7
	slli x7, x7, 1
	slt x24, x9, x16
	lui x19, 488191
	lbu x18, 232(s0)
	blt x31, x28, .Lsk_21
	nop
.Lsk_21:
	bge x21, x26, .Lsk_22
	nop
.Lsk_22:
	bne x29, x29, .Lsk_23
	nop
.Lsk_23:
	lw x20, 208(s0)
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
	.word 0xB9B7FD7C
	.word 0xA85B5E22
