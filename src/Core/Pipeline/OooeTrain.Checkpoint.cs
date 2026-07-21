#region

using Mechanism;
using Pipeline.Ooo;

#endregion

namespace Pipeline;

// ── Microarchitectural checkpointing (TODO.md Analysis: Option B) ──────────────
//
// A microcheckpoint is only valid at a *drained* boundary — no in-flight instructions
// anywhere in the pipeline. At that boundary the ROB/issue queues/load-store queues/
// decode-rename latches/exec-CDB buffers/in-flight-MSHR-wait list are all empty by
// construction, so the only state worth carrying across a checkpoint is the trained
// steady-state tables: caches, TLBs, the branch predictor, and the RAS. See
// MicroarchitecturalCheckpoint's doc comment and TODO.md/README.md for the rationale.

public sealed partial class OooeTrain {
    /// <summary>
    ///     True when the pipeline has no in-flight instructions anywhere — the boundary a
    ///     microarchitectural checkpoint requires. See <see cref="Drain" />.
    /// </summary>
    public bool IsDrained => _core.IsDrained;

    /// <summary>
    ///     Stops admitting new instructions and steps the pipeline until it reaches a drained
    ///     boundary (<see cref="IsDrained" />) or halts. Call after <see cref="BeginStepping" /> (or
    ///     partway through a run via the stepping API); the pipeline resumes fetching normally once
    ///     <see cref="SaveMicroCheckpoint" /> (or nothing) is called next.
    /// </summary>
    /// <param name="maxTicks">
    ///     Upper bound on ticks spent draining, or -1 for a default proportional to ROB capacity.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     The pipeline halted (program exit) before draining, or exceeded <paramref name="maxTicks" />.
    /// </exception>
    public void Drain(long maxTicks = -1) => _core.Drain(maxTicks);

    /// <summary>
    ///     Saves a microarchitectural checkpoint: the architectural state (PC, registers, memory,
    ///     CSRs — via <see cref="ArchitecturalCheckpoint" />) plus every configured cache/TLB/branch
    ///     predictor/RAS table. The pipeline must already be drained (<see cref="Drain" />).
    /// </summary>
    /// <exception cref="InvalidOperationException">The pipeline is not drained.</exception>
    public void SaveMicroCheckpoint(string path, ISnapshotableMemory memory) {
        if (!_core.IsDrained)
            throw new InvalidOperationException(
                "OooeTrain.SaveMicroCheckpoint: the pipeline is not drained. Call Drain() first."
            );

        MicroarchitecturalCheckpoint.Save(path, ArchState, memory, (ulong)CurrentTick, _core.BuildCheckpointSections());
    }

    /// <summary>Stream-based overload of <see cref="SaveMicroCheckpoint(string, ISnapshotableMemory)" />.</summary>
    /// <exception cref="InvalidOperationException">The pipeline is not drained.</exception>
    public void SaveMicroCheckpoint(Stream stream, ISnapshotableMemory memory) {
        if (!_core.IsDrained)
            throw new InvalidOperationException(
                "OooeTrain.SaveMicroCheckpoint: the pipeline is not drained. Call Drain() first."
            );

        MicroarchitecturalCheckpoint.Save(
            stream, ArchState, memory, (ulong)CurrentTick, _core.BuildCheckpointSections()
        );
    }

    /// <summary>
    ///     Restores a microarchitectural checkpoint into this (freshly constructed, not yet
    ///     stepped) train: architectural state via <see cref="ArchitecturalCheckpoint.RestoreInto" />,
    ///     then every table section this train has a matching live component for. Sections the
    ///     checkpoint doesn't have, or that this train has no matching component for (a different
    ///     cache/predictor configuration), are left cold-started rather than throwing.
    /// </summary>
    public void RestoreMicroCheckpoint(string path, ISnapshotableMemory memory) {
        MicroarchitecturalCheckpoint chk = MicroarchitecturalCheckpoint.Load(path);
        chk.Architectural.RestoreInto(ArchState, memory);
        _core.RestoreCheckpointSections(chk);
    }

    /// <summary>Stream-based overload of <see cref="RestoreMicroCheckpoint(string, ISnapshotableMemory)" />.</summary>
    public void RestoreMicroCheckpoint(Stream stream, ISnapshotableMemory memory) {
        MicroarchitecturalCheckpoint chk = MicroarchitecturalCheckpoint.Load(stream);
        chk.Architectural.RestoreInto(ArchState, memory);
        _core.RestoreCheckpointSections(chk);
    }
}

internal sealed partial class OoOPipelineCore {
    internal bool IsDrained =>
        _rob.IsEmpty && _lq.IsEmpty && _sq.IsEmpty &&
        _decodeQueue.Count == 0 && _renameQueue.Count == 0 &&
        _execBuffer.Count == 0 && _cdbBuffer.Count == 0 && _inFlight.Count == 0 &&
        AllIqsEmpty();

    private bool AllIqsEmpty() {
        foreach (IssueQueue iq in _iqs)
            if (!iq.IsEmpty)
                return false;
        return true;
    }

    internal void Drain(long maxTicks) {
        long limit = maxTicks > 0 ? maxTicks : Math.Max(256L, (long)_rob.Capacity * 8);
        _fetchInhibited = true;
        try {
            long start = Escapement.CurrentTick;
            while (!IsDrained) {
                if (_halted)
                    throw new InvalidOperationException(
                        "OoOPipelineCore.Drain: the train halted (program exit) before the pipeline " +
                        "reached a drained boundary."
                    );
                if (Escapement.CurrentTick - start >= limit)
                    throw new InvalidOperationException(
                        $"OoOPipelineCore.Drain: did not reach a drained boundary within {limit} ticks."
                    );
                Escapement.Step();
            }
        }
        finally { _fetchInhibited = false; }
    }

