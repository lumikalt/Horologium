using F18A.MultiCore;

namespace F18A.Arbors;

/// <summary>
///     Connects one F18A node to its four directional <see cref="RendezvousArbor" />s.
///     Maps port-space word-addresses to North/East/South/West channels.
/// </summary>
public sealed class NodeArborBus(F18ANode node) : IArborBus {
    // Port word-addresses (matching F18ANode constants)
    private const uint Nr = F18ANode.PortAddrNorthRead;
    private const uint Nw = F18ANode.PortAddrNorthWrite;
    private const uint Er = F18ANode.PortAddrEastRead;
    private const uint Ew = F18ANode.PortAddrEastWrite;
    private const uint Sr = F18ANode.PortAddrSouthRead;
    private const uint Sw = F18ANode.PortAddrSouthWrite;
    private const uint Wr = F18ANode.PortAddrWestRead;
    private const uint Ww = F18ANode.PortAddrWestWrite;

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
        return arbor is null || arbor.TryWrite(value);
    }

    public bool IsReady(uint wordAddr, bool isRead) {
        RendezvousArbor? arbor = isRead ? ReadArbor(wordAddr) : WriteArbor(wordAddr);
        return arbor is null || arbor.HasPending(isRead);
    }

    private RendezvousArbor? ReadArbor(uint addr) => addr switch {
        NodeArborBus.Nr => node.NorthArbor, NodeArborBus.Er => node.EastArbor,
        NodeArborBus.Sr => node.SouthArbor, NodeArborBus.Wr => node.WestArbor,
        _               => null,
    };

    private RendezvousArbor? WriteArbor(uint addr) => addr switch {
        NodeArborBus.Nw => node.NorthArbor, NodeArborBus.Ew => node.EastArbor,
        NodeArborBus.Sw => node.SouthArbor, NodeArborBus.Ww => node.WestArbor,
        _               => null,
    };
}