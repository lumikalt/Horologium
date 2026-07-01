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
	divu x26, x4, x25
	sw x6, 244(s0)
	lh x29, 210(s0)
	blt x14, x13, .Lsk_0
	nop
.Lsk_0:
	sw x24, 28(s0)
	sh x10, 154(s0)
	ori x16, x24, -611
	lb x15, 109(s0)
	bge x3, x20, .Lsk_1
	nop
.Lsk_1:
	addi x31, x7, -615
	add  x31, x31, x31
	slli x31, x31, 1
	xor x30, x13, x30
	beq x3, x15, .Lsk_2
	nop
.Lsk_2:
	add x9, x10, x19
	sh x21, 146(s0)
	addi x7, x25, -1419
	add  x7, x7, x7
	slli x7, x7, 1
	flw f22, 92(s0)
	flw f15, 104(s0)
	fsub.s f7, f22, f15
	fsw f7, 164(s0)
	fcvt.w.s x23, f22, rtz
	sb x21, 35(s0)
	ori x23, x12, -1858
	ori x15, x9, 393
	auipc x22, 149772
	addi x20, x28, 930
	add  x20, x20, x20
	slli x20, x20, 1
	flw f22, 8(s0)
	flw f22, 116(s0)
	fmax.s f1, f22, f22
	fsw f1, 24(s0)
	fcvt.w.s x15, f22, rtz
	slti x28, x27, 224
	xori x3, x21, -978
	mulh x28, x26, x5
	lhu x9, 18(s0)
	sra x5, x16, x10
	sw x20, 72(s0)
	sb x15, 238(s0)
	flw f25, 48(s0)
	flw f30, 192(s0)
	fmul.s f20, f25, f30
	fsw f20, 140(s0)
	fcvt.w.s x14, f25, rtz
	auipc x6, 247901
	addi x29, x22, -384
	add  x29, x29, x29
	slli x29, x29, 1
	sb x9, 250(s0)
	bne x24, x11, .Lsk_3
	nop
.Lsk_3:
	andi x11, x12, 1839
	auipc x22, 131808
	sh x16, 192(s0)
	flw f21, 56(s0)
	flw f4, 28(s0)
	fadd.s f15, f21, f4
	fsw f15, 176(s0)
	fcvt.w.s x24, f21, rtz
	lw x12, 168(s0)
	sub x17, x27, x13
	lui x30, 104544
	lb x25, 39(s0)
	sb x19, 131(s0)
	lw x22, 212(s0)
	addi x6, x24, 1581
	add  x6, x6, x6
	slli x6, x6, 1
	or x16, x19, x21
	sll x4, x28, x14
	slli x18, x28, 20
	lw x10, 60(s0)
	beq x30, x31, .Lsk_4
	nop
.Lsk_4:
	srli x18, x15, 27
	addi x31, x31, -357
	add  x31, x31, x31
	slli x31, x31, 1
	flw f9, 212(s0)
	flw f18, 196(s0)
	fmul.s f10, f9, f18
	fsw f10, 160(s0)
	fcvt.w.s x18, f9, rtz
	sll x23, x10, x24
	bge x22, x31, .Lsk_5
	nop
.Lsk_5:
	flw f21, 24(s0)
	flw f9, 20(s0)
	fmax.s f4, f21, f9
	fsw f4, 184(s0)
	fcvt.w.s x6, f21, rtz
	addi x10, x14, -1262
	add  x10, x10, x10
	slli x10, x10, 1
	mulh x24, x6, x14
	sb x17, 168(s0)
	slt x20, x23, x17
	sw x31, 184(s0)
	lbu x29, 214(s0)
	flw f0, 20(s0)
	flw f20, 188(s0)
	fmin.s f24, f0, f20
	fsw f24, 148(s0)
	fcvt.w.s x17, f0, rtz
	xor x19, x16, x12
	xor x25, x24, x24
	slt x19, x14, x6
	addi x6, x25, -2001
	add  x6, x6, x6
	slli x6, x6, 1
	lui x10, 814301
	sw x10, 148(s0)
	addi x18, x18, 1199
	add  x18, x18, x18
	slli x18, x18, 1
	slti x26, x30, 172
	flw f31, 84(s0)
	flw f25, 76(s0)
	fmax.s f31, f31, f25
	fsw f31, 168(s0)
	fcvt.w.s x4, f31, rtz
	sb x12, 252(s0)
	bne x27, x23, .Lsk_6
	nop
