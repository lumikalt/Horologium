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
	addi x6, x17, 973
	add  x6, x6, x6
	slli x6, x6, 1
	xor x6, x29, x23
	sb x9, 107(s0)
	addi x31, x7, -1534
	add  x31, x31, x31
	slli x31, x31, 1
	slti x17, x28, -152
	bgeu x22, x27, .Lsk_0
	nop
.Lsk_0:
	flw f15, 32(s0)
	flw f21, 212(s0)
	fmin.s f13, f15, f21
	fsw f13, 184(s0)
	fcvt.w.s x19, f15, rtz
	rem x24, x28, x11
	auipc x3, 368796
	srai x10, x9, 18
	addi x11, x23, -899
	add  x11, x11, x11
	slli x11, x11, 1
	addi x30, x16, -1714
	add  x30, x30, x30
	slli x30, x30, 1
	addi x10, x7, 334
	add  x10, x10, x10
	slli x10, x10, 1
	lb x7, 83(s0)
	addi x19, x6, 49
	add  x19, x19, x19
	slli x19, x19, 1
	flw f14, 52(s0)
	flw f28, 44(s0)
	fmul.s f26, f14, f28
	fsw f26, 220(s0)
	fcvt.w.s x26, f14, rtz
	lb x15, 7(s0)
	slti x14, x19, -534
	addi x17, x30, 985
	add  x17, x17, x17
	slli x17, x17, 1
	lbu x10, 250(s0)
	addi x28, x13, -470
	add  x28, x28, x28
	slli x28, x28, 1
	lw x20, 224(s0)
	addi x29, x4, 2020
	add  x29, x29, x29
	slli x29, x29, 1
	srl x7, x13, x23
	slti x4, x14, -1226
	beq x24, x10, .Lsk_1
	nop
.Lsk_1:
	flw f6, 48(s0)
	flw f19, 48(s0)
	fmax.s f0, f6, f19
	fsw f0, 252(s0)
	fcvt.w.s x3, f6, rtz
	lhu x12, 112(s0)
	srli x16, x24, 28
	flw f29, 88(s0)
	flw f2, 120(s0)
	fadd.s f8, f29, f2
	fsw f8, 40(s0)
	fcvt.w.s x25, f29, rtz
	lbu x20, 222(s0)
	lw x14, 232(s0)
	mulhu x19, x19, x13
	div x7, x9, x18
	mulhu x23, x3, x25
	bgeu x23, x26, .Lsk_2
	nop
.Lsk_2:
	addi x12, x14, -957
	add  x12, x12, x12
	slli x12, x12, 1
	addi x10, x9, 70
	add  x10, x10, x10
	slli x10, x10, 1
	lbu x10, 105(s0)
	bgeu x30, x30, .Lsk_3
	nop
.Lsk_3:
	srai x16, x27, 13
	lbu x26, 125(s0)
	lbu x19, 250(s0)
	blt x4, x6, .Lsk_4
	nop
.Lsk_4:
	slti x27, x20, 1314
	remu x17, x12, x23
	sh x6, 0(s0)
	lb x4, 122(s0)
	add x26, x11, x30
	srli x20, x11, 5
	lw x18, 204(s0)
	add x17, x29, x16
	addi x31, x26, 17
	add  x31, x31, x31
	slli x31, x31, 1
	mulhsu x14, x6, x31
	srli x15, x22, 8
	flw f27, 4(s0)
	flw f5, 212(s0)
	fsub.s f23, f27, f5
	fsw f23, 232(s0)
	fcvt.w.s x25, f27, rtz
	addi x18, x30, -876
	add  x18, x18, x18
	slli x18, x18, 1
	addi x18, x26, 2035
	add  x18, x18, x18
	slli x18, x18, 1
	addi x14, x20, 1421
	add  x14, x14, x14
	slli x14, x14, 1
	lhu x9, 116(s0)
	sltu x12, x29, x21
	sub x20, x26, x29
	flw f14, 4(s0)
	flw f22, 184(s0)
	fmin.s f28, f14, f22
	fsw f28, 212(s0)
	fcvt.w.s x20, f14, rtz
	lui x14, 681356
	sb x30, 201(s0)
	mul x25, x6, x21
	sw x26, 80(s0)
	flw f30, 40(s0)
	flw f15, 96(s0)
	fmin.s f29, f30, f15
	fsw f29, 56(s0)
	fcvt.w.s x12, f30, rtz
	lbu x15, 95(s0)
	bge x11, x5, .Lsk_5
	nop
