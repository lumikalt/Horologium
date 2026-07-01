namespace Orrery.Cache;

public enum MesiState : byte {
    Invalid,
    Shared,
    Exclusive,
    Modified,
}
