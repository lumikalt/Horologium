#region

using Chip8.Decode;
using Mechanism;

#endregion

namespace Chip8.Execute;

public sealed class Chip8Executor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        var chip8 = (Chip8ArchState)state;
        IRegisterFile v = chip8.IntegerRegisters;
        ulong pc = instruction.Pc;

        return instruction.Payload switch {
            ClearDisplay        => ExecClearDisplay(chip8),
            Call                => ExecuteResult.Clean, // RCA 1802 — no-op
            Return              => ExecReturn(chip8),
            Goto g              => ExecuteResult.WithBranch(true, g.Imm),
            CallSub c           => ExecCallSub(chip8, (ushort)(pc + 2), c.Imm),
            SkipEqImm s         => v.Read(s.Vx) == s.Imm ? Skip(pc) : ExecuteResult.Clean,
            SkipNeqImm s        => v.Read(s.Vx) != s.Imm ? Skip(pc) : ExecuteResult.Clean,
            SkipEq s            => v.Read(s.Vx) == v.Read(s.Vy) ? Skip(pc) : ExecuteResult.Clean,
            SkipNeq s           => v.Read(s.Vx) != v.Read(s.Vy) ? Skip(pc) : ExecuteResult.Clean,
            SetImm s            => ExecuteResult.WithResult(s.Imm),
            AddImm a            => ExecuteResult.WithResult((byte)(v.Read(a.Vx) + a.Imm)),
            Set s               => ExecuteResult.WithResult(v.Read(s.Vy)),
            BitOr b             => ExecuteResult.WithResult((byte)(v.Read(b.Vx) | v.Read(b.Vy))),
            BitAnd b            => ExecuteResult.WithResult((byte)(v.Read(b.Vx) & v.Read(b.Vy))),
            BitXor b            => ExecuteResult.WithResult((byte)(v.Read(b.Vx) ^ v.Read(b.Vy))),
            Add a               => ExecAdd(v, a.Vx, a.Vy),
            Sub s               => ExecSub(v, s.Vx, s.Vy),
            ShiftRight1 s       => ExecShr(v, s.Vx),
            SubYx s             => ExecSubYx(v, s.Vx, s.Vy),
            ShiftLeft1 s        => ExecShl(v, s.Vx),
            SetIImm s           => ExecSetI(chip8, s.Imm),
            JumpV0Offset j      => ExecuteResult.WithBranch(true, j.Imm + v.Read(0)),
            RandAnd r           => ExecuteResult.WithResult((byte)(Random.Shared.Next(256) & r.Imm)),
            Draw d              => ExecDraw(chip8, v, d.Vx, d.Vy, d.Imm, memory),
            SkipKeyPressed s    => chip8.Keys[v.Read(s.Vx) & 0xF] ? Skip(pc) : ExecuteResult.Clean,
            SkipKeyNotPressed s => !chip8.Keys[v.Read(s.Vx) & 0xF] ? Skip(pc) : ExecuteResult.Clean,
            GetDelayTimer       => ExecuteResult.WithResult(chip8.DelayTimer),
            GetKey              => ExecGetKey(chip8, pc),
            SetDelayTimer s     => ExecSetDelay(chip8, v, s.Vx),
            SetSoundTimer s     => ExecSetSound(chip8, v, s.Vx),
            AddToI a            => ExecAddToI(chip8, v, a.Vx),
            SetISprite s        => ExecSetISprite(chip8, v, s.Vx),
            Bcd b               => ExecBcd(chip8, v, b.Vx, memory),
            RegDump r           => ExecRegDump(chip8, v, r.Vx, memory),
            RegLoad r           => ExecRegLoad(chip8, v, r.Vx, memory),
            _ => throw new InvalidOperationException(
                $"Unknown CHIP-8 op: {instruction.Payload}"
            ),
        };
    }

    private static ExecuteResult Skip(ulong pc) => ExecuteResult.WithBranch(true, pc + 4);

    private static ExecuteResult ExecClearDisplay(Chip8ArchState chip8) {
        Array.Clear(chip8.Display);
        return ExecuteResult.Clean;
    }

    // Sprites are clipped at the right (x≥64) and bottom (y≥32) edges per COSMAC VIP behavior.
    private static ExecuteResult ExecDraw(
        Chip8ArchState chip8,
        IRegisterFile v,
        int vx,
        int vy,
        byte height,
        IMemory memory
    ) {
        var x0 = (int)(v.Read(vx) % 64);
        var y0 = (int)(v.Read(vy) % 32);
        var collision = false;
        for (var row = 0; row < height; row++) {
            int y = y0 + row;
            if (y >= 32) break;
            var spriteByte = (byte)memory.Read((ulong)(chip8.I + row), 1);
            for (var col = 0; col < 8; col++) {
                int x = x0 + col;
                if (x >= 64) break;
                if ((spriteByte & (0x80 >> col)) == 0) continue;
                int idx = y * 64 + x;
                if (chip8.Display[idx]) collision = true;
                chip8.Display[idx] ^= true;
            }
        }

        v.Write(0xF, collision ? 1UL : 0UL);
        return ExecuteResult.Clean;
    }

    // Stalls (re-executes same PC) until any key is pressed; returns its index in Vx.
    private static ExecuteResult ExecGetKey(Chip8ArchState chip8, ulong pc) =>
        chip8.TryGetPressedKey(out int key) ? ExecuteResult.WithResult((ulong)key) : ExecuteResult.WithBranch(true, pc);

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