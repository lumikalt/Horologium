#region

using Mechanism;
using RiscV32.Decode;
using RiscV32.Registers;

#endregion

// ReSharper disable CompareOfFloatsByEqualityOperator

namespace RiscV32.Execute;

public partial class Rv32Executor {
    // ── V widening FP helpers ─────────────────────────────────────────────────

    private const ulong RvCanonicalNaN64 = 0x7FF8_0000_0000_0000UL;
    // ── FP vector helpers ─────────────────────────────────────────────────────

    // Read element i of a vector register as a float32 (bit-exact).
    private static float VFpElem(IArchState state, int vreg, int i) {
        byte[] data = VState(state).VectorRegisters.Read(vreg);
        var bits = (uint)(data[i * 4] | (data[i * 4 + 1] << 8) | (data[i * 4 + 2] << 16) | (data[i * 4 + 3] << 24));
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    private static void WriteVFpElem(byte[] data, int i, float value) {
        uint bits = float.IsNaN(value) ? Rv32Executor.RvCanonicalNaN : (uint)BitConverter.SingleToInt32Bits(value);
        int off = i * 4;
        data[off] = (byte)bits;
        data[off + 1] = (byte)(bits >> 8);
        data[off + 2] = (byte)(bits >> 16);
        data[off + 3] = (byte)(bits >> 24);
    }

    private static float ApplyVFpBin(VFpBinOp op, float a, float b) => op switch {
        VFpBinOp.Add => a + b,
        VFpBinOp.Sub => a - b,
        VFpBinOp.Mul => a * b,
        VFpBinOp.Div => a / b,
        VFpBinOp.Min => FMin(a, b),
        VFpBinOp.Max => FMax(a, b),
        VFpBinOp.Sgnj => BitConverter.Int32BitsToSingle(
            (BitConverter.SingleToInt32Bits(a) & 0x7FFFFFFF)
          | (BitConverter.SingleToInt32Bits(b) & unchecked((int)0x80000000))
        ),
        VFpBinOp.Sgnjn => BitConverter.Int32BitsToSingle(
            (BitConverter.SingleToInt32Bits(a) & 0x7FFFFFFF)
          | (~BitConverter.SingleToInt32Bits(b) & unchecked((int)0x80000000))
        ),
        VFpBinOp.Sgnjx => BitConverter.Int32BitsToSingle(
            (BitConverter.SingleToInt32Bits(a) & 0x7FFFFFFF) |
            ((BitConverter.SingleToInt32Bits(a) ^ BitConverter.SingleToInt32Bits(b)) & unchecked((int)0x80000000))
        ),
        _ => float.NaN,
    };

    private static ExecuteResult ExecuteVFpBin(
        IArchState state,
        VFpBinOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            float b = getB(i);
            WriteVFpElem(result, i, ApplyVFpBin(op, a, b));
        }

        return VectorWrite(vd, result);
    }

    // FMA semantics for each op variant.
    private static float ApplyVFpFma(VFpFmaOp op, float vdElem, float a, float b) => op switch {
        // vfmacc:  vd = vd + a*b       vfmadd:  vd = vd*a + b
        VFpFmaOp.Macc  => vdElem + a * b,
        VFpFmaOp.Nmacc => -vdElem - a * b,
        VFpFmaOp.Msac  => vdElem - a * b,
        VFpFmaOp.Nmsac => -vdElem + a * b,
        VFpFmaOp.Madd  => vdElem * a + b,
        VFpFmaOp.Nmadd => -(vdElem * a) - b,
        VFpFmaOp.Msub  => vdElem * a - b,
        VFpFmaOp.Nmsub => -(vdElem * a) + b,
        _              => float.NaN,
    };

    private static ExecuteResult ExecuteVFpFma(
        IArchState state,
        VFpFmaOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vdData = VState(state).VectorRegisters.Read(vd);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float acc = BitConverter.Int32BitsToSingle((int)ReadVElement(vdData, i, 4));
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            float b = getB(i);
            WriteVFpElem(result, i, ApplyVFpFma(op, acc, a, b));
        }

