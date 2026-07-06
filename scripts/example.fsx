// Example .fsx machine spec
// Returns a MachineSpec; Runner --script evaluates this and runs the workload.
// All Spec namespaces are pre-opened; no #r or open needed.

let pipeline = FiveStageSpec(ForwardingEnabled = true)

let l1 = CacheLevelSpec(4096, 4, 64, 10)
let tlb = TlbSpec(64)
let cache = CacheHierarchySpec.Unified(CachePathSpec([| l1 |], tlb))

MachineSpec(pipeline, (fun () -> Rv32Mechanism()), cache)
