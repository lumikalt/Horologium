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
	sb x20, 210(s0)
	srai x31, x24, 22
	and x9, x16, x19
	blt x30, x5, .Lsk_0
	nop
.Lsk_0:
	lw x10, 28(s0)
	flw f6, 240(s0)
	flw f27, 176(s0)
	fsub.s f14, f6, f27
	fsw f14, 80(s0)
	fcvt.w.s x20, f6, rtz
	sw x29, 244(s0)
	flw f19, 208(s0)
	flw f25, 148(s0)
	fadd.s f14, f19, f25
	fsw f14, 172(s0)
	fcvt.w.s x14, f19, rtz
	sltu x23, x19, x30
	addi x10, x27, -896
	add  x10, x10, x10
	slli x10, x10, 1
	lbu x15, 164(s0)
	mul x11, x18, x17
	sb x21, 60(s0)
	and x3, x15, x20
	lbu x3, 131(s0)
	slt x17, x29, x22
	srai x7, x20, 29
	addi x9, x7, 969
	add  x9, x9, x9
	slli x9, x9, 1
	lh x26, 106(s0)
	slli x30, x10, 3
	addi x13, x17, -1037
	add  x13, x13, x13
	slli x13, x13, 1
	addi x18, x26, 1809
	add  x18, x18, x18
	slli x18, x18, 1
	lui x5, 997549
	auipc x16, 617586
	addi x19, x15, 360
	add  x19, x19, x19
	slli x19, x19, 1
	addi x6, x28, 779
	add  x6, x6, x6
	slli x6, x6, 1
	sh x7, 144(s0)
	addi x29, x16, 1632
	add  x29, x29, x29
	slli x29, x29, 1
	slt x17, x23, x13
	addi x26, x6, 1844
	add  x26, x26, x26
	slli x26, x26, 1
	lh x21, 208(s0)
	srli x30, x18, 18
	lw x29, 124(s0)
	lhu x22, 8(s0)
	lhu x3, 176(s0)
	auipc x26, 335507
	sltu x27, x5, x10
	slli x31, x25, 12
	beq x28, x9, .Lsk_1
	nop
.Lsk_1:
	addi x18, x12, 835
	add  x18, x18, x18
	slli x18, x18, 1
	lb x10, 7(s0)
	addi x5, x21, -1002
	add  x5, x5, x5
	slli x5, x5, 1
	add x14, x15, x5
	addi x11, x29, 1945
	add  x11, x11, x11
	slli x11, x11, 1
	lhu x23, 48(s0)
	lbu x15, 24(s0)
	mulhsu x9, x11, x3
	flw f26, 244(s0)
	flw f22, 68(s0)
	fmax.s f22, f26, f22
	fsw f22, 160(s0)
	fcvt.w.s x10, f26, rtz
	rem x29, x28, x16
	lui x22, 472384
	auipc x9, 125815
	xori x3, x28, -1152
	addi x19, x3, -1580
	add  x19, x19, x19
	slli x19, x19, 1
	lhu x31, 114(s0)
	addi x22, x28, 892
	add  x22, x22, x22
	slli x22, x22, 1
	mul x6, x11, x20
	bgeu x14, x27, .Lsk_2
	nop
.Lsk_2:
	beq x30, x19, .Lsk_3
	nop
.Lsk_3:
	flw f15, 56(s0)
	flw f28, 160(s0)
	fmul.s f22, f15, f28
	fsw f22, 12(s0)
	fcvt.w.s x25, f15, rtz
	sh x5, 184(s0)
	sh x24, 70(s0)
	flw f5, 52(s0)
	flw f31, 68(s0)
	fmax.s f31, f5, f31
	fsw f31, 116(s0)
	fcvt.w.s x12, f5, rtz
	mulhsu x16, x31, x24
	mulhu x7, x7, x25
	addi x12, x18, 1706
	addi x17, x24, -1806
	add  x17, x17, x17
	slli x17, x17, 1
	srli x15, x6, 11
	div x4, x23, x9
	auipc x30, 540702
	sra x30, x26, x9
	flw f8, 88(s0)
	flw f12, 160(s0)
	fmin.s f16, f8, f12
	fsw f16, 172(s0)
	fcvt.w.s x10, f8, rtz
	lh x5, 84(s0)
	lhu x13, 214(s0)
	mulhu x20, x19, x6
	bgeu x28, x17, .Lsk_4
	nop
