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
	bgeu x5, x15, .Lsk_0
	nop
.Lsk_0:
	addi x9, x30, -17
	add  x9, x9, x9
	slli x9, x9, 1
	bge x11, x7, .Lsk_1
	nop
.Lsk_1:
	divu x21, x26, x19
	addi x3, x26, -1941
	add  x3, x3, x3
	slli x3, x3, 1
	remw x7, x31, x3
	addi x31, x9, 1510
	add  x31, x31, x31
	slli x31, x31, 1
	slt x22, x3, x23
	slt x30, x18, x4
	addi x3, x10, -917
	add  x3, x3, x3
	slli x3, x3, 1
	blt x21, x25, .Lsk_2
	nop
.Lsk_2:
	addiw x14, x27, -2020
	addi x17, x9, 20
	add  x17, x17, x17
	slli x17, x17, 1
	sh x15, 144(s0)
	remw x6, x22, x11
	sh x15, 136(s0)
	mulh x4, x16, x20
	bltu x25, x30, .Lsk_3
	nop
.Lsk_3:
	sh x27, 20(s0)
	srli x30, x12, 44
	lb x27, 230(s0)
	divw x12, x15, x29
	mulhsu x31, x10, x6
	addi x17, x18, 1550
	add  x17, x17, x17
	slli x17, x17, 1
	sw x19, 188(s0)
	sra x3, x29, x31
	addi x7, x24, -1290
	add  x7, x7, x7
	slli x7, x7, 1
	rem x9, x23, x14
	remuw x16, x4, x21
	blt x15, x28, .Lsk_4
	nop
.Lsk_4:
	addi x7, x26, 1523
	add  x7, x7, x7
	slli x7, x7, 1
	lb x29, 55(s0)
	srli x19, x18, 54
	sw x9, 64(s0)
	addi x6, x22, -239
	add  x6, x6, x6
	slli x6, x6, 1
	addi x15, x12, -2024
	add  x15, x15, x15
	slli x15, x15, 1
	beq x10, x21, .Lsk_5
	nop
.Lsk_5:
	sh x7, 186(s0)
	addi x20, x21, -809
	add  x20, x20, x20
	slli x20, x20, 1
	sll x27, x3, x6
	lwu x17, 12(s0)
	sub x17, x14, x9
	addi x5, x4, 487
	add  x5, x5, x5
	slli x5, x5, 1
	slli x12, x11, 60
	addi x18, x25, -1182
	add  x18, x18, x18
	slli x18, x18, 1
	bge x13, x3, .Lsk_6
	nop
.Lsk_6:
	lw x11, 68(s0)
	auipc x25, 845178
	auipc x26, 455476
	xori x29, x7, 124
	addi x29, x11, 1312
	add  x29, x29, x29
	slli x29, x29, 1
	sw x18, 228(s0)
	beq x28, x19, .Lsk_7
	nop
.Lsk_7:
	ld x14, 0(s0)
	sll x28, x12, x30
	remuw x14, x29, x19
	andi x10, x27, 735
	divu x31, x23, x21
	addi x18, x26, 1918
	add  x18, x18, x18
	slli x18, x18, 1
	rem x29, x15, x4
	beq x24, x9, .Lsk_8
	nop
.Lsk_8:
	flw f8, 20(s0)
	flw f17, 208(s0)
	fsub.s f21, f8, f17
	fsw f21, 244(s0)
	fcvt.w.s x29, f8, rtz
	lwu x19, 104(s0)
	sraiw x6, x19, 5
	fld f15, 128(s0)
	fld f29, 232(s0)
	fmin.d f7, f15, f29
	fsd f7, 104(s0)
	fcvt.l.d x3, f15, rtz
	sd x28, 72(s0)
	sd x16, 128(s0)
	ld x31, 152(s0)
	addi x13, x27, -527
	add  x13, x13, x13
	slli x13, x13, 1
	ld x19, 72(s0)
	addi x22, x16, 234
	add  x22, x22, x22
	slli x22, x22, 1
	beq x29, x17, .Lsk_9
	nop
.Lsk_9:
	blt x16, x16, .Lsk_10
	nop
.Lsk_10:
	sw x5, 244(s0)
	add x16, x18, x9
	flw f18, 4(s0)
	flw f2, 84(s0)
	fmin.s f11, f18, f2
	fsw f11, 88(s0)
	fcvt.w.s x24, f18, rtz
	sra x20, x4, x24
	bltu x15, x26, .Lsk_11
	nop
.Lsk_11:
	and x7, x16, x23
	lb x23, 58(s0)
	sh x7, 66(s0)
	lwu x30, 64(s0)
	mulhsu x22, x28, x30
	addi x31, x17, -226
	add  x31, x31, x31
	slli x31, x31, 1
	lhu x10, 224(s0)
	lw x19, 208(s0)
	bltu x18, x30, .Lsk_12
	nop
.Lsk_12:
	andi x17, x17, -1989
	lb x10, 215(s0)
	xori x21, x26, 1310
	addi x14, x10, -1130
	add  x14, x14, x14
	slli x14, x14, 1
	sw x3, 236(s0)
	ld x26, 144(s0)
	auipc x25, 813853
	sltiu x24, x24, -1505
	sd x7, 176(s0)
	lh x4, 28(s0)
	addi x9, x29, 1917
	add  x9, x9, x9
	slli x9, x9, 1
	lui x12, 493307
	lw x31, 236(s0)
	bne x18, x10, .Lsk_13
	nop
