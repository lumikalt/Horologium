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
	bgeu x29, x28, .Lsk_0
	nop
.Lsk_0:
	lw x9, 192(s0)
	sh x5, 100(s0)
	lw x13, 88(s0)
	remw x31, x25, x30
	addi x9, x18, 990
	add  x9, x9, x9
	slli x9, x9, 1
	addi x15, x30, -1341
	add  x15, x15, x15
	slli x15, x15, 1
	ori x13, x17, 2017
	addi x27, x5, 988
	lbu x28, 156(s0)
	sraiw x23, x4, 14
	add x19, x18, x18
	sb x12, 68(s0)
	xori x6, x19, 560
	lhu x30, 238(s0)
	addi x20, x9, 213
	add  x20, x20, x20
	slli x20, x20, 1
	sh x13, 224(s0)
	blt x7, x28, .Lsk_1
	nop
.Lsk_1:
	bltu x28, x13, .Lsk_2
	nop
.Lsk_2:
	slli x9, x23, 3
	srai x25, x12, 27
	fld f22, 136(s0)
	fld f14, 144(s0)
	fmax.d f9, f22, f14
	fsd f9, 72(s0)
	fcvt.l.d x28, f22, rtz
	lwu x20, 148(s0)
	lb x17, 185(s0)
	lb x30, 179(s0)
	sraiw x26, x30, 7
	sd x22, 104(s0)
	or x24, x14, x27
	lh x16, 78(s0)
	lwu x17, 100(s0)
	addi x16, x23, 534
	add  x16, x16, x16
	slli x16, x16, 1
	sltiu x14, x29, -514
	andi x9, x26, 855
	addi x11, x5, -1320
	add  x11, x11, x11
	slli x11, x11, 1
	remw x19, x26, x23
	slliw x6, x4, 14
	sw x18, 16(s0)
	srlw x22, x12, x14
	slli x11, x24, 43
	lb x26, 244(s0)
	fld f23, 24(s0)
	fld f1, 32(s0)
	fsub.d f19, f23, f1
	fsd f19, 208(s0)
	fcvt.l.d x29, f23, rtz
	addi x29, x21, -1445
	add  x29, x29, x29
	slli x29, x29, 1
	addi x23, x14, 1215
	add  x23, x23, x23
	slli x23, x23, 1
	addi x18, x22, -1633
	add  x18, x18, x18
	slli x18, x18, 1
	blt x18, x9, .Lsk_3
	nop
.Lsk_3:
	addi x21, x20, -819
	add  x21, x21, x21
	slli x21, x21, 1
	beq x9, x23, .Lsk_4
	nop
.Lsk_4:
	flw f22, 228(s0)
	flw f18, 48(s0)
	fadd.s f28, f22, f18
	fsw f28, 108(s0)
	fcvt.w.s x17, f22, rtz
	mul x10, x10, x4
	addi x5, x19, 1295
	add  x5, x5, x5
	slli x5, x5, 1
	srliw x22, x16, 16
	fld f3, 160(s0)
	fld f12, 16(s0)
	fmax.d f28, f3, f12
	fsd f28, 40(s0)
	fcvt.l.d x13, f3, rtz
	lbu x3, 198(s0)
	lbu x6, 201(s0)
	mulw x4, x6, x5
	srli x24, x20, 24
	bge x21, x12, .Lsk_5
	nop
.Lsk_5:
	sh x22, 242(s0)
	sw x5, 232(s0)
	beq x28, x7, .Lsk_6
	nop