.Lsk_4:
	addi x5, x21, 1723
	add  x5, x5, x5
	slli x5, x5, 1
	addi x7, x4, 1367
	or x31, x10, x5
	beq x3, x22, .Lsk_5
	nop
.Lsk_5:
	sh x23, 240(s0)
	sb x7, 67(s0)
	lb x25, 98(s0)
	addi x26, x27, -832
	add  x26, x26, x26
	slli x26, x26, 1
	slt x5, x20, x29
	lb x13, 190(s0)
	addi x14, x7, -804
	add  x14, x14, x14
	slli x14, x14, 1
	sw x21, 120(s0)
	srai x31, x21, 20
	bltu x13, x3, .Lsk_6
	nop
.Lsk_6:
	lbu x17, 171(s0)
	lw x4, 12(s0)
	flw f4, 80(s0)
	flw f9, 136(s0)
	fsub.s f10, f4, f9
	fsw f10, 196(s0)
	fcvt.w.s x24, f4, rtz
	sltiu x20, x13, 1666
	lhu x4, 150(s0)
	bne x4, x7, .Lsk_7
	nop
.Lsk_7:
	addi x19, x14, -649
	add  x19, x19, x19
	slli x19, x19, 1
	xor x12, x4, x25
	flw f11, 36(s0)
	flw f13, 196(s0)
	fadd.s f27, f11, f13
	fsw f27, 156(s0)
	fcvt.w.s x31, f11, rtz
	bgeu x15, x6, .Lsk_8
	nop
.Lsk_8:
	addi x16, x21, -212
	srai x5, x13, 4
	lw x11, 24(s0)
	add x28, x7, x9
	div x13, x31, x26
	srai x22, x19, 12
	addi x30, x26, -1077
	add  x30, x30, x30
	slli x30, x30, 1
	mul x13, x20, x22
	addi x31, x9, -110
	add  x31, x31, x31
	slli x31, x31, 1
	addi x13, x15, 425
	add  x13, x13, x13
	slli x13, x13, 1
	bgeu x10, x14, .Lsk_9
	nop
.Lsk_9:
	lbu x6, 22(s0)
	sub x10, x25, x21
	sb x12, 217(s0)
	addi x6, x5, -1031
	add  x6, x6, x6
	slli x6, x6, 1
	auipc x28, 862496
	sltu x11, x23, x19
	addi x18, x4, 874
	add  x18, x18, x18
	slli x18, x18, 1
	flw f2, 44(s0)
	flw f31, 20(s0)
	fsub.s f28, f2, f31
	fsw f28, 212(s0)
	fcvt.w.s x13, f2, rtz
	lh x19, 40(s0)
	addi x6, x10, 856
	add  x6, x6, x6
	slli x6, x6, 1
	srl x12, x21, x6
	bltu x5, x12, .Lsk_10
	nop
.Lsk_10:
	slti x13, x25, 840
	srl x3, x18, x30
	mulhu x24, x31, x6
	flw f7, 96(s0)
	flw f30, 8(s0)
	fmin.s f20, f7, f30
	fsw f20, 64(s0)
	fcvt.w.s x29, f7, rtz
	sh x25, 108(s0)
	lbu x29, 96(s0)
	flw f21, 176(s0)
	flw f0, 164(s0)
	fmul.s f0, f21, f0
	fsw f0, 164(s0)
	fcvt.w.s x30, f21, rtz
	or x23, x29, x15
	auipc x17, 419079
	ori x25, x30, 1543
	addi x19, x27, 1107
	add  x19, x19, x19
	slli x19, x19, 1
	bne x30, x19, .Lsk_11
	nop
