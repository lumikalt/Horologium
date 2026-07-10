namespace Mechanism;

/// <summary>One dimension of a multi-dimensional affine stream: iteration count and byte stride.</summary>
public readonly record struct StreamDimension(long Count, long Stride);

/// <summary>Which field of a stream dimension a static modifier updates.</summary>
public enum StreamModifierTarget {
    Size = 0, Offset = 1, Stride = 2,
}

/// <summary>How a modifier updates its target field.</summary>
public enum StreamModifierBehavior {
    Inc = 0,
    Dec = 1, // static: target += ±Displacement
    Add = 2,
    Sub = 3, // indirect: target = original_base ± source_value
    Set = 4, // indirect: target = source_value
}

/// <summary>
/// Descriptor modifier: when dimension DimIndex wraps, update field Target.
/// <para>
/// Static modifiers (SourceStreamId &lt; 0): apply Behavior(Displacement) up to MaxApplications times.
/// Indirect modifiers (SourceStreamId ≥ 0): consume one element from the IndSource stream and
/// apply Behavior to the field. MaxApplications=0 means unlimited. Displacement is unused.
/// </para>
/// </summary>
public readonly record struct StreamModifier(
    int DimIndex,
    StreamModifierTarget Target,
    StreamModifierBehavior Behavior,
    long Displacement,
    int MaxApplications = 0,
    int SourceStreamId = -1
);

/// <summary>
/// Describes an affine memory stream with one or more dimensions.
/// Dimension[0] is the innermost (fastest-varying); higher indices are outer loops.
/// Negative strides walk backward through memory.
/// ElementBytes must be 1–8.
/// </summary>
public readonly record struct StreamDescriptor(
    ulong BaseAddress,
    int ElementBytes,
    StreamDimension[] Dimensions,
    StreamModifier[]? Modifiers = null,
    bool IsVectorMode = false,
    int VecCfgDim = -1
) {
    /// <summary>Backward-compatible 1D constructor.</summary>
    public StreamDescriptor(ulong baseAddress, int elementBytes, long count, long stride)
        : this(baseAddress, elementBytes, [new StreamDimension(count, stride),]) { }

    /// <summary>Convenience accessor for 1D streams.</summary>
    public long Count => Dimensions[0].Count;

    /// <summary>Convenience accessor for 1D streams.</summary>
    public long Stride => Dimensions[0].Stride;
}