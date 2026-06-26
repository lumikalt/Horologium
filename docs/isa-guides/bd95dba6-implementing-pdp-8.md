# Implementing PDP-8

Created: 2026-06-26
Tags: #pdp8 #accumulator

---

The PDP-8 (1965) is a 12-bit accumulator machine with a remarkably minimal
instruction set: three bits of opcode, yielding eight instruction classes. It
is more interesting to plug into Horologium than SUBLEQ because it has a real
accumulator register, page-relative addressing, indirect memory access, and a
compound "operate" instruction that encodes micro-operations via bit fields.

## Architecture summary

**Word size:** 12 bits, stored in 2 bytes (the top 4 bits of the 16-bit storage
are always 0).

**Registers:**
- `AC` — 12-bit accumulator; the only general-purpose register
- `L` — 1-bit link; carry/borrow from AC, used in shifts

**Memory:** 4096 × 12-bit words (8 KiB in 2-byte storage).

**Instruction format:**
```
[11:9]  opcode   (3 bits, 0–7)
[8]     I        indirect flag
[7]     Z        0 = page 0, 1 = current page
[6:0]   offset   (7 bits within page)
```

Opcodes 0–5 are Memory Reference Instructions (MRI). Opcode 6 is IOT (I/O).
Opcode 7 is OPR (operate micro-instructions — no memory access).

**MRI effective address:**
```
directEa = Z == 0 ? offset                 // page 0
                  : (PC & 0xF80) | offset  // current page (PC bits [11:7])
if I == 1:
    if directEa in [8..15]:                // auto-increment range (010–017 octal)
        mem[directEa]++
    ea = mem[directEa]                     // indirect
else:
    ea = directEa
```

**MRI instruction table:**

| Opcode | Mnemonic | Operation |
|---|---|---|
| 0 | AND | AC &= mem[ea] |
| 1 | TAD | AC += mem[ea]; L ^= carry |
| 2 | ISZ | mem[ea]++; if mem[ea]==0: skip |
| 3 | DCA | mem[ea] = AC; AC = 0 |
| 4 | JMS | mem[ea] = PC+1; PC = ea+1 |
| 5 | JMP | PC = ea |

**OPR Group 1** (bit 11 is clear in the micro-op field, i.e. bit 8 of the word = 0):

| Bit | Micro-op | Operation |
|---|---|---|
| 7 | CLA | AC = 0 |
| 6 | CLL | L = 0 |
| 5 | CMA | AC = ~AC (12-bit) |
| 4 | CML | L ^= 1 |
| 3 | RAR | {L,AC} >>= 1 (13-bit rotate right) |
| 3+2 | RTR | {L,AC} >>= 2 |
| 2 | RAL | {L,AC} <<= 1 |
| 2+3 | RTL | {L,AC} <<= 2 |
| 1 | IAC | AC++ |
| 0 | BSW | swap AC bytes (6-bit halves) |

Micro-ops execute in order: CLA/CLL, then CMA/CML, then rotate, then IAC.

**OPR Group 2** (bit 8 of the raw word = 1; bit 0 = 0):
Skip conditions test AC or L. Multiple conditions are OR'd or AND'd depending
on bit 3. HLT (bit 1) halts. OSR (bit 2) ORs the front-panel switch register
into AC.

## Mapping to Horologium concepts

| PDP-8 concept | Horologium abstraction | Notes |
|---|---|---|
| AC (12-bit) | register 0 | Masked to 12 bits on write |
| L (1-bit link) | register 1 | 0 or 1 only |
| 12-bit word in 2 bytes | `SizeBytes = 2` | High 4 bits of raw encoding unused |
| MRI with direct EA | payload carries `DirectEa` + `Indirect` flag | Executor resolves indirect |
| OPR micro-ops | bit-field record in payload | Executor applies in sequence |
| TAD carry → L | `SideEffect` writing register 1 | L is modified out-of-band from AC |
| ISZ skip | `ExecuteResult.WithBranch(taken, pc + 4)` | Skip = advance by 2 instructions |
| JMS return address | `SideEffect` writing memory and setting AC | No register write; use SideEffect |

