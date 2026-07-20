#region

using Mechanism;

#endregion

namespace Orrery.Devices;

/// <summary>
///     Memory-mapped UART modelled after the SiFive FE310-G002 UART0 register map
///     (§16 of the FE310-G002 manual). TX bytes flush immediately to <see cref="Output" />;
///     RX bytes are supplied via <see cref="Enqueue" />. TX is never reported as full;
///     RX reports empty (bit 31 set) when the queue is empty.
///     <para>
///         Register offsets from the base address in the containing <see cref="PeripheralBus" />:
///         0x00 txdata — bits[7:0] = byte to transmit; bit[31] = TX full (always 0 here).
///         0x04 rxdata — bits[7:0] = received byte; bit[31] = RX empty (1 when queue empty).
///         0x08 txctrl — bit[0] = txen (always 1).
///         0x0C rxctrl — bit[0] = rxen (1 when RX queue is non-empty).
///         0x10 ie     — interrupt enable (reads 0; writes ignored).
///         0x14 ip     — interrupt pending (reads 0).
///         0x18 div    — baud-rate divisor (reads 0; writes ignored).
///     </para>
/// </summary>
public sealed class UartDevice(TextWriter output) : IMemory {
    public const ulong DefaultBase = 0x10013000;
    public const ulong RegionSize = 0x20;

    private readonly Queue<byte> _rx = new();

    public TextWriter Output { get; } = output;

    public ulong Read(ulong address, int bytes) =>
        (address & 0x1F) switch {
            0x00 => 0u,                                          // txdata: TX never full
            0x04 => _rx.Count > 0 ? _rx.Dequeue() : 0x80000000u, // rxdata: empty flag in bit 31
            0x08 => 1u,                                          // txctrl: txen=1
            0x0C => _rx.Count > 0 ? 1u : 0u,                     // rxctrl: data present
            _    => 0u,
        };

    public void Write(ulong address, ulong value, int bytes) {
        if ((address & 0x1F) == 0x00) Output.Write((char)(byte)value);
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) { }

    /// <summary>Enqueues a byte to be returned by the next rxdata read.</summary>
    public void Enqueue(byte b) => _rx.Enqueue(b);
}