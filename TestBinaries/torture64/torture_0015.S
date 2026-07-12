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
	sra x19, x28, x28
	fld f2, 176(s0)
	fld f3, 240(s0)
	fmin.d f30, f2, f3
	fsd f30, 72(s0)
	fcvt.l.d x5, f2, rtz
	bne x28, x25, .Lsk_0
	nop
.Lsk_0:
	slt x20, x25, x13
	lh x23, 206(s0)
	lbu x26, 122(s0)
	srli x27, x19, 46
	sltu x25, x19, x4
	lb x5, 113(s0)
	lwu x30, 116(s0)
	addi x14, x15, -782
	add  x14, x14, x14
	slli x14, x14, 1
	srliw x22, x14, 18
	addi x12, x22, -63
	addi x20, x15, 1066
	add  x20, x20, x20
	slli x20, x20, 1
	ld x23, 0(s0)
	beq x5, x3, .Lsk_1
	nop
.Lsk_1:
	fld f19, 200(s0)
	fld f21, 72(s0)
	fmax.d f0, f19, f21
	fsd f0, 48(s0)
	fcvt.l.d x15, f19, rtz
	andi x17, x14, -1794
	sra x11, x27, x7
	xori x29, x12, -1604
	bltu x27, x10, .Lsk_2
	nop
.Lsk_2:
	lbu x20, 28(s0)
	addi x19, x12, 821
	add  x19, x19, x19
	slli x19, x19, 1
	slt x15, x4, x24
	remw x25, x10, x20
	addi x30, x5, -160
	add  x30, x30, x30
	slli x30, x30, 1
	lb x20, 66(s0)
	slti x24, x21, -1040
	ori x23, x26, 786
	lhu x4, 42(s0)
	rem x21, x28, x17
	bltu x23, x6, .Lsk_3
	nop
.Lsk_3:
	add x5, x17, x4
	and x16, x23, x30
	lwu x26, 80(s0)
	and x19, x10, x20
	lwu x13, 128(s0)
	flw f15, 244(s0)
	flw f15, 68(s0)
	fmin.s f14, f15, f15
	fsw f14, 168(s0)
	fcvt.w.s x15, f15, rtz
	sub x26, x27, x28
	sw x31, 76(s0)
	addi x5, x10, -1716
	add  x5, x5, x5
	slli x5, x5, 1
	addiw x13, x4, -780
	addiw x28, x19, 1940
	ld x4, 40(s0)
	addi x19, x21, -1415
	add  x19, x19, x19
	slli x19, x19, 1
	lwu x29, 236(s0)
	addi x11, x30, 1989
	add  x11, x11, x11
	slli x11, x11, 1
	fld f26, 240(s0)
	fld f16, 48(s0)
	fadd.d f13, f26, f16
	fsd f13, 208(s0)
	fcvt.l.d x25, f26, rtz
	lwu x13, 12(s0)
	sll x28, x20, x12
	lw x10, 72(s0)
	lh x20, 162(s0)
	addi x20, x31, -1652
	add  x20, x20, x20
	slli x20, x20, 1
	flw f6, 112(s0)
	flw f8, 8(s0)
	fmax.s f11, f6, f8
	fsw f11, 204(s0)
	fcvt.w.s x23, f6, rtz
	addi x27, x25, -1607
	add  x27, x27, x27
	slli x27, x27, 1
	xori x28, x16, -1214
	addi x22, x17, 551
	add  x22, x22, x22
	slli x22, x22, 1
	bne x6, x17, .Lsk_4
	nop
.Lsk_4:
	addi x4, x3, 939
	add  x4, x4, x4
	slli x4, x4, 1
	fld f10, 72(s0)
	fld f31, 224(s0)
	fmin.d f21, f10, f31
	fsd f21, 0(s0)
	fcvt.l.d x15, f10, rtz
	sra x14, x13, x25
	auipc x11, 384605
	lh x31, 52(s0)
	lbu x24, 225(s0)
	bltu x19, x28, .Lsk_5
	nop
