using Mechanism;

namespace RiscV32.Decode;

/// <summary>
/// A decoded RV32I instruction.
/// The Payload carries the pre-decoded operation so the executor
/// doesn't need to re-decode from the raw encoding.
/// </summary>
public sealed class RvInstruction(
    ulong pc,
    uint raw,
    int dest,
    IReadOnlyList<int> sources,
    ToothClass cls,
    object? payload,
    int sizeBytes = 4
)
    : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = raw;
    public int SizeBytes { get; } = sizeBytes;
    public int DestinationRegister { get; } = dest;
    public IReadOnlyList<int> SourceRegisters { get; } = sources;
    public ToothClass Class { get; } = cls;
    public object? Payload { get; } = payload;

    // These views are derived purely from the (immutable) Payload. Decode results
    // are cached and reused millions of times on the hot path, so compute each
    // once at construction rather than re-running the type-pattern switch — and the
    // collection-valued ones (below) re-allocated an array on every access.
    public int VectorDestinationRegister { get; } = payload switch {
        RvVIntAluVv op      => op.Vd,
        RvVIntAluVx op      => op.Vd,
        RvVIntAluVi op      => op.Vd,
        RvVMulVv op         => op.Vd,
        RvVMulVx op         => op.Vd,
        RvVRedVs op         => op.Vd,
        RvVWideRedVs op     => op.Vd,
        RvVMaskCmpVv op     => op.Vd,
        RvVMaskCmpVx op     => op.Vd,
        RvVMaskCmpVi op     => op.Vd,
        RvVleVv op          => op.Vd,
        RvVlm op            => op.Vd,
        RvVlsegVv op        => op.Vd,
        RvVlseVv op         => op.Vd,
        RvVlxeiVv op        => op.Vd,
        RvVWideVv op        => op.Vd,
        RvVWideVx op        => op.Vd,
        RvVNarrVv op        => op.Vd,
        RvVNarrVx op        => op.Vd,
        RvVNarrVi op        => op.Vd,
        RvVSlideVx op       => op.Vd,
        RvVFpSlide1Vf op    => op.Vd,
        RvVSlideVi op       => op.Vd,
        RvVRgatherVv op     => op.Vd,
        RvVRgatherEi16Vv op => op.Vd,
        RvVRgatherVx op     => op.Vd,
        RvVRgatherVi op     => op.Vd,
        RvVFpBinVv op       => op.Vd,
        RvVFpBinVf op       => op.Vd,
        RvVFpFmaVv op       => op.Vd,
        RvVFpFmaVf op       => op.Vd,
        RvVmFpCmpVv op      => op.Vd,
        RvVmFpCmpVf op      => op.Vd,
        RvVFpSqrt op        => op.Vd,
        RvVFpClass op       => op.Vd,
        RvVFpCvt op         => op.Vd,
        RvVFpMvSf op        => op.Vd,
        RvVFpMvVf op        => op.Vd,
        RvVFpRedVs op       => op.Vd,
        RvVIntMacVv op      => op.Vd,
        RvVIntMacVx op      => op.Vd,
        RvVMvSx op          => op.Vd,
        RvVMergeVv op       => op.Vd,
        RvVMergeVx op       => op.Vd,
        RvVMergeVi op       => op.Vd,
        RvVFpMergeVf op     => op.Vd,
        RvVMaskLogMm op     => op.Vd,
        RvVMaskUnary op     => op.Vd,
        RvVCompress op      => op.Vd,
        RvVMvNr op          => op.Vd,
        RvVwMacVv op        => op.Vd,
        RvVwMacVx op        => op.Vd,
        RvVSatIntVv op      => op.Vd,
        RvVSatIntVx op      => op.Vd,
        RvVSatIntVi op      => op.Vd,
        RvVnClipVv op       => op.Vd,
        RvVnClipVx op       => op.Vd,
        RvVnClipVi op       => op.Vd,
        RvVFpWArithVv op    => op.Vd,
        RvVFpWArithVf op    => op.Vd,
        RvVFpWMacVv op      => op.Vd,
        RvVFpWMacVf op      => op.Vd,
        RvVFpWCvt op        => op.Vd,
        RvVFpNCvt op        => op.Vd,
        RvVlrV op           => op.Vd,
        RvVleFf op          => op.Vd,
        RvVExt op           => op.Vd,
        RvVAvgVv op         => op.Vd,
        RvVAvgVx op         => op.Vd,
        RvVFpWideRedVs op   => op.Vd,
        RvVlssegVv op       => op.Vd,
        RvVlxsegVv op       => op.Vd,
        // RvVcpop/RvVfirst write integer rd, not a vector register → -1
        _ => -1,
    };

    public IReadOnlyList<int> UveStreamSources { get; } = payload switch {
        RvUveSoAFp op     => op.Usrc2 >= 0 ? [op.Usrc1, op.Usrc2,] : [op.Usrc1,],
        RvUveSoAInt op    => op.Usrc2 >= 0 ? [op.Usrc1, op.Usrc2,] : [op.Usrc1,],
        RvUveSoALogic op  => op.Usrc2 >= 0 ? [op.Usrc1, op.Usrc2,] : [op.Usrc1,],
        RvUveSoAShiftV op => [op.Usrc1, op.Usrc2,],
        RvUveSoAShiftS op => [op.Usrc1,],
        RvUveSoASadde op  => [op.Usrc1,],
        RvUveSoVMvvs op   => [op.Us1,],
        _                 => [],
    };

    public IReadOnlyList<int> UveBranchStreams { get; } = payload switch {
        RvUveSoBNc op => [op.Urs,],
        RvUveSoBc op  => [op.Urs,],
        _             => [],
    };

    public IReadOnlyList<(int StreamId, int Dim)> UveDimBranchSources { get; } = payload switch {
        RvUveSoBNdc op => [(op.Urs, op.Dim),],
        RvUveSoBdc op  => [(op.Urs, op.Dim),],
        _              => [],
    };

    public int LoadSignExtendBytes { get; } = payload switch {
        RvLb => 1,
        RvLh => 2,
        _    => 0,
    };

    public bool NanBoxLoadResult { get; } = payload is RvFlw;
    public bool IsDiv { get; } = payload is RvDiv or RvDivu or RvRem or RvRemu;
    public bool IsStoreConditional { get; } = payload is RvScW;

    // W (bit 0) in the predecessor set and R (bit 1) in the successor set: the fence
    // orders older stores before younger loads — the only ordering TSO doesn't already
    // guarantee. Covers FENCE.TSO (pred=RW, succ=RW) too.
    public bool IsStoreLoadFence { get; } =
        payload is RvFence f && (f.Pred & 0x1) != 0 && (f.Succ & 0x2) != 0;

    public IReadOnlyList<int> VectorSourceRegisters { get; } = payload switch {
        RvVIntAluVv op  => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVIntAluVx op  => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVIntAluVi op  => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVMulVv op     => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVMulVx op     => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVRedVs op     => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVWideRedVs op => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVMaskCmpVv op => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVMaskCmpVx op => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVMaskCmpVi op => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVseVv op      => op.Masked ? [op.Vs3, 0,] : [op.Vs3,],
        RvVsm op        => [op.Vs3,],
        RvVlsegVv op    => op.Masked ? [0,] : [],
        RvVleFf op      => op.Masked ? [0,] : [],
        RvVlssegVv op   => op.Masked ? [0,] : [],
        RvVlxsegVv op   => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVssegVv op => op.NumFields switch {
            2 => op.Masked ? [op.Vs3, op.Vs3 + 1, 0,] : [op.Vs3, op.Vs3 + 1,],
            3 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2,],
            4 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3,],
            5 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4,],
            6 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5,],
            7 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6,],
            _ => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs3 + 7, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs3 + 7,],
        },
        RvVsseVv op  => op.Masked ? [op.Vs3, 0,] : [op.Vs3,],
        RvVlxeiVv op => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVsxeiVv op => op.Masked ? [op.Vs3, op.Vs2, 0,] : [op.Vs3, op.Vs2,],
        RvVsssegVv op => op.NumFields switch {
            2 => op.Masked ? [op.Vs3, op.Vs3 + 1, 0,] : [op.Vs3, op.Vs3 + 1,],
            3 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2,],
            4 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3,],
            5 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4,],
            6 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5,],
            7 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6,],
            _ => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs3 + 7, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs3 + 7,],
        },
        RvVsxsegVv op => op.NumFields switch {
            2 => op.Masked ? [op.Vs3, op.Vs3 + 1, op.Vs2, 0,] : [op.Vs3, op.Vs3 + 1, op.Vs2,],
            3 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs2, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs2,],
            4 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs2, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs2,],
            5 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs2, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs2,],
            6 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs2, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs2,],
            7 => op.Masked
                ? [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs2, 0,]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs2,],
            _ => op.Masked
                ? [
                    op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs3 + 7, op.Vs2,
                    0,
                ]
                : [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3, op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs3 + 7, op.Vs2,],
        },
        RvVAvgVv op         => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVAvgVx op         => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVExt op           => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpWideRedVs op   => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVWideVv op        => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVWideVx op        => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVNarrVv op        => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVNarrVx op        => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVNarrVi op        => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVSlideVx op       => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpSlide1Vf op    => op.Masked ? [op.Vs2, op.FpRs1, 0,] : [op.Vs2, op.FpRs1,],
        RvVSlideVi op       => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVRgatherVv op     => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVRgatherEi16Vv op => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVRgatherVx op     => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVRgatherVi op     => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVMvXs op          => [op.Vs2,],
        // vd is also a source for FMA (accumulate target); scalar fp source in SourceRegisters
        RvVFpBinVv op  => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVFpBinVf op  => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpFmaVv op  => op.Masked ? [op.Vd, op.Vs2, op.Vs1, 0,] : [op.Vd, op.Vs2, op.Vs1,],
        RvVFpFmaVf op  => op.Masked ? [op.Vd, op.Vs2, 0,] : [op.Vd, op.Vs2,],
        RvVmFpCmpVv op => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVmFpCmpVf op => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpSqrt op   => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpClass op  => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpCvt op    => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpMvFs op   => [op.Vs2,],
        RvVFpMvSf _    => [],
        RvVFpMvVf op   => op.Masked ? [0,] : [],
        RvVFpRedVs op  => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        // vd is accumulator source for integer MAC; vmerge always reads v0 mask
        RvVIntMacVv op  => op.Masked ? [op.Vd, op.Vs2, op.Vs1, 0,] : [op.Vd, op.Vs2, op.Vs1,],
        RvVIntMacVx op  => op.Masked ? [op.Vd, op.Vs2, 0,] : [op.Vd, op.Vs2,],
        RvVMvSx _       => [],
        RvVMergeVv op   => [op.Vs2, op.Vs1, 0,],
        RvVMergeVx op   => [op.Vs2, 0,],
        RvVMergeVi op   => [op.Vs2, 0,],
        RvVFpMergeVf op => [op.Vs2, op.FpRs1, 0,], // always masked (vm=0);
        RvVMaskLogMm op => [op.Vs2, op.Vs1,],
        RvVcpop op      => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVfirst op     => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        // vid.v has no vs2 data source; viota/vmsbf/vmsif/vmsof read vs2
        RvVMaskUnary op => op.Op == VMaskUnaryOp.Id
            ? op.Masked ? (IReadOnlyList<int>)[0,] : []
            : op.Masked
                ? [op.Vs2, 0,]
                : [op.Vs2,],
        RvVCompress op => [op.Vs2, op.Vs1,], // vs1 is explicit mask, always vm=1
        // vd is the 2×SEW accumulator — it must be in VectorSourceRegisters so hazard checks fire
        RvVwMacVv op     => op.Masked ? [op.Vd, op.Vs2, op.Vs1, 0,] : [op.Vd, op.Vs2, op.Vs1,],
        RvVwMacVx op     => op.Masked ? [op.Vd, op.Vs2, 0,] : [op.Vd, op.Vs2,],
        RvVSatIntVv op   => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVSatIntVx op   => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVSatIntVi op   => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVnClipVv op    => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVnClipVx op    => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVnClipVi op    => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpWArithVv op => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        RvVFpWArithVf op => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        // vd is 2×SEW accumulator — must be in VectorSourceRegisters so hazard checks fire
        RvVFpWMacVv op => op.Masked ? [op.Vd, op.Vs2, op.Vs1, 0,] : [op.Vd, op.Vs2, op.Vs1,],
        RvVFpWMacVf op => op.Masked ? [op.Vd, op.Vs2, 0,] : [op.Vd, op.Vs2,],
        RvVFpWCvt op   => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVFpNCvt op   => op.Masked ? [op.Vs2, 0,] : [op.Vs2,],
        RvVlrV _       => [],
        RvVsrV op => op.NumRegs switch {
            1 => [op.Vs3,],
            2 => [op.Vs3, op.Vs3 + 1,],
            4 => [op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3,],
            _ => [
                op.Vs3, op.Vs3 + 1, op.Vs3 + 2, op.Vs3 + 3,
                op.Vs3 + 4, op.Vs3 + 5, op.Vs3 + 6, op.Vs3 + 7,
            ],
        },
        RvVMvNr op => op.NumRegs switch {
            1 => [op.Vs2,],
            2 => [op.Vs2, op.Vs2 + 1,],
            4 => [op.Vs2, op.Vs2 + 1, op.Vs2 + 2, op.Vs2 + 3,],
            _ => [
                op.Vs2, op.Vs2 + 1, op.Vs2 + 2, op.Vs2 + 3,
                op.Vs2 + 4, op.Vs2 + 5, op.Vs2 + 6, op.Vs2 + 7,
            ],
        },
        _ => [],
    };

    public override string ToString() =>
        $"[0x{Pc:X8}] {Payload?.GetType().Name ?? "?"} raw=0x{RawEncoding:X8}";
}

