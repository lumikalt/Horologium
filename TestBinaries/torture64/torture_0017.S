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
	sd x14, 192(s0)
	addi x24, x7, 769
	add  x24, x24, x24
	slli x24, x24, 1
	remu x9, x16, x26
	srl x13, x30, x6
	addiw x27, x3, -1170
	sh x20, 242(s0)
	flw f10, 108(s0)
	flw f14, 156(s0)
	fmax.s f9, f10, f14
	fsw f9, 200(s0)
	fcvt.w.s x29, f10, rtz
	divu x30, x17, x3
	sltu x23, x19, x30
	addi x10, x27, -896
	add  x10, x10, x10
	slli x10, x10, 1
	lbu x15, 164(s0)
	addw x11, x18, x17
	sw x15, 8(s0)
	slli x7, x22, 2
	xori x19, x17, -16
	sw x7, 228(s0)
	addi x20, x27, 1771
	add  x20, x20, x20
	slli x20, x20, 1
	addi x9, x7, 969
	add  x9, x9, x9
	slli x9, x9, 1
	lwu x10, 104(s0)
	slli x30, x10, 6
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
	sd x7, 144(s0)
	addi x29, x16, 1632
	add  x29, x29, x29
	slli x29, x29, 1
	slt x17, x23, x13
	addi x26, x6, 1844
	add  x26, x26, x26
	slli x26, x26, 1
	lh x21, 208(s0)
	divuw x30, x18, x13
	lw x29, 124(s0)
	ld x19, 8(s0)
	ld x16, 8(s0)
	auipc x26, 335507
	sltu x27, x5, x10
	slli x31, x25, 24
	beq x28, x9, .Lsk_0
	nop
.Lsk_0:
	addi x18, x12, 835
	add  x18, x18, x18
	slli x18, x18, 1
	ld x26, 136(s0)
	or x31, x3, x7
	add x14, x15, x5
	addi x11, x29, 1945
	add  x11, x11, x11
	slli x11, x11, 1
	lhu x23, 48(s0)
	lbu x15, 24(s0)
	mulhsu x9, x11, x3
	flw f26, 160(s0)
	flw f22, 116(s0)
	fmin.s f22, f26, f22
	fsw f22, 188(s0)
	fcvt.w.s x10, f26, rtz
	mulhu x17, x16, x11
	auipc x9, 125815
	xori x3, x28, -1152
	addi x19, x3, -1580
	add  x19, x19, x19
	slli x19, x19, 1
	lhu x31, 114(s0)
	addi x22, x28, 892
	add  x22, x22, x22
	slli x22, x22, 1
	remuw x6, x11, x3
	lhu x26, 164(s0)
	beq x4, x30, .Lsk_1
	nop
.Lsk_1:
	lbu x11, 224(s0)
	srli x27, x6, 3
	bgeu x29, x26, .Lsk_2
	nop
.Lsk_2:
	and x6, x19, x28
	or x7, x28, x5
	fld f31, 136(s0)
	fld f17, 192(s0)
	fmin.d f6, f31, f17
	fsd f6, 192(s0)
	fcvt.l.d x7, f31, rtz
	fld f7, 80(s0)
	fld f8, 136(s0)
	fsub.d f9, f7, f8
	fsd f9, 232(s0)
	fcvt.l.d x19, f7, rtz
	slliw x24, x17, 22
	sltiu x25, x12, -1740
	sb x9, 6(s0)
	lbu x30, 165(s0)
	sltiu x28, x30, 1983
	slt x14, x7, x9
	fld f20, 40(s0)
	fld f21, 80(s0)
	fadd.d f30, f20, f21
	fsd f30, 200(s0)
	fcvt.l.d x18, f20, rtz
	lb x23, 214(s0)
	mulhu x20, x19, x6
	bgeu x28, x17, .Lsk_3
	nop
.Lsk_3:
	addi x5, x21, 1723
	add  x5, x5, x5
	slli x5, x5, 1
	addi x7, x4, 1367
	or x31, x10, x5
	beq x3, x22, .Lsk_4
	nop
.Lsk_4:
	sd x23, 240(s0)
	sh x20, 66(s0)
	lb x25, 98(s0)
	addi x26, x27, -832
	add  x26, x26, x26
	slli x26, x26, 1
	slt x5, x20, x29
	lwu x26, 184(s0)
	lui x28, 199999
	sh x11, 40(s0)
	addi x21, x19, 614
	add  x21, x21, x21
	slli x21, x21, 1
	bltu x13, x3, .Lsk_5
	nop