`FiveStageTrain` is a reasonable first choice — the PDP-8 is a scalar in-order
machine, and register hazards on AC are real and common (nearly every
instruction modifies it).

## Project layout

```
Pdp8/
  Pdp8Mechanism.cs
  Pdp8ArchState.cs
  Pdp8TrapController.cs
  Decode/
    Pdp8Instruction.cs   (ITooth + payload types)
    Pdp8Decoder.cs       (IDecoder)
  Execute/
    Pdp8Executor.cs      (IExecutor)
  Pdp8.csproj
```

## Step 1 — Payload types and Pdp8Instruction (ITooth)

```csharp
using Mechanism;

namespace Pdp8.Decode;

// Payload union
public abstract record Pdp8Op;

public enum MriCode { And, Tad, Isz, Dca, Jms, Jmp }

public record MriOp(MriCode Code, int DirectEa, bool Indirect) : Pdp8Op;

public record Opr1Op(
    bool Cla, bool Cll, bool Cma, bool Cml,
    bool Rar, bool Ral, bool Rtr, bool Rtl,
    bool Iac, bool Bsw) : Pdp8Op;

// OrMode = true: skip if ANY condition is true (default)
// OrMode = false: skip if ALL inverted conditions are true (AND sense, when bit 3 = 0)
public record Opr2Op(
    bool Cla, bool Sma, bool Sza, bool Snl,
    bool Hlt, bool Osr, bool OrMode) : Pdp8Op;

public record IotOp(int Device, int Function) : Pdp8Op;

public sealed class Pdp8Instruction(ulong pc, int dest, IReadOnlyList<int> srcs,
    ToothClass cls, Pdp8Op op) : ITooth
{
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = (uint)((op as MriOp)?.DirectEa ?? 0);
    public int SizeBytes => 2;
    public int DestinationRegister => dest;
    public IReadOnlyList<int> SourceRegisters => srcs;
    public ToothClass Class => cls;
    public object? Payload => op;
}
```

Assign `DestinationRegister` and `SourceRegisters` based on what each opcode
actually modifies and reads:

| Instruction | `DestinationRegister` | `SourceRegisters` |
|---|---|---|
| AND | 0 (AC) | [0] |
| TAD | 0 (AC) | [0] (L written via SideEffect) |
| ISZ | -1 | [] |
| DCA | 0 (AC = 0) | [0] |
| JMS | -1 | [] (return addr written via SideEffect) |
| JMP | -1 | [] |
| OPR1 (reads AC) | 0 | [0, 1] if rotation; [0] otherwise |
| OPR1 (CLA only) | 0 | [] |
| OPR2 | -1 | [0, 1] (tests AC and/or L) |
| OPR2 + CLA | 0 | [0, 1] |
| HLT | -1 | [] |
| IOT | -1 | [] |

## Step 2 — Pdp8Decoder (IDecoder)

