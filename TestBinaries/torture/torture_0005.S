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
	addi x3, x9, -402
	beq x9, x6, .Lsk_0
	nop
.Lsk_0:
	lh x20, 252(s0)
	rem x18, x25, x7
	blt x15, x27, .Lsk_1
	nop
.Lsk_1:
	and x29, x27, x30
	mul x3, x21, x10
	flw f22, 56(s0)
	flw f16, 140(s0)
	fmin.s f7, f22, f16
	fsw f7, 108(s0)
	fcvt.w.s x24, f22, rtz
	lb x17, 239(s0)
	lui x25, 298317
	mul x16, x24, x20
	auipc x6, 235690
	sh x26, 24(s0)
	sra x24, x12, x27
	bgeu x15, x26, .Lsk_2
	nop
.Lsk_2:
	lbu x3, 118(s0)
	auipc x4, 43354
	blt x16, x14, .Lsk_3
	nop
.Lsk_3:
	slli x18, x7, 26
	lhu x6, 12(s0)
	slt x26, x17, x14
	bne x11, x28, .Lsk_4
	nop
.Lsk_4:
	sw x13, 100(s0)
	srai x20, x29, 25
	remu x21, x6, x18
	add x4, x29, x3
	xor x12, x23, x5
	lh x9, 166(s0)
	bgeu x31, x26, .Lsk_5
	nop
.Lsk_5:
	flw f8, 216(s0)
	flw f20, 76(s0)
	fadd.s f11, f8, f20
	fsw f11, 192(s0)
	fcvt.w.s x14, f8, rtz
	flw f1, 12(s0)
	flw f27, 140(s0)
	fmin.s f9, f1, f27
	fsw f9, 0(s0)
	fcvt.w.s x22, f1, rtz
	add x27, x25, x21
	rem x4, x30, x26
	srai x24, x15, 10
	auipc x4, 516851
	lh x4, 6(s0)
	lw x15, 84(s0)
	lh x20, 106(s0)
	sh x4, 158(s0)
	addi x30, x24, -893
	add  x30, x30, x30
	slli x30, x30, 1
	lw x20, 132(s0)
	lhu x7, 28(s0)
	div x16, x16, x13
	addi x26, x27, 1622
	add  x26, x26, x26
	slli x26, x26, 1
	bgeu x25, x27, .Lsk_6
	nop
.Lsk_6:
	sub x18, x12, x5
	sh x28, 94(s0)
	sh x21, 84(s0)
	sw x28, 44(s0)
	addi x31, x23, 1575
	add  x31, x31, x31
	slli x31, x31, 1
	bge x16, x23, .Lsk_7
	nop
.Lsk_7:
	bltu x24, x20, .Lsk_8
	nop
.Lsk_8:
	sub x17, x24, x20
	div x3, x18, x14
	blt x14, x12, .Lsk_9
	nop
.Lsk_9:
	lui x24, 409695
	bgeu x11, x18, .Lsk_10
	nop
.Lsk_10:
	flw f30, 172(s0)
	flw f17, 224(s0)
	fmin.s f0, f30, f17
	fsw f0, 188(s0)
	fcvt.w.s x23, f30, rtz
	flw f5, 96(s0)
	flw f9, 24(s0)
	fmax.s f27, f5, f9
	fsw f27, 252(s0)
	fcvt.w.s x24, f5, rtz
	mul x7, x12, x26
	addi x26, x3, 1769
	add  x26, x26, x26
	slli x26, x26, 1
	slt x11, x24, x18
	addi x10, x30, -526
	add  x10, x10, x10
	slli x10, x10, 1
	slt x11, x21, x9
	srai x30, x24, 30
	srli x30, x28, 5
	lh x13, 6(s0)
	rem x10, x25, x26
	mulh x18, x20, x18
	lw x13, 64(s0)
	divu x26, x15, x11
	slt x19, x29, x4
	blt x20, x15, .Lsk_11
	nop
.Lsk_11:
	lw x25, 72(s0)
	blt x13, x25, .Lsk_12
	nop
.Lsk_12:
	mulhsu x25, x7, x13
	addi x17, x10, -1102
	add  x17, x17, x17
	slli x17, x17, 1
	srai x22, x27, 10
	or x22, x11, x31
	lh x5, 42(s0)
	sb x14, 148(s0)
	flw f3, 44(s0)
	flw f26, 160(s0)
	fmin.s f26, f3, f26
	fsw f26, 208(s0)
	fcvt.w.s x11, f3, rtz
	andi x9, x20, -896
	lb x6, 118(s0)
	flw f21, 176(s0)
	flw f23, 80(s0)
	fmax.s f23, f21, f23
	fsw f23, 88(s0)
	fcvt.w.s x24, f21, rtz
	beq x28, x10, .Lsk_13
	nop
.Lsk_13:
	sra x31, x24, x4
	sltu x18, x9, x25
	mul x11, x11, x24
	div x9, x6, x14
	addi x3, x25, -1558
	add  x3, x3, x3
	slli x3, x3, 1
	blt x17, x15, .Lsk_14
	nop
.Lsk_14:
	bge x29, x15, .Lsk_15
	nop
