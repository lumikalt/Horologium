# Implementing J1 Forth

Created: 2026-06-26
Tags: #j1 #forth #stack-machine

---

The J1 CPU (James Bowman, 2010) is a small 16-bit Forth stack machine whose
entire implementation fits on a single FPGA primitive. Its four instruction
types, its data and return stacks, and its ALU encoding make it a good
Horologium target: it is more interesting than SUBLEQ (explicit stacks, four
instruction classes, a rich ALU) while remaining simpler than RISC-V. The
companion file [[7fa83f25-j1-forth-alu-encoding-reference]] covers the full
ALU bit layout and a table of common Forth words.

## Architecture summary

**Word size:** 16 bits. Every instruction is exactly one 16-bit word (2 bytes).

**Stacks:**
- *Data stack* — `T` (top of stack, TOS) and `N` (next on stack, NOS) are the
  two directly accessible values. Below them is a hardware stack of typical
  depth 32. `DSP` is the stack-depth counter.
- *Return stack* — `R` (top of return stack). Depth typically also 32. `RSP`
  is the return-stack-depth counter.

**PC:** On real hardware the PC is a *word* address. Horologium tracks PC in
*bytes*, so the Horologium PC = J1 word address × 2. All branch targets in the
instruction encoding are word addresses and must be converted before being
placed in `ExecuteResult.BranchTarget`.

**Memory:** Word-addressed on the J1 (address `n` = the n-th 16-bit word).
In Horologium's byte-addressed `IMemory`, word address `n` lives at byte
address `n * 2`.

**Instruction classification by top bits:**

| Bits [15:13] | Type | Notes |
|---|---|---|
| 1xx | Literal | Bit 15 = 1; any value of 14:13 |
| 000 | Jump | Unconditional |
| 001 | CondJump | Jump if T == 0; pops data stack |
| 010 | Call | Push PC+1 to return stack; jump |
| 011 | ALU | Rich encoding; see companion reference |

**Literal format:**
```
[15]    1          (identifies Literal)
[14:0]  value      (zero-extended to 16 bits, pushed onto data stack)
```

**Jump / CondJump / Call format:**
```
[15:13]  type code (as above)
[12:0]   word target
```

**ALU format (bits [15:13] = 011):**
See [[7fa83f25-j1-forth-alu-encoding-reference]] for the full field layout.
Summary:
```
[12]    R→PC      return: pop return stack, set PC = R
[11:8]  TOut      T' selector (16 options; what new T becomes)
[7]     T→N       copy old T to N before stack adjustment
[6]     T→R       copy old T to R
[5]     N→[T]     write N to mem[T] (word store)
[4]     reserved  always 0
[3:2]   DDelta    data-stack adjustment (2-bit signed two's complement)
[1:0]   RDelta    return-stack adjustment (2-bit signed two's complement)
```

## Mapping to Horologium concepts

| J1 concept | Horologium abstraction | Notes |
|---|---|---|
| T (TOS) | — | Managed by `J1ArchState.DPush/DPop`; not a named register |
| N (NOS) | — | Second slot in data stack array |
| Data stack depth | `J1ArchState.Dsp` | Counter only; actual values in array |
| Return stack | `J1ArchState.RPush/RPop` | `R` is `_rstack[_rsp]` |
| 16-bit word | `SizeBytes = 2` | `FlatMemory` is byte-addressed; 1 word = 2 bytes |
| PC (word addr) | PC = word addr × 2 | Decoder keeps PC as byte address throughout |
| Word-addressed mem | Multiply all J1 addresses by 2 | `mem[T]` → `memory.Read((ulong)t * 2, 2)` |
| Branch targets (word) | Byte target = word target × 2 | Applied in `ExecuteResult.BranchTarget` |
| ALU side effects | `ExecuteResult.SideEffect` | All stack mutations deferred to commit |
| No named registers | `DestinationRegister = -1` | Pipeline hazard unit sees no register deps |

