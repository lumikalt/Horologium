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
	flw f2, 108(s0)
	flw f16, 92(s0)
	fmax.s f22, f2, f16
	fsw f22, 44(s0)
	fcvt.w.s x27, f2, rtz
	srli x24, x20, 6
	sb x16, 43(s0)
	flw f22, 28(s0)
	flw f0, 24(s0)
	fmax.s f5, f22, f0
	fsw f5, 60(s0)
	fcvt.w.s x14, f22, rtz
	srai x23, x19, 22
	ori x21, x3, 479
	bltu x17, x20, .Lsk_0
	nop
.Lsk_0:
	srl x16, x18, x3
	addi x3, x19, 1238
	add  x3, x3, x3
	slli x3, x3, 1
	addi x4, x3, -857
	add  x4, x4, x4
	slli x4, x4, 1
	srl x11, x27, x28
	lhu x22, 80(s0)
	xori x16, x12, 1035
	lh x6, 140(s0)
	bltu x9, x23, .Lsk_1
	nop
.Lsk_1:
	xor x6, x31, x3
	lb x7, 201(s0)
	srai x24, x21, 6
	and x31, x23, x26
	xori x18, x29, 1115
	sw x19, 148(s0)
	flw f26, 168(s0)
	flw f8, 196(s0)
	fadd.s f4, f26, f8
	fsw f4, 192(s0)
	fcvt.w.s x14, f26, rtz
	sh x18, 234(s0)
	slt x22, x5, x6
	flw f4, 228(s0)
	flw f20, 184(s0)
	fmul.s f17, f4, f20
	fsw f17, 228(s0)
	fcvt.w.s x23, f4, rtz
	addi x4, x15, -287
	add  x4, x4, x4
	slli x4, x4, 1
	lui x10, 1019507
	lhu x20, 224(s0)
	sh x20, 14(s0)
	bne x30, x18, .Lsk_2
	nop
.Lsk_2:
	or x19, x31, x3
	flw f22, 96(s0)
	flw f17, 188(s0)
	fmax.s f25, f22, f17
	fsw f25, 108(s0)
	fcvt.w.s x7, f22, rtz
	add x28, x22, x28
	sub x7, x16, x24
	lb x16, 2(s0)
	rem x20, x11, x19
	bltu x24, x31, .Lsk_3
	nop
.Lsk_3:
	sh x17, 50(s0)
	flw f27, 72(s0)
	flw f24, 188(s0)
	fadd.s f13, f27, f24
	fsw f13, 164(s0)
	fcvt.w.s x19, f27, rtz
	flw f14, 80(s0)
	flw f29, 208(s0)
	fmin.s f3, f14, f29
	fsw f3, 148(s0)
	fcvt.w.s x19, f14, rtz
	addi x13, x7, -285
	add  x13, x13, x13
	slli x13, x13, 1
	bgeu x11, x3, .Lsk_4
	nop
.Lsk_4:
	addi x9, x18, -1124
	add  x9, x9, x9
	slli x9, x9, 1
	lhu x21, 106(s0)
	divu x31, x14, x15
	lb x25, 93(s0)
	lb x9, 46(s0)
	lb x15, 38(s0)
	lbu x3, 119(s0)
	lw x20, 144(s0)
	andi x29, x13, -354
	addi x13, x4, -634
	add  x13, x13, x13
	slli x13, x13, 1
	lui x13, 770800
	mulh x31, x30, x18
	sltiu x16, x12, 960
	addi x4, x27, -1899
	add  x4, x4, x4
	slli x4, x4, 1
	addi x21, x16, 497
	add  x21, x21, x21
	slli x21, x21, 1
	addi x9, x19, -1034
	add  x9, x9, x9
	slli x9, x9, 1
	sub x31, x20, x6
	div x30, x30, x16
	addi x23, x20, 988
	add  x23, x23, x23
	slli x23, x23, 1
	lui x25, 244828
	flw f24, 36(s0)
	flw f7, 140(s0)
	fmax.s f2, f24, f7
	fsw f2, 152(s0)
	fcvt.w.s x25, f24, rtz
	sltu x21, x30, x14
	addi x7, x12, 552
	add  x7, x7, x7
	slli x7, x7, 1
	sb x14, 208(s0)
	lhu x27, 132(s0)
	bge x24, x17, .Lsk_5
	nop
