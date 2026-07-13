using F18A.Arbors;
using F18A.Decode;
using F18A.Memory;

namespace F18A.MultiCore;

/// <summary>
///     One F18A processing node with its own RAM+ROM, arch state, and four directional arbors.
///     Stepped by <see cref="F18AGrid" /> which coordinates arbor rendezvous across nodes.
///     <para>
///         Constraint: at most one @b / !b port-op per instruction word, and no memory
///         stores before a blocking arbor op in the same word.  Programs that violate this
///         may observe non-deterministic behaviour on blocked-arbor retries.
///     </para>
/// </summary>
public sealed class F18ANode {
    // Port word-addresses visible via @b / !b when B is set to these values.
    // These match the GA144 sheet address map for the inter-node ports.
    public const uint PortAddrNorthRead = 0x1C1;
    public const uint PortAddrNorthWrite = 0x1C5;
    public const uint PortAddrEastRead = 0x1A1;
    public const uint PortAddrEastWrite = 0x1A5;
    public const uint PortAddrSouthRead = 0x181;
    public const uint PortAddrSouthWrite = 0x185;
    public const uint PortAddrWestRead = 0x141;
    public const uint PortAddrWestWrite = 0x145;

    public F18ANode(int row, int col, ReadOnlySpan<byte> rom) {
        Row = row;
        Col = col;
        State = new F18AArchState();
        Memory = new F18ANodeMemory();
        Memory.ArborBus = new NodeArborBus(this);
        // Load ROM into word-addresses 64–127
        if (rom.Length > 0) {
            Memory.Load(64 * 4, rom);
            State.Pc = 64 * 4; // start execution from ROM
        }
    }

    public int Row { get; }
    public int Col { get; }

    public F18AArchState State { get; }
    public F18ANodeMemory Memory { get; }

    // Arbors; null = no neighbour on that side
    public RendezvousArbor? NorthArbor { get; set; }
    public RendezvousArbor? EastArbor { get; set; }
    public RendezvousArbor? SouthArbor { get; set; }
    public RendezvousArbor? WestArbor { get; set; }

    /// <summary>
    ///     Checks whether the current instruction will block on an arbor access.
    ///     The grid calls this before deciding to step the node.
    /// </summary>
    public bool WillBlock(F18ADecoder decoder) {
        var insn = (F18AInstruction)decoder.Decode(State.Pc, Memory);
        return !CanProceed(insn);
    }

    private bool CanProceed(F18AInstruction insn) {
        byte[] slots = [insn.Slot0, insn.Slot1, insn.Slot2, insn.Slot3,];
        foreach (byte op in slots) {
            switch (op) {
                case F18AOp.FetchB: {
                    uint wordAddr = State.B;
                    if (wordAddr >= F18ANodeMemory.PortBase) return Memory.IsPortReady(wordAddr, true);
                    break;
                }
                case F18AOp.StoreB: {
                    uint wordAddr = State.B;
                    if (wordAddr >= F18ANodeMemory.PortBase) return Memory.IsPortReady(wordAddr, false);
                    break;
                }
            }

            // Stop scanning if we hit any control-flow op (later slots won't execute)
            if (op is F18AOp.Return or F18AOp.Jump or F18AOp.Call
                   or F18AOp.Unext or F18AOp.Next or F18AOp.If or F18AOp.MinusIf)
                break;
        }

        return true;
    }
}