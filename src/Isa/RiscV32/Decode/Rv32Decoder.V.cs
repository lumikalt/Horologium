using Mechanism;

namespace RiscV32.Decode;

public partial class Rv32Decoder {

    // ── V extension ───────────────────────────────────────────────────────────

    // VLE8/16/32 and VLM: opcode=0x07, funct3 ≠ 2.
    private static RvInstruction DecodeVLoad(
        ulong pc,
        uint raw,
        int vd,
        int rs1,
        uint funct3,
        uint word
    ) {
        uint mop = (word >> 26) & 0x3; // 00=unit-stride, 01=unordered-indexed, 10=strided, 11=ordered-indexed
        bool masked = ((word >> 25) & 1) == 0;

        switch (mop) {
            case 2: {
                // Strided: bits[24:20] = rs2 (stride register)
                var rs2 = (int)((word >> 20) & 0x1F);
                int sew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V strided load: unsupported funct3=0x{funct3:X}"
                    ),
                };
                var nfS = (int)((word >> 29) & 0x7);
                if (nfS > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1, rs2,], ToothClass.Vector,
                        new RvVlssegVv(nfS + 1, vd, rs1, rs2, sew, masked)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2,], ToothClass.Vector,
                    new RvVlseVv(vd, rs1, rs2, sew, masked)
                );
            }
            case 1 or 3: {
                // Indexed (unordered mop=1, ordered mop=3): bits[24:20] = vs2 (index vector)
                var vs2 = (int)((word >> 20) & 0x1F);
                int indexSew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V indexed load: unsupported index width funct3=0x{funct3:X}"
                    ),
                };
                var nfI = (int)((word >> 29) & 0x7);
                if (nfI > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVlxsegVv(nfI + 1, vd, rs1, vs2, indexSew, masked, mop == 3)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVlxeiVv(vd, rs1, vs2, indexSew, masked, mop == 3)
                );
            }
        }

        if (mop != 0)
            throw new IllegalInstructionException(
                raw, $"V load: mop={mop} not supported"
            );

        uint lumop = (word >> 20) & 0x1F; // unit-stride sub-mode

        // VLM: funct3=0 + lumop=01011
        if (funct3 == 0 && lumop == 0x0B)
            return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVlm(vd, rs1));

        // VL1R/2R/4R/8R: lumop=8, nf in bits[31:29] (0→1, 1→2, 3→4, 7→8)
        if (lumop == 0x08)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVlrV((int)((word >> 29) & 0x7) + 1, vd, rs1)
            );

        int sewU = funct3 switch {
            0 => 8,
            5 => 16,
            6 => 32,
            _ => throw new IllegalInstructionException(
                raw, $"V load: unsupported element width funct3=0x{funct3:X}"
            ),
        };

        // VLE{8,16,32}FF: lumop=0x10, fault-only-first (modeled as regular vle)
        if (lumop == 0x10)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVleFf(vd, rs1, sewU, masked)
            );

        var nf = (int)((word >> 29) & 0x7);
        if (nf > 0)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVlsegVv(nf + 1, vd, rs1, sewU, masked)
            );
        return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVleVv(vd, rs1, sewU, masked));
    }

    // VSE8/16/32, VSM, and VSSE8/16/32: opcode=0x27, funct3 ≠ 2.
    // rd (bits[11:7]) = vs3, rs2 (bits[24:20]) = sumop or stride register.
    private static RvInstruction DecodeVStore(
        ulong pc,
        uint raw,
        int vs3, // bits[11:7]
        int rs1,
        int rs2Field, // bits[24:20]: sumop for unit-stride, rs2 for strided
        uint funct3,
        uint word
    ) {
        uint mop = (word >> 26) & 0x3;
        bool masked = ((word >> 25) & 1) == 0;

        switch (mop) {
            case 2: {
                // Strided: rs2Field is the stride register
                int sew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V strided store: unsupported funct3=0x{funct3:X}"
                    ),
                };
                var nfSs = (int)((word >> 29) & 0x7);
                if (nfSs > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1, rs2Field,], ToothClass.Vector,
                        new RvVsssegVv(nfSs + 1, vs3, rs1, rs2Field, sew, masked)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1, rs2Field,], ToothClass.Vector,
                    new RvVsseVv(vs3, rs1, rs2Field, sew, masked)
                );
            }
            case 1:
            case 3: {
                // Indexed: bits[24:20] = vs2 (index vector); rs2Field already holds that value
                int indexSew = funct3 switch {
                    0 => 8,
                    5 => 16,
                    6 => 32,
                    _ => throw new IllegalInstructionException(
                        raw, $"V indexed store: unsupported index width funct3=0x{funct3:X}"
                    ),
                };
                var nfSx = (int)((word >> 29) & 0x7);
                if (nfSx > 0)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVsxsegVv(nfSx + 1, vs3, rs1, rs2Field, indexSew, masked, mop == 3)
                    );
                return new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVsxeiVv(vs3, rs1, rs2Field, indexSew, masked, mop == 3)
                );
            }
        }

        if (mop != 0)
            throw new IllegalInstructionException(
                raw, $"V store: mop={mop} not supported"
            );

        // VSM: funct3=0 + sumop=01011
        if (funct3 == 0 && rs2Field == 0x0B)
            return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVsm(vs3, rs1));

        // VS1R/2R/4R/8R: sumop=8, nf in bits[31:29] (0→1, 1→2, 3→4, 7→8)
        if (rs2Field == 0x08)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVsrV((int)((word >> 29) & 0x7) + 1, vs3, rs1)
            );

        int sewU = funct3 switch {
            0 => 8,
            5 => 16,
            6 => 32,
            _ => throw new IllegalInstructionException(
                raw, $"V store: unsupported element width funct3=0x{funct3:X}"
            ),
        };
        var nf = (int)((word >> 29) & 0x7);
        if (nf > 0)
            return new RvInstruction(
                pc, raw, -1, [rs1,], ToothClass.Vector,
                new RvVssegVv(nf + 1, vs3, rs1, sewU, masked)
            );
        return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVseVv(vs3, rs1, sewU, masked));
    }

    // OPIVV / OPIVX / OPIVI / OPCFG: opcode=0x57.
    private static RvInstruction DecodeVOp(
        ulong pc,
        uint raw,
        int rs1,
        uint funct3
    ) {
        var vd = (int)((raw >> 7) & 0x1F);
        var vs2 = (int)((raw >> 20) & 0x1F);
        bool masked = ((raw >> 25) & 1) == 0;
        uint funct6 = (raw >> 26) & 0x3F;

        switch (funct3) {
            // OPCFG (funct3=7)
            case 7: return DecodeVCfg(pc, raw, vd, rs1, vs2);

            // OPMVV (funct3=2): reductions, vmv.x.s, vcpop.m, vfirst.m, mask-unary, vcompress, …
            case 2: {
                switch (funct6) {
                    // VWXUNARY0: disambiguation by vs1 field
                    case 0x10 when rs1 == 0:
                        return new RvInstruction(pc, raw, vd /*rd*/, [], ToothClass.Vector, new RvVMvXs(vd, vs2));
                    case 0x10 when rs1 == 16:
                        return new RvInstruction(
                            pc, raw, vd /*rd*/, [], ToothClass.Vector, new RvVcpop(vd, vs2, masked)
                        );
                    case 0x10 when rs1 == 17:
                        return new RvInstruction(
                            pc, raw, vd /*rd*/, [], ToothClass.Vector, new RvVfirst(vd, vs2, masked)
                        );
                    case 0x10: throw new IllegalInstructionException(raw, $"V VWXUNARY0: unknown vs1=0x{rs1:X}");
                    case 0x14: {
                        // VMUNARY0: disambiguation by vs1 field
                        VMaskUnaryOp? unaryOp = rs1 switch {
                            1  => VMaskUnaryOp.Msbf,
                            2  => VMaskUnaryOp.Msof,
                            3  => VMaskUnaryOp.Msif,
                            16 => VMaskUnaryOp.Iota,
                            17 => VMaskUnaryOp.Id,
                            _  => null,
                        };
                        if (!unaryOp.HasValue)
                            throw new IllegalInstructionException(raw, $"V VMUNARY0: unknown vs1=0x{rs1:X}");
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVMaskUnary(unaryOp.Value, vd, vs2, masked)
                        );
                    }
                    case 0x17:
                        return new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVCompress(vd, vs2, rs1));
                }

                // vaaddu/vaadd/vasubu/vasub (fixed-point averaging): funct6=0x08-0x0B
                VAvgOp? avgOpVv = funct6 switch {
                    0x08 => VAvgOp.Addu, 0x09 => VAvgOp.Add,
                    0x0A => VAvgOp.Subu, 0x0B => VAvgOp.Sub,
                    _    => null,
                };
                if (avgOpVv.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVAvgVv(avgOpVv.Value, vd, vs2, rs1, masked)
                    );

                // vzext/vsext (VXUNARY0): funct6=0x12, vs1 field selects factor and sign
                if (funct6 == 0x12) {
                    bool extSigned = (rs1 & 1) != 0;
                    int factor = rs1 switch {
                        2 or 3 => 8, 4 or 5 => 4, 6 or 7 => 2,
                        _      => throw new IllegalInstructionException(raw, $"V VXUNARY0: unknown vs1=0x{rs1:X}"),
                    };
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVExt(extSigned, factor, vd, vs2, masked)
                    );
                }

                VRedOp? redOp = funct6 switch {
                    0 => VRedOp.Sum, 1  => VRedOp.And,
                    2 => VRedOp.Or, 3   => VRedOp.Xor,
                    4 => VRedOp.Minu, 5 => VRedOp.Min,
                    6 => VRedOp.Maxu, 7 => VRedOp.Max,
                    _ => null,
                };
                if (redOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVRedVs(redOp.Value, vd, vs2, rs1, masked)
                    );
                VMulOp? mulOp = funct6 switch {
                    0x25 => VMulOp.Mul,
                    0x27 => VMulOp.MulH,
                    0x24 => VMulOp.MulHu,
                    0x26 => VMulOp.MulHsu,
                    0x21 => VMulOp.Div,
                    0x20 => VMulOp.Divu,
                    0x23 => VMulOp.Rem,
                    0x22 => VMulOp.Remu,
                    _    => null,
                };
                if (mulOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVMulVv(mulOp.Value, vd, vs2, rs1, masked)
                    );
                // Widening add/sub (funct6 0x30-0x37) and widening mul (0x38, 0x3A, 0x3B)
                VWideOp? wideOpMvv = funct6 switch {
                    0x30 => VWideOp.AddU, 0x31 => VWideOp.Add,
                    0x32 => VWideOp.SubU, 0x33 => VWideOp.Sub,
                    0x34 => VWideOp.AddU, 0x35 => VWideOp.Add, // .wv: vs2 is 2*SEW
                    0x36 => VWideOp.SubU, 0x37 => VWideOp.Sub, // .wv: vs2 is 2*SEW
                    0x38 => VWideOp.MulU, 0x3A => VWideOp.MulSu, 0x3B => VWideOp.Mul,
                    _    => null,
                };
                if (wideOpMvv.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVWideVv(wideOpMvv.Value, vd, vs2, rs1, masked, funct6 is >= 0x34 and <= 0x37)
                    );
                VIntMacOp? macOp = funct6 switch {
                    0x29 => VIntMacOp.Madd,
                    0x2B => VIntMacOp.Nmsub,
                    0x2D => VIntMacOp.Macc,
                    0x2F => VIntMacOp.Nmsac,
                    _    => null,
                };
                if (macOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVIntMacVv(macOp.Value, vd, vs2, rs1, masked)
                    );
                VMaskLogOp? logOp = funct6 switch {
                    0x18 => VMaskLogOp.Andn,
                    0x19 => VMaskLogOp.And,
                    0x1A => VMaskLogOp.Or,
                    0x1B => VMaskLogOp.Xor,
                    0x1C => VMaskLogOp.Orn,
                    0x1D => VMaskLogOp.Nand,
                    0x1E => VMaskLogOp.Nor,
                    0x1F => VMaskLogOp.Xnor,
                    _    => null,
                };
                if (logOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVMaskLogMm(logOp.Value, vd, vs2, rs1)
                    );
                // Widening MAC: vwmaccu=0x3C, vwmacc=0x3D, vwmaccsu=0x3F (OPMVV; vwmaccus has no VV form)
                VwMacOp? wMacOp = funct6 switch {
                    0x3C => VwMacOp.Maccu,
                    0x3D => VwMacOp.Macc,
                    0x3F => VwMacOp.Maccsu,
                    _    => null,
                };
                if (wMacOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVwMacVv(wMacOp.Value, vd, vs2, rs1, masked)
                    );
                throw new IllegalInstructionException(raw, $"V op: unsupported OPMVV funct6=0x{funct6:X2}");
            }

            // OPMVX (funct3=6): integer multiply/divide VX
            case 6: {
                // vaaddu/vaadd/vasubu/vasub VX: funct6=0x08-0x0B
                VAvgOp? avgOpVx = funct6 switch {
                    0x08 => VAvgOp.Addu, 0x09 => VAvgOp.Add,
                    0x0A => VAvgOp.Subu, 0x0B => VAvgOp.Sub,
                    _    => null,
                };
                if (avgOpVx.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVAvgVx(avgOpVx.Value, vd, vs2, rs1, masked)
                    );

                VMulOp? mulOp = funct6 switch {
                    0x25 => VMulOp.Mul,
                    0x27 => VMulOp.MulH,
                    0x24 => VMulOp.MulHu,
                    0x26 => VMulOp.MulHsu,
                    0x21 => VMulOp.Div,
                    0x20 => VMulOp.Divu,
                    0x23 => VMulOp.Rem,
                    0x22 => VMulOp.Remu,
                    _    => null,
                };
                if (mulOp.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVMulVx(mulOp.Value, vd, vs2, rs1, masked)
                    );
                // Widening add/sub/mul VX variants (same funct6 as OPMVV)
                VWideOp? wideOpMvx = funct6 switch {
                    0x30 => VWideOp.AddU, 0x31 => VWideOp.Add,
                    0x32 => VWideOp.SubU, 0x33 => VWideOp.Sub,
                    0x34 => VWideOp.AddU, 0x35 => VWideOp.Add, // .wx: vs2 is 2*SEW
                    0x36 => VWideOp.SubU, 0x37 => VWideOp.Sub,
                    0x38 => VWideOp.MulU, 0x3A => VWideOp.MulSu, 0x3B => VWideOp.Mul,
                    _    => null,
                };
                if (wideOpMvx.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVWideVx(wideOpMvx.Value, vd, vs2, rs1, masked, funct6 is >= 0x34 and <= 0x37)
                    );
                switch (funct6) {
                    // vslide1up.vx (0x0E) / vslide1down.vx (0x0F)
                    case 0x0E:
                    case 0x0F:
                        return new RvInstruction(
                            pc, raw, -1, [rs1,], ToothClass.Vector,
                            new RvVSlideVx(funct6 == 0x0E ? VSlideDir.Up : VSlideDir.Down, true, vd, vs2, rs1, masked)
                        );
                    // vmv.s.x: move scalar integer rs1 into element 0 of vd (vs2 field must be 0)
                    case 0x10: return new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVMvSx(vd, rs1));
                }

                VIntMacOp? macOpX = funct6 switch {
                    0x29 => VIntMacOp.Madd,
                    0x2B => VIntMacOp.Nmsub,
                    0x2D => VIntMacOp.Macc,
                    0x2F => VIntMacOp.Nmsac,
                    _    => null,
                };
                if (macOpX.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVIntMacVx(macOpX.Value, vd, vs2, rs1, masked)
                    );
                // Widening MAC VX: vwmaccu=0x3C, vwmacc=0x3D, vwmaccus=0x3E, vwmaccsu=0x3F
                VwMacOp? wMacOpX = funct6 switch {
                    0x3C => VwMacOp.Maccu,
                    0x3D => VwMacOp.Macc,
                    0x3E => VwMacOp.Maccus,
                    0x3F => VwMacOp.Maccsu,
                    _    => null,
                };
                if (wMacOpX.HasValue)
                    return new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVwMacVx(wMacOpX.Value, vd, vs2, rs1, masked)
                    );
                throw new IllegalInstructionException(raw, $"V op: unsupported OPMVX funct6=0x{funct6:X2}");
            }
        }

        // OPFVV (funct3=1) and OPFVF (funct3=5): FP vector ops
        if (funct3 is 1 or 5) {
            bool isVf = funct3 == 5;
            int fpRs1 = rs1 + 32; // unified FRF index for scalar FP source

            switch (funct6) {
                // vfmv.f.s (OPFVV, funct6=0x10): float scalar ← vs2[0]
                case 0x10 when !isVf:
                    return new RvInstruction(
                        pc, raw, vd + 32, [], ToothClass.Vector,
                        new RvVFpMvFs(vd + 32, vs2)
                    );
                // vfmv.s.f (OPFVF, funct6=0x10, vs2=0): vd[0] ← float scalar
                case 0x10 when isVf:
                    return new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpMvSf(vd, fpRs1)
                    );
                // vfmv.v.f (OPFVF, funct6=0x17, vm=1): broadcast scalar float
                // vfmerge.vfm (OPFVF, funct6=0x17, vm=0): FP conditional merge using v0 mask
                case 0x17 when isVf:
                    return masked
                        ? new RvInstruction(pc, raw, -1, [fpRs1,], ToothClass.Vector, new RvVFpMergeVf(vd, vs2, fpRs1))
                        : new RvInstruction(pc, raw, -1, [fpRs1,], ToothClass.Vector, new RvVFpMvVf(vd, fpRs1, false));
            }

            // vfslide1up.vf (0x0E) / vfslide1down.vf (0x0F): FP slide1 with scalar float
            if (isVf && (funct6 == 0x0E || funct6 == 0x0F))
                return new RvInstruction(
                    pc, raw, -1, [fpRs1,], ToothClass.Vector,
                    new RvVFpSlide1Vf(funct6 == 0x0E ? VSlideDir.Up : VSlideDir.Down, vd, vs2, fpRs1, masked)
                );

            switch (funct6) {
                // vfcvt.* / vfwcvt.* / vfncvt.* (funct6=0x12): vs1 field selects op
                case 0x12: {
                    VFpCvtOp? cvtOp = rs1 switch {
                        0 => VFpCvtOp.XuFromF,
                        1 => VFpCvtOp.XFromF,
                        2 => VFpCvtOp.FFromXu,
                        3 => VFpCvtOp.FFromX,
                        6 => VFpCvtOp.RtzXuFromF,
                        7 => VFpCvtOp.RtzXFromF,
                        _ => null,
                    };
                    if (cvtOp.HasValue)
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVFpCvt(cvtOp.Value, vd, vs2, masked)
                        );
                    VFpWCvtOp? wCvtOp = rs1 switch {
                        8  => VFpWCvtOp.XuFromF,
                        9  => VFpWCvtOp.XFromF,
                        10 => VFpWCvtOp.FFromXu,
                        11 => VFpWCvtOp.FFromX,
                        12 => VFpWCvtOp.FFromF,
                        14 => VFpWCvtOp.RtzXuFromF,
                        15 => VFpWCvtOp.RtzXFromF,
                        _  => null,
                    };
                    if (wCvtOp.HasValue)
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVFpWCvt(wCvtOp.Value, vd, vs2, masked)
                        );
                    VFpNCvtOp? nCvtOp = rs1 switch {
                        16 => VFpNCvtOp.XuFromF,
                        17 => VFpNCvtOp.XFromF,
                        18 => VFpNCvtOp.FFromXu,
                        19 => VFpNCvtOp.FFromX,
                        20 => VFpNCvtOp.FFromF,
                        21 => VFpNCvtOp.RodFFromF,
                        22 => VFpNCvtOp.RtzXuFromF,
                        23 => VFpNCvtOp.RtzXFromF,
                        _  => null,
                    };
                    if (nCvtOp.HasValue)
                        return new RvInstruction(
                            pc, raw, -1, [], ToothClass.Vector,
                            new RvVFpNCvt(nCvtOp.Value, vd, vs2, masked)
                        );
                    throw new IllegalInstructionException(raw, $"V vfcvt: unknown vs1=0x{rs1:X}");
                }
                // vfsqrt.v (funct6=0x13, vs1=0) / vfclass.v (funct6=0x13, vs1=16)
                case 0x13:
                    return rs1 switch {
                        0  => new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVFpSqrt(vd, vs2, masked)),
                        16 => new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVFpClass(vd, vs2, masked)),
                        _  => throw new IllegalInstructionException(raw, $"V op: funct6=0x13 unknown vs1=0x{rs1:X}"),
                    };
            }

            // FP reductions: odd funct6 0x01/0x03/0x05/0x07 (OPFVV only, no VF form)
            VFpRedOp? fpRedOp = funct6 switch {
                0x01 => VFpRedOp.Usum,
                0x03 => VFpRedOp.Osum,
                0x05 => VFpRedOp.Min,
                0x07 => VFpRedOp.Max,
                _    => null,
            };
            if (fpRedOp.HasValue) {
                if (isVf)
                    throw new IllegalInstructionException(raw, $"V FP reduction has no VF form (funct6=0x{funct6:X2})");
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVFpRedVs(fpRedOp.Value, vd, vs2, rs1, masked)
                );
            }

            VFpBinOp? binOp = funct6 switch {
                0x00 => VFpBinOp.Add, 0x02  => VFpBinOp.Sub,
                0x04 => VFpBinOp.Min, 0x06  => VFpBinOp.Max,
                0x08 => VFpBinOp.Sgnj, 0x09 => VFpBinOp.Sgnjn, 0x0A => VFpBinOp.Sgnjx,
                0x20 => VFpBinOp.Div, 0x24  => VFpBinOp.Mul,
                _    => null,
            };
            if (binOp.HasValue)
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpBinVf(binOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpBinVv(binOp.Value, vd, vs2, rs1, masked)
                    );

            VFpCmpOp? fpCmpOp = funct6 switch {
                0x18 => VFpCmpOp.Eq, 0x19 => VFpCmpOp.Le,
                0x1B => VFpCmpOp.Lt, 0x1C => VFpCmpOp.Ne,
                0x1D => VFpCmpOp.Gt, 0x1F => VFpCmpOp.Ge,
                _    => null,
            };
            if (fpCmpOp.HasValue) {
                if ((fpCmpOp == VFpCmpOp.Gt || fpCmpOp == VFpCmpOp.Ge) && !isVf)
                    throw new IllegalInstructionException(raw, "vmfgt/vmfge.vv is not a valid encoding");
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVmFpCmpVf(fpCmpOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVmFpCmpVv(fpCmpOp.Value, vd, vs2, rs1, masked)
                    );
            }

            VFpFmaOp? fmaOp = funct6 switch {
                0x28 => VFpFmaOp.Madd, 0x29 => VFpFmaOp.Nmadd,
                0x2A => VFpFmaOp.Msub, 0x2B => VFpFmaOp.Nmsub,
                0x2C => VFpFmaOp.Macc, 0x2D => VFpFmaOp.Nmacc,
                0x2E => VFpFmaOp.Msac, 0x2F => VFpFmaOp.Nmsac,
                _    => null,
            };
            if (fmaOp.HasValue)
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpFmaVf(fmaOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpFmaVv(fmaOp.Value, vd, vs2, rs1, masked)
                    );

            // vfwredusum.vs (0x31) / vfwredosum.vs (0x33): widening FP sum reduction (OPFVV only)
            if ((funct6 == 0x31 || funct6 == 0x33) && !isVf)
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVFpWideRedVs(funct6 == 0x33, vd, vs2, rs1, masked)
                );

            // Widening FP arithmetic: 0x30=vfwadd.vv, 0x32=vfwsub.vv,
            //   0x34=vfwadd.wv, 0x36=vfwsub.wv, 0x38=vfwmul.vv
            VFpWideArithOp? wArithOp = funct6 switch {
                0x30 => VFpWideArithOp.Add, 0x32 => VFpWideArithOp.Sub,
                0x34 => VFpWideArithOp.Add, 0x36 => VFpWideArithOp.Sub,
                0x38 => VFpWideArithOp.Mul, _    => null,
            };
            if (wArithOp.HasValue) {
                bool vs2Wide = funct6 is 0x34 or 0x36;
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpWArithVf(wArithOp.Value, vd, vs2, fpRs1, vs2Wide, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpWArithVv(wArithOp.Value, vd, vs2, rs1, vs2Wide, masked)
                    );
            }

            // Widening FP MAC: 0x3C=vfwmacc, 0x3D=vfwnmacc, 0x3E=vfwmsac, 0x3F=vfwnmsac
            VFpWMacOp? wMacOp = funct6 switch {
                0x3C => VFpWMacOp.Macc, 0x3D => VFpWMacOp.Nmacc,
                0x3E => VFpWMacOp.Msac, 0x3F => VFpWMacOp.Nmsac,
                _    => null,
            };
            if (wMacOp.HasValue)
                return isVf
                    ? new RvInstruction(
                        pc, raw, -1, [fpRs1,], ToothClass.Vector,
                        new RvVFpWMacVf(wMacOp.Value, vd, vs2, fpRs1, masked)
                    )
                    : new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVFpWMacVv(wMacOp.Value, vd, vs2, rs1, masked)
                    );

            throw new IllegalInstructionException(
                raw, $"V op: unsupported OPFVV/OPFVF funct6=0x{funct6:X2}"
            );
        }

        // OPIVV (funct3=0), OPIVX (funct3=4), OPIVI (funct3=3)
        if (funct3 is not (0 or 3 or 4))
            throw new IllegalInstructionException(
                raw, $"V op: unsupported funct3=0x{funct3:X}"
            );

        switch (funct6) {
            // vrgather (funct6=0x0C): VV/VX/VI
            case 0x0C:
                return funct3 switch {
                    0 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVRgatherVv(vd, vs2, rs1, masked)
                    ),
                    3 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVRgatherVi(vd, vs2, rs1, masked)
                    ), // rs1 field = imm (unsigned uimm5)
                    _ => new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVRgatherVx(vd, vs2, rs1, masked)
                    ),
                };
            // vrgatherei16.vv: funct6=0x0E, OPIVV (funct3=0); u16 index vector regardless of SEW
            case 0x0E when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVRgatherEi16Vv(vd, vs2, rs1, masked)
                );
            // vslideup (funct6=0x0E) / vslidedown (funct6=0x0F): VX and VI
            case 0x0E or 0x0F: {
                VSlideDir dir = funct6 == 0x0E ? VSlideDir.Up : VSlideDir.Down;
                return funct3 == 3
                    ? new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVSlideVi(dir, vd, vs2, rs1, masked)
                    ) // rs1 field = uimm5 offset
                    : new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector,
                        new RvVSlideVx(dir, false, vd, vs2, rs1, masked)
                    );
            }
        }

        // Narrowing shift: funct6=0x2C (vnsrl) or 0x2D (vnsra); uses same funct3 as OPIVV/OPIVX/OPIVI.
        VNarrOp? narrOp = funct6 switch {
            0x2C => VNarrOp.Srl,
            0x2D => VNarrOp.Sra,
            _    => null,
        };
        if (narrOp.HasValue)
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVNarrVv(narrOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVNarrVi(narrOp.Value, vd, vs2, rs1, masked)
                ), // rs1 field = imm
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVNarrVx(narrOp.Value, vd, vs2, rs1, masked)
                ),
            };

        switch (funct6) {
            // vmerge.vvm/vxm/vim: funct6=0x17 with vm=0 (masked=true); same funct6 as vmv.v.* but masked
            case 0x17 when masked:
                return funct3 switch {
                    0 => new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVMergeVv(vd, vs2, rs1)),
                    3 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector, new RvVMergeVi(vd, vs2, SignExtend5(rs1))
                    ),
                    _ => new RvInstruction(pc, raw, -1, [rs1,], ToothClass.Vector, new RvVMergeVx(vd, vs2, rs1)),
                };
            // vmv{N}r.v: OPIVI (funct3=3) funct6=0x27; imm5 field (rs1) = N-1
            case 0x27 when funct3 == 3:
                return new RvInstruction(pc, raw, -1, [], ToothClass.Vector, new RvVMvNr(rs1 + 1, vd, vs2));
            // vwredsumu.vs (0x30) / vwredsum.vs (0x31): widening integer sum reduction (OPIVV only)
            case 0x30 or 0x31 when funct3 == 0:
                return new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVWideRedVs(funct6 == 0x31, vd, vs2, rs1, masked)
                );
        }

        // Saturating: 0x20=vsaddu, 0x21=vsadd, 0x22=vssubu, 0x23=vssub, 0x27=vsmul, 0x2A=vssrl, 0x2B=vssra
        VSatIntOp? satOp = funct6 switch {
            0x20 => VSatIntOp.Saddu,
            0x21 => VSatIntOp.Sadd,
            0x22 => VSatIntOp.Ssubu,
            0x23 => VSatIntOp.Ssub,
            0x27 => VSatIntOp.Smul,
            0x2A => VSatIntOp.Ssrl,
            0x2B => VSatIntOp.Ssra,
            _    => null,
        };
        if (satOp.HasValue) {
            if (funct3 == 3 && satOp.Value is VSatIntOp.Ssub or VSatIntOp.Ssubu)
                throw new IllegalInstructionException(raw, "V: vssub/vssubu has no VI form");
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVSatIntVv(satOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVSatIntVi(satOp.Value, vd, vs2, SignExtend5(rs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVSatIntVx(satOp.Value, vd, vs2, rs1, masked)
                ),
            };
        }

        // Narrowing saturating clip: 0x2E=vnclipu, 0x2F=vnclip
        VnClipOp? clipOp = funct6 switch {
            0x2E => VnClipOp.Clipu,
            0x2F => VnClipOp.Clip,
            _    => null,
        };
        if (clipOp.HasValue)
            return funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVnClipVv(clipOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector,
                    new RvVnClipVi(clipOp.Value, vd, vs2, SignExtend5(rs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector,
                    new RvVnClipVx(clipOp.Value, vd, vs2, rs1, masked)
                ),
            };

        VIntOp? intOp = funct6 switch {
            0  => VIntOp.Add,
            2  => VIntOp.Sub,
            3  => VIntOp.Rsub,
            4  => VIntOp.Minu,
            5  => VIntOp.Min,
            6  => VIntOp.Maxu,
            7  => VIntOp.Max,
            9  => VIntOp.And,
            10 => VIntOp.Or,
            11 => VIntOp.Xor,
            23 => VIntOp.Mov, // vmv.v.v / vmv.v.x / vmv.v.i (vm=1, unmasked)
            37 => VIntOp.Sll,
            40 => VIntOp.Srl,
            41 => VIntOp.Sra,
            _  => null,
        };

        VMaskCmpOp? cmpOp = funct6 switch {
            24 => VMaskCmpOp.Eq,
            25 => VMaskCmpOp.Ne,
            26 => VMaskCmpOp.Ltu,
            27 => VMaskCmpOp.Lt,
            30 => VMaskCmpOp.Gtu,
            31 => VMaskCmpOp.Gt,
            _  => null,
        };

        return intOp switch {
            null => cmpOp switch {
                null => throw new IllegalInstructionException(
                    raw, $"V op: unknown funct6=0x{funct6:X2} funct3=0x{funct3:X}"
                ),
                // vmsgtu/vmsgt: VX and VI only, not VV
                VMaskCmpOp.Gtu or VMaskCmpOp.Gt when funct3 == 0 => throw new IllegalInstructionException(
                    raw, "vmsgtu/vmsgt.vv is not a valid encoding"
                ),
                _ => funct3 switch {
                    0 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector, new RvVMaskCmpVv(cmpOp.Value, vd, vs2, rs1, masked)
                    ),
                    3 => new RvInstruction(
                        pc, raw, -1, [], ToothClass.Vector,
                        new RvVMaskCmpVi(cmpOp.Value, vd, vs2, SignExtend5(rs1), masked)
                    ),
                    _ => new RvInstruction(
                        pc, raw, -1, [rs1,], ToothClass.Vector, new RvVMaskCmpVx(cmpOp.Value, vd, vs2, rs1, masked)
                    ),
                },
            },
            // vsub has no VI variant
            VIntOp.Sub when funct3 == 3 => throw new IllegalInstructionException(
                raw, "vsub.vi is not a valid instruction"
            ),
            // vrsub has no VV form
            VIntOp.Rsub when funct3 == 0 => throw new IllegalInstructionException(
                raw, "vrsub.vv is not a valid encoding"
            ),
            // vminu/vmin/vmaxu/vmax have no VI variant
            VIntOp.Minu or VIntOp.Min or VIntOp.Maxu or VIntOp.Max when funct3 == 3 =>
                throw new IllegalInstructionException(raw, "vminu/vmin/vmaxu/vmax.vi is not a valid instruction"),
            _ => funct3 switch {
                0 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector, new RvVIntAluVv(intOp.Value, vd, vs2, rs1, masked)
                ),
                3 => new RvInstruction(
                    pc, raw, -1, [], ToothClass.Vector, new RvVIntAluVi(intOp.Value, vd, vs2, SignExtend5(rs1), masked)
                ),
                _ => new RvInstruction(
                    pc, raw, -1, [rs1,], ToothClass.Vector, new RvVIntAluVx(intOp.Value, vd, vs2, rs1, masked)
                ),
            },
        };
    }

    // OPCFG: vsetvli / vsetivli / vsetvl.
    private static RvInstruction DecodeVCfg(ulong pc, uint raw, int rd, int rs1, int rs2) {
        uint bits31 = raw >> 31;
        uint bits3130 = (raw >> 30) & 0x3;

        if (bits31 == 0) {
            // vsetvli: bit[31]=0, vtypei = bits[30:20] (11 bits)
            var vtypei = (int)((raw >> 20) & 0x7FF);
            IReadOnlyList<int> sources = rs1 != 0 ? [rs1,] : [];
            return new RvInstruction(pc, raw, rd, sources, ToothClass.Vector, new RvVsetvli(rd, rs1, vtypei));
        }

        if (bits3130 == 3) {
            // vsetivli: bits[31:30]=11, vtypei = bits[29:20] (10 bits), zimm = bits[19:15]
            var vtypei = (int)((raw >> 20) & 0x3FF);
            var zimm = (int)((raw >> 15) & 0x1F);
            return new RvInstruction(pc, raw, rd, [], ToothClass.Vector, new RvVsetivli(rd, zimm, vtypei));
        }

        // vsetvl: bits[31:25]=1000000
        return new RvInstruction(pc, raw, rd, [rs1, rs2,], ToothClass.Vector, new RvVsetvl(rd, rs1, rs2));
    }

    private static int SignExtend5(int value) =>
        (value & 0x10) != 0 ? value | unchecked((int)0xFFFFFFE0) : value & 0x1F;

}
