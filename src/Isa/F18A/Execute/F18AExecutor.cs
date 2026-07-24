#region

using F18A.Decode;
using Mechanism;

#endregion

namespace F18A.Execute;

public sealed class F18AExecutor : IExecutor {
    private const uint Mask18 = 0x3FFFFu;

    public ExecuteResult Execute(ITooth tooth, IArchState archState, IMemory memory) {
        var insn = (F18AInstruction)tooth;

        // Work on a local copy; SideEffect applies it back at commit
        var work = (F18AArchState)archState.Snapshot();

        var branchTaken = false;
        ulong branchTarget = 0;
        const bool halted = false;
        var pOffset = 0; // words consumed from instruction stream by @p+ / !p+

        byte[] slots = [insn.Slot0, insn.Slot1, insn.Slot2, insn.Slot3,];
        uint[] addrs = [insn.AddrAfterSlot0, insn.AddrAfterSlot1, insn.AddrAfterSlot2, 0,];

        for (var i = 0; i < 4 && !branchTaken; i++)
            RunSlot(
                slots[i], addrs[i], tooth.Pc, work, memory,
                ref pOffset, ref branchTaken, ref branchTarget
            );

        // @p+ / !p+ advanced P past literals: return explicit sequential target
        if (!branchTaken && pOffset > 0) {
            branchTaken = true;
            branchTarget = tooth.Pc + 4 + (ulong)(pOffset * 4);
        }

        return new ExecuteResult {
            BranchTaken = branchTaken,
            BranchTarget = branchTaken ? branchTarget : null,
            IsHalt = halted,
            SideEffect = s => ((F18AArchState)s).CopyFrom(work),
        };
    }

    private static void RunSlot(
        byte op,
        uint addr,
        ulong pc,
        F18AArchState w,
        IMemory memory,
        ref int pOffset,
        ref bool branchTaken,
        ref ulong branchTarget
    ) {
        switch (op) {
            case F18AOp.Return:
                branchTaken = true;
                branchTarget = w.RPop() * 4ul;
                break;

            case F18AOp.Ex: {
                uint t = w.T;
                uint r = w.R;
                w.T = r;
                w.R = t;
                break;
            }

            case F18AOp.Jump:
                branchTaken = true;
                branchTarget = addr * 4ul;
                break;

            case F18AOp.Call:
                // Push address of the next instruction word (pc/4 + 1)
                w.RPush((uint)(pc / 4 + 1));
                branchTaken = true;
                branchTarget = addr * 4ul;
                break;

            case F18AOp.Unext:
                // Always terminates the instruction word (no subsequent slots execute).
                branchTaken = true;
                if (w.R > 0) {
                    w.R--;
                    branchTarget = pc; // loop back to current word
                }
                else {
                    w.RPop();
                    branchTarget = pc + 4; // sequential: advance to next word
                }

                break;

            case F18AOp.Next:
                // Always terminates the instruction word.
                branchTaken = true;
                if (w.R > 0) {
                    w.R--;
                    branchTarget = addr * 4ul;
                }
                else {
                    w.RPop();
                    branchTarget = pc + 4; // sequential
                }

                break;

            case F18AOp.If:
                // Always terminates the instruction word.
                // Canonical F18A: branch when T == 0 (false), fall through when T != 0 (true).
                branchTaken = true;
                branchTarget = w.T == 0 ? addr * 4ul : pc + 4;
                break;

            case F18AOp.MinusIf:
                // Always terminates the instruction word.
                // Branch when T ≥ 0 (sign bit 17 is clear); fall through when T < 0.
                branchTaken = true;
                branchTarget = (int)(w.T << 14) >= 0 ? addr * 4ul : pc + 4;
                break;

            case F18AOp.FetchP: {
                ulong litAddr = pc + 4 + (ulong)(pOffset * 4);
                uint val = (uint)memory.Read(litAddr, 4) & F18AExecutor.Mask18;
                w.DPush(val);
                pOffset++;
                break;
            }

            case F18AOp.FetchAp: {
                uint val = (uint)memory.Read(w.A * 4ul, 4) & F18AExecutor.Mask18;
                w.DPush(val);
                w.A = (w.A + 1) & F18AExecutor.Mask18;
                break;
            }

            case F18AOp.FetchB: {
                uint val = (uint)memory.Read(w.B * 4ul, 4) & F18AExecutor.Mask18;
                w.DPush(val);
                break;
            }

            case F18AOp.FetchA: {
                uint val = (uint)memory.Read(w.A * 4ul, 4) & F18AExecutor.Mask18;
                w.DPush(val);
                break;
            }

            case F18AOp.StoreP: {
                ulong litAddr = pc + 4 + (ulong)(pOffset * 4);
                memory.Write(litAddr, w.DPop(), 4);
                pOffset++;
                break;
            }

            case F18AOp.StoreAp: {
                memory.Write(w.A * 4ul, w.DPop(), 4);
                w.A = (w.A + 1) & F18AExecutor.Mask18;
                break;
            }

            case F18AOp.StoreB: memory.Write(w.B * 4ul, w.DPop(), 4); break;

            case F18AOp.StoreA: memory.Write(w.A * 4ul, w.DPop(), 4); break;

            case F18AOp.MulStep: {
                // One step of the F18A shift-and-add multiply.
                // If A[0] set, add S to T; then arithmetic right-shift T:A as a 36-bit pair.
                int t = (int)(w.T << 14) >> 14;                  // sign-extend T from 18 bits
                if ((w.A & 1) != 0) t += (int)(w.S << 14) >> 14; // sign-extend S and accumulate
                uint tNew = (uint)t & F18AExecutor.Mask18;
                w.A = (w.A >> 1) | ((tNew & 1) << 17);                         // T[0] into A[17]
                w.T = ((tNew >> 1) | (tNew & 0x20000u)) & F18AExecutor.Mask18; // arith right shift
                break;
            }

            case F18AOp.Shift2L: w.T = (w.T << 1) & F18AExecutor.Mask18; break;

            case F18AOp.Shift2R: {
                // Arithmetic right shift on 18-bit value: sign bit is bit 17
                int signed = (int)(w.T << 14) >> 14; // sign-extend from bit 17
                w.T = (uint)(signed >> 1) & F18AExecutor.Mask18;
                break;
            }

            case F18AOp.Not: w.T = ~w.T & F18AExecutor.Mask18; break;

            case F18AOp.Add: {
                uint n = w.DPop();
                w.T = (w.T + n) & F18AExecutor.Mask18;
                break;
            }

            case F18AOp.And: {
                uint n = w.DPop();
                w.T = w.T & n;
                break;
            }

            case F18AOp.Xor: {
                uint n = w.DPop();
                w.T = w.T ^ n;
                break;
            }

            case F18AOp.Drop: w.DPop(); break;

            case F18AOp.Dup: w.DPush(w.T); break;

            case F18AOp.Pop: w.DPush(w.RPop()); break;

            case F18AOp.Over: w.DPush(w.S); break;

            case F18AOp.APush: w.DPush(w.A); break;

            case F18AOp.Nop: break;

            case F18AOp.Push: w.RPush(w.DPop()); break;

            case F18AOp.BStore: w.B = w.DPop(); break;

            case F18AOp.AStore: w.A = w.DPop(); break;
        }
    }
}