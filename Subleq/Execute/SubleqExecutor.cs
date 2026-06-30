using Mechanism;
using Subleq.Decode;

namespace Subleq.Execute;

public sealed class SubleqExecutor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var op = (SubleqOp)instruction.Payload!;
        ulong pc = instruction.Pc;

        long valA = op.A == -1 ? 0 : (long)(int)memory.Read((ulong)op.A, 4);
        long valB = op.B == -1 ? 0 : (long)(int)memory.Read((ulong)op.B, 4);
        long result = valB - valA;

        if (op.B != -1)
            memory.Write((ulong)op.B, (ulong)(uint)(int)result, 4);
        else
            Console.Write((char)(result & 0xFF));

        if (op.C < 0) return new ExecuteResult { IsHalt = true, };

        bool taken = result <= 0;
        ulong target = taken ? (ulong)op.C : pc + 12;

        // Self-branch: program halted by looping to itself.
        if (taken && target == pc) return new ExecuteResult { IsHalt = true, };

        return ExecuteResult.WithBranch(taken, target);
    }
}