```csharp
using Mechanism;
using Pdp8.Decode;

namespace Pdp8.Decode;

public sealed class Pdp8Decoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 2;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        uint raw = firstWord & 0xFFF;
        int opcode = (int)(raw >> 9);
        bool isBranch = opcode is 4 or 5 or 2; // JMS, JMP, ISZ
        bool isReturn = false;                 // PDP-8 has no RETURN opcode
        return new FetchHint {
            InstructionSize = 2,
            IsBranch = isBranch,
            IsCall = opcode == 4,
        };
    }

    public ITooth Decode(ulong pc, IMemory memory) =>
        Decode(pc, (uint)memory.Read(pc, 2));

    public ITooth Decode(ulong pc, uint raw) {
        raw &= 0xFFF; // keep 12 bits
        int opcode = (int)(raw >> 9) & 7;
        bool indirect = (raw & 0x100) != 0;
        bool currentPage = (raw & 0x80) != 0;
        int offset = (int)(raw & 0x7F);

        int directEa = currentPage
            ? ((int)(pc >> 1) & ~0x7F) | offset // PC is byte address; word page = PC/2 & ~127
            : offset;                           // page 0

        return opcode switch {
            0 => MriTooth(pc, MriCode.And, directEa, indirect,
                     ToothClass.IntegerAlu, dest: 0, srcs: [0]),
            1 => MriTooth(pc, MriCode.Tad, directEa, indirect,
                     ToothClass.IntegerAlu, dest: 0, srcs: [0]),
            2 => MriTooth(pc, MriCode.Isz, directEa, indirect,
                     ToothClass.ConditionalBranch, dest: -1, srcs: []),
            3 => MriTooth(pc, MriCode.Dca, directEa, indirect,
                     ToothClass.Store, dest: 0, srcs: [0]),
            4 => MriTooth(pc, MriCode.Jms, directEa, indirect,
                     ToothClass.Branch, dest: -1, srcs: []),
            5 => MriTooth(pc, MriCode.Jmp, directEa, indirect,
                     ToothClass.Branch, dest: -1, srcs: []),
            6 => new Pdp8Instruction(pc, -1, [],
                     ToothClass.System, new IotOp((int)((raw >> 3) & 0x3F), (int)(raw & 7))),
            7 => DecodeOpr(pc, raw),
            _ => throw new IllegalInstructionException(pc, raw, $"impossible opcode {opcode}"),
        };
    }

    private static Pdp8Instruction MriTooth(ulong pc, MriCode code, int ea, bool ind,
        ToothClass cls, int dest, int[] srcs) =>
        new(pc, dest, srcs, cls, new MriOp(code, ea, ind));

    private static Pdp8Instruction DecodeOpr(ulong pc, uint raw) {
        bool group2 = (raw & 0x100) != 0;

        if (!group2) {
            // Group 1
            bool rotRight = (raw & 0x08) != 0;
            bool rotLeft  = (raw & 0x04) != 0;
            bool rotate2  = rotRight && rotLeft; // RTR or RTL when both set
            var op = new Opr1Op(
                Cla: (raw & 0x80) != 0,
                Cll: (raw & 0x40) != 0,
                Cma: (raw & 0x20) != 0,
                Cml: (raw & 0x10) != 0,
                Rar: rotRight && !rotate2,
                Ral: rotLeft  && !rotate2,
                Rtr: rotate2  && rotRight,
                Rtl: rotate2  && rotLeft,
                Iac: (raw & 0x02) != 0,
                Bsw: (raw & 0x01) != 0);

            bool readsAc  = !op.Cla || op.Cma || op.Rar || op.Ral || op.Rtr || op.Rtl || op.Bsw;
            bool readsL   = op.Cml || op.Rar || op.Ral || op.Rtr || op.Rtl;
            List<int> srcs = [];
            if (readsAc) srcs.Add(0);
            if (readsL)  srcs.Add(1);
            return new Pdp8Instruction(pc, 0, srcs, ToothClass.IntegerAlu, op);
        } else {
            // Group 2 (bit 0 == 0; Group 3 EAE when bit 0 == 1, not covered here)
            bool orMode = (raw & 0x08) != 0; // bit 3: 1 = OR skip conditions
            var op = new Opr2Op(
                Cla: (raw & 0x80) != 0,
                Sma: (raw & 0x40) != 0,
                Sza: (raw & 0x20) != 0,
                Snl: (raw & 0x10) != 0,
                Hlt: (raw & 0x02) != 0,
                Osr: (raw & 0x04) != 0,
                OrMode: orMode);

            if (op.Hlt)
                return new Pdp8Instruction(pc, -1, [], ToothClass.Halt, op);

            bool testsAc = op.Sma || op.Sza;
            bool testsL  = op.Snl;
            List<int> srcs = [];
            if (testsAc) srcs.Add(0);
            if (testsL)  srcs.Add(1);
            int dest = op.Cla ? 0 : -1;
            return new Pdp8Instruction(pc, dest, srcs, ToothClass.ConditionalBranch, op);
        }
    }
}
```

The current-page EA calculation shifts by 1 because PC is a byte address while
the PDP-8 page grid is word-indexed. `((int)(pc >> 1) & ~0x7F) | offset`
gives the correct word address.

## Step 3 — Pdp8Executor (IExecutor)

