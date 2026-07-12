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
	lb x7, 178(s0)
	sll x12, x3, x15
	slli x9, x14, 62
	bge x19, x26, .Lsk_0
	nop
.Lsk_0:
	addi x10, x14, 1866
	add  x10, x10, x10
	slli x10, x10, 1
	flw f27, 136(s0)
	flw f0, 216(s0)
	fsub.s f3, f27, f0
	fsw f3, 4(s0)
	fcvt.w.s x14, f27, rtz
	sw x7, 16(s0)
	flw f24, 236(s0)
	flw f16, 140(s0)
	fsub.s f17, f24, f16
	fsw f17, 224(s0)
	fcvt.w.s x25, f24, rtz
	srlw x7, x4, x11
	slti x18, x10, -491
	lb x25, 59(s0)
	ld x21, 56(s0)
	addi x7, x27, 712
	add  x7, x7, x7
	slli x7, x7, 1
	lui x21, 630081
	blt x4, x3, .Lsk_1
	nop
.Lsk_1:
	or x23, x19, x16
	addi x10, x26, -961
	add  x10, x10, x10
	slli x10, x10, 1
	fld f24, 0(s0)
	fld f27, 128(s0)
	fmin.d f10, f24, f27
	fsd f10, 136(s0)
	fcvt.l.d x7, f24, rtz
	and x26, x27, x28
	flw f10, 168(s0)
	flw f2, 36(s0)
	fmax.s f4, f10, f2
	fsw f4, 8(s0)
	fcvt.w.s x5, f10, rtz
	addi x14, x7, -630
	add  x14, x14, x14
	slli x14, x14, 1
	addi x19, x6, 852
	add  x19, x19, x19
	slli x19, x19, 1
	beq x21, x26, .Lsk_2
	nop
.Lsk_2:
	auipc x15, 264023
	bge x22, x5, .Lsk_3
	nop
.Lsk_3:
	addi x26, x22, -821
	add  x26, x26, x26
	slli x26, x26, 1
	fld f16, 192(s0)
	fld f17, 0(s0)
	fsub.d f0, f16, f17
	fsd f0, 80(s0)
	fcvt.l.d x23, f16, rtz
	flw f11, 244(s0)
	flw f10, 52(s0)
	fadd.s f10, f11, f10
	fsw f10, 172(s0)
	fcvt.w.s x24, f11, rtz
	sraiw x23, x23, 5
	lh x26, 74(s0)
	flw f21, 116(s0)
	flw f23, 36(s0)
	fmin.s f3, f21, f23
	fsw f3, 116(s0)
	fcvt.w.s x15, f21, rtz
	flw f10, 132(s0)
	flw f7, 24(s0)
	fmin.s f18, f10, f7
	fsw f18, 48(s0)
	fcvt.w.s x9, f10, rtz
	addi x21, x5, 1065
	add  x21, x21, x21
	slli x21, x21, 1
	sb x13, 82(s0)
	flw f8, 148(s0)
	flw f18, 184(s0)
	fadd.s f30, f8, f18
	fsw f30, 196(s0)
	fcvt.w.s x13, f8, rtz
	lh x14, 100(s0)
	addi x31, x11, -672
	add  x31, x31, x31
	slli x31, x31, 1
	sll x11, x26, x24
	srliw x30, x4, 27
	bge x22, x12, .Lsk_4
	nop
.Lsk_4:
	sw x30, 208(s0)
	auipc x9, 897640
	bne x12, x26, .Lsk_5
	nop