.Lsk_15:
	sh x16, 102(s0)
	slti x26, x30, 990
	add x22, x9, x18
	flw f13, 184(s0)
	flw f22, 32(s0)
	fadd.s f21, f13, f22
	fsw f21, 72(s0)
	fcvt.w.s x12, f13, rtz
	sh x12, 254(s0)
	ori x29, x16, 1791
	srai x24, x22, 22
	auipc x18, 232594
	flw f6, 60(s0)
	flw f18, 168(s0)
	fmul.s f9, f6, f18
	fsw f9, 152(s0)
	fcvt.w.s x28, f6, rtz
	addi x11, x23, 1268
	add  x11, x11, x11
	slli x11, x11, 1
	lw x23, 188(s0)
	lbu x24, 103(s0)
	addi x20, x5, -2013
	add  x20, x20, x20
	slli x20, x20, 1
	auipc x10, 907643
	lhu x14, 124(s0)
	ori x5, x22, 105
	lbu x25, 161(s0)
	flw f23, 112(s0)
	flw f6, 176(s0)
	fmax.s f9, f23, f6
	fsw f9, 148(s0)
	fcvt.w.s x26, f23, rtz
	lhu x4, 46(s0)
	mulhu x16, x24, x12
	sll x6, x10, x22
	lb x23, 44(s0)
	auipc x4, 766108
	flw f10, 144(s0)
	flw f15, 132(s0)
	fmin.s f22, f10, f15
	fsw f22, 184(s0)
	fcvt.w.s x21, f10, rtz
	addi x31, x17, -1564
	blt x17, x12, .Lsk_16
	nop
.Lsk_16:
	flw f18, 204(s0)
	flw f0, 100(s0)
	fmul.s f13, f18, f0
	fsw f13, 36(s0)
	fcvt.w.s x17, f18, rtz
	sw x6, 252(s0)
	bge x15, x12, .Lsk_17
	nop
.Lsk_17:
	sw x10, 136(s0)
	lb x29, 14(s0)
	beq x26, x14, .Lsk_18
	nop
.Lsk_18:
	flw f2, 204(s0)
	flw f31, 36(s0)
	fmul.s f7, f2, f31
	fsw f7, 212(s0)
	fcvt.w.s x21, f2, rtz
	lhu x22, 6(s0)
	xor x30, x23, x20
	auipc x19, 43281
	lbu x24, 154(s0)
	addi x16, x19, 1887
	add  x16, x16, x16
	slli x16, x16, 1
	addi x29, x6, 1455
	add  x29, x29, x29
	slli x29, x29, 1
	rem x30, x21, x10
	auipc x31, 470961
	blt x19, x20, .Lsk_19
	nop
.Lsk_19:
	auipc x11, 61248
	flw f29, 52(s0)
	flw f17, 72(s0)
	fmax.s f19, f29, f17
	fsw f19, 236(s0)
	fcvt.w.s x6, f29, rtz
	blt x31, x29, .Lsk_20
	nop
.Lsk_20:
	bltu x17, x20, .Lsk_21
	nop
.Lsk_21:
	lw x29, 100(s0)
	auipc x24, 189274
	addi x13, x14, 560
	add  x13, x13, x13
	slli x13, x13, 1
	slt x17, x30, x7
	lh x22, 80(s0)
	lbu x5, 113(s0)
	slt x18, x23, x25
	bgeu x27, x19, .Lsk_22
	nop
.Lsk_22:
	sw x6, 244(s0)
	xori x15, x26, -212
	lb x14, 88(s0)
	lh x13, 174(s0)
	addi x9, x28, -762
	add  x9, x9, x9
	slli x9, x9, 1
	beq x17, x12, .Lsk_23
	nop
.Lsk_23:
	sh x23, 158(s0)
	lw x24, 44(s0)
	slti x30, x12, 1684
	lui x9, 41665
	srli x15, x14, 7
	bge x31, x13, .Lsk_24
	nop
.Lsk_24:
	sh x28, 196(s0)
	bne x27, x31, .Lsk_25
	nop
.Lsk_25:
	addi x13, x26, 201
	add  x13, x13, x13
	slli x13, x13, 1
	sra x6, x11, x19
	blt x29, x26, .Lsk_26
	nop
.Lsk_26:
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
	.word 0xB54A1BB5
	.word 0xBF42FD8D
	.word 0x2DE8FC8E
	.word 0x0D091B42
	.word 0xB3970309
	.word 0x5DB2A308
	.word 0x8804E451
	.word 0xEDD44DED
	.word 0x2B1769B9
	.word 0x8F1693A2
	.word 0xD436EEB6
	.word 0x4AF18980
	.word 0xA153D790
	.word 0x8C2B8F22
	.word 0x6FBBD713
	.word 0x027F859D
	.word 0xAC1EDBB9
	.word 0x6772208E
	.word 0x96ABE1AF
	.word 0x409F4BDF
	.word 0xE895D851
	.word 0x75F03A05
	.word 0x576F2EB7
	.word 0x6F134B60
	.word 0x4109D960
	.word 0xC401DE6C
	.word 0x2858840A
	.word 0x5ACB424D
	.word 0x3C8878AF
	.word 0x0C96D46A
	.word 0x543F310D
	.word 0xB8C4479D
	.word 0xA812FDAC
	.word 0xBB37D004
	.word 0xF5774497
	.word 0x5AF5EE63
	.word 0x1F127AD3
	.word 0xF6D77959
	.word 0xD7007029
	.word 0x9F674975
	.word 0x90C35944
	.word 0x8791D6FE
	.word 0xEF9EADD8
	.word 0xFBBB6476
	.word 0x8014E648
	.word 0x73684DC3
	.word 0x4FD6CE15
	.word 0xC92D6DD7
	.word 0xB6FE6D31
	.word 0x18427B84
	.word 0x091C9ABB
	.word 0x00FE430C
	.word 0xC02B91E6
	.word 0x260D3047
	.word 0x80F1204F
	.word 0x157A69BD
	.word 0x4859F71C
	.word 0x0C20146E
	.word 0x675DFE21
	.word 0x02C94132
	.word 0x5EB6250F
	.word 0x7F0BB906
	.word 0x1F8C3D8E
	.word 0x64E0930D
