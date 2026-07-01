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
	ori x13, x13, 1160
	lb x11, 44(s0)
	flw f27, 236(s0)
	flw f2, 40(s0)
	fadd.s f6, f27, f2
	fsw f6, 76(s0)
	fcvt.w.s x29, f27, rtz
	flw f25, 92(s0)
	flw f13, 88(s0)
	fmin.s f26, f25, f13
	fsw f26, 88(s0)
	fcvt.w.s x11, f25, rtz
	beq x25, x24, .Lsk_0
	nop
.Lsk_0:
	addi x20, x28, -813
	add  x20, x20, x20
	slli x20, x20, 1
	addi x19, x10, 852
	add  x19, x19, x19
	slli x19, x19, 1
	addi x15, x19, -502
	lui x10, 287619
	sb x31, 178(s0)
	divu x13, x4, x14
	rem x14, x3, x9
	addi x31, x20, 1014
	add  x31, x31, x31
	slli x31, x31, 1
	xori x18, x25, 1483
	divu x29, x28, x17
	xor x6, x7, x29
	sw x30, 212(s0)
	divu x4, x20, x11
	sh x10, 98(s0)
	auipc x26, 1045639
	addi x9, x5, 1673
	add  x9, x9, x9
	slli x9, x9, 1
	sw x10, 184(s0)
	sub x3, x27, x24
	sb x4, 130(s0)
	addi x22, x17, -847
	add  x22, x22, x22
	slli x22, x22, 1
	mul x18, x12, x29
	div x17, x13, x25
	lbu x20, 66(s0)
	blt x7, x26, .Lsk_1
	nop
.Lsk_1:
	remu x6, x17, x19
	sw x17, 188(s0)
	addi x3, x24, -675
	add  x3, x3, x3
	slli x3, x3, 1
	or x24, x16, x21
	sltu x3, x29, x7
	bgeu x6, x7, .Lsk_2
	nop
.Lsk_2:
	auipc x19, 835806
	sub x19, x29, x30
	srai x6, x24, 0
	bgeu x24, x12, .Lsk_3
	nop
.Lsk_3:
	addi x28, x14, 86
	add  x28, x28, x28
	slli x28, x28, 1
	addi x5, x18, -890
	add  x5, x5, x5
	slli x5, x5, 1
	lbu x29, 96(s0)
	addi x3, x19, 1386
	add  x3, x3, x3
	slli x3, x3, 1
	addi x18, x30, -1333
	add  x18, x18, x18
	slli x18, x18, 1
	beq x10, x25, .Lsk_4
	nop
.Lsk_4:
	mulhu x7, x14, x31
	lh x29, 224(s0)
	lhu x24, 170(s0)
	lh x16, 224(s0)
	addi x22, x12, -689
	add  x22, x22, x22
	slli x22, x22, 1
	bgeu x30, x27, .Lsk_5
	nop
.Lsk_5:
	sra x29, x13, x4
	sw x31, 20(s0)
	sltu x17, x17, x17
	and x25, x27, x12
	sb x31, 7(s0)
	addi x7, x30, -2030
	add  x7, x7, x7
	slli x7, x7, 1
	blt x29, x21, .Lsk_6
	nop
.Lsk_6:
	flw f31, 180(s0)
	flw f19, 232(s0)
	fmax.s f4, f31, f19
	fsw f4, 92(s0)
	fcvt.w.s x27, f31, rtz
	mulhsu x25, x29, x4
	sw x26, 96(s0)
	lw x15, 48(s0)
	bge x10, x10, .Lsk_7
	nop
.Lsk_7:
	flw f30, 204(s0)
	flw f13, 20(s0)
	fsub.s f18, f30, f13
	fsw f18, 228(s0)
	fcvt.w.s x3, f30, rtz
	sb x18, 37(s0)
	sh x15, 40(s0)
	addi x11, x4, 1275
	add  x11, x11, x11
	slli x11, x11, 1
	sw x16, 12(s0)
	lb x28, 157(s0)
	flw f8, 100(s0)
	flw f17, 132(s0)
	fadd.s f5, f8, f17
	fsw f5, 36(s0)
	fcvt.w.s x3, f8, rtz
	addi x29, x24, -1053
	add  x29, x29, x29
	slli x29, x29, 1
	addi x16, x28, -1678
	add  x16, x16, x16
	slli x16, x16, 1
	lui x5, 361054
	lui x4, 460235
	lbu x10, 37(s0)
	sb x9, 153(s0)
	sw x12, 36(s0)
	sltiu x7, x30, 1612
	addi x5, x30, 654
	add  x5, x5, x5
	slli x5, x5, 1
	blt x7, x3, .Lsk_8
	nop
.Lsk_8:
	sltiu x5, x21, -404
	bgeu x22, x13, .Lsk_9
	nop
.Lsk_9:
	flw f31, 44(s0)
	flw f28, 144(s0)
	fsub.s f2, f31, f28
	fsw f2, 72(s0)
	fcvt.w.s x21, f31, rtz
	lw x9, 140(s0)
	sltu x12, x5, x10
	xori x20, x3, -1915
	flw f13, 112(s0)
	flw f31, 216(s0)
	fmul.s f13, f13, f31
	fsw f13, 52(s0)
	fcvt.w.s x16, f13, rtz
	add x16, x23, x22
	bne x7, x12, .Lsk_10
	nop
