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
	mulhu x30, x28, x14
	srai x29, x30, 12
	flw f26, 176(s0)
	flw f4, 4(s0)
	fsub.s f16, f26, f4
	fsw f16, 64(s0)
	fcvt.w.s x25, f26, rtz
	lui x26, 141553
	sll x26, x23, x21
	bgeu x26, x19, .Lsk_0
	nop
.Lsk_0:
	slli x23, x20, 10
	sh x9, 40(s0)
	lb x31, 45(s0)
	beq x10, x13, .Lsk_1
	nop
.Lsk_1:
	bne x9, x25, .Lsk_2
	nop
.Lsk_2:
	srai x30, x20, 23
	flw f9, 84(s0)
	flw f8, 176(s0)
	fmax.s f28, f9, f8
	fsw f28, 204(s0)
	fcvt.w.s x31, f9, rtz
	flw f11, 184(s0)
	flw f0, 220(s0)
	fmin.s f16, f11, f0
	fsw f16, 108(s0)
	fcvt.w.s x3, f11, rtz
	mulh x31, x13, x7
	bltu x14, x13, .Lsk_3
	nop
.Lsk_3:
	lbu x30, 239(s0)
	bltu x3, x6, .Lsk_4
	nop
.Lsk_4:
	sltu x7, x12, x25
	lw x23, 200(s0)
	ori x24, x27, -1212
	sh x29, 232(s0)
	auipc x11, 430562
	bgeu x15, x19, .Lsk_5
	nop
.Lsk_5:
	addi x5, x30, -799
	add  x5, x5, x5
	slli x5, x5, 1
	srai x19, x30, 18
	slli x28, x10, 7
	addi x28, x17, 1232
	add  x28, x28, x28
	slli x28, x28, 1
	lh x3, 248(s0)
	addi x11, x16, -1545
	add  x11, x11, x11
	slli x11, x11, 1
	slt x25, x17, x26
	lw x12, 8(s0)
	addi x22, x12, 1531
	add  x22, x22, x22
	slli x22, x22, 1
	addi x15, x5, -225
	add  x15, x15, x15
	slli x15, x15, 1
	sb x23, 116(s0)
	rem x13, x7, x9
	sll x17, x4, x24
	sb x15, 162(s0)
	flw f8, 248(s0)
	flw f0, 144(s0)
	fmax.s f3, f8, f0
	fsw f3, 180(s0)
	fcvt.w.s x6, f8, rtz
	or x27, x25, x17
	flw f6, 36(s0)
	flw f14, 140(s0)
	fmin.s f6, f6, f14
	fsw f6, 0(s0)
	fcvt.w.s x22, f6, rtz
	sw x21, 176(s0)
	addi x31, x9, -790
	add  x31, x31, x31
	slli x31, x31, 1
	sb x10, 94(s0)
	sw x5, 236(s0)
	sltiu x22, x23, -512
	beq x20, x29, .Lsk_6
	nop
.Lsk_6:
	andi x12, x27, 1082
	flw f23, 172(s0)
	flw f28, 80(s0)
	fsub.s f14, f23, f28
	fsw f14, 232(s0)
	fcvt.w.s x5, f23, rtz
	addi x28, x29, 389
	add  x28, x28, x28
	slli x28, x28, 1
	bne x24, x18, .Lsk_7
	nop
.Lsk_7:
	addi x6, x10, -1825
	add  x6, x6, x6
	slli x6, x6, 1
	sb x21, 208(s0)
	sw x30, 104(s0)
	srli x30, x6, 18
	addi x15, x27, 937
	add  x15, x15, x15
	slli x15, x15, 1
	addi x16, x18, -161
	add  x16, x16, x16
	slli x16, x16, 1
	flw f21, 8(s0)
	flw f4, 148(s0)
	fmin.s f23, f21, f4
	fsw f23, 76(s0)
	fcvt.w.s x30, f21, rtz
	mulhsu x5, x9, x12
	lhu x26, 8(s0)
	addi x20, x24, -1291
	add  x20, x20, x20
	slli x20, x20, 1
	sub x25, x24, x24
	slt x17, x31, x9
	bltu x13, x22, .Lsk_8
	nop