    /// <summary>Builds the tagged section writers for <see cref="MicroarchitecturalCheckpoint.Save" />.</summary>
    internal IReadOnlyList<(string Tag, Action<BinaryWriter> Write)> BuildCheckpointSections() {
        var sections = new List<(string, Action<BinaryWriter>)>();
        if (ILayers.Cache is not null) sections.Add(("ICACHE", ILayers.Cache.WriteState));
        if (DLayers.Cache is not null) sections.Add(("DCACHE", DLayers.Cache.WriteState));
        if (ILayers.L2Cache is not null) sections.Add(("IL2CACHE", ILayers.L2Cache.WriteState));
        if (DLayers.L2Cache is not null) sections.Add(("DL2CACHE", DLayers.L2Cache.WriteState));
        if (ILayers.L3Cache is not null) sections.Add(("IL3CACHE", ILayers.L3Cache.WriteState));
        if (DLayers.L3Cache is not null) sections.Add(("DL3CACHE", DLayers.L3Cache.WriteState));
        if (ILayers.Tlb is not null) sections.Add(("ITLB", ILayers.Tlb.WriteState));
        if (DLayers.Tlb is not null) sections.Add(("DTLB", DLayers.Tlb.WriteState));
        // Tagged with the predictor's concrete type so a restore into a differently-configured
        // train (a different predictor type) skips this section instead of feeding it foreign
        // bytes — see the matching check in RestoreCheckpointSections.
        string predictorType = _predictor.GetType().FullName ?? "";
        sections.Add((
            "BPRED", w => {
                w.Write(predictorType);
                _predictor.WriteState(w);
            }
        ));
        sections.Add(("RAS", _ras.WriteState));
        sections.Add(("CRAS", _committedRas.WriteState));

        if (_storeSets is not null) sections.Add(("STORESETS", _storeSets.WriteState));
        if (_smbPredictor is not null) sections.Add(("SMB", _smbPredictor.WriteState));
        if (Rdip is not null) sections.Add(("RDIP", Rdip.WriteState));

        // ICriticalityPredictor/IValuePredictor can have more than one concrete implementation
        // (like IBranchPredictor), so tag with the concrete type and skip on mismatch — see BPRED.
        if (_criticalityPredictor is not null) {
            string criticalityType = _criticalityPredictor.GetType().FullName ?? "";
            sections.Add((
                "CRITICALITY", w => {
                    w.Write(criticalityType);
                    _criticalityPredictor.WriteState(w);
                }
            ));
        }

        if (_valuePredictor is not null) {
            string valuePredictorType = _valuePredictor.GetType().FullName ?? "";
            sections.Add((
                "VALUEPRED", w => {
                    w.Write(valuePredictorType);
                    _valuePredictor.WriteState(w);
                }
            ));
        }

        return sections;
    }

    /// <summary>Restores every section this train has a matching live component for.</summary>
    internal void RestoreCheckpointSections(MicroarchitecturalCheckpoint chk) {
        if (ILayers.Cache is not null) chk.TryRestoreSection("ICACHE", ILayers.Cache.ReadState);
        if (DLayers.Cache is not null) chk.TryRestoreSection("DCACHE", DLayers.Cache.ReadState);
        if (ILayers.L2Cache is not null) chk.TryRestoreSection("IL2CACHE", ILayers.L2Cache.ReadState);
        if (DLayers.L2Cache is not null) chk.TryRestoreSection("DL2CACHE", DLayers.L2Cache.ReadState);
        if (ILayers.L3Cache is not null) chk.TryRestoreSection("IL3CACHE", ILayers.L3Cache.ReadState);
        if (DLayers.L3Cache is not null) chk.TryRestoreSection("DL3CACHE", DLayers.L3Cache.ReadState);
        if (ILayers.Tlb is not null) chk.TryRestoreSection("ITLB", ILayers.Tlb.ReadState);
        if (DLayers.Tlb is not null) chk.TryRestoreSection("DTLB", DLayers.Tlb.ReadState);
        string predictorType = _predictor.GetType().FullName ?? "";
        chk.TryRestoreSection(
            "BPRED", r => {
                string savedType = r.ReadString();
                if (savedType == predictorType) _predictor.ReadState(r);
            }
        );
        chk.TryRestoreSection("RAS", _ras.ReadState);
        chk.TryRestoreSection("CRAS", _committedRas.ReadState);

        if (_storeSets is not null) chk.TryRestoreSection("STORESETS", _storeSets.ReadState);
        if (_smbPredictor is not null) chk.TryRestoreSection("SMB", _smbPredictor.ReadState);
        if (Rdip is not null) chk.TryRestoreSection("RDIP", Rdip.ReadState);

        if (_criticalityPredictor is not null) {
            string criticalityType = _criticalityPredictor.GetType().FullName ?? "";
            chk.TryRestoreSection(
                "CRITICALITY", r => {
                    string savedType = r.ReadString();
                    if (savedType == criticalityType) _criticalityPredictor.ReadState(r);
                }
            );
        }

        if (_valuePredictor is not null) {
            string valuePredictorType = _valuePredictor.GetType().FullName ?? "";
            chk.TryRestoreSection(
                "VALUEPRED", r => {
                    string savedType = r.ReadString();
                    if (savedType == valuePredictorType) _valuePredictor.ReadState(r);
                }
            );
        }
    }
}