```csharp
using Mechanism;
using Pdp8.Decode;

namespace Pdp8.Execute;

public sealed class Pdp8Executor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var pdp8 = (Pdp8ArchState)state;
        ulong pc = instruction.Pc;

        return instruction.Payload switch {
            MriOp mri   => ExecMri(mri, pc, pdp8, memory),
            Opr1Op opr1 => ExecOpr1(opr1, pdp8),
            Opr2Op opr2 => ExecOpr2(opr2, pc, pdp8),
            IotOp iot   => ExecuteResult.Clean, // I/O stub
            _ => throw new InvalidOperationException($"Unknown PDP-8 op: {instruction.Payload}"),
        };
    }

    private static ulong ResolveEa(MriOp mri, IMemory memory) {
        ulong ea = (ulong)mri.DirectEa;
        if (!mri.Indirect) return ea;
        // Auto-increment: indirect through addresses 8–15 (octal 010–017)
        if (ea is >= 8 and <= 15) {
            ulong incVal = (memory.Read(ea * 2, 2) + 1) & 0xFFF;
            memory.Write(ea * 2, incVal, 2);
            return incVal;
        }
        return memory.Read(ea * 2, 2) & 0xFFF;
    }

    private static ExecuteResult ExecMri(MriOp mri, ulong pc, Pdp8ArchState pdp8, IMemory memory) {
        ulong ea = ResolveEa(mri, memory);
        ulong eaBytes = ea * 2; // word address → byte address
        ulong ac = pdp8.Ac;

        return mri.Code switch {
            MriCode.And => ExecuteResult.WithResult(ac & memory.Read(eaBytes, 2) & 0xFFF),

            MriCode.Tad => ExecTad(ac, memory.Read(eaBytes, 2), pdp8),

            MriCode.Isz => ExecIsz(ea, eaBytes, pc, memory),

            MriCode.Dca => ExecDca(ac, eaBytes, memory),

            MriCode.Jms => new ExecuteResult {
                BranchTaken = true,
                BranchTarget = eaBytes + 2, // PC = ea+1 (in bytes: ea*2+2)
                SideEffect = s => {
                    // Write return address (word after JMS) to mem[ea]
                    memory.Write(eaBytes, (pc + 2) / 2, 2);   // return addr as word number
                },
            },

            MriCode.Jmp => ExecuteResult.WithBranch(true, eaBytes),

            _ => throw new InvalidOperationException($"Unknown MRI code: {mri.Code}"),
        };
    }

    private static ExecuteResult ExecTad(ulong ac, ulong memVal, Pdp8ArchState pdp8) {
        ulong sum = ac + (memVal & 0xFFF);
        ulong newAc = sum & 0xFFF;
        ulong newL  = pdp8.L ^ ((sum >> 12) & 1); // L XOR carry
        return new ExecuteResult {
            RegisterResult = (newAc, true),
            SideEffect = s => ((Pdp8ArchState)s).L = newL,
        };
    }

    private static ExecuteResult ExecIsz(ulong ea, ulong eaBytes, ulong pc, IMemory memory) {
        ulong newVal = (memory.Read(eaBytes, 2) + 1) & 0xFFF;
        memory.Write(eaBytes, newVal, 2);
        bool skip = newVal == 0;
        return ExecuteResult.WithBranch(skip, skip ? pc + 4 : pc + 2);
    }

    private static ExecuteResult ExecDca(ulong ac, ulong eaBytes, IMemory memory) {
        memory.Write(eaBytes, ac, 2);
        return ExecuteResult.WithResult(0); // AC = 0
    }

    private static ExecuteResult ExecOpr1(Opr1Op op, Pdp8ArchState pdp8) {
        ulong ac = op.Cla ? 0 : pdp8.Ac;
        ulong l  = op.Cll ? 0 : pdp8.L;
        if (op.Cma) ac ^= 0xFFF;
        if (op.Cml) l  ^= 1;

        // Rotations act on the 13-bit {L, AC} register
        ulong la = (l << 12) | ac;
        if      (op.Rar) la = (la >> 1) | ((la & 1) << 12);
        else if (op.Rtr) la = (la >> 2) | ((la & 3) << 11);
        else if (op.Ral) la = ((la << 1) | (la >> 12)) & 0x1FFF;
        else if (op.Rtl) la = ((la << 2) | (la >> 11)) & 0x1FFF;
        else if (op.Bsw) la = ((la & 0x3F) << 6) | ((la >> 6) & 0x3F); // swap 6-bit halves of AC

        ac = la & 0xFFF;
        l  = (la >> 12) & 1;
        if (op.Iac) { ac = (ac + 1) & 0xFFF; }

        ulong finalL = l;
        return new ExecuteResult {
            RegisterResult = (ac, true),
            SideEffect = s => ((Pdp8ArchState)s).L = finalL,
        };
    }

    private static ExecuteResult ExecOpr2(Opr2Op op, ulong pc, Pdp8ArchState pdp8) {
        ulong ac = pdp8.Ac;
        bool skip;

        if (op.OrMode) {
            // Skip if any tested condition is true
            skip = (op.Sma && (ac & 0x800) != 0) // AC negative (bit 11 set)
                || (op.Sza && ac == 0)
                || (op.Snl && pdp8.L != 0);
        } else {
            // AND sense: skip if none of the tested conditions are true
            skip = !(op.Sma && (ac & 0x800) != 0)
                && !(op.Sza && ac == 0)
                && !(op.Snl && pdp8.L != 0);
        }

        ulong newAc = op.Cla ? 0 : ac;
        bool hasResult = op.Cla;
        ulong target = skip ? pc + 4 : pc + 2;

        return new ExecuteResult {
            RegisterResult = (newAc, hasResult),
            BranchTaken = skip,
            BranchTarget = target,
        };
    }
}
```

