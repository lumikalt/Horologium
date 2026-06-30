# Implementing a GA144 F18A ISA Plugin

## Architecture summary

The **GA144** (GreenArrays 144) is a chip with **144 F18A nodes** arranged in an 18×8 grid.
Each node is a minimal stack computer:

| Property | Value |
|---|---|
| Word size | 18 bits |
| Instruction word | 18 bits, up to 4 slots: 5+5+5+3 bits |
| Data stack | 8 entries (T = top, S = second) |
| Return stack | 8 entries (R = top) |
| Registers | A (address), B (port/memory pointer), P (program counter) |
| Address space | 128 words per node (64 RAM + 64 ROM) |
| PC width | 9 bits (word addresses) |
| Inter-node | Synchronous rendezvous over North/East/South/West ports |

The instruction word is fetched as a whole, then its four slots execute left-to-right.
If a control-flow slot is reached, the remaining slots are skipped.

## Slot encoding

```
bit:  17 16 15 14 13   12 11 10  9  8    7  6  5  4  3    2  1  0
      [   slot 0   ]   [   slot 1   ]   [   slot 2   ]   [ slot3 ]
       5 bits             5 bits           5 bits          3 bits
```

Branch address extraction (when a jump/call/if/next opcode appears in a slot):

| Slot | Bits remaining | Max address |
|------|----------------|-------------|
| 0    | bits[12:0]     | 8191 words  |
| 1    | bits[7:0]      | 255 words   |
| 2    | bits[2:0]      | 7 words     |
| 3    | none           | 0 (degenerate) |

## Opcodes

| Code | Mnemonic | Effect |
|------|----------|--------|
| `00000` | `;` | return: P ← R, pop R |
| `00001` | `ex` | exchange T ↔ R (neither stack changes depth) |
| `00010` | `jump` | P ← address field |
| `00011` | `call` | push P to R, P ← address field |
| `00100` | `unext` | R−−; if R > 0, jump to start of current word; else pop R |
| `00101` | `next` | R−−; if R > 0, P ← address; else pop R, fall through |
| `00110` | `if` | if T ≠ 0, P ← address; else fall through (T unchanged) |
| `00111` | `@p+` | push fetch(P), P++ (literal load from instruction stream) |
| `01000` | `@+` | push fetch(A), A++ |
| `01001` | `@b` | push fetch(B) |
| `01010` | `@` | push fetch(A) |
| `01011` | `!p+` | store T → P, pop T, P++ |
| `01100` | `!+` | store T → A, pop T, A++ |
| `01101` | `!b` | store T → B, pop T |
| `01110` | `!` | store T → A, pop T |
| `01111` | `2*` | T ← T << 1 (18-bit, top bit lost) |
| `10000` | `2/` | T ← T >> 1 (arithmetic, sign from bit 17) |
| `10001` | `-` | T ← ~T (bitwise NOT; negate with `0 - n` idiom) |
| `10010` | `+` | T ← S + T, pop S (stack shrinks by 1) |
| `10011` | `and` | T ← S & T, pop S |
| `10100` | `xor` | T ← S ^ T, pop S |
| `10101` | `drop` | pop data stack (discard T) |
| `10110` | `dup` | push T (stack grows by 1) |
| `10111` | `pop` | push R to data stack, pop return stack |
| `11000` | `over` | push S (stack grows by 1, T = old S) |
| `11001` | `a` | push A register |
| `11010` | `.` | nop |
| `11011` | `push` | push T to return stack, pop data stack |
| `11100` | `b!` | B ← T, pop T |
| `11101` | `a!` | A ← T, pop T |
| `11110` | — | undefined |
| `11111` | — | undefined |

Slot 3 only has 3 bits, so it can encode opcodes `000`–`111` (`;` through `@p+`).

## Example programs (assembly notation)

### Literal load
```
@p+  .  .  .   ; slot 0 = @p+, slots 1-3 = nop
0x12345         ; literal word follows
; After execution: T = 0x12345, P advanced past literal
```

### Countdown loop
```
; Push count (e.g. 5) to return stack, then:
; word at address 3:
.  .  .  unext  ; slots 0-2 = nop, slot 3 = unext
                ; executes 5+1 times, then pops R and falls through
```

### Conditional forward skip
```
if  .  .  .   ; jump to address in bits[12:0] if T ≠ 0
              ; remaining slots of *this* word are skipped after jump
```

### Inter-node send (via B register)
```
@p+  b!  .  .  ; load port address into B
0x1E5           ; East port address (node-specific)
@p+  !b  .  .  ; load value, store to port (blocks until receiver is ready)
0xABCD
```

## Mapping to Horologium concepts