.Lsk_10:
	sb x11, 7(s0)
	mulhsu x7, x7, x3
	lui x27, 187233
	sltiu x4, x18, -1344
	addi x18, x25, -950
	add  x18, x18, x18
	slli x18, x18, 1
	sll x29, x27, x11
	addi x27, x13, 1196
	add  x27, x27, x27
	slli x27, x27, 1
	sw x19, 140(s0)
	lhu x16, 226(s0)
	or x29, x12, x27
	sw x17, 236(s0)
	addi x13, x26, -657
	add  x13, x13, x13
	slli x13, x13, 1
	lhu x16, 48(s0)
	flw f5, 192(s0)
	flw f9, 60(s0)
	fmin.s f15, f5, f9
	fsw f15, 96(s0)
	fcvt.w.s x17, f5, rtz
	or x3, x24, x16
	addi x6, x21, 493
	add  x6, x6, x6
	slli x6, x6, 1
	and x30, x27, x28
	lbu x5, 183(s0)
	lb x4, 245(s0)
	sw x17, 48(s0)
	sh x17, 212(s0)
	sb x16, 17(s0)
	mulhu x9, x6, x23
	sh x18, 64(s0)
	add x11, x14, x11
	addi x25, x25, -720
	add  x25, x25, x25
	slli x25, x25, 1
	sh x25, 224(s0)
	lw x6, 180(s0)
	sb x18, 62(s0)
	flw f15, 124(s0)
	flw f27, 216(s0)
	fsub.s f30, f15, f27
	fsw f30, 64(s0)
	fcvt.w.s x9, f15, rtz
	sb x23, 120(s0)
	addi x4, x3, 1093
	add  x4, x4, x4
	slli x4, x4, 1
	sltiu x12, x4, -348
	mulh x6, x11, x16
	lh x24, 42(s0)
	sltiu x22, x9, 1046
	sh x10, 122(s0)
	sb x7, 83(s0)
	srli x10, x11, 11
	mulh x17, x18, x30
	addi x13, x21, -276
	add  x13, x13, x13
	slli x13, x13, 1
	addi x17, x10, -229
	add  x17, x17, x17
	slli x17, x17, 1
	rem x23, x23, x21
	addi x19, x5, 685
	add  x19, x19, x19
	slli x19, x19, 1
	sh x18, 100(s0)
	lw x6, 104(s0)
	sltiu x7, x7, 1758
	ori x11, x30, 681
	slti x30, x18, 946
	flw f23, 160(s0)
	flw f31, 116(s0)
	fadd.s f3, f23, f31
	fsw f3, 156(s0)
	fcvt.w.s x26, f23, rtz
	flw f8, 84(s0)
	flw f2, 132(s0)
	fmin.s f2, f8, f2
	fsw f2, 160(s0)
	fcvt.w.s x19, f8, rtz
	bge x7, x16, .Lsk_11
	nop
.Lsk_11:
	lh x9, 222(s0)
	auipc x18, 582146
	blt x15, x19, .Lsk_12
	nop
.Lsk_12:
	slli x24, x3, 28
	slli x21, x17, 17
	addi x7, x15, 1410
	add  x7, x7, x7
	slli x7, x7, 1
	bltu x19, x16, .Lsk_13
	nop
.Lsk_13:
	flw f7, 80(s0)
	flw f15, 124(s0)
	fsub.s f25, f7, f15
	fsw f25, 40(s0)
	fcvt.w.s x6, f7, rtz
	bne x3, x13, .Lsk_14
	nop
.Lsk_14:
	addi x19, x6, -1543
	add  x19, x19, x19
	slli x19, x19, 1
	lh x4, 80(s0)
	sh x24, 174(s0)
	auipc x25, 100797
	addi x30, x3, -1325
	add  x30, x30, x30
	slli x30, x30, 1
	lhu x17, 182(s0)
	addi x25, x21, 946
	add  x25, x25, x25
	slli x25, x25, 1
	addi x19, x25, 1094
	add  x19, x19, x19
	slli x19, x19, 1
	srli x3, x24, 5
	ori x19, x6, -493
	auipc x16, 276682
	sw x29, 68(s0)
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
	.word 0xBBF00BE4
	.word 0xCF76EA9B
	.word 0x6D1BA976
	.word 0x5A3B68AA
	.word 0xF93C13DF
	.word 0x8D074517
	.word 0x496FD644
	.word 0xB9A1F736
	.word 0x5E4EE83E
	.word 0x06CF04DC
	.word 0x3C5AF9FD
	.word 0xA4760719
	.word 0xDE1E8AE0
	.word 0x583798F2
	.word 0x1BF85061
	.word 0x77FBC603
	.word 0x166E4C68
	.word 0x8FD22AC8
	.word 0x10B8EF58
	.word 0x178086E1
	.word 0xC625668C
	.word 0x687232EB
	.word 0x3D718EE6
	.word 0x8171EFEA
	.word 0xFCF5DCD9
	.word 0xBD79F60A
	.word 0x7AF22B20
	.word 0x5D024F91
	.word 0x8029195B
	.word 0xE4C0304E
	.word 0x175D3651
	.word 0x257916E9
	.word 0x21A07C5F
	.word 0x4B503EBA
	.word 0x64FBB113
	.word 0x4F89E12D
	.word 0x31652ACF
	.word 0x5D7C403C
	.word 0xA1160263
	.word 0x1C26A9CD
	.word 0xD6615C6F
	.word 0xD31F4EA7
	.word 0xD450DFD2
	.word 0x9E59E6B5
	.word 0xCDAD5F46
	.word 0xD3BC5E80
	.word 0x9B6708C6
	.word 0xD03CDB4B
	.word 0xD2D271DD
	.word 0x3052A612
	.word 0x48773660
	.word 0xA6E2FEA2
	.word 0x6CD5E4D7
	.word 0xACFC3DF1
	.word 0xC987A7BE
	.word 0x45273242
	.word 0xF4D006DD
	.word 0xE4DC4649
	.word 0x9AA92A14
	.word 0xC2F0AFAA
	.word 0xBC734983
	.word 0xA46004A0
	.word 0x76825F3C
	.word 0x34B154AB
