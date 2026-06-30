using Mechanism;
using Subleq.Decode;
using Subleq.Execute;

namespace Subleq;

public sealed class SubleqMechanism : IMechanism {
    public string Name => "SUBLEQ";
    public IDecoder Decoder { get; } = new SubleqDecoder();
    public IExecutor Executor { get; } = new SubleqExecutor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new SubleqTrapController();

    public IArchState CreateArchState() => new SubleqArchState();
}