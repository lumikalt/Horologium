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
	divuw x26, x4, x25
	sw x6, 244(s0)
	lh x29, 210(s0)
	blt x14, x13, .Lsk_0
	nop
.Lsk_0:
	sh x24, 28(s0)
	sd x10, 152(s0)
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
	sb x21, 146(s0)
	addi x7, x25, -1419
	add  x7, x7, x7
	slli x7, x7, 1
	fld f22, 104(s0)
	fld f15, 160(s0)
	fmax.d f7, f22, f15
	fsd f7, 96(s0)
	fcvt.l.d x23, f22, rtz
	sraw x21, x5, x12
	flw f1, 152(s0)
	flw f20, 48(s0)
	fmax.s f18, f1, f20
	fsw f18, 92(s0)
	fcvt.w.s x15, f1, rtz
	addiw x15, x18, -1463
	addi x20, x28, 930
	add  x20, x20, x20
	slli x20, x20, 1
	fld f22, 8(s0)
	fld f22, 112(s0)
	fmax.d f1, f22, f22
	fsd f1, 24(s0)
	fcvt.l.d x15, f22, rtz
	slliw x28, x27, 17
	xori x3, x21, -978
	mulh x28, x26, x5
	lhu x9, 18(s0)
	sra x5, x16, x10
	sh x20, 72(s0)
	sb x18, 206(s0)
	lbu x20, 160(s0)
	xori x27, x6, 220
	addi x13, x3, -1271
	add  x13, x13, x13
	slli x13, x13, 1
	lhu x15, 60(s0)
	addi x29, x22, -384
	add  x29, x29, x29
	slli x29, x29, 1
	sh x19, 72(s0)
	slt x24, x11, x12
	bgeu x19, x14, .Lsk_3
	nop
.Lsk_3:
	lwu x25, 164(s0)
	addw x3, x19, x14
	addi x30, x11, -1109
	add  x30, x30, x30
	slli x30, x30, 1
	sltiu x26, x31, -1762
	lw x12, 168(s0)
	sub x17, x27, x13
	addiw x30, x3, -1874
	bgeu x4, x20, .Lsk_4
	nop
.Lsk_4:
	flw f4, 28(s0)
	flw f22, 212(s0)
	fmin.s f31, f4, f22
	fsw f31, 48(s0)
	fcvt.w.s x30, f4, rtz
	or x16, x19, x21
	sll x4, x28, x14
	slli x18, x28, 41
	lw x10, 60(s0)
	beq x30, x31, .Lsk_5
	nop
.Lsk_5:
	remw x18, x15, x17
	addi x31, x31, -357
	add  x31, x31, x31
	slli x31, x31, 1
	flw f9, 196(s0)
	flw f18, 160(s0)
	fmul.s f10, f9, f18
	fsw f10, 160(s0)
	fcvt.w.s x18, f9, rtz
	subw x10, x11, x24
	bge x22, x31, .Lsk_6
	nop
.Lsk_6:
	flw f21, 20(s0)
	flw f9, 184(s0)
	fsub.s f4, f21, f9
	fsw f4, 180(s0)
	fcvt.w.s x6, f21, rtz
	sub x29, x28, x15
	addi x14, x22, 212
	add  x14, x14, x14
	slli x14, x14, 1
	sb x5, 35(s0)
	auipc x17, 915470
	ori x29, x23, -2047
	sd x17, 16(s0)
	sltiu x24, x13, -1442
	lw x14, 132(s0)
	xor x25, x24, x24
	slt x19, x14, x6
	addi x6, x25, -2001
	add  x6, x6, x6
	slli x6, x6, 1
	addiw x10, x16, -741
	sd x22, 8(s0)
	remw x13, x5, x10
	sltu x30, x28, x28
	flw f31, 168(s0)
	flw f25, 36(s0)
	fmin.s f31, f31, f25
	fsw f31, 140(s0)
	fcvt.w.s x4, f31, rtz
	bne x27, x23, .Lsk_7
	nop
.Lsk_7:
	xor x16, x29, x31
	xor x21, x27, x15
	subw x27, x10, x26
	sb x11, 100(s0)
	addi x16, x7, -14
	add  x16, x16, x16
	slli x16, x16, 1
	sra x17, x12, x29
	sra x19, x15, x4
	slt x25, x31, x13
	addi x9, x4, 1647
	add  x9, x9, x9
	slli x9, x9, 1
	sh x12, 22(s0)
	sw x19, 36(s0)
	addi x20, x3, -872
	add  x20, x20, x20
	slli x20, x20, 1
	lh x15, 224(s0)
	mulhsu x24, x13, x15
	flw f22, 120(s0)
	flw f24, 0(s0)
	fadd.s f17, f22, f24
	fsw f17, 48(s0)
	fcvt.w.s x22, f22, rtz
	srliw x6, x3, 2
	blt x6, x7, .Lsk_8
	nop
.Lsk_8:
	sltiu x5, x21, 453
	rem x6, x7, x3
	lh x22, 212(s0)
	sd x19, 48(s0)
	lw x21, 36(s0)
	sra x31, x7, x31
	lh x27, 46(s0)
	addi x20, x18, 1461
	sb x13, 106(s0)
	bne x10, x31, .Lsk_9
	nop
.Lsk_9:
	bltu x30, x6, .Lsk_10
	nop
.Lsk_10:
	addi x25, x16, -285
	add  x25, x25, x25
	slli x25, x25, 1
	addi x6, x29, -456
	add  x6, x6, x6
	slli x6, x6, 1
	ld x22, 80(s0)
	flw f1, 96(s0)
	flw f26, 132(s0)
	fmin.s f31, f1, f26
	fsw f31, 192(s0)
	fcvt.w.s x29, f1, rtz
	sub x18, x27, x4
	lwu x16, 244(s0)
	beq x22, x7, .Lsk_11
	nop
