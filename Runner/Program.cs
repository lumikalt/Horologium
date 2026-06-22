using RiscV;
using RiscV.Memory;
using RiscV.Trains;

static FlatMemory Mem(params uint[] words) {
    var mem = new FlatMemory(4096);
    var bytes = new byte[words.Length * 4];
    for (var i = 0; i < words.Length; i++) {
        bytes[i * 4 + 0] = (byte)words[i];
        bytes[i * 4 + 1] = (byte)(words[i] >> 8);
        bytes[i * 4 + 2] = (byte)(words[i] >> 16);
        bytes[i * 4 + 3] = (byte)(words[i] >> 24);
    }
    mem.Load(0, bytes);
    return mem;
}

FlatMemory mem = Mem(
    0x00000093, // addi x1, x0, 0
    0x00500113, // addi x2, x0, 5
    0x00108093, // addi x1, x1, 1   ← loop (addr 8)
    0xFE20CEE3, // blt  x1, x2, -4
    0x00100073  // ebreak
);

var train = new FiveStageTrain(new RvMechanism(), mem, 0, true, null);
var result = train.Run();
Console.WriteLine($"x1={train.ArchState.IntegerRegisters.Read(1)} (expect 5)");
Console.WriteLine(result);