.Lsk_8:
	flw f23, 100(s0)
	flw f10, 124(s0)
	fmax.s f29, f23, f10
	fsw f29, 156(s0)
	fcvt.w.s x11, f23, rtz
	addi x14, x10, -1124
	add  x14, x14, x14
	slli x14, x14, 1
	flw f18, 220(s0)
	flw f13, 200(s0)
	fmax.s f30, f18, f13
	fsw f30, 180(s0)
	fcvt.w.s x22, f18, rtz
	sb x22, 239(s0)
	srli x4, x23, 23
	lw x11, 48(s0)
	rem x12, x14, x28
	andi x24, x9, 1480
	mulhsu x21, x23, x11
	lh x6, 130(s0)
	flw f21, 208(s0)
	flw f10, 208(s0)
	fmul.s f4, f21, f10
	fsw f4, 52(s0)
	fcvt.w.s x4, f21, rtz
	lw x27, 20(s0)
	lw x9, 228(s0)
	sltu x17, x4, x24
	bge x26, x12, .Lsk_9
	nop
.Lsk_9:
	sh x13, 106(s0)
	andi x15, x14, -1864
	addi x23, x13, -55
	add  x23, x23, x23
	slli x23, x23, 1
	lbu x30, 116(s0)
	auipc x16, 807652
	lh x27, 202(s0)
	slt x20, x7, x16
	lh x11, 132(s0)
	lui x7, 275286
	lui x29, 799183
	mulh x12, x23, x29
	lbu x9, 42(s0)
	sll x7, x29, x3
	srai x25, x7, 20
	mulhsu x18, x20, x23
	flw f17, 124(s0)
	flw f27, 232(s0)
	fmax.s f14, f17, f27
	fsw f14, 80(s0)
	fcvt.w.s x15, f17, rtz
	slli x9, x23, 15
	addi x14, x19, 1115
	lui x11, 125664
	flw f13, 156(s0)
	flw f30, 24(s0)
	fadd.s f28, f13, f30
	fsw f28, 16(s0)
	fcvt.w.s x6, f13, rtz
	rem x15, x27, x15
	addi x30, x21, 219
	add  x30, x30, x30
	slli x30, x30, 1
	bge x4, x12, .Lsk_10
	nop
.Lsk_10:
	lw x10, 180(s0)
	addi x19, x5, -1859
	add  x19, x19, x19
	slli x19, x19, 1
	lh x17, 202(s0)
	sra x11, x21, x27
	addi x18, x16, 1026
	add  x18, x18, x18
	slli x18, x18, 1
	lb x26, 134(s0)
	lb x7, 39(s0)
	sub x20, x18, x6
	sw x4, 252(s0)
	slt x14, x12, x17
	lw x13, 104(s0)
	xori x25, x24, -1719
	flw f6, 164(s0)
	flw f11, 100(s0)
	fmin.s f16, f6, f11
	fsw f16, 132(s0)
	fcvt.w.s x20, f6, rtz
	bge x31, x20, .Lsk_11
	nop
.Lsk_11:
	addi x28, x4, 1797
	add  x28, x28, x28
	slli x28, x28, 1
	lw x11, 68(s0)
	flw f12, 32(s0)
	flw f12, 188(s0)
	fsub.s f11, f12, f12
	fsw f11, 244(s0)
	fcvt.w.s x3, f12, rtz
	bltu x15, x9, .Lsk_12
	nop
