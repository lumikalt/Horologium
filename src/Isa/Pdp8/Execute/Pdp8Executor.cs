#region

using Mechanism;
using Pdp8.Decode;

#endregion

namespace Pdp8.Execute;

public sealed class Pdp8Executor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        ulong pc = instruction.Pc;
        var ac = (int)state.IntegerRegisters.Read(0);
        var l = (int)state.IntegerRegisters.Read(1);

        return instruction.Payload switch {
            MriOp mri   => ExecMri(mri, pc, ac, l, memory),
            Opr1Op opr1 => ExecOpr1(opr1, ac, l),
            Opr2Op opr2 => ExecOpr2(opr2, pc, ac, l),
            IotOp       => ExecuteResult.Clean, // no peripheral model
            _           => throw new InvalidOperationException($"Unknown PDP-8 op: {instruction.Payload}"),
        };
    }

    private static int ResolveEa(MriOp mri, IMemory memory) {
        int ea = mri.DirectEa;
        if (!mri.Indirect) return ea;
        // Auto-increment for word addresses 8–15 (octal 010–017)
        if (ea is >= 8 and <= 15) {
            int val = ((int)memory.Read((ulong)(ea * 2), 2) + 1) & 0xFFF;
            memory.Write((ulong)(ea * 2), (ulong)val, 2);
            return val;
        }

        return (int)memory.Read((ulong)(ea * 2), 2) & 0xFFF;
    }

    private static ExecuteResult ExecMri(MriOp mri, ulong pc, int ac, int l, IMemory memory) {
        int ea = ResolveEa(mri, memory);
        var eaBytes = (ulong)(ea * 2);

        switch (mri.Code) {
            case MriCode.And: {
                int val = (int)memory.Read(eaBytes, 2) & 0xFFF;
                int newAc = ac & val;
                return new ExecuteResult {
                    RegisterResult = ((ulong)newAc, true),
                    SideEffect = s => s.IntegerRegisters.Write(0, (ulong)newAc),
                };
            }
            case MriCode.Tad: {
                int val = (int)memory.Read(eaBytes, 2) & 0xFFF;
                int sum = ac + val;
                int newAc = sum & 0xFFF;
                int newL = l ^ ((sum >> 12) & 1);
                return new ExecuteResult {
                    RegisterResult = ((ulong)newAc, true),
                    SideEffect = s => {
                        s.IntegerRegisters.Write(0, (ulong)newAc);
                        s.IntegerRegisters.Write(1, (ulong)newL);
                    },
                };
            }
            case MriCode.Isz: {
                int val = ((int)memory.Read(eaBytes, 2) + 1) & 0xFFF;
                memory.Write(eaBytes, (ulong)val, 2);
                return val == 0
                    ? ExecuteResult.WithBranch(true, pc + 4)
                    : ExecuteResult.Clean;
            }
            case MriCode.Dca: {
                memory.Write(eaBytes, (ulong)ac, 2);
                return new ExecuteResult {
                    RegisterResult = (0, true),
                    SideEffect = s => s.IntegerRegisters.Write(0, 0),
                };
            }
            case MriCode.Jms: {
                // Store return-word-address at ea; jump to ea+1
                memory.Write(eaBytes, (pc + 2) / 2, 2);
                return ExecuteResult.WithBranch(true, (ulong)((ea + 1) * 2));
            }
            case MriCode.Jmp: return ExecuteResult.WithBranch(true, eaBytes);

            default: return ExecuteResult.Clean;
        }
    }

    private static ExecuteResult ExecOpr1(Opr1Op op, int ac, int l) {
        // PDP-8 OPR Group 1 microop sequence (fixed hardware order):
        // 1. CLA/CLL  2. CMA/CML  3. Rotate or BSW  4. IAC
        if (op.Cla) ac = 0;
        if (op.Cll) l = 0;
        if (op.Cma) ac ^= 0xFFF;
        if (op.Cml) l ^= 1;

        if (op.Bsw) {
            // Swap the two 6-bit halves of AC; L is unaffected
            ac = ((ac & 0x3F) << 6) | ((ac >> 6) & 0x3F);
        }
        else if (op.RotRight) {
            int reps = op.TwoStep ? 2 : 1;
            for (var i = 0; i < reps; i++) {
                int cout = ac & 1;
                ac = (l << 11) | (ac >> 1);
                l = cout;
            }
        }
        else if (op.RotLeft) {
            int reps = op.TwoStep ? 2 : 1;
            for (var i = 0; i < reps; i++) {
                int cout = (ac >> 11) & 1;
                ac = ((ac << 1) | l) & 0xFFF;
                l = cout;
            }
        }

        if (op.Iac) {
            int sum = ac + 1;
            l ^= (sum >> 12) & 1;
            ac = sum & 0xFFF;
        }

        int finalAc = ac, finalL = l;
        return new ExecuteResult {
            RegisterResult = ((ulong)finalAc, true),
            SideEffect = s => {
                s.IntegerRegisters.Write(0, (ulong)finalAc);
                s.IntegerRegisters.Write(1, (ulong)finalL);
            },
        };
    }

    private static ExecuteResult ExecOpr2(Opr2Op op, ulong pc, int ac, int l) {
        if (op.Hlt) return new ExecuteResult { IsHalt = true, };

        // Natural skip: OR of enabled conditions.
        // OrMode (RSS bit, bit3): when true, COMPLEMENTS the natural skip sense.
        //   RSS=0 (OrMode=false): skip if any condition true  [SMA, SZA, SNL natural]
        //   RSS=1 (OrMode=true):  skip if NO condition true   [SPA, SNA, SZL, SKP]
        bool natural = (op.Sma && (ac & 0x800) != 0)
                    || (op.Sza && ac == 0)
                    || (op.Snl && l != 0);
        bool skip = op.OrMode ? !natural : natural;

        // OSR (bit2): OR switch-register into AC; no panel model → no-op
        int finalAc = op.Cla ? 0 : ac;
        Action<IArchState>? se = op.Cla
            ? s => s.IntegerRegisters.Write(0, (ulong)finalAc)
            : null;

        return new ExecuteResult {
            RegisterResult = ((ulong)finalAc, op.Cla),
            BranchTaken = skip,
            BranchTarget = pc + (skip ? 4u : 2u),
            SideEffect = se,
        };
    }
}