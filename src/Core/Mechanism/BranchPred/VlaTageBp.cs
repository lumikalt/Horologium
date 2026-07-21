namespace Mechanism.BranchPred;

/// <summary>
///     VLA-TAGE: Vector-Loop-Aware TAGE — Zhang et al., IEEE CAL 2026.
///     <para>
///         Extends TAGE-SC-L with a power-gating mechanism that bypasses the tagged
///         history tables (T1 - T3) and the Statistical Corrector when the innermost
///         vector loop has sufficiently many remaining iterations.  Only the bimodal
///         T0 and the inherited loop predictor remain active while gating.
///     </para>
///     <para>
///         A Vector Loop Table (VLT) with eight entries tracks backward branches.
///         The pipeline calls <see cref="NotifyVectorInstruction" /> at every vector
///         instruction's execution time, and <see cref="NotifyLoopBranchExecute" /> at
///         every taken backward-branch's execution time with the two comparison-register
///         values.  The Loop Monitor (LM) uses those register values to estimate
///         remaining iterations without expensive division: it compares the current
///         induction variable against the loop bound after applying the step size.
///     </para>
///     <para>
///         The PEN latch for a given backward branch is asserted when estimated
///         remaining iterations ≥ MinIterThreshold (32), and the loop body has
///         contained at least one vector instruction. PEN is deasserted early when
///         remaining ≤ LoopTermThreshold (5), expecting the loop exit branch
///         prediction by the pipeline latency between Issue and Fetch (per the paper).
///     </para>
///     <para>
///         Unlike the global history register (which the base <see cref="LTageBp" /> now keeps
///         speculative), the VLT is intentionally <em>not</em> fetch-speculative: it is driven by
///         register operand values delivered at (<see cref="NotifyLoopBranchExecute" />), which
///         are unknown at fetch, so there is no predicted direction to fold in speculatively. The
///         inherited speculative <c>Ghr</c> is all the fetch-time history VLA-TAGE carries.
///     </para>
/// </summary>
public sealed class VlaTageBp : TageScLBp, IVectorAwareBranchPredictor {
    private const int VltSize = 8;
    private const int MinIterThreshold = 32;
    private const int LoopTermThreshold = 5;

    private readonly VltEntry[] _vlt = new VltEntry[VlaTageBp.VltSize];

    /// <summary>
    ///     Number of predictions made under the PEN gating signal
    ///     (history tables and SC bypassed).  Useful as a proxy for power savings.
    /// </summary>
    public int GatedPredictions { get; private set; }

    // ── IVectorAwareBranchPredictor ───────────────────────────────────────────

    /// <inheritdoc />
    public void NotifyVectorInstruction(ulong pc) {
        for (var i = 0; i < VlaTageBp.VltSize; i++) {
            ref VltEntry e = ref _vlt[i];
            // Mark only if we know the loop bounds; Target=0 means not yet resolved.
            if (e is { IsValid: true, Target: > 0, } && e.Target <= pc && pc <= e.Pc) e.IsVectorLoop = true;
        }
    }

    /// <inheritdoc />
    public void NotifyLoopBranchExecute(ulong branchPc, ulong loopTarget, ulong rs1, ulong rs2) {
        int slot = FindSlot(branchPc);

        if (slot < 0) {
            // First encounter (OooeTrain: execute precedes commit — create entry here).
            slot = FindOrAllocate(branchPc);
            ref VltEntry n = ref _vlt[slot];
            n = new VltEntry {
                Pc = branchPc, Target = loopTarget,
                IsValid = true, PrevRs1 = rs1, PrevRs2 = rs2,
                HaveFirst = true, EstimatedRemaining = -1,
            };
            return;
        }

        ref VltEntry e = ref _vlt[slot];
        e.Target = loopTarget; // keep target current

        if (!e.HaveFirst) {
            e.PrevRs1 = rs1;
            e.PrevRs2 = rs2;
            e.HaveFirst = true;
            return;
        }

        // Determine which register is the induction variable (changed) and which
        // is the bound (unchanged).  If exactly one register 
        // is changed, we can compute the increment and remaining.
        bool rs1Changed = rs1 != e.PrevRs1;
        bool rs2Changed = rs2 != e.PrevRs2;

        if (rs1Changed ^ rs2Changed) {
            long cur = rs1Changed ? (long)rs1 : (long)rs2;
            long prev = rs1Changed ? (long)e.PrevRs1 : (long)e.PrevRs2;
            long bnd = rs1Changed ? (long)rs2 : (long)rs1;
            long inc = cur - prev;
            if (inc != 0) {
                long rem = (bnd - cur) / inc;
                e.EstimatedRemaining = rem >= 0 ? rem : -1L;
            }
        }
        // If both changed or neither changed, the pattern is irregular; keep the
        // previous estimate unchanged so a momentary blip does not drop PEN.

        e.PrevRs1 = rs1;
        e.PrevRs2 = rs2;

        // PEN latch: hysteresis between MinIterThreshold and LoopTermThreshold.
        e.PenLatch = e switch {
            { PenLatch: false, IsVectorLoop: true, EstimatedRemaining: >= VlaTageBp.MinIterThreshold, } => true,
            { PenLatch: true, EstimatedRemaining: >= 0 and <= VlaTageBp.LoopTermThreshold, }            => false,
            _                                                                                           => e.PenLatch,
        };
    }

