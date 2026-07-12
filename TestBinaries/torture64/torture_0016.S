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
	fld f2, 104(s0)
	fld f16, 88(s0)
	fmax.d f22, f2, f16
	fsd f22, 40(s0)
	fcvt.l.d x27, f2, rtz
	srli x24, x20, 12
	sd x28, 40(s0)
	flw f22, 60(s0)
	flw f0, 136(s0)
	fmax.s f5, f22, f0
	fsw f5, 216(s0)
	fcvt.w.s x14, f22, rtz
	lw x20, 180(s0)
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
	rem x11, x27, x28
	lhu x22, 80(s0)
	srliw x16, x12, 24
	ld x27, 72(s0)
	addiw x24, x22, -598
	sw x6, 40(s0)
	beq x19, x6, .Lsk_1
	nop
.Lsk_1:
	sraw x7, x16, x22
	lw x10, 0(s0)
	lbu x26, 147(s0)
	sd x27, 192(s0)
	sh x19, 148(s0)
	fld f26, 192(s0)
	fld f8, 192(s0)
	fmax.d f4, f26, f8
	fsd f4, 0(s0)
	fcvt.l.d x14, f26, rtz
	lhu x18, 94(s0)
	addi x22, x5, -337
	add  x22, x22, x22
	slli x22, x22, 1
	ori x10, x6, -1504
	srlw x23, x18, x14
	addi x4, x15, -287
	add  x4, x4, x4
	slli x4, x4, 1
	addiw x10, x17, -1131
	addi x17, x23, 1960
	add  x17, x17, x17
	slli x17, x17, 1
	lbu x13, 161(s0)
	lbu x3, 30(s0)
	slti x18, x5, 618
	sb x27, 139(s0)
	addi x7, x24, -510
	add  x7, x7, x7
	slli x7, x7, 1
	ori x22, x12, -1388
	bne x31, x7, .Lsk_2
	nop
.Lsk_2:
	lb x5, 198(s0)
	or x16, x3, x11
	lhu x19, 222(s0)
	sh x16, 210(s0)
	flw f5, 72(s0)
	flw f27, 188(s0)
	fadd.s f24, f5, f27
	fsw f24, 164(s0)
	fcvt.w.s x10, f5, rtz
	flw f14, 148(s0)
	flw f29, 208(s0)
	fmul.s f3, f14, f29
	fsw f3, 212(s0)
	fcvt.w.s x19, f14, rtz
	andi x10, x24, -149
	mulw x17, x9, x18
	xor x21, x10, x14
	sb x15, 238(s0)
	srliw x30, x13, 18
	mulh x18, x10, x17
	rem x22, x3, x5
	lbu x13, 5(s0)
	mulhu x13, x26, x13
	sltiu x9, x9, -374
	addiw x9, x14, 587
	srli x18, x12, 7
	addi x7, x4, 1262
	divw x24, x9, x24
	addi x3, x26, -1512
	add  x3, x3, x3
	slli x3, x3, 1
	sltiu x5, x30, 1140
	addi x23, x20, 988
	add  x23, x23, x23
	slli x23, x23, 1
	addiw x25, x11, -164
	lui x29, 82084
	addi x5, x12, 418
	add  x5, x5, x5
	slli x5, x5, 1
	sb x21, 16(s0)
	mulhsu x24, x13, x7
	div x27, x27, x20
	sllw x19, x15, x27
	addi x24, x17, 949
	add  x24, x24, x24
	slli x24, x24, 1
	sltu x13, x17, x6
	addiw x10, x21, 490
	addi x15, x15, 231
	add  x15, x15, x15
	slli x15, x15, 1
	lui x25, 683328
	sb x23, 199(s0)
	sltu x15, x20, x18
	flw f14, 52(s0)
	flw f12, 72(s0)
	fsub.s f11, f14, f12
	fsw f11, 224(s0)
	fcvt.w.s x25, f14, rtz
	addi x19, x11, 2008
	add  x19, x19, x19
	slli x19, x19, 1
	lbu x9, 44(s0)
	lw x24, 156(s0)
	sw x6, 112(s0)
	or x26, x24, x19
	sllw x16, x30, x21
	mulhu x13, x9, x19
	sraw x29, x19, x11
	auipc x18, 456067
	ld x12, 152(s0)
	remuw x4, x11, x19
	auipc x14, 107879
	srli x6, x12, 8
	add x27, x15, x21
	flw f1, 68(s0)
	flw f26, 28(s0)
	fmul.s f20, f1, f26
	fsw f20, 192(s0)
	fcvt.w.s x17, f1, rtz
	slliw x28, x20, 18
	ld x16, 200(s0)
	bne x31, x7, .Lsk_3
	nop
.Lsk_3:
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
	divuw x7, x22, x7
	fld f14, 232(s0)
	fld f0, 224(s0)
	fsub.d f25, f14, f0
	fsd f25, 224(s0)
	fcvt.l.d x4, f14, rtz
	lw x26, 136(s0)
	sraiw x16, x31, 30
	blt x27, x4, .Lsk_4
	nop
