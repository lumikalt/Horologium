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
	bgeu x5, x15, .Lsk_0
	nop
.Lsk_0:
	addi x9, x30, -17
	add  x9, x9, x9
	slli x9, x9, 1
	bge x11, x7, .Lsk_1
	nop
.Lsk_1:
	divu x21, x26, x19
	addi x3, x26, -1941
	add  x3, x3, x3
	slli x3, x3, 1
	srli x7, x31, 1
	addi x31, x9, 1510
	add  x31, x31, x31
	slli x31, x31, 1
	slt x22, x3, x23
	slt x30, x18, x4
	addi x3, x10, -917
	add  x3, x3, x3
	slli x3, x3, 1
	blt x21, x25, .Lsk_2
	nop
.Lsk_2:
	lui x14, 479757
	lh x25, 128(s0)
	sb x11, 185(s0)
	srli x15, x6, 14
	sw x15, 136(s0)
	mulh x4, x16, x20
	bltu x25, x30, .Lsk_3
	nop
.Lsk_3:
	sw x27, 20(s0)
	srli x30, x12, 22
	lb x27, 230(s0)
	slli x12, x15, 4
	lh x10, 90(s0)
	remu x11, x17, x21
	addi x19, x29, 995
	add  x19, x19, x19
	slli x19, x19, 1
	sra x3, x29, x31
	addi x7, x24, -1290
	add  x7, x7, x7
	slli x7, x7, 1
	rem x9, x23, x14
	divu x16, x4, x15
	flw f9, 52(s0)
	flw f27, 148(s0)
	fmin.s f21, f9, f27
	fsw f21, 24(s0)
	fcvt.w.s x29, f9, rtz
	lb x17, 217(s0)
	sb x20, 62(s0)
	srai x23, x6, 23
	or x25, x24, x21
	sw x15, 76(s0)
	sll x27, x3, x6
	lhu x30, 12(s0)
	sub x17, x14, x9
	addi x5, x4, 487
	add  x5, x5, x5
	slli x5, x5, 1
	slli x12, x11, 30
	addi x18, x25, -1182
	add  x18, x18, x18
	slli x18, x18, 1
	bge x13, x3, .Lsk_4
	nop
.Lsk_4:
	lw x11, 68(s0)
	auipc x25, 845178
	auipc x26, 455476
	xori x29, x7, 124
	addi x29, x11, 1312
	add  x29, x29, x29
	slli x29, x29, 1
	sb x21, 2(s0)
	lhu x25, 218(s0)
	sltiu x19, x17, -2028
	sll x28, x12, x30
	srai x14, x29, 31
	andi x10, x27, 735
	divu x31, x23, x21
	addi x18, x26, 1918
	add  x18, x18, x18
	slli x18, x18, 1
	rem x29, x15, x4
	beq x24, x9, .Lsk_5
	nop
.Lsk_5:
	flw f8, 184(s0)
	flw f17, 20(s0)
	fmax.s f21, f8, f17
	fsw f21, 208(s0)
	fcvt.w.s x29, f8, rtz
	sw x20, 144(s0)
	bltu x25, x27, .Lsk_6
	nop
.Lsk_6:
	addi x5, x12, -1092
	add  x5, x5, x5
	slli x5, x5, 1
	lb x21, 43(s0)
	flw f15, 132(s0)
	flw f29, 236(s0)
	fmin.s f7, f15, f29
	fsw f7, 108(s0)
	fcvt.w.s x3, f15, rtz
	sb x16, 72(s0)
	sh x16, 132(s0)
	lb x16, 157(s0)
	bge x3, x30, .Lsk_7
	nop
.Lsk_7:
	lh x24, 204(s0)
	andi x21, x4, 6
	addi x16, x21, 716
	add  x16, x16, x16
	slli x16, x16, 1
	sw x19, 4(s0)
	slti x18, x5, 1488
	addi x28, x27, -1755
	add  x28, x28, x28
	slli x28, x28, 1
	or x9, x5, x9
	or x16, x10, x4
	ori x19, x24, 993
	bltu x7, x16, .Lsk_8
	nop
.Lsk_8:
	div x23, x23, x23
	sll x6, x30, x17
	bne x7, x11, .Lsk_9
	nop
.Lsk_9:
	sw x21, 72(s0)
	flw f31, 216(s0)
	flw f26, 104(s0)
	fmin.s f14, f31, f26
	fsw f14, 224(s0)
	fcvt.w.s x19, f31, rtz
	bge x17, x25, .Lsk_10
	nop
.Lsk_10:
	lui x21, 894300
	lw x25, 0(s0)
	lb x10, 215(s0)
	xori x21, x26, 1310
	addi x14, x10, -1130
	add  x14, x14, x14
	slli x14, x14, 1
	sw x3, 236(s0)
	lb x13, 140(s0)
	addi x21, x30, 1131
	add  x21, x21, x21
	slli x21, x21, 1
	auipc x24, 139121
	sb x20, 212(s0)
	addi x15, x18, -1019
	add  x15, x15, x15
	slli x15, x15, 1
	and x4, x28, x19
	lui x12, 493307
	lw x31, 236(s0)
	bne x18, x10, .Lsk_11
	nop
