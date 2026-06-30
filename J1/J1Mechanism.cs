using Mechanism;
using J1.Decode;
using J1.Execute;

namespace J1;

public sealed class J1Mechanism : IMechanism {
    public string Name => "J1 Forth";
    public IDecoder Decoder { get; } = new J1Decoder();
    public IExecutor Executor { get; } = new J1Executor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new J1TrapController();

    public IArchState CreateArchState() => new J1ArchState();
}
