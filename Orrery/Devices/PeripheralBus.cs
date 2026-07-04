using Mechanism;

namespace Orrery.Devices;

/// <summary>
/// Routes memory accesses to registered MMIO device address windows.
/// Accesses that fall outside every registered window are forwarded to
/// <paramref name="backing"/> (the main RAM or a wrapped-RAM chain such as
/// <see cref="RiscV32.Memory.HtifMemory"/>). Load calls always go to the backing,
/// since device registers are never part of the program image.
/// </summary>
public sealed class PeripheralBus(
    IMemory backing,
    IReadOnlyList<(IMemory Device, ulong Base, ulong Size)> devices
) : IMemory {
    private IMemory Resolve(ulong address) {
        foreach ((IMemory device, ulong @base, ulong size) in devices)
            if (address >= @base && address < @base + size)
                return device;
        return backing;
    }

    public ulong Read(ulong address, int bytes) => Resolve(address).Read(address, bytes);
    public void Write(ulong address, ulong value, int bytes) => Resolve(address).Write(address, value, bytes);
    public void Load(ulong address, ReadOnlySpan<byte> data) => backing.Load(address, data);
}