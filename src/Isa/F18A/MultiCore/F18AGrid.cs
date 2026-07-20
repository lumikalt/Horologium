#region

using F18A.Arbors;
using F18A.Decode;
using F18A.Execute;
using Mechanism;

#endregion

namespace F18A.MultiCore;

/// <summary>
///     A rectangular grid of F18A nodes wired together with rendezvous arbors.
///     Each call to <see cref="Step" /> advances all non-blocked nodes by one instruction word.
///     <para>
///         Arbor rendezvous is resolved in two sub-phases per tick:
///         <list type="number">
///             <item>
///                 <description><em>Arbor reset</em>: all nodes call BeginTick; arbors discard stale state.</description>
///             </item>
///             <item>
///                 <description>
///                     <em>Readiness check</em>: each node declares intent; nodes whose arbor will
///                     block are skipped this tick.
///                 </description>
///             </item>
///             <item>
///                 <description><em>Execution</em>: ready nodes decode → execute → commit.</description>
///             </item>
///         </list>
///     </para>
///     <para>
///         Nodes that need an arbor partner to proceed will retry on subsequent ticks until
///         both sides arrive in the same tick.
///     </para>
/// </summary>
public sealed class F18AGrid {
    private readonly F18ADecoder _decoder = new();
    private readonly F18AExecutor _executor = new();
    private readonly RendezvousArbor[,] _hArbors; // horizontal: [row, col] = arbor between (row,col)→(row,col+1)
    private readonly F18ANode[,] _nodes;
    private readonly RendezvousArbor[,] _vArbors; // vertical:   [row, col] = arbor between (row,col)→(row+1,col)

    public F18AGrid(int rows, int cols) {
        Rows = rows;
        Cols = cols;
        _nodes = new F18ANode[rows, cols];
        _hArbors = new RendezvousArbor[rows, cols - 1];
        _vArbors = new RendezvousArbor[rows - 1, cols];

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            _nodes[r, c] = new F18ANode(r, c, ReadOnlySpan<byte>.Empty);

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols - 1; c++) {
            RendezvousArbor a = _hArbors[r, c] = new RendezvousArbor();
            _nodes[r, c].EastArbor = a;
            _nodes[r, c + 1].WestArbor = a;
        }

        for (var r = 0; r < rows - 1; r++)
        for (var c = 0; c < cols; c++) {
            RendezvousArbor a = _vArbors[r, c] = new RendezvousArbor();
            _nodes[r, c].SouthArbor = a;
            _nodes[r + 1, c].NorthArbor = a;
        }
    }

    public int Rows { get; }
    public int Cols { get; }
    public long Ticks { get; private set; }

    public F18ANode NodeAt(int row, int col) => _nodes[row, col];

    /// <summary>
    ///     Advance all nodes by one instruction word.
    ///     Returns the number of nodes that actually executed (vs blocked on an arbor).
    /// </summary>
    public int Step() {
        // Phase 1: reset all arbors for this tick
        foreach (RendezvousArbor a in _hArbors) a.BeginTick();
        foreach (RendezvousArbor a in _vArbors) a.BeginTick();

        var executed = 0;
        for (var r = 0; r < Rows; r++)
        for (var c = 0; c < Cols; c++) {
            F18ANode node = _nodes[r, c];
            if (node.WillBlock(_decoder)) continue;
            StepNode(node);
            executed++;
        }

        Ticks++;
        return executed;
    }

    /// <summary>Step until all nodes are halted or <paramref name="maxTicks" /> is reached.</summary>
    public void Run(long maxTicks = 1_000_000) {
        while (Ticks < maxTicks) {
            int ran = Step();
            if (ran == 0) break; // all nodes either halted or blocked
        }
    }

    private void StepNode(F18ANode node) {
        var insn = (F18AInstruction)_decoder.Decode(node.State.Pc, node.Memory);
        ExecuteResult result = _executor.Execute(insn, node.State, node.Memory);
        result.SideEffect?.Invoke(node.State);
        if (result.IsHalt) return; // leave PC as-is; node is done
        node.State.Pc = result.BranchTaken
            ? result.BranchTarget!.Value
            : node.State.Pc + 4;
    }
}