**Which pipeline to use:** `SingleCycleTrain`. The J1 is a pure stack machine —
every instruction implicitly depends on the result of the previous instruction
through T and N. Staging a J1 in a five-stage pipeline would stall on almost
every instruction pair. `SingleCycleTrain` is the right fit and produces the
cleanest result. If you want to explore `FiveStageTrain`, map T to register 0
and N to register 1, expose `DestinationRegister` and `SourceRegisters`
accordingly, and ensure that the SideEffect writes T via `IRegisterFile.Write`
so forwarding paths work.

## Project layout

```
J1/
  J1Mechanism.cs
  J1ArchState.cs
  J1TrapController.cs
  Decode/
    J1Instruction.cs   (ITooth + payload types)
    J1Decoder.cs       (IDecoder)
  Execute/
    J1Executor.cs      (IExecutor)
  J1.csproj
```

```xml
<!-- J1.csproj -->
<ItemGroup>
  <ProjectReference Include="../Mechanism/Mechanism.csproj" />
  <ProjectReference Include="../Pipeline/Pipeline.csproj" />
</ItemGroup>
```

## Step 1 — Payload types and J1Instruction (ITooth)

```csharp
using Mechanism;

namespace J1.Decode;

// Payload union — one record per instruction type
public abstract record J1Op;

public record Literal(ushort Value) : J1Op;

public record Jump(int WordTarget) : J1Op;

public record CondJump(int WordTarget) : J1Op;

public record Call(int WordTarget) : J1Op;

public record Alu(
    int  TOut,        // bits [11:8]: T' selector
    bool ReturnFromR, // bit [12]:   R→PC
    bool TtoN,        // bit [7]:    copy T to N
    bool TtoR,        // bit [6]:    copy T to R
    bool NtoMem,      // bit [5]:    N→[T] store
    int  DDelta,      // bits [3:2]: data-stack adjustment (signed)
    int  RDelta       // bits [1:0]: return-stack adjustment (signed)
) : J1Op;

public sealed class J1Instruction(ulong pc, J1Op op, ToothClass cls) : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = op is Literal lit ? lit.Value : 0u;
    public int SizeBytes => 2;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public ToothClass Class { get; } = cls;
    public object? Payload => op;
}
```

`ToothClass` assignment:

| Instruction | `ToothClass` |
|---|---|
| Literal | `IntegerAlu` |
| Jump | `Branch` |
| CondJump | `ConditionalBranch` |
| Call | `Branch` |
| ALU with R→PC | `Branch` |
| ALU without R→PC | `IntegerAlu` |
| ALU with N→[T] | `Store` |
| ALU with TOut=0xC | `Load` |

For simplicity, classify all ALU instructions as `IntegerAlu` unless they are
stores (`NtoMem`) or loads (`TOut == 0xC`). Return-path ALU ops (`ReturnFromR`)
can be classified as `Branch` so the fetch stage knows they redirect the PC.

## Step 2 — J1Decoder (IDecoder)

