namespace RiscV.Registers;

public sealed class VectorRegisterFile {
    public const int VLen = 128; // bits
    public const int VLenB = 16; // bytes per register
    public const int Count = 32;

    private readonly byte[][] _regs = new byte[VectorRegisterFile.Count][];

    public VectorRegisterFile() {
        for (var i = 0; i < VectorRegisterFile.Count; i++) _regs[i] = new byte[VectorRegisterFile.VLenB];
    }

    public byte[] Read(int vr) {
        var copy = new byte[VectorRegisterFile.VLenB];
        Array.Copy(_regs[vr], copy, VectorRegisterFile.VLenB);
        return copy;
    }

    public void Write(int vr, byte[] data) =>
        Array.Copy(data, _regs[vr], Math.Min(data.Length, VectorRegisterFile.VLenB));

    public void Reset() {
        foreach (byte[] r in _regs) Array.Clear(r, 0, VectorRegisterFile.VLenB);
    }
}