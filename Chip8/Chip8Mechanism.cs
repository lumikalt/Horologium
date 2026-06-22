using Chip8.Decode;
using Chip8.Execute;
using Mechanism;

namespace Chip8;

public sealed class Chip8Mechanism : IMechanism {
    public string Name => "CHIP-8";
    public IDecoder Decoder { get; } = new Decoder();
    public IExecutor Executor { get; } = new Chip8Executor();
    public IUopCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new Chip8TrapController();

    public IArchState CreateArchState() => new Chip8ArchState();
}