**Key design point:** JMS stores the return address via `SideEffect` rather than
a direct write. The `SideEffect` lambda captures the memory write — this is
technically allowed in the executor (only *register file / CSR writes to
`IArchState`* must go through `SideEffect`; memory writes may be direct), but
for JMS the memory write is semantically the commit action, so deferring it to
the `SideEffect` slot makes the intent explicit and would be safe in a
`FiveStageTrain` as well. The branch target (`eaBytes + 2`) is set immediately
so the pipeline can redirect fetch.

## Step 4 — Pdp8ArchState (IArchState)

```csharp
using Mechanism;
using Pdp8.Registers;

namespace Pdp8;

public sealed class Pdp8ArchState : IArchState {
    private readonly Pdp8RegisterFile _regs = new();

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters => _regs;
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    // Convenience accessors
    public ulong Ac {
        get => _regs.Read(0);
        set => _regs.Write(0, value & 0xFFF);
    }
    public ulong L {
        get => _regs.Read(1);
        set => _regs.Write(1, value & 1);
    }

    public IArchState Snapshot() {
        var s = new Pdp8ArchState { Pc = Pc, PrivilegeLevel = PrivilegeLevel };
        s.Ac = Ac; s.L = L;
        return s;
    }
    public void Reset() { Pc = 0; Ac = 0; L = 0; }
}
```

```csharp
// Pdp8/Registers/Pdp8RegisterFile.cs
using Mechanism;

namespace Pdp8.Registers;

public sealed class Pdp8RegisterFile : IRegisterFile {
    private readonly ulong[] _r = new ulong[2]; // [0] = AC, [1] = L
    public int Count => 2;
    public int Width => 12;
    public ulong Read(int index) => _r[index];
    public void Write(int index, ulong value) =>
        _r[index] = index == 0 ? value & 0xFFF : value & 1;
}

public sealed class NullSystemRegisters : ISystemRegisters {
    public static readonly NullSystemRegisters Instance = new();
    public ulong Read(uint address, PrivilegeLevel _) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel _) { }
    public bool Exists(uint address) => false;
}
```

## Step 5 — Pdp8TrapController (ITrapController)

```csharp
using Mechanism;

namespace Pdp8;

public sealed class Pdp8TrapController : ITrapController {
    public ulong RaiseTrap(TrapInfo trap, IArchState state) => trap.Pc;
    public ulong ReturnFromTrap(PrivilegeLevel returningFrom, IArchState state) => state.Pc;
}
```

