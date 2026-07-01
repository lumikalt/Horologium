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
	sra x19, x28, x28
	flw f2, 180(s0)
	flw f3, 240(s0)
	fmin.s f30, f2, f3
	fsw f30, 76(s0)
	fcvt.w.s x5, f2, rtz
	bne x28, x25, .Lsk_0
	nop
.Lsk_0:
	slt x20, x25, x13
	lh x23, 206(s0)
	lbu x26, 122(s0)
	srli x27, x19, 23
	sltu x25, x19, x4
	lb x5, 113(s0)
	lh x17, 172(s0)
	and x28, x7, x14
	bge x26, x13, .Lsk_1
	nop
.Lsk_1:
	lui x12, 508193
	addi x20, x15, 1066
	add  x20, x20, x20
	slli x20, x20, 1
	lbu x3, 20(s0)
	divu x29, x6, x22
	blt x21, x13, .Lsk_2
	nop
.Lsk_2:
	lw x22, 128(s0)
	add x14, x26, x14
	addi x27, x5, 1624
	add  x27, x27, x27
	slli x27, x27, 1
	sll x14, x29, x16
	addi x24, x11, -316
	add  x24, x24, x24
	slli x24, x24, 1
	lbu x20, 28(s0)
	addi x19, x12, 821
	add  x19, x19, x19
	slli x19, x19, 1
	slt x15, x4, x24
	srli x25, x10, 27
	flw f14, 64(s0)
	flw f31, 56(s0)
	fmax.s f30, f14, f31
	fsw f30, 140(s0)
	fcvt.w.s x12, f14, rtz
	flw f9, 176(s0)
	flw f7, 244(s0)
	fadd.s f20, f9, f7
	fsw f20, 248(s0)
	fcvt.w.s x6, f9, rtz
	xori x14, x21, 1428
	lbu x23, 59(s0)
	add x5, x17, x4
	and x16, x23, x30
	lh x15, 252(s0)
	slli x27, x22, 19
	lh x24, 126(s0)
	addi x30, x11, 821
	add  x30, x30, x30
	slli x30, x30, 1
	divu x19, x7, x29
	beq x3, x27, .Lsk_3
	nop
.Lsk_3:
	lui x22, 921037
	sltu x23, x26, x4
	addi x22, x7, -1441
	add  x22, x22, x22
	slli x22, x22, 1
	lhu x19, 210(s0)
	sra x4, x29, x21
	sh x26, 236(s0)
	addi x11, x30, 1989
	add  x11, x11, x11
	slli x11, x11, 1
	flw f26, 188(s0)
	flw f16, 240(s0)
	fmin.s f13, f26, f16
	fsw f13, 52(s0)
	fcvt.w.s x25, f26, rtz
	flw f26, 72(s0)
	flw f5, 36(s0)
	fsub.s f19, f26, f5
	fsw f19, 8(s0)
	fcvt.w.s x3, f26, rtz
	sll x10, x6, x20
	lbu x14, 75(s0)
	lw x28, 48(s0)
	sb x7, 89(s0)
	sh x11, 10(s0)
	flw f3, 196(s0)
	flw f4, 172(s0)
	fmax.s f10, f3, f4
	fsw f10, 244(s0)
	fcvt.w.s x28, f3, rtz
	flw f27, 216(s0)
	flw f20, 184(s0)
	fadd.s f9, f27, f20
	fsw f9, 28(s0)
	fcvt.w.s x6, f27, rtz
	addi x15, x29, -643
	add  x15, x15, x15
	slli x15, x15, 1
	lb x15, 74(s0)
	lw x24, 248(s0)
	sra x14, x13, x25
	auipc x11, 384605
	lh x31, 52(s0)
	lbu x24, 225(s0)
	bltu x19, x28, .Lsk_4
	nop
.Lsk_4:
	div x4, x4, x26
	lh x5, 176(s0)
	sw x10, 240(s0)
	lb x14, 20(s0)
	lh x11, 58(s0)
	lb x21, 250(s0)
	lui x23, 150503
	sh x3, 148(s0)
	bgeu x21, x6, .Lsk_5
	nop
.Lsk_5:
	addi x25, x7, 1733
	add  x25, x25, x25
	slli x25, x25, 1
	srli x30, x23, 27
	slli x20, x3, 24
	addi x17, x16, 1649
	add  x17, x17, x17
	slli x17, x17, 1
	flw f4, 120(s0)
	flw f23, 92(s0)
	fmax.s f28, f4, f23
	fsw f28, 20(s0)
	fcvt.w.s x27, f4, rtz
	slt x13, x25, x14
	divu x23, x30, x18
	addi x28, x15, 871
	add  x28, x28, x28
	slli x28, x28, 1
	sw x29, 116(s0)
	sw x6, 72(s0)
	addi x3, x28, 1830
	add  x3, x3, x3
	slli x3, x3, 1
	lbu x28, 124(s0)
	sw x18, 16(s0)
	sb x15, 202(s0)
	addi x11, x25, -917
	add  x11, x11, x11
	slli x11, x11, 1
	addi x23, x25, -169
	add  x23, x23, x23
	slli x23, x23, 1
	lb x5, 139(s0)
	sb x20, 116(s0)
	flw f26, 24(s0)
	flw f22, 40(s0)
	fmax.s f29, f26, f22
	fsw f29, 0(s0)
	fcvt.w.s x21, f26, rtz
	flw f31, 96(s0)
	flw f10, 76(s0)
	fadd.s f30, f31, f10
	fsw f30, 144(s0)
	fcvt.w.s x29, f31, rtz
	bltu x16, x16, .Lsk_6
	nop
