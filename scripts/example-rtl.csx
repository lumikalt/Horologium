// Machine spec with every plug-in surface driven by verilated Chisel RTL (native/RtlFu):
// the divider replaces the C# div/rem model (its cycle count becomes the FU latency),
// the gshare steers speculative fetch, and the D-cache runs the RTL SRRIP policy and
// RPT stride prefetcher. Build the shared libraries first:
//
//   native/RtlFu/build.sh native/RtlFu/generated/DivUnit.sv  DivUnit  /tmp/rtl_div.so
//   native/RtlFu/build.sh native/RtlFu/generated/GshareBp.sv GshareBp /tmp/rtl_gshare.so rtl_bp_shim.cpp
//   native/RtlFu/build.sh native/RtlFu/generated/SrripRp.sv  SrripRp  /tmp/rtl_srrip.so  rtl_rp_shim.cpp
//   native/RtlFu/build.sh native/RtlFu/generated/StridePf.sv StridePf /tmp/rtl_stride.so rtl_pf_shim.cpp
//
// Run: dotnet run --project src/Apps/Runner -- prog.elf --script scripts/example-rtl.csx

var rtlDiv = "/tmp/rtl_div.so";
var rtlGshare = "/tmp/rtl_gshare.so";
var rtlSrrip = "/tmp/rtl_srrip.so";
var rtlStride = "/tmp/rtl_stride.so";

new MachineSpec(
    new OutOfOrderSpec(
        IssueWidth: 2,
        RobCapacity: 32,
        BranchPredictorFactory: () => new RtlFfiBranchPredictor(rtlGshare)
    ),
    () => {
        var mech = new Rv32Mechanism();
        mech.Executor = new RtlBackedExecutor(
            mech.Executor, new RtlFfiFunctionalUnit(rtlDiv), RvRtlDiv.Select
        );
        return mech;
    },
    // Split I/D so the D-path prefetcher reaches the pipeline (the unified path
    // hands the train a plain accessor without a prefetcher hook).
    CacheHierarchySpec.SplitId(
        new CachePathSpec([new CacheLevelSpec(8192, 4, 32, 10)]),
        new CachePathSpec([
            new CacheLevelSpec(
                8192, 4, 32, 10,
                // TryCreate returns null on geometry mismatch → level falls back to
                // ReplacementPolicy; the SRRIP model is elaborated for 64 sets × 4 ways.
                PolicyFactory: (sets, ways) => RtlFfiReplacementPolicy.TryCreate(rtlSrrip, sets, ways),
                PrefetcherFactory: () => new RtlFfiPrefetcher(rtlStride)
            )
        ])
    )
)