.Lsk_11:
	flw f13, 176(s0)
	flw f31, 220(s0)
	fsub.s f5, f13, f31
	fsw f5, 72(s0)
	fcvt.w.s x25, f13, rtz
	slli x12, x10, 4
	addi x10, x17, 1564
	add  x10, x10, x10
	slli x10, x10, 1
	lbu x11, 136(s0)
	addi x3, x13, 1389
	add  x3, x3, x3
	slli x3, x3, 1
	sh x12, 118(s0)
	flw f26, 132(s0)
	flw f22, 188(s0)
	fmin.s f27, f26, f22
	fsw f27, 128(s0)
	fcvt.w.s x14, f26, rtz
	sub x21, x12, x15
	auipc x31, 793018
	sltu x13, x31, x24
	srli x31, x7, 27
	flw f27, 88(s0)
	flw f28, 60(s0)
	fsub.s f21, f27, f28
	fsw f21, 128(s0)
	fcvt.w.s x7, f27, rtz
	div x9, x19, x4
	beq x29, x12, .Lsk_12
	nop
.Lsk_12:
	div x26, x25, x22
	lw x27, 80(s0)
	addi x21, x13, -125
	add  x21, x21, x21
	slli x21, x21, 1
	addi x29, x26, -154
	add  x29, x29, x29
	slli x29, x29, 1
	addi x29, x4, -127
	add  x29, x29, x29
	slli x29, x29, 1
	flw f22, 240(s0)
	flw f7, 196(s0)
	fmul.s f1, f22, f7
	fsw f1, 24(s0)
	fcvt.w.s x17, f22, rtz
	sw x31, 96(s0)
	flw f29, 176(s0)
	flw f29, 244(s0)
	fmin.s f2, f29, f29
	fsw f2, 40(s0)
	fcvt.w.s x23, f29, rtz
	bgeu x15, x25, .Lsk_13
	nop
.Lsk_13:
	flw f26, 228(s0)
	flw f13, 76(s0)
	fmul.s f22, f26, f13
	fsw f22, 208(s0)
	fcvt.w.s x29, f26, rtz
	srli x10, x28, 8
	beq x17, x18, .Lsk_14
	nop
.Lsk_14:
	sw x26, 240(s0)
	addi x31, x14, -1954
	add  x31, x31, x31
	slli x31, x31, 1
	lbu x10, 144(s0)
	lui x17, 230886
	lb x14, 112(s0)
	sh x28, 116(s0)
	flw f30, 132(s0)
	flw f24, 40(s0)
	fadd.s f2, f30, f24
	fsw f2, 56(s0)
	fcvt.w.s x24, f30, rtz
	xor x5, x12, x25
	flw f30, 32(s0)
	flw f27, 228(s0)
	fsub.s f11, f30, f27
	fsw f11, 100(s0)
	fcvt.w.s x29, f30, rtz
	lb x29, 97(s0)
	auipc x27, 665072
	auipc x24, 100709
	beq x4, x31, .Lsk_15
	nop
.Lsk_15:
	addi x23, x20, 1690
	add  x23, x23, x23
	slli x23, x23, 1
	lui x11, 478949
	beq x24, x22, .Lsk_16
	nop
.Lsk_16:
	beq x23, x7, .Lsk_17
	nop
.Lsk_17:
	sw x9, 60(s0)
	sh x3, 250(s0)
	sb x9, 80(s0)
	rem x23, x4, x19
	mul x7, x3, x15
	bge x5, x22, .Lsk_18
	nop
.Lsk_18:
	addi x26, x18, 1132
	add  x26, x26, x26
	slli x26, x26, 1
	addi x27, x21, 305
	add  x27, x27, x27
	slli x27, x27, 1
	sll x23, x26, x16
	flw f13, 84(s0)
	flw f29, 56(s0)
	fmax.s f14, f13, f29
	fsw f14, 88(s0)
	fcvt.w.s x4, f13, rtz
	flw f6, 36(s0)
	flw f6, 212(s0)
	fsub.s f17, f6, f6
	fsw f17, 96(s0)
	fcvt.w.s x13, f6, rtz
	addi x4, x19, -1696
	add  x4, x4, x4
	slli x4, x4, 1
	rem x16, x5, x28
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
	.word 0x3B1AE83A
	.word 0x6D76AFC1
	.word 0xE1CD0DE2
	.word 0x0F177888
	.word 0xF932A9A5
	.word 0x548B7D77
	.word 0x95900A6F
	.word 0x5A313022
	.word 0xEFC5E33E
	.word 0x2B878FCC
	.word 0xC2058391
	.word 0xE9104635
	.word 0xB4EAC058
	.word 0x50F40F60
	.word 0xA82B6A0E
	.word 0x3B073747
	.word 0xE61CBF5C
	.word 0xD5C9FBB8
	.word 0x39CB0ABF
	.word 0x5B5088AB
	.word 0x5B74BD79
	.word 0x79267A66
	.word 0x5C9F6259
	.word 0x6686FAF9
	.word 0xA67278CE
	.word 0x6F444195
	.word 0x9FC930F9
	.word 0xFAD2259C
	.word 0x6F9EC4D3
	.word 0x8986E294
	.word 0x06561737
	.word 0xDA3E9E93
	.word 0x9DF47B02
	.word 0x900BC845
	.word 0x61AF3ABD
	.word 0xBF45D171
	.word 0x1CB5A64C
	.word 0x97EC9CA2
	.word 0x779FBAC3
	.word 0xC573A6D8
	.word 0x04D5BCAF
	.word 0x60B7FA66
	.word 0xF7E13EC1
	.word 0x25C72DEE
	.word 0x07F9315D
	.word 0xD9332100
	.word 0x128934E4
	.word 0x9C417DED
	.word 0xA909125E
	.word 0x63009CE7
	.word 0x8003A4A6
	.word 0x1C63982F
	.word 0x97732B60
	.word 0x948120A6
	.word 0xE5F2EC16
	.word 0x4723C140
	.word 0x58A5460B
	.word 0x772C9D68
	.word 0x7FD35EFB
	.word 0xC896EDEB
	.word 0xCBBD0B31
	.word 0x82666617
	.word 0xDD08022D
	.word 0x38BD1925
