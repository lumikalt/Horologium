namespace Mechanism.BranchPredictModels;

/// <summary>
/// Fixed-size circular LIFO stack for function return address prediction.
/// <para>
/// Speculative pushes/pops are not checkpointed — a pipeline flush leaves
/// the stack transiently wrong. It self-corrects within a call depth.
/// </para>
/// </summary>
public sealed class ReturnAddressStack {
    private readonly ulong[] _entries;
    private int _top;   // index of most-recently-pushed entry
    private int _count; // number of valid entries

    private int Depth { get; }

    /// <summary>
    /// Creates a new stack with the given depth.
    /// </summary>
    /// <param name="depth">
    /// Maximum number of entries.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> ≤ 0.</exception>
    public ReturnAddressStack(int depth = 16) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        Depth = depth;
        _entries = new ulong[depth];
    }

    /// <summary>
    /// Push a return address onto the stack.
    /// </summary>
    /// <param name="returnAddress">Address to push.</param>
    public void Push(ulong returnAddress) {
        _top = (_top + 1) % Depth;
        _entries[_top] = returnAddress;
        if (_count < Depth) _count++;
    }

    /// <summary>
    /// Tries to pop a return address from the stack.
    /// </summary>
    /// <param name="returnAddress">Address to pop.</param>
    /// <returns>
    /// True if a return address was popped.
    /// </returns>
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

    /// <summary>
    /// Overwrites this stack's contents with a copy of <paramref name="other"/>.
    /// Used to restore the speculative stack from a committed (architectural) shadow
    /// on a pipeline flush, discarding wrong-path corruption. Both stacks must share
    /// the same depth.
    /// </summary>
    public void CopyFrom(ReturnAddressStack other) {
        Array.Copy(other._entries, _entries, Math.Min(_entries.Length, other._entries.Length));
        _top = other._top;
        _count = other._count;
    }
}