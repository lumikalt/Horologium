#region

using Mechanism;
using RiscV32.Decode;
using RiscV32.State;

#endregion

// ReSharper disable CompareOfFloatsByEqualityOperator

namespace RiscV32.Execute;

public partial class Rv32Executor {
    // ── UVE extension helpers ─────────────────────────────────────────────────

    private static Rv32ArchState UState(IArchState state) => (Rv32ArchState)state;

    // so.v.dp.(b/h/w) ud, rs1 — data pack: broadcast masked register value into u-reg lanes.
    // 32-bit width: broadcast to all 4 lanes, Vector mode. Sub-word: lane 0 only, Scalar mode.
    private static ExecuteResult ExecuteUveSoVDp(IRegisterFile regs, int ud, int rs1, int elemBytes) {
        uint mask = elemBytes switch { 1 => 0xFFu, 2 => 0xFFFFu, _ => 0xFFFFFFFFu, };
        uint bits = (uint)regs.Read(rs1) & mask;
        int lanes = elemBytes >= 4 ? 4 : 1;
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                for (var i = 0; i < lanes; i++) u.SetLane32(ud, i, bits);
                u.RegMode[ud] = lanes > 1 ? UveRegMode.Vector : UveRegMode.Scalar;
                u.ValidElements[ud] = lanes;
                u.RegKind[ud] = UveRegKind.Scalar;
            },
        };
    }

    private static ExecuteResult ExecuteUveSoVMvvs(IArchState state, int rd, int us1) {
        uint bits = UState(state).UveState.GetLane32(us1, 0);
        return new ExecuteResult {
            SideEffect = s => UState(s).IntegerRegisters.Write(rd, bits),
        };
    }

    private static ExecuteResult ExecuteUveSoVMvsv(IRegisterFile regs, int ud, int rs1, int elemBytes) {
        uint mask = elemBytes switch { 1 => 0xFFu, 2 => 0xFFFFu, _ => 0xFFFFFFFFu, };
        uint bits = (uint)regs.Read(rs1) & mask;
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                u.SetLane32(ud, 0, bits);
                u.RegMode[ud] = UveRegMode.Scalar;
                u.ValidElements[ud] = 1;
                u.RegKind[ud] = UveRegKind.Scalar;
            },
        };
    }

    // so.a.fp ud, usrc1, usrc2 — element-wise FP arithmetic, per lane.
    // Both scalar sources → vLen=1 (scalar result). Both vector → vLen=4.
    private static ExecuteResult ExecuteUveSoAFp(
        IArchState state,
        IMemory memory,
        UveFpOp op,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            float a = BitConverter.Int32BitsToSingle((int)uveState.GetLane32(usrc1, i));
            float b = usrc2 >= 0 ? BitConverter.Int32BitsToSingle((int)uveState.GetLane32(usrc2, i)) : 0f;
            float acc = BitConverter.Int32BitsToSingle((int)uveState.GetLane32(ud, i));
            float r = op switch {
                UveFpOp.Mul     => a * b,
                UveFpOp.Add     => a + b,
                UveFpOp.Mac     => acc + a * b,
                UveFpOp.Sub     => a - b,
                UveFpOp.Div     => a / b,
                UveFpOp.Min     => MathF.Min(a, b),
                UveFpOp.Max     => MathF.Max(a, b),
                UveFpOp.Abs     => MathF.Abs(a),
                UveFpOp.Inc     => a + 1f,
                UveFpOp.Dec     => a - 1f,
                UveFpOp.Sqrt    => MathF.Sqrt(a),
                UveFpOp.Adde    => a,
                UveFpOp.AddeAcc => acc + a,
                UveFpOp.Mine    => MathF.Min(acc, a),
                UveFpOp.Maxe    => MathF.Max(acc, a),
                _               => throw new InvalidOperationException($"Unknown UveFpOp {op}"),
            };
            results[i] = (uint)BitConverter.SingleToInt32Bits(r);
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoAInt(
        IArchState state,
        IMemory memory,
        UveIntOp op,
        bool signed,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint rawA = uveState.GetLane32(usrc1, i);
            uint rawB = usrc2 >= 0 ? uveState.GetLane32(usrc2, i) : 0u;
            uint rawAcc = uveState.GetLane32(ud, i);
            uint r;
            if (signed) {
                int a = (int)rawA, b = (int)rawB, acc = (int)rawAcc;
                r = (uint)(op switch {
                    UveIntOp.Add     => a + b,
                    UveIntOp.Sub     => a - b,
                    UveIntOp.Mul     => a * b,
                    UveIntOp.Div     => a / b,
                    UveIntOp.Mac     => acc + a * b,
                    UveIntOp.Min     => Math.Min(a, b),
                    UveIntOp.Max     => Math.Max(a, b),
                    UveIntOp.Abs     => Math.Abs(a),
                    UveIntOp.Inc     => a + 1,
                    UveIntOp.Dec     => a - 1,
                    UveIntOp.Adde    => a,
                    UveIntOp.AddeAcc => acc + a,
                    UveIntOp.Mine    => Math.Min(acc, a),
                    UveIntOp.Maxe    => Math.Max(acc, a),
                    _                => throw new InvalidOperationException($"Unknown UveIntOp {op}"),
                });
            }
            else {
                r = op switch {
                    UveIntOp.Add     => rawA + rawB,
                    UveIntOp.Sub     => rawA - rawB,
                    UveIntOp.Mul     => rawA * rawB,
                    UveIntOp.Div     => rawA / rawB,
                    UveIntOp.Mac     => rawAcc + rawA * rawB,
                    UveIntOp.Min     => Math.Min(rawA, rawB),
                    UveIntOp.Max     => Math.Max(rawA, rawB),
                    UveIntOp.Abs     => rawA,
                    UveIntOp.Inc     => rawA + 1u,
                    UveIntOp.Dec     => rawA - 1u,
                    UveIntOp.Adde    => rawA,
                    UveIntOp.AddeAcc => rawAcc + rawA,
                    UveIntOp.Mine    => Math.Min(rawAcc, rawA),
                    UveIntOp.Maxe    => Math.Max(rawAcc, rawA),
                    _                => throw new InvalidOperationException($"Unknown UveIntOp {op}"),
                };
            }

            results[i] = r;
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoALogic(
        IArchState state,
        IMemory memory,
        UveLogicOp op,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint a = uveState.GetLane32(usrc1, i);
            uint b = usrc2 >= 0 ? uveState.GetLane32(usrc2, i) : 0u;
            results[i] = op switch {
                UveLogicOp.Nand => ~(a & b),
                UveLogicOp.And  => a & b,
                UveLogicOp.Nor  => ~(a | b),
                UveLogicOp.Or   => a | b,
                UveLogicOp.Not  => ~a,
                UveLogicOp.Xor  => a ^ b,
                _               => throw new InvalidOperationException($"Unknown UveLogicOp {op}"),
            };
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoAShiftV(
        IArchState state,
        IMemory memory,
        UveShiftOp op,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint a = uveState.GetLane32(usrc1, i);
            var shamt = (int)(uveState.GetLane32(usrc2, i) & 0x1F);
            results[i] = op switch {
                UveShiftOp.Sll => a << shamt,
                UveShiftOp.Srl => a >> shamt,
                UveShiftOp.Sra => (uint)((int)a >> shamt),
                _              => throw new InvalidOperationException($"Unknown UveShiftOp {op}"),
            };
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoAShiftS(
        IArchState state,
        IMemory memory,
        IRegisterFile regs,
        UveShiftOp op,
        int ud,
        int usrc1,
        int rs2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, -1);
        var shamt = (int)(regs.Read(rs2) & 0x1F);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint a = uveState.GetLane32(usrc1, i);
            results[i] = op switch {
                UveShiftOp.Sll => a << shamt,
                UveShiftOp.Srl => a >> shamt,
                UveShiftOp.Sra => (uint)((int)a >> shamt),
                _              => throw new InvalidOperationException($"Unknown UveShiftOp {op}"),
            };
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoASadde(
        IArchState state,
        IRegisterFile regs,
        bool isFp,
        bool acc,
        int rd,
        int usrc1,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        int vLen = uveState.ValidElements[usrc1] > 0 ? uveState.ValidElements[usrc1] : 1;
        bool[] predReg = uveState.PredicateRegs[ps3];
        // predicate byte for element i (float32 = 4 bytes/element): (i+1)*4-1
        const int elemBytesInPred = 4;
        if (isFp) {
            var sum = 0f;
            for (var i = 0; i < vLen; i++)
                if (predReg[(i + 1) * elemBytesInPred - 1])
                    sum += BitConverter.Int32BitsToSingle((int)uveState.GetLane32(usrc1, i));
            float result = acc ? FBits(regs, rd) + sum : sum;
            ulong nanBoxed = 0xFFFFFFFF00000000UL | (uint)BitConverter.SingleToInt32Bits(result);
            return new ExecuteResult { SideEffect = s => { UState(s).IntegerRegisters.Write(rd, nanBoxed); }, };
        }
        else {
            var sum = 0;
            for (var i = 0; i < vLen; i++)
                if (predReg[(i + 1) * elemBytesInPred - 1])
                    sum += (int)uveState.GetLane32(usrc1, i);
            int result = acc ? (int)(uint)regs.Read(rd) + sum : sum;
            return new ExecuteResult { SideEffect = s => { UState(s).IntegerRegisters.Write(rd, (uint)result); }, };
        }
    }

    // SO_C: stream lifecycle — stop / suspend / resume.
    private static ExecuteResult ExecuteUveSoCBreak(int ud) =>
        new() {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.StoreStreams[ud] = null;
                uvs.RegKind[ud] = UveRegKind.None;
                uvs.StreamDone[ud] = true;
                uvs.Suspended[ud] = false;
            },
        };

    private static ExecuteResult ExecuteUveSoCSuspd(int ud) =>
        new() { SideEffect = s => { UState(s).UveState.Suspended[ud] = true; }, };

    private static ExecuteResult ExecuteUveSoCResum(int ud) =>
        new() { SideEffect = s => { UState(s).UveState.Suspended[ud] = false; }, };

    // SO_C: vector-length control — getvl / setvl.
    private static ExecuteResult ExecuteUveSoCGetvl(IArchState state, int rd) {
        int vl = UState(state).UveState.VectorLength;
        return new ExecuteResult { SideEffect = s => { UState(s).IntegerRegisters.Write(rd, (uint)vl); }, };
    }

    private static ExecuteResult ExecuteUveSoCSetvl(IArchState state, IRegisterFile regs, int rd, int rs1) {
        int oldVl = UState(state).UveState.VectorLength;
        var newVl = (int)(uint)regs.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                UState(s).UveState.VectorLength = newVl;
                UState(s).IntegerRegisters.Write(rd, (uint)oldVl);
            },
        };
    }

    // Returns (vLen, zeroing).
    // vLen: 1 if either source is Scalar; otherwise the min of sources' ValidElements counts
    //       (0 → 4 as safe default). This makes the out-of-range zeroing/merging loop in
    //       UveWriteResult actually fire when VL < VLEN/ew (e.g., after ss.setvl).
    // zeroing: true when no source register has pm=1 (merging), so lanes beyond vLen are zeroed.
    private static (int vLen, bool zeroing) UveLaneParams(UveState u, int usrc1, int usrc2) {
        bool s1Scalar = u.RegMode[usrc1] == UveRegMode.Scalar;
        bool s2Scalar = usrc2 < 0 || u.RegMode[usrc2] == UveRegMode.Scalar;
        int vLen;
        if (s1Scalar || s2Scalar) { vLen = 1; }
        else {
            int v1 = u.ValidElements[usrc1] > 0 ? u.ValidElements[usrc1] : 4;
            int v2 = usrc2 < 0 ? v1 : u.ValidElements[usrc2] > 0 ? u.ValidElements[usrc2] : 4;
            vLen = Math.Min(v1, v2);
        }

        bool zeroing = !u.RegMerging[usrc1] && (usrc2 < 0 || !u.RegMerging[usrc2]);
        return (vLen, zeroing);
    }

    // Shared write-back for so.a.* ops: writes vLen lane results to store stream or u-register.
    // ps3 selects the governing predicate register (p0 = all-ones = all lanes active).
    // Per Spike semantics: predicate-inactive lanes always merge (keep existing dest value);
    // zeroing the flag only applies to lanes beyond vLen (controlled by stream pm).
    private static ExecuteResult UveWriteResult(
        IArchState state,
        IMemory memory,
        int ud,
        uint[] results,
        int vLen,
        bool zeroing,
        int ps3
    ) {
        // Predicate byte for lane i (float32 = 4 bytes/element): (i+1)*4-1 = i*4+3
        const int elemBytesInPred = 4;
        UveState uveState = UState(state).UveState;
        if (uveState.RegKind[ud] == UveRegKind.StoreStream && uveState.StoreStreams[ud] is { } ss) {
            int ewBytes = ss.ElementBytes;
            bool[] predReg = uveState.PredicateRegs[ps3];
            for (var i = 0; i < vLen; i++) {
                if (predReg[(i + 1) * elemBytesInPred - 1]) memory.Write(ss.CurrentAddress, results[i], ewBytes);
                ss.Advance(); // always advance stream position, even for inactive lanes
            }

            return new ExecuteResult {
                SideEffect = s => {
                    UveState uvs = UState(s).UveState;
                    bool[] pr = uvs.PredicateRegs[ps3];
                    for (var i = 0; i < vLen; i++)
                        if (pr[(i + 1) * elemBytesInPred - 1])
                            uvs.SetLane32(ud, i, results[i]);
                    uvs.RegMode[ud] = vLen == 1 ? UveRegMode.Scalar : UveRegMode.Vector;
                    uvs.ValidElements[ud] = vLen;
                },
            };
        }

        const int maxLanes = 4;
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                bool[] pr = uvs.PredicateRegs[ps3];
                for (var i = 0; i < vLen; i++)
                    if (pr[(i + 1) * elemBytesInPred - 1])
                        uvs.SetLane32(ud, i, results[i]);
                // predicate-inactive lanes: merging — no write, existing value preserved
                if (zeroing)
                    for (int i = vLen; i < maxLanes; i++)
                        uvs.SetLane32(ud, i, 0);
                uvs.RegMode[ud] = vLen == 1 ? UveRegMode.Scalar : UveRegMode.Vector;
                uvs.ValidElements[ud] = vLen;
            },
        };
    }

    // sb.nc urs, imm — branch (PC += imm) while stream urs is not exhausted
    // The pipeline has already synced the exhaustion state into UveState via IUveScalars.
    private static ExecuteResult ExecuteUveSoBNc(IArchState state, ulong pc, int urs, int imm) {
        bool done = UState(state).UveState.StreamDone[urs];
        return !done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // sb.c urs, imm — branch when stream urs IS exhausted (complete polarity of sb.nc)
    private static ExecuteResult ExecuteUveSoBc(IArchState state, ulong pc, int urs, int imm) {
        bool done = UState(state).UveState.StreamDone[urs];
        return done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // ss.sta.{ld|st}.* — start multi-dim stream configuration. Sets base and element width; no dimension added.
    private static ExecuteResult ExecuteUveSsSta(
        IRegisterFile regs,
        int ud,
        int rs1,
        bool isLoad,
        int ew,
        bool isVec = false,
        int vecCfgDim = -1,
        bool mergingPredication = false
    ) {
        ulong baseAddr = regs.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.PendingConfig[ud] = new PendingStreamConfig {
                    BaseAddress = baseAddr, ElementBytes = ew, IsLoad = isLoad,
                    IsVector = isVec, VecCfgDim = vecCfgDim, MergingPredication = mergingPredication,
                };
            },
        };
    }

    // ss.sta.ld.*_inds ud, rs1 — begin IndSource stream configuration (provides values for indirect modifiers).
    private static ExecuteResult ExecuteUveSsStaLdWInds(IRegisterFile regs, int ud, int rs1, int ew) {
        ulong baseAddr = regs.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.PendingConfig[ud] = new PendingStreamConfig {
                    BaseAddress = baseAddr, ElementBytes = ew, IsLoad = true, IsIndSource = true,
                };
            },
        };
    }

    // ss.app.ind ud, rs1_indsrc — append one indirect modifier to the pending stream config.
    // The trigger is positional: the most recently appended dimension at execute time (Spike
    // keys modifiers to dimensions.size()-1). Pending modifiers hold SPIKE (outermost-first)
    // indices in TriggerDim/TargetDim; ExecuteUveSsEnd remaps both to engine order.
    private static ExecuteResult ExecuteUveSsAppInd(
        int ud,
        int targetDimRaw,
        StreamModifierTarget target,
        StreamModifierBehavior behavior,
        int sourceStreamId
    ) {
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null || cfg.Dimensions.Count == 0) return;
                int spikeTrigger = cfg.Dimensions.Count - 1;
                int spikeTarget = targetDimRaw == 7 ? spikeTrigger + 1 : targetDimRaw;
                cfg.Modifiers.Add(new StreamModifier(spikeTrigger, spikeTarget, target, behavior, 0, sourceStreamId));
            },
        };
    }

    // ss.app ud, rs1_offset, rs2_count, rs3_stride — append the next dimension to the pending config.
    // Dimensions are configured OUTERMOST-FIRST (Spike: dimensions.push_back, dimensions.back() = innermost);
    // ss.end appends the innermost dimension. The list is reversed into the engine's innermost-first
    // order when the stream is activated.
    // rs1_offset adds offset*ew to the stream base address (accumulated into PendingStreamConfig.OffsetBytes).
    // rs3_stride is an element count (Spike/RTL convention); scaled to bytes here before storing.
    private static ExecuteResult ExecuteUveSsApp(IRegisterFile regs, int ud, int rs1, int rs2, int rs3) {
        var offset = (long)regs.Read(rs1);
        var count = (long)regs.Read(rs2);
        var stride = (long)regs.Read(rs3);
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null) return;
                cfg.OffsetBytes += offset * cfg.ElementBytes;
                cfg.Dimensions.Add(new StreamDimension(count, stride * cfg.ElementBytes));
            },
        };
    }

    // ss.end ud, rs1_offset, rs2_count, rs3_stride — innermost dimension + activate stream.
    // Config order is outermost-first (Spike convention), so the accumulated dimension list is
    // reversed into the engine's innermost-first order here. Modifier DimIndex values (ss.app.mod,
    // ss.app.ind) and explicit vecCfgDim are encoded outermost-first and remapped the same way.
    // rs1_offset adds offset*ew to the stream base address (combined with any prior ss.app offsets).
    // rs3_stride is an element count (Spike/RTL convention); scaled to bytes here before storing.
    private static ExecuteResult ExecuteUveSsEnd(
        IArchState state,
        IRegisterFile regs,
        int ud,
        int rs1,
        int rs2,
        int rs3
    ) {
        var offset = (long)regs.Read(rs1);
        var count = (long)regs.Read(rs2);
        var stride = (long)regs.Read(rs3);
        UveState uveState = UState(state).UveState;
        PendingStreamConfig? pending = uveState.PendingConfig[ud];
        if (pending is null) return ExecuteResult.Clean;

        long totalOffsetBytes = pending.OffsetBytes + offset * pending.ElementBytes;
        var baseAddr = (ulong)((long)pending.BaseAddress + totalOffsetBytes);
        StreamDimension[] dims = pending.Dimensions.Append(new StreamDimension(count, stride * pending.ElementBytes))
                                        .Reverse().ToArray();
        return BuildAndActivatePendingStream(ud, pending, baseAddr, dims);
    }

    // ss.app.sgi ud, rs1_indsrc — attach scatter-gather modifier to the pending config.
    // Fires per element, always targeting Offset of dimension 0; encoded in PendingStreamConfig.SgiMod.
    private static ExecuteResult ExecuteUveSsAppSgi(int ud, int srcId, StreamModifierBehavior behavior) {
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null) return;
                cfg.SgiMod = (srcId, behavior);
            },
        };
    }

    // ss.end.sgi ud, rs1_indsrc — attach scatter-gather modifier + activate stream.
    // Does NOT add a new dimension; existing dimensions from prior ss.app instructions are used.
    private static ExecuteResult ExecuteUveSsEndSgi(
        IArchState state,
        int ud,
        int srcId,
        StreamModifierBehavior behavior
    ) {
        UveState uveState = UState(state).UveState;
        PendingStreamConfig? pending = uveState.PendingConfig[ud];
        if (pending is null || pending.Dimensions.Count == 0) return ExecuteResult.Clean;

        pending.SgiMod = (srcId, behavior);
        var baseAddr = (ulong)((long)pending.BaseAddress + pending.OffsetBytes);
        StreamDimension[] dims = ((IEnumerable<StreamDimension>)pending.Dimensions).Reverse().ToArray();
        return BuildAndActivatePendingStream(ud, pending, baseAddr, dims);
    }

    // Shared activation helper: builds a StreamDescriptor from pending config + already-reversed dims
    // and returns the appropriate ExecuteResult (load/IndSource → StreamConfig; store → SideEffect).
    private static ExecuteResult BuildAndActivatePendingStream(
        int ud,
        PendingStreamConfig pending,
        ulong baseAddr,
        StreamDimension[] dims
    ) {
        int ndim = dims.Length;

        // Remap modifier dims and explicit vecCfgDim from Spike (outermost=0) to engine (innermost=0).
        // Pending TriggerDim holds the Spike deque index K of the dimension the modifier was appended
        // after; that dimension ADVANCES when its inner neighbor (deque K+1) wraps, so the engine
        // trigger is ndim-2-K. TargetDim is a plain index remapping.
        StreamModifier[]? mods = null;
        if (pending.Modifiers.Count > 0)
            mods = pending.Modifiers
                          .Select(m => m with {
                                   TriggerDim = ndim - 2 - m.TriggerDim, TargetDim = ndim - 1 - m.TargetDim,
                               }
                           )
                          .ToArray();
        int vecCfgDim = pending.VecCfgDim >= 0 ? ndim - 1 - pending.VecCfgDim : -1;

        var descriptor = new StreamDescriptor(
            baseAddr, pending.ElementBytes, dims, mods, pending.IsVector, vecCfgDim, pending.MergingPredication,
            pending.SgiMod
        );
        bool isLoad = pending.IsLoad;
        bool isIndSource = pending.IsIndSource;

        if (isLoad || isIndSource) {
            UveRegKind regKind = isIndSource ? UveRegKind.IndSource : UveRegKind.LoadStream;
            return new ExecuteResult {
                StreamConfig = (ud, descriptor),
                SideEffect = s => {
                    UveState uvs = UState(s).UveState;
                    uvs.PendingConfig[ud] = null;
                    uvs.RegKind[ud] = regKind;
                },
            };
        }

        // Store stream: full multi-dim cursor, innermost dimension first.
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.PendingConfig[ud] = null;
                var ss = new UveStoreStream {
                    BaseAddress = descriptor.BaseAddress, ElementBytes = descriptor.ElementBytes,
                    Dimensions = dims, Indices = new long[dims.Length],
                };
                ss.Initialize();
                uvs.StoreStreams[ud] = ss;
                uvs.RegKind[ud] = UveRegKind.StoreStream;
            },
        };
    }

    // ss.app.mod ud, tdim, target, behavior, rs3Disp — append static modifier (UVE2).
    // Trigger is positional (the most recently appended dimension); tdim is the target dimension
    // in Spike outermost-first order (7 = "linked" → the dimension configured right after the
    // trigger). Pending modifiers hold Spike indices; ExecuteUveSsEnd remaps to engine order.
    private static ExecuteResult ExecuteUveSsAppMod(
        IRegisterFile regs,
        int ud,
        int targetDimRaw,
        StreamModifierTarget target,
        StreamModifierBehavior behavior,
        int rs3Disp
    ) {
        var disp = (long)regs.Read(rs3Disp);
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null || cfg.Dimensions.Count == 0) return;
                int spikeTrigger = cfg.Dimensions.Count - 1;
                int spikeTarget = targetDimRaw == 7 ? spikeTrigger + 1 : targetDimRaw;
                // Stride displacement is an element count; scale to bytes for the engine.
                // Offset displacement stays as element count (engine scales internally).
                // Size displacement is already a count; no scaling.
                long scaledDisp = target == StreamModifierTarget.Stride ? disp * cfg.ElementBytes : disp;
                cfg.Modifiers.Add(new StreamModifier(spikeTrigger, spikeTarget, target, behavior, scaledDisp));
            },
        };
    }

    // sb.ndc.D urs, imm — branch while the dimension has not completed its pass.
    // dim = funct3 = D-1, counting from the OUTERMOST dimension (Spike convention);
    // the pipeline has already remapped and synced the flag into UveState.DimDone[urs, dim].
    private static ExecuteResult ExecuteUveSoBNdc(IArchState state, ulong pc, int urs, int dim, int imm) {
        bool done = UState(state).UveState.DimDone[urs, dim];
        return !done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // sb.dc.D urs, imm — branch when dimension D IS complete (complete polarity of sb.ndc)
    private static ExecuteResult ExecuteUveSoBdc(IArchState state, ulong pc, int urs, int dim, int imm) {
        bool done = UState(state).UveState.DimDone[urs, dim];
        return done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // ── SO_P predicate register operations ───────────────────────────────────

    // so.p.{zero,one,vr,not,mv,mvt} pd, ... — manipulate predicate register pd.
    // GovPred[i]=true → apply operation; GovPred[i]=false → zeroing ? 0 : keep old.
    private static ExecuteResult ExecuteUveSoPSimple(
        IArchState state,
        UveSoPSimpleOp op,
        int pd,
        int govPred,
        bool zeroing,
        int ps1
    ) {
        UveState uvs = UState(state).UveState;
        // Capture the valid element count for Vr before the closure.
        int validCount = uvs.VectorLength > 0 ? uvs.VectorLength : UveState.PredBytes;
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                bool[] gov = u.PredicateRegs[govPred];
                bool[] dst = u.PredicateRegs[pd];
                bool[] src = ps1 >= 0 ? u.PredicateRegs[ps1] : [];
                for (var i = 0; i < UveState.PredBytes; i++) {
                    if (!gov[i]) {
                        if (zeroing) dst[i] = false;
                        continue;
                    }

                    dst[i] = op switch {
                        UveSoPSimpleOp.Zero => false,
                        UveSoPSimpleOp.One  => true,
                        UveSoPSimpleOp.Vr   => i < validCount,
                        UveSoPSimpleOp.Not  => !src[i],
                        UveSoPSimpleOp.Mv   => src[i],
                        UveSoPSimpleOp.Mvt  => src[UveState.PredBytes - 1 - i],
                        _                   => throw new InvalidOperationException($"Unknown UveSoPSimpleOp {op}"),
                    };
                }
            },
        };
    }

    // so.p.{ge,eq,lt}.{us,fp,sg}[.z] pd, vs1, vs2 — element-wise comparison into predicate register.
    // Scalar sources → uniform result broadcast to all governed bytes.
    // Both vector → per-lane result (4 bytes per lane for float32 width).
    // zeroing=true (_z variant): sets PredZeroing[pd]=true — tags the output register Zeroing.
    private static ExecuteResult ExecuteUveSoPCmp(
        IArchState state,
        UveSoPCmpOp op,
        UveSoPCmpType cmpType,
        int pd,
        int govPred,
        int vs1,
        int vs2,
        bool zeroing
    ) {
        UveState uvs = UState(state).UveState;
        bool isVector = uvs.RegMode[vs1] == UveRegMode.Vector && uvs.RegMode[vs2] == UveRegMode.Vector;
        int vLen = isVector
            ? Math.Max(
                uvs.ValidElements[vs1] > 0 ? uvs.ValidElements[vs1] : 1,
                uvs.ValidElements[vs2] > 0 ? uvs.ValidElements[vs2] : 1
            )
            : 1;
        var laneResults = new bool[vLen];
        for (var i = 0; i < vLen; i++) {
            uint rawA = uvs.GetLane32(vs1, i);
            uint rawB = uvs.GetLane32(vs2, i);
            float fa = BitConverter.Int32BitsToSingle((int)rawA);
            float fb = BitConverter.Int32BitsToSingle((int)rawB);
            laneResults[i] = (op, cmpType) switch {
                (UveSoPCmpOp.Ge, UveSoPCmpType.Us) => rawA >= rawB,
                (UveSoPCmpOp.Ge, UveSoPCmpType.Sg) => (int)rawA >= (int)rawB,
                (UveSoPCmpOp.Ge, UveSoPCmpType.Fp) => fa >= fb,
                (UveSoPCmpOp.Eq, UveSoPCmpType.Us) => rawA == rawB,
                (UveSoPCmpOp.Eq, UveSoPCmpType.Sg) => (int)rawA == (int)rawB,
                (UveSoPCmpOp.Eq, UveSoPCmpType.Fp) => fa == fb,
                (UveSoPCmpOp.Lt, UveSoPCmpType.Us) => rawA < rawB,
                (UveSoPCmpOp.Lt, UveSoPCmpType.Sg) => (int)rawA < (int)rawB,
                (UveSoPCmpOp.Lt, UveSoPCmpType.Fp) => fa < fb,
                _ => throw new InvalidOperationException($"Unknown SO_P comparison {op}/{cmpType}"),
            };
        }

        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                bool[] gov = u.PredicateRegs[govPred];
                bool[] dst = u.PredicateRegs[pd];
                if (!isVector) {
                    bool r = laneResults[0];
                    for (var i = 0; i < UveState.PredBytes; i++)
                        if (gov[i])
                            dst[i] = r;
                }
                else {
                    for (var i = 0; i < vLen; i++) {
                        int predBase = i * 4;
                        int repIdx = predBase + 3;
                        if (repIdx < UveState.PredBytes && gov[repIdx]) {
                            bool r = laneResults[i];
                            for (int k = predBase; k <= repIdx; k++) dst[k] = r;
                        }
                    }
                }

                u.PredZeroing[pd] = zeroing;
            },
        };
    }

    // so.v.mv/mvt vd, vs1, pred — copy vs1 into vd where predicate PredIdx is active (merging).
    // Transpose variant (mvt) reverses the active range.
    // When vd is a store-stream register, the copy writes through to memory immediately (like
    // UveWriteResult's arithmetic-op store-stream path and OoOE's vector stores generally: the
    // write is not deferred to SideEffect commit) rather than only updating vd's own lanes —
    // confirmed against Spike (so_v_mv.h/so_v_mvt.h both write through the generic register
    // setElements() path, which handles the store-stream case uniformly for every writer).
    private static ExecuteResult ExecuteUveSoVMv(
        IArchState state, IMemory memory, bool transpose, int vd, int vs1, int predIdx
    ) {
        UveState uvs = UState(state).UveState;
        bool isVector = uvs.RegMode[vs1] == UveRegMode.Vector;
        int vLen = isVector ? uvs.ValidElements[vs1] > 0 ? uvs.ValidElements[vs1] : 1 : 1;
        var srcLanes = new uint[vLen];
        for (var i = 0; i < vLen; i++) srcLanes[i] = uvs.GetLane32(vs1, i);

        UveStoreStream? storeStream = uvs.RegKind[vd] == UveRegKind.StoreStream ? uvs.StoreStreams[vd] : null;
        if (storeStream is { } ss) {
            bool[] predNow = uvs.PredicateRegs[predIdx];
            int ewBytes = ss.ElementBytes;
            if (!isVector) {
                int checkIdx = transpose ? UveState.PredBytes - 1 : 0;
                if (predNow[checkIdx]) memory.Write(ss.CurrentAddress, srcLanes[0], ewBytes);
                ss.Advance();
            }
            else {
                for (var i = 0; i < vLen; i++) {
                    int predByte = transpose ? UveState.PredBytes - 1 - (i * 4 + 3) : i * 4 + 3;
                    if (predByte is >= 0 and < UveState.PredBytes && predNow[predByte])
                        memory.Write(ss.CurrentAddress, srcLanes[i], ewBytes);
                    ss.Advance();
                }
            }
        }

        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                bool[] pred = u.PredicateRegs[predIdx];
                if (!isVector) {
                    int checkIdx = transpose ? UveState.PredBytes - 1 : 0;
                    if (pred[checkIdx]) {
                        u.SetLane32(vd, 0, srcLanes[0]);
                        u.RegMode[vd] = UveRegMode.Scalar;
                        u.ValidElements[vd] = 1;
                        if (storeStream is null) u.RegKind[vd] = UveRegKind.Scalar;
                    }
                }
                else {
                    for (var i = 0; i < vLen; i++) {
                        int predByte = transpose ? UveState.PredBytes - 1 - (i * 4 + 3) : i * 4 + 3;
                        if (predByte is >= 0 and < UveState.PredBytes && pred[predByte])
                            u.SetLane32(vd, i, srcLanes[i]);
                    }
                }
            },
        };
    }

    // so.p.cv.<srcW>.<destW>[.z] pd, ps1 — predicate register width conversion.
    // Maps the active bit for each element from the source width slot to the destination width slot.
    // Horologium active-bit position for element i of width W: (i+1)*W - 1 (MSByte).
    // nElems = PredBytes / max(srcBytes, destBytes): elements that fit in both widths.
    private static ExecuteResult ExecuteUveSoPCv(
        IArchState state,
        int pd,
        int ps1,
        int srcBytes,
        int destBytes,
        bool zeroing
    ) {
        UveState uvs = UState(state).UveState;
        bool[] src = uvs.PredicateRegs[ps1];
        int nElems = UveState.PredBytes / Math.Max(srcBytes, destBytes);
        var destPred = new bool[UveState.PredBytes];
        for (var i = 0; i < nElems; i++) destPred[(i + 1) * destBytes - 1] = src[(i + 1) * srcBytes - 1];
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                Array.Copy(destPred, u.PredicateRegs[pd], UveState.PredBytes);
                u.PredZeroing[pd] = zeroing;
            },
        };
    }

    // so.v.cv.{fp,sg,us}.<destW> vd, vs1 — vector u-register element type conversion.
    // Reads ValidElements[vs1] lanes from vs1 (interpreting each as RegElemBytes[vs1]-wide),
    // converts to destBytes width, and writes to vd.
    // isFp=true: floating-point cast; isSigned=true: sign-extend; else zero-extend.
    private static ExecuteResult ExecuteUveSoVCv(
        IArchState state,
        int vd,
        int vs1,
        int destBytes,
        bool isFp,
        bool isSigned
    ) {
        UveState uvs = UState(state).UveState;
        int srcBytes = uvs.RegElemBytes[vs1] > 0 ? uvs.RegElemBytes[vs1] : 4;
        int srcValid = Math.Max(1, uvs.ValidElements[vs1]);
        int finalCount = Math.Min(4, srcValid);
        var converted = new uint[finalCount];
        for (var i = 0; i < finalCount; i++) {
            uint raw = uvs.GetLane32(vs1, i);
            converted[i] = isFp
                ? ConvertFpLane(raw, srcBytes, destBytes)
                : ConvertIntLane(raw, srcBytes, destBytes, isSigned);
        }

        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                for (var i = 0; i < finalCount; i++) u.SetLane32(vd, i, converted[i]);
                u.RegMode[vd] = uvs.RegMode[vs1];
                u.ValidElements[vd] = finalCount;
                u.RegElemBytes[vd] = destBytes;
            },
        };
    }

    // Mask raw to srcBytes width, then sign- or zero-extend to destBytes width stored as uint32.
    private static uint ConvertIntLane(uint raw, int srcBytes, int destBytes, bool signed) {
        uint mask = srcBytes >= 4 ? uint.MaxValue : (1u << (srcBytes * 8)) - 1u;
        uint narrow = raw & mask;
        if (signed && srcBytes < 4) {
            uint signBit = 1u << (srcBytes * 8 - 1);
            if ((narrow & signBit) != 0) narrow |= ~mask;
        }

        // Truncate to destBytes width before storing as uint32.
        uint destMask = destBytes >= 4 ? uint.MaxValue : (1u << (destBytes * 8)) - 1u;
        return narrow & destMask;
    }

    // Floating-point conversion between lane widths (stored as uint32 bits).
    // Only the combinations meaningful for a 32-bit lane model are handled:
    // fp.h (float32→float16), fp.w (float32→float32 = identity), others default to identity.
    private static uint ConvertFpLane(uint raw, int srcBytes, int destBytes) {
        switch (srcBytes) {
            case 4 when destBytes == 2: {
                float f = BitConverter.Int32BitsToSingle((int)raw);
                var h = (Half)f;
                return BitConverter.HalfToUInt16Bits(h);
            }
            case 2 when destBytes == 4: {
                Half h = BitConverter.UInt16BitsToHalf((ushort)raw);
                return (uint)BitConverter.SingleToInt32Bits((float)h);
            }
            default: return raw; // identity for matching widths or unsupported combos
        }
    }
}