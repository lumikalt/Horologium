namespace Mechanism.BranchPredictModels;

/// <summary>
/// Fixed-size circular LIFO stack for function return address prediction.
///
/// Speculative pushes/pops are not checkpointed — a pipeline flush leaves
/// the stack transiently wrong. It self-corrects within a call depth.
/// </summary>
public sealed class ReturnAddressStack {
    private readonly ulong[] _entries;
    private int _top;   // index of most-recently-pushed entry
    private int _count; // number of valid entries

    public int Depth { get; }

    public ReturnAddressStack(int depth = 16) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        Depth = depth;
        _entries = new ulong[depth];
    }

    public void Push(ulong returnAddress) {
        _top = (_top + 1) % Depth;
        _entries[_top] = returnAddress;
        if (_count < Depth) _count++;
    }

    public bool TryPop(out ulong returnAddress) {
        if (_count == 0) {
            returnAddress = 0;
            return false;
        }
        returnAddress = _entries[_top];
        _top = (_top + Depth - 1) % Depth;
        _count--;
        return true;
    }
}
