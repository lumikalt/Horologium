# Implementing SUBLEQ

Created: 2026-06-26
Tags: #oisc #subleq

---

SUBLEQ ("SUBtract and branch if Less than or EQual to zero") is a One
Instruction Set Computer (OISC). There is no opcode field — every word triple
in memory is interpreted as the same single instruction. It is the simplest
possible target to validate that the engine accepts a new `IMechanism`.

## Semantics

```
SUBLEQ a, b, c      ; three consecutive signed 32-bit words
  mem[b] -= mem[a]
  if mem[b] <= 0: PC = c
  else:           PC += 12   ; advance past this instruction (3 × 4 bytes)
```

Common conventions:

- `a == -1` or `b == -1`: I/O port (read/write a byte to stdout/stdin)
- `c < 0` (e.g. `-1`): halt
- Self-branch (`c == PC`): also used as halt

Instruction size is always 12 bytes (three 32-bit signed words).

## Mapping to Horologium Concepts

| SUBLEQ concept     | Horologium abstraction                       | Notes                       |
|--------------------|----------------------------------------------|-----------------------------|
| 3-word instruction | `ITooth` + `SizeBytes = 12`                  | No opcode needed in payload |
| Addresses a, b, c  | `SubleqOp(int A, int B, int C)` in `Payload` | Signed integers             |
| Memory subtract    | `IExecutor.Execute` → direct `memory.Write`  | Not via SideEffect          |
| Conditional branch | `ExecuteResult.WithBranch(taken, target)`    | Both outcomes computed      |
| No register file   | Stub `IRegisterFile` with `Count = 0`        | Return 0 on reads           |
| No traps defined   | Minimal `ITrapController`                    | Re-raises at same PC        |

Because there is no register file, `DestinationRegister` is always `-1` and
`SourceRegisters` is always empty. Set `Class = ToothClass.ConditionalBranch`
so the fetch stage knows every instruction can redirect the PC.

Use `SingleCycleTrain` — it is the right fit for any OISC. There are no
pipeline hazards to model and no benefit from deeper staging.

## Project layout

```
Subleq/
  SubleqMechanism.cs
  SubleqArchState.cs
  SubleqTrapController.cs
  Decode/
    SubleqInstruction.cs   (ITooth)
    SubleqDecoder.cs       (IDecoder)
  Execute/
    SubleqExecutor.cs      (IExecutor)
  Subleq.csproj
```

Add a project reference to `Mechanism` and `Pipeline` in `Subleq.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="../Mechanism/Mechanism.csproj" />
  <ProjectReference Include="../Pipeline/Pipeline.csproj" />
</ItemGroup>
```

## Step 1 — SubleqInstruction (ITooth)

```csharp
using Mechanism;

namespace Subleq.Decode;

public sealed record SubleqOp(int A, int B, int C);

public sealed class SubleqInstruction(ulong pc, SubleqOp op) : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = (uint)op.A;   // first word as proxy
    public int SizeBytes => 12;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public ToothClass Class => ToothClass.ConditionalBranch;
    public object? Payload => op;
}
```

## Step 2 — SubleqDecoder (IDecoder)

```csharp
using Mechanism;
using Subleq.Decode;

namespace SubLeq.Decode;

public sealed class SubleqDecoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 12;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) => new() {
        InstructionSize = 12,
        IsBranch = true,
    };

    public ITooth Decode(ulong pc, IMemory memory) {
        var a = (int)memory.Read(pc, 4);
        var b = (int)memory.Read(pc + 4, 4);
        var c = (int)memory.Read(pc + 8, 4);
        return new SubleqInstruction(pc, new SubleqOp(a, b, c));
    }

    // Required by the interface; SUBLEQ can't be decoded from a single uint.
    public ITooth Decode(ulong pc, uint raw) => throw new NotSupportedException(
        "SUBLEQ requires three words; use Decode(pc, memory) instead.");
}
```

`GetFetchHint` is called by the fetch stage with only the first 4 bytes
already in hand. Setting `IsBranch = true` tells the fetch stage to consult
the branch predictor on every instruction.

## Step 3 — SubleqExecutor (IExecutor)