```csharp
using Mechanism;
using J1.Decode;

namespace J1.Decode;

public sealed class J1Decoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 2;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        var raw = (ushort)firstWord;
        bool isLiteral  = (raw & 0x8000) != 0;
        int  type       = (raw >> 13) & 7;
        bool isBranch   = !isLiteral && type is 0 or 1 or 2;
        bool isAluReturn = !isLiteral && type == 3 && (raw & 0x1000) != 0;
        bool isCall     = !isLiteral && type == 2;

        return new FetchHint {
            InstructionSize = 2,
            IsBranch  = isBranch || isAluReturn,
            IsCall    = isCall,
            IsReturn  = isAluReturn,
            BranchTarget = (isBranch && !isLiteral && type != 1)
                ? ((ulong)(raw & 0x1FFF) * 2, true)  // Jump / Call target
                : default,
        };
    }

    public ITooth Decode(ulong pc, IMemory memory) =>
        Decode(pc, (uint)memory.Read(pc, 2));

    public ITooth Decode(ulong pc, uint firstWord) {
        var raw = (ushort)firstWord;

        if ((raw & 0x8000) != 0) {
            // Literal: push bits [14:0] zero-extended
            return new J1Instruction(pc, new Literal((ushort)(raw & 0x7FFF)), ToothClass.IntegerAlu);
        }

        int type   = (raw >> 13) & 7;
        int target = raw & 0x1FFF;

        return type switch {
            0 => new J1Instruction(pc, new Jump(target),    ToothClass.Branch),
            1 => new J1Instruction(pc, new CondJump(target), ToothClass.ConditionalBranch),
            2 => new J1Instruction(pc, new Call(target),    ToothClass.Branch),
            3 => DecodeAlu(pc, raw),
            _ => throw new IllegalInstructionException(pc, raw, $"Unknown J1 type {type}"),
        };
    }

    private static J1Instruction DecodeAlu(ulong pc, ushort raw) {
        // Two's-complement decode for 2-bit signed deltas
        static int Delta(int bits) => bits switch { 0 => 0, 1 => 1, 2 => -2, 3 => -1, _ => 0 };

        var op = new Alu(
            TOut:        (raw >> 8) & 0xF,
            ReturnFromR: (raw & 0x1000) != 0,
            TtoN:        (raw & 0x0080) != 0,
            TtoR:        (raw & 0x0040) != 0,
            NtoMem:      (raw & 0x0020) != 0,
            DDelta:      Delta((raw >> 2) & 3),
            RDelta:      Delta(raw & 3));

        ToothClass cls = op.NtoMem        ? ToothClass.Store
                       : op.TOut == 0xC   ? ToothClass.Load
                       : op.ReturnFromR   ? ToothClass.Branch
                                          : ToothClass.IntegerAlu;
        return new J1Instruction(pc, op, cls);
    }
}
```

`GetFetchHint` receives only the first 2 bytes (the whole instruction for a
fixed-width ISA). The `BranchTarget` static hint is populated for Jump and Call
(whose targets are statically encoded); it is omitted for CondJump (target
depends on T at runtime) and for ALU-return (target = R, also runtime).

## Step 3 — J1Executor (IExecutor)

The full executor is documented with line-by-line commentary in
[[7fa83f25-j1-forth-alu-encoding-reference]]. It is reproduced here for
self-containment.