## Step 6 — Pdp8Mechanism (IMechanism)

```csharp
using Mechanism;
using Pdp8.Decode;
using Pdp8.Execute;

namespace Pdp8;

public sealed class Pdp8Mechanism : IMechanism {
    public string Name => "PDP-8";
    public IDecoder Decoder { get; } = new Pdp8Decoder();
    public IExecutor Executor { get; } = new Pdp8Executor();
    public IImpulseCracker? UopCracker => null;
    public ITrapController TrapController { get; } = new Pdp8TrapController();

    public IArchState CreateArchState() => new Pdp8ArchState();
}
```

## Running a program

Memory is word-addressed on real hardware (12-bit word at address `n`), but
Horologium is byte-addressed. The convention used throughout this guide is:
**word `n` lives at byte address `n * 2`**. Load a PDP-8 binary by packing
each 12-bit word into 2 little-endian bytes and calling `memory.Load(0, bytes)`.

```csharp
using Mechanism;
using Pipeline;
using Pdp8;

byte[] binary = LoadPdp8Binary("program.bin"); // 2 bytes per word, LE
var memory = new FlatMemory(4096 * 2);
memory.Load(0, binary);

var train = new FiveStageTrain(new Pdp8Mechanism(), memory, entryPoint: 0);
var result = train.Run(maxTicks: 1_000_000);
```

Use `SingleCycleTrain` first to validate the `Mechanism` in isolation, then
switch to `FiveStageTrain` to observe how AC-dependency chains stall.

## Things to watch out for

**Word vs byte addresses.** The PDP-8 is word-addressed; Horologium is
byte-addressed. Every memory access in the executor must multiply the word
address by 2. `ResolveEa` returns a word address; convert with `ea * 2` before
calling `memory.Read/Write`. PC is always a byte address (the pipeline manages
it). Bytes stored in `FetchHint.InstructionSize = 2` keep the PC incrementing
by 2 per instruction, which corresponds to one word — consistent.

**12-bit masking.** Always mask AC writes with `& 0xFFF` and L writes with
`& 1`. The `Pdp8RegisterFile.Write` enforces this, but also mask in the
executor before building `RegisterResult` so the value stored is always clean.

**OPR micro-operation sequencing.** Group 1 micro-ops execute in a fixed
order: (1) CLA/CLL, (2) CMA/CML, (3) rotate, (4) IAC. BSW is mutually
exclusive with rotates in practice (bit 0 vs bits 2-3) but follow the same
slot. Getting this order wrong produces wrong results for combined operations
like `CMA IAC` (two's complement negate).

**OPR Group 2 sense.** Bit 3 of the OPR Group 2 encoding selects OR-mode
(1) vs AND-mode (0). AND-mode is the inverted conditions: skip if *none* of
the tested conditions is true. This is the NOR behavior — common implementations
get this backwards. The mnemonic SPA (Skip if Positive or zero Accumulator)
is the AND-mode inversion of SMA.

**JMS return address.** JMS stores `PC + 1` in *word* units at `mem[ea]`, then
jumps to `ea + 1`. In byte terms: stores `(pc + 2) / 2` at byte address `ea * 2`,
branches to `ea * 2 + 2`. Subroutines return via `JMP I ea` (indirect jump
through `mem[ea]`), which reads the saved return address and jumps there.

**Auto-increment addresses.** Indirect through word addresses 8–15 (byte
addresses 16–30) auto-increments the location *before* loading the pointer.
This is a hardware feature used to implement loops without modifying program
text. The executor handles it in `ResolveEa`.

**ISZ skip distance.** ISZ skips the *next* instruction, not the one after.
In byte terms: the skip target is `pc + 4` (current 2-byte instruction plus
the 2-byte instruction being skipped). This is identical to the RISC-V
compressed-branch skip pattern.

**OPR Group 3 (EAE).** Bits 8=1, 0=1 select the Extended Arithmetic Element
— multiply, divide, and normalize. Not covered in this guide; the decoder
should throw `IllegalInstructionException` for these until they are needed.