.Lsk_6:
	xor x16, x29, x31
	xor x21, x27, x15
	mulhsu x27, x10, x26
	sh x11, 100(s0)
	addi x16, x7, -14
	add  x16, x16, x16
	slli x16, x16, 1
	sra x17, x12, x29
	sra x19, x15, x4
	slt x25, x31, x13
	addi x9, x4, 1647
	add  x9, x9, x9
	slli x9, x9, 1
	sw x12, 20(s0)
	sw x19, 36(s0)
	addi x20, x3, -872
	add  x20, x20, x20
	slli x20, x20, 1
	lh x15, 224(s0)
	mulhsu x24, x13, x15
	flw f22, 52(s0)
	flw f24, 180(s0)
	fadd.s f17, f22, f24
	fsw f17, 120(s0)
	fcvt.w.s x22, f22, rtz
	sltu x4, x9, x4
	blt x6, x7, .Lsk_7
	nop
.Lsk_7:
	sltiu x5, x21, 453
	srl x6, x7, x3
	lh x22, 212(s0)
	sh x19, 52(s0)
	lw x21, 36(s0)
	sra x31, x7, x31
	lh x27, 46(s0)
	lui x20, 898360
	sh x13, 106(s0)
	bne x10, x31, .Lsk_8
	nop
.Lsk_8:
	bltu x30, x6, .Lsk_9
	nop
.Lsk_9:
	addi x25, x16, -285
	add  x25, x25, x25
	slli x25, x25, 1
	addi x6, x29, -456
	add  x6, x6, x6
	slli x6, x6, 1
	lbu x9, 11(s0)
	addi x19, x29, 1294
	add  x19, x19, x19
	slli x19, x19, 1
	addi x10, x12, 1045
	add  x10, x10, x10
	slli x10, x10, 1
	addi x31, x19, -445
	add  x31, x31, x31
	slli x31, x31, 1
	andi x27, x10, -1460
	remu x4, x25, x26
	lbu x7, 168(s0)
	lhu x4, 70(s0)
	sb x22, 166(s0)
	sb x18, 206(s0)
	beq x30, x30, .Lsk_10
	nop
.Lsk_10:
	addi x29, x25, -121
	add  x29, x29, x29
	slli x29, x29, 1
	sb x14, 44(s0)
	xori x28, x17, -839
	xori x10, x16, 912
	lui x7, 137683
	bne x16, x25, .Lsk_11
	nop
.Lsk_11:
	addi x13, x19, 164
	add  x13, x13, x13
	slli x13, x13, 1
	flw f29, 240(s0)
	flw f4, 40(s0)
	fmax.s f15, f29, f4
	fsw f15, 228(s0)
	fcvt.w.s x11, f29, rtz
	bne x6, x7, .Lsk_12
	nop
.Lsk_12:
	auipc x28, 17008
	addi x7, x14, 941
	add  x7, x7, x7
	slli x7, x7, 1
	lui x11, 35221
	bltu x12, x26, .Lsk_13
	nop
.Lsk_13:
	flw f15, 28(s0)
	flw f16, 188(s0)
	fmin.s f21, f15, f16
	fsw f21, 92(s0)
	fcvt.w.s x19, f15, rtz
	lh x15, 224(s0)
	addi x27, x16, -1411
	add  x27, x27, x27
	slli x27, x27, 1
	beq x14, x23, .Lsk_14
	nop
