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
	lui x19, 380651
	flw f0, 156(s0)
	flw f26, 104(s0)
	fmul.s f17, f0, f26
	fsw f17, 228(s0)
	fcvt.w.s x9, f0, rtz
	addi x13, x14, 861
	addi x12, x6, 157
	add  x12, x12, x12
	slli x12, x12, 1
	auipc x16, 116973
	sh x29, 114(s0)
	flw f18, 28(s0)
	flw f13, 108(s0)
	fadd.s f9, f18, f13
	fsw f9, 144(s0)
	fcvt.w.s x15, f18, rtz
	addi x24, x27, 823
	add  x24, x24, x24
	slli x24, x24, 1
	addi x9, x23, -1388
	add  x9, x9, x9
	slli x9, x9, 1
	addi x14, x9, 1841
	add  x14, x14, x14
	slli x14, x14, 1
	srai x7, x15, 30
	slli x25, x3, 19
	addi x9, x27, -1066
	add  x9, x9, x9
	slli x9, x9, 1
	beq x24, x15, .Lsk_0
	nop
.Lsk_0:
	addi x9, x16, -1369
	add  x9, x9, x9
	slli x9, x9, 1
	div x4, x6, x17
	and x28, x27, x10
	srli x11, x18, 13
	divu x17, x9, x10
	lb x11, 156(s0)
	beq x19, x27, .Lsk_1
	nop
.Lsk_1:
	sb x22, 164(s0)
	ori x18, x11, 1168
	sw x18, 116(s0)
	lb x25, 79(s0)
	addi x20, x23, -1275
	add  x20, x20, x20
	slli x20, x20, 1
	sw x11, 24(s0)
	or x14, x16, x22
	lhu x26, 254(s0)
	addi x15, x20, 760
	add  x15, x15, x15
	slli x15, x15, 1
	lh x7, 148(s0)
	div x3, x13, x28
	lh x10, 246(s0)
	sh x10, 248(s0)
	andi x31, x24, 962
	addi x7, x18, -570
	add  x7, x7, x7
	slli x7, x7, 1
	auipc x14, 991261
	flw f9, 0(s0)
	flw f30, 184(s0)
	fmul.s f10, f9, f30
	fsw f10, 188(s0)
	fcvt.w.s x28, f9, rtz
	bne x9, x19, .Lsk_2
	nop
.Lsk_2:
	flw f1, 88(s0)
	flw f27, 100(s0)
	fmax.s f19, f1, f27
	fsw f19, 248(s0)
	fcvt.w.s x20, f1, rtz
	sb x27, 159(s0)
	addi x5, x4, -785
	add  x5, x5, x5
	slli x5, x5, 1
	sh x6, 22(s0)
	sb x31, 49(s0)
	bne x21, x3, .Lsk_3
	nop
.Lsk_3:
	srli x7, x28, 12
	xori x13, x9, 1152
	lhu x6, 212(s0)
	slli x18, x29, 24
	sw x30, 88(s0)
	sb x19, 77(s0)
	andi x16, x7, 125
	lw x7, 244(s0)
	sw x21, 44(s0)
	beq x23, x11, .Lsk_4
	nop
.Lsk_4:
	sra x16, x13, x23
	addi x3, x23, -600
	add  x3, x3, x3
	slli x3, x3, 1
	bge x13, x27, .Lsk_5
	nop
.Lsk_5:
	addi x9, x23, 1133
	add  x9, x9, x9
	slli x9, x9, 1
	lbu x28, 29(s0)
	lui x30, 628343
	divu x7, x11, x19
	srli x16, x10, 19
	mulhu x13, x15, x29
	sw x14, 176(s0)
	lui x10, 942647
	sltu x10, x20, x30
	sltiu x24, x19, -975
	flw f0, 84(s0)
	flw f23, 116(s0)
	fmax.s f12, f0, f23
	fsw f12, 24(s0)
	fcvt.w.s x29, f0, rtz
	sh x12, 110(s0)
	srli x14, x26, 1
	lhu x30, 10(s0)
	addi x26, x9, 603
	add  x26, x26, x26
	slli x26, x26, 1
	mul x13, x22, x24
	lb x15, 25(s0)
	sw x13, 152(s0)
	sh x16, 248(s0)
	flw f4, 80(s0)
	flw f7, 164(s0)
	fmax.s f27, f4, f7
	fsw f27, 244(s0)
	fcvt.w.s x20, f4, rtz
	bltu x21, x17, .Lsk_6
	nop
