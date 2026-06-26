# J1 Forth — ALU Encoding Reference

Created: 2026-06-26
Tags: #j1 #forth #reference

---

Reference for ALU instruction encoding in the J1 CPU. See
[[7b2f2489-implementing-j1-forth]] for the full implementation guide.

## ALU instruction bit layout

An ALU instruction has bits [15:13] = `011`. The remaining 13 bits:

```
[12]    R→PC      return: pop return stack, set PC = R
[11:8]  TOut      T' selector — what new T becomes after the op
[7]     T→N       copy old T to N (before stack adjustment)
[6]     T→R       copy old T to R (before return-stack adjustment)
[5]     N→[T]     write N to memory word at address T (store)
[4]     reserved  always 0
[3:2]   DDelta    data stack adjustment (2-bit signed)
[1:0]   RDelta    return stack adjustment (2-bit signed)
```

## Stack delta encoding

Both DDelta and RDelta use 2-bit two's complement:

| Bits | Delta | Typical use |
|---|---|---|
| `00` | 0 | no change |
| `01` | +1 | push |
| `10` | -2 | double pop |
| `11` | -1 | pop |

## T' selector (TOut, bits [11:8])

| Code | New T | Notes |
|---|---|---|
| `0x0` | T | unchanged; used for side-effect-only ops |
| `0x1` | N | expose second item |
| `0x2` | T + N | 16-bit add (wraps) |
| `0x3` | T AND N | bitwise AND |
| `0x4` | T OR N | bitwise OR |
| `0x5` | T XOR N | bitwise XOR |
| `0x6` | ~T | bitwise NOT (12 bits on real hw; 16 bits here) |
| `0x7` | N == T ? -1 : 0 | equality; -1 = `0xFFFF` (Forth true) |
| `0x8` | N < T (signed) ? -1 : 0 | signed less-than |
| `0x9` | N >> T | logical right shift |
| `0xA` | T − 1 | decrement |
| `0xB` | R | read top of return stack |
| `0xC` | mem[T] | 16-bit word load; T is word address |
| `0xD` | N << T | logical left shift |
| `0xE` | DSP | data stack depth (useful for DEPTH) |
| `0xF` | N < T (unsigned) ? -1 : 0 | unsigned less-than |

## Common Forth words

Each row shows the 13-bit ALU op field (bit 12 = R→PC; bits 11:0 listed as hex).

| Forth word | Stack effect | R→PC | TOut | T→N | T→R | N→[T] | DDelta | RDelta | Op[11:0] |
|---|---|---|---|---|---|---|---|---|---|
| NOP | ( -- ) | 0 | 0 | 0 | 0 | 0 | 0 | 0 | `0x000` |
| DUP | ( n -- n n ) | 0 | 0 | 1 | 0 | 0 | +1 | 0 | `0x081` |
| DROP | ( n -- ) | 0 | 1 | 0 | 0 | 0 | -1 | 0 | `0x103` |
| SWAP | ( a b -- b a ) | 0 | 1 | 1 | 0 | 0 | 0 | 0 | `0x180` |
| OVER | ( a b -- a b a ) | 0 | 1 | 1 | 0 | 0 | +1 | 0 | `0x181` |
| NIP | ( a b -- b ) | 0 | 0 | 0 | 0 | 0 | -1 | 0 | `0x003` |
| + | ( a b -- a+b ) | 0 | 2 | 0 | 0 | 0 | -1 | 0 | `0x203` |
| AND | ( a b -- a&b ) | 0 | 3 | 0 | 0 | 0 | -1 | 0 | `0x303` |
| OR | ( a b -- a\|b ) | 0 | 4 | 0 | 0 | 0 | -1 | 0 | `0x403` |
| XOR | ( a b -- a^b ) | 0 | 5 | 0 | 0 | 0 | -1 | 0 | `0x503` |
| INVERT | ( n -- ~n ) | 0 | 6 | 0 | 0 | 0 | 0 | 0 | `0x600` |
| = | ( a b -- flag ) | 0 | 7 | 0 | 0 | 0 | -1 | 0 | `0x703` |
| < | ( a b -- flag ) | 0 | 8 | 0 | 0 | 0 | -1 | 0 | `0x803` |
| U< | ( a b -- flag ) | 0 | F | 0 | 0 | 0 | -1 | 0 | `0xF03` |
| RSHIFT | ( n u -- n>>u ) | 0 | 9 | 0 | 0 | 0 | -1 | 0 | `0x903` |
| LSHIFT | ( n u -- n<<u ) | 0 | D | 0 | 0 | 0 | -1 | 0 | `0xD03` |
| 1- | ( n -- n-1 ) | 0 | A | 0 | 0 | 0 | 0 | 0 | `0xA00` |
| @ | ( addr -- n ) | 0 | C | 0 | 0 | 0 | 0 | 0 | `0xC00` |
| ! | ( n addr -- n ) | 0 | 0 | 0 | 0 | 1 | -1 | 0 | `0x023` |
| >R | ( n -- ) | 0 | 0 | 0 | 1 | 0 | -1 | +1 | `0x047` |
| R> | ( -- n ) | 0 | B | 0 | 0 | 0 | +1 | -1 | `0xB81`... |
| R@ | ( -- n ) | 0 | B | 1 | 0 | 0 | +1 | 0 | `0xB81` |
| EXIT | ( -- ) | 1 | 0 | 0 | 0 | 0 | 0 | -1 | bit12 + `0x003` |
| DEPTH | ( -- n ) | 0 | E | 1 | 0 | 0 | +1 | 0 | `0xE81` |

