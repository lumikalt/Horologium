#region

using Mechanism;
using Move.Decode;
using Move.Execute;

#endregion

namespace Move;

public sealed class MoveMechanism : IMechanism {
    public string Name => "TTA/MOVE";
    public IDecoder Decoder { get; } = new MoveDecoder();
    public IExecutor Executor { get; } = new MoveExecutor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new MoveTrapController();

    public IArchState CreateArchState() => new MoveArchState();
}