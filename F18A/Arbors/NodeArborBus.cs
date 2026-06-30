using F18A.MultiCore;

namespace F18A.Arbors;

/// <summary>
/// Connects one F18A node to its four directional <see cref="RendezvousArbor"/>s.
/// Maps port-space word-addresses to North/East/South/West channels.
/// </summary>
public sealed class NodeArborBus(F18ANode node) : IArborBus {
    // Port word-addresses (matching F18ANode constants)
    private const uint NR = F18ANode.PortAddrNorthRead;
    private const uint NW = F18ANode.PortAddrNorthWrite;
    private const uint ER = F18ANode.PortAddrEastRead;
    private const uint EW = F18ANode.PortAddrEastWrite;
    private const uint SR = F18ANode.PortAddrSouthRead;
    private const uint SW = F18ANode.PortAddrSouthWrite;
    private const uint WR = F18ANode.PortAddrWestRead;
    private const uint WW = F18ANode.PortAddrWestWrite;

    public bool TryRead(uint wordAddr, out uint value) {
        RendezvousArbor? arbor = ReadArbor(wordAddr);
        if (arbor is null) {
            value = 0;
            return true;
        }

        return arbor.TryRead(out value);
    }

    public bool TryWrite(uint wordAddr, uint value) {
        RendezvousArbor? arbor = WriteArbor(wordAddr);
        if (arbor is null) return true;
        return arbor.TryWrite(value);
    }

    public bool IsReady(uint wordAddr, bool isRead) {
        RendezvousArbor? arbor = isRead ? ReadArbor(wordAddr) : WriteArbor(wordAddr);
        if (arbor is null) return true;
        return arbor.HasPending(isRead);
    }

    private RendezvousArbor? ReadArbor(uint addr) => addr switch {
        NodeArborBus.NR => node.NorthArbor, NodeArborBus.ER => node.EastArbor,
        NodeArborBus.SR => node.SouthArbor, NodeArborBus.WR => node.WestArbor,
        _               => null,
    };

    private RendezvousArbor? WriteArbor(uint addr) => addr switch {
        NodeArborBus.NW => node.NorthArbor, NodeArborBus.EW => node.EastArbor,
        NodeArborBus.SW => node.SouthArbor, NodeArborBus.WW => node.WestArbor,
        _               => null,
    };
}