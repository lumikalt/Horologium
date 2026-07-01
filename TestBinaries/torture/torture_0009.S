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
	auipc x19, 152907
	or x24, x29, x12
	auipc x17, 118851
	auipc x17, 437353
	flw f22, 32(s0)
	flw f26, 92(s0)
	fmin.s f12, f22, f26
	fsw f12, 28(s0)
	fcvt.w.s x9, f22, rtz
	auipc x22, 57242
	xor x20, x12, x12
	addi x19, x27, 2044
	add  x19, x19, x19
	slli x19, x19, 1
	flw f31, 220(s0)
	flw f23, 244(s0)
	fmul.s f27, f31, f23
	fsw f27, 76(s0)
	fcvt.w.s x19, f31, rtz
	lh x20, 152(s0)
	addi x15, x23, -860
	add  x15, x15, x15
	slli x15, x15, 1
	sh x16, 208(s0)
	flw f8, 168(s0)
	flw f28, 60(s0)
	fmin.s f30, f8, f28
	fsw f30, 176(s0)
	fcvt.w.s x26, f8, rtz
	mulhsu x13, x20, x14
	addi x27, x20, -1545
	lhu x3, 88(s0)
	auipc x18, 380532
	slli x19, x6, 30
	beq x20, x31, .Lsk_0
	nop
.Lsk_0:
	addi x17, x16, 704
	add  x17, x17, x17
	slli x17, x17, 1
	addi x28, x29, 1278
	add  x28, x28, x28
	slli x28, x28, 1
	slli x13, x20, 13
	lui x12, 973517
	mul x29, x7, x31
	beq x26, x14, .Lsk_1
	nop
.Lsk_1:
	sh x10, 8(s0)
	flw f29, 28(s0)
	flw f7, 4(s0)
	fmin.s f26, f29, f7
	fsw f26, 252(s0)
	fcvt.w.s x17, f29, rtz
	beq x14, x14, .Lsk_2
	nop
.Lsk_2:
	addi x28, x16, 1351
	add  x28, x28, x28
	slli x28, x28, 1
	flw f28, 20(s0)
	flw f25, 148(s0)
	fmin.s f31, f28, f25
	fsw f31, 208(s0)
	fcvt.w.s x6, f28, rtz
	sh x25, 68(s0)
	addi x7, x13, 469
	add  x7, x7, x7
	slli x7, x7, 1
	srli x7, x22, 5
	lb x19, 25(s0)
	div x14, x22, x29
	auipc x10, 214390
	beq x5, x7, .Lsk_3
	nop
.Lsk_3:
	srai x15, x12, 8
	addi x9, x17, 1813
	flw f26, 16(s0)
	flw f6, 4(s0)
	fsub.s f27, f26, f6
	fsw f27, 132(s0)
	fcvt.w.s x12, f26, rtz
	flw f24, 36(s0)
	flw f23, 164(s0)
	fadd.s f19, f24, f23
	fsw f19, 120(s0)
	fcvt.w.s x19, f24, rtz
	lh x18, 56(s0)
	flw f6, 236(s0)
	flw f27, 144(s0)
	fmul.s f24, f6, f27
	fsw f24, 72(s0)
	fcvt.w.s x25, f6, rtz
	lbu x17, 142(s0)
	addi x16, x22, 953
	add  x16, x16, x16
	slli x16, x16, 1
	auipc x30, 203811
	auipc x11, 691584
	bltu x28, x23, .Lsk_4
	nop
.Lsk_4:
	sh x19, 74(s0)
	addi x5, x16, -541
	add  x5, x5, x5
	slli x5, x5, 1
	bge x23, x28, .Lsk_5
	nop
.Lsk_5:
	lw x11, 64(s0)
	bne x30, x3, .Lsk_6
	nop
.Lsk_6:
	sra x22, x3, x4
	blt x21, x16, .Lsk_7
	nop
