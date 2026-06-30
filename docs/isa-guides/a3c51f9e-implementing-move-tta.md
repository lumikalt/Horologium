# Implementing TTA/MOVE

Created: 2026-06-30
Tags: #tta #move #transport-triggered #corporaal

---

Transport Triggered Architecture (TTA) is a programming paradigm proposed by Henk
Corporaal in his 1995 work *Microprocessor Architectures*. In a TTA, the ISA exposes
no arithmetic instructions. Instead, every "instruction" is a **data transport** between
named ports on functional units (FUs). Computation happens as a side effect: when data
arrives at a FU's **trigger port**, the FU computes its result and latches it at its
output port, where a subsequent transport can read it.

The *MOVE* family of processors (also by Corporaal's group) is the canonical TTA
implementation. This guide covers a simplified 16-bit MOVE-inspired variant suitable
for demonstrating the paradigm in Horologium.

## Architecture summary

**Word size:** 16 bits. All values, addresses, and register contents are `ushort`.

**Instruction format:** Fixed 32-bit (4 bytes).

```
 31      24  23      16  15             0
┌──────────┬──────────┬─────────────────┐
│   dst    │   src    │       imm       │
└──────────┴──────────┴─────────────────┘
```

- `dst` (8 bits): destination port ID
- `src` (8 bits): source port ID; `0xFE` means "use `imm`"
- `imm` (16 bits): immediate value; used when `src = 0xFE`

**Registers:** r0–r7 (eight 16-bit general-purpose registers).

**Functional units:**

| FU | Ports | Role |
|---|---|---|
| ALU | `alu.op`, `alu.in1`, `alu.in2` (trigger), `alu.out` | 16 arithmetic/logic operations |
| Mem | `mem.load` (trigger), `mem.addr`, `mem.store` (trigger), `mem.out` | 16-bit loads and stores |
| Branch | `br.cond`, `br.target` (trigger) | Conditional PC redirect |

**Source IDs** (what provides the value):

| ID | Source |
|---|---|
| `0x00`–`0x07` | r0–r7 |
| `0x10` | `alu.out` (latched ALU result) |
| `0x20` | `mem.out` (latched load result) |
| `0xFE` | `imm` (bits [15:0] of the instruction) |
| `0xFF` | PC (current instruction address, low 16 bits) |

**Destination IDs** (where the value goes):

| ID | Destination | Trigger? |
|---|---|---|
| `0x00`–`0x07` | r0–r7 | No — register write |
| `0x10` | `alu.op` | No — latch ALU operation code |
| `0x11` | `alu.in1` | No — latch ALU operand 1 |
| `0x12` | `alu.in2` | **Yes** — `alu.out ← f(alu.op, alu.in1, value)` |
| `0x20` | `mem.load` | **Yes** — `mem.out ← mem[value]` (value = byte address) |
| `0x21` | `mem.addr` | No — latch store address |
| `0x22` | `mem.store` | **Yes** — `mem[mem.addr] ← value` (value = data to write) |
| `0x30` | `br.cond` | No — latch branch condition |
| `0x31` | `br.target` | **Yes** — if `br.cond ≠ 0`, `PC ← value` |
| `0xFF` | halt | — terminates simulation |

**ALU operation codes** (written to `alu.op` before triggering `alu.in2`):

| Code | Operation | Expression |
|---|---|---|
| `0x00` | ADD | `in1 + in2` |
| `0x01` | SUB | `in1 - in2` |
| `0x02` | AND | `in1 & in2` |
| `0x03` | OR | `in1 \| in2` |
| `0x04` | XOR | `in1 ^ in2` |
| `0x05` | NOT | `~in2` (ignores in1) |
| `0x06` | SHL | `in1 << (in2 & 0xF)` |
| `0x07` | SHR | `in1 >> (in2 & 0xF)` (logical) |
| `0x08` | SRA | `(signed)in1 >> (in2 & 0xF)` |
| `0x09` | EQ | `in1 == in2 ? 1 : 0` |
| `0x0A` | LT | `(signed)in1 < (signed)in2 ? 1 : 0` |
| `0x0B` | ULT | `(unsigned)in1 < (unsigned)in2 ? 1 : 0` |
| `0x0C` | NEG | `-(short)in2` (ignores in1) |
| `0x0D` | INC | `in2 + 1` |
| `0x0E` | DEC | `in2 - 1` |
| `0x0F` | COPY | `in2` (pass-through) |

Memory is byte-addressed; loads and stores operate on 2-byte (16-bit) words.
`mem.load` reads `memory.Read(value, 2)`. `mem.store` writes `memory.Write(mem.addr, value, 2)`.

## Example programs

**Load r1 into r2 via ALU pass-through:**
```
imm 0x0F → alu.op    // COPY
r1       → alu.in2   // trigger: alu.out = r1
alu.out  → r2        // r2 = r1
```

**Add r0 + r1 → r2:**
```
imm 0x00 → alu.op   // ADD
r0       → alu.in1
r1       → alu.in2  // trigger: alu.out = r0 + r1
alu.out  → r2
```

`alu.op` is persistent — you only need to set it when the operation changes.

**Load from address in r3 into r4:**
```
r3      → mem.load  // trigger: mem.out = mem[r3]
mem.out → r4
```

**Store r5 to address in r6:**
```
r6      → mem.addr
r5      → mem.store  // trigger: mem[r6] = r5
```

**Conditional branch (branch to 0x0100 if r0 ≠ 0):**
```
r0      → br.cond
imm 256 → br.target   // if br.cond ≠ 0, PC = 256
```

**Unconditional branch to label:**
```
imm 1    → br.cond    // condition is always nonzero
imm addr → br.target  // always taken
```

**Countdown loop (r0 = 5, loop until r0 = 0):**
```
PC=0:  imm 5    → r0           // r0 = 5
PC=4:  imm 0x0E → alu.op       // DEC (in2 - 1)
// loop:
PC=8:  r0       → alu.in2      // trigger: alu.out = r0 - 1
PC=12: alu.out  → r0           // r0 = r0 - 1
PC=16: r0       → br.cond      // br.cond = r0
PC=20: imm 8    → br.target    // if r0 ≠ 0, PC = 8 (loop)
PC=24: imm 0    → 0xFF         // halt
// ticks: 2 (init) + 5 × 4 (loop body) + 1 (halt) = 23 ticks
```

## Mapping to Horologium concepts

| MOVE concept | Horologium abstraction | Notes |
|---|---|---|
| MOVE instruction | `MoveInstruction : ITooth` | Carries dst, src, imm; no payload union needed (one instruction type) |
| Register file r0–r7 | `IRegisterFile` in `MoveArchState` | Exposed for debug/UI; pipeline doesn't use it for hazards |
| FU state (AluOp, AluIn1, AluOut, MemAddr, MemOut, BrCond) | Fields on `MoveArchState` | All mutations via `SideEffect`; memory writes direct to `IMemory` |
| Trigger port | `ExecuteResult` with optional `SideEffect` + branch fields | ALU trigger sets AluOut in SideEffect; branch trigger sets BranchTaken/BranchTarget |
| Memory load | Read in `Execute()`, write MemOut in `SideEffect` | Allowed: reads from `IMemory` are side-effect-free |
| Memory store | Write directly to `IMemory` in `Execute()` | Allowed: memory writes bypass SideEffect; only IArchState writes are deferred |
| No named destination per instruction for hazards | `DestinationRegister = -1`, `SourceRegisters = []` | SingleCycleTrain only; hazard unit cannot track FU port state |
| No trap mechanism | `MoveTrapController` is a no-op | Re-raise at the faulting PC |

**Which pipeline to use:** `SingleCycleTrain` only. The MOVE ISA has no register hazards
visible to the pipeline (all operand flow is through FU state, not named registers), so
`FiveStageTrain` and `OooeTrain` cannot detect dependencies. With `SingleCycleTrain` one
instruction executes and commits per tick — matching the programmer's explicit sequencing
of transports.

## Project layout

```
Move/
  MoveMechanism.cs
  MoveArchState.cs
  MoveTrapController.cs
  Decode/
    MoveInstruction.cs   (ITooth)
    MoveDecoder.cs       (IDecoder)
  Execute/
    MoveExecutor.cs      (IExecutor)
  Registers/
    MoveRegisterFile.cs  (IRegisterFile + NullSystemRegisters)
  Move.csproj
```

```xml
<!-- Move.csproj -->
<ItemGroup>
  <ProjectReference Include="../Mechanism/Mechanism.csproj" />
  <ProjectReference Include="../Pipeline/Pipeline.csproj" />
</ItemGroup>
```

## Step 1 — MoveInstruction (ITooth)

The MOVE ISA has exactly one instruction form, so no payload union is needed.
All relevant fields live directly on `MoveInstruction`.

```csharp
using Mechanism;

namespace Move.Decode;

public sealed class MoveInstruction : ITooth {
    public ulong Pc { get; }
    public uint RawEncoding { get; }
    public int SizeBytes => 4;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public object? Payload => null;

    public byte Dst { get; }
    public byte Src { get; }
    public ushort Imm { get; }
    public ToothClass Class { get; }

    public MoveInstruction(ulong pc, byte dst, byte src, ushort imm) {
        Pc  = pc;
        Dst = dst; Src = src; Imm = imm;
        RawEncoding = (uint)(dst << 24 | src << 16 | imm);
        Class = dst switch {
            0x20 => ToothClass.Load,
            0x22 => ToothClass.Store,
            0x31 => ToothClass.ConditionalBranch,
            0xFF => ToothClass.Branch,   // halt counts as terminal branch
            _    => ToothClass.IntegerAlu,
        };
    }
}
```

## Step 2 — MoveDecoder (IDecoder)

```csharp
using Mechanism;
using Move.Decode;

namespace Move.Decode;

public sealed class MoveDecoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 4;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        byte dst = (byte)(firstWord >> 24);
        byte src = (byte)((firstWord >> 16) & 0xFF);
        ushort imm = (ushort)(firstWord & 0xFFFF);
        bool isBranch = dst is 0x31 or 0xFF;
        // Provide a static target only for unconditional halts and imm-target branches
        (ulong, bool)? target = (isBranch && dst == 0x31 && src == 0xFE)
            ? ((ulong)imm, false)
            : null;
        return new FetchHint {
            InstructionSize = 4,
            IsBranch  = isBranch,
            BranchTarget = target,
        };
    }

    public ITooth Decode(ulong pc, IMemory memory) =>
        Decode(pc, (uint)memory.Read(pc, 4));

    public ITooth Decode(ulong pc, uint firstWord) {
        byte   dst = (byte)(firstWord >> 24);
        byte   src = (byte)((firstWord >> 16) & 0xFF);
        ushort imm = (ushort)(firstWord & 0xFFFF);
        return new MoveInstruction(pc, dst, src, imm);
    }
}
```

`GetFetchHint` supplies a static branch target only when `dst = br.target` and
`src = imm` — in that case the target is constant. All other branch-like destinations
(halt, conditional branches whose condition depends on runtime state) leave
`BranchTarget = null`.

## Step 3 — MoveExecutor (IExecutor)

```csharp
using Mechanism;
using Move.Decode;

namespace Move.Execute;

public sealed class MoveExecutor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var mv = (MoveInstruction)instruction;
        var m  = (MoveArchState)state;

        ushort value = ReadSource(mv.Src, mv.Imm, m);

        return mv.Dst switch {
            >= 0x00 and <= 0x07 => WriteReg(mv.Dst, value),
            0x10 => SetAluOp((byte)value),
            0x11 => SetAluIn1(value),
            0x12 => TriggerAlu(m, value),
            0x20 => TriggerLoad(memory, value),
            0x21 => LatchAddr(value),
            0x22 => TriggerStore(memory, m, value),
            0x30 => SetBrCond(value),
            0x31 => TriggerBranch(m, value),
            0xFF => new ExecuteResult { IsHalt = true },
            _    => new ExecuteResult(),
        };
    }

    private static ushort ReadSource(byte src, ushort imm, MoveArchState m) => src switch {
        >= 0x00 and <= 0x07 => m.R[src],
        0x10 => m.AluOut,
        0x20 => m.MemOut,
        0xFE => imm,
        0xFF => (ushort)m.Pc,
        _    => 0,
    };

    private static ExecuteResult WriteReg(byte idx, ushort value) =>
        new() { SideEffect = s => ((MoveArchState)s).R[idx] = value };

    private static ExecuteResult SetAluOp(byte op) =>
        new() { SideEffect = s => ((MoveArchState)s).AluOp = op };

    private static ExecuteResult SetAluIn1(ushort value) =>
        new() { SideEffect = s => ((MoveArchState)s).AluIn1 = value };

    private static ExecuteResult TriggerAlu(MoveArchState m, ushort in2) {
        ushort result = ComputeAlu(m.AluOp, m.AluIn1, in2);
        return new() { SideEffect = s => ((MoveArchState)s).AluOut = result };
    }

    private static ExecuteResult TriggerLoad(IMemory memory, ushort addr) {
        ushort data = (ushort)memory.Read(addr, 2);
        return new() { SideEffect = s => ((MoveArchState)s).MemOut = data };
    }

    private static ExecuteResult LatchAddr(ushort addr) =>
        new() { SideEffect = s => ((MoveArchState)s).MemAddr = addr };

    private static ExecuteResult TriggerStore(IMemory memory, MoveArchState m, ushort data) {
        memory.Write(m.MemAddr, data, 2);
        return new();
    }

    private static ExecuteResult SetBrCond(ushort cond) =>
        new() { SideEffect = s => ((MoveArchState)s).BrCond = cond };

    private static ExecuteResult TriggerBranch(MoveArchState m, ushort target) =>
        new() { BranchTaken = m.BrCond != 0, BranchTarget = target };

    private static ushort ComputeAlu(byte op, ushort a, ushort b) => op switch {
        0x00 => (ushort)(a + b),
        0x01 => (ushort)(a - b),
        0x02 => (ushort)(a & b),
        0x03 => (ushort)(a | b),
        0x04 => (ushort)(a ^ b),
        0x05 => (ushort)~b,
        0x06 => (ushort)(a << (b & 0xF)),
        0x07 => (ushort)(a >> (b & 0xF)),
        0x08 => (ushort)((short)a >> (b & 0xF)),
        0x09 => a == b ? (ushort)1 : (ushort)0,
        0x0A => (short)a < (short)b ? (ushort)1 : (ushort)0,
        0x0B => a < b ? (ushort)1 : (ushort)0,
        0x0C => (ushort)(-(short)b),
        0x0D => (ushort)(b + 1),
        0x0E => (ushort)(b - 1),
        0x0F => b,
        _    => 0,
    };
}
```

**Reading source before writing destination.** `ReadSource` runs before the destination
switch, capturing all FU state from the pre-instruction `MoveArchState`. This means a
move from `alu.out` to `alu.out` (0x10 → 0x10) is a no-op, not a self-read-after-write —
matching hardware behaviour where reads and writes to the same port in one cycle use the
old value.

**Memory reads in Execute.** `TriggerLoad` reads from `IMemory` inside `Execute()` (not
inside `SideEffect`). This is correct for `SingleCycleTrain`: the engine won't commit the
SideEffect until after Execute returns, but the load value is determined at Execute time
and captured in a local before being written to `MemOut` via the closure.

**Memory writes in Execute.** `TriggerStore` writes directly to `IMemory` in Execute —
no SideEffect needed. The CLAUDE.md invariant is that *IArchState* mutations must be
deferred; memory writes may be issued directly in the executor, which is the right choice
for a `SingleCycleTrain` where every instruction commits sequentially.

## Step 4 — MoveArchState (IArchState)

```csharp
using Mechanism;
using Move.Registers;

namespace Move;

public sealed class MoveArchState : IArchState {
    public readonly ushort[] R = new ushort[8];  // r0-r7

    // ALU functional unit state
    public byte   AluOp  = 0;     // operation code (default ADD)
    public ushort AluIn1 = 0;
    public ushort AluOut = 0;

    // Memory functional unit state
    public ushort MemAddr = 0;    // latched store address
    public ushort MemOut  = 0;    // latched load result

    // Branch functional unit state
    public ushort BrCond = 0;

    private readonly MoveRegisterFile _regs;

    public MoveArchState() {
        _regs = new MoveRegisterFile(this);
    }

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters => _regs;
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    public IArchState Snapshot() {
        var s = new MoveArchState {
            Pc = Pc, PrivilegeLevel = PrivilegeLevel,
            AluOp = AluOp, AluIn1 = AluIn1, AluOut = AluOut,
            MemAddr = MemAddr, MemOut = MemOut,
            BrCond = BrCond,
        };
        Array.Copy(R, s.R, 8);
        return s;
    }

    public void Reset() {
        Pc = 0;
        Array.Clear(R);
        AluOp = AluIn1 = AluOut = 0;
        MemAddr = MemOut = BrCond = 0;
    }
}
```

```csharp
// Move/Registers/MoveRegisterFile.cs
using Mechanism;

namespace Move.Registers;

public sealed class MoveRegisterFile(MoveArchState state) : IRegisterFile {
    public int Count => 8;
    public int Width => 16;
    public ulong Read(int index) => index is >= 0 and < 8 ? state.R[index] : 0;
    public void Write(int index, ulong value) {
        if (index is >= 0 and < 8) state.R[index] = (ushort)value;
    }
}

public sealed class NullSystemRegisters : ISystemRegisters {
    public static readonly NullSystemRegisters Instance = new();
    public ulong Read(uint address, PrivilegeLevel _) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel _) { }
    public bool Exists(uint address) => false;
}
```

## Step 5 — MoveTrapController (ITrapController)

The MOVE model defines no trap mechanism. Re-raise at the faulting PC.

```csharp
using Mechanism;

namespace Move;

public sealed class MoveTrapController : ITrapController {
    public ulong RaiseTrap(TrapInfo trap, IArchState state) => trap.Pc;
    public ulong ReturnFromTrap(PrivilegeLevel returningFrom, IArchState state) => state.Pc;
}
```

## Step 6 — MoveMechanism (IMechanism)

```csharp
using Mechanism;
using Move.Decode;
using Move.Execute;

namespace Move;

public sealed class MoveMechanism : IMechanism {
    public string Name => "TTA/MOVE";
    public IDecoder Decoder { get; } = new MoveDecoder();
    public IExecutor Executor { get; } = new MoveExecutor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new MoveTrapController();

    public IArchState CreateArchState() => new MoveArchState();
}
```

## Running a program

MOVE programs are flat arrays of 32-bit little-endian instructions. Load them into
`FlatMemory` and run with `SingleCycleTrain`.

```csharp
using Mechanism;
using Pipeline;
using RiscV32.Memory;
using Move;

uint[] program = [ /* encoded instructions */ ];
var memory = new FlatMemory(65536);
for (int i = 0; i < program.Length; i++)
    memory.Write((ulong)(i * 4), program[i], 4);

var train = new SingleCycleTrain(new MoveMechanism(), memory, entryPoint: 0);
train.Run(maxTicks: 1_000_000);
```

### Encoding helper

In tests, build instructions with a small helper:

```csharp
// src = 0xFE → use imm; src = register id; src = 0x10 = alu.out; etc.
static uint Mov(byte dst, byte src, ushort imm = 0) =>
    (uint)(dst << 24 | src << 16 | imm);

// Common destination constants
const byte R0  = 0x00, R1 = 0x01, /* ... */ R7 = 0x07;
const byte ALU_OP    = 0x10;
const byte ALU_IN1   = 0x11;
const byte ALU_IN2   = 0x12;   // trigger
const byte MEM_LOAD  = 0x20;   // trigger
const byte MEM_ADDR  = 0x21;
const byte MEM_STORE = 0x22;   // trigger
const byte BR_COND   = 0x30;
const byte BR_TARGET = 0x31;   // trigger
const byte HALT      = 0xFF;

// Common source constants
const byte SRC_IMM    = 0xFE;
const byte SRC_PC     = 0xFF;
const byte SRC_ALUOUT = 0x10;
const byte SRC_MEMOUT = 0x20;
```

## Things to watch out for

**ALU state is persistent.** `AluOp`, `AluIn1`, and `AluOut` persist across instructions.
You do not need to set `alu.op` on every compute — only when the operation changes.
A common bug is assuming AluOp resets between computations; it does not.

**`br.cond` is persistent.** Once set, `br.cond` stays until changed. An unconditional
jump is written as two transports: `imm 1 → br.cond` then `imm addr → br.target`. A
program that sets `br.cond` once at the start can reuse it on every loop iteration.

**`alu.in2` reads the value you transport, not a latched field.** Unlike `alu.in1`
which is a latch, `alu.in2` is the trigger port — the value you move *into* it is
immediately used as the second operand. The compute fires as soon as the value
arrives. `alu.in1` must already be set before moving to `alu.in2`.

**`mem.store` transports data, not an address.** The value moved to `mem.store` is the
data to write; the address used is whatever was last latched in `mem.addr`. Forgetting
to set `mem.addr` before storing writes to byte address 0 (the start of the program
image), silently corrupting code.

**`mem.load` returns on the *next* instruction.** `TriggerLoad` reads from memory in
`Execute()` and writes `MemOut` via `SideEffect`. In `SingleCycleTrain` the SideEffect
runs at the end of the same tick, so `mem.out` is available on the *very next* instruction.
You do not need to wait an extra cycle — the sequencing is:
tick N executes `r1 → mem.load` (MemOut updated), tick N+1 can read `mem.out → r2`.

**All addresses are byte addresses into the 16-bit address space.** Both `mem.load`
and `mem.addr` take byte addresses. A 16-bit word at word index `n` lives at byte
address `n * 2`. `memory.Read(addr, 2)` and `memory.Write(addr, data, 2)` are
the correct calls.

**`FetchHint.BranchTarget` is only supplied for static targets.** `GetFetchHint`
provides a static target when `dst = 0x31` (br.target) and `src = 0xFE` (immediate).
For register-sourced branch targets, `BranchTarget = null`; the real target
arrives in `ExecuteResult.BranchTarget` at execute time, which is fine for
`SingleCycleTrain`.

**`dst = 0xFF` (halt) returns `IsHalt = true`.** `SingleCycleTrain` stops iteration
immediately on seeing this; the instruction is not retired. The `src` and `imm` fields
of a halt instruction are ignored. Use `imm 0 → 0xFF` (i.e., `Mov(HALT, SRC_IMM, 0)`)
by convention.

**No hazard detection is possible.** `DestinationRegister = -1` and `SourceRegisters = []`
mean no pipeline stage can detect a dependency on FU state. Using `FiveStageTrain` or
`OooeTrain` will produce incorrect results for any program that reads a FU output in
the cycle after a trigger. Stick to `SingleCycleTrain`.