| F18A concept | Horologium mapping |
|---|---|
| 18-bit instruction word (4 slots) | Single `ITooth`; executor iterates slots internally |
| T/S data stack | `F18AArchState` with circular `uint[]` + DSP pointer |
| R return stack | `F18AArchState` with circular `uint[]` + RSP pointer |
| A, B registers | Fields on `F18AArchState` |
| P register (PC) | `IArchState.Pc` (byte address = word_addr × 4) |
| `@p+` literal skip | Executor returns explicit `BranchTarget = pc + 4 + literals×4` |
| `unext` self-loop | Executor returns `BranchTaken=true, BranchTarget = tooth.Pc` |
| Port read/write | `IMemory` wrapper routes port-range addresses to `RendezvousPort` |
| Inter-node blocking | `RendezvousPort.TryRead`/`TryWrite`: never consume unless both sides present |

**Why NOT `IImpulseCracker`?** Slots share mutable stack state — slot 1 reads T that slot 0
wrote. Micro-ops are independent by design; cracking into Impulses would require forwarding
all intra-word dependences. It is simpler and more faithful to model the word as one `ITooth`
and iterate slots in `IExecutor.Execute`.

## Implementation steps

### Step 1 — Arch state (`F18AArchState`)

```csharp
public sealed class F18AArchState : IArchState {
    private const int Depth = 8;
    private readonly uint[] _dstack = new uint[Depth];
    private readonly uint[] _rstack = new uint[Depth];
    private int _dsp, _rsp;

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters { get; }  // F18ARegisterFile
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    // T = top of data stack, S = second, R = top of return stack
    public uint T  { get => _dstack[_dsp & (Depth-1)]; set => _dstack[_dsp & (Depth-1)] = value; }
    public uint S  => _dstack[(_dsp - 1) & (Depth-1)];
    public uint R  { get => _rstack[_rsp & (Depth-1)]; set => _rstack[_rsp & (Depth-1)] = value; }
    public uint A { get; set; }
    public uint B { get; set; }

    public void DPush(uint v) { _dsp++; T = v; }
    public uint DPop()        { uint v = T; _dsp--; return v; }
    public void RPush(uint v) { _rsp++; R = v; }
    public uint RPop()        { uint v = R; _rsp--; return v; }

    public void CopyFrom(F18AArchState src) { /* copy all fields */ }
    public IArchState Snapshot() { var s = new F18AArchState(); s.CopyFrom(this); return s; }
    public void Reset() { /* zero everything */ }
}
```

### Step 2 — Instruction token (`F18AInstruction`)

```csharp
public sealed class F18AInstruction(ulong pc, uint raw) : ITooth {
    public ulong Pc          => pc;
    public uint  RawEncoding => raw;
    public int   SizeBytes   => 4;
    public int   DestinationRegister    => -1;  // stack machine
    public IReadOnlyList<int> SourceRegisters => [];
    public object? Payload   => null;
    public ToothClass Class  { get; init; }

    // Slot opcodes extracted from the 18-bit word
    public byte Slot0 => (byte)((raw >> 13) & 0x1F);
    public byte Slot1 => (byte)((raw >>  8) & 0x1F);
    public byte Slot2 => (byte)((raw >>  3) & 0x1F);
    public byte Slot3 => (byte)( raw        & 0x07);

    // Address field remaining after each slot (masked to 9-bit word address)
    public uint AddrAfterSlot0 => (raw & 0x1FFF) & 0x1FF;   // 13 bits → 9 bits
    public uint AddrAfterSlot1 => (raw & 0x00FF) & 0x1FF;   // 8 bits
    public uint AddrAfterSlot2 => (raw & 0x0007) & 0x1FF;   // 3 bits
}
```

### Step 3 — Decoder (`F18ADecoder`)

```csharp
public int InstructionSize(ulong pc, IMemory memory) => 4;

public FetchHint GetFetchHint(ulong pc, uint firstWord) {
    // 18-bit word lives in bits [17:0] of firstWord
    uint raw = firstWord & 0x3FFFF;
    byte s0  = (byte)((raw >> 13) & 0x1F);
    bool isBranch = s0 is Op.Jump or Op.Call or Op.Return or Op.If or Op.Next or Op.Unext;
    var target = (s0 is Op.Jump or Op.Call)
        ? ((raw & 0x1FFF & 0x1FF) * 4ul, true)
        : default;
    return new FetchHint { InstructionSize = 4, IsBranch = isBranch, BranchTarget = target };
}

public ITooth Decode(ulong pc, uint firstWord) {
    uint raw = firstWord & 0x3FFFF;
    byte s0  = (byte)((raw >> 13) & 0x1F);
    var cls  = ClassifySlot0(s0);
    return new F18AInstruction(pc, raw) { Class = cls };
}
```

### Step 4 — Executor (`F18AExecutor`)