.Lsk_6:
	addi x11, x28, -182
	add  x11, x11, x11
	slli x11, x11, 1
	addi x4, x28, 120
	add  x4, x4, x4
	slli x4, x4, 1
	sra x3, x11, x19
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
	sb x27, 155(s0)
	mulhu x18, x19, x22
	lw x6, 16(s0)
	lbu x9, 180(s0)
	srli x16, x22, 4
	addi x11, x12, 97
	add  x11, x11, x11
	slli x11, x11, 1
	addi x15, x19, -706
	add  x15, x15, x15
	slli x15, x15, 1
	flw f0, 72(s0)
	flw f24, 240(s0)
	fmul.s f11, f0, f24
	fsw f11, 236(s0)
	fcvt.w.s x13, f0, rtz
	bltu x10, x9, .Lsk_9
	nop
.Lsk_9:
	lh x12, 250(s0)
	addi x30, x30, -304
	sb x17, 201(s0)
	mulh x11, x16, x4
	sb x13, 74(s0)
	slt x13, x12, x16
	bltu x14, x3, .Lsk_10
	nop
.Lsk_10:
	sltiu x26, x25, 1403
	lb x14, 92(s0)
	mulh x24, x19, x16
	flw f10, 52(s0)
	flw f4, 180(s0)
	fmul.s f21, f10, f4
	fsw f21, 12(s0)
	fcvt.w.s x25, f10, rtz
	bge x31, x27, .Lsk_11
	nop
.Lsk_11:
	sh x3, 44(s0)
	flw f10, 212(s0)
	flw f7, 124(s0)
	fmin.s f21, f10, f7
	fsw f21, 64(s0)
	fcvt.w.s x20, f10, rtz
	lw x18, 100(s0)
	lui x21, 253811
	add x26, x6, x16
	addi x24, x26, 138
	add  x24, x24, x24
	slli x24, x24, 1
	lb x10, 214(s0)
	lui x9, 30787
	flw f19, 64(s0)
	flw f4, 60(s0)
	fsub.s f19, f19, f4
	fsw f19, 184(s0)
	fcvt.w.s x28, f19, rtz
	ori x10, x12, -757
	bltu x14, x10, .Lsk_12
	nop
.Lsk_12:
	addi x7, x23, -393
	add  x7, x7, x7
	slli x7, x7, 1
	sw x23, 116(s0)
	sh x18, 240(s0)
	srli x28, x13, 31
	div x23, x28, x15
	addi x18, x26, 504
	add  x18, x18, x18
	slli x18, x18, 1
	addi x9, x22, -1255
	add  x9, x9, x9
	slli x9, x9, 1
	lh x28, 252(s0)
	lb x7, 222(s0)
	lbu x15, 158(s0)
	bge x25, x9, .Lsk_13
	nop
.Lsk_13:
	bgeu x30, x6, .Lsk_14
	nop
.Lsk_14:
	lui x27, 1034948
	sub x30, x11, x18
	sh x13, 102(s0)
	bge x3, x5, .Lsk_15
	nop
.Lsk_15:
	sh x20, 164(s0)
	rem x5, x22, x6
	lw x5, 56(s0)
	sub x13, x19, x19
	sra x10, x15, x19
	addi x11, x18, 425
	add  x11, x11, x11
	slli x11, x11, 1
	bgeu x29, x23, .Lsk_16
	nop
.Lsk_16:
	flw f0, 116(s0)
	flw f25, 0(s0)
	fsub.s f3, f0, f25
	fsw f3, 92(s0)
	fcvt.w.s x25, f0, rtz
	lw x17, 184(s0)
	lbu x9, 167(s0)
	sb x23, 219(s0)
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
	.word 0x2201A441
	.word 0x4A376AFA
	.word 0x4F0DA178
	.word 0x89D6C00B
	.word 0x458AA385
	.word 0x75380FA8
	.word 0xBE3AD1E1
	.word 0xA2B5F167
	.word 0x70F12A59
	.word 0x07BA2657
	.word 0x872511E3
	.word 0xF20BD023
	.word 0x9B9FC8D1
	.word 0xD52BA62A
	.word 0x9B441768
	.word 0x9D69C87C
	.word 0x3411115B
	.word 0x111F22EC
	.word 0x04041E30
	.word 0xEDD8A640
	.word 0x7BA3A917
	.word 0x4D42E3E4
	.word 0x5B4AC0B3
	.word 0xD5F3CC5B
	.word 0x5C9E83F4
	.word 0x192F6F9E
	.word 0xB4453E54
	.word 0x42CCFEA2
	.word 0x05E85915
	.word 0x45F2706F
	.word 0x4F93564F
	.word 0x30EA2752
	.word 0x0E869C94
	.word 0xDC54004B
	.word 0x5214FD76
	.word 0xB388DBB7
	.word 0x35C72CB7
	.word 0xBB497AD2
	.word 0xA6D209AC
	.word 0xE34B44FC
	.word 0x8FEA541E
	.word 0xE71F53A9
	.word 0x5AB5F60B
	.word 0x96AFAE35
	.word 0x067A8C8E
	.word 0xE879DFF8
	.word 0x3D3956C8
	.word 0xF227F14E
	.word 0x503E4E0B
	.word 0xA1AE54B3
	.word 0x3E07A7DB
	.word 0x86AF4F78
	.word 0xD7D5C826
	.word 0x49A06A79
	.word 0x20A9ABCE
	.word 0xB14F108B
	.word 0x2DDEF41F
	.word 0xFF4ADDDF
	.word 0xF360EAB0
	.word 0x5DAE5E37
	.word 0x5F2BAAED
	.word 0xE4A62215
	.word 0x4EBF563C
	.word 0xFB93F311
