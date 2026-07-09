using Chip8;
using Chip8.Memory;
using Chip8.Trains;

namespace Tests.Isa.Chip8;

public class Chip8Tests {
    private static (Chip8Train train, Chip8Memory mem) Make() {
        var mem = new Chip8Memory();
        var train = new Chip8Train(new Chip8Mechanism(), mem);
        return (train, mem);
    }

    // Load big-endian 16-bit instructions at 0x200 (CHIP-8 program area)
    private static void Load(Chip8Memory mem, params ushort[] words) {
        var bytes = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 2 + 0] = (byte)(words[i] >> 8);
            bytes[i * 2 + 1] = (byte)(words[i] & 0xFF);
        }

        mem.Load(0x200, bytes);
    }

    private static byte Reg(Chip8Train t, int r) => (byte)t.ArchState.IntegerRegisters.Read(r);
    private static Chip8ArchState State(Chip8Train t) => (Chip8ArchState)t.ArchState;
    private static bool Pixel(Chip8Train t, int x, int y) => State(t).Display[y * 64 + x];

    // ── SetImm ────────────────────────────────────────────────────────────────

    [Fact]
    public void SetImm_WritesRegister() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6002, // SET V0, 2
            0x1202  // HALT
        );
        train.Run();
        Assert.Equal(2, Reg(train, 0));
    }

    // ── AddImm ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddImm_AddsToRegister() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6005, // SET V0, 5
            0x7002, // ADD_IMM V0, 2
            0x1204  // HALT
        );
        train.Run();
        Assert.Equal(7, Reg(train, 0));
    }

    [Fact]
    public void AddImm_WrapsAt256_DoesNotAffectVF() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x60FF, // SET V0, 0xFF
            0x7001, // ADD_IMM V0, 1  → 0x00 (no carry to VF)
            0x1204  // HALT
        );
        train.Run();
        Assert.Equal(0, Reg(train, 0));
        Assert.Equal(0, Reg(train, 0xF)); // VF unaffected by AddImm
    }

    // ── Set ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Set_CopiesRegister() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x600A, // SET V0, 10
            0x8100, // SET V1, V0
            0x1204  // HALT
        );
        train.Run();
        Assert.Equal(10, Reg(train, 1));
    }

    // ── Add (with carry) ──────────────────────────────────────────────────────

    [Fact]
    public void Add_ProducesCarry() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x60FF, // SET V0, 0xFF
            0x6101, // SET V1, 1
            0x8014, // ADD V0, V1  (V0 = 0, VF = 1)
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(0, Reg(train, 0));
        Assert.Equal(1, Reg(train, 0xF));
    }

    [Fact]
    public void Add_NoCarry_ClearsVF() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6001, // SET V0, 1
            0x6102, // SET V1, 2
            0x8014, // ADD V0, V1  (V0 = 3, VF = 0)
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(3, Reg(train, 0));
        Assert.Equal(0, Reg(train, 0xF));
    }

    // ── Sub ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Sub_NoBorrow_SetsVF() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x600A, // SET V0, 10
            0x6103, // SET V1, 3
            0x8015, // SUB V0, V1  (V0 = 7, VF = 1)
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(7, Reg(train, 0));
        Assert.Equal(1, Reg(train, 0xF));
    }

    [Fact]
    public void Sub_Borrow_ClearsVF() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6003, // SET V0, 3
            0x610A, // SET V1, 10
            0x8015, // SUB V0, V1  (V0 = 0xF9, VF = 0)
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(0xF9, Reg(train, 0));
        Assert.Equal(0, Reg(train, 0xF));
    }

    // ── ShiftRight1 ───────────────────────────────────────────────────────────

    [Fact]
    public void ShiftRight1_ShiftsAndCapturesLsb() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x60AB, // SET V0, 0xAB = 0b10101011  (LSB = 1)
            0x8006, // SHR V0  → V0 = 0x55, VF = 1
            0x1204  // HALT
        );
        train.Run();
        Assert.Equal(0x55, Reg(train, 0));
        Assert.Equal(1, Reg(train, 0xF));
    }

    // ── Goto ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Goto_JumpsToAddress() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x1204, // GOTO 0x204
            0x6001, // SET V0, 1  ← skipped
            0x6002, // SET V0, 2
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(2, Reg(train, 0));
    }

    // ── CallSub + Return ──────────────────────────────────────────────────────

    [Fact]
    public void CallSub_And_Return_WorkCorrectly() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x2208, // CALL 0x208  (push 0x202, jump to 0x208)
            0x6001, // SET V0, 1   ← return address lands here
            0x1204, // HALT
            0x0000, // padding (never reached)
            0x6102, // SET V1, 2   ← subroutine at 0x208
            0x00EE  // RET         (pop → 0x202)
        );
        train.Run();
        Assert.Equal(1, Reg(train, 0));
        Assert.Equal(2, Reg(train, 1));
    }

    // ── SkipEqImm ────────────────────────────────────────────────────────────

    [Fact]
    public void SkipEqImm_Taken_SkipsNextInstruction() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6005, // SET V0, 5
            0x3005, // SE V0, 5   → skip next (V0 == 5)
            0x6001, // SET V0, 1  ← skipped
            0x600A, // SET V0, 10
            0x1208  // HALT
        );
        train.Run();
        Assert.Equal(10, Reg(train, 0));
    }

    [Fact]
    public void SkipEqImm_NotTaken_ContinuesSequentially() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6005, // SET V0, 5
            0x3006, // SE V0, 6   → not taken (V0 != 6)
            0x6001, // SET V0, 1
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(1, Reg(train, 0));
    }

    // ── SkipNeqImm ───────────────────────────────────────────────────────────

    [Fact]
    public void SkipNeqImm_Taken_SkipsNextInstruction() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6005, // SET V0, 5
            0x4006, // SNE V0, 6  → skip next (V0 != 6)
            0x6001, // SET V0, 1  ← skipped
            0x600A, // SET V0, 10
            0x1208  // HALT
        );
        train.Run();
        Assert.Equal(10, Reg(train, 0));
    }

    // ── Bitwise ops ───────────────────────────────────────────────────────────

    [Fact]
    public void BitOr_ProducesCorrectResult() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x600F, // SET V0, 0x0F
            0x61F0, // SET V1, 0xF0
            0x8011, // OR V0, V1  → 0xFF
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(0xFF, Reg(train, 0));
    }

    [Fact]
    public void BitAnd_ProducesCorrectResult() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x60FF, // SET V0, 0xFF
            0x610F, // SET V1, 0x0F
            0x8012, // AND V0, V1  → 0x0F
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(0x0F, Reg(train, 0));
    }

    [Fact]
    public void BitXor_ProducesCorrectResult() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x60FF, // SET V0, 0xFF
            0x61F0, // SET V1, 0xF0
            0x8013, // XOR V0, V1  → 0x0F
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(0x0F, Reg(train, 0));
    }

    // ── SetIImm + AddToI ──────────────────────────────────────────────────────

    [Fact]
    public void SetIImm_And_AddToI_UpdateIRegister() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0xA300, // LD I, 0x300
            0x6005, // SET V0, 5
            0xF01E, // ADD I, V0  (I = 0x305)
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(0x305, State(train).I);
    }

    // ── Timers ────────────────────────────────────────────────────────────────

    [Fact]
    public void SetAndGetDelayTimer_RoundTrips() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6105, // SET V1, 5
            0xF115, // LD DT, V1  (delay = 5)
            0xF007, // LD V0, DT  (V0 = 5)
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(5, Reg(train, 0));
    }

    // ── BCD ───────────────────────────────────────────────────────────────────

    [Fact]
    public void BCD_StoresThreeDigitsAtI() {
        // 0x96 = 150 → hundreds=1, tens=5, ones=0
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6096, // SET V0, 0x96 = 150
            0xA300, // LD I, 0x300
            0xF033, // LD B, V0
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(1UL, mem.Read(0x300, 1));
        Assert.Equal(5UL, mem.Read(0x301, 1));
        Assert.Equal(0UL, mem.Read(0x302, 1));
    }

    // ── Draw ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Draw_SetsPixels() {
        (Chip8Train train, Chip8Memory mem) = Make();
        mem.Write(0x300, 0b11110000, 1); // top row of sprite: first 4 pixels on
        Load(
            mem,
            0xA300, // LD I, 0x300
            0x6000, // SET V0, 0  (x)
            0x6100, // SET V1, 0  (y)
            0xD011, // DRAW V0,V1,1
            0x1208  // HALT
        );
        train.Run();
        Assert.True(Pixel(train, 0, 0));
        Assert.True(Pixel(train, 1, 0));
        Assert.True(Pixel(train, 2, 0));
        Assert.True(Pixel(train, 3, 0));
        Assert.False(Pixel(train, 4, 0));
    }

    [Fact]
    public void Draw_NoCollision_ClearsVF() {
        (Chip8Train train, Chip8Memory mem) = Make();
        mem.Write(0x300, 0xFF, 1);
        Load(
            mem,
            0xA300, // LD I, 0x300
            0x6000, // SET V0, 0
            0x6100, // SET V1, 0
            0xD011, // DRAW V0,V1,1  (first draw, no collision)
            0x1208  // HALT
        );
        train.Run();
        Assert.Equal(0, Reg(train, 0xF));
    }

    [Fact]
    public void Draw_Collision_SetsVF() {
        (Chip8Train train, Chip8Memory mem) = Make();
        mem.Write(0x300, 0xFF, 1);
        Load(
            mem,
            0xA300, // LD I, 0x300
            0x6000, // SET V0, 0
            0x6100, // SET V1, 0
            0xD011, // DRAW V0,V1,1  (first draw)
            0xD011, // DRAW V0,V1,1  (same location → collision)
            0x120A  // HALT
        );
        train.Run();
        Assert.Equal(1, Reg(train, 0xF));
    }

    [Fact]
    public void Draw_XorTogglesPixels() {
        (Chip8Train train, Chip8Memory mem) = Make();
        mem.Write(0x300, 0xFF, 1);
        Load(
            mem,
            0xA300, // LD I, 0x300
            0x6000, // SET V0, 0
            0x6100, // SET V1, 0
            0xD011, // DRAW V0,V1,1  (on)
            0xD011, // DRAW V0,V1,1  (off again)
            0x120A  // HALT
        );
        train.Run();
        Assert.False(Pixel(train, 0, 0)); // XOR erased the pixel
    }

    // ── ClearDisplay ─────────────────────────────────────────────────────────

    [Fact]
    public void ClearDisplay_ErasesPixels() {
        (Chip8Train train, Chip8Memory mem) = Make();
        mem.Write(0x300, 0xFF, 1);
        Load(
            mem,
            0xA300, // LD I, 0x300
            0x6000, // SET V0, 0
            0x6100, // SET V1, 0
            0xD011, // DRAW V0,V1,1  (turn pixels on)
            0x00E0, // CLS
            0x120A  // HALT
        );
        train.Run();
        Assert.False(Pixel(train, 0, 0));
        Assert.False(Pixel(train, 7, 0));
    }

    // ── SkipKeyPressed / SkipKeyNotPressed ────────────────────────────────────

    [Fact]
    public void SkipKeyPressed_Taken_WhenKeyIsDown() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6105, // SET V1, 5
            0xE19E, // SKP V1 (skip if key 5 pressed)
            0x6001, // SET V0, 1  ← skipped
            0x600A, // SET V0, 10
            0x1208  // HALT
        );
        State(train).Keys[5] = true;
        train.Run();
        Assert.Equal(10, Reg(train, 0));
    }

    [Fact]
    public void SkipKeyPressed_NotTaken_WhenKeyIsUp() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6105, // SET V1, 5
            0xE19E, // SKP V1 (key 5 not pressed → don't skip)
            0x6001, // SET V0, 1  ← reached when not taken
            0x1206  // HALT
        );
        train.Run();
        Assert.Equal(1, Reg(train, 0));
    }

    [Fact]
    public void SkipKeyNotPressed_Taken_WhenKeyIsUp() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6105, // SET V1, 5
            0xE1A1, // SKNP V1 (skip if key 5 not pressed)
            0x6001, // SET V0, 1  ← skipped
            0x600A, // SET V0, 10
            0x1208  // HALT
        );
        train.Run();
        Assert.Equal(10, Reg(train, 0));
    }

    [Fact]
    public void SkipKeyNotPressed_NotTaken_WhenKeyIsDown() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6105, // SET V1, 5
            0xE1A1, // SKNP V1 (key 5 is pressed → don't skip)
            0x6001, // SET V0, 1  ← reached when not taken
            0x1206  // HALT
        );
        State(train).Keys[5] = true;
        train.Run();
        Assert.Equal(1, Reg(train, 0));
    }

    // ── GetKey ───────────────────────────────────────────────────────────────

    [Fact]
    public void GetKey_ReturnsIndexOfPressedKey() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0xF00A, // GET_KEY V0 (wait for any key)
            0x1202  // HALT
        );
        State(train).Keys[7] = true; // pre-press key 7
        train.Run();
        Assert.Equal(7, Reg(train, 0));
    }

    // ── RegDump / RegLoad ─────────────────────────────────────────────────────

    [Fact]
    public void RegDump_Then_RegLoad_RestoresRegisters() {
        (Chip8Train train, Chip8Memory mem) = Make();
        Load(
            mem,
            0x6001, // SET V0, 1
            0x6102, // SET V1, 2
            0x6203, // SET V2, 3
            0xA300, // LD I, 0x300
            0xF255, // LD [I], V2  (dump V0–V2)
            0x6000, // SET V0, 0
            0x6100, // SET V1, 0
            0x6200, // SET V2, 0
            0xA300, // LD I, 0x300
            0xF265, // LD V2, [I]  (reload V0–V2)
            0x1214  // HALT at 0x214
        );
        train.Run();
        Assert.Equal(1, Reg(train, 0));
        Assert.Equal(2, Reg(train, 1));
        Assert.Equal(3, Reg(train, 2));
    }
}