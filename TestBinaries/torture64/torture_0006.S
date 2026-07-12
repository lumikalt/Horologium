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
	ori x13, x13, 1160
	lb x11, 44(s0)
	flw f27, 76(s0)
	flw f2, 0(s0)
	fmin.s f6, f27, f2
	fsw f6, 68(s0)
	fcvt.w.s x29, f27, rtz
	addiw x11, x9, -590
	andi x24, x9, -813
	addi x19, x10, 852
	add  x19, x19, x19
	slli x19, x19, 1
	addi x15, x19, -502
	lui x10, 287619
	sb x4, 159(s0)
	mulhu x13, x15, x14
	add x29, x16, x31
	lw x3, 228(s0)
	addi x26, x22, 1062
	add  x26, x26, x26
	slli x26, x26, 1
	lh x29, 146(s0)
	addi x14, x17, -344
	add  x14, x14, x14
	slli x14, x14, 1
	and x7, x3, x12
	addi x30, x30, 1348
	add  x30, x30, x30
	slli x30, x30, 1
	sllw x4, x20, x11
	sb x10, 98(s0)
	auipc x26, 1045639
	addi x9, x5, 1673
	add  x9, x9, x9
	slli x9, x9, 1
	sw x10, 184(s0)
	sub x3, x27, x24
	sw x12, 220(s0)
	divu x9, x18, x25
	addi x31, x4, -398
	add  x31, x31, x31
	slli x31, x31, 1
	sb x17, 152(s0)
	flw f19, 76(s0)
	flw f27, 184(s0)
	fmin.s f8, f19, f27
	fsw f8, 56(s0)
	fcvt.w.s x25, f19, rtz
	lhu x19, 78(s0)
	lui x17, 351495
	or x24, x16, x21
	sltu x3, x29, x7
	bgeu x6, x7, .Lsk_0
	nop
.Lsk_0:
	addiw x19, x18, 1449
	flw f5, 52(s0)
	flw f9, 56(s0)
	fmax.s f30, f5, f9
	fsw f30, 52(s0)
	fcvt.w.s x29, f5, rtz
	lw x27, 128(s0)
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
	beq x10, x25, .Lsk_1
	nop
.Lsk_1:
	subw x7, x14, x31
	lh x29, 224(s0)
	lhu x24, 170(s0)
	lh x16, 224(s0)
	addi x22, x12, -689
	add  x22, x22, x22
	slli x22, x22, 1
	bgeu x30, x27, .Lsk_2
	nop
.Lsk_2:
	sra x29, x13, x4
	sh x31, 22(s0)
	sltu x17, x17, x17
	and x25, x27, x12
	sw x11, 64(s0)
	fld f0, 176(s0)
	fld f18, 232(s0)
	fmax.d f31, f0, f18
	fsd f31, 88(s0)
	fcvt.l.d x13, f0, rtz
	divw x25, x29, x4
	sw x26, 96(s0)
	lw x15, 48(s0)
	bge x10, x10, .Lsk_3
	nop
.Lsk_3:
	fld f30, 200(s0)
	fld f13, 16(s0)
	fsub.d f18, f30, f13
	fsd f18, 224(s0)
	fcvt.l.d x3, f30, rtz
	sd x24, 32(s0)
	sd x15, 40(s0)
	addi x11, x4, 1275
	add  x11, x11, x11
	slli x11, x11, 1
	sh x16, 12(s0)
	lb x28, 157(s0)
	flw f8, 36(s0)
	flw f17, 24(s0)
	fmin.s f5, f8, f17
	fsw f5, 60(s0)
	fcvt.w.s x3, f8, rtz
	flw f2, 44(s0)
	flw f5, 32(s0)
	fsub.s f23, f2, f5
	fsw f23, 88(s0)
	fcvt.w.s x5, f2, rtz
	sra x18, x24, x28
	sw x22, 132(s0)
	lb x21, 40(s0)
	slliw x27, x20, 8
	addi x30, x13, 1612
	add  x30, x30, x30
	slli x30, x30, 1
	addi x5, x30, 654
	add  x5, x5, x5
	slli x5, x5, 1
	blt x7, x3, .Lsk_4
	nop
.Lsk_4:
	srliw x5, x21, 12
	bgeu x22, x13, .Lsk_5
	nop
.Lsk_5:
	fld f31, 40(s0)
	fld f28, 144(s0)
	fsub.d f2, f31, f28
	fsd f2, 72(s0)
	fcvt.l.d x21, f31, rtz
	lw x9, 140(s0)
	sltu x12, x5, x10
	srliw x20, x3, 1
	fld f13, 112(s0)
	fld f31, 216(s0)
	fmul.d f13, f13, f31
	fsd f13, 48(s0)
	fcvt.l.d x16, f13, rtz
	add x16, x23, x22
	bne x7, x12, .Lsk_6
	nop
.Lsk_6:
	sh x25, 6(s0)
	divw x7, x7, x3
	addiw x27, x30, -1668
	bne x4, x18, .Lsk_7
	nop
.Lsk_7:
	fld f20, 64(s0)
	fld f5, 168(s0)
	fadd.d f1, f20, f5
	fsd f1, 112(s0)
	fcvt.l.d x18, f20, rtz
	sh x31, 106(s0)
	bge x23, x27, .Lsk_8
	nop
