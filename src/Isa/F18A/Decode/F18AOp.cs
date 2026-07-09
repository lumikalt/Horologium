namespace F18A.Decode;

public static class F18AOp {
    public const byte Return = 0b00000; // ;
    public const byte Ex = 0b00001;
    public const byte Jump = 0b00010;
    public const byte Call = 0b00011;
    public const byte Unext = 0b00100;
    public const byte Next = 0b00101;
    public const byte If = 0b00110;
    public const byte MinusIf = 0x07; // -if: branch if T ≥ 0 (sign bit clear)
    public const byte FetchP = 0x08;  // @p
    public const byte FetchAp = 0x09; // @+
    public const byte FetchB = 0x0A;  // @b
    public const byte FetchA = 0x0B;  // @
    public const byte StoreP = 0x0C;  // !p
    public const byte StoreAp = 0x0D; // !+
    public const byte StoreB = 0x0E;  // !b
    public const byte StoreA = 0x0F;  // !
    public const byte MulStep = 0x10; // +*: one step of the shift-and-add multiply
    public const byte Shift2L = 0x11; // 2*
    public const byte Shift2R = 0x12; // 2/
    public const byte Not = 0x13;     // - (bitwise complement)
    public const byte Add = 0x14;     // +
    public const byte And = 0x15;
    public const byte Xor = 0x16;
    public const byte Drop = 0x17;
    public const byte Dup = 0x18;
    public const byte Pop = 0x19; // r> : R → data stack
    public const byte Over = 0x1A;
    public const byte APush = 0x1B;  // a
    public const byte Nop = 0x1C;    // .
    public const byte Push = 0x1D;   // >r : T → return stack
    public const byte BStore = 0x1E; // b!
    public const byte AStore = 0x1F; // a!
}