/// <summary>
/// The decoded operation carried as Payload in RvInstruction.
/// The executor pattern-matches on this.
/// </summary>
public abstract record RvOp;

// ── R-type ────────────────────────────────────────────────────────────────────
public record RvAdd(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSub(int Rd, int Rs1, int Rs2) : RvOp;

public record RvXor(int Rd, int Rs1, int Rs2) : RvOp;

public record RvOr(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAnd(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSll(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSrl(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSra(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSlt(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSltu(int Rd, int Rs1, int Rs2) : RvOp;

// ── I-type ALU ────────────────────────────────────────────────────────────────
public record RvAddi(int Rd, int Rs1, int Imm) : RvOp;

public record RvXori(int Rd, int Rs1, int Imm) : RvOp;

public record RvOri(int Rd, int Rs1, int Imm) : RvOp;

public record RvAndi(int Rd, int Rs1, int Imm) : RvOp;

public record RvSlli(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSrli(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSrai(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSlti(int Rd, int Rs1, int Imm) : RvOp;

public record RvSltiu(int Rd, int Rs1, int Imm) : RvOp;

// ── Loads ─────────────────────────────────────────────────────────────────────
public record RvLb(int Rd, int Rs1, int Imm) : RvOp;

public record RvLh(int Rd, int Rs1, int Imm) : RvOp;

public record RvLw(int Rd, int Rs1, int Imm) : RvOp;

public record RvLbu(int Rd, int Rs1, int Imm) : RvOp;

public record RvLhu(int Rd, int Rs1, int Imm) : RvOp;

// ── Stores ────────────────────────────────────────────────────────────────────
public record RvSb(int Rs1, int Rs2, int Imm) : RvOp;

public record RvSh(int Rs1, int Rs2, int Imm) : RvOp;

public record RvSw(int Rs1, int Rs2, int Imm) : RvOp;

// ── Branches ──────────────────────────────────────────────────────────────────
public record RvBeq(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBne(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBlt(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBge(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBltu(int Rs1, int Rs2, int Imm) : RvOp;

public record RvBgeu(int Rs1, int Rs2, int Imm) : RvOp;

// ── Jumps ─────────────────────────────────────────────────────────────────────
public record RvJal(int Rd, int Imm) : RvOp;

public record RvJalr(int Rd, int Rs1, int Imm) : RvOp;

// ── Upper immediates ──────────────────────────────────────────────────────────
public record RvLui(int Rd, int Imm) : RvOp;

public record RvAuipc(int Rd, int Imm) : RvOp;

// ── System ────────────────────────────────────────────────────────────────────
public record RvEcall : RvOp;

public record RvEbreak : RvOp;

public record RvCsrrw(int Rd, int Rs1, uint Csr) : RvOp;

public record RvCsrrs(int Rd, int Rs1, uint Csr) : RvOp;

public record RvCsrrc(int Rd, int Rs1, uint Csr) : RvOp;

public record RvCsrrwi(int Rd, uint Zimm, uint Csr) : RvOp;

public record RvCsrrsi(int Rd, uint Zimm, uint Csr) : RvOp;

public record RvCsrrci(int Rd, uint Zimm, uint Csr) : RvOp;

public record RvMret : RvOp;

public record RvSret : RvOp;

public record RvWfi : RvOp;

/// <summary>
/// FENCE with its ordering sets. <paramref name="Pred"/>/<paramref name="Succ"/> are the
/// 4-bit predecessor/successor masks (bit 3 = I, 2 = O, 1 = R, 0 = W); <paramref name="Fm"/>
/// is the fence mode (0 = normal, 8 = FENCE.TSO). Architecturally a no-op in the executor;
/// the pipeline enforces the timing ordering via <c>ITooth.IsStoreLoadFence</c>.
/// </summary>
public record RvFence(uint Pred, uint Succ, uint Fm) : RvOp;

/// <summary>FENCE.I (Zifencei): instruction-stream synchronization. Executes as a no-op —
/// I-cache invalidation on self-modifying code is not modeled.</summary>
public record RvFenceI : RvOp;

/// <summary>SFENCE.VMA: TLB shootdown. No-op in our NOMMU simulation.</summary>
public record RvSfenceVma : RvOp;

// ── M extension (multiply / divide) ──────────────────────────────────────────
public record RvMul(int Rd, int Rs1, int Rs2) : RvOp;

public record RvMulh(int Rd, int Rs1, int Rs2) : RvOp;

public record RvMulhsu(int Rd, int Rs1, int Rs2) : RvOp;

public record RvMulhu(int Rd, int Rs1, int Rs2) : RvOp;

public record RvDiv(int Rd, int Rs1, int Rs2) : RvOp;

public record RvDivu(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRem(int Rd, int Rs1, int Rs2) : RvOp;

public record RvRemu(int Rd, int Rs1, int Rs2) : RvOp;

// ── F extension (single-precision float) ─────────────────────────────────────
// Register indices in all F records are unified: 0-31 = int, 32-63 = float.

public record RvFlw(int Rd, int Rs1, int Imm) : RvOp; // Rd=fp, Rs1=int

public record RvFsw(int Rs1, int Rs2, int Imm) : RvOp; // Rs1=int base, Rs2=fp data

public record RvFaddS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsubS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmulS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFdivS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsqrtS(int Rd, int Rs1) : RvOp;

public record RvFsgnjS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjnS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjxS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFminS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmaxS(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFeqS(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFltS(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFleS(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFclassS(int Rd, int Rs1) : RvOp; // Rd=int result

public record RvFcvtWs(int Rd, int Rs1, int Rm) : RvOp; // float→signed int

public record RvFcvtWuS(int Rd, int Rs1, int Rm) : RvOp; // float→unsigned int

public record RvFcvtSw(int Rd, int Rs1, int Rm) : RvOp; // signed int→float

public record RvFcvtSWu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int→float

public record RvFmvXw(int Rd, int Rs1) : RvOp; // fp bits→int reg

public record RvFmvWx(int Rd, int Rs1) : RvOp; // int bits→fp reg

public record RvFmaddS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFmsubS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmsubS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmaddS(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

// ── D extension ───────────────────────────────────────────────────────────────
// Register indices: 0-31 = int, 32-63 = fp (64-bit NaN-boxed when .S)

public record RvFld(int Rd, int Rs1, int Imm) : RvOp; // Rd=fp64, Rs1=int

public record RvFsd(int Rs1, int Rs2, int Imm) : RvOp; // Rs1=int base, Rs2=fp64 data

public record RvFaddD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsubD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmulD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFdivD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsqrtD(int Rd, int Rs1) : RvOp;

public record RvFsgnjD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjnD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFsgnjxD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFminD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFmaxD(int Rd, int Rs1, int Rs2) : RvOp;

public record RvFeqD(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFltD(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFleD(int Rd, int Rs1, int Rs2) : RvOp; // Rd=int result

public record RvFclassD(int Rd, int Rs1) : RvOp; // Rd=int result

public record RvFcvtWd(int Rd, int Rs1, int Rm) : RvOp; // double→signed int32

public record RvFcvtWuD(int Rd, int Rs1, int Rm) : RvOp; // double→unsigned int32

public record RvFcvtDw(int Rd, int Rs1, int Rm) : RvOp; // signed int32→double

public record RvFcvtDWu(int Rd, int Rs1, int Rm) : RvOp; // unsigned int32→double

public record RvFcvtSd(int Rd, int Rs1, int Rm) : RvOp; // double→single (narrowing)

public record RvFcvtDs(int Rd, int Rs1, int Rm) : RvOp; // single→double (widening)

public record RvFmaddD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFmsubD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmsubD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

public record RvFnmaddD(int Rd, int Rs1, int Rs2, int Rs3) : RvOp;

// ── A extension (atomics) ─────────────────────────────────────────────────────
public record RvLrW(int Rd, int Rs1) : RvOp;

public record RvScW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoswapW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoaddW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoxorW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoandW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoorW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominuW(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxuW(int Rd, int Rs1, int Rs2) : RvOp;

// ── Zacas extension (compare-and-swap) ───────────────────────────────────────
// rd is both comparand (source) and destination for the old value.
public record RvAmocasW(int Rd, int Rs1, int Rs2) : RvOp;

// ── Zabha extension (byte/halfword atomics) ───────────────────────────────────
public record RvAmoswapB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoaddB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoxorB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoandB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoorB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominuB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxuB(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoswapH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoaddH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoxorH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoandH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmoorH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmominuH(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAmomaxuH(int Rd, int Rs1, int Rs2) : RvOp;

// ── V extension (vector) ──────────────────────────────────────────────────────

// Config: rd = new vl (integer), vtypei/rs2 = new vtype
public record RvVsetvli(int Rd, int Rs1, int Vtypei) : RvOp;

public record RvVsetivli(int Rd, int Zimm, int Vtypei) : RvOp;

public record RvVsetvl(int Rd, int Rs1, int Rs2) : RvOp;

// Unit-stride loads: Vd = destination vector register, Rs1 = base address, Sew = element width in bits
public record RvVleVv(int Vd, int Rs1, int Sew, bool Masked) : RvOp;

public record RvVlm(int Vd, int Rs1) : RvOp;

// Segment loads: NumFields = 2..8; writes to vd, vd+1, ..., vd+NumFields-1 (one reg per field per element).
public record RvVlsegVv(int NumFields, int Vd, int Rs1, int Sew, bool Masked) : RvOp;

// Strided loads: Rs2 = byte stride (may be negative)
public record RvVlseVv(int Vd, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

// Unit-stride stores: Vs3 = source vector register, Rs1 = base address, Sew = element width in bits
public record RvVseVv(int Vs3, int Rs1, int Sew, bool Masked) : RvOp;

public record RvVsm(int Vs3, int Rs1) : RvOp;

// Segment stores: NumFields = 2..8; reads from vs3, vs3+1, ..., vs3+NumFields-1.
public record RvVssegVv(int NumFields, int Vs3, int Rs1, int Sew, bool Masked) : RvOp;

// Strided stores: Rs2 = byte stride
public record RvVsseVv(int Vs3, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

// Slide ops: shift elements up (towards higher indices) or down (towards lower indices).
// Is1=true for vslide1up/vslide1down (insert scalar at boundary; .vx only).
public enum VSlideDir { Up, Down, }

public record RvVSlideVx(VSlideDir Dir, bool Is1, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVFpSlide1Vf(VSlideDir Dir, int Vd, int Vs2, int FpRs1, bool Masked) : RvOp;

public record RvVSlideVi(VSlideDir Dir, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// Indexed gather: vd[i] = vs2[index] or 0 if index >= vl.
public record RvVRgatherVv(int Vd, int Vs2, int Vs1, bool Masked) : RvOp; // SEW-wide indices

public record RvVRgatherEi16Vv(int Vd, int Vs2, int Vs1, bool Masked) : RvOp; // u16 indices regardless of SEW

public record RvVRgatherVx(int Vd, int Vs2, int Rs1, bool Masked) : RvOp; // broadcast scalar index

public record RvVRgatherVi(int Vd, int Vs2, int Imm, bool Masked) : RvOp; // broadcast immediate index

// Indexed loads: Vs2 = index vector (byte offsets), IndexSew = index element width in bits,
// data element width comes from runtime vtype CSR. Ordered=true → vloxei (ordered/faulting-only).
public record RvVlxeiVv(int Vd, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

// Indexed stores: Vs3 = data vector, Vs2 = index vector (byte offsets), IndexSew = index element width.
public record RvVsxeiVv(int Vs3, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

// Integer ALU — split by source variant
public enum VIntOp {
    Add,
    Sub,
    Rsub, // vrsub: vd[i] = scalar/imm - vs2[i] (VX and VI only)
    And,
    Or,
    Xor,
    Sll,
    Srl,
    Sra,
    Mov, // vmv.v.v / vmv.v.x / vmv.v.i: vd[i] = source[i] (broadcast)
    Minu,
    Min,
    Maxu,
    Max,
}

public enum VIntMacOp {
    Macc,  // vmacc:  vd[i] = vd[i] + vs2[i]*vs1[i]
    Nmsac, // vnmsac: vd[i] = vd[i] - vs2[i]*vs1[i]
    Madd,  // vmadd:  vd[i] = vs2[i] + vd[i]*vs1[i]
    Nmsub, // vnmsub: vd[i] = vs2[i] - vd[i]*vs1[i]
}

public enum VMaskLogOp {
    Andn, // vmandn.mm: vd = vs2 & ~vs1
    And,  // vmand.mm:  vd = vs2 &  vs1
    Or,   // vmor.mm:   vd = vs2 |  vs1
    Xor,  // vmxor.mm:  vd = vs2 ^  vs1
    Orn,  // vmorn.mm:  vd = vs2 | ~vs1
    Nand, // vmnand.mm: vd = ~(vs2 &  vs1)
    Nor,  // vmnor.mm:  vd = ~(vs2 |  vs1)
    Xnor, // vmxnor.mm: vd = ~(vs2 ^  vs1)
}

public enum VMaskUnaryOp {
    Msbf = 1,  // vmsbf.m: 1 for elements before first set bit in vs2
    Msof = 2,  // vmsof.m: 1 only at the position of first set bit
    Msif = 3,  // vmsif.m: 1 for elements up to and including first set bit
    Iota = 16, // viota.m: exclusive prefix-sum of vs2 bits written per element
    Id = 17,   // vid.v:   write element index i into vd[i]
}

public enum VMaskCmpOp {
    Eq,
    Ne,
    Ltu,
    Lt,
    Gtu,
    Gt,
}

public record RvVIntAluVv(VIntOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVIntAluVx(VIntOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVIntAluVi(VIntOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// vmv.x.s rd, vs2: extract element 0 from vs2 into integer rd (OPMVV, funct6=16)
public record RvVMvXs(int Rd, int Vs2) : RvOp;

// vmv.s.x vd, rs1: move integer rs1 into element 0 of vd (OPMVX, funct6=0x10, vs2=0)
public record RvVMvSx(int Vd, int Rs1) : RvOp;

// Integer multiply-accumulate (OPMVV funct3=2 / OPMVX funct3=6): vd is accumulator.
// vmacc.vv:  vd[i] = vd[i] + vs2[i]*vs1[i]
// vnmsac.vv: vd[i] = vd[i] - vs2[i]*vs1[i]
public record RvVIntMacVv(VIntMacOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVIntMacVx(VIntMacOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// vmerge.vvm/vxm/vim: funct6=0x17, vm=0. mask=1 → active source, mask=0 → vs2[i].
public record RvVMergeVv(int Vd, int Vs2, int Vs1) : RvOp;

public record RvVMergeVx(int Vd, int Vs2, int Rs1) : RvOp;

public record RvVMergeVi(int Vd, int Vs2, int Imm) : RvOp;

// vfmerge.vfm: FP conditional merge; always uses v0 mask. mask=1 → fpRs1 (scalar float bits), mask=0 → vs2[i].
public record RvVFpMergeVf(int Vd, int Vs2, int FpRs1) : RvOp;

// Reduction ops — OPMVV (funct3=2); result lands in vd[0].
// Format: vredop.vs vd, vs2, vs1  (vs2=source vector, vs1=scalar initial accumulator)
public enum VRedOp {
    Sum,  // vredsum
    And,  // vredand
    Or,   // vredor
    Xor,  // vredxor
    Minu, // vredminu
    Min,  // vredmin
    Maxu, // vredmaxu
    Max,  // vredmax
}

public record RvVRedVs(VRedOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// Widening integer sum reduction: vd[0] = 2×SEW(vs1[0]) + Σ zero/sign-extend(vs2[i])
public record RvVWideRedVs(bool Signed, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// Integer multiply/divide — OPMVV (VV) and OPMVX (VX); no VI variant.
public enum VMulOp {
    Mul,    // vmul:    low half of signed product
    MulH,   // vmulh:   signed high half
    MulHu,  // vmulhu:  unsigned high half
    MulHsu, // vmulhsu: vs2 signed × vs1/rs1 unsigned, high half
    Div,    // vdiv:    signed truncated division
    Divu,   // vdivu:   unsigned division
    Rem,    // vrem:    signed remainder
    Remu,   // vremu:   unsigned remainder
}

public record RvVMulVv(VMulOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVMulVx(VMulOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// Widening integer arithmetic: vwaddu/vwadd/vwsubu/vwsub/vwmulu/vwmulsu/vwmul.
// Vs2IsWide=true for the .wv/.wx variants where vs2 is already 2*SEW.
public enum VWideOp {
    AddU,
    Add,
    SubU,
    Sub,
    MulU,
    MulSu,
    Mul,
}

public record RvVWideVv(VWideOp Op, int Vd, int Vs2, int Vs1, bool Masked, bool Vs2IsWide) : RvOp;

public record RvVWideVx(VWideOp Op, int Vd, int Vs2, int Rs1, bool Masked, bool Vs2IsWide) : RvOp;

// Narrowing shift: vs2 is 2*SEW, result vd is SEW.  vnsrl=logical, vnsra=arithmetic.
public enum VNarrOp { Srl, Sra, }

public record RvVNarrVv(VNarrOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVNarrVx(VNarrOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVNarrVi(VNarrOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// Mask logical ops (OPMVV, mm form): bitwise ops on full mask registers, not limited by vl
public record RvVMaskLogMm(VMaskLogOp Op, int Vd, int Vs2, int Vs1) : RvOp;

// vcpop.m rd, vs2: count active mask bits in vs2 (OPMVV funct6=0x10 vs1=16); writes integer rd
public record RvVcpop(int Rd, int Vs2, bool Masked) : RvOp;

// vfirst.m rd, vs2: first active set bit index in vs2 (OPMVV funct6=0x10 vs1=17); -1 if none; writes integer rd
public record RvVfirst(int Rd, int Vs2, bool Masked) : RvOp;

// vmsbf/vmsof/vmsif/viota/vid — OPMVV funct6=0x14 (VMUNARY0); vs2 field unused for vid.v
public record RvVMaskUnary(VMaskUnaryOp Op, int Vd, int Vs2, bool Masked) : RvOp;

// vcompress.vm vd, vs2, vs1: pack elements of vs2 where vs1[i]=1 into vd; vs1 is the explicit mask
public record RvVCompress(int Vd, int Vs2, int Vs1) : RvOp;

// vmv{N}r.v vd, vs2: copy N consecutive vector registers (OPIVI funct6=0x27); NumRegs = 1/2/4/8
public record RvVMvNr(int NumRegs, int Vd, int Vs2) : RvOp;

// vl{N}r.v: whole-register load — loads NumRegs*VLenB bytes, ignores vtype/vl; NumRegs = 1/2/4/8
public record RvVlrV(int NumRegs, int Vd, int Rs1) : RvOp;

// vs{N}r.v: whole-register store — stores NumRegs*VLenB bytes, ignores vtype/vl; NumRegs = 1/2/4/8
public record RvVsrV(int NumRegs, int Vs3, int Rs1) : RvOp;

// vle{SEW}ff.v: fault-only-first load — like vle but trims vl on fault; modeled as regular vle.
public record RvVleFf(int Vd, int Rs1, int Sew, bool Masked) : RvOp;

// vzext.vfN / vsext.vfN: zero/sign-extend each element from SEW/Factor bits to SEW bits.
// Factor=2 → vf2, Factor=4 → vf4, Factor=8 → vf8.
public record RvVExt(bool Signed, int Factor, int Vd, int Vs2, bool Masked) : RvOp;

// vaaddu/vaadd/vasubu/vasub: fixed-point averaging add/sub (OPMVV/OPMVX).
// Result = (vs2 ± vs1/rs1 + round) >> 1, where round comes from vxrm.
public enum VAvgOp {
    Addu,
    Add,
    Subu,
    Sub,
}

public record RvVAvgVv(VAvgOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVAvgVx(VAvgOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// vfwredusum.vs / vfwredosum.vs: widening FP sum reduction (OPFVV only).
// Accumulator in vs1[0] is 2×SEW; elements in vs2 are SEW; result in vd[0] is 2×SEW.
public record RvVFpWideRedVs(bool Ordered, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// vlsseg{NF}e{SEW}.v / vssseg{NF}e{SEW}.v — strided segment loads/stores.
public record RvVlssegVv(int NumFields, int Vd, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

public record RvVsssegVv(int NumFields, int Vs3, int Rs1, int Rs2, int Sew, bool Masked) : RvOp;

// vluxseg/vloxseg/vsuxseg/vsoxseg — indexed segment loads/stores.
// IndexSew = index element width; data element width from runtime vtype.
// Ordered=true → ordered (vloxseg/vsoxseg); false → unordered (vluxseg/vsuxseg).
public record RvVlxsegVv(int NumFields, int Vd, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

public record RvVsxsegVv(int NumFields, int Vs3, int Rs1, int Vs2, int IndexSew, bool Masked, bool Ordered) : RvOp;

// Widening integer multiply-accumulate — OPMVV (funct3=2) / OPMVX (funct3=6).
// vd is the 2×SEW accumulator (both source and destination).
public enum VwMacOp {
    Macc,   // vwmacc:   vd += signed(vs2) * signed(vs1/rs1)
    Maccu,  // vwmaccu:  vd += unsigned(vs2) * unsigned(vs1/rs1)
    Maccsu, // vwmaccsu: vd += signed(vs2) * unsigned(vs1/rs1)
    Maccus, // vwmaccus: vd += unsigned(vs2) * signed(rs1)  (VX only)
}

public record RvVwMacVv(VwMacOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVwMacVx(VwMacOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// Mask comparisons (result: 1 bit per element packed in vd)
public record RvVMaskCmpVv(VMaskCmpOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVMaskCmpVx(VMaskCmpOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVMaskCmpVi(VMaskCmpOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// ── Vector FP extension (V 1.0, OPFVV funct3=1 / OPFVF funct3=5) ─────────────

public enum VFpBinOp {
    Add,
    Sub,
    Mul,
    Div,
    Min,
    Max,
    Sgnj,
    Sgnjn,
    Sgnjx,
}

// Rs1/Vs1 in VF variants stores the unified FRF index (float reg + 32).
public record RvVFpBinVv(VFpBinOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVFpBinVf(VFpBinOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVFpSqrt(int Vd, int Vs2, bool Masked) : RvOp;

// vfclass.v: each element → 10-bit classification mask written as float-width integer.
public record RvVFpClass(int Vd, int Vs2, bool Masked) : RvOp;

public enum VFpCvtOp {
    XuFromF,
    XFromF,
    FFromXu,
    FFromX,
    RtzXuFromF, // vfcvt.rtz.xu.f.v: truncate-to-zero float→uint
    RtzXFromF,  // vfcvt.rtz.x.f.v:  truncate-to-zero float→int
}

public record RvVFpCvt(VFpCvtOp Op, int Vd, int Vs2, bool Masked) : RvOp;

public enum VFpFmaOp {
    Macc,
    Nmacc,
    Msac,
    Nmsac,
    Madd,
    Nmadd,
    Msub,
    Nmsub,
}

// FMA: vd = f(vd, vs2, vs1/rs1). vd is both source and destination.
public record RvVFpFmaVv(VFpFmaOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVFpFmaVf(VFpFmaOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public enum VFpCmpOp {
    Eq,
    Le,
    Lt,
    Ne,
    Gt,
    Ge,
}

// FP compare: result is a mask register (1 bit per element).
public record RvVmFpCmpVv(VFpCmpOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVmFpCmpVf(VFpCmpOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// vfmv.f.s rd, vs2: scalar float rd ← vs2[0]. Rd is unified FRF index (float reg + 32).
public record RvVFpMvFs(int Rd, int Vs2) : RvOp;

// vfmv.s.f vd, rs1: vd[0] ← scalar float rs1 (unified FRF index). Other elements undisturbed.
public record RvVFpMvSf(int Vd, int Rs1) : RvOp;

// vfmv.v.f vd, rs1: broadcast scalar float to all active elements.
public record RvVFpMvVf(int Vd, int Rs1, bool Masked) : RvOp;

// FP reduction ops — OPFVV (funct3=1); result lands in vd[0].
// Format: vfredop.vs vd, vs2, vs1  (vs2=source vector, vs1[0]=scalar initial accumulator)
public enum VFpRedOp {
    Usum, // vfredusum: unordered floating-point sum
    Osum, // vfredosum: ordered floating-point sum
    Min,  // vfredmin
    Max,  // vfredmax
}

public record RvVFpRedVs(VFpRedOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

// ── V saturating integer arithmetic (OPIVV/OPIVX/OPIVI) ──────────────────────
public enum VSatIntOp {
    Sadd,  // vsadd:   signed saturating add
    Saddu, // vsaddu:  unsigned saturating add
    Ssub,  // vssub:   signed saturating subtract
    Ssubu, // vssubu:  unsigned saturating subtract
    Smul,  // vsmul:   signed saturating fixed-point multiply (round by SEW-1)
    Ssrl,  // vssrl:   scaled (rounded) shift right logical
    Ssra,  // vssra:   scaled (rounded) shift right arithmetic
}

public record RvVSatIntVv(VSatIntOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVSatIntVx(VSatIntOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVSatIntVi(VSatIntOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// ── V narrowing saturating clip (vnclipu/vnclip) ─────────────────────────────
// Input is 2×SEW wide; shift+round; saturate to SEW-wide output.
public enum VnClipOp {
    Clipu, // vnclipu: unsigned narrowing clip
    Clip,  // vnclip:  signed narrowing clip
}

public record RvVnClipVv(VnClipOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVnClipVx(VnClipOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

public record RvVnClipVi(VnClipOp Op, int Vd, int Vs2, int Imm, bool Masked) : RvOp;

// ── V widening FP arithmetic (vfwadd/vfwsub VV/VF, vfwadd/vfwsub WV/WF, vfwmul VV/VF) ──
public enum VFpWideArithOp {
    Add, Sub, Mul,
}

// Vs2Wide=true → WV/WF form (vs2 is already 2×SEW); false → VV/VF form
public record RvVFpWArithVv(VFpWideArithOp Op, int Vd, int Vs2, int Vs1, bool Vs2Wide, bool Masked) : RvOp;

public record RvVFpWArithVf(VFpWideArithOp Op, int Vd, int Vs2, int Rs1, bool Vs2Wide, bool Masked) : RvOp;

// ── V widening FP MAC (vd is 2×SEW accumulator — both read and written) ──────
public enum VFpWMacOp {
    Macc,  // vfwmacc:  vd += vs2*vs1
    Nmacc, // vfwnmacc: vd = -(vs2*vs1) - vd
    Msac,  // vfwmsac:  vd = (vs2*vs1) - vd
    Nmsac, // vfwnmsac: vd = -(vs2*vs1) + vd
}

public record RvVFpWMacVv(VFpWMacOp Op, int Vd, int Vs2, int Vs1, bool Masked) : RvOp;

public record RvVFpWMacVf(VFpWMacOp Op, int Vd, int Vs2, int Rs1, bool Masked) : RvOp;

// ── V widening FP converts (funct6=0x12, vs1 field 8-15) ─────────────────────
public enum VFpWCvtOp {
    XuFromF,    // vfwcvt.xu.f.v    (vs1=8):  f32 → u64
    XFromF,     // vfwcvt.x.f.v     (vs1=9):  f32 → i64
    FFromXu,    // vfwcvt.f.xu.v    (vs1=10): u32 → f64
    FFromX,     // vfwcvt.f.x.v     (vs1=11): i32 → f64
    FFromF,     // vfwcvt.f.f.v     (vs1=12): f32 → f64
    RtzXuFromF, // vfwcvt.rtz.xu.f.v (vs1=14): f32 → u64 truncate
    RtzXFromF,  // vfwcvt.rtz.x.f.v  (vs1=15): f32 → i64 truncate
}

public record RvVFpWCvt(VFpWCvtOp Op, int Vd, int Vs2, bool Masked) : RvOp;

// ── V narrowing FP converts (funct6=0x12, vs1 field 16-23) ───────────────────
public enum VFpNCvtOp {
    XuFromF,    // vfncvt.xu.f.w     (vs1=16): f64 → u32
    XFromF,     // vfncvt.x.f.w      (vs1=17): f64 → i32
    FFromXu,    // vfncvt.f.xu.w     (vs1=18): u64 → f32
    FFromX,     // vfncvt.f.x.w      (vs1=19): i64 → f32
    FFromF,     // vfncvt.f.f.w      (vs1=20): f64 → f32
    RodFFromF,  // vfncvt.rod.f.f.w  (vs1=21): f64 → f32 round-to-odd
    RtzXuFromF, // vfncvt.rtz.xu.f.w (vs1=22): f64 → u32 truncate
    RtzXFromF,  // vfncvt.rtz.x.f.w  (vs1=23): f64 → i32 truncate
}

public record RvVFpNCvt(VFpNCvtOp Op, int Vd, int Vs2, bool Masked) : RvOp;

// ── Zbc extension (carry-less multiplication) ─────────────────────────────────
// R-type (opcode=0x33, funct7=0x05, funct3=1/2/3)
public record RvClmul(int Rd, int Rs1, int Rs2) : RvOp; // lower 32 bits of carry-less product

public record RvClmulh(int Rd, int Rs1, int Rs2) : RvOp; // upper 32 bits

public record RvClmulr(int Rd, int Rs1, int Rs2) : RvOp; // bits [62:31]

// ── Zba extension (address generation) ───────────────────────────────────────
// R-type (opcode=0x33, funct7=0x10): rd = rs2 + (rs1 << N)
public record RvSh1Add(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSh2Add(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSh3Add(int Rd, int Rs1, int Rs2) : RvOp;

// ── Zbs extension (single-bit ops) ────────────────────────────────────────────
// R-type (opcode=0x33): register-indexed single-bit operations
public record RvBclr(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 & ~(1 << (rs2 & 31))

public record RvBext(int Rd, int Rs1, int Rs2) : RvOp; // rd = (rs1 >> (rs2 & 31)) & 1

public record RvBinv(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 ^ (1 << (rs2 & 31))

public record RvBset(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 | (1 << (rs2 & 31))

// I-type (opcode=0x13): immediate-indexed single-bit operations
public record RvBclri(int Rd, int Rs1, int Shamt) : RvOp;

public record RvBexti(int Rd, int Rs1, int Shamt) : RvOp;

public record RvBinvi(int Rd, int Rs1, int Shamt) : RvOp;

public record RvBseti(int Rd, int Rs1, int Shamt) : RvOp;

// ── Zicond extension (integer conditional operations) ─────────────────────────
// R-type (opcode=0x33, funct7=0x07)
public record RvCzeroEqz(int Rd, int Rs1, int Rs2) : RvOp; // rd = (rs2 == 0) ? 0 : rs1

public record RvCzeroNez(int Rd, int Rs1, int Rs2) : RvOp; // rd = (rs2 != 0) ? 0 : rs1

// ── Zbb extension (basic bit manipulation) ────────────────────────────────────
// R-type (opcode=0x33)
public record RvAndn(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 & ~rs2

public record RvOrn(int Rd, int Rs1, int Rs2) : RvOp; // rd = rs1 | ~rs2

public record RvXnor(int Rd, int Rs1, int Rs2) : RvOp; // rd = ~(rs1 ^ rs2)

public record RvMax(int Rd, int Rs1, int Rs2) : RvOp; // signed maximum

public record RvMaxu(int Rd, int Rs1, int Rs2) : RvOp; // unsigned maximum

public record RvMin(int Rd, int Rs1, int Rs2) : RvOp; // signed minimum

public record RvMinu(int Rd, int Rs1, int Rs2) : RvOp; // unsigned minimum

public record RvRol(int Rd, int Rs1, int Rs2) : RvOp; // rotate left

public record RvRor(int Rd, int Rs1, int Rs2) : RvOp; // rotate right

public record RvZextH(int Rd, int Rs1) : RvOp; // zero-extend halfword

// I-type unary ops (opcode=0x13, funct3=1, funct7=0x30)
public record RvClz(int Rd, int Rs1) : RvOp; // count leading zeros

public record RvCtz(int Rd, int Rs1) : RvOp; // count trailing zeros

public record RvCpop(int Rd, int Rs1) : RvOp; // population count

public record RvSextB(int Rd, int Rs1) : RvOp; // sign-extend byte

public record RvSextH(int Rd, int Rs1) : RvOp; // sign-extend halfword

// I-type shift-space ops (opcode=0x13, funct3=5)
public record RvRori(int Rd, int Rs1, int Shamt) : RvOp; // rotate right immediate

public record RvOrcB(int Rd, int Rs1) : RvOp; // OR-combine bytes (0 → 0x00, nonzero → 0xFF per byte)

public record RvRev8(int Rd, int Rs1) : RvOp; // byte-reverse

// ── Zawrs extension (wait-on-reservation-set) ─────────────────────────────────
// SYSTEM space (opcode=0x73, funct3=0): NOP in single-core simulation.
public record RvWrsNto : RvOp; // wrs.nto (imm=0x00D): wait for reservation set, no timeout

public record RvWrsSto : RvOp; // wrs.sto (imm=0x01D): wait for reservation set, short timeout

// ── Zicbom extension (cache block management) ─────────────────────────────────
// opcode=0x0F, funct3=2, bits[24:20] selects operation; rs1 = base address.
// NOP in simulation (no cache coherence model).
public record RvCboInval(int Rs1) : RvOp; // cbo.inval (bits[24:20]=0x00): invalidate cache block

public record RvCboClean(int Rs1) : RvOp; // cbo.clean (bits[24:20]=0x01): clean cache block

public record RvCboFlush(int Rs1) : RvOp; // cbo.flush (bits[24:20]=0x02): flush cache block

// ── Zicboz extension (cache block zero) ───────────────────────────────────────
// opcode=0x0F, funct3=2, bits[24:20]=0x04; zeros 64 bytes at cache-line-aligned address.
public record RvCboZero(int Rs1) : RvOp;

// ── Zimop extension (may-be-operations) ───────────────────────────────────────
// opcode=0x73, funct3=4; always return 0 in rd (reserved NOP encodings).
public record RvMopR(int Rd) : RvOp; // mop.r.N: read-only may-be-op

public record RvMopRr(int Rd) : RvOp; // mop.rr.N: register-register may-be-op

// ── Zcmop extension (compressed may-be-operations) ────────────────────────────
// Q1/funct3=3, nzimm=0, rd=odd 1..15; 8 variants: c.mop.N, N ∈ {1,3,5,...,15}.
// Pattern: (c & 0xF8FF) == 0x6081; N = 2*(bits[10:8])+1.
// All are hint NOPs with no architectural effect.
public record RvCMopN(int N) : RvOp; // c.mop.N (N odd, 1..15)

// ── UVE extension ─────────────────────────────────────────────────────────────
// Stream setup (custom-0, opcode=0x0B, R4-type):
//   bits[31:27]=rs3, bits[26:25]=funct2, bits[24:20]=rs2, bits[19:15]=rs1, bits[14:12]=funct3, bits[11:7]=ud
//   funct2=0: ss.sta.{ld|st}.* — funct3[2]=1→load,0→store; ew=1<<(funct3&3); only rs1(base) used
//   funct2=1, funct3=0: ss.app   — rs1=offset reg, rs2=count reg, rs3=stride reg
//   funct2=2, funct3=0: ss.end   — rs1=offset reg, rs2=count reg, rs3=stride reg; activates stream
//   funct2=3, funct3=dimIndex (0-7): ss.app.mod — rs1=E reg (MaxApplications, 0=∞), rs2=target+behavior literal, rs3=disp reg
public record RvUveSsStaLdW(int Ud, int Rs1Base, int ElementBytes = 4) : RvOp;

public record RvUveSsStaStW(int Ud, int Rs1Base, int ElementBytes = 4) : RvOp;

// Rs1Offset is the offset register (Spike adds offset*ew to base); ignored — no offset field in StreamDimension.
public record RvUveSsApp(int Ud, int Rs1Offset, int Rs2Count, int Rs3Stride) : RvOp;

// Same field layout as ss.app; activates the stream after appending the outermost dimension.
public record RvUveSsEnd(int Ud, int Rs1Offset, int Rs2Count, int Rs3Stride) : RvOp;

// ss.app.mod: append a static modifier. funct3=dimIndex, rs1=E register (0 means unlimited), rs2=target+behavior literal, rs3=disp reg.
public record RvUveSsAppMod(
    int Ud,
    int DimIndex,
    StreamModifierTarget Target,
    StreamModifierBehavior Behavior,
    int Rs3Disp,
    int Rs1Size
) : RvOp;

// so.v.dp.(width) ud, rs1 — broadcast integer register rs1 bits (masked to ElementBytes) into u-reg scalar slot
// (custom-1, opcode=0x2B, funct7=0x56; funct3: 0=b, 1=h, 2=w, 3=d)
public record RvUveSoVDp(int Ud, int Rs1, int ElementBytes) : RvOp;

// so.v.mvvs rd, us1 — write first element of UVE register us1 into integer register rd
// (custom-1, opcode=0x2B, funct7=0x54, rs2=16, Rd=integer dest)
public record RvUveSoVMvvs(int Us1, int Rd) : RvOp;

// so.v.mvsv.(width) ud, rs1 — move integer register rs1 (masked to ElementBytes) into UVE register ud as scalar
// (custom-1, opcode=0x2B, funct7=0x54, rs2=24; funct3: 0=b, 1=h, 2=w, 3=d)
public record RvUveSoVMvsv(int Ud, int Rs1, int ElementBytes) : RvOp;

// Arithmetic on stream elements (custom-1, opcode=0x2B):
//   (funct7>>3, funct3): Add=(0,1), Sub=(0,5), Mul=(1,1), Div=(1,5), Mac=(3,5)
public enum UveFpOp {
    Mul = 0,
    Add = 1,
    Mac = 2,
    Sub = 3,
    Div = 4,
    Min = 5,
    Max = 6,
    Abs = 7,
    Inc = 8,
    Dec = 9,
    Sqrt = 10,
    Adde = 11,    // accumulate stream element into ud (overwrite)
    AddeAcc = 12, // accumulate stream element into ud (add)
    Mine = 13,    // ud = min(ud, stream_elem)
    Maxe = 14,    // ud = max(ud, stream_elem)
}

// FP arithmetic on stream elements; Usrc2=-1 for unary ops (Abs, Inc, Dec, Sqrt).
public record RvUveSoAFp(UveFpOp Op, int Ud, int Usrc1, int Usrc2) : RvOp;

public enum UveIntOp {
    Add = 0,
    Sub = 1,
    Mul = 2,
    Div = 3,
    Mac = 4,
    Min = 5,
    Max = 6,
    Abs = 7,
    Inc = 8,
    Dec = 9,
    Adde = 10,
    AddeAcc = 11,
    Mine = 12,
    Maxe = 13,
}

// Integer arithmetic on stream elements; Usrc2=-1 for unary ops (Abs, Inc, Dec).
public record RvUveSoAInt(UveIntOp Op, bool Signed, int Ud, int Usrc1, int Usrc2) : RvOp;

public enum UveLogicOp {
    Nand,
    And,
    Nor,
    Or,
    Not,
    Xor,
}

// Bitwise logic on stream elements; Usrc2=-1 for Not (unary).
public record RvUveSoALogic(UveLogicOp Op, int Ud, int Usrc1, int Usrc2) : RvOp;

public enum UveShiftOp {
    Sll, Srl, Sra,
}

// Element-wise shift with amount from another u-reg.
public record RvUveSoAShiftV(UveShiftOp Op, int Ud, int Usrc1, int Usrc2) : RvOp;

// Element-wise shift with amount from integer register Rs2.
public record RvUveSoAShiftS(UveShiftOp Op, int Ud, int Usrc1, int Rs2) : RvOp;

// Scalar-write reduction: accumulate stream element into integer (sadde, IsFp=false) or FP (fsadde, IsFp=true).
// Rd is the unified-file index: integer reg (0–31) for sadde; FP reg (32–63, pre-offset) for fsadde.
public record RvUveSoASadde(bool IsFp, bool Acc, int Rd, int Usrc1) : RvOp;

// SO_C group (custom-1, funct7=0x58): stream lifecycle and vector-length control.
// ss.stop ud — terminate stream in u-reg ud (SO_C_BREAK, funct3=3).
public record RvUveSoCBreak(int Ud) : RvOp;

// ss.suspend ud — suspend stream in u-reg ud (SO_C_SUSPD, funct3=1).
public record RvUveSoCSuspd(int Ud) : RvOp;

// ss.resume ud — resume suspended stream in u-reg ud (SO_C_RESUM, funct3=2).
public record RvUveSoCResum(int Ud) : RvOp;

// ss.getvl rd — read current vector length into integer register rd (SO_C_GETVL, funct3=7).
public record RvUveSoCGetvl(int Rd) : RvOp;

// ss.setvl rd, rs1 — set vector length from integer rs1, return old VL in rd (SO_C_SETVL, funct3=0).
public record RvUveSoCSetvl(int Rd, int Rs1) : RvOp;

// Stream branch (custom-1, opcode=0x2B, UVE B-type: bits[31:29]=111, bit28=imm[12]):
//   funct3=0:     so.b.nc urs, imm — not exhausted (bit20=1) / so.b.c urs, imm — exhausted (bit20=0)
//   funct3=D≥1:  so.b.ndc.D urs, imm — dim D not complete (bit20=1) / so.b.dc.D — dim D complete (bit20=0)
public record RvUveSoBNc(int Urs, int Imm) : RvOp;

public record RvUveSoBNdc(int Urs, int Dim, int Imm) : RvOp;

public record RvUveSoBc(int Urs, int Imm) : RvOp;

public record RvUveSoBdc(int Urs, int Dim, int Imm) : RvOp;

// ── RV64I W-suffix instructions (opcode=0x3B: OP-32; opcode=0x1B: OP-IMM-32) ──────────────
// Each performs the operation on the lower 32 bits and sign-extends the 32-bit result to 64.
public record RvAddw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSubw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSllw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSrlw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvSraw(int Rd, int Rs1, int Rs2) : RvOp;

public record RvAddiw(int Rd, int Rs1, int Imm) : RvOp;

public record RvSlliw(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSrliw(int Rd, int Rs1, int Shamt) : RvOp;

public record RvSraiw(int Rd, int Rs1, int Shamt) : RvOp;

// ── RV64I new load/store variants ───────────────────────────────────────────────────────────
public record RvLwu(int Rd, int Rs1, int Imm) : RvOp; // load word unsigned — zero-extend to 64 bits

public record RvLd(int Rd, int Rs1, int Imm) : RvOp; // load doubleword

public record RvSd(int Rs1, int Rs2, int Imm) : RvOp; // store doubleword