.Lsk_11:
	addi x20, x5, 1334
	add  x20, x20, x20
	slli x20, x20, 1
	addi x7, x23, 619
	add  x7, x7, x7
	slli x7, x7, 1
	sd x16, 48(s0)
	flw f23, 212(s0)
	flw f15, 196(s0)
	fadd.s f21, f23, f15
	fsw f21, 72(s0)
	fcvt.w.s x5, f23, rtz
	addiw x16, x28, 912
	lui x7, 137683
	bne x16, x25, .Lsk_12
	nop
.Lsk_12:
	addi x13, x19, 164
	add  x13, x13, x13
	slli x13, x13, 1
	fld f29, 40(s0)
	fld f4, 224(s0)
	fsub.d f15, f29, f4
	fsd f15, 224(s0)
	fcvt.l.d x11, f29, rtz
	addi x4, x30, 276
	divu x24, x7, x4
	add x14, x10, x26
	fld f16, 168(s0)
	fld f22, 240(s0)
	fmul.d f15, f16, f22
	fsd f15, 24(s0)
	fcvt.l.d x12, f16, rtz
	bne x18, x19, .Lsk_13
	nop
.Lsk_13:
	addi x15, x31, 1565
	add  x15, x15, x15
	slli x15, x15, 1
	addi x27, x16, -1411
	add  x27, x27, x27
	slli x27, x27, 1
	beq x14, x23, .Lsk_14
	nop
.Lsk_14:
	sub x5, x13, x3
	fld f4, 232(s0)
	fld f18, 128(s0)
	fmul.d f3, f4, f18
	fsd f3, 192(s0)
	fcvt.l.d x4, f4, rtz
	mulhsu x17, x29, x16
	sra x20, x30, x17
	sraw x4, x5, x22
	slli x30, x13, 48
	or x26, x10, x15
	ld x31, 56(s0)
	sub x30, x4, x7
	and x24, x25, x20
	ori x18, x24, 368
	remu x27, x14, x20
	blt x3, x19, .Lsk_15
	nop
.Lsk_15:
	fld f2, 56(s0)
	fld f10, 176(s0)
	fadd.d f12, f2, f10
	fsd f12, 136(s0)
	fcvt.l.d x30, f2, rtz
	flw f10, 128(s0)
	flw f26, 220(s0)
	fmul.s f12, f10, f26
	fsw f12, 112(s0)
	fcvt.w.s x15, f10, rtz
	and x27, x30, x28
	addi x7, x4, 656
	add  x7, x7, x7
	slli x7, x7, 1
	flw f30, 84(s0)
	flw f0, 28(s0)
	fmax.s f17, f30, f0
	fsw f17, 208(s0)
	fcvt.w.s x3, f30, rtz
	addi x7, x6, -1344
	add  x7, x7, x7
	slli x7, x7, 1
	sh x11, 176(s0)
	remu x30, x10, x12
	addi x11, x30, -258
	add  x11, x11, x11
	slli x11, x11, 1
	sh x10, 168(s0)
	flw f24, 128(s0)
	flw f1, 140(s0)
	fmul.s f14, f24, f1
	fsw f14, 72(s0)
	fcvt.w.s x6, f24, rtz
	bge x12, x25, .Lsk_16
	nop
.Lsk_16:
	bge x27, x24, .Lsk_17
	nop
.Lsk_17:
	beq x6, x18, .Lsk_18
	nop
.Lsk_18:
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
	.word 0xD7CD8E72
	.word 0x8DAAB14F
	.word 0x5B0258A2
	.word 0xF1D232B5
	.word 0x38CDB150
	.word 0x3267142D
	.word 0x84C6B272
	.word 0x4DEF3D54
	.word 0xD9196C43
	.word 0xAC27517C
	.word 0x6F35DF0E
	.word 0x27D8F333
	.word 0x4D2A50B9
	.word 0x7CAF30A8
	.word 0xE7F530D5
	.word 0xA7E97C92
	.word 0x21D3AFEF
	.word 0x638385EB
	.word 0x86D1DA18
	.word 0xF32E9DD3
	.word 0x5204D953
	.word 0xD4215A84
	.word 0x92FFB475
	.word 0x60F1C085
	.word 0xEF2DD7B2
	.word 0x0674DE14
	.word 0xADB57742
	.word 0x6FE213EF
	.word 0xDD4D77C5
	.word 0xE2E829F6
	.word 0x393E2EA4
	.word 0xB046539B
	.word 0x7E067753
	.word 0xC8A31422
	.word 0x4FAA63BB
	.word 0x76B020B1
	.word 0xD64CA2B1
	.word 0xB673D506
	.word 0x784C0246
	.word 0x5FAEF561
	.word 0x0CC0B877
	.word 0xF135CAB2
	.word 0xDD67E338
	.word 0xEAB887F5
	.word 0xE7ECD61C
	.word 0x3865FBCF
	.word 0x22178AA6
	.word 0xAEA92A67
	.word 0x8EF6E4A9
	.word 0xEE808185
	.word 0x0B175054
	.word 0x59D984AB
	.word 0xCE4BD576
	.word 0xB5F015AF
	.word 0x1A4EADB7
	.word 0xF1260942
	.word 0xBA3892B0
	.word 0x456356D7
	.word 0x29EDD37B
	.word 0x7B336DC5
	.word 0x9D6DBDE4
	.word 0x2A57FB08
	.word 0x1E209688
	.word 0x5B4346E5