.Lsk_5:
	addi x6, x30, 419
	add  x6, x6, x6
	slli x6, x6, 1
	lb x6, 56(s0)
	div x21, x31, x12
	mulh x12, x4, x16
	srl x21, x20, x4
	sra x20, x7, x29
	bne x10, x25, .Lsk_6
	nop
.Lsk_6:
	addi x25, x18, -1183
	add  x25, x25, x25
	slli x25, x25, 1
	bge x31, x26, .Lsk_7
	nop
.Lsk_7:
	sra x9, x19, x11
	addi x9, x25, -1336
	add  x9, x9, x9
	slli x9, x9, 1
	lw x24, 156(s0)
	sb x25, 30(s0)
	add x21, x11, x22
	addi x21, x19, 691
	add  x21, x21, x21
	slli x21, x21, 1
	sh x17, 190(s0)
	addi x5, x6, 305
	add  x5, x5, x5
	slli x5, x5, 1
	addi x12, x15, -106
	add  x12, x12, x12
	slli x12, x12, 1
	lh x29, 248(s0)
	sra x21, x11, x25
	addi x14, x21, -267
	add  x14, x14, x14
	slli x14, x14, 1
	lb x13, 144(s0)
	lh x20, 254(s0)
	auipc x14, 107879
	srli x6, x12, 4
	add x27, x15, x21
	flw f1, 112(s0)
	flw f26, 144(s0)
	fadd.s f20, f1, f26
	fsw f20, 68(s0)
	fcvt.w.s x17, f1, rtz
	sh x31, 148(s0)
	slti x28, x20, 298
	lhu x27, 206(s0)
	bne x31, x7, .Lsk_8
	nop
.Lsk_8:
	addi x9, x21, 1544
	add  x9, x9, x9
	slli x9, x9, 1
	addi x4, x11, -311
	add  x4, x4, x4
	slli x4, x4, 1
	addi x20, x23, -748
	add  x20, x20, x20
	slli x20, x20, 1
	mulh x21, x17, x24
	divu x7, x22, x7
	flw f14, 236(s0)
	flw f0, 228(s0)
	fsub.s f25, f14, f0
	fsw f25, 228(s0)
	fcvt.w.s x4, f14, rtz
	lw x26, 136(s0)
	andi x16, x31, 1811
	blt x27, x4, .Lsk_9
	nop
.Lsk_9:
	mulhu x10, x15, x3
	div x15, x26, x14
	bge x15, x26, .Lsk_10
	nop
.Lsk_10:
	addi x27, x29, -371
	add  x27, x27, x27
	slli x27, x27, 1
	sra x5, x26, x28
	flw f7, 204(s0)
	flw f16, 80(s0)
	fsub.s f3, f7, f16
	fsw f3, 232(s0)
	fcvt.w.s x20, f7, rtz
	flw f1, 248(s0)
	flw f15, 176(s0)
	fmin.s f24, f1, f15
	fsw f24, 160(s0)
	fcvt.w.s x25, f1, rtz
	slti x16, x6, -1253
	lw x4, 112(s0)
	flw f0, 156(s0)
	flw f3, 48(s0)
	fsub.s f21, f0, f3
	fsw f21, 140(s0)
	fcvt.w.s x11, f0, rtz
	and x4, x18, x9
	sh x29, 188(s0)
	lh x27, 2(s0)
	lh x4, 54(s0)
	lh x19, 248(s0)
	remu x17, x18, x23
	addi x23, x7, -1949
	add  x23, x23, x23
	slli x23, x23, 1
	lh x5, 60(s0)
	auipc x9, 124265
	srli x12, x21, 11
	addi x12, x20, -149
	add  x12, x12, x12
	slli x12, x12, 1
	flw f3, 184(s0)
	flw f26, 32(s0)
	fmin.s f22, f3, f26
	fsw f22, 156(s0)
	fcvt.w.s x29, f3, rtz
	sh x9, 46(s0)
	sh x24, 38(s0)
	slt x16, x31, x4
	or x6, x9, x24
	lh x12, 210(s0)
	slt x5, x31, x27
	lhu x22, 122(s0)
	lbu x24, 144(s0)
	sw x3, 144(s0)
	addi x19, x25, -848
	add  x19, x19, x19
	slli x19, x19, 1
	addi x3, x15, 1562
	add  x3, x3, x3
	slli x3, x3, 1
	bgeu x13, x30, .Lsk_11
	nop
