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
	addi x6, x17, 973
	add  x6, x6, x6
	slli x6, x6, 1
	divu x6, x29, x23
	sh x25, 106(s0)
	addi x31, x7, -1534
	add  x31, x31, x31
	slli x31, x31, 1
	slti x17, x28, -152
	bgeu x22, x27, .Lsk_0
	nop
.Lsk_0:
	flw f15, 212(s0)
	flw f21, 184(s0)
	fadd.s f13, f15, f21
	fsw f13, 228(s0)
	fcvt.w.s x19, f15, rtz
	addi x28, x17, 1866
	add  x28, x28, x28
	slli x28, x28, 1
	flw f25, 244(s0)
	flw f15, 252(s0)
	fmul.s f0, f25, f15
	fsw f0, 88(s0)
	fcvt.w.s x3, f25, rtz
	auipc x9, 468172
	sh x31, 0(s0)
	fld f2, 144(s0)
	fld f24, 232(s0)
	fmul.d f12, f2, f24
	fsd f12, 184(s0)
	fcvt.l.d x7, f2, rtz
	addi x25, x9, 1913
	add  x25, x25, x25
	slli x25, x25, 1
	srli x12, x28, 53
	bltu x22, x21, .Lsk_1
	nop
.Lsk_1:
	mulhsu x5, x17, x27
	srli x3, x14, 22
	sltiu x29, x17, 1322
	flw f13, 60(s0)
	flw f31, 224(s0)
	fadd.s f19, f13, f31
	fsw f19, 60(s0)
	fcvt.w.s x25, f13, rtz
	sw x28, 68(s0)
	slli x26, x16, 42
	bne x6, x25, .Lsk_2
	nop
.Lsk_2:
	addi x24, x10, -292
	add  x24, x24, x24
	slli x24, x24, 1
	addi x13, x3, -1810
	add  x13, x13, x13
	slli x13, x13, 1
	addiw x19, x24, 1411
	sd x12, 112(s0)
	srli x16, x24, 56
	flw f29, 40(s0)
	flw f2, 12(s0)
	fmin.s f8, f29, f2
	fsw f8, 228(s0)
	fcvt.w.s x25, f29, rtz
	addi x29, x31, 1519
	add  x29, x29, x29
	slli x29, x29, 1
	lw x14, 232(s0)
	mulhu x19, x19, x13
	sllw x7, x9, x18
	subw x23, x3, x25
	bgeu x23, x26, .Lsk_3
	nop
.Lsk_3:
	addi x12, x14, -957
	add  x12, x12, x12
	slli x12, x12, 1
	addi x10, x9, 70
	add  x10, x10, x10
	slli x10, x10, 1
	ld x21, 104(s0)
	slti x20, x30, 1276
	addi x27, x23, 1459
	add  x27, x27, x27
	slli x27, x27, 1
	sh x20, 170(s0)
	flw f15, 144(s0)
	flw f27, 24(s0)
	fmul.s f12, f15, f27
	fsw f12, 56(s0)
	fcvt.w.s x22, f15, rtz
	srl x20, x26, x10
	addiw x12, x13, 20
	slli x3, x17, 30
	add x26, x11, x30
	srli x20, x11, 10
	lw x18, 204(s0)
	mul x17, x29, x16
	addi x31, x26, 17
	add  x31, x31, x31
	slli x31, x31, 1
	mulhsu x14, x6, x31
	srli x15, x22, 16
	fld f27, 208(s0)
	fld f5, 232(s0)
	fmin.d f23, f27, f5
	fsd f23, 104(s0)
	fcvt.l.d x25, f27, rtz
	lh x31, 116(s0)
	ld x29, 248(s0)
	addi x14, x20, 1421
	add  x14, x14, x14
	slli x14, x14, 1
	lwu x18, 92(s0)
	sh x15, 130(s0)
	fld f7, 104(s0)
	fld f18, 72(s0)
	fsub.d f2, f7, f18
	fsd f2, 56(s0)
	fcvt.l.d x4, f7, rtz
	remuw x15, x28, x29
	addi x22, x15, 1351
	add  x22, x22, x22
	slli x22, x22, 1
	lhu x14, 12(s0)
	addi x9, x14, -725
	add  x9, x9, x9
	slli x9, x9, 1
	bge x9, x14, .Lsk_4
	nop
.Lsk_4:
	beq x13, x17, .Lsk_5
	nop
.Lsk_5:
	addi x21, x22, -295
	add  x21, x21, x21
	slli x21, x21, 1
	bne x30, x13, .Lsk_6
	nop
.Lsk_6:
	lhu x12, 42(s0)
	srli x19, x17, 23
	bge x11, x5, .Lsk_7
	nop