.Lsk_14:
	sub x5, x13, x3
	flw f4, 68(s0)
	flw f18, 236(s0)
	fmin.s f3, f4, f18
	fsw f3, 132(s0)
	fcvt.w.s x4, f4, rtz
	ori x7, x17, -749
	auipc x11, 877616
	divu x4, x5, x25
	addi x13, x21, -281
	add  x13, x13, x13
	slli x13, x13, 1
	addi x28, x16, -1590
	add  x28, x28, x28
	slli x28, x28, 1
	sw x3, 176(s0)
	lw x5, 108(s0)
	flw f1, 156(s0)
	flw f26, 36(s0)
	fmax.s f5, f1, f26
	fsw f5, 236(s0)
	fcvt.w.s x7, f1, rtz
	lhu x18, 62(s0)
	lb x9, 231(s0)
	flw f20, 252(s0)
	flw f18, 176(s0)
	fadd.s f26, f20, f18
	fsw f26, 128(s0)
	fcvt.w.s x22, f20, rtz
	lh x28, 16(s0)
	sltiu x30, x25, 238
	flw f1, 208(s0)
	flw f19, 100(s0)
	fsub.s f10, f1, f19
	fsw f10, 176(s0)
	fcvt.w.s x27, f1, rtz
	lbu x20, 129(s0)
	lh x13, 192(s0)
	sb x30, 97(s0)
	addi x28, x31, -886
	add  x28, x28, x28
	slli x28, x28, 1
	andi x14, x29, 1809
	sll x12, x3, x27
	lui x25, 212553
	xor x11, x10, x5
	flw f12, 120(s0)
	flw f12, 108(s0)
	fsub.s f28, f12, f12
	fsw f28, 104(s0)
	fcvt.w.s x12, f12, rtz
	bltu x31, x16, .Lsk_15
	nop
.Lsk_15:
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
	.word 0x380501E8
	.word 0x40BF3515
	.word 0x2461EDB8
	.word 0xBD2FD8C8
	.word 0x6A69AAD6
	.word 0xBBE071D2
	.word 0x1CDC22C7
	.word 0x732FCCB8
	.word 0x979B5FB4
	.word 0xEA1477F8
	.word 0x54AAC9B0
	.word 0x5277121D
	.word 0xFDA0049A
	.word 0xC3E61C58
	.word 0x55D94486
	.word 0x31F2932A
	.word 0xAB7E9E93
	.word 0x8726529E
	.word 0xF16C57BA
	.word 0x958B6850
	.word 0xC5796AB8
	.word 0x18AD0CD4
	.word 0x6CC74E3A
	.word 0xC054E99B
	.word 0x011F7C74
	.word 0x6678B117
	.word 0xA79B0B9A
	.word 0x965C3C39
	.word 0xC6AA0157
	.word 0xA412F91C
	.word 0xA3D48A86
	.word 0x55DF9200
	.word 0x874DC529
	.word 0x99D0ED83
	.word 0x99AE2B56
	.word 0x5D14F5F7
	.word 0xD1F19AD9
	.word 0x24206B14
	.word 0xC8E320BC
	.word 0xD026D180
	.word 0xC26CD667
	.word 0x6FC9F171
	.word 0xE4872606
	.word 0x3FB40B2B
	.word 0x72960390
	.word 0x87727F07
	.word 0x0F4BDC99
	.word 0xEA058F9D
	.word 0xA7E44BA9
	.word 0xE857B692
	.word 0xCF560FC1
	.word 0xBB890F13
	.word 0x35F33A99
	.word 0x1DC52A23
	.word 0xBDCCB207
	.word 0x1623A390
	.word 0xB0B963AF
	.word 0xDDA3A79A
	.word 0xF05B991C
	.word 0xF7F69401
	.word 0x7CD3BAD7
	.word 0xE303074C
	.word 0x9F119A66
	.word 0x44EF228D