.Lsk_5:
	div x4, x4, x26
	lh x5, 176(s0)
	sw x10, 240(s0)
	ld x25, 136(s0)
	slt x23, x20, x10
	or x6, x19, x19
	andi x23, x30, -1865
	beq x21, x24, .Lsk_6
	nop
.Lsk_6:
	andi x25, x13, -1260
	addi x25, x7, 1733
	add  x25, x25, x25
	slli x25, x25, 1
	srli x30, x23, 55
	divw x20, x3, x16
	addi x17, x16, 1649
	add  x17, x17, x17
	slli x17, x17, 1
	fld f4, 88(s0)
	fld f23, 16(s0)
	fsub.d f28, f4, f23
	fsd f28, 0(s0)
	fcvt.l.d x27, f4, rtz
	xor x25, x10, x3
	fld f26, 176(s0)
	fld f23, 176(s0)
	fmax.d f28, f26, f23
	fsd f28, 8(s0)
	fcvt.l.d x31, f26, rtz
	addi x25, x11, -648
	add  x25, x25, x25
	slli x25, x25, 1
	sra x7, x22, x18
	addi x28, x11, -1507
	add  x28, x28, x28
	slli x28, x28, 1
	lbu x24, 186(s0)
	lbu x11, 70(s0)
	addi x23, x25, -169
	add  x23, x23, x23
	slli x23, x23, 1
	lwu x12, 36(s0)
	slli x24, x24, 52
	addi x15, x18, -1628
	add  x15, x15, x15
	slli x15, x15, 1
	auipc x3, 1032174
	sll x19, x29, x13
	addi x27, x30, 1167
	add  x27, x27, x27
	slli x27, x27, 1
	ld x11, 112(s0)
	addi x4, x28, 120
	add  x4, x4, x4
	slli x4, x4, 1
	remu x3, x11, x19
	blt x14, x7, .Lsk_7
	nop
.Lsk_7:
	sll x21, x25, x9
	lb x28, 121(s0)
	lui x15, 264651
	bltu x20, x26, .Lsk_8
	nop
.Lsk_8:
	sub x21, x30, x18
	sb x11, 242(s0)
	lhu x4, 110(s0)
	sh x4, 54(s0)
	srai x15, x28, 45
	srli x16, x22, 9
	addi x11, x12, 97
	add  x11, x11, x11
	slli x11, x11, 1
	addi x15, x19, -706
	add  x15, x15, x15
	slli x15, x15, 1
	fld f0, 72(s0)
	fld f24, 240(s0)
	fmul.d f11, f0, f24
	fsd f11, 232(s0)
	fcvt.l.d x13, f0, rtz
	bltu x10, x9, .Lsk_9
	nop
.Lsk_9:
	lh x12, 250(s0)
	addi x30, x30, -304
	sd x16, 160(s0)
	fld f25, 24(s0)
	fld f21, 248(s0)
	fsub.d f26, f25, f21
	fsd f26, 144(s0)
	fcvt.l.d x28, f25, rtz
	slt x13, x12, x16
	bltu x14, x3, .Lsk_10
	nop
.Lsk_10:
	srliw x26, x25, 26
	lb x14, 92(s0)
	mulh x24, x19, x16
	flw f10, 180(s0)
	flw f4, 12(s0)
	fsub.s f21, f10, f4
	fsw f21, 176(s0)
	fcvt.w.s x25, f10, rtz
	lwu x23, 144(s0)
	lw x28, 72(s0)
	lbu x9, 58(s0)
	mulhsu x20, x17, x16
	lw x18, 100(s0)
	addi x21, x31, -1057
	mul x26, x6, x16
	addi x24, x26, 138
	add  x24, x24, x24
	slli x24, x24, 1
	lwu x15, 108(s0)
	fld f17, 80(s0)
	fld f14, 0(s0)
	fmax.d f11, f17, f14
	fsd f11, 152(s0)
	fcvt.l.d x24, f17, rtz
	sub x13, x28, x20
	slt x27, x9, x12
	bltu x24, x9, .Lsk_11
	nop