    /// <inheritdoc />
    public override void Update(ulong pc, bool taken, ulong actualTarget) {
        UpdateVlt(pc, taken, actualTarget);
        base.Update(pc, taken, actualTarget);
    }

    // ── IBranchPredictor overrides ────────────────────────────────────────────

    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        if (IsPenActive(pc)) {
            GatedPredictions++;
            return BimodalPrediction(pc);
        }

        return base.ResolvePrediction(pc, provider, tagePred);
    }

    // ── VLT internals ─────────────────────────────────────────────────────────

    private bool IsPenActive(ulong pc) {
        int slot = FindSlot(pc);
        if (slot < 0) return false;
        ref VltEntry e = ref _vlt[slot];
        return e is { IsValid: true, IsVectorLoop: true, PenLatch: true, };
    }

    private void UpdateVlt(ulong pc, bool taken, ulong actualTarget) {
        switch (taken) {
            case true when actualTarget < pc: {
                int slot = FindOrAllocate(pc);
                ref VltEntry e = ref _vlt[slot];
                if (!e.IsValid)
                    e = new VltEntry { Pc = pc, Target = actualTarget, IsValid = true, };
                else
                    e.Target = actualTarget;
                break;
            }
            case false: {
                int slot = FindSlot(pc);
                if (slot >= 0) _vlt[slot] = new VltEntry(); // invalidate entire entry
                break;
            }
        }
    }

    private int FindSlot(ulong pc) {
        for (var i = 0; i < VlaTageBp.VltSize; i++)
            if (_vlt[i].IsValid && _vlt[i].Pc == pc)
                return i;
        return -1;
    }

    private int FindOrAllocate(ulong pc) {
        for (var i = 0; i < VlaTageBp.VltSize; i++)
            if (_vlt[i].IsValid && _vlt[i].Pc == pc)
                return i;
        for (var i = 0; i < VlaTageBp.VltSize; i++)
            if (!_vlt[i].IsValid)
                return i;
        return (int)(pc >> 2) % VlaTageBp.VltSize; // evict via PC hash
    }

    /// <summary>
    ///     Serializes the inherited TAGE-SC-L state (via <c>base</c>) plus the Vector Loop Table.
    ///     Deliberately does not serialize <see cref="GatedPredictions" />, a pure inspection
    ///     statistic.
    /// </summary>
    public override void WriteState(BinaryWriter w) {
        base.WriteState(w);
        foreach (VltEntry e in _vlt) {
            w.Write(e.Pc);
            w.Write(e.Target);
            w.Write(e.PrevRs1);
            w.Write(e.PrevRs2);
            w.Write(e.EstimatedRemaining);
            w.Write(e.IsValid);
            w.Write(e.IsVectorLoop);
            w.Write(e.PenLatch);
            w.Write(e.HaveFirst);
        }
    }

    /// <summary>Restores state written by <see cref="WriteState" />.</summary>
    public override void ReadState(BinaryReader r) {
        base.ReadState(r);
        for (var i = 0; i < _vlt.Length; i++)
            _vlt[i] = new VltEntry {
                Pc = r.ReadUInt64(), Target = r.ReadUInt64(), PrevRs1 = r.ReadUInt64(), PrevRs2 = r.ReadUInt64(),
                EstimatedRemaining = r.ReadInt64(), IsValid = r.ReadBoolean(), IsVectorLoop = r.ReadBoolean(),
                PenLatch = r.ReadBoolean(), HaveFirst = r.ReadBoolean(),
            };
    }
}

internal struct VltEntry {
    public ulong Pc;
    public ulong Target;  // loop head address
    public ulong PrevRs1; // rs1 from previous LM call
    public ulong PrevRs2;
    public long EstimatedRemaining; // -1 = unknown
    public bool IsValid;
    public bool IsVectorLoop; // body contains at least one vector instruction
    public bool PenLatch;     // current PEN output for this back-edge
    public bool HaveFirst;    // first register sample captured
}