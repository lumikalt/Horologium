using F18A.Decode;
using F18A.Execute;
using Mechanism;

namespace F18A;

public sealed class F18AMechanism : IMechanism {
    public string Name => "F18A";
    public IDecoder Decoder { get; } = new F18ADecoder();
    public IExecutor Executor { get; } = new F18AExecutor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new F18ATrapController();

    public IArchState CreateArchState() => new F18AArchState();
}