```csharp
using Mechanism;
using J1.Decode;

namespace J1.Execute;

public sealed class J1Executor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var j1 = (J1ArchState)state;
        ulong pc = instruction.Pc;

        return instruction.Payload switch {
            Literal lit  => ExecLiteral(lit),
            Jump jmp     => ExecuteResult.WithBranch(true, (ulong)jmp.WordTarget * 2),
            CondJump cj  => ExecCondJump(cj, pc, j1),
            Call call    => ExecCall(call, pc, j1),
            Alu alu      => ExecAlu(alu, pc, j1, memory),
            _ => throw new InvalidOperationException($"Unknown J1 op: {instruction.Payload}"),
        };
    }

    private static ExecuteResult ExecLiteral(Literal lit) =>
        new() { SideEffect = s => ((J1ArchState)s).DPush(lit.Value) };

    private static ExecuteResult ExecCondJump(CondJump cj, ulong pc, J1ArchState j1) {
        bool taken  = j1.T == 0;
        ulong target = taken ? (ulong)cj.WordTarget * 2 : pc + 2;
        return new ExecuteResult {
            BranchTaken  = taken,
            BranchTarget = target,
            SideEffect   = s => ((J1ArchState)s).DPop(),
        };
    }

    private static ExecuteResult ExecCall(Call call, ulong pc, J1ArchState j1) =>
        new ExecuteResult {
            BranchTaken  = true,
            BranchTarget = (ulong)call.WordTarget * 2,
            SideEffect   = s => ((J1ArchState)s).RPush((ushort)((pc / 2) + 1)),
        };

    private static ExecuteResult ExecAlu(Alu op, ulong pc, J1ArchState j1, IMemory memory) {
        ushort t = j1.T, n = j1.N, r = j1.R;

        ushort newT = op.TOut switch {
            0x0 => t,
            0x1 => n,
            0x2 => (ushort)(t + n),
            0x3 => (ushort)(t & n),
            0x4 => (ushort)(t | n),
            0x5 => (ushort)(t ^ n),
            0x6 => (ushort)~t,
            0x7 => n == t ? (ushort)0xFFFF : (ushort)0,
            0x8 => (short)n < (short)t ? (ushort)0xFFFF : (ushort)0,
            0x9 => (ushort)((ushort)n >> (t & 0xF)),
            0xA => (ushort)(t - 1),
            0xB => r,
            0xC => (ushort)memory.Read((ulong)t * 2, 2),
            0xD => (ushort)((ushort)n << (t & 0xF)),
            0xE => (ushort)j1.Dsp,
            0xF => (ushort)n < (ushort)t ? (ushort)0xFFFF : (ushort)0,
            _ => t,
        };

        bool  isReturn   = op.ReturnFromR;
        ulong branchTarget = isReturn ? (ulong)r * 2 : pc + 2;

        ushort captT = t, captN = n, captNewT = newT;
        return new ExecuteResult {
            BranchTaken  = isReturn,
            BranchTarget = branchTarget,
            SideEffect   = s => {
                var j = (J1ArchState)s;
                if (op.NtoMem) memory.Write((ulong)captT * 2, captN, 2);
                if (op.TtoR)   j.R = captT;
                if (isReturn)  j.RPop();
                else           ApplyRDelta(j, op.RDelta);
                ApplyDDelta(j, op.DDelta, captNewT);
                if (op.TtoN)   j.N = captT;
            },
        };
    }

    private static void ApplyDDelta(J1ArchState j, int delta, ushort newT) {
        switch (delta) {
            case  0: j.T = newT; break;
            case  1: j.DPush(newT); break;
            case -1: j.DPop(); j.T = newT; break;
            case -2: j.DPop(); j.DPop(); j.T = newT; break;
        }
    }

    private static void ApplyRDelta(J1ArchState j, int delta) {
        if      (delta ==  1) j.RPush(0);
        else if (delta == -1) j.RPop();
    }
}
```

**Stack sequencing.** The SideEffect applies operations in this fixed order:
N→[T] store, T→R copy, return-stack adjustment, data-stack adjustment (which
also sets new T), T→N copy. Capturing `captT` and `captN` before the lambda
ensures every step sees the pre-op stack values.

**Memory store is immediate.** The `N→[T]` write goes directly to the `memory`
parameter, not through `SideEffect`. This is permitted — the `SideEffect` rule
only requires that `IArchState` mutations (register/CSR writes) be deferred.
Memory writes may be issued directly in the executor, which is correct for a
`SingleCycleTrain` where every instruction commits before the next is fetched.

## Step 4 — J1ArchState (IArchState)