.Lsk_6:
	fld f1, 120(s0)
	fld f0, 80(s0)
	fmul.d f0, f1, f0
	fsd f0, 176(s0)
	fcvt.l.d x5, f1, rtz
	flw f15, 16(s0)
	flw f11, 32(s0)
	fmul.s f2, f15, f11
	fsw f2, 248(s0)
	fcvt.w.s x3, f15, rtz
	lhu x22, 174(s0)
	sh x21, 96(s0)
	div x28, x11, x19
	addi x12, x16, -1464
	add  x12, x12, x12
	slli x12, x12, 1
	lhu x13, 218(s0)
	divw x27, x17, x17
	divu x26, x12, x21
	addiw x6, x29, 1134
	flw f10, 160(s0)
	flw f19, 108(s0)
	fsub.s f23, f10, f19
	fsw f23, 84(s0)
	fcvt.w.s x22, f10, rtz
	addi x14, x17, -1731
	add  x14, x14, x14
	slli x14, x14, 1
	sllw x11, x23, x12
	bltu x28, x16, .Lsk_7
	nop
.Lsk_7:
	lw x3, 232(s0)
	xori x24, x20, 417
	ori x28, x19, -706
	addi x29, x7, -1916
	add  x29, x29, x29
	slli x29, x29, 1
	and x10, x10, x20
	bgeu x21, x14, .Lsk_8
	nop
.Lsk_8:
	lw x26, 252(s0)
	sd x30, 232(s0)
	sh x6, 56(s0)
	bgeu x11, x25, .Lsk_9
	nop
.Lsk_9:
	srli x17, x7, 53
	sub x3, x21, x9
	bne x12, x26, .Lsk_10
	nop
.Lsk_10:
	sltiu x3, x9, -1556
	addi x10, x10, -1239
	add  x10, x10, x10
	slli x10, x10, 1
	bne x10, x25, .Lsk_11
	nop
.Lsk_11:
	sh x17, 196(s0)
	addi x4, x28, 1918
	add  x4, x4, x4
	slli x4, x4, 1
	addiw x3, x11, 2030
	sw x29, 36(s0)
	srliw x28, x13, 29
	bge x12, x22, .Lsk_12
	nop
.Lsk_12:
	sw x16, 188(s0)
	sw x7, 4(s0)
	blt x14, x27, .Lsk_13
	nop
.Lsk_13:
	addi x28, x23, 332
	add  x28, x28, x28
	slli x28, x28, 1
	lh x26, 106(s0)
	addi x10, x17, -1782
	add  x10, x10, x10
	slli x10, x10, 1
	and x7, x11, x5
	lwu x13, 240(s0)
	fld f17, 8(s0)
	fld f5, 224(s0)
	fmax.d f18, f17, f5
	fsd f18, 96(s0)
	fcvt.l.d x20, f17, rtz
	sll x7, x4, x3
	fld f1, 56(s0)
	fld f7, 88(s0)
	fmax.d f10, f1, f7
	fsd f10, 224(s0)
	fcvt.l.d x23, f1, rtz
	bgeu x10, x31, .Lsk_14
	nop
.Lsk_14:
	addi x28, x21, 1967
	add  x28, x28, x28
	slli x28, x28, 1
	lui x9, 733690
	addi x4, x16, 1282
	add  x4, x4, x4
	slli x4, x4, 1
	srliw x15, x4, 16
	lwu x9, 68(s0)
	sh x30, 42(s0)
	addi x23, x6, 529
	add  x23, x23, x23
	slli x23, x23, 1
	lhu x27, 252(s0)
	addi x13, x3, 2013
	add  x13, x13, x13
	slli x13, x13, 1
	lh x10, 226(s0)
	lui x20, 1030523
	addi x23, x27, -731
	add  x23, x23, x23
	slli x23, x23, 1
	srli x17, x30, 27
	addi x29, x12, 383
	add  x29, x29, x29
	slli x29, x29, 1
	ld x30, 56(s0)
	lw x10, 204(s0)
	sw x27, 48(s0)
	and x17, x25, x23
	flw f14, 212(s0)
	flw f7, 12(s0)
	fsub.s f15, f14, f7
	fsw f15, 104(s0)
	fcvt.w.s x9, f14, rtz
	sltu x15, x11, x6
	addiw x17, x22, 288
	sh x21, 44(s0)
	addi x19, x10, -1482
	add  x19, x19, x19
	slli x19, x19, 1
	addi x29, x26, -838
	add  x29, x29, x29
	slli x29, x29, 1
	sltu x16, x5, x13
	addi x14, x31, 630
	add  x14, x14, x14
	slli x14, x14, 1
	addi x5, x22, 1439
	add  x5, x5, x5
	slli x5, x5, 1
	srai x31, x4, 22
	fld f16, 112(s0)
	fld f20, 200(s0)
	fadd.d f25, f16, f20
	fsd f25, 0(s0)
	fcvt.l.d x21, f16, rtz
	addi x5, x3, 1817
	add  x5, x5, x5
	slli x5, x5, 1
	sltu x7, x6, x21
	srli x18, x6, 44
	srliw x21, x27, 0
	lwu x12, 168(s0)
	lbu x13, 203(s0)
	xori x16, x5, 604
	bltu x30, x5, .Lsk_15
	nop