.Lsk_5:
	lbu x17, 171(s0)
	lw x4, 12(s0)
	fld f4, 80(s0)
	fld f9, 136(s0)
	fsub.d f10, f4, f9
	fsd f10, 192(s0)
	fcvt.l.d x24, f4, rtz
	sltiu x20, x13, 1666
	lhu x4, 150(s0)
	bne x4, x7, .Lsk_6
	nop
.Lsk_6:
	addi x19, x14, -649
	add  x19, x19, x19
	slli x19, x19, 1
	xor x12, x4, x25
	flw f11, 196(s0)
	flw f13, 156(s0)
	fmin.s f27, f11, f13
	fsw f27, 28(s0)
	fcvt.w.s x31, f11, rtz
	beq x5, x13, .Lsk_7
	nop
.Lsk_7:
	lwu x16, 0(s0)
	srai x5, x13, 9
	lwu x6, 124(s0)
	add x28, x7, x9
	div x13, x31, x26
	srai x22, x19, 25
	addi x30, x26, -1077
	add  x30, x30, x30
	slli x30, x30, 1
	sraw x13, x20, x27
	or x27, x31, x11
	addi x13, x15, 425
	add  x13, x13, x13
	slli x13, x13, 1
	bgeu x10, x14, .Lsk_8
	nop
.Lsk_8:
	ld x31, 48(s0)
	bgeu x4, x13, .Lsk_9
	nop
.Lsk_9:
	bgeu x6, x27, .Lsk_10
	nop
.Lsk_10:
	srliw x23, x29, 8
	add x29, x6, x5
	sb x17, 18(s0)
	sltu x11, x23, x19
	addi x18, x4, 874
	add  x18, x18, x18
	slli x18, x18, 1
	flw f2, 212(s0)
	flw f31, 64(s0)
	fsub.s f28, f2, f31
	fsw f28, 236(s0)
	fcvt.w.s x13, f2, rtz
	lw x31, 56(s0)
	srai x15, x12, 52
	addi x26, x31, -1442
	add  x26, x26, x26
	slli x26, x26, 1
	slliw x23, x13, 15
	lh x15, 6(s0)
	lwu x17, 24(s0)
	addi x31, x18, 1149
	add  x31, x31, x31
	slli x31, x31, 1
	addiw x29, x16, -1067
	ld x10, 8(s0)
	sd x23, 168(s0)
	bge x23, x29, .Lsk_11
	nop
.Lsk_11:
	sh x30, 42(s0)
	addi x31, x3, -2022
	add  x31, x31, x31
	slli x31, x31, 1
	flw f20, 136(s0)
	flw f20, 188(s0)
	fadd.s f23, f20, f20
	fsw f23, 184(s0)
	fcvt.w.s x13, f20, rtz
	flw f12, 224(s0)
	flw f9, 240(s0)
	fsub.s f28, f12, f9
	fsw f28, 196(s0)
	fcvt.w.s x10, f12, rtz
	fld f31, 152(s0)
	fld f7, 184(s0)
	fmin.d f23, f31, f7
	fsd f23, 248(s0)
	fcvt.l.d x25, f31, rtz
	fld f19, 240(s0)
	fld f15, 208(s0)
	fmul.d f30, f19, f15
	fsd f30, 8(s0)
	fcvt.l.d x17, f19, rtz
	flw f7, 220(s0)
	flw f16, 112(s0)
	fsub.s f27, f7, f16
	fsw f27, 96(s0)
	fcvt.w.s x24, f7, rtz
	flw f5, 44(s0)
	flw f21, 120(s0)
	fsub.s f31, f5, f21
	fsw f31, 136(s0)
	fcvt.w.s x18, f5, rtz
	lw x17, 216(s0)
	bgeu x7, x3, .Lsk_12
	nop
.Lsk_12:
	srli x20, x3, 5
	sb x9, 208(s0)
	bne x14, x30, .Lsk_13
	nop
.Lsk_13:
	sd x12, 48(s0)
	mulw x12, x15, x24
	or x16, x13, x14
	addi x10, x4, 1127
	add  x10, x10, x10
	slli x10, x10, 1
	addi x19, x15, -1798
	add  x19, x19, x19
	slli x19, x19, 1
	sub x3, x12, x23
	slti x5, x23, 548
	slli x17, x5, 61
	bgeu x27, x7, .Lsk_14
	nop
.Lsk_14:
	bne x10, x14, .Lsk_15
	nop
.Lsk_15:
	sd x5, 96(s0)
	srliw x13, x12, 4
	ld x18, 32(s0)
	sd x16, 208(s0)
	fld f24, 56(s0)
	fld f29, 112(s0)
	fadd.d f9, f24, f29
	fsd f9, 168(s0)
	fcvt.l.d x14, f24, rtz
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