.Lsk_6:
	flw f13, 28(s0)
	flw f3, 128(s0)
	fmax.s f2, f13, f3
	fsw f2, 84(s0)
	fcvt.w.s x21, f13, rtz
	flw f15, 240(s0)
	flw f31, 252(s0)
	fmax.s f22, f15, f31
	fsw f22, 104(s0)
	fcvt.w.s x23, f15, rtz
	slt x10, x29, x7
	sb x11, 15(s0)
	add x11, x14, x19
	lb x12, 209(s0)
	lw x28, 80(s0)
	divu x22, x12, x6
	sh x15, 4(s0)
	lh x24, 178(s0)
	sw x18, 252(s0)
	sb x26, 150(s0)
	sw x28, 220(s0)
	bltu x19, x31, .Lsk_7
	nop
.Lsk_7:
	bne x11, x19, .Lsk_8
	nop
.Lsk_8:
	srai x4, x14, 25
	bne x10, x21, .Lsk_9
	nop
.Lsk_9:
	sll x15, x23, x7
	srai x14, x16, 14
	divu x4, x20, x21
	lh x11, 120(s0)
	add x15, x30, x28
	bge x21, x20, .Lsk_10
	nop
.Lsk_10:
	slt x3, x22, x7
	sra x27, x15, x25
	sub x3, x9, x12
	flw f6, 180(s0)
	flw f3, 164(s0)
	fadd.s f4, f6, f3
	fsw f4, 152(s0)
	fcvt.w.s x26, f6, rtz
	lw x19, 48(s0)
	flw f8, 76(s0)
	flw f30, 44(s0)
	fsub.s f7, f8, f30
	fsw f7, 244(s0)
	fcvt.w.s x12, f8, rtz
	lhu x9, 108(s0)
	slt x15, x26, x16
	mulh x31, x9, x21
	slt x6, x22, x27
	lui x30, 190978
	srai x16, x20, 26
	sw x21, 96(s0)
	slti x21, x30, 1154
	addi x15, x27, 69
	sh x20, 18(s0)
	lb x23, 111(s0)
	lui x20, 47805
	beq x25, x21, .Lsk_11
	nop
.Lsk_11:
	mulh x19, x15, x23
	flw f4, 48(s0)
	flw f17, 72(s0)
	fadd.s f31, f4, f17
	fsw f31, 140(s0)
	fcvt.w.s x29, f4, rtz
	blt x18, x16, .Lsk_12
	nop
.Lsk_12:
	flw f22, 228(s0)
	flw f2, 44(s0)
	fmul.s f4, f22, f2
	fsw f4, 108(s0)
	fcvt.w.s x9, f22, rtz
	addi x15, x27, 209
	add  x15, x15, x15
	slli x15, x15, 1
	and x7, x25, x18
	bne x25, x17, .Lsk_13
	nop
