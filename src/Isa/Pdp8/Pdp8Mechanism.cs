#region

using Mechanism;
using Pdp8.Decode;
using Pdp8.Execute;

#endregion

namespace Pdp8;

public sealed class Pdp8Mechanism : IMechanism {
    public string Name => "PDP-8";
    public IDecoder Decoder { get; } = new Pdp8Decoder();
    public IExecutor Executor { get; } = new Pdp8Executor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new Pdp8TrapController();
    public IArchState CreateArchState() => new Pdp8ArchState();
}