.Lsk_5:
	lwu x19, 248(s0)
	sh x16, 182(s0)
	xor x30, x19, x9
	lb x7, 166(s0)
	srli x23, x3, 43
	lbu x17, 200(s0)
	xori x18, x4, 327
	add x15, x13, x25
	flw f29, 4(s0)
	flw f16, 12(s0)
	fmax.s f24, f29, f16
	fsw f24, 164(s0)
	fcvt.w.s x31, f29, rtz
	mulhu x5, x19, x27
	addi x13, x25, -1812
	add  x13, x13, x13
	slli x13, x13, 1
	fld f4, 184(s0)
	fld f4, 80(s0)
	fmin.d f7, f4, f4
	fsd f7, 112(s0)
	fcvt.l.d x20, f4, rtz
	flw f21, 4(s0)
	flw f28, 224(s0)
	fadd.s f15, f21, f28
	fsw f15, 60(s0)
	fcvt.w.s x24, f21, rtz
	lh x27, 118(s0)
	ld x21, 48(s0)
	sw x31, 228(s0)
	ld x16, 16(s0)
	ld x25, 32(s0)
	lb x23, 23(s0)
	sw x9, 240(s0)
	addi x12, x18, 1699
	add  x12, x12, x12
	slli x12, x12, 1
	addi x6, x5, -226
	add  x6, x6, x6
	slli x6, x6, 1
	flw f5, 104(s0)
	flw f18, 20(s0)
	fmin.s f10, f5, f18
	fsw f10, 40(s0)
	fcvt.w.s x23, f5, rtz
	sw x7, 44(s0)
	bge x29, x10, .Lsk_6
	nop
.Lsk_6:
	addi x17, x29, -581
	add  x17, x17, x17
	slli x17, x17, 1
	bgeu x31, x10, .Lsk_7
	nop
.Lsk_7:
	lb x13, 53(s0)
	mulw x7, x24, x30
	addi x3, x7, 1866
	add  x3, x3, x3
	slli x3, x3, 1
	bltu x20, x17, .Lsk_8
	nop
.Lsk_8:
	lw x27, 204(s0)
	addi x31, x19, -2021
	add  x31, x31, x31
	slli x31, x31, 1
	addi x3, x28, -1663
	add  x3, x3, x3
	slli x3, x3, 1
	xori x23, x5, 174
	sllw x28, x30, x9
	div x18, x27, x4
	flw f14, 108(s0)
	flw f14, 124(s0)
	fmin.s f13, f14, f14
	fsw f13, 232(s0)
	fcvt.w.s x13, f14, rtz
	ori x17, x10, 1633
	sraw x15, x21, x17
	addi x13, x5, 0
	add  x13, x13, x13
	slli x13, x13, 1
	sb x16, 79(s0)
	bge x19, x18, .Lsk_9
	nop
.Lsk_9:
	bgeu x23, x21, .Lsk_10
	nop
.Lsk_10:
	addiw x13, x15, 197
	addi x30, x13, -583
	add  x30, x30, x30
	slli x30, x30, 1
	sb x13, 81(s0)
	remw x17, x28, x26
	sltiu x16, x5, 1239
	sh x14, 56(s0)
	addi x3, x20, -1101
	add  x3, x3, x3
	slli x3, x3, 1
	lw x22, 176(s0)
	addi x31, x28, 1669
	add  x31, x31, x31
	slli x31, x31, 1
	slliw x7, x16, 20
	flw f12, 36(s0)
	flw f17, 236(s0)
	fadd.s f29, f12, f17
	fsw f29, 124(s0)
	fcvt.w.s x17, f12, rtz
	addi x24, x14, 1639
	add  x24, x24, x24
	slli x24, x24, 1
	flw f6, 248(s0)
	flw f4, 116(s0)
	fadd.s f7, f6, f4
	fsw f7, 156(s0)
	fcvt.w.s x25, f6, rtz
	slliw x7, x31, 31
	sw x20, 220(s0)
	addi x27, x24, 556
	add  x27, x27, x27
	slli x27, x27, 1
	lb x24, 43(s0)
	addi x14, x25, -750
	add  x14, x14, x14
	slli x14, x14, 1
	lhu x5, 52(s0)
	mulw x13, x31, x23
	addi x10, x9, -229
	add  x10, x10, x10
	slli x10, x10, 1
	flw f0, 64(s0)
	flw f9, 100(s0)
	fmul.s f27, f0, f9
	fsw f27, 168(s0)
	fcvt.w.s x15, f0, rtz
	flw f13, 56(s0)
	flw f9, 48(s0)
	fmin.s f5, f13, f9
	fsw f5, 144(s0)
	fcvt.w.s x30, f13, rtz
	slliw x10, x12, 25
	and x16, x16, x23
	sb x19, 220(s0)
	slli x11, x3, 51
	bne x31, x25, .Lsk_11
	nop
