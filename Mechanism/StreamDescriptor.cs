namespace Mechanism;

/// <summary>
/// Describes one affine memory stream: a base address, element width, element count,
/// and byte stride between consecutive element starts.
///
/// Negative strides walk backward through memory.
/// ElementBytes must be 1–8.
/// </summary>
public readonly record struct StreamDescriptor(
    ulong BaseAddress,
    int ElementBytes,
    long Count,
    long Stride
);