.Lsk_8:
	auipc x12, 821707
	addi x7, x22, 74
	add  x7, x7, x7
	slli x7, x7, 1
	slti x31, x20, 1283
	addi x31, x25, 1759
	add  x31, x31, x31
	slli x31, x31, 1
	addi x13, x26, -657
	add  x13, x13, x13
	slli x13, x13, 1
	lhu x16, 48(s0)
	fld f5, 56(s0)
	fld f9, 96(s0)
	fmul.d f15, f5, f9
	fsd f15, 232(s0)
	fcvt.l.d x17, f5, rtz
	srlw x24, x3, x6
	sh x11, 116(s0)
	fld f25, 176(s0)
	fld f26, 168(s0)
	fmin.d f5, f25, f26
	fsd f5, 16(s0)
	fcvt.l.d x25, f25, rtz
	sh x17, 48(s0)
	sb x17, 212(s0)
	sd x30, 16(s0)
	sraw x9, x6, x27
	sltu x23, x22, x18
	add x11, x14, x11
	addi x25, x25, -720
	add  x25, x25, x25
	slli x25, x25, 1
	sb x25, 224(s0)
	lw x6, 180(s0)
	sd x29, 56(s0)
	flw f15, 216(s0)
	flw f27, 64(s0)
	fmax.s f30, f15, f27
	fsw f30, 108(s0)
	fcvt.w.s x9, f15, rtz
	mul x23, x29, x3
	lbu x12, 20(s0)
	fld f16, 112(s0)
	fld f13, 200(s0)
	fadd.d f12, f16, f13
	fsd f12, 144(s0)
	fcvt.l.d x31, f16, rtz
	lw x11, 40(s0)
	sltiu x22, x9, 1046
	sb x10, 123(s0)
	sh x30, 82(s0)
	remw x10, x11, x9
	addw x17, x18, x30
	addi x13, x21, -276
	add  x13, x13, x13
	slli x13, x13, 1
	addi x17, x10, -229
	add  x17, x17, x17
	slli x17, x17, 1
	srlw x23, x23, x21
	addi x19, x5, 685
	add  x19, x19, x19
	slli x19, x19, 1
	sb x18, 100(s0)
	lw x6, 104(s0)
	sltiu x7, x7, 1758
	sraiw x11, x30, 21
	slti x30, x18, 946
	fld f23, 112(s0)
	fld f31, 152(s0)
	fsub.d f3, f23, f31
	fsd f3, 56(s0)
	fcvt.l.d x26, f23, rtz
	bltu x23, x21, .Lsk_9
	nop
.Lsk_9:
	srliw x19, x9, 20
	lh x17, 78(s0)
	ld x9, 88(s0)
	lh x18, 230(s0)
	lwu x30, 140(s0)
	blt x15, x19, .Lsk_10
	nop
.Lsk_10:
	mulw x24, x3, x18
	divw x21, x17, x12
	addi x7, x15, 1410
	add  x7, x7, x7
	slli x7, x7, 1
	bltu x19, x16, .Lsk_11
	nop
.Lsk_11:
	flw f7, 124(s0)
	flw f15, 40(s0)
	fadd.s f25, f7, f15
	fsw f25, 108(s0)
	fcvt.w.s x6, f7, rtz
	addi x3, x13, 2039
	add  x3, x3, x3
	slli x3, x3, 1
	add x4, x17, x21
	srliw x24, x15, 5
	addi x21, x30, -1461
	add  x21, x21, x21
	slli x21, x21, 1
	flw f16, 240(s0)
	flw f3, 212(s0)
	fmin.s f1, f16, f3
	fsw f1, 180(s0)
	fcvt.w.s x5, f16, rtz
	blt x10, x19, .Lsk_12
	nop
.Lsk_12:
	addi x16, x9, -1920
	add  x16, x16, x16
	slli x16, x16, 1
	bge x5, x10, .Lsk_13
	nop
.Lsk_13:
	lui x6, 446894
	sub x10, x18, x29
	addi x27, x11, 1900
	add  x27, x27, x27
	slli x27, x27, 1
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
	.word 0x26CD1B63
	.word 0x473EB4C5
	.word 0x4A18892B
	.word 0x4279E670
	.word 0xB493720F
	.word 0x33C347D9
	.word 0xF69E6511
	.word 0x950B4107
	.word 0x8E216DAD
	.word 0x104B8CA9
	.word 0x1A99A425
	.word 0x26DBE0A3
	.word 0x32BC1698
	.word 0x3EEEB877
	.word 0x1A12296C
	.word 0x9B2C5687
	.word 0x6D930DB1
	.word 0x16497D27
	.word 0x3B6EE8A2
	.word 0x682D4292
	.word 0xCF891075
	.word 0x0D3D0B92
	.word 0xE4ED2827
	.word 0x5B6FEAFB
	.word 0xD6B3ECD0
	.word 0x410400D8
	.word 0xFEF1422C
	.word 0x250C539E
	.word 0x0F07045C
	.word 0x05B7453B
	.word 0x3D44BB4B
	.word 0xEF66D76A
	.word 0x38E9BC0D
	.word 0x4EE1CA71
	.word 0x7AA80DDE
	.word 0xD5C0C50A
	.word 0x33F0D12D
	.word 0x03EB6936
	.word 0x772F4708
	.word 0x10938E32
	.word 0xE99EE710
	.word 0x946B21A9
	.word 0xA2C83BF1
	.word 0xEFB8806C
	.word 0x4984A77B
	.word 0x02F42A42
	.word 0xEF3A28EF
	.word 0x4AEA0D28
	.word 0x93D36C15
	.word 0x2160D289
	.word 0x9539CA06
	.word 0x403E99D7
	.word 0x910F71A5
	.word 0x6CEA4FB3
	.word 0x35D0633A
	.word 0xD9E97611
	.word 0x35AB1E32
	.word 0x42334D96
	.word 0x2118317C
	.word 0xA64D4A0F
	.word 0x3FCEF15F
	.word 0xC1F798A4
	.word 0x28E8B8A2
	.word 0xA73D58F4