.Lsk_4:
	mulhu x10, x15, x3
	div x15, x26, x14
	bge x15, x26, .Lsk_5
	nop
.Lsk_5:
	addi x27, x29, -371
	add  x27, x27, x27
	slli x27, x27, 1
	remu x5, x26, x28
	flw f7, 80(s0)
	flw f16, 232(s0)
	fmin.s f3, f7, f16
	fsw f3, 76(s0)
	fcvt.w.s x20, f7, rtz
	flw f1, 176(s0)
	flw f15, 160(s0)
	fadd.s f24, f1, f15
	fsw f24, 196(s0)
	fcvt.w.s x25, f1, rtz
	addw x6, x25, x16
	flw f2, 124(s0)
	flw f14, 156(s0)
	fmul.s f0, f2, f14
	fsw f0, 48(s0)
	fcvt.w.s x4, f2, rtz
	blt x25, x4, .Lsk_6
	nop
.Lsk_6:
	lbu x9, 174(s0)
	flw f23, 68(s0)
	flw f31, 16(s0)
	fmin.s f14, f23, f31
	fsw f14, 52(s0)
	fcvt.w.s x27, f23, rtz
	remw x10, x19, x17
	lwu x22, 252(s0)
	sh x3, 208(s0)
	lh x5, 60(s0)
	addiw x9, x12, 1786
	sw x16, 140(s0)
	sd x9, 168(s0)
	addi x20, x11, 1365
	srl x29, x15, x23
	bne x5, x22, .Lsk_7
	nop
.Lsk_7:
	flw f4, 96(s0)
	flw f2, 24(s0)
	fmax.s f24, f4, f2
	fsw f24, 100(s0)
	fcvt.w.s x31, f4, rtz
	sraiw x9, x3, 31
	srliw x10, x12, 13
	addi x31, x12, 1584
	add  x31, x31, x31
	slli x31, x31, 1
	srli x18, x16, 36
	sw x3, 144(s0)
	addi x19, x25, -848
	add  x19, x19, x19
	slli x19, x19, 1
	addi x3, x15, 1562
	add  x3, x3, x3
	slli x3, x3, 1
	bgeu x13, x30, .Lsk_8
	nop
.Lsk_8:
	addi x27, x13, -336
	add  x27, x27, x27
	slli x27, x27, 1
	sh x30, 114(s0)
	addi x31, x10, 334
	add  x31, x31, x31
	slli x31, x31, 1
	ld x6, 224(s0)
	bltu x16, x6, .Lsk_9
	nop
.Lsk_9:
	addi x24, x6, 1571
	add  x24, x24, x24
	slli x24, x24, 1
	lh x19, 224(s0)
	lui x10, 868004
	lwu x3, 68(s0)
	bne x27, x10, .Lsk_10
	nop
.Lsk_10:
	lwu x6, 28(s0)
	andi x28, x29, -453
	bltu x19, x26, .Lsk_11
	nop
.Lsk_11:
	sd x11, 128(s0)
	srai x7, x23, 42
	sllw x22, x18, x18
	addi x16, x26, -1916
	add  x16, x16, x16
	slli x16, x16, 1
	addi x7, x15, -1282
	add  x7, x7, x7
	slli x7, x7, 1
	slti x6, x17, 1192
	lw x9, 80(s0)
	bge x27, x17, .Lsk_12
	nop
.Lsk_12:
	sb x22, 245(s0)
	addw x23, x10, x28
	addi x4, x5, 1332
	add  x4, x4, x4
	slli x4, x4, 1
	addiw x24, x15, 20
	ld x3, 176(s0)
	addi x4, x7, 41
	add  x4, x4, x4
	slli x4, x4, 1
	slt x23, x4, x27
	sb x12, 165(s0)
	mulhu x19, x26, x17
	bltu x21, x10, .Lsk_13
	nop
.Lsk_13:
	lbu x15, 30(s0)
	ld x7, 64(s0)
	beq x21, x11, .Lsk_14
	nop
.Lsk_14:
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
	.word 0xF90406B3
	.word 0xD6A57805
	.word 0xE54E6F1D
	.word 0x52B9F043
	.word 0xF287F532
	.word 0x6288249D
	.word 0x9D166DA1
	.word 0xD0CDDFF4
	.word 0x26718B6B
	.word 0xA4A81E80
	.word 0x08891822
	.word 0xFC765908
	.word 0x6544DB92
	.word 0xE3929F3E
	.word 0xC7184087
	.word 0xEF4D089E
	.word 0x868004D3
	.word 0x39194D74
	.word 0x8B3167C3
	.word 0x0432117B
	.word 0x002CD854
	.word 0x8703DF31
	.word 0xEE953B4D
	.word 0xFBE7090F
	.word 0x48393C1F
	.word 0x7D890F06
	.word 0x26D2487E
	.word 0xF937B348
	.word 0x0350B762
	.word 0x76A5B826
	.word 0x5B40A288
	.word 0x8A867E54
	.word 0xC532E4CB
	.word 0xCEAF323E
	.word 0x6DE9AE23
	.word 0x1E042326
