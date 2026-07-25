#region

using Mechanism;

#endregion

namespace RiscV32.Decode;

/// <summary>
///     A decoded RV32I instruction.
///     The Payload carries the pre-decoded operation so the executor
///     doesn't need to re-decode from the raw encoding.
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
        RvVaesEmVv op       => op.Vd,
        RvVaesEfVv op       => op.Vd,
        // RvVcpop/RvVfirst write integer rd, not a vector register → -1
        _ => -1,
    };

    // True for element-group vector-crypto ops whose actual register span (vl/EGS physical
    // registers, one per element group) depends on runtime vtype/LMUL and so can't be captured
    // by VectorDestinationRegister/VectorSourceRegisters at decode time. See
    // ITooth.HasRuntimeSizedVectorDestination.
    public bool HasRuntimeSizedVectorDestination { get; } = payload is RvVaesEmVv or RvVaesEfVv;

    // RV32 amocas.d (Zacas) holds its 64-bit result in a register pair: Rd gets the
    // low word (via the normal DestinationRegister/RegisterResult path), Rd+1 gets the
    // high word via SideEffect. See ITooth.SecondaryDestinationRegister.
    // ECALL never has a DestinationRegister of its own (rd=-1 at decode — see
    // Rv32Decoder), but ISyscallHandler.Handle always delivers its return value to a0
    // (x10) via SideEffect (LinuxSyscallEmulator's ABI: "return value written to a0").
    // Without this, a0 is never renamed for the ECALL, so a younger consumer resolves
    // its RAT lookup to whatever produced a0 *before* the syscall and never learns of
    // the dependency at all — reproduced by stdin_echo64.elf under OooTrain (SYS_write's
    // count depends on SYS_read's return via `mv a2, a0`, which read stale/pre-ecall a0).
    public int SecondaryDestinationRegister { get; } = payload switch {
        RvAmocasDPair op => op.Rd + 1,
        RvEcall          => 10,
        _                => -1,
    };

    public IReadOnlyList<int> UveStreamSources { get; } = payload switch {
        RvUveSoAFp op     => op.Usrc2 >= 0 ? [op.Usrc1, op.Usrc2,] : [op.Usrc1,],
        RvUveSoAInt op    => op.Usrc2 >= 0 ? [op.Usrc1, op.Usrc2,] : [op.Usrc1,],
        RvUveSoALogic op  => op.Usrc2 >= 0 ? [op.Usrc1, op.Usrc2,] : [op.Usrc1,],
        RvUveSoAShiftV op => [op.Usrc1, op.Usrc2,],
        RvUveSoAShiftS op => [op.Usrc1,],
        RvUveSoASadde op  => [op.Usrc1,],
        RvUveSoVMvvs op   => [op.Us1,],
        RvUveSoVCv op     => [op.Vs1,],
        RvUveSoVMv op     => [op.Vs1,],
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

    // RvLw sign-extends 32→64 bits on RV64; on RV32 the extra sign bits above bit 31 are
    // simply unused, so applying it unconditionally is correct for both widths.
    public int LoadSignExtendBytes { get; } = payload switch {
        RvLb => 1,
        RvLh => 2,
        RvLw => 4,
        _    => 0,
    };

    public bool NanBoxLoadResult { get; } = payload is RvFlw;

    public int MemoryAccessBytes { get; } = payload switch {
        RvLb or RvLbu or RvSb                   => 1,
        RvLh or RvLhu or RvSh or RvFlh or RvFsh => 2,
        RvLw or RvSw or RvLwu or RvFlw or RvFsw => 4,
        RvLd or RvSd or RvFld or RvFsd          => 8,
        _                                       => 0,
    };

    public bool IsDiv { get; } = payload is RvDiv or RvDivu or RvRem or RvRemu;
    public bool IsStoreConditional { get; } = payload is RvScW or RvScD;
    public bool MayAccessArbitraryMemory { get; } = payload is RvEcall;

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
        RvVFpMvSf      => [],
        RvVFpMvVf op   => op.Masked ? [0,] : [],
        RvVFpRedVs op  => op.Masked ? [op.Vs2, op.Vs1, 0,] : [op.Vs2, op.Vs1,],
        // vd is accumulator source for integer MAC; vmerge always reads v0 mask
        RvVIntMacVv op => op.Masked ? [op.Vd, op.Vs2, op.Vs1, 0,] : [op.Vd, op.Vs2, op.Vs1,],
        // vd is the round-state source (read-modify-write) for AES round instructions; unmasked
        // always (vm bit is hardcoded to 1 in the encoding — no v0 mask source).
        RvVaesEmVv op   => [op.Vd, op.Vs2,],
        RvVaesEfVv op   => [op.Vd, op.Vs2,],
        RvVIntMacVx op  => op.Masked ? [op.Vd, op.Vs2, 0,] : [op.Vd, op.Vs2,],
        RvVMvSx         => [],
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
        RvVlrV         => [],
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
///     The decoded operation carried as Payload in RvInstruction.
///     The executor pattern-matches on this.
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
///     FENCE with its ordering sets. <paramref name="Pred" />/<paramref name="Succ" /> are the
///     4-bit predecessor/successor masks (bit 3 = I, 2 = O, 1 = R, 0 = W); <paramref name="Fm" />
///     is the fence mode (0 = normal, 8 = FENCE.TSO). Architecturally a no-op in the executor;
///     the pipeline enforces the timing ordering via <c>ITooth.IsStoreLoadFence</c>.
/// </summary>
public record RvFence(uint Pred, uint Succ, uint Fm) : RvOp;

/// <summary>
///     FENCE.I (Zifencei): instruction-stream synchronization. Executes as a no-op —
///     I-cache invalidation on self-modifying code is not modeled.
/// </summary>
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