.Lsk_5:
	flw f4, 220(s0)
	flw f13, 240(s0)
	fmax.s f10, f4, f13
	fsw f10, 248(s0)
	fcvt.w.s x24, f4, rtz
	srli x18, x5, 0
	lui x6, 463708
	beq x7, x18, .Lsk_6
	nop
.Lsk_6:
	lbu x22, 7(s0)
	flw f17, 84(s0)
	flw f31, 52(s0)
	fmin.s f28, f17, f31
	fsw f28, 248(s0)
	fcvt.w.s x17, f17, rtz
	sw x3, 160(s0)
	lw x27, 32(s0)
	slli x27, x18, 19
	lb x30, 14(s0)
	flw f20, 212(s0)
	flw f0, 196(s0)
	fmax.s f20, f20, f0
	fsw f20, 172(s0)
	fcvt.w.s x18, f20, rtz
	sltiu x15, x5, 897
	addi x31, x20, -2028
	add  x31, x31, x31
	slli x31, x31, 1
	bne x25, x26, .Lsk_7
	nop
.Lsk_7:
	lh x9, 164(s0)
	auipc x5, 642432
	auipc x5, 359512
	bne x12, x17, .Lsk_8
	nop
.Lsk_8:
	auipc x29, 673479
	addi x19, x17, -470
	add  x19, x19, x19
	slli x19, x19, 1
	or x10, x6, x19
	lui x3, 44494
	addi x20, x12, -849
	add  x20, x20, x20
	slli x20, x20, 1
	blt x12, x15, .Lsk_9
	nop
.Lsk_9:
	xori x9, x24, 1548
	auipc x9, 129552
	lbu x15, 45(s0)
	lh x30, 208(s0)
	ori x21, x24, -1643
	addi x18, x19, 587
	add  x18, x18, x18
	slli x18, x18, 1
	flw f15, 240(s0)
	flw f7, 188(s0)
	fmul.s f1, f15, f7
	fsw f1, 204(s0)
	fcvt.w.s x10, f15, rtz
	mulhsu x24, x6, x7
	bge x21, x12, .Lsk_10
	nop
.Lsk_10:
	sra x24, x9, x30
	lb x17, 35(s0)
	or x27, x5, x27
	slt x16, x7, x27
	sb x14, 30(s0)
	slti x16, x7, 373
	xori x25, x9, -403
	flw f7, 244(s0)
	flw f11, 92(s0)
	fmin.s f28, f7, f11
	fsw f28, 156(s0)
	fcvt.w.s x15, f7, rtz
	addi x11, x25, -1252
	add  x11, x11, x11
	slli x11, x11, 1
	slti x31, x31, -1172
	addi x7, x24, 1329
	add  x7, x7, x7
	slli x7, x7, 1
	beq x10, x16, .Lsk_11
	nop
.Lsk_11:
	srli x18, x31, 6
	addi x23, x7, 1364
	auipc x19, 1003851
	auipc x10, 862892
	slli x5, x6, 4
	addi x10, x13, -1843
	add  x10, x10, x10
	slli x10, x10, 1
	or x29, x26, x14
	flw f23, 120(s0)
	flw f12, 68(s0)
	fsub.s f0, f23, f12
	fsw f0, 68(s0)
	fcvt.w.s x29, f23, rtz
	add x26, x9, x4
	mulh x18, x9, x9
	bgeu x12, x21, .Lsk_12
	nop