.Lsk_11:
	addi x27, x13, -336
	add  x27, x27, x27
	slli x27, x27, 1
	sw x30, 112(s0)
	addi x31, x10, 334
	add  x31, x31, x31
	slli x31, x31, 1
	lw x18, 144(s0)
	lw x31, 60(s0)
	bge x3, x10, .Lsk_12
	nop
.Lsk_12:
	lh x9, 100(s0)
	rem x14, x3, x26
	sll x25, x20, x27
	sll x25, x26, x20
	flw f6, 224(s0)
	flw f12, 120(s0)
	fsub.s f30, f6, f12
	fsw f30, 132(s0)
	fcvt.w.s x26, f6, rtz
	flw f7, 232(s0)
	flw f21, 224(s0)
	fmin.s f11, f7, f21
	fsw f11, 176(s0)
	fcvt.w.s x20, f7, rtz
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
	.word 0xF0105A7E
	.word 0xF9CFAB49
	.word 0x236A9C0B
	.word 0x17F07D7F
	.word 0x1CBFD004
	.word 0x9947023D
	.word 0xBDC21063
	.word 0x6B1B436E
	.word 0x1BC3EF45
	.word 0x76896611
	.word 0xBD1E540C
	.word 0xD481443D
	.word 0x96EAA7AE
	.word 0x9E6DE04F
	.word 0xF640AEEC
	.word 0x0A528696
	.word 0xA677BB4C
	.word 0xA23ACB1D
	.word 0xE0CD2FFA
	.word 0x40A386AB
	.word 0x05FFD686
	.word 0xFA46D150
	.word 0x0884B291
	.word 0x414812B9
	.word 0x0AE2FA0A
	.word 0x5C3064F5
	.word 0xC2365DEE
	.word 0x3D6F04BE
	.word 0x3E779E47
	.word 0xB4A23E07
	.word 0x3474FC83
	.word 0x5C9AE7F1
	.word 0x7EAAE0D1
	.word 0xD30E0034
	.word 0xE3FDBD82
	.word 0x8F9F6672
	.word 0x9787D10C
	.word 0x1C3B891C
	.word 0x17BE7B7D
	.word 0x324CDB1A
	.word 0x41ADCC48
	.word 0xFA0CC12C
	.word 0xFCD8C340
	.word 0x268094B7
	.word 0x2B1CEF21
	.word 0xFB97127C
	.word 0x1CDF4998
	.word 0x02C0636D
	.word 0x588C50CA
	.word 0xC2A15800
	.word 0xC34A284C
	.word 0x25A63B0B
	.word 0x8ACFD8AC
	.word 0xBCF9040B
	.word 0xFCC53976
	.word 0x8720A704
	.word 0xBA8580D3
	.word 0xF44FE016
	.word 0xB89D5D7F
	.word 0x21F014ED
	.word 0xC784EBA0
	.word 0xD225AF99
	.word 0x03B09905
	.word 0x18AD08C5
