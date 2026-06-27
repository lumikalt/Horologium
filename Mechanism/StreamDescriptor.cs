namespace Mechanism;

/// <summary>One dimension of a multi-dimensional affine stream: iteration count and byte stride.</summary>
public readonly record struct StreamDimension(long Count, long Stride);

/// <summary>
/// Describes an affine memory stream with one or more dimensions.
/// Dimension[0] is the innermost (fastest-varying); higher indices are outer loops.
/// Negative strides walk backward through memory.
/// ElementBytes must be 1–8.
/// </summary>
public readonly record struct StreamDescriptor(
    ulong BaseAddress,
    int ElementBytes,
    StreamDimension[] Dimensions
) {
    /// <summary>Backward-compatible 1D constructor.</summary>
    public StreamDescriptor(ulong baseAddress, int elementBytes, long count, long stride)
        : this(baseAddress, elementBytes, [new StreamDimension(count, stride),]) { }

    /// <summary>Convenience accessor for 1D streams.</summary>
    public long Count => Dimensions[0].Count;

    /// <summary>Convenience accessor for 1D streams.</summary>
    public long Stride => Dimensions[0].Stride;
};