.Lsk_11:
	auipc x24, 892792
	sltiu x19, x19, -32
	lbu x21, 243(s0)
	flw f1, 216(s0)
	flw f23, 120(s0)
	fmax.s f7, f1, f23
	fsw f7, 216(s0)
	fcvt.w.s x12, f1, rtz
	addi x11, x10, -450
	add  x11, x11, x11
	slli x11, x11, 1
	flw f5, 64(s0)
	flw f21, 44(s0)
	fmul.s f31, f5, f21
	fsw f31, 120(s0)
	fcvt.w.s x18, f5, rtz
	rem x10, x3, x24
	sub x28, x24, x30
	lbu x6, 186(s0)
	slli x5, x24, 26
	bne x14, x30, .Lsk_12
	nop
.Lsk_12:
	sh x12, 54(s0)
	mul x12, x15, x24
	or x16, x13, x14
	addi x10, x4, 1127
	add  x10, x10, x10
	slli x10, x10, 1
	addi x19, x15, -1798
	add  x19, x19, x19
	slli x19, x19, 1
	sub x3, x12, x23
	lui x5, 664793
	slli x17, x5, 30
	bgeu x27, x7, .Lsk_13
	nop
.Lsk_13:
	bne x10, x14, .Lsk_14
	nop
.Lsk_14:
	sh x5, 100(s0)
	xori x13, x12, -1529
	lhu x31, 38(s0)
	sh x16, 210(s0)
	flw f24, 60(s0)
	flw f29, 116(s0)
	fadd.s f9, f24, f29
	fsw f9, 168(s0)
	fcvt.w.s x14, f24, rtz
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
	.word 0xA2DABD5D
	.word 0x048E9BA6
	.word 0x0B939C43
	.word 0xFD2F58C3
	.word 0xDDE533F7
	.word 0xF5B21383
	.word 0xA13E3A82
	.word 0xE34EE297
	.word 0x8D1128E9
	.word 0x2D314F3D
	.word 0xF8566C67
	.word 0x8DAB88E5
	.word 0x00A2AA1B
	.word 0xEF20D77D
	.word 0x2F65E22F
	.word 0x659E47CF
	.word 0x9456474C
	.word 0xB14D63B6
	.word 0x6094F33F
	.word 0x81FE1FDA
	.word 0xCC8F0E3A
	.word 0x90669B72
	.word 0x6675CB05
	.word 0xE10B9AA9
	.word 0xAA724D12
	.word 0xE18AE2F7
	.word 0x40B7C906
	.word 0xBC810783
	.word 0x603D1BE1
	.word 0xCBAC7060
	.word 0xB090A5BC
	.word 0x0E55350F
	.word 0x9CA2CA1F
	.word 0x38187590
	.word 0x038B74F0
	.word 0xE52587DB
	.word 0xB2486433
	.word 0x8CB13787
	.word 0x4EF48295
	.word 0xA72C6F3E
	.word 0xA33D8BB1
	.word 0xB9BC851A
	.word 0x03777C2A
	.word 0x5B436C9A
	.word 0xF9EA152C
	.word 0xE8CCBF39
	.word 0x1CAD2221
	.word 0x6B03938D
	.word 0x59E857DB
	.word 0xB3682EE5
	.word 0xDE07098D
	.word 0x6192E145
	.word 0xC1E924E3
	.word 0xA520F39F
	.word 0x597488C5
	.word 0x7A13F49C
	.word 0x37ED594D
	.word 0x270DA805
	.word 0x32F8FD3C
	.word 0x1E5B65DD
	.word 0x20855D10
	.word 0xD4C91178
	.word 0x0B5BDB75
	.word 0x7CE644C1
