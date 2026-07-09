using Mechanism;

namespace Chip8.Registers;

public class DataRegisterFile : IRegisterFile {
    private readonly byte[] _data = new byte[16];
    public int Count => 16;
    public int Width => 8;

    public ulong Read(int index) => _data[index];
    public void Write(int index, ulong value) { _data[index] = (byte)value; }
}