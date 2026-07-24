#region

using Mechanism;

#endregion

namespace RiscV32.Decode;

public partial class Rv32Decoder {
    // ── UVE extension ─────────────────────────────────────────────────────────
    // custom-0 (0x0B): stream setup (ss.*)
    // custom-1 (0x2B): stream operations (so.*)

    private static RvInstruction DecodeUveSetup(ulong pc, uint raw) {
        // R4-type: rs3[31:27] | funct2[26:25] | rs2[24:20] | rs1[19:15] | funct3[14:12] | rd[11:7] | 0x0B
        var ud = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        var rs2 = (int)((raw >> 20) & 0x1F);
        var rs3 = (int)((raw >> 27) & 0x1F);
        uint funct2 = (raw >> 25) & 0x3;
        uint funct3 = (raw >> 12) & 0x7;

        switch (funct2) {
            case 0: {
                // ss.sta.{ld|st}.* stream header (UVE2): funct3[2]=1→load,0→store; ew=1<<(funct3&3)
                //   pm   [31] = 1 → merging predication (ss.sta.m; default is zeroing)
                //   vec  [30] = 1 → vectorial stream; vdim [29:27] = vector-coupled dim
                //                   (7 → innermost = -1; 0..6 explicit, outermost-first, remapped at ss.end)
                //   inds [24] = 1 + isLoad → IndSource stream (ss.sta.ld.*_inds)
                //   mem  [23:22] = cache-level routing (ss.sta.mem[l]; 0 = default)
                int ew = UveElementBytes(funct3);
                bool isLoad = funct3 >> 2 != 0;
                bool merging = (rs3 & 0x10) != 0;
                var memLevel = (int)((raw >> 22) & 0x3);
                if (isLoad && (rs2 & 0x10) != 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSsStaLdWInds(ud, rs1, ew, memLevel)
                    );
                bool isVec = (rs3 & 0x8) != 0;
                int vecCfgDim = isVec ? (rs3 & 0x7) == 0x7 ? -1 : rs3 & 0x7 : -1;
                return isLoad
                    ? new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Uve,
                        new RvUveSsStaLdW(ud, rs1, ew, isVec, vecCfgDim, merging, memLevel)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Uve,
                        new RvUveSsStaStW(ud, rs1, ew, isVec, vecCfgDim, merging, memLevel)
                    );
            }
            // ss.app ud, rs1_offset, rs2_count, rs3_stride
            case 1 when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2, rs3,], ToothClass.Uve, new RvUveSsApp(ud, rs1, rs2, rs3)
                );
            // ss.app.mod — static modifier (UVE2): funct2=1(APP), funct3=4(MOD).
            // b[24:22]=behavior, ta[21:20]=target (0=Size,1=Stride,2=Offset), tdim[17:15]=target dim
            // (outermost-first, Spike order; 7 = "linked"), rs3=displacement register.
            // The trigger dimension is positional and resolved at execute time.
            case 1 when funct3 == 4: {
                var tdim = (int)((raw >> 15) & 0x7);
                return new RvInstruction(
                    pc, raw, -1, [rs3,], ToothClass.Uve,
                    new RvUveSsAppMod(ud, tdim, UveModTarget(rs2 & 0x3), UveModBehavior((rs2 >> 2) & 0x7), rs3)
                );
            }
            // ss.app.ind ud, rs1_indsrc — indirect (dynamic) modifier; funct2=1, funct3=6, bit27=0.
            // ss.app.sgi ud, rs1_indsrc — scatter-gather modifier; funct2=1, funct3=6, bit27=1.
            // tdim[30:28] (ind only); b = (rs2>>2)&7; ta[1:0] (ind only);
            // rs1 = UVE register number of the IndSource stream (not an integer register read).
            case 1 when funct3 == 6: {
                if ((raw & (1u << 27)) != 0)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Uve,
                        new RvUveSsAppSgi(ud, rs1, UveModBehavior((rs2 >> 2) & 0x7))
                    );
                var tdim = (int)((raw >> 28) & 0x7);
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Uve,
                    new RvUveSsAppInd(ud, tdim, UveModTarget(rs2 & 0x3), UveModBehavior((rs2 >> 2) & 0x7), rs1)
                );
            }
            // ss.end.sgi ud, rs1_indsrc — scatter-gather modifier + activate; funct2=2, funct3=6.
            // rs1 = source stream ID; behavior = (rs2_literal >> 2) & 7; no new dimension added.
            case 2 when funct3 == 6:
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Uve,
                    new RvUveSsEndSgi(ud, rs1, UveModBehavior((rs2 >> 2) & 0x7))
                );
            // ss.end ud, rs1_offset, rs2_count, rs3_stride
            case 2 when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2, rs3,], ToothClass.Uve, new RvUveSsEnd(ud, rs1, rs2, rs3)
                );
            default:
                throw new IllegalInstructionException(
                    raw, $"Unknown UVE setup funct2=0x{funct2:X} funct3=0x{funct3:X}"
                );
        }
    }

    // funct3 encodes element width for ss.sta.*: ew = 1 << (funct3 & 3) → 1/2/4/8 bytes
    private static int UveElementBytes(uint funct3) => 1 << (int)(funct3 & 3);

    // Modifier ta field: 0=Size, 1=Stride, 2=Offset (Spike Table 2.4)
    private static StreamModifierTarget UveModTarget(int ta) => ta switch {
        0 => StreamModifierTarget.Size,
        1 => StreamModifierTarget.Stride,
        _ => StreamModifierTarget.Offset,
    };

    // Modifier b field: 0=Inc, 1=Dec, 2=Add, 3=Sub, 4+=Set (Spike Table 2.3)
    private static StreamModifierBehavior UveModBehavior(int b) => b switch {
        0 => StreamModifierBehavior.Inc,
        1 => StreamModifierBehavior.Dec,
        2 => StreamModifierBehavior.Add,
        3 => StreamModifierBehavior.Sub,
        _ => StreamModifierBehavior.Set,
    };

    private static RvInstruction DecodeUveOp(ulong pc, uint raw) {
        var rd = (int)((raw >> 7) & 0x1F);
        var rs1 = (int)((raw >> 15) & 0x1F);
        var rs2 = (int)((raw >> 20) & 0x1F);
        uint funct3 = (raw >> 12) & 0x7;
        uint funct7 = (raw >> 25) & 0x7F;

        // UVE branch: bits[31:29]=111 (funct7[6:4]=111, i.e. raw>>29==7)
        // funct3=0 selects the no-suffix EOS-equivalent form (so.b.[n]c); funct3=1..7 select the
        // dimensioned form (so.b.[n]dc.D, D = funct3+1, counting from the outermost) — Appendix B's
        // original listing and Spike's encoding, both confirmed correct by the UVE2 author
        // (2026-07-22 email retracted 2026-07-24; see SPEC_NOTES.md's "Branch `d` field" entry for
        // the full back-and-forth). dc.1 is NOT reachable (there is no funct3 value left for it once
        // funct3=0 is EOS) — so.b.[n]c (checking the outermost/first dimension) already serves that
        // purpose, since the outermost dimension completing is definitionally the same event as the
        // whole stream completing.
        if (raw >> 29 == 7) {
            int imm = UveBranchImm(raw);
            int notDone = rs2 & 1; // LSB of rs2 field

            if (funct3 == 0)
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Uve,
                    notDone != 0 ? new RvUveSoBNc(rs1, imm) : new RvUveSoBc(rs1, imm)
                );

            return new RvInstruction(
                pc, raw, -1, [], ToothClass.Uve,
                notDone != 0
                    ? new RvUveSoBNdc(rs1, (int)funct3, imm)
                    : new RvUveSoBdc(rs1, (int)funct3, imm)
            );
        }

        switch (funct7) {
            // so.v.dp.(width): funct7=0x56; funct3 selects element width (0=b, 1=h, 2=w, 3=d)
            case 0x56: {
                int elemBytes = (int)funct3 switch {
                    0 => 1, 1 => 2, 2 => 4, 3 => 8,
                    _ => throw new IllegalInstructionException(raw, $"Unknown so.v.dp width funct3=0x{funct3:X}"),
                };
                return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSoVDp(rd, rs1, elemBytes));
            }
            // so.v.mv family: funct7=0x54; rs2[4:3] selects op (2=mvvs, 3=mvsv, 0=mv, 1=mvt)
            case 0x54: {
                int mvKind = (rs2 >> 3) & 3;
                switch (mvKind) {
                    // dest=-1 (not rd): unlike every other UVE op (whose destination is a u-register,
                    // never part of integer rename, so -1 is unconditionally correct for them), this op's
                    // destination genuinely is an integer register — normally renamed. Passing rd as a
                    // real DestinationRegister here (as an earlier version of this line did) allocates a
                    // PRF slot that nothing ever writes a value into, so commit-time PRF->architectural
                    // writeback clobbers ExecuteUveSoVMvvs's correct SideEffect write with zero. dest=-1
                    // fixes that, at the cost of a narrower one: the write now bypasses rename entirely,
                    // so it's visible to head-serialized UVE consumers and to post-run architectural
                    // reads, but NOT to a later renamed integer read of rd (see the comment on
                    // ExecuteUveSoVMvvs, and "so.v.mvvs result visibility" in TODO.md).
                    case 2: return new RvInstruction(pc, raw, -1, [], ToothClass.Uve, new RvUveSoVMvvs(rs1, rd));
                    case 3: {
                        int elemBytes = (int)funct3 switch {
                            0 => 1, 1 => 2, 2 => 4, 3 => 8,
                            _ => throw new IllegalInstructionException(
                                raw, $"Unknown so.v.mvsv width funct3=0x{funct3:X}"
                            ),
                        };
                        return new RvInstruction(
                            pc, raw, -1, [rs1,], ToothClass.Uve, new RvUveSoVMvsv(rd, rs1, elemBytes)
                        );
                    }
                    default: {
                        // mv/mvt: rs2[2:0] = uve_v_pred = bits[22:20]
                        int predIdx = rs2 & 7;
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Uve, new RvUveSoVMv(mvKind == 1, rd, rs1, predIdx)
                        );
                    }
                }
            }
        }

        // so.a.*: group = funct7>>3, ps3 = funct7&7 (bits[27:25] = governing predicate register)
        // upper = funct3&4, type = funct3&3 (0=US, 1=FP, 2=SG)
        var group = (int)(funct7 >> 3);
        var ps3 = (int)(funct7 & 7);
        bool upper = (funct3 & 4) != 0;
        var type = (int)(funct3 & 3);

        RvOp uvOp = group switch {
            0 => UveArith(
                upper ? UveFpOp.Sub : UveFpOp.Add, upper ? UveIntOp.Sub : UveIntOp.Add, type, rd, rs1, rs2, ps3
            ),
            1 => UveArith(
                upper ? UveFpOp.Div : UveFpOp.Mul, upper ? UveIntOp.Div : UveIntOp.Mul, type, rd, rs1, rs2, ps3
            ),
            // Group 2 lower: adde — accumulate the stream element into ud; rs2=1 selects the += variant.
            // Group 2 upper: sadde/fsadde — write the stream element (or accumulate) into integer/FP scalar reg.
            2 when !upper && rs2 == 1 => UveArith(UveFpOp.AddeAcc, UveIntOp.AddeAcc, type, rd, rs1, -1, ps3),
            2 when !upper             => UveArith(UveFpOp.Adde, UveIntOp.Adde, type, rd, rs1, -1, ps3),
            2 when upper && rs2 == 1  => new RvUveSoASadde(type == 1, true, type == 1 ? rd + 32 : rd, rs1, ps3),
            2 when upper              => new RvUveSoASadde(type == 1, false, type == 1 ? rd + 32 : rd, rs1, ps3),
            3 when upper              => UveArith(UveFpOp.Mac, UveIntOp.Mac, type, rd, rs1, rs2, ps3),
            // ABS has no US variant in Spike (MATCH_SO_A_ABS_SG uses funct3=0); force Signed=true.
            3 when !upper && type == 1 => new RvUveSoAFp(UveFpOp.Abs, rd, rs1, -1, ps3),
            3 when !upper              => new RvUveSoAInt(UveIntOp.Abs, true, rd, rs1, -1, ps3),
            4 => UveArith(
                upper ? UveFpOp.Max : UveFpOp.Min, upper ? UveIntOp.Max : UveIntOp.Min, type, rd, rs1, rs2, ps3
            ),
            // Group 5: mine/maxe — running min/max reduction into ud.
            5 => UveArith(
                upper ? UveFpOp.Maxe : UveFpOp.Mine, upper ? UveIntOp.Maxe : UveIntOp.Mine, type, rd, rs1, -1, ps3
            ),
            6 when upper && rs2 == 1 && type == 1 => new RvUveSoAFp(UveFpOp.Sqrt, rd, rs1, -1, ps3),
            6 => UveArith(
                upper ? UveFpOp.Dec : UveFpOp.Inc, upper ? UveIntOp.Dec : UveIntOp.Inc, type, rd, rs1, -1, ps3
            ),
            // Group 10 (funct7=0x50..0x57): SO_V_CV — vector element type conversion.
            // funct3 = destWidthIdx (0=b,1=h,2=w,3=d); rs2 = cvType (0=US, 8=FP, 16=SG).
            10 when funct3 <= 3 => new RvUveSoVCv(rd, rs1, 1 << (int)funct3, rs2 == 8, rs2 == 16),
            10                  => throw new IllegalInstructionException(raw, $"Unknown so.v.cv funct3=0x{funct3:X}"),
            // Group 11 (funct7=0x58): SO_C — stream lifecycle and vector-length control.
            // funct3 distinguishes ops; only rd (and rs1 for SETVL) are register fields.
            11 => (int)funct3 switch {
                0 => new RvUveSoCSetvl(rd, rs1),
                1 => new RvUveSoCSuspd(rd),
                2 => new RvUveSoCResum(rd),
                3 => new RvUveSoCBreak(rd),
                7 => new RvUveSoCGetvl(rd),
                _ => throw new IllegalInstructionException(raw, $"Unknown UVE SO_C funct3=0x{funct3:X}"),
            },
            12 => (int)funct3 switch {
                0 => new RvUveSoALogic(UveLogicOp.Nand, rd, rs1, rs2, ps3),
                1 => new RvUveSoALogic(UveLogicOp.And, rd, rs1, rs2, ps3),
                2 => new RvUveSoALogic(UveLogicOp.Nor, rd, rs1, rs2, ps3),
                3 => new RvUveSoALogic(UveLogicOp.Or, rd, rs1, rs2, ps3),
                4 => new RvUveSoALogic(UveLogicOp.Not, rd, rs1, -1, ps3),
                5 => new RvUveSoALogic(UveLogicOp.Xor, rd, rs1, rs2, ps3),
                _ => throw new IllegalInstructionException(raw, $"Unknown UVE logic funct3=0x{funct3:X}"),
            },
            13 => (int)funct3 switch {
                0 => new RvUveSoAShiftV(UveShiftOp.Sll, rd, rs1, rs2, ps3),
                1 => new RvUveSoAShiftS(UveShiftOp.Sll, rd, rs1, rs2, ps3),
                2 => new RvUveSoAShiftV(UveShiftOp.Srl, rd, rs1, rs2, ps3),
                3 => new RvUveSoAShiftS(UveShiftOp.Srl, rd, rs1, rs2, ps3),
                4 => new RvUveSoAShiftV(UveShiftOp.Sra, rd, rs1, rs2, ps3),
                5 => new RvUveSoAShiftS(UveShiftOp.Sra, rd, rs1, rs2, ps3),
                _ => throw new IllegalInstructionException(raw, $"Unknown UVE shift funct3=0x{funct3:X}"),
            },
            // Groups 8/9: SO_P predicate register operations.
            // bits[31:28]=1000 → group=8 (simple ops + GE); bits[31:28]=1001 → group=9 (EQ + LT)
            8 or 9 => DecodeSoP(raw, group, funct3),
            _      => throw new IllegalInstructionException(raw, $"Unknown UVE op group={group} funct3=0x{funct3:X}"),
        };

        // ShiftS uses integer shift-amount; Sadde/fsadde and SO_C getvl/setvl write scalar regs.
        int dest = uvOp switch {
            RvUveSoASadde s => s.Rd,
            RvUveSoCGetvl s => s.Rd,
            RvUveSoCSetvl s => s.Rd,
            _               => -1,
        };
        int[] intSrcs = uvOp switch {
            RvUveSoAShiftS ss              => [ss.Rs2,],
            RvUveSoASadde { Acc: true, } s => [s.Rd,],
            RvUveSoCSetvl s                => [s.Rs1,],
            _                              => [],
        };
        return new RvInstruction(pc, raw, dest, intSrcs, ToothClass.Uve, uvOp);
    }

    // SO_P predicate register operations.
    // group=8: simple manipulation ops (funct3[2]=0) and GE comparisons (funct3[2]=1)
    // group=9: EQ comparisons (funct3[2]=0) and LT comparisons (funct3[2]=1)
    private static RvOp DecodeSoP(uint raw, int group, uint funct3) {
        var govPred = (int)((raw >> 25) & 0x7); // bits[27:25] — governing predicate reg index
        bool zeroing = (raw & (1u << 24)) != 0; // bit[24] — only valid for simple ops
        var predRd = (int)((raw >> 7) & 0xF);   // bits[10:7] — dest pred reg (uve_pred_rd)
        var vs1 = (int)((raw >> 15) & 0x1F);    // bits[19:15] — source ud or pred reg
        var predRs1 = (int)((raw >> 15) & 0xF); // bits[18:15] — source pred reg (4-bit)

        switch (group) {
            // so.p.cv: group=8, funct3=3 — predicate width conversion.
            // rs2[1:0]=srcWidthIdx, rs2[3:2]=destWidthIdx, rs2[4]=zeroing (same as outer zeroing = bit24).
            case 8 when funct3 == 3: {
                var cvRs2 = (int)((raw >> 20) & 0x1F);
                int srcBytes = 1 << (cvRs2 & 3);
                int destBytes = 1 << ((cvRs2 >> 2) & 3);
                return new RvUveSoPCv(predRd, predRs1, srcBytes, destBytes, zeroing);
            }
            case 8 when (funct3 & 4) == 0: {
                // Simple ops: funct3[1:0] + bit[11]
                var bit11 = (int)((raw >> 11) & 1);
                var subOp = (int)(funct3 & 3);
                return (subOp, bit11) switch {
                    (0, 0) => new RvUveSoPSimple(UveSoPSimpleOp.Zero, predRd, govPred, zeroing, -1, -1),
                    (0, 1) => new RvUveSoPSimple(UveSoPSimpleOp.One, predRd, govPred, zeroing, -1, -1),
                    (1, 0) => new RvUveSoPSimple(UveSoPSimpleOp.Vr, predRd, govPred, zeroing, -1, vs1),
                    (1, 1) => new RvUveSoPSimple(UveSoPSimpleOp.Not, predRd, govPred, zeroing, predRs1, -1),
                    (2, 0) => new RvUveSoPSimple(UveSoPSimpleOp.Mv, predRd, govPred, zeroing, predRs1, -1),
                    (2, 1) => new RvUveSoPSimple(UveSoPSimpleOp.Mvt, predRd, govPred, zeroing, predRs1, -1),
                    _ => throw new IllegalInstructionException(raw, $"Unknown SO_P simple subOp={subOp} bit11={bit11}"),
                };
            }
        }

        // Comparison ops: GE (group=8, funct3[2]=1), EQ (group=9, funct3[2]=0), LT (group=9, funct3[2]=1)
        // vs2 = bits[24:20] (uve_pred_rs2); bit24 is the MSB of vs2, NOT the zeroing flag.
        // _z variants encoded as bit[11]=1; sets Zeroing mode tag on the output predicate register.
        var vs2 = (int)((raw >> 20) & 0x1F);
        bool cmpZeroing = ((raw >> 11) & 1) != 0;
        UveSoPCmpType cmpType = (funct3 & 3) switch {
            0 => UveSoPCmpType.Us,
            1 => UveSoPCmpType.Fp,
            2 => UveSoPCmpType.Sg,
            _ => throw new IllegalInstructionException(raw, $"Unknown SO_P cmp type funct3[1:0]={funct3 & 3}"),
        };
        UveSoPCmpOp cmpOp = group == 8 ? UveSoPCmpOp.Ge : (funct3 & 4) == 0 ? UveSoPCmpOp.Eq : UveSoPCmpOp.Lt;
        return new RvUveSoPCmp(cmpOp, cmpType, predRd, govPred, vs1, vs2, cmpZeroing);
    }

    private static RvOp UveArith(UveFpOp fpOp, UveIntOp intOp, int type, int ud, int usrc1, int usrc2, int ps3) =>
        type switch {
            1 => new RvUveSoAFp(fpOp, ud, usrc1, usrc2, ps3),
            2 => new RvUveSoAInt(intOp, true, ud, usrc1, usrc2, ps3),
            _ => new RvUveSoAInt(intOp, false, ud, usrc1, usrc2, ps3),
        };

    // UVE non-standard B-type immediate: bit28=imm[12](sign), bits[27:22]=imm[10:5], bit7=imm[11], bits[11:8]=imm[4:1]
    private static int UveBranchImm(uint raw) => SignExtendN(
        (int)(((raw >> 8) & 0xF) << 1) |
        (int)(((raw >> 22) & 0x3F) << 5) |
        (int)(((raw >> 7) & 1) << 11) |
        (int)(((raw >> 28) & 1) << 12),
        13
    );
}