The key pattern: take a snapshot, execute all slots on the snapshot (memory writes go
directly to `IMemory`), return a `SideEffect` closure that applies the final snapshot state.

```csharp
public ExecuteResult Execute(ITooth tooth, IArchState archState, IMemory memory) {
    var insn  = (F18AInstruction)tooth;
    var work  = (F18AArchState)archState.Snapshot(); // local mutable copy

    bool branchTaken = false;
    ulong branchTarget = 0;
    bool halted = false;
    int pOffset = 0; // literal words consumed by @p+ or !p+

    for (int i = 0; i < 4 && !branchTaken && !halted; i++) {
        byte op   = i switch { 0=>insn.Slot0, 1=>insn.Slot1, 2=>insn.Slot2, _=>insn.Slot3 };
        uint addr = i switch { 0=>insn.AddrAfterSlot0, 1=>insn.AddrAfterSlot1, 2=>insn.AddrAfterSlot2, _=>0 };
        ExecuteSlot(op, addr, tooth.Pc, work, memory, ref pOffset, ref branchTaken, ref branchTarget, ref halted);
    }

    // @p+ / !p+ consumed literals: override sequential PC to skip them
    if (!branchTaken && pOffset > 0) {
        branchTaken   = true;
        branchTarget  = tooth.Pc + 4 + (ulong)(pOffset * 4);
    }

    var captured = work;
    return new ExecuteResult {
        BranchTaken   = branchTaken,
        BranchTarget  = branchTaken ? branchTarget : null,
        IsHalt        = halted,
        SideEffect    = s => ((F18AArchState)s).CopyFrom(captured),
    };
}
```

Key slot-execution cases:

```csharp
case Op.Return:   branchTaken = true;  branchTarget = work.RPop() * 4; break;
case Op.Jump:     branchTaken = true;  branchTarget = addr * 4; break;
case Op.Call:     branchTaken = true;  work.RPush((uint)(tooth.Pc / 4 + 1)); branchTarget = addr * 4; break;
case Op.Unext:
    if (work.R > 0) { work.R--; branchTaken = true; branchTarget = tooth.Pc; }
    else             { work.RPop(); }
    break;
case Op.FetchP: {
    ulong litAddr = tooth.Pc + 4 + (ulong)(pOffset * 4);
    uint  val     = (uint)memory.Read(litAddr, 4) & 0x3FFFF;
    work.DPush(val);
    pOffset++;
    break;
}
case Op.Add:   { uint n = work.DPop(); work.T = (work.T + n) & 0x3FFFF; break; }
// ... etc.
```

### Step 5 — Register file

Expose T, S, A, B, R (5 named registers) for the `Face` register panel.
`DestinationRegister == -1` on all instructions (stack machine — pipeline does no renaming).

### Step 6 — Multi-core ports (`RendezvousPort`)

```csharp
public sealed class RendezvousPort {
    private uint? _pending; // value waiting for the other side
    private bool  _reading; // a reader is waiting

    // Returns true only when both a writer and a reader are present this tick.
    public bool TryWrite(uint value) { _pending = value; return _reading; }
    public bool TryRead(out uint value) {
        _reading = true;
        if (_pending.HasValue) { value = _pending.Value; _pending = null; _reading = false; return true; }
        value = 0; return false;
    }
    public void Reset() { _pending = null; _reading = false; }
}
```

The port is stateless between ticks: the `F18ANode.Step()` loop retries the current
instruction until the port transfer completes, holding P at the port-access word.

## Gotchas

1. **P is the PC.** `@p+` and `!p+` advance the program counter — the executor must
   return an explicit `BranchTarget` (even for sequential execution) when these fire.

2. **Slot 3 has only 3 bits.** It can encode opcodes 0–7 only (`;` through `@p+`).
   `jump`/`call`/`if`/`next` in slot 3 produce a degenerate address of 0.

3. **`unext` is a self-loop.** It jumps back to `tooth.Pc` (the current word), not
   to a different address. The executor returns `BranchTarget = tooth.Pc`.

4. **Address is in word units, PC is in bytes.** All branch targets extracted from
   the instruction word are word addresses; multiply by 4 for Horologium byte addresses.

5. **Port blocking is cooperative.** Neither `TryRead` nor `TryWrite` should consume
   the value until both sides are present in the same simulated tick, otherwise a retry
   after a partial tick would double-deliver.

6. **`+` is unsigned 18-bit addition.** Mask result to `& 0x3FFFF`. The F18A has no
   carry flag; carry is discarded.

7. **`2/` is arithmetic (signed) right shift on 18-bit.** Sign bit is bit 17.
   Implement as `(int)(T << 14) >> 15` to get the sign, or sign-extend before shifting.
