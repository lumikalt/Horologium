using Mechanism;
using RiscV32.Decode;
using RiscV32.Registers;
using RiscV32.State;

// ReSharper disable CompareOfFloatsByEqualityOperator

namespace RiscV32.Execute;

public partial class Rv32Executor {
    // ── V extension helpers ───────────────────────────────────────────────────

    private static Rv32ArchState VState(IArchState state) => (Rv32ArchState)state;

    private static ExecuteResult VectorWrite(int vd, byte[] data) {
        return new ExecuteResult { SideEffect = s => ((Rv32ArchState)s).VectorRegisters.Write(vd, data), };
    }

    private static ulong VReadElem(IArchState state, int vreg, int idx, int ewBytes) {
        byte[] data = VState(state).VectorRegisters.Read(vreg);
        return ReadVElement(data, idx, ewBytes);
    }

    private static ulong ReadVElement(byte[] data, int idx, int ewBytes) {
        int off = idx * ewBytes;
        return ewBytes switch {
            1 => data[off],
            2 => (ulong)(data[off] | (data[off + 1] << 8)),
            8 => BitConverter.ToUInt64(data, off),
            _ => (ulong)(data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24)),
        };
    }

    private static void WriteVElement(byte[] data, int idx, int ewBytes, ulong value) {
        int off = idx * ewBytes;
        for (var b = 0; b < ewBytes; b++) data[off + b] = (byte)(value >> (b * 8));
    }

    // Vlse/Vsse stride: RV32's rs2 holds a 32-bit signed byte stride that must be sign-extended
    // to the 64-bit address-arithmetic width. Rv64Executor overrides this — under RV64 the
    // register already holds the full native-width stride and must not be truncated to 32 bits.
    protected virtual long ReadStride(IRegisterFile regs, int rs2) => (int)(uint)regs.Read(rs2);

    private static (uint vl, int ewBytes) VGetVlEw(IArchState state) {
        CsrFile csrs = VState(state).CsrFile;
        uint vl = csrs.DirectRead(CsrFile.Vl);
        uint vsew = (csrs.DirectRead(CsrFile.Vtype) >> 3) & 0x7; // vtype bits [5:3]
        var ewBytes = (int)(1u << (int)vsew);                    // 1, 2, 4, 8
        return (vl, ewBytes);
    }

    private static uint ComputeVlmax(int vtypei) {
        var vsew = (uint)((vtypei >> 3) & 0x7); // vtypei bits [5:3] = vsew
        var ewBits = (int)(8u << (int)vsew);    // 8, 16, 32, 64
        int vlmulField = vtypei & 0x7;          // vtypei bits [2:0] = vlmul
        // LMUL: fields 0-3 = 1,2,4,8; fields 5-7 = 1/8,1/4,1/2 (fractional)
        uint vlmax;
        if (vlmulField <= 3)
            vlmax = ((uint)VectorRegisterFile.VLen << vlmulField) / (uint)ewBits;
        else
            vlmax = ((uint)VectorRegisterFile.VLen >> (8 - vlmulField)) / (uint)ewBits;
        return vlmax == 0 ? 1 : vlmax;
    }

    private static ExecuteResult ExecuteVsetvli(IArchState state, int rd, int rs1, int vtypei) {
        CsrFile csrs = VState(state).CsrFile;
        uint vlmax = ComputeVlmax(vtypei);
        uint currentVl = csrs.DirectRead(CsrFile.Vl);

        uint newVl = rd == 0 && rs1 == 0
            ? currentVl // preserve vl
            : rs1 == 0
                ? vlmax // set vl = vlmax
                : Math.Min((uint)state.IntegerRegisters.Read(rs1), vlmax);

        csrs.DirectWrite(CsrFile.Vl, newVl);
        csrs.DirectWrite(CsrFile.Vtype, (uint)vtypei);
        return rd == 0 ? ExecuteResult.Clean : ExecuteResult.WithResult(newVl);
    }

    private static ExecuteResult ExecuteVsetivli(IArchState state, int rd, int zimm, int vtypei) {
        CsrFile csrs = VState(state).CsrFile;
        uint vlmax = ComputeVlmax(vtypei);
        uint newVl = Math.Min((uint)zimm, vlmax);
        csrs.DirectWrite(CsrFile.Vl, newVl);
        csrs.DirectWrite(CsrFile.Vtype, (uint)vtypei);
        return rd == 0 ? ExecuteResult.Clean : ExecuteResult.WithResult(newVl);
    }

    private static ExecuteResult ExecuteVsetvl(
        IArchState state,
        int rd,
        int rs1,
        int rs2,
        IRegisterFile regs
    ) {
        CsrFile csrs = VState(state).CsrFile;
        var vtypei = (int)(uint)regs.Read(rs2);
        uint vlmax = ComputeVlmax(vtypei);
        uint currentVl = csrs.DirectRead(CsrFile.Vl);

        uint newVl = rd == 0 && rs1 == 0
            ? currentVl
            : rs1 == 0
                ? vlmax
                : Math.Min((uint)regs.Read(rs1), vlmax);

        csrs.DirectWrite(CsrFile.Vl, newVl);
        csrs.DirectWrite(CsrFile.Vtype, (uint)vtypei);
        return rd == 0 ? ExecuteResult.Clean : ExecuteResult.WithResult(newVl);
    }

    private static ExecuteResult ExecuteVle(
        IArchState state,
        IMemory memory,
        int vd,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        var result = new byte[VectorRegisterFile.VLenB];
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong val = memory.Read(baseAddr + (ulong)(i * ewBytes), ewBytes);
            WriteVElement(result, i, ewBytes, val);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVlm(IArchState state, IMemory memory, int vd, int rs1) {
        (uint vl, _) = VGetVlEw(state);
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        var result = new byte[VectorRegisterFile.VLenB];
        // VLM loads ceil(vl/8) bytes
        int byteCount = ((int)vl + 7) / 8;
        for (var b = 0; b < byteCount; b++) result[b] = (byte)memory.Read(baseAddr + (ulong)b, 1);
        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVse(
        IArchState state,
        IMemory memory,
        int vs3,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong val = ReadVElement(data, i, ewBytes);
            memory.Write(baseAddr + (ulong)(i * ewBytes), val, ewBytes);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVsm(IArchState state, IMemory memory, int vs3, int rs1) {
        (uint vl, _) = VGetVlEw(state);
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        int byteCount = ((int)vl + 7) / 8;
        for (var b = 0; b < byteCount; b++) memory.Write(baseAddr + (ulong)b, data[b], 1);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVlr(IArchState state, IMemory memory, int numRegs, int vd, int rs1) {
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                var rv = (Rv32ArchState)s;
                for (var r = 0; r < numRegs; r++) {
                    var buf = new byte[VectorRegisterFile.VLenB];
                    for (var b = 0; b < VectorRegisterFile.VLenB; b++)
                        buf[b] = (byte)memory.Read(baseAddr + (ulong)(r * VectorRegisterFile.VLenB + b), 1);
                    rv.VectorRegisters.Write(vd + r, buf);
                }
            },
        };
    }

    private static ExecuteResult ExecuteVsr(IArchState state, IMemory memory, int numRegs, int vs3, int rs1) {
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        for (var r = 0; r < numRegs; r++) {
            byte[] data = vregs.Read(vs3 + r);
            for (var b = 0; b < VectorRegisterFile.VLenB; b++)
                memory.Write(baseAddr + (ulong)(r * VectorRegisterFile.VLenB + b), data[b], 1);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVlseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vd,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        var results = new byte[numFields][];
        for (var f = 0; f < numFields; f++) results[f] = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + (ulong)((i * numFields + f) * ewBytes);
                WriteVElement(results[f], i, ewBytes, memory.Read(addr, ewBytes));
            }
        }

        return new ExecuteResult {
            SideEffect = s => {
                for (var f = 0; f < numFields; f++) VState(s).VectorRegisters.Write(vd + f, results[f]);
            },
        };
    }

    private static ExecuteResult ExecuteVsseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vs3,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        var srcs = new byte[numFields][];
        for (var f = 0; f < numFields; f++) srcs[f] = VState(state).VectorRegisters.Read(vs3 + f);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + (ulong)((i * numFields + f) * ewBytes);
                memory.Write(addr, ReadVElement(srcs[f], i, ewBytes), ewBytes);
            }
        }

        return ExecuteResult.Clean;
    }

    private ExecuteResult ExecuteVlse(
        IArchState state,
        IMemory memory,
        int vd,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = ReadStride(state.IntegerRegisters, rs2);
        var result = new byte[VectorRegisterFile.VLenB];
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var addr = (ulong)((long)baseAddr + stride * i);
            WriteVElement(result, i, ewBytes, memory.Read(addr, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private ExecuteResult ExecuteVsse(
        IArchState state,
        IMemory memory,
        int vs3,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = ReadStride(state.IntegerRegisters, rs2);
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var addr = (ulong)((long)baseAddr + stride * i);
            memory.Write(addr, ReadVElement(data, i, ewBytes), ewBytes);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVlxei(
        IArchState state,
        IMemory memory,
        int vd,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            WriteVElement(result, i, ewBytes, memory.Read(baseAddr + offset, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVsxei(
        IArchState state,
        IMemory memory,
        int vs3,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            memory.Write(baseAddr + offset, ReadVElement(data, i, ewBytes), ewBytes);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVWide(
        IArchState state,
        VWideOp op,
        int vd,
        int vs2,
        bool masked,
        bool vs2IsWide,
        Func<int, int, ulong> getSource
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int outEwBytes = ewBytes * 2;
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / outEwBytes);
        int vs2EwBytes = vs2IsWide ? outEwBytes : ewBytes;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, vs2EwBytes);
            ulong b = getSource(i, ewBytes);
            WriteVElement(result, i, outEwBytes, ApplyVWideOp(op, a, b, ewBytes, vs2IsWide));
        }

        return VectorWrite(vd, result);
    }

    private static ulong ApplyVWideOp(VWideOp op, ulong a, ulong b, int ewBytes, bool vs2IsWide) {
        return op switch {
            VWideOp.AddU  => (vs2IsWide ? a : Zx(a)) + Zx(b),
            VWideOp.Add   => (ulong)((vs2IsWide ? Sx2(a) : Sx(a)) + Sx(b)),
            VWideOp.SubU  => (vs2IsWide ? a : Zx(a)) - Zx(b),
            VWideOp.Sub   => (ulong)((vs2IsWide ? Sx2(a) : Sx(a)) - Sx(b)),
            VWideOp.MulU  => Zx(a) * Zx(b),
            VWideOp.MulSu => (ulong)(Sx(a) * (long)Zx(b)),
            VWideOp.Mul   => (ulong)(Sx(a) * Sx(b)),
            _             => throw new InvalidOperationException($"Unknown VWideOp {op}"),
        };

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };

        long Sx2(ulong v) => (ewBytes * 2) switch {
            2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };

        ulong Zx(ulong v) => ewBytes switch {
            1 => (byte)v, 2 => (ushort)v, 4 => (uint)v, _ => v,
        };
    }

    // vwmacc/vwmaccu/vwmaccsu/vwmaccus: vd[i] (2×SEW) += product of two SEW operands.
    private static ExecuteResult ExecuteVwMac(
        IArchState state,
        VwMacOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int outEwBytes = ewBytes * 2;
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / outEwBytes);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vdData = vregs.Read(vd);
        byte[] mask = vregs.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdData, result, vdData.Length);

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong acc = ReadVElement(result, i, outEwBytes);
            ulong product = op switch {
                VwMacOp.Maccu  => Zx(a) * Zx(b),
                VwMacOp.Macc   => (ulong)(Sx(a) * Sx(b)),
                VwMacOp.Maccsu => (ulong)(Sx(a) * (long)Zx(b)),
                VwMacOp.Maccus => (ulong)((long)Zx(a) * Sx(b)),
                _              => 0UL,
            };
            WriteVElement(result, i, outEwBytes, acc + product);
        }

        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };

        ulong Zx(ulong v) => ewBytes switch {
            1 => (byte)v, 2 => (ushort)v, 4 => (uint)v, _ => v,
        };
    }

    private static ExecuteResult ExecuteVNarr(
        IArchState state,
        VNarrOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getShift
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state); // output element width
        int srcEwBytes = ewBytes * 2;             // vs2 source element width (2*SEW)
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / srcEwBytes);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        int shamtMask = srcEwBytes * 8 - 1; // log2(2*SEW) bits

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong src = ReadVElement(vs2Data, i, srcEwBytes);
            var shamt = (int)(getShift(i, ewBytes) & (ulong)shamtMask);
            ulong res = op == VNarrOp.Sra
                ? (ulong)(srcEwBytes switch {
                    2 => (short)(ushort)src >> shamt,
                    4 => (int)(uint)src >> shamt,
                    _ => (long)src >> shamt,
                })
                : src >> shamt;
            WriteVElement(result, i, ewBytes, res);
        }

        return VectorWrite(vd, result);
    }

    // ── V new ops ─────────────────────────────────────────────────────────────

    // vzext/vsext: zero/sign extend each element from SEW/factor bits to SEW bits.
    private static ExecuteResult ExecuteVExt(IArchState state, bool signed, int factor, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int srcBytes = ewBytes / factor;
        if (srcBytes < 1) throw new InvalidOperationException("VExt: factor exceeds SEW");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong raw = ReadVElement(vs2Data, i, srcBytes);
            ulong val = signed
                ? srcBytes switch {
                    1 => (ulong)(sbyte)(byte)raw,
                    2 => (ulong)(short)(ushort)raw,
                    _ => (ulong)(int)(uint)raw,
                }
                : raw;
            ulong mask = ewBytes == 8 ? ulong.MaxValue : (1UL << (ewBytes * 8)) - 1;
            WriteVElement(result, i, ewBytes, val & mask);
        }

        return VectorWrite(vd, result);
    }

    // vaaddu/vaadd/vasubu/vasub: fixed-point averaging with vxrm rounding.
    private static ExecuteResult ExecuteVAvg(
        IArchState state,
        VAvgOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        uint vxrm = VState(state).CsrFile.DirectRead(CsrFile.Vxrm) & 3;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        ulong uMax = ewBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, 4 => 0xFFFFFFFFUL, _ => ulong.MaxValue, };

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong sum;
            ulong shifted;
            switch (op) {
                case VAvgOp.Addu:
                    sum = (a & uMax) + (b & uMax);
                    shifted = sum >> 1;
                    break;
                case VAvgOp.Add: {
                    long ssum = Sx(a) + Sx(b);
                    sum = (ulong)ssum;
                    shifted = (ulong)(ssum >> 1);
                    break;
                }
                case VAvgOp.Subu:
                    sum = (a & uMax) - (b & uMax);
                    shifted = sum >> 1;
                    break;
                case VAvgOp.Sub:
                default: {
                    // Sub (signed)
                    long sdiff = Sx(a) - Sx(b);
                    sum = (ulong)sdiff;
                    shifted = (ulong)(sdiff >> 1);
                    break;
                }
            }

            WriteVElement(result, i, ewBytes, (shifted + ComputeRoundBit(sum, 1, shifted, vxrm)) & uMax);
        }

        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };
    }

    // vfwredusum.vs / vfwredosum.vs: f32 elements summed into f64 accumulator in vd[0].
    private static ExecuteResult ExecuteVFpWideRed(
        IArchState state,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwred: only SEW=32 supported");
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vs1Data = vregs.Read(vs1);
        byte[] maskData = vregs.Read(0);
        double acc = BitConverter.Int64BitsToDouble((long)ReadVElement(vs1Data, 0, 8));

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            acc += BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
        }

        var result = new byte[VectorRegisterFile.VLenB];
        WriteVElement(result, 0, 8, (ulong)BitConverter.DoubleToInt64Bits(acc));
        return VectorWrite(vd, result);
    }

    // vlsseg: strided segment load — element i field f at base + stride*i + f*ewBytes.
    private static ExecuteResult ExecuteVlsseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vd,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = (int)(uint)state.IntegerRegisters.Read(rs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var results = new byte[numFields][];
        for (var f = 0; f < numFields; f++) results[f] = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = (ulong)((long)baseAddr + stride * i) + (ulong)(f * ewBytes);
                WriteVElement(results[f], i, ewBytes, memory.Read(addr, ewBytes));
            }
        }

        return new ExecuteResult {
            SideEffect = s => {
                for (var f = 0; f < numFields; f++) VState(s).VectorRegisters.Write(vd + f, results[f]);
            },
        };
    }

    // vsseg (strided): element i field f at base + stride*i + f*ewBytes.
    private static ExecuteResult ExecuteVssseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vs3,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = (int)(uint)state.IntegerRegisters.Read(rs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var srcs = new byte[numFields][];
        for (var f = 0; f < numFields; f++) srcs[f] = VState(state).VectorRegisters.Read(vs3 + f);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = (ulong)((long)baseAddr + stride * i) + (ulong)(f * ewBytes);
                memory.Write(addr, ReadVElement(srcs[f], i, ewBytes), ewBytes);
            }
        }

        return ExecuteResult.Clean;
    }

    // vlxseg: indexed segment load — element i field f at base + index[i] + f*dataSew/8.
    private static ExecuteResult ExecuteVlxseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vd,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var results = new byte[numFields][];
        for (var f = 0; f < numFields; f++) results[f] = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + offset + (ulong)(f * ewBytes);
                WriteVElement(results[f], i, ewBytes, memory.Read(addr, ewBytes));
            }
        }

        return new ExecuteResult {
            SideEffect = s => {
                for (var f = 0; f < numFields; f++) VState(s).VectorRegisters.Write(vd + f, results[f]);
            },
        };
    }

    // vsxseg: indexed segment store — element i field f at base + index[i] + f*dataSew/8.
    private static ExecuteResult ExecuteVsxseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vs3,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var srcs = new byte[numFields][];
        for (var f = 0; f < numFields; f++) srcs[f] = VState(state).VectorRegisters.Read(vs3 + f);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + offset + (ulong)(f * ewBytes);
                memory.Write(addr, ReadVElement(srcs[f], i, ewBytes), ewBytes);
            }
        }

        return ExecuteResult.Clean;
    }

    // ── Saturating integer helpers (vxrm rounding modes) ─────────────────────

    // Round bit for a right-shift of shiftAmt bits on value, given already-shifted result.
    private static ulong ComputeRoundBit(ulong value, int shiftAmt, ulong shifted, uint vxrm) {
        switch (vxrm) {
            case 0: // rnu: round-nearest-up (add guard bit)
                return (value >> (shiftAmt - 1)) & 1;
            case 1: {
                // rne: round-nearest-even (round to even if exactly halfway)
                ulong g = (value >> (shiftAmt - 1)) & 1;
                ulong sticky = shiftAmt >= 2 ? value & ((1UL << (shiftAmt - 1)) - 1) : 0;
                return g & ((sticky != 0 ? 1UL : 0) | (shifted & 1));
            }
            case 2: // rdn: round-down (truncate)
                return 0;
            default: {
                // rod: round-to-odd (force result LSB = 1 if any truncated bits are nonzero)
                ulong lost = shiftAmt < 64 ? value & ((1UL << shiftAmt) - 1) : value != 0 ? 1UL : 0;
                return ~shifted & 1 & (lost != 0 ? 1UL : 0);
            }
        }
    }

    private static ulong VRoundShiftU(ulong value, int shiftAmt, uint vxrm) {
        if (shiftAmt == 0) return value;
        ulong shifted = value >> shiftAmt;
        return shifted + ComputeRoundBit(value, shiftAmt, shifted, vxrm);
    }

    // Arithmetic rounded shift; value is sign-extended from ewBytes before shifting.
    private static ulong VRoundShiftS(ulong value, int shiftAmt, uint vxrm, int ewBytes) {
        long svalue = ewBytes switch {
            1 => (sbyte)(byte)value,
            2 => (short)(ushort)value,
            4 => (int)(uint)value,
            _ => (long)value,
        };
        if (shiftAmt == 0) return (ulong)svalue;
        var uvalue = (ulong)svalue;
        var shifted = (ulong)(svalue >> shiftAmt);
        return shifted + ComputeRoundBit(uvalue, shiftAmt, shifted, vxrm);
    }

    private static ExecuteResult ExecuteVSatInt(
        IArchState state,
        VSatIntOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        uint vxrm = VState(state).CsrFile.DirectRead(CsrFile.Vxrm) & 3;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        int sew = ewBytes * 8;
        int shiftMask = sew - 1;
        ulong uMax = ewBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, 4 => 0xFFFFFFFFUL, _ => ulong.MaxValue, };
        long sMin = ewBytes switch { 1 => sbyte.MinValue, 2 => short.MinValue, 4 => int.MinValue, _ => long.MinValue, };
        long sMax = ewBytes switch { 1 => sbyte.MaxValue, 2 => short.MaxValue, 4 => int.MaxValue, _ => long.MaxValue, };

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong elem;
            switch (op) {
                case VSatIntOp.Sadd: elem = (ulong)Math.Clamp(Sx(a) + Sx(b), sMin, sMax); break;
                case VSatIntOp.Saddu: {
                    ulong sum = (a & uMax) + (b & uMax);
                    elem = sum > uMax ? uMax : sum;
                    break;
                }
                case VSatIntOp.Ssub: elem = (ulong)Math.Clamp(Sx(a) - Sx(b), sMin, sMax); break;
                case VSatIntOp.Ssubu: {
                    ulong ua = a & uMax, ub = b & uMax;
                    elem = ua >= ub ? ua - ub : 0;
                    break;
                }
                case VSatIntOp.Smul: {
                    // 2*SEW product, round right by (SEW-1), saturate
                    long sa = Sx(a), sb = Sx(b);
                    long product = sa * sb;
                    int shift = sew - 1;
                    ulong rounded = VRoundShiftS((ulong)product, shift, vxrm, ewBytes * 2);
                    elem = (ulong)Math.Clamp((long)rounded, sMin, sMax);
                    break;
                }
                case VSatIntOp.Ssrl: {
                    var shamt = (int)(b & (ulong)shiftMask);
                    elem = VRoundShiftU(a & uMax, shamt, vxrm) & uMax;
                    break;
                }
                case VSatIntOp.Ssra: {
                    var shamt = (int)(b & (ulong)shiftMask);
                    elem = VRoundShiftS(a, shamt, vxrm, ewBytes) & uMax;
                    break;
                }
                default: elem = 0; break;
            }

            WriteVElement(result, i, ewBytes, elem);
        }

        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };
    }

    private static ExecuteResult ExecuteVnClip(
        IArchState state,
        VnClipOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state); // output SEW
        int inEwBytes = ewBytes * 2;              // input is 2*SEW
        uint vxrm = VState(state).CsrFile.DirectRead(CsrFile.Vxrm) & 3;
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / inEwBytes);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        int shiftMask = inEwBytes * 8 - 1; // log2(2*SEW) bits
        ulong uMax = ewBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, 4 => 0xFFFFFFFFUL, _ => ulong.MaxValue, };
        long sMin = ewBytes switch { 1 => sbyte.MinValue, 2 => short.MinValue, 4 => int.MinValue, _ => long.MinValue, };
        long sMax = ewBytes switch { 1 => sbyte.MaxValue, 2 => short.MaxValue, 4 => int.MaxValue, _ => long.MaxValue, };
        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, inEwBytes);
            ulong b = getB(i, ewBytes);
            var shamt = (int)(b & (ulong)shiftMask);
            ulong elem;
            if (op == VnClipOp.Clipu) {
                ulong shifted = VRoundShiftU(a, shamt, vxrm);
                elem = Math.Min(shifted, uMax);
            }
            else {
                ulong shifted = VRoundShiftS(a, shamt, vxrm, inEwBytes);
                elem = (ulong)Math.Clamp((long)shifted, sMin, sMax);
            }

            WriteVElement(result, i, ewBytes, elem);
        }

        return VectorWrite(vd, result);
    }

    // offsetOrVal interpretation:
    //   is1=false → unsigned offset count (elements to shift by)
    //   is1=true  → scalar value to insert at the slide boundary (position 0 for Up, vl-1 for Down)
    private static ExecuteResult ExecuteVSlide(
        IArchState state,
        VSlideDir dir,
        bool is1,
        int vd,
        int vs2,
        bool masked,
        ulong offsetOrVal
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        // Start with current vd content so unaffected elements are preserved (TU semantics).
        var result = (byte[])VState(state).VectorRegisters.Read(vd).Clone();

        if (dir == VSlideDir.Up) {
            ulong offset = is1 ? 1UL : offsetOrVal;
            for (var i = (int)offset; i < (int)vl; i++) {
                if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
                ulong val = i == 0 && is1
                    ? offsetOrVal // insert scalar at vd[0]
                    : ReadVElement(src, i - (int)offset, ewBytes);
                WriteVElement(result, i, ewBytes, val);
            }

            // vslide1up: also write scalar at position 0
            if (is1 && vl > 0 && (!masked || ((mask[0] >> 0) & 1) != 0)) WriteVElement(result, 0, ewBytes, offsetOrVal);
        }
        else {
            ulong offset = is1 ? 1UL : offsetOrVal;
            for (var i = 0; i < (int)vl; i++) {
                if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
                ulong srcIdx = (ulong)i + offset;
                ulong val = is1 && i == (int)vl - 1
                    ? offsetOrVal // insert scalar at last position
                    : srcIdx < vl
                        ? ReadVElement(src, (int)srcIdx, ewBytes)
                        : 0;
                WriteVElement(result, i, ewBytes, val);
            }
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVRgatherEi16(
        IArchState state,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] idxData = VState(state).VectorRegisters.Read(vs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong index = ReadVElement(idxData, i, 2); // always u16 regardless of SEW
            WriteVElement(result, i, ewBytes, index < vl ? ReadVElement(src, (int)index, ewBytes) : 0);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVRgather(
        IArchState state,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getIndex
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong index = getIndex(i, ewBytes);
            WriteVElement(result, i, ewBytes, index < vl ? ReadVElement(src, (int)index, ewBytes) : 0);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVIntAlu(
        IArchState state,
        VIntOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getSource
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0); // snapshot before any writes
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getSource(i, ewBytes);
            WriteVElement(result, i, ewBytes, ApplyVIntOp(op, a, b, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVMaskLog(IArchState state, VMaskLogOp op, int vd, int vs2, int vs1) {
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] a = vregs.Read(vs2);
        byte[] b = vregs.Read(vs1);
        var result = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < VectorRegisterFile.VLenB; i++)
            result[i] = op switch {
                VMaskLogOp.Andn => (byte)(a[i] & ~b[i]),
                VMaskLogOp.And  => (byte)(a[i] & b[i]),
                VMaskLogOp.Or   => (byte)(a[i] | b[i]),
                VMaskLogOp.Xor  => (byte)(a[i] ^ b[i]),
                VMaskLogOp.Orn  => (byte)(a[i] | ~b[i]),
                VMaskLogOp.Nand => (byte)~(a[i] & b[i]),
                VMaskLogOp.Nor  => (byte)~(a[i] | b[i]),
                VMaskLogOp.Xnor => (byte)~(a[i] ^ b[i]),
                _               => 0,
            };
        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVcpop(IArchState state, int vs2, bool masked) {
        (uint vl, _) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = masked ? VState(state).VectorRegisters.Read(0) : [];
        ulong count = 0;
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            if (((src[i >> 3] >> (i & 7)) & 1) != 0) count++;
        }

        return ExecuteResult.WithResult(count);
    }

    private static ExecuteResult ExecuteVfirst(IArchState state, int vs2, bool masked) {
        (uint vl, _) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = masked ? VState(state).VectorRegisters.Read(0) : [];
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            if (((src[i >> 3] >> (i & 7)) & 1) != 0) return ExecuteResult.WithResult((uint)i);
        }

        return ExecuteResult.WithResult(ulong.MaxValue); // -1 sign-extended to XLEN
    }

    private static ExecuteResult ExecuteVMaskUnary(
        IArchState state,
        VMaskUnaryOp op,
        int vd,
        int vs2,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] maskReg = masked ? vregs.Read(0) : [];
        byte[] vdOld = vregs.Read(vd);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdOld, result, VectorRegisterFile.VLenB);

        switch (op) {
            case VMaskUnaryOp.Msbf or VMaskUnaryOp.Msof or VMaskUnaryOp.Msif: {
                byte[] src2 = vregs.Read(vs2);
                int firstSet = -1;
                for (var i = 0; i < (int)vl; i++)
                    if (((src2[i >> 3] >> (i & 7)) & 1) != 0) {
                        firstSet = i;
                        break;
                    }

                for (var i = 0; i < (int)vl; i++) {
                    if (masked && ((maskReg[i >> 3] >> (i & 7)) & 1) == 0) continue;
                    bool val = op switch {
                        VMaskUnaryOp.Msbf => firstSet < 0 || i < firstSet,
                        VMaskUnaryOp.Msof => i == firstSet,
                        _                 => firstSet < 0 || i <= firstSet, // Msif
                    };
                    if (val)
                        result[i >> 3] |= (byte)(1 << (i & 7));
                    else
                        result[i >> 3] &= (byte)~(1 << (i & 7));
                }

                return VectorWrite(vd, result);
            }
            case VMaskUnaryOp.Iota: {
                byte[] src2 = vregs.Read(vs2);
                ulong prefix = 0;
                for (var i = 0; i < (int)vl; i++) {
                    bool isSet = ((src2[i >> 3] >> (i & 7)) & 1) != 0;
                    if (masked && ((maskReg[i >> 3] >> (i & 7)) & 1) == 0) {
                        if (isSet) prefix++;
                        continue;
                    }

                    WriteVElement(result, i, ewBytes, prefix);
                    if (isSet) prefix++;
                }

                return VectorWrite(vd, result);
            }
        }

        // VMaskUnaryOp.Id: write element index i into each active vd[i]
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskReg[i >> 3] >> (i & 7)) & 1) == 0) continue;
            WriteVElement(result, i, ewBytes, (uint)i);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVCompress(IArchState state, int vd, int vs2, int vs1) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] srcData = vregs.Read(vs2);
        byte[] maskReg = vregs.Read(vs1); // vs1 is the explicit mask register (not v0)
        byte[] vdOld = vregs.Read(vd);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdOld, result, VectorRegisterFile.VLenB); // tail elements undisturbed
        var destIdx = 0;
        for (var i = 0; i < (int)vl; i++) {
            if (((maskReg[i >> 3] >> (i & 7)) & 1) == 0) continue;
            WriteVElement(result, destIdx, ewBytes, ReadVElement(srcData, i, ewBytes));
            destIdx++;
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVMvNr(IArchState state, int numRegs, int vd, int vs2) {
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        var snapshots = new byte[numRegs][];
        for (var i = 0; i < numRegs; i++) snapshots[i] = vregs.Read(vs2 + i);
        return new ExecuteResult {
            SideEffect = s => {
                for (var i = 0; i < numRegs; i++) ((Rv32ArchState)s).VectorRegisters.Write(vd + i, snapshots[i]);
            },
        };
    }

    private static ExecuteResult ExecuteVMaskCmp(
        IArchState state,
        VMaskCmpOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getSource
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getSource(i, ewBytes);
            if (ApplyVMaskCmp(op, a, b, ewBytes)) result[i >> 3] |= (byte)(1 << (i & 7));
        }

        return VectorWrite(vd, result);
    }

    private static ulong ApplyVIntOp(VIntOp op, ulong a, ulong b, int ewBytes) {
        int bits = ewBytes * 8;
        ulong mask = (1UL << bits) - 1;
        int shiftMask = bits - 1;
        ulong r = op switch {
            VIntOp.Add  => a + b,
            VIntOp.Sub  => a - b,
            VIntOp.Rsub => b - a, // vrsub: scalar/imm (b) minus vector element (a)
            VIntOp.And  => a & b,
            VIntOp.Or   => a | b,
            VIntOp.Xor  => a ^ b,
            VIntOp.Mov  => b, // vmv.v.v/x/i: broadcast second operand (vs1 or scalar or imm)
            VIntOp.Minu => a < b ? a : b,
            VIntOp.Maxu => a > b ? a : b,
            VIntOp.Min => ewBytes switch {
                1 => (sbyte)(byte)a < (sbyte)(byte)b ? a : b,
                2 => (short)(ushort)a < (short)(ushort)b ? a : b,
                _ => (int)(uint)a < (int)(uint)b ? a : b,
            },
            VIntOp.Max => ewBytes switch {
                1 => (sbyte)(byte)a > (sbyte)(byte)b ? a : b,
                2 => (short)(ushort)a > (short)(ushort)b ? a : b,
                _ => (int)(uint)a > (int)(uint)b ? a : b,
            },
            VIntOp.Sll => a << (int)(b & (uint)shiftMask),
            VIntOp.Srl => (a & mask) >> (int)(b & (uint)shiftMask),
            VIntOp.Sra => ewBytes switch {
                1 => (byte)((sbyte)(byte)(a & 0xFF) >> (int)(b & 7)),
                2 => (ushort)((short)(ushort)(a & 0xFFFF) >> (int)(b & 15)),
                _ => (ulong)(uint)((int)(uint)(a & 0xFFFFFFFF) >> (int)(b & 31)),
            },
            _ => 0,
        };
        return r & mask;
    }

    private static bool ApplyVMaskCmp(VMaskCmpOp op, ulong a, ulong b, int ewBytes) =>
        op switch {
            VMaskCmpOp.Eq  => a == b,
            VMaskCmpOp.Ne  => a != b,
            VMaskCmpOp.Ltu => a < b,
            VMaskCmpOp.Gtu => a > b,
            VMaskCmpOp.Lt => ewBytes switch {
                1 => (sbyte)(byte)a < (sbyte)(byte)b,
                2 => (short)(ushort)a < (short)(ushort)b,
                _ => (int)(uint)a < (int)(uint)b,
            },
            VMaskCmpOp.Gt => ewBytes switch {
                1 => (sbyte)(byte)a > (sbyte)(byte)b,
                2 => (short)(ushort)a > (short)(ushort)b,
                _ => (int)(uint)a > (int)(uint)b,
            },
            _ => false,
        };

    private static ExecuteResult ExecuteVMul(
        IArchState state,
        VMulOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getSource
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getSource(i, ewBytes);
            WriteVElement(result, i, ewBytes, ApplyVMulOp(op, a, b, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private static ulong ApplyVMulOp(VMulOp op, ulong a, ulong b, int ewBytes) {
        ulong mask = (1UL << (ewBytes * 8)) - 1;
        ulong r = op switch {
            VMulOp.Mul => a * b,
            VMulOp.MulH => ewBytes switch {
                1 => (byte)((short)((sbyte)(byte)a * (sbyte)(byte)b) >> 8),
                2 => (ushort)(((short)(ushort)a * (short)(ushort)b) >> 16),
                _ => (ulong)(uint)(((int)(uint)a * (long)(int)(uint)b) >> 32),
            },
            VMulOp.MulHu => ewBytes switch {
                1 => (byte)((ushort)((byte)a * (byte)b) >> 8),
                2 => (ushort)((uint)((ushort)a * (ushort)b) >> 16),
                _ => (ulong)(uint)(((uint)a * (ulong)(uint)b) >> 32),
            },
            // vs2 signed × vs1/rs1 unsigned, high half
            VMulOp.MulHsu => ewBytes switch {
                1 => (byte)((short)((sbyte)(byte)a * (byte)b) >> 8),
                2 => (ushort)(((short)(ushort)a * (ushort)b) >> 16),
                _ => (ulong)(uint)(((int)(uint)a * (uint)b) >> 32),
            },
            VMulOp.Div => VDivSigned(a, b, ewBytes),
            VMulOp.Divu => ewBytes switch {
                1 => (byte)b == 0 ? 0xFFUL : (ulong)((byte)a / (byte)b),
                2 => (ushort)b == 0 ? 0xFFFFUL : (ulong)((ushort)a / (ushort)b),
                _ => (uint)b == 0 ? 0xFFFFFFFFUL : (uint)a / (uint)b,
            },
            VMulOp.Rem => VRemSigned(a, b, ewBytes),
            VMulOp.Remu => ewBytes switch {
                1 => (byte)b == 0 ? a & 0xFFUL : (ulong)((byte)a % (byte)b),
                2 => (ushort)b == 0 ? a & 0xFFFFUL : (ulong)((ushort)a % (ushort)b),
                _ => (uint)b == 0 ? a & 0xFFFFFFFFUL : (uint)a % (uint)b,
            },
            _ => 0,
        };
        return r & mask;
    }

    private static ulong VDivSigned(ulong a, ulong b, int ewBytes) {
        switch (ewBytes) {
            case 1: {
                var sa = (sbyte)(byte)a;
                var sb = (sbyte)(byte)b;
                if (sb == 0) return 0xFFUL;
                if (sa == sbyte.MinValue && sb == -1) return unchecked((byte)sbyte.MinValue);
                return (byte)(sa / sb);
            }
            case 2: {
                var sa = (short)(ushort)a;
                var sb = (short)(ushort)b;
                if (sb == 0) return 0xFFFFUL;
                if (sa == short.MinValue && sb == -1) return unchecked((ushort)short.MinValue);
                return (ushort)(sa / sb);
            }
            default: {
                var sa = (int)(uint)a;
                var sb = (int)(uint)b;
                if (sb == 0) return 0xFFFFFFFFUL;
                if (sa == int.MinValue && sb == -1) return unchecked((uint)int.MinValue);
                return (uint)(sa / sb);
            }
        }
    }

    private static ulong VRemSigned(ulong a, ulong b, int ewBytes) {
        switch (ewBytes) {
            case 1: {
                var sa = (sbyte)(byte)a;
                var sb = (sbyte)(byte)b;
                if (sb == 0) return a & 0xFFUL;
                if (sa == sbyte.MinValue && sb == -1) return 0;
                return (byte)(sa % sb);
            }
            case 2: {
                var sa = (short)(ushort)a;
                var sb = (short)(ushort)b;
                if (sb == 0) return a & 0xFFFFUL;
                if (sa == short.MinValue && sb == -1) return 0;
                return (ushort)(sa % sb);
            }
            default: {
                var sa = (int)(uint)a;
                var sb = (int)(uint)b;
                if (sb == 0) return a & 0xFFFFFFFFUL;
                if (sa == int.MinValue && sb == -1) return 0;
                return (uint)(sa % sb);
            }
        }
    }

    private static ExecuteResult ExecuteVRed(
        IArchState state,
        VRedOp op,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vs1Data = VState(state).VectorRegisters.Read(vs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        ulong acc = ReadVElement(vs1Data, 0, ewBytes); // seed from vs1[0]

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            acc = ApplyVRedOp(op, acc, ReadVElement(vs2Data, i, ewBytes), ewBytes);
        }

        // Write result to element 0 of vd; all other elements are undefined (left zero).
        var result = new byte[VectorRegisterFile.VLenB];
        WriteVElement(result, 0, ewBytes, acc);
        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVWideRed(
        IArchState state,
        bool signed,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int wBytes = ewBytes * 2;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vs1Data = VState(state).VectorRegisters.Read(vs1);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        ulong acc = ReadVElement(vs1Data, 0, wBytes); // seed from vs1[0] at 2×SEW

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong elem = ReadVElement(vs2Data, i, ewBytes);
            acc += signed ? (ulong)Sx(elem) : elem;
        }

        var result = new byte[VectorRegisterFile.VLenB];
        WriteVElement(result, 0, wBytes, acc);
        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, _ => (int)(uint)v,
        };
    }

    private static ulong ApplyVRedOp(VRedOp op, ulong acc, ulong elem, int ewBytes) {
        ulong mask = (1UL << (ewBytes * 8)) - 1;
        ulong r = op switch {
            VRedOp.Sum  => acc + elem,
            VRedOp.And  => acc & elem,
            VRedOp.Or   => acc | elem,
            VRedOp.Xor  => acc ^ elem,
            VRedOp.Minu => (acc & mask) < (elem & mask) ? acc : elem,
            VRedOp.Maxu => (acc & mask) > (elem & mask) ? acc : elem,
            VRedOp.Min => ewBytes switch {
                1 => (sbyte)(byte)acc < (sbyte)(byte)elem ? acc : elem,
                2 => (short)(ushort)acc < (short)(ushort)elem ? acc : elem,
                _ => (int)(uint)acc < (int)(uint)elem ? acc : elem,
            },
            VRedOp.Max => ewBytes switch {
                1 => (sbyte)(byte)acc > (sbyte)(byte)elem ? acc : elem,
                2 => (short)(ushort)acc > (short)(ushort)elem ? acc : elem,
                _ => (int)(uint)acc > (int)(uint)elem ? acc : elem,
            },
            _ => acc,
        };
        return r & mask;
    }

    // vmv.x.s rd, vs2 — read element 0 of vs2 into integer rd (sign-extended to XLEN).
    private static ExecuteResult ExecuteVMvXs(IArchState state, int rd, int vs2) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (vl == 0 || rd == 0) return ExecuteResult.Clean;
        ulong elem = VReadElem(state, vs2, 0, ewBytes);
        ulong result = ewBytes switch {
            1 => (ulong)(sbyte)(byte)elem,
            2 => (ulong)(short)(ushort)elem,
            _ => elem,
        };
        return ExecuteResult.WithResult(result);
    }

    // vmacc/vnmsac: vd[i] = vd[i] ± vs2[i]*vs1[i]; vd is both source and destination.
    private static ExecuteResult ExecuteVIntMac(
        IArchState state,
        VIntMacOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vdData = VState(state).VectorRegisters.Read(vd);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdData, result, vdData.Length);

        ulong elemMask = (1UL << (ewBytes * 8)) - 1;
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong acc = ReadVElement(vdData, i, ewBytes);
            ulong r = op switch {
                VIntMacOp.Macc  => acc + a * b,
                VIntMacOp.Nmsac => acc - a * b,
                VIntMacOp.Madd  => a + acc * b,
                VIntMacOp.Nmsub => a - acc * b,
                _               => acc,
            };
            WriteVElement(result, i, ewBytes, r & elemMask);
        }

        return VectorWrite(vd, result);
    }

    // vmv.s.x: write integer register value into element 0 of vector register vd.
    private static ExecuteResult ExecuteVMvSx(IArchState state, int vd, ulong rs1Val) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (vl == 0) return ExecuteResult.Clean;
        byte[] current = VState(state).VectorRegisters.Read(vd);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(current, result, current.Length);
        WriteVElement(result, 0, ewBytes, rs1Val);
        return VectorWrite(vd, result);
    }

    // vmerge: for each active element, mask=1 → active source, mask=0 → vs2[i].
    private static ExecuteResult ExecuteVMerge(
        IArchState state,
        int vd,
        int vs2,
        Func<int, int, ulong> getActiveSrc
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            bool bit = ((mask[i >> 3] >> (i & 7)) & 1) == 1;
            ulong src = bit ? getActiveSrc(i, ewBytes) : ReadVElement(vs2Data, i, ewBytes);
            WriteVElement(result, i, ewBytes, src);
        }

        return VectorWrite(vd, result);
    }
}