.Lsk_11:
	flw f18, 184(s0)
	flw f15, 248(s0)
	fmax.s f10, f18, f15
	fsw f10, 92(s0)
	fcvt.w.s x10, f18, rtz
	and x31, x4, x9
	addi x12, x5, 70
	add  x12, x12, x12
	slli x12, x12, 1
	lui x14, 614559
	flw f31, 204(s0)
	flw f4, 92(s0)
	fmax.s f22, f31, f4
	fsw f22, 188(s0)
	fcvt.w.s x22, f31, rtz
	fld f5, 208(s0)
	fld f14, 8(s0)
	fadd.d f25, f5, f14
	fsd f25, 72(s0)
	fcvt.l.d x21, f5, rtz
	lb x15, 212(s0)
	addi x31, x13, -1191
	add  x31, x31, x31
	slli x31, x31, 1
	fld f17, 48(s0)
	fld f11, 16(s0)
	fadd.d f30, f17, f11
	fsd f30, 96(s0)
	fcvt.l.d x30, f17, rtz
	sltiu x27, x30, 1136
	fld f12, 96(s0)
	fld f20, 104(s0)
	fadd.d f21, f12, f20
	fsd f21, 192(s0)
	fcvt.l.d x3, f12, rtz
	sw x3, 164(s0)
	lb x13, 236(s0)
	sb x15, 172(s0)
	sw x19, 192(s0)
	addi x6, x19, 519
	add  x6, x6, x6
	slli x6, x6, 1
	addiw x12, x15, -1585
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
	.word 0x03061448
	.word 0xB79AB26E
	.word 0x7CA49776
	.word 0x7CFD22F2
	.word 0x2E4053C6
	.word 0xD30972AC
	.word 0x82708311
	.word 0xBA9B0A5B
	.word 0x95640FCD
	.word 0x403382A7
	.word 0xED0034E1
	.word 0xF07DBFA9
	.word 0x48571F26
	.word 0xE678A9B4
	.word 0xC2BCF09C
	.word 0xA2D30C53
	.word 0x14B054D3
	.word 0xF38E6E87
	.word 0x09A71801
	.word 0xDD23A90A
	.word 0x7C4E58BD
	.word 0x54B81136
	.word 0x3F343E9A
	.word 0x4E8B121C
	.word 0x435292A1
	.word 0x74F5B14F
	.word 0x325F0736
	.word 0x1F7BCFDC
	.word 0xF91E21A3
	.word 0x7DE481C3
	.word 0xF3B7AAD7
	.word 0x8DC7BD1D
	.word 0x485AEC71
	.word 0x2781B468
	.word 0x5C10579B
	.word 0x06C8CD55
	.word 0xB9CAD7A8
	.word 0x92AE028C
	.word 0x7AA8324B
	.word 0x6EB140D1
	.word 0x70403316
	.word 0x398CF9CE
	.word 0x033F0E46
	.word 0x128BE1E3
	.word 0x651777DD
	.word 0x36CF11F7
	.word 0x0DE96448
	.word 0x0504398C
	.word 0xD60F4998
	.word 0x427B31C6
	.word 0x91F644B7
	.word 0xCF2D7364
	.word 0xCA3504DF
	.word 0x36A42A60
	.word 0x44D607DE
	.word 0xFF22AD4A
	.word 0xE6D33C9E
	.word 0xD8F528C8
	.word 0xBC5DA29F
	.word 0xFF22F41E
	.word 0x5792ABA7
	.word 0xD5EF09AF
	.word 0x1F06BF76
	.word 0x6DCC3D52