        return VectorWrite(vd, result);
    }

    private static bool ApplyVFpCmp(VFpCmpOp op, float a, float b) => op switch {
        VFpCmpOp.Eq => !float.IsNaN(a) && !float.IsNaN(b) && a == b,
        VFpCmpOp.Le => !float.IsNaN(a) && !float.IsNaN(b) && a <= b,
        VFpCmpOp.Lt => !float.IsNaN(a) && !float.IsNaN(b) && a < b,
        VFpCmpOp.Ne => float.IsNaN(a) || float.IsNaN(b) || a != b,
        VFpCmpOp.Gt => !float.IsNaN(a) && !float.IsNaN(b) && a > b,
        VFpCmpOp.Ge => !float.IsNaN(a) && !float.IsNaN(b) && a >= b,
        _           => false,
    };

    // FP compare: result is 1 mask bit per element packed in vd.
    private static ExecuteResult ExecuteVFpCmp(
        IArchState state,
        VFpCmpOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            float b = getB(i);
            if (ApplyVFpCmp(op, a, b)) result[i >> 3] |= (byte)(1 << (i & 7));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpUnary(
        IArchState state,
        int vd,
        int vs2,
        bool masked,
        Func<float, float> f
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            WriteVFpElem(result, i, f(a));
        }

        return VectorWrite(vd, result);
    }

    // vfclass.v: classify each element, write the 10-bit mask as a float-width integer.
    private static ExecuteResult ExecuteVFpClassOp(IArchState state, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var bits = (uint)ReadVElement(vs2Data, i, 4);
            WriteVElement(result, i, 4, FClass(bits));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpCvt(IArchState state, VFpCvtOp op, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var src = (uint)ReadVElement(vs2Data, i, 4);
            ulong val = op switch {
                VFpCvtOp.XuFromF    => FcvtWuS(BitConverter.Int32BitsToSingle((int)src)),
                VFpCvtOp.XFromF     => FcvtWs(BitConverter.Int32BitsToSingle((int)src)),
                VFpCvtOp.FFromXu    => (uint)BitConverter.SingleToInt32Bits(src),
                VFpCvtOp.FFromX     => (uint)BitConverter.SingleToInt32Bits((int)src),
                VFpCvtOp.RtzXuFromF => FcvtWuS(BitConverter.Int32BitsToSingle((int)src)),
                VFpCvtOp.RtzXFromF  => FcvtWs(BitConverter.Int32BitsToSingle((int)src)),
                _                   => 0,
            };
            WriteVElement(result, i, 4, val);
        }

        return VectorWrite(vd, result);
    }

    // vfmv.f.s: scalar float rd ← vs2[0]
    private static ExecuteResult ExecuteVFpMvFs(IArchState state, int vs2) {
        byte[] data = VState(state).VectorRegisters.Read(vs2);
        var elem0 = (uint)ReadVElement(data, 0, 4);
        return ExecuteResult.WithResult(elem0);
    }

    // vfmv.s.f: vd[0] ← scalar float rs1; other elements undisturbed
    private static ExecuteResult ExecuteVFpMvSf(IArchState state, IRegisterFile regs, int vd, int rs1) {
        float scalar = FBits(regs, rs1);
        var current = (byte[])VState(state).VectorRegisters.Read(vd).Clone();
        WriteVFpElem(current, 0, scalar);
        return VectorWrite(vd, current);
    }

    // vfmv.v.f: broadcast scalar float to all active elements
    private static ExecuteResult ExecuteVFpMvVf(IArchState state, int vd, float scalar, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            WriteVFpElem(result, i, scalar);
        }

        return VectorWrite(vd, result);
    }

    // vfredusum/vfredosum/vfredmin/vfredmax: reduce vs2 into scalar vd[0]; vs1[0] seeds the accumulator.
    private static ExecuteResult ExecuteVFpRed(
        IArchState state,
        VFpRedOp op,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vs1Data = vregs.Read(vs1);
        byte[] mask = vregs.Read(0);
        float acc = BitConverter.Int32BitsToSingle((int)ReadVElement(vs1Data, 0, 4));

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float elem = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            acc = op switch {
                VFpRedOp.Usum or VFpRedOp.Osum => acc + elem,
                VFpRedOp.Min                   => FMin(acc, elem),
                VFpRedOp.Max                   => FMax(acc, elem),
                _                              => acc,
            };
        }

        var result = new byte[VectorRegisterFile.VLenB];
        WriteVFpElem(result, 0, acc);
        return VectorWrite(vd, result);
    }

    private static void WriteVFpElemD(byte[] data, int i, double value) {
        ulong bits = double.IsNaN(value) ? Rv32Executor.RvCanonicalNaN64 : (ulong)BitConverter.DoubleToInt64Bits(value);
        WriteVElement(data, i, 8, bits);
    }

    // f32 → u64 saturating (NaN or negative → 0; overflow → MaxValue)
    private static ulong VFcvtXuFromF32(float f) =>
        f switch {
            float.NaN or < 0f          => 0,
            >= 1.8446744073709552E+19f => ulong.MaxValue,
            _                          => (ulong)f,
        };

    // f32 → i64 saturating
    private static ulong VFcvtXFromF32(float f) =>
        f switch {
            float.NaN or >= 9.2233720368547758E+18f => long.MaxValue,
            < -9.2233720368547758E+18f              => unchecked((ulong)long.MinValue),
            _                                       => (ulong)(long)f,
        };

    // f64 → u32 saturating
    private static ulong VFcvtXuFromF64(double d) =>
        d switch {
            double.NaN or < 0.0 => 0,
            >= 4294967296.0     => uint.MaxValue,
            _                   => (uint)d,
        };

    // f64 → i32 saturating
    private static ulong VFcvtXFromF64(double d) =>
        d switch {
            double.NaN or >= 2147483648.0 => int.MaxValue,
            < -2147483648.0               => unchecked((uint)int.MinValue),
            _                             => (uint)(int)d,
        };

    // vfncvt.rod.f.f.w: f64 → f32, round-to-odd (if inexact, force mantissa LSB=1)
    private static float VFcvtRodF32FromF64(double d) {
        if (double.IsNaN(d)) return BitConverter.Int32BitsToSingle(2143289344);
        var f = (float)d;
        if (float.IsInfinity(f) || f == d) return f;
        return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(f) | 1);
    }

    private static ExecuteResult ExecuteVFpWArith(
        IArchState state,
        VFpWideArithOp op,
        int vd,
        int vs2,
        bool vs2Wide,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwArith: only SEW=32 supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            double a = vs2Wide
                ? BitConverter.Int64BitsToDouble((long)ReadVElement(vs2Data, i, 8))
                : BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            var b = (double)getB(i);
            double res = op switch {
                VFpWideArithOp.Add => a + b,
                VFpWideArithOp.Sub => a - b,
                VFpWideArithOp.Mul => a * b,
                _                  => double.NaN,
            };
            WriteVFpElemD(result, i, res);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpWMac(
        IArchState state,
        VFpWMacOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwMac: only SEW=32 supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vdData = vregs.Read(vd);
        byte[] mask = vregs.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdData, result, vdData.Length);

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            double acc = BitConverter.Int64BitsToDouble((long)ReadVElement(result, i, 8));
            var a = (double)BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            var b = (double)getB(i);
            double res = op switch {
                VFpWMacOp.Macc  => acc + a * b,
                VFpWMacOp.Nmacc => -acc - a * b,
                VFpWMacOp.Msac  => a * b - acc,
                VFpWMacOp.Nmsac => acc - a * b,
                _               => double.NaN,
            };
            WriteVFpElemD(result, i, res);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpWCvt(IArchState state, VFpWCvtOp op, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwcvt: only SEW=32 supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var src = (uint)ReadVElement(vs2Data, i, 4);
            float srcF = BitConverter.Int32BitsToSingle((int)src);
            ulong val = op switch {
                VFpWCvtOp.XuFromF    => VFcvtXuFromF32(srcF),
                VFpWCvtOp.XFromF     => VFcvtXFromF32(srcF),
                VFpWCvtOp.FFromXu    => (ulong)BitConverter.DoubleToInt64Bits(src),
                VFpWCvtOp.FFromX     => (ulong)BitConverter.DoubleToInt64Bits((int)src),
                VFpWCvtOp.FFromF     => (ulong)BitConverter.DoubleToInt64Bits(srcF),
                VFpWCvtOp.RtzXuFromF => VFcvtXuFromF32(srcF),
                VFpWCvtOp.RtzXFromF  => VFcvtXFromF32(srcF),
                _                    => 0,
            };
            WriteVElement(result, i, 8, val);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpNCvt(IArchState state, VFpNCvtOp op, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfncvt: only SEW=32 (output) supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong raw64 = ReadVElement(vs2Data, i, 8);
            double srcD = BitConverter.Int64BitsToDouble((long)raw64);
            ulong val = op switch {
                VFpNCvtOp.XuFromF    => VFcvtXuFromF64(srcD),
                VFpNCvtOp.XFromF     => VFcvtXFromF64(srcD),
                VFpNCvtOp.FFromXu    => (uint)BitConverter.SingleToInt32Bits(raw64),
                VFpNCvtOp.FFromX     => (uint)BitConverter.SingleToInt32Bits((long)raw64),
                VFpNCvtOp.FFromF     => (uint)BitConverter.SingleToInt32Bits((float)srcD),
                VFpNCvtOp.RodFFromF  => (uint)BitConverter.SingleToInt32Bits(VFcvtRodF32FromF64(srcD)),
                VFpNCvtOp.RtzXuFromF => VFcvtXuFromF64(srcD),
                VFpNCvtOp.RtzXFromF  => VFcvtXFromF64(srcD),
                _                    => 0,
            };
            WriteVElement(result, i, 4, val);
        }

        return VectorWrite(vd, result);
    }
}