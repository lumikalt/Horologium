// F# machine spec wiring the example RTL units (MyAlu.scala, MyBp.scala) into a
// 2-wide out-of-order train. Build the shared libraries first:
//
//   examples/rtl-machine/build.sh
//
// Run: dotnet run --project src/Apps/Runner -- prog.elf --script examples/rtl-machine/machine.fsx
//
// The instruction→opcode mapping for the custom ALU lives HERE, in the script — the
// selector pattern-matches decoded RvInstruction payloads and reads operand values
// through state.IntegerRegisters at call time (so it sees pipeline forwarding and the
// OoO train's speculative register values, exactly like the built-in selectors).
// Nothing in the C# codebase knows about MyAlu.

open RiscV32.Decode

let aluLib = "/tmp/rtl_example_alu.so"
let bpLib = "/tmp/rtl_example_bp.so"

// MyAlu opcodes: 0=ADD 1=SUB 2=AND 3=OR 4=XOR (see MyAlu.scala).
let aluSelector =
    System.Func<ITooth, IArchState, System.Nullable<RtlRequest>>(fun tooth state ->
        let regs = state.IntegerRegisters
        let req (op: uint32) (rs1: int) (rs2: int) =
            System.Nullable(RtlRequest(op, uint32 (regs.Read rs1), uint32 (regs.Read rs2)))
        match tooth.Payload with
        | :? RvAdd as i -> req 0u i.Rs1 i.Rs2
        | :? RvSub as i -> req 1u i.Rs1 i.Rs2
        | :? RvAnd as i -> req 2u i.Rs1 i.Rs2
        | :? RvOr as i -> req 3u i.Rs1 i.Rs2
        | :? RvXor as i -> req 4u i.Rs1 i.Rs2
        | _ -> System.Nullable<RtlRequest>())

// Each machine build loads fresh verilated models (they are not thread-safe).
let mechanismFactory =
    System.Func<IMechanism>(fun () ->
        let mech = Rv32Mechanism()
        mech.Executor <- RtlBackedExecutor(mech.Executor, RtlFfiFunctionalUnit(aluLib), aluSelector)
        mech :> IMechanism)

let bpFactory =
    System.Func<IBranchPredictor>(fun () -> RtlFfiBranchPredictor(bpLib) :> IBranchPredictor)

MachineSpec(
    OutOfOrderSpec(IssueWidth = 2, RobCapacity = 32, BranchPredictorFactory = bpFactory),
    mechanismFactory
)
