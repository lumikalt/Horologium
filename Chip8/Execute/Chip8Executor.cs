using Chip8.Decode;
using Mechanism;

namespace Chip8.Execute;

public sealed class Chip8Executor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var chip8 = (Chip8ArchState)state;
        IRegisterFile v = chip8.IntegerRegisters;
        ulong pc = instruction.Pc;

        return instruction.Payload switch {
            ClearDisplay      => ExecuteResult.Clean,
            Call              => ExecuteResult.Clean, // RCA 1802 — no-op
            Return            => ExecReturn(chip8),
            Goto g            => ExecuteResult.WithBranch(true, g.Imm),
            CallSub c         => ExecCallSub(chip8, (ushort)(pc + 2), c.Imm),
            SkipEqImm s       => v.Read(s.Vx) == s.imm ? Skip(pc) : ExecuteResult.Clean,
            SkipNeqImm s      => v.Read(s.Vx) != s.imm ? Skip(pc) : ExecuteResult.Clean,
            SkipEq s          => v.Read(s.Vx) == v.Read(s.vy) ? Skip(pc) : ExecuteResult.Clean,
            SkipNeq s         => v.Read(s.Vx) != v.Read(s.vy) ? Skip(pc) : ExecuteResult.Clean,
            SetImm s          => ExecuteResult.WithResult(s.imm),
            AddImm a          => ExecuteResult.WithResult((byte)(v.Read(a.vx) + a.imm)),
            Set s             => ExecuteResult.WithResult(v.Read(s.vy)),
            BitOr b           => ExecuteResult.WithResult((byte)(v.Read(b.vx) | v.Read(b.vy))),
            BitAnd b          => ExecuteResult.WithResult((byte)(v.Read(b.vx) & v.Read(b.vy))),
            BitXor b          => ExecuteResult.WithResult((byte)(v.Read(b.vx) ^ v.Read(b.vy))),
            Add a             => ExecAdd(v, a.vx, a.vy),
            Sub s             => ExecSub(v, s.vx, s.vy),
            ShiftRight1 s     => ExecShr(v, s.vx),
            SubYx s           => ExecSubYx(v, s.Vx, s.Vy),
            ShiftLeft1 s      => ExecShl(v, s.Vx),
            SetIImm s         => ExecSetI(chip8, s.imm),
            JumpV0Offset j    => ExecuteResult.WithBranch(true, j.imm + v.Read(0)),
            RandAnd r         => ExecuteResult.WithResult((byte)(Random.Shared.Next(256) & r.imm)),
            Draw              => ExecuteResult.Clean, // display not implemented
            SkipKeyPressed    => ExecuteResult.Clean, // key never pressed
            SkipKeyNotPressed => Skip(pc),            // key never pressed — always skip
            GetDelayTimer     => ExecuteResult.WithResult(chip8.DelayTimer),
            GetKey            => ExecuteResult.WithResult(0), // no input — return 0
            SetDelayTimer s   => ExecSetDelay(chip8, v, s.vx),
            SetSoundTimer s   => ExecSetSound(chip8, v, s.vx),
            AddToI a          => ExecAddToI(chip8, v, a.vx),
            SetISprite s      => ExecSetISprite(chip8, v, s.vx),
            BCD b             => ExecBcd(chip8, v, b.vx, memory),
            RegDump r         => ExecRegDump(chip8, v, r.vx, memory),
            RegLoad r         => ExecRegLoad(chip8, v, r.vx, memory),
            _ => throw new InvalidOperationException(
                $"Unknown CHIP-8 op: {instruction.Payload}"
            ),
        };
    }

    private static ExecuteResult Skip(ulong pc) => ExecuteResult.WithBranch(true, pc + 4);

    private static ExecuteResult ExecReturn(Chip8ArchState chip8) =>
        ExecuteResult.WithBranch(true, chip8.PopStack());

    private static ExecuteResult ExecCallSub(Chip8ArchState chip8, ushort returnAddr, ushort target) {
        chip8.PushStack(returnAddr);
        return ExecuteResult.WithBranch(true, target);
    }

    private static ExecuteResult ExecAdd(IRegisterFile v, int vx, int vy) {
        ulong sum = v.Read(vx) + v.Read(vy);
        v.Write(0xF, sum > 0xFF ? 1UL : 0UL);
        return ExecuteResult.WithResult((byte)sum);
    }

    private static ExecuteResult ExecSub(IRegisterFile v, int vx, int vy) {
        ulong a = v.Read(vx), b = v.Read(vy);
        v.Write(0xF, a >= b ? 1UL : 0UL);
        return ExecuteResult.WithResult((byte)(a - b));
    }

    private static ExecuteResult ExecSubYx(IRegisterFile v, int vx, int vy) {
        ulong a = v.Read(vx), b = v.Read(vy);
        v.Write(0xF, b >= a ? 1UL : 0UL);
        return ExecuteResult.WithResult((byte)(b - a));
    }

    private static ExecuteResult ExecShr(IRegisterFile v, int vx) {
        ulong val = v.Read(vx);
        v.Write(0xF, val & 1);
        return ExecuteResult.WithResult(val >> 1);
    }

    private static ExecuteResult ExecShl(IRegisterFile v, int vx) {
        ulong val = v.Read(vx);
        v.Write(0xF, (val >> 7) & 1);
        return ExecuteResult.WithResult((byte)(val << 1));
    }

    private static ExecuteResult ExecSetI(Chip8ArchState chip8, ushort imm) {
        chip8.I = imm;
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecSetDelay(Chip8ArchState chip8, IRegisterFile v, int vx) {
        chip8.DelayTimer = (byte)v.Read(vx);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecSetSound(Chip8ArchState chip8, IRegisterFile v, int vx) {
        chip8.SoundTimer = (byte)v.Read(vx);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecAddToI(Chip8ArchState chip8, IRegisterFile v, int vx) {
        chip8.I = (ushort)(chip8.I + v.Read(vx));
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecSetISprite(Chip8ArchState chip8, IRegisterFile v, int vx) {
        chip8.I = (ushort)(v.Read(vx) * 5); // font sprites at 0x000, 5 bytes each
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecBcd(Chip8ArchState chip8, IRegisterFile v, int vx, IMemory memory) {
        var val = (byte)v.Read(vx);
        memory.Write(chip8.I, (ulong)(val / 100), 1);
        memory.Write((ulong)(chip8.I + 1), (ulong)(val / 10 % 10), 1);
        memory.Write((ulong)(chip8.I + 2), (ulong)(val % 10), 1);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecRegDump(Chip8ArchState chip8, IRegisterFile v, int vx, IMemory memory) {
        for (var i = 0; i <= vx; i++) memory.Write((ulong)(chip8.I + i), v.Read(i), 1);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecRegLoad(Chip8ArchState chip8, IRegisterFile v, int vx, IMemory memory) {
        for (var i = 0; i <= vx; i++) v.Write(i, memory.Read((ulong)(chip8.I + i), 1));
        return ExecuteResult.Clean;
    }
}