```csharp
using Mechanism;
using Subleq.Decode;

namespace Subleq.Execute;

public sealed class SubleqExecutor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var op = (SubleqOp)instruction.Payload!;
        ulong pc = instruction.Pc;

        // I/O convention: address -1 is a terminal port.
        long valA = op.A == -1 ? 0 : (long)(int)memory.Read((ulong)op.A, 4);
        long valB = op.B == -1 ? 0 : (long)(int)memory.Read((ulong)op.B, 4);
        long result = valB - valA;

        if (op.B != -1)
            memory.Write((ulong)op.B, (ulong)(uint)(int)result, 4);
        else
            Console.Write((char)(result & 0xFF)); // stdout I/O

        if (op.C < 0) return new ExecuteResult { IsHalt = true };

        bool taken = result <= 0;
        ulong target = taken ? (ulong)op.C : pc + 12;
        return ExecuteResult.WithBranch(taken, target);
    }
}
```

Memory writes happen directly on the `memory` parameter — they do not go
through `SideEffect`. `SideEffect` is reserved for `IArchState` mutations
(register / CSR writes).

## Step 4 — SubleqArchState (IArchState)

SUBLEQ has no architectural register file. Provide a no-op stub so the
engine's register-file plumbing compiles cleanly.

```csharp
using Mechanism;

namespace Subleq;

public sealed class SubleqArchState : IArchState {
    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters { get; } = new EmptyRegisterFile();
    public ISystemRegisters SystemRegisters { get; } = new NullSystemRegisters();

    public IArchState Snapshot() => new SubleqArchState { Pc = Pc };
    public void Reset() => Pc = 0;
}

internal sealed class EmptyRegisterFile : IRegisterFile {
    public int Count => 0;
    public int Width => 32;
    public ulong Read(int index) => 0;
    public void Write(int index, ulong value) { }
}

internal sealed class NullSystemRegisters : ISystemRegisters {
    public ulong Read(uint address, PrivilegeLevel _) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel _) { }
    public bool Exists(uint address) => false;
}
```

## Step 5 — SubleqTrapController (ITrapController)

SUBLEQ defines no trap mechanism. Re-raising at the same PC lets the engine
record the event without corrupting state.

```csharp
using Mechanism;

namespace Subleq;

public sealed class SubleqTrapController : ITrapController {
    public ulong RaiseTrap(TrapInfo trap, IArchState state) => trap.Pc;
    public ulong ReturnFromTrap(PrivilegeLevel returningFrom, IArchState state) => state.Pc;
}
```

## Step 6 — SubleqMechanism (IMechanism)

```csharp
using Mechanism;
using SubLeq.Decode;
using Subleq.Execute;

namespace Subleq;

public sealed class SubleqMechanism : IMechanism {
    public string Name => "SUBLEQ";
    public IDecoder Decoder { get; } = new SubleqDecoder();
    public IExecutor Executor { get; } = new SubleqExecutor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new SubleqTrapController();

    public IArchState CreateArchState() => new SubleqArchState();
}
```

## Running a program

```csharp
using Mechanism;
using Orrery.Memory;   // or whatever flat-memory type you wire in
using Pipeline;
using Subleq;

// Use any IMemory implementation — a plain byte-array wrapper works.
var memory = new FlatMemory(65536);
memory.Load(0, programBytes);

var mechanism = new SubleqMechanism();
var train = new SingleCycleTrain(mechanism, memory, entryPoint: 0);
var result = train.Run(maxTicks: 1_000_000);
```

## Things to watch out for

**Signed vs unsigned.** SUBLEQ addresses and cell values are signed 32-bit
integers. `IMemory.Read` returns `ulong`; cast to `int` before arithmetic to
preserve sign extension. Cast back to `uint` then to `ulong` before writing.

**Self-halting programs.** Many SUBLEQ programs halt by branching to themselves
(`c == PC`). This produces an infinite loop of `ExecuteResult.WithBranch(true, pc)`
with no `IsHalt = true`. Either detect the self-branch in the executor and
return `IsHalt = true`, or rely on `maxTicks` in `train.Run()` to stop the run.

**`Decode(pc, uint)` is unreachable.** The `FetchHint` path in the pipeline only
supplies the first fetched word. For SUBLEQ this is meaningless — the decoder
must read all three words from memory. `SingleCycleTrain` always calls
`Decode(pc, memory)`, so the throwing implementation is safe for this train.
If you later try `FiveStageTrain` you will need to revisit this.

**No register hazards, no OoOE.** Because `DestinationRegister = -1` and
`SourceRegisters = []`, the pipeline's hazard units see no data dependencies.
`SingleCycleTrain` is the only pipeline that makes sense — each instruction
reads and writes memory that the next instruction may also read, and only the
single-cycle model guarantees every instruction fully commits before the next
is fetched.