.Lsk_13:
	fld f13, 216(s0)
	fld f31, 72(s0)
	fsub.d f5, f13, f31
	fsd f5, 72(s0)
	fcvt.l.d x25, f13, rtz
	lui x10, 141141
	addi x10, x17, 1564
	add  x10, x10, x10
	slli x10, x10, 1
	lbu x11, 136(s0)
	addi x3, x13, 1389
	add  x3, x3, x3
	slli x3, x3, 1
	sb x12, 118(s0)
	fld f26, 184(s0)
	fld f22, 128(s0)
	fsub.d f27, f26, f22
	fsd f27, 200(s0)
	fcvt.l.d x14, f26, rtz
	sb x11, 83(s0)
	auipc x15, 1014879
	addiw x3, x13, 448
	addi x11, x31, -981
	add  x11, x11, x11
	slli x11, x11, 1
	sd x17, 144(s0)
	addi x17, x18, 756
	add  x17, x17, x17
	slli x17, x17, 1
	slt x9, x29, x15
	addi x19, x18, 287
	add  x19, x19, x19
	slli x19, x19, 1
	sltiu x9, x6, 404
	sltiu x25, x13, 2033
	lwu x21, 80(s0)
	addi x21, x13, -125
	add  x21, x21, x21
	slli x21, x21, 1
	addi x29, x26, -154
	add  x29, x29, x29
	slli x29, x29, 1
	addi x29, x4, -127
	add  x29, x29, x29
	slli x29, x29, 1
	fld f22, 240(s0)
	fld f7, 192(s0)
	fmul.d f1, f22, f7
	fsd f1, 24(s0)
	fcvt.l.d x17, f22, rtz
	sh x31, 96(s0)
	flw f29, 244(s0)
	flw f29, 40(s0)
	fmul.s f2, f29, f29
	fsw f2, 216(s0)
	fcvt.w.s x23, f29, rtz
	addi x29, x21, 1339
	add  x29, x29, x29
	slli x29, x29, 1
	flw f13, 208(s0)
	flw f22, 180(s0)
	fsub.s f28, f13, f22
	fsw f28, 148(s0)
	fcvt.w.s x24, f13, rtz
	flw f23, 116(s0)
	flw f8, 240(s0)
	fmul.s f6, f23, f8
	fsw f6, 128(s0)
	fcvt.w.s x5, f23, rtz
	flw f27, 208(s0)
	flw f13, 252(s0)
	fmax.s f12, f27, f13
	fsw f12, 28(s0)
	fcvt.w.s x13, f27, rtz
	sw x19, 172(s0)
	mul x21, x21, x13
	flw f14, 132(s0)
	flw f30, 40(s0)
	fadd.s f24, f14, f30
	fsw f24, 56(s0)
	fcvt.w.s x22, f14, rtz
	xor x5, x12, x25
	flw f30, 228(s0)
	flw f27, 100(s0)
	fmin.s f11, f30, f27
	fsw f11, 76(s0)
	fcvt.w.s x29, f30, rtz
	flw f12, 124(s0)
	flw f2, 248(s0)
	fadd.s f27, f12, f2
	fsw f27, 160(s0)
	fcvt.w.s x27, f12, rtz
	addi x23, x29, -899
	add  x23, x23, x23
	slli x23, x23, 1
	xor x24, x17, x23
	lwu x18, 0(s0)
	bne x26, x29, .Lsk_14
	nop
.Lsk_14:
	bltu x11, x27, .Lsk_15
	nop
.Lsk_15:
	beq x24, x22, .Lsk_16
	nop
.Lsk_16:
	beq x23, x7, .Lsk_17
	nop
.Lsk_17:
	sw x9, 60(s0)
	sd x3, 248(s0)
	sh x21, 80(s0)
	rem x23, x4, x19
	mulw x7, x3, x15
	bge x5, x22, .Lsk_18
	nop
.Lsk_18:
	addi x26, x18, 1132
	add  x26, x26, x26
	slli x26, x26, 1
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
	.word 0x498B3394
	.word 0xB0EEF857
	.word 0xA717DD91
	.word 0x381375DB
	.word 0x38BF9207
	.word 0x2B89372D
	.word 0x1911157E
	.word 0x47E1DCEE
	.word 0xEF6E2723
	.word 0x6AC6E64D
	.word 0x22A0F7B0
	.word 0xFC5B41F2
	.word 0x60ECF2A6
	.word 0x63308B65
	.word 0xBF32DDFB
	.word 0xC4A30E5B
	.word 0x8D619372
	.word 0x80727023
	.word 0x7E6A75B0
	.word 0x10D7C84D
	.word 0x0CD358C6
	.word 0xDD38A437
	.word 0x029E6EBD
	.word 0x86E45943
	.word 0x191D63C1
	.word 0x466E26C9
	.word 0xC525E555
	.word 0xB10F9854
	.word 0x488E0833
	.word 0x89A93115
	.word 0xEE853F1D
	.word 0x525A9A08
	.word 0x5A04D9A6
	.word 0x2DDC5E2F
	.word 0xA76FEAD9
	.word 0x48B3EE2A
	.word 0x63AC7042
	.word 0x4D8353A5
	.word 0x08E14075
	.word 0x62C95DF1
	.word 0x896B95A2
	.word 0xBACE9AF6
	.word 0xBDB29E44
	.word 0x53CD61EA
	.word 0xC896DE59
	.word 0x25B3C990
	.word 0x3128EF0F
	.word 0x9E65665B
	.word 0x68C3E1E4
	.word 0x1328592F
	.word 0x08E66E5C
	.word 0x1AE071CA
	.word 0x882A2B35
	.word 0xA50F0427
	.word 0xC5728F36
	.word 0xD3AA56BC
	.word 0x0557BE62
	.word 0xE7749B72
	.word 0x17744BD8
	.word 0x2ADFC500
	.word 0xDA353CE5
	.word 0x1534AA58
	.word 0xFE2E402B
	.word 0x830B1546