.Lsk_12:
	flw f2, 0(s0)
	flw f4, 32(s0)
	fmul.s f19, f2, f4
	fsw f19, 4(s0)
	fcvt.w.s x12, f2, rtz
	sub x10, x13, x11
	addi x9, x16, 873
	add  x9, x9, x9
	slli x9, x9, 1
	lw x29, 4(s0)
	addi x4, x3, 1225
	add  x4, x4, x4
	slli x4, x4, 1
	lhu x15, 168(s0)
	lw x12, 120(s0)
	addi x11, x11, 126
	add  x11, x11, x11
	slli x11, x11, 1
	bge x12, x9, .Lsk_13
	nop
.Lsk_13:
	lw x26, 28(s0)
	lw x25, 128(s0)
	lb x18, 164(s0)
	auipc x25, 410374
	addi x16, x22, -305
	add  x16, x16, x16
	slli x16, x16, 1
	lhu x17, 138(s0)
	addi x31, x29, 1396
	add  x31, x31, x31
	slli x31, x31, 1
	ori x31, x31, 287
	lui x23, 519321
	addi x20, x15, -1674
	add  x20, x20, x20
	slli x20, x20, 1
	mulhsu x14, x19, x6
	andi x30, x12, -350
	flw f24, 12(s0)
	flw f27, 120(s0)
	fmul.s f10, f24, f27
	fsw f10, 8(s0)
	fcvt.w.s x6, f24, rtz
	auipc x5, 878913
	or x26, x30, x5
	lh x19, 224(s0)
	flw f0, 160(s0)
	flw f31, 48(s0)
	fmin.s f30, f0, f31
	fsw f30, 40(s0)
	fcvt.w.s x15, f0, rtz
	addi x6, x9, -221
	add  x6, x6, x6
	slli x6, x6, 1
	lhu x21, 112(s0)
	lhu x21, 118(s0)
	lui x24, 618013
	mulh x6, x15, x25
	sltu x5, x25, x27
	xori x22, x20, -1877
	addi x5, x5, -982
	add  x5, x5, x5
	slli x5, x5, 1
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
	.word 0x98950229
	.word 0x62AB9053
	.word 0x7FEFC5E9
	.word 0x445D1357
	.word 0xDE8CB51C
	.word 0xB09D7F48
	.word 0xDEEE032C
	.word 0x915940EE
	.word 0xE251F771
	.word 0xBB6BC1D6
	.word 0x3663CBA5
	.word 0x90ACCB94
	.word 0xB6905DA0
	.word 0x03E9A982
	.word 0xE7E0426B
	.word 0xB676D568
	.word 0x2AFAF4D0
	.word 0x21E6CC1E
	.word 0xB98A64C3
	.word 0x0EB5C394
	.word 0x6740273A
	.word 0x4C6551DE
	.word 0x2848F1CC
	.word 0x56FEBCC2
	.word 0x300AACC3
	.word 0x6635B952
	.word 0x998BD745
	.word 0x4F172C4F
	.word 0xB8C34ED2
	.word 0xB190CA27
	.word 0x8B563242
	.word 0x608F934D
	.word 0xC5D25E34
	.word 0xE88456E5
	.word 0xC0155CDE
	.word 0x9080D2E1
	.word 0x28EE9097
	.word 0x267A3A0B
	.word 0xE6BF15C1
	.word 0xC06923EB
	.word 0xC8D8F84F
	.word 0x02B71B00
	.word 0x41F34182
	.word 0x5C086CEF
	.word 0xB4172A1A
	.word 0x1AE1A1E9
	.word 0x4D050602
	.word 0x16EFA49A
	.word 0x83CC4AB2
	.word 0x8B142136
	.word 0xBED6FD6C
	.word 0xC846609A
	.word 0x409DCB3E
	.word 0x0BBD0616
	.word 0x73C90576
	.word 0x29D53C1C
	.word 0xC663CCE4
	.word 0x37060540
	.word 0xAE7B4AD8
	.word 0x99C5237C
	.word 0x2460A45E
	.word 0x91D67C4D
	.word 0xBB91191F
	.word 0x8DEB7BD8