.Lsk_11:
	bltu x14, x10, .Lsk_12
	nop
.Lsk_12:
	addi x7, x23, -393
	add  x7, x7, x7
	slli x7, x7, 1
	sh x23, 118(s0)
	sd x18, 240(s0)
	srli x28, x13, 63
	sllw x23, x28, x15
	addi x18, x26, 504
	add  x18, x18, x18
	slli x18, x18, 1
	addi x9, x22, -1255
	add  x9, x9, x9
	slli x9, x9, 1
	lwu x9, 252(s0)
	lb x7, 222(s0)
	lwu x21, 188(s0)
	sub x16, x25, x5
	bltu x31, x25, .Lsk_13
	nop
.Lsk_13:
	addi x19, x9, -115
	add  x19, x19, x19
	slli x19, x19, 1
	srli x5, x18, 37
	lui x16, 786314
	lb x15, 123(s0)
	addi x22, x17, 2038
	add  x22, x22, x22
	slli x22, x22, 1
	addi x6, x19, -1445
	add  x6, x6, x6
	slli x6, x6, 1
	sra x6, x14, x3
	bge x6, x22, .Lsk_14
	nop
.Lsk_14:
	divw x12, x18, x18
	bgeu x13, x24, .Lsk_15
	nop
.Lsk_15:
	addi x29, x23, -2037
	add  x29, x29, x29
	slli x29, x29, 1
	lwu x11, 0(s0)
	flw f12, 236(s0)
	flw f26, 60(s0)
	fmul.s f12, f12, f26
	fsw f12, 88(s0)
	fcvt.w.s x5, f12, rtz
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
	.word 0xBCDD0BEF
	.word 0x310ABBE4
	.word 0x7401A5F8
	.word 0xE4FBD8C5
	.word 0x8E8ED249
	.word 0x2626FAE7
	.word 0xE180A001
	.word 0xC5D77CDA
	.word 0xCC5A69C9
	.word 0x78670651
	.word 0x52CBEBF3
	.word 0x7B3D2A1A
	.word 0x737DF931
	.word 0x2748069B
	.word 0xDCC5A6D4
	.word 0x7D67E34A
	.word 0x0030B7CF
	.word 0x0B901711
	.word 0xDEC4B3E5
	.word 0x4F97502B
	.word 0x0E816368
	.word 0x12E79ADA
	.word 0x31CD20FC
	.word 0x9D4DBA39
	.word 0x44973029
	.word 0x91D78A3D
	.word 0x712532E8
	.word 0xCA5944ED
	.word 0xBE2FE318
	.word 0xA594DDCB
	.word 0xF197DB92
	.word 0x4EC32381
	.word 0x97073CBE
	.word 0x0CB6A9F0
	.word 0x67877BD2
	.word 0x427EC111
	.word 0xF391EC64
	.word 0xFE84E0FA
	.word 0x43258B7D
	.word 0xDB9D5800
	.word 0xFA754372
	.word 0xED1B9ACD
	.word 0x3DAC91BC
	.word 0x827AC772
	.word 0xE949C6A1
	.word 0x7273A365
	.word 0xFFD467E9
	.word 0x16E9688F
	.word 0xC03762A4
	.word 0xBC422A18
	.word 0xA3DF1641
	.word 0xF26B8F1D
	.word 0xB7800C2B
	.word 0x2FFA9BA3
	.word 0x7D036216
	.word 0x20C659F1
	.word 0xA20648F5
	.word 0x4B92C239
	.word 0x853ECF4C
	.word 0xF58FF4DB
	.word 0xFC892C79
	.word 0x9B892B0F
	.word 0x2BBE857F
	.word 0xB7BB9040