```csharp
using Mechanism;
using J1.Registers;

namespace J1;

public sealed class J1ArchState : IArchState {
    private const int StackDepth = 32;

    private readonly ushort[] _dstack = new ushort[StackDepth];
    private readonly ushort[] _rstack = new ushort[StackDepth];
    private int _dsp = 0; // data stack pointer (index of current T)
    private int _rsp = 0; // return stack pointer (index of current R)

    private readonly J1RegisterFile _regs;

    public J1ArchState() {
        _regs = new J1RegisterFile(this);
    }

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters => _regs;
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    // Data stack accessors
    public ushort T {
        get => _dstack[_dsp & (StackDepth - 1)];
        set => _dstack[_dsp & (StackDepth - 1)] = value;
    }

    public ushort N {
        get => _dstack[(_dsp - 1) & (StackDepth - 1)];
        set => _dstack[(_dsp - 1) & (StackDepth - 1)] = value;
    }

    public int Dsp => _dsp;

    public void DPush(ushort value) {
        _dsp = (_dsp + 1) & (StackDepth - 1);
        _dstack[_dsp] = value;
    }

    public ushort DPop() {
        ushort v = T;
        _dsp = (_dsp - 1) & (StackDepth - 1);
        return v;
    }

    // Return stack accessors
    public ushort R {
        get => _rstack[_rsp & (StackDepth - 1)];
        set => _rstack[_rsp & (StackDepth - 1)] = value;
    }

    public void RPush(ushort value) {
        _rsp = (_rsp + 1) & (StackDepth - 1);
        _rstack[_rsp] = value;
    }

    public ushort RPop() {
        ushort v = R;
        _rsp = (_rsp - 1) & (StackDepth - 1);
        return v;
    }

    public IArchState Snapshot() {
        var s = new J1ArchState { Pc = Pc, PrivilegeLevel = PrivilegeLevel };
        Array.Copy(_dstack, s._dstack, StackDepth);
        Array.Copy(_rstack, s._rstack, StackDepth);
        s._dsp = _dsp;
        s._rsp = _rsp;
        return s;
    }

    public void Reset() {
        Pc = 0;
        _dsp = 0; _rsp = 0;
        Array.Clear(_dstack); Array.Clear(_rstack);
    }
}
```

```csharp
// J1/Registers/J1RegisterFile.cs
using Mechanism;

namespace J1.Registers;

// Exposes T (index 0) and N (index 1) for pipeline visibility.
// The actual values live on the J1ArchState stacks.
public sealed class J1RegisterFile(J1ArchState state) : IRegisterFile {
    public int Count => 2;
    public int Width => 16;
    public ulong Read(int index) => index == 0 ? state.T : state.N;
    public void Write(int index, ulong value) {
        if (index == 0) state.T = (ushort)value;
        else            state.N = (ushort)value;
    }
}

public sealed class NullSystemRegisters : ISystemRegisters {
    public static readonly NullSystemRegisters Instance = new();
    public ulong Read(uint address, PrivilegeLevel _) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel _) { }
    public bool Exists(uint address) => false;
}
```

The stacks use power-of-two wrapping (`& (StackDepth - 1)`) so stack overflow
and underflow wrap silently — matching the behaviour of the hardware. Detect
overflow explicitly in tests if needed.

## Step 5 — J1TrapController (ITrapController)

The J1 defines no trap mechanism. Re-raise at the same PC.

```csharp
using Mechanism;

namespace J1;

public sealed class J1TrapController : ITrapController {
    public ulong RaiseTrap(TrapInfo trap, IArchState state) => trap.Pc;
    public ulong ReturnFromTrap(PrivilegeLevel returningFrom, IArchState state) => state.Pc;
}
```

## Step 6 — J1Mechanism (IMechanism)

```csharp
using Mechanism;
using J1.Decode;
using J1.Execute;

namespace J1;

public sealed class J1Mechanism : IMechanism {
    public string Name => "J1 Forth";
    public IDecoder Decoder { get; } = new J1Decoder();
    public IExecutor Executor { get; } = new J1Executor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new J1TrapController();

    public IArchState CreateArchState() => new J1ArchState();
}
```

## Running a program

J1 binaries are a flat sequence of 16-bit words. Load them into `FlatMemory`
as little-endian pairs (low byte first). The J1 conventionally starts execution
at word address 0 (byte address 0).