.Lsk_12:
	remu x22, x12, x11
	slt x21, x16, x26
	bltu x7, x3, .Lsk_13
	nop
.Lsk_13:
	mulhsu x12, x15, x10
	sb x15, 171(s0)
	mulh x6, x6, x28
	addi x20, x15, -1061
	add  x20, x20, x20
	slli x20, x20, 1
	lhu x22, 250(s0)
	or x27, x19, x20
	lui x22, 620807
	flw f0, 172(s0)
	flw f28, 36(s0)
	fsub.s f23, f0, f28
	fsw f23, 80(s0)
	fcvt.w.s x18, f0, rtz
	lw x22, 236(s0)
	lh x10, 88(s0)
	lh x22, 90(s0)
	blt x18, x22, .Lsk_14
	nop
.Lsk_14:
	mulh x16, x28, x12
	flw f27, 108(s0)
	flw f16, 4(s0)
	fsub.s f27, f27, f16
	fsw f27, 220(s0)
	fcvt.w.s x19, f27, rtz
	sh x9, 40(s0)
	div x6, x7, x23
	srai x28, x26, 30
	sh x5, 152(s0)
	mulhsu x26, x10, x19
	srli x26, x4, 4
	addi x12, x30, -1729
	add  x12, x12, x12
	slli x12, x12, 1
	auipc x20, 918521
	sw x17, 144(s0)
	sra x5, x15, x19
	bgeu x26, x18, .Lsk_15
	nop
.Lsk_15:
	addi x22, x6, 1140
	add  x22, x22, x22
	slli x22, x22, 1
	mul x16, x7, x9
	lh x13, 228(s0)
	flw f19, 80(s0)
	flw f16, 248(s0)
	fmul.s f6, f19, f16
	fsw f6, 4(s0)
	fcvt.w.s x9, f19, rtz
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
	.word 0xF22CA8ED
	.word 0xC32B6780
	.word 0xD6FEE2A7
	.word 0x64B62A67
	.word 0x39775679
	.word 0x356C8F52
	.word 0x7E0E53AB
	.word 0x813F4378
	.word 0xBF8F970F
	.word 0xCFEA76DA
	.word 0x85835A3A
	.word 0x21FDB97E
	.word 0xA6196F5D
	.word 0x2FCC13C9
	.word 0x8C52F8A8
	.word 0xA5D98ED5
	.word 0x97864045
	.word 0x8AD42B0B
	.word 0xF9BF2249
	.word 0x550D8C40
	.word 0xD15D23C8
	.word 0x81DF8A28
	.word 0x41F93A99
	.word 0xB6F0CD41
	.word 0x76EAC208
	.word 0xCF950C27
	.word 0x44C2695D
	.word 0xBF6CD948
	.word 0x8A680661
	.word 0xCF33820E
	.word 0xC33C4EA3
	.word 0x986DAD97
	.word 0x07F722C2
	.word 0x6AB39D64
	.word 0x56871E29
	.word 0xA3CC0425
	.word 0x47E35780
	.word 0xD54AB941
	.word 0x19B6DD80
	.word 0x984900C9
	.word 0xCE42C97A
	.word 0xBFF07B55
	.word 0x5F5F3F1A
	.word 0x844D936F
	.word 0x180746E7
	.word 0x399E6007
	.word 0xB18FD02A
	.word 0xBA99D5E3
	.word 0x7FDE2D2C
	.word 0x201E9566
	.word 0x6751D95A
	.word 0xA0214EF0
	.word 0xA9CCA868
	.word 0x39E62A81
	.word 0x58B3BD32
	.word 0x74409337
	.word 0x9EB66C81
	.word 0x51C3AD34
	.word 0x4F5E1BE4
	.word 0xCE50B17C
	.word 0xA7C0F8FD
	.word 0x61E5448B
	.word 0x307F87CE
	.word 0xFD796E82