.Lsk_7:
	and x29, x29, x3
	bgeu x28, x27, .Lsk_8
	nop
.Lsk_8:
	flw f29, 4(s0)
	flw f27, 88(s0)
	fadd.s f4, f29, f27
	fsw f4, 84(s0)
	fcvt.w.s x9, f29, rtz
	addi x19, x6, 1671
	add  x19, x19, x19
	slli x19, x19, 1
	srl x4, x11, x25
	beq x23, x29, .Lsk_9
	nop
.Lsk_9:
	sh x9, 224(s0)
	addi x10, x6, 408
	add  x10, x10, x10
	slli x10, x10, 1
	rem x28, x12, x18
	lw x15, 36(s0)
	flw f8, 28(s0)
	flw f21, 164(s0)
	fmin.s f9, f8, f21
	fsw f9, 60(s0)
	fcvt.w.s x6, f8, rtz
	flw f22, 28(s0)
	flw f20, 224(s0)
	fadd.s f4, f22, f20
	fsw f4, 84(s0)
	fcvt.w.s x16, f22, rtz
	addi x18, x15, -225
	add  x18, x18, x18
	slli x18, x18, 1
	mulhu x21, x22, x13
	beq x14, x5, .Lsk_10
	nop
.Lsk_10:
	lhu x20, 90(s0)
	lhu x3, 140(s0)
	flw f9, 124(s0)
	flw f19, 40(s0)
	fadd.s f8, f9, f19
	fsw f8, 92(s0)
	fcvt.w.s x17, f9, rtz
	sltiu x13, x4, -1816
	lhu x18, 70(s0)
	flw f5, 8(s0)
	flw f21, 228(s0)
	fsub.s f9, f5, f21
	fsw f9, 108(s0)
	fcvt.w.s x12, f5, rtz
	addi x30, x24, 1550
	add  x30, x30, x30
	slli x30, x30, 1
	sw x19, 120(s0)
	addi x26, x25, 1000
	flw f19, 236(s0)
	flw f10, 224(s0)
	fmul.s f8, f19, f10
	fsw f8, 240(s0)
	fcvt.w.s x31, f19, rtz
	auipc x9, 185103
	lui x10, 177907
	lb x6, 121(s0)
	sw x12, 64(s0)
	lui x23, 141008
	lh x18, 86(s0)
	sw x18, 188(s0)
	lhu x7, 176(s0)
	addi x30, x16, -921
	add  x30, x30, x30
	slli x30, x30, 1
	addi x14, x6, 186
	add  x14, x14, x14
	slli x14, x14, 1
	sh x6, 98(s0)
	flw f25, 124(s0)
	flw f12, 156(s0)
	fmin.s f29, f25, f12
	fsw f29, 0(s0)
	fcvt.w.s x12, f25, rtz
	sub x15, x29, x31
	lw x23, 224(s0)
	sltu x9, x24, x28
	srli x10, x18, 23
	lui x27, 180747
	lw x6, 248(s0)
	sub x11, x23, x16
	lh x9, 74(s0)
	srl x14, x24, x12
	lh x17, 70(s0)
	addi x19, x17, -407
	add  x19, x19, x19
	slli x19, x19, 1
	andi x29, x20, 766
	bltu x10, x16, .Lsk_11
	nop
.Lsk_11:
	sra x22, x15, x21
	mulhu x25, x14, x12
	beq x16, x14, .Lsk_12
	nop
.Lsk_12:
	addi x19, x7, 446
	add  x19, x19, x19
	slli x19, x19, 1
	srl x28, x28, x14
	bltu x24, x22, .Lsk_13
	nop
.Lsk_13:
	sh x17, 228(s0)
	flw f27, 108(s0)
	flw f0, 164(s0)
	fmax.s f23, f27, f0
	fsw f23, 56(s0)
	fcvt.w.s x25, f27, rtz
	auipc x29, 359836
	srai x5, x11, 28
	sra x16, x12, x5
	addi x11, x11, 1470
	add  x11, x11, x11
	slli x11, x11, 1
	lhu x18, 66(s0)
	blt x4, x17, .Lsk_14
	nop
