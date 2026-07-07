using Mechanism;

namespace Orrery.Devices;

/// <summary>
/// Memory-mapped UART modelled after the ns16550a (National Semiconductor 16550A),
/// as used by the QEMU virt machine. This is the console device at 0x10000000 that
/// OpenSBI and the Linux kernel use for early boot output.
/// <para>
/// Register offsets (base = device base address, stride = 1 byte at the given index
/// assuming no DLAB unless noted):
/// 0x00 RHR/THR  — receive holding / transmit holding register; DLAB=1: DLL (ignored)
/// 0x01 IER      — interrupt enable register; DLAB=1: DLM (ignored)
/// 0x02 IIR/FCR  — interrupt identification (read) / FIFO control (write)
/// 0x03 LCR      — line control register (writable; DLAB bit ignored)
/// 0x04 MCR      — modem control register (writable; ignored)
/// 0x05 LSR      — line status register: THRE (bit 5) and TEMT (bit 6) always set
/// 0x06 MSR      — modem status register (reads 0)
/// 0x07 SCR      — scratch register (readable/writable)
/// </para>
/// The transmitter is always ready (LSR.THRE=1, LSR.TEMT=1).
/// TX bytes flush immediately to <see cref="Output"/>.
/// RX bytes are supplied via <see cref="Enqueue"/>; LSR.DR (bit 0) reflects queue depth.
/// </summary>
public sealed class Ns16550AUart(TextWriter output) : IMemory {
    public const ulong DefaultBase = 0x10000000UL;
    public const ulong RegionSize = 0x100UL;

    private const uint LsrDr = 1u << 0;   // data ready
    private const uint LsrThre = 1u << 5; // transmit holding register empty
    private const uint LsrTemt = 1u << 6; // transmitter empty

    private readonly Queue<byte> _rx = new();
    private uint _ier; // interrupt enable
    private uint _lcr; // line control
    private uint _mcr; // modem control
    private uint _scr; // scratch

    public TextWriter Output { get; } = output;

    /// <summary>Enqueues a byte to be returned by the next RHR read.</summary>
    public void Enqueue(byte b) => _rx.Enqueue(b);

    public ulong Read(ulong address, int bytes) =>
        (address & 0xFF) switch {
            0x00 => _rx.Count > 0 ? _rx.Dequeue() : 0u, // RHR: receive data or 0 when empty
            0x01 => _ier,
            0x02 => 0xC1u, // IIR: no interrupt pending (bit 0 = 1), FIFO enabled (bits 7:6 = 11)
            0x03 => _lcr,
            0x04 => _mcr,
            0x05 => Ns16550AUart.LsrThre | Ns16550AUart.LsrTemt
                                         | (_rx.Count > 0 ? Ns16550AUart.LsrDr : 0u), // LSR: always TX-ready
            0x06 => 0u,                                                               // MSR: no modem signals
            0x07 => _scr,
            _    => 0u,
        };

    public void Write(ulong address, ulong value, int bytes) {
        switch (address & 0xFF) {
            case 0x00: Output.Write((char)(byte)value); break; // THR: transmit character
            case 0x01: _ier = (uint)value; break;
            case 0x02: break; // FCR: FIFO control — ignored (no FIFO depth model needed)
            case 0x03: _lcr = (uint)value; break;
            case 0x04: _mcr = (uint)value; break;
            case 0x07: _scr = (uint)value; break;
        }
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) { }
}