```csharp
using Mechanism;
using Pipeline;
using RiscV.Memory;  // FlatMemory lives here; reuse or write your own
using J1;

byte[] binary = File.ReadAllBytes("program.bin");  // raw 16-bit LE words
var memory = new FlatMemory(65536);                // 64 KiB = 32 K words
memory.Load(0, binary);

var train = new SingleCycleTrain(new J1Mechanism(), memory, entryPoint: 0);
var result = train.Run(maxTicks: 1_000_000);
```

Start with `SingleCycleTrain`. Once the `Mechanism` is passing tests, switch to
`FiveStageTrain` only if you have exposed `DestinationRegister` and
`SourceRegisters` for hazard detection (see mapping table notes above).

## Things to watch out for

**PC is a byte address, branch targets are word addresses.** The J1 instruction
encoding stores all branch targets as word addresses (13-bit field). Convert
with `(ulong)wordTarget * 2` before placing in `ExecuteResult.BranchTarget`.
The pipeline manages PC as a byte address, so after a Jump to word address 5
the PC becomes 10, not 5. In the decoder, `pc / 2` converts back to a word
address (e.g. for the Call return address pushed to the return stack: the
hardware pushes `PC_word + 1`, so `(ushort)((pc / 2) + 1)`).

**Memory is word-addressed.** All J1 memory addresses (T in a load or store)
are word addresses. In the executor, always multiply by 2: `memory.Read((ulong)t * 2, 2)`.
`FlatMemory` is byte-addressed and little-endian; a 16-bit word stored at byte
address `n` is read correctly by `memory.Read(n, 2)`.

**Literal bit 15 takes precedence.** The literal encoding uses bit 15 = 1
regardless of bits 14:13. Test for `(raw & 0x8000) != 0` first in the decoder,
before looking at `(raw >> 13) & 7`. Otherwise a literal value with bits 14:13
= 11 (e.g. `0xE000`) would be misclassified as an ALU instruction.

**Stack depth is DepthFirst (DSP reports pre-push depth).** The `DSP` value
readable via `TOut = 0xE` (the `DEPTH` word) reflects the current number of
items on the stack before this instruction executes. In the `J1ArchState`
implementation, `_dsp` is the index of T (0 when the stack holds one item).
Match the hardware semantics your test programs expect.

**ALU stack sequencing order.** The five ALU side effects must execute in order:
(1) N→[T] memory store, (2) T→R, (3) return-stack adjustment, (4) data-stack
adjustment, (5) T→N. Getting the order wrong produces wrong results for combined
operations like `>R` (T→R=1, DDelta=-1, RDelta=+1) or `R>` (TOut=R, DDelta=+1,
RDelta=-1). The `captT` and `captN` captures at the start of `ExecAlu` are
essential — they hold the pre-op T and N values that each subsequent step needs.

**`!` leaves N on top (not ANS Forth semantics).** The standard J1 `!` encoding
stores N to `mem[T]` and pops T (DDelta=-1), leaving N as the new T. This is
`( n addr -- n )`, not the ANS Forth `( n addr -- )`. If you need standard
drop-both behaviour, follow `!` with a `DROP`. This is a hardware convention
of the original J1, not a bug in the executor.

**CondJump pops T regardless.** The `CondJump` instruction always pops T off
the data stack whether or not the branch is taken. The SideEffect must call
`DPop()` unconditionally — do not guard it on the branch condition.

**Call stores a word address.** The Call instruction pushes the return address
onto the return stack as a *word* address: `(ushort)((pc / 2) + 1)`. Since
`pc` is a byte address, `pc / 2` converts it to the J1 word address, and `+ 1`
advances past the call instruction. When `EXIT` (ALU R→PC) fires, it reads this
word address from R and converts it back: `branchTarget = (ulong)r * 2`.

**Stack arrays wrap modulo depth.** The ring-buffer indexing (`& (StackDepth - 1)`)
means stack overflow wraps silently, clobbering old values. This matches
real J1 hardware behaviour but makes debugging harder. For testing, add a check
in `DPush`/`RPush` that asserts `_dsp < StackDepth`.