.Lsk_13:
	lui x24, 123932
	and x10, x26, x4
	sh x17, 206(s0)
	flw f9, 248(s0)
	flw f26, 68(s0)
	fmul.s f1, f9, f26
	fsw f1, 108(s0)
	fcvt.w.s x19, f9, rtz
	lhu x24, 142(s0)
	addi x21, x5, 33
	add  x21, x21, x21
	slli x21, x21, 1
	flw f15, 92(s0)
	flw f26, 116(s0)
	fsub.s f27, f15, f26
	fsw f27, 236(s0)
	fcvt.w.s x9, f15, rtz
	flw f3, 12(s0)
	flw f24, 64(s0)
	fmax.s f2, f3, f24
	fsw f2, 204(s0)
	fcvt.w.s x19, f3, rtz
	flw f5, 220(s0)
	flw f3, 88(s0)
	fmin.s f11, f5, f3
	fsw f11, 68(s0)
	fcvt.w.s x7, f5, rtz
	flw f6, 252(s0)
	flw f18, 88(s0)
	fsub.s f0, f6, f18
	fsw f0, 120(s0)
	fcvt.w.s x15, f6, rtz
	mulhu x17, x24, x21
	flw f23, 20(s0)
	flw f30, 156(s0)
	fadd.s f13, f23, f30
	fsw f13, 204(s0)
	fcvt.w.s x10, f23, rtz
	sb x30, 220(s0)
	lw x4, 176(s0)
	auipc x13, 454043
	slli x27, x22, 24
	flw f24, 40(s0)
	flw f12, 164(s0)
	fmul.s f27, f24, f12
	fsw f27, 112(s0)
	fcvt.w.s x21, f24, rtz
	sltiu x12, x3, 490
	sw x7, 176(s0)
	lh x16, 252(s0)
	addi x4, x13, -1073
	add  x4, x4, x4
	slli x4, x4, 1
	lbu x29, 1(s0)
	auipc x22, 539031
	sw x16, 44(s0)
	blt x27, x31, .Lsk_14
	nop
.Lsk_14:
	flw f0, 196(s0)
	flw f3, 0(s0)
	fmul.s f4, f0, f3
	fsw f4, 228(s0)
	fcvt.w.s x12, f0, rtz
	sltiu x11, x6, 1971
	lb x15, 252(s0)
	sb x26, 200(s0)
	flw f16, 252(s0)
	flw f15, 184(s0)
	fsub.s f16, f16, f15
	fsw f16, 120(s0)
	fcvt.w.s x7, f16, rtz
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
	.word 0x50778504
	.word 0xD5E70E46
	.word 0xC4642375
	.word 0x1CA97D35
	.word 0x7FAD07D6
	.word 0x706FDD61
	.word 0xC3F9A9DD
	.word 0x7B3ACA23
	.word 0x9DD3F14E
	.word 0xCAAE40EA
	.word 0xA327DF3E
	.word 0xC0AF3AE2
	.word 0x235552E7
	.word 0x914276CB
	.word 0xA47E76AB
	.word 0x76319584
	.word 0x87B58A64
	.word 0x1921BAC4
	.word 0x9C3809F3
	.word 0xEE21966B
	.word 0xDD96C4EC
	.word 0x923B2A0E
	.word 0xC1547F3C
	.word 0xEE4BF275
	.word 0x22B0E62A
	.word 0x642BD303
	.word 0x45AB3A20
	.word 0xD7CFA36E
	.word 0xA9078ADD
	.word 0x72F0C2FC
	.word 0xC761D786
	.word 0x8DA0C29B
	.word 0x2191364F
	.word 0xD37004A8
	.word 0x3FB8472E
	.word 0x71F071B9
	.word 0xBC4E1054
	.word 0x49E743E7
	.word 0xEE8439FA
	.word 0x4F1C58E6
	.word 0xFE269DB0
	.word 0x4D515471
	.word 0x33F158D8
	.word 0xB320D1EB
	.word 0x9A93C8BC
	.word 0xFFA6C17D
	.word 0xEBACAA89
	.word 0x5C0C0EFC
	.word 0x6EB07D86
	.word 0x713DFDC1
	.word 0xACB283D8
	.word 0xEDBEC0B3
	.word 0x771ADA36
	.word 0x61F30715
	.word 0x9FCAC108
	.word 0xAA57618A
	.word 0x0E15DBDC
	.word 0x53CF31A5
	.word 0x853837B6
	.word 0x03836A2A
	.word 0x0D1618ED
	.word 0x0D65480B
	.word 0xE278C425
	.word 0xB56B6FD7
