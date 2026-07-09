using Mechanism;
using Move.Decode;

namespace Move.Execute;

public sealed class MoveExecutor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var mv = (MoveInstruction)instruction;
        var m = (MoveArchState)state;

        ushort value = ReadSource(mv.Src, mv.Imm, m);

        return mv.Dst switch {
            <= 0x07 => WriteReg(mv.Dst, value),
            0x10    => SetAluOp((byte)value),
            0x11    => SetAluIn1(value),
            0x12    => TriggerAlu(m, value),
            0x20    => TriggerLoad(memory, value),
            0x21    => LatchAddr(value),
            0x22    => TriggerStore(memory, m, value),
            0x30    => SetBrCond(value),
            0x31    => TriggerBranch(m, value),
            0xFF    => new ExecuteResult { IsHalt = true, },
            _       => new ExecuteResult(),
        };
    }

    private static ushort ReadSource(byte src, ushort imm, MoveArchState m) => src switch {
        <= 0x07 => m.R[src],
        0x10    => m.AluOut,
        0x20    => m.MemOut,
        0xFE    => imm,
        0xFF    => (ushort)m.Pc,
        _       => 0,
    };

    private static ExecuteResult WriteReg(byte idx, ushort value) =>
        new() { SideEffect = s => ((MoveArchState)s).R[idx] = value, };

    private static ExecuteResult SetAluOp(byte op) =>
        new() { SideEffect = s => ((MoveArchState)s).AluOp = op, };

    private static ExecuteResult SetAluIn1(ushort value) =>
        new() { SideEffect = s => ((MoveArchState)s).AluIn1 = value, };

    private static ExecuteResult TriggerAlu(MoveArchState m, ushort in2) {
        ushort result = ComputeAlu(m.AluOp, m.AluIn1, in2);
        return new ExecuteResult { SideEffect = s => ((MoveArchState)s).AluOut = result, };
    }

    private static ExecuteResult TriggerLoad(IMemory memory, ushort addr) {
        var data = (ushort)memory.Read(addr, 2);
        return new ExecuteResult { SideEffect = s => ((MoveArchState)s).MemOut = data, };
    }

    private static ExecuteResult LatchAddr(ushort addr) =>
        new() { SideEffect = s => ((MoveArchState)s).MemAddr = addr, };

    private static ExecuteResult TriggerStore(IMemory memory, MoveArchState m, ushort data) {
        memory.Write(m.MemAddr, data, 2);
        return new ExecuteResult();
    }

    private static ExecuteResult SetBrCond(ushort cond) =>
        new() { SideEffect = s => ((MoveArchState)s).BrCond = cond, };

    private static ExecuteResult TriggerBranch(MoveArchState m, ushort target) =>
        new() { BranchTaken = m.BrCond != 0, BranchTarget = target, };

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
        0x0C => (ushort)-(short)b,
        0x0D => (ushort)(b + 1),
        0x0E => (ushort)(b - 1),
        0x0F => b,
        _    => 0,
    };
}