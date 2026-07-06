// Example .csx machine spec
// Returns a MachineSpec; Runner --script evaluates this and runs the workload.

new MachineSpec(
    new FiveStageSpec(ForwardingEnabled: true),
    () => new Rv32Mechanism(),
    CacheHierarchySpec.Unified(
        new CachePathSpec(
            [new CacheLevelSpec(4096, 4, 64, 10)],
            new TlbSpec(64)
        )
    )
)