.Lsk_14:
	bne x13, x20, .Lsk_15
	nop
.Lsk_15:
	addi x7, x9, 1574
	add  x7, x7, x7
	slli x7, x7, 1
	flw f10, 128(s0)
	flw f0, 4(s0)
	fadd.s f4, f10, f0
	fsw f4, 204(s0)
	fcvt.w.s x27, f10, rtz
	sh x3, 20(s0)
	sb x25, 82(s0)
	bne x25, x11, .Lsk_16
	nop
.Lsk_16:
	or x17, x18, x11
	mulhsu x27, x23, x15
	sw x10, 248(s0)
	blt x31, x26, .Lsk_17
	nop
.Lsk_17:
	slti x22, x24, 1612
	bgeu x19, x10, .Lsk_18
	nop
.Lsk_18:
	or x28, x13, x15
	addi x9, x14, 12
	add  x9, x9, x9
	slli x9, x9, 1
	div x31, x30, x4
	addi x26, x10, -1367
	add  x26, x26, x26
	slli x26, x26, 1
	srai x12, x21, 21
	sw x25, 76(s0)
	lh x26, 100(s0)
	addi x9, x10, -726
	add  x9, x9, x9
	slli x9, x9, 1
	divu x31, x5, x10
	addi x27, x5, -1445
	add  x27, x27, x27
	slli x27, x27, 1
	auipc x27, 621147
	flw f12, 100(s0)
	flw f8, 152(s0)
	fmin.s f25, f12, f8
	fsw f25, 136(s0)
	fcvt.w.s x25, f12, rtz
	sltu x23, x3, x6
	rem x12, x3, x10
	srli x16, x25, 14
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
	.word 0xE5C5854C
	.word 0x4933E486
	.word 0xCDA28BD9
	.word 0xE3511945
	.word 0x10719285
	.word 0x9BE19270
	.word 0x65C5A72A
	.word 0x3168DE08
	.word 0x86045909
	.word 0x21CDD80A
	.word 0x2A18D7DA
	.word 0x78C15A7A
	.word 0x4CB10C37
	.word 0xA778B268
	.word 0x65C0C328
	.word 0xAB82E7B4
	.word 0xF6D1F38E
	.word 0x49C20842
	.word 0xC6894AF8
	.word 0xD0310327
	.word 0x37FB96C3
	.word 0x79DC535E
	.word 0x9A9CFD17
	.word 0x5D4F7805
	.word 0x5349E1CB
	.word 0x45904CEE
	.word 0xB5EED482
	.word 0xC043B556
	.word 0x5B48E0FA
	.word 0x43D4809C
	.word 0x1CE6F3BB
	.word 0x14BA38C5
	.word 0xD448D114
	.word 0x07515726
	.word 0xC8B1DB73
	.word 0x9485CFAB
	.word 0x660F080B
	.word 0x313B759F
	.word 0x88F1D0ED
	.word 0x37990392
	.word 0x86AE9902
	.word 0xF1A1E966
	.word 0xE625C3B1
	.word 0x5DE5520B
	.word 0xF7FC8592
	.word 0x1C12E721
	.word 0x33057CCC
	.word 0xE1DE373F
	.word 0x1DE708E6
	.word 0xC7DE51C0
	.word 0x93491FCF
	.word 0xDB413101
	.word 0x0D9D306E
	.word 0xB1B275DA
	.word 0x79255EED
	.word 0xE8EB05E4
	.word 0x4E98F325
	.word 0xD1D673C6
	.word 0x21DC6FD3
	.word 0x669B6FE8
	.word 0x660F779E
	.word 0xFA317092
	.word 0xCCBB3BD6
	.word 0x9078F826