**Note on `!`**: N→[T] stores N to mem[T] and DDelta=-1 pops T. This leaves N as
the new T (standard J1 convention: `!` is `( n addr -- n )`), not `( n addr -- )`
as in ANS Forth. To get standard drop-both behaviour, add an explicit DROP after.

**Note on R> vs R@**: R> moves R to T and pops the return stack (RDelta=-1);
R@ copies R to T without popping. Both push onto the data stack (DDelta=+1).

## Executor implementation

```csharp
using Mechanism;
using J1.Decode;

namespace J1.Execute;

public sealed class J1Executor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var j1 = (J1ArchState)state;
        ulong pc = instruction.Pc;

        return instruction.Payload switch {
            Literal lit  => ExecLiteral(lit, j1),
            Jump jmp     => ExecuteResult.WithBranch(true, (ulong)jmp.WordTarget * 2),
            CondJump cj  => ExecCondJump(cj, pc, j1),
            Call call    => ExecCall(call, pc, j1),
            Alu alu      => ExecAlu(alu, pc, j1, memory),
            _ => throw new InvalidOperationException($"Unknown J1 op: {instruction.Payload}"),
        };
    }

    private static ExecuteResult ExecLiteral(Literal lit, J1ArchState j1) =>
        new() { SideEffect = s => ((J1ArchState)s).DPush(lit.Value) };

    private static ExecuteResult ExecCondJump(CondJump cj, ulong pc, J1ArchState j1) {
        bool taken = j1.T == 0;
        ulong target = taken ? (ulong)cj.WordTarget * 2 : pc + 2;
        return new ExecuteResult {
            BranchTaken = taken,
            BranchTarget = target,
            SideEffect = s => ((J1ArchState)s).DPop(),
        };
    }

    private static ExecuteResult ExecCall(Call call, ulong pc, J1ArchState j1) =>
        new ExecuteResult {
            BranchTaken = true,
            BranchTarget = (ulong)call.WordTarget * 2,
            SideEffect = s => ((J1ArchState)s).RPush((ushort)((pc / 2) + 1)),
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

        bool isReturn = op.ReturnFromR;
        ulong branchTarget = isReturn ? (ulong)r * 2 : pc + 2;

        ushort captT = t, captN = n, captNewT = newT;
        return new ExecuteResult {
            BranchTaken = isReturn,
            BranchTarget = branchTarget,
            SideEffect = s => {
                var j = (J1ArchState)s;
                if (op.NtoMem)   memory.Write((ulong)captT * 2, captN, 2);
                if (op.TtoR)     j.R = captT;
                if (isReturn)    j.RPop();
                else             ApplyRDelta(j, op.RDelta);
                ApplyDDelta(j, op.DDelta, captNewT);
                if (op.TtoN)     j.N = captT;
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

**Stack sequencing rule.** The SideEffect lambda applies operations in this order:
N→[T] (memory store), T→R (save T to return stack), R-stack adjustment, D-stack
adjustment (which sets new T), T→N (overwrite N with old T). This order ensures
that `captT` and `captN` hold the pre-op values throughout.
