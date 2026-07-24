#region

using J1.Decode;
using Mechanism;

#endregion

namespace J1.Execute;

public sealed class J1Executor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var j1 = (J1ArchState)state;
        ulong pc = instruction.Pc;

        return instruction.Payload switch {
            Literal lit => ExecLiteral(lit),
            Jump jmp    => ExecuteResult.WithBranch(true, (ulong)jmp.WordTarget * 2),
            CondJump cj => ExecCondJump(cj, pc, j1),
            Call call   => ExecCall(call, pc),
            Alu alu     => ExecAlu(alu, pc, j1, memory),
            _           => throw new InvalidOperationException($"Unknown J1 op: {instruction.Payload}"),
        };
    }

    private static ExecuteResult ExecLiteral(Literal lit) =>
        new() { SideEffect = s => ((J1ArchState)s).DPush(lit.Value), };

    private static ExecuteResult ExecCondJump(CondJump cj, ulong pc, J1ArchState j1) {
        bool taken = j1.T == 0;
        ulong target = taken ? (ulong)cj.WordTarget * 2 : pc + 2;
        return new ExecuteResult {
            BranchTaken = taken,
            BranchTarget = target,
            SideEffect = s => ((J1ArchState)s).DPop(),
        };
    }

    private static ExecuteResult ExecCall(Call call, ulong pc) =>
        new() {
            BranchTaken = true,
            BranchTarget = (ulong)call.WordTarget * 2,
            SideEffect = s => ((J1ArchState)s).RPush((ushort)(pc / 2 + 1)),
        };

    private static ExecuteResult ExecAlu(Alu op, ulong pc, J1ArchState j1, IMemory memory) {
        ushort t = j1.T, n = j1.N, r = j1.R;

        ushort newT = op.Out switch {
            0x0 => t,
            0x1 => n,
            0x2 => (ushort)(t + n),
            0x3 => (ushort)(t & n),
            0x4 => (ushort)(t | n),
            0x5 => (ushort)(t ^ n),
            0x6 => (ushort)~t,
            0x7 => n == t ? (ushort)0xFFFF : (ushort)0,
            0x8 => (short)n < (short)t ? (ushort)0xFFFF : (ushort)0,
            0x9 => (ushort)(n >> (t & 0xF)),
            0xA => (ushort)(t - 1),
            0xB => r,
            0xC => (ushort)memory.Read((ulong)t * 2, 2),
            0xD => (ushort)(n << (t & 0xF)),
            0xE => (ushort)j1.Dsp,
            0xF => n < t ? (ushort)0xFFFF : (ushort)0,
            _   => t,
        };

        bool isReturn = op.ReturnFromR;
        ulong branchTarget = isReturn ? (ulong)r * 2 : pc + 2;

        return new ExecuteResult {
            BranchTaken = isReturn,
            BranchTarget = branchTarget,
            SideEffect = s => {
                var j = (J1ArchState)s;
                if (op.NtoMem) memory.Write((ulong)t * 2, n, 2);
                if (isReturn)
                    j.RPop();
                else
                    ApplyRDelta(j, op.RDelta);
                if (op.TtoR) j.R = t; // write to new RSP slot after adjustment
                ApplyDDelta(j, op.DDelta, newT);
                if (op.TtoN) j.N = t;
            },
        };
    }

    private static void ApplyDDelta(J1ArchState j, int delta, ushort newT) {
        switch (delta) {
            case 0: j.T = newT; break;
            case 1: j.DPush(newT); break;
            case -1:
                j.DPop();
                j.T = newT;
                break;
            case -2:
                j.DPop();
                j.DPop();
                j.T = newT;
                break;
        }
    }

    private static void ApplyRDelta(J1ArchState j, int delta) {
        switch (delta) {
            case 1:  j.RPush(0); break;
            case -1: j.RPop(); break;
        }
    }
}