.Lsk_15:
	lwu x7, 8(s0)
	lui x17, 144023
	ori x18, x9, 977
	sw x22, 80(s0)
	xori x5, x14, 1541
	flw f6, 16(s0)
	flw f8, 72(s0)
	fsub.s f25, f6, f8
	fsw f25, 188(s0)
	fcvt.w.s x13, f6, rtz
	srl x30, x29, x11
	auipc x14, 194928
	ld x27, 24(s0)
	addi x26, x10, 936
	add  x26, x26, x26
	slli x26, x26, 1
	sd x30, 16(s0)
	bge x7, x9, .Lsk_16
	nop
.Lsk_16:
	auipc x6, 1037829
	add x17, x25, x10
	blt x16, x29, .Lsk_17
	nop
.Lsk_17:
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
	.word 0x92480AA3
	.word 0xC416EC45
	.word 0xDEA697C7
	.word 0x2FA882E5
	.word 0x10D0B561
	.word 0x626F7369
	.word 0x54A120A4
	.word 0x902FEA44
	.word 0xB72EC36D
	.word 0xC294BB77
	.word 0xED0C27B6
	.word 0xEB42F966
	.word 0x2DAA5AE2
	.word 0xBF5F502E
	.word 0x0A1AB6BA
	.word 0xC76966A6
	.word 0xBCB1BA40
	.word 0x5879C268
	.word 0xD75A5B20
	.word 0x671998D1
	.word 0x377917C0
	.word 0x419A7642
	.word 0x0EEBEE08
	.word 0xEEB8E427
	.word 0xBC5EE3AC
	.word 0xBD6DF482
	.word 0x22C02D02
	.word 0x7E893A18
	.word 0xF9911899
	.word 0x8D5A29B5
	.word 0xA89D311B
	.word 0x1BCBE57A
	.word 0xD7C0CAC1
	.word 0x386EF4B4
	.word 0x19A2A87D
	.word 0xCBCBAED2
	.word 0xFE6EE8CF
	.word 0xE94AE406
	.word 0xDA5531A2
	.word 0xCD689541
	.word 0xA8A85918
	.word 0x51E73A8F
	.word 0x8B752CCA
	.word 0xB60F9E64
	.word 0xCFC5B478
	.word 0xC4B3C4EC
	.word 0xFD0EEED0
	.word 0x811BAE7E
	.word 0x799D9400
	.word 0x581090E6
	.word 0xC5631C17
	.word 0x8A9A1186
	.word 0x14822592
	.word 0xCB790595
	.word 0x236D2E62
	.word 0x78898C9C
	.word 0x2FBC73A4
	.word 0xB7C88B83
	.word 0x91B9EFB8
	.word 0xA5FB7596
	.word 0xB55D52DF
	.word 0xCDA698CF
	.word 0x002AB4E8
	.word 0xDD637425