.Lsk_7:
	fld f4, 240(s0)
	fld f13, 248(s0)
	fmin.d f10, f4, f13
	fsd f10, 136(s0)
	fcvt.l.d x24, f4, rtz
	lui x22, 726127
	lh x15, 98(s0)
	addi x27, x31, -1338
	add  x27, x27, x27
	slli x27, x27, 1
	srai x17, x24, 1
	fld f17, 48(s0)
	fld f31, 248(s0)
	fmax.d f28, f17, f31
	fsd f28, 192(s0)
	fcvt.l.d x17, f17, rtz
	addi x3, x14, 1347
	add  x3, x3, x3
	slli x3, x3, 1
	lw x27, 32(s0)
	slli x27, x18, 39
	ld x14, 8(s0)
	fld f20, 208(s0)
	fld f0, 192(s0)
	fmax.d f20, f20, f0
	fsd f20, 168(s0)
	fcvt.l.d x18, f20, rtz
	sltiu x15, x5, 897
	addi x31, x20, -2028
	add  x31, x31, x31
	slli x31, x31, 1
	bne x25, x26, .Lsk_8
	nop
.Lsk_8:
	lh x9, 164(s0)
	andi x5, x25, 1158
	addiw x5, x20, 1684
	div x22, x10, x13
	fld f10, 208(s0)
	fld f27, 96(s0)
	fmul.d f20, f10, f27
	fsd f20, 32(s0)
	fcvt.l.d x26, f10, rtz
	slli x7, x27, 2
	lhu x9, 10(s0)
	addi x20, x12, -849
	add  x20, x20, x20
	slli x20, x20, 1
	blt x12, x15, .Lsk_9
	nop
.Lsk_9:
	xori x9, x24, 1548
	addiw x9, x6, 1071
	addi x22, x4, 1114
	add  x22, x22, x22
	slli x22, x22, 1
	subw x15, x5, x30
	addi x3, x23, -1641
	add  x3, x3, x3
	slli x3, x3, 1
	sw x7, 228(s0)
	ld x11, 120(s0)
	addi x6, x3, -482
	add  x6, x6, x6
	slli x6, x6, 1
	fld f23, 56(s0)
	fld f25, 192(s0)
	fsub.d f23, f23, f25
	fsd f23, 80(s0)
	fcvt.l.d x6, f23, rtz
	bge x21, x12, .Lsk_10
	nop
.Lsk_10:
	remu x24, x9, x30
	ld x25, 136(s0)
	lbu x5, 153(s0)
	sraiw x5, x6, 11
	fld f9, 160(s0)
	fld f15, 24(s0)
	fmin.d f15, f9, f15
	fsd f15, 160(s0)
	fcvt.l.d x27, f9, rtz
	sll x11, x13, x27
	fld f12, 88(s0)
	fld f24, 224(s0)
	fmin.d f7, f12, f24
	fsd f7, 176(s0)
	fcvt.l.d x29, f12, rtz
	mulh x16, x21, x14
	ld x24, 40(s0)
	slt x25, x25, x7
	bgeu x29, x17, .Lsk_11
	nop
.Lsk_11:
	beq x10, x16, .Lsk_12
	nop
.Lsk_12:
	srli x18, x31, 13
	slliw x23, x7, 26
	ori x19, x7, 1508
	ld x10, 144(s0)
	flw f21, 48(s0)
	flw f26, 8(s0)
	fmin.s f6, f21, f26
	fsw f6, 36(s0)
	fcvt.w.s x20, f21, rtz
	xor x13, x25, x29
	bgeu x31, x21, .Lsk_13
	nop
.Lsk_13:
	addw x25, x26, x29
	sh x27, 68(s0)
	sra x23, x26, x3
	sub x22, x18, x9
	bgeu x12, x21, .Lsk_14
	nop
.Lsk_14:
	remu x22, x12, x11
	slt x21, x16, x26
	bltu x7, x3, .Lsk_15
	nop
.Lsk_15:
	subw x12, x15, x10
	sb x20, 171(s0)
	mulw x6, x6, x28
	addi x20, x15, -1061
	add  x20, x20, x20
	slli x20, x20, 1
	lhu x22, 250(s0)
	or x27, x19, x20
	addiw x22, x9, -1012
	addi x29, x23, -1964
	add  x29, x29, x29
	slli x29, x29, 1
	addi x18, x25, 899
	add  x18, x18, x18
	slli x18, x18, 1
	lbu x5, 81(s0)
	srli x24, x4, 26
	and x9, x20, x20
	divuw x11, x12, x16
	flw f19, 132(s0)
	flw f6, 220(s0)
	fsub.s f17, f19, f6
	fsw f17, 244(s0)
	fcvt.w.s x29, f19, rtz
	addiw x9, x23, -734
	auipc x22, 752075
	ori x5, x3, 713
	flw f30, 60(s0)
	flw f4, 96(s0)
	fmul.s f16, f30, f4
	fsw f16, 144(s0)
	fcvt.w.s x5, f30, rtz
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
	.word 0x258A8DFA
	.word 0xB5C7F5CD
	.word 0x3A8C708C
	.word 0xD53B2DC8
	.word 0xD3A3CAB0
	.word 0x30A0BD65
	.word 0x49180BD8
	.word 0xBF80BA4D
	.word 0x5C22C04C
	.word 0x795DD87C
	.word 0x63A28B1B
	.word 0xB6B85778
	.word 0x24D1DC55
	.word 0xFFE689BD
	.word 0x2AF4EA71
	.word 0xCFD04F63
	.word 0x4ACFABFA
	.word 0x43EA555A
	.word 0xE62E0706
	.word 0x7C76B1BD
	.word 0xB8C24A71
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
