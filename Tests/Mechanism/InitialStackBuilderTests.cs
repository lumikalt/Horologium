#region

using System.Text;
using Mechanism;
using RiscV32.Memory;

#endregion

namespace Tests.Mechanism;

/// <summary>
///     Unit tests for <see cref="InitialStackBuilder" />'s psABI stack layout, in isolation from
///     any consuming ELF — this is the authority on layout correctness; the end-to-end
///     <c>Tests/RiscV64/System/InitialStackTests.cs</c> proves SP-wiring, not the layout itself
///     (see that class's doc comment for why both are needed).
/// </summary>
public class InitialStackBuilderTests {
    private const ulong StackTop = 0x8010_0000UL;

    private static string ReadCString(FlatMemory memory, ulong address) {
        var bytes = new List<byte>();
        ulong a = address;
        while (true) {
            var b = (byte)memory.Read(a, 1);
            if (b == 0) break;
            bytes.Add(b);
            a++;
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Layout_MatchesPsAbiSpec_ForBothWordSizes(int wordSize) {
        var memory = new FlatMemory(0x10000, InitialStackBuilderTests.StackTop - 0x10000);
        string[] argv = ["a.out", "hello",];
        string[] envp = ["FOO=bar",];
        (ulong Type, ulong Value)[] auxv =
            [(InitialStackBuilder.AtPagesz, 4096), (InitialStackBuilder.AtEntry, 0x1000),];

        ulong sp = InitialStackBuilder.BuildInitialStack(
            memory, InitialStackBuilderTests.StackTop, wordSize, argv, envp, auxv
        );

        Assert.Equal(0UL, sp % 16);

        ulong cursor = sp;
        Assert.Equal((ulong)argv.Length, memory.Read(cursor, wordSize));
        cursor += (ulong)wordSize;

        foreach (string expected in argv) {
            ulong ptr = memory.Read(cursor, wordSize);
            Assert.Equal(expected, ReadCString(memory, ptr));
            cursor += (ulong)wordSize;
        }

        Assert.Equal(0UL, memory.Read(cursor, wordSize)); // argv[] NULL terminator
        cursor += (ulong)wordSize;

        foreach (string expected in envp) {
            ulong ptr = memory.Read(cursor, wordSize);
            Assert.Equal(expected, ReadCString(memory, ptr));
            cursor += (ulong)wordSize;
        }

        Assert.Equal(0UL, memory.Read(cursor, wordSize)); // envp[] NULL terminator
        cursor += (ulong)wordSize;

        foreach ((ulong type, ulong value) in auxv) {
            Assert.Equal(type, memory.Read(cursor, wordSize));
            cursor += (ulong)wordSize;
            Assert.Equal(value, memory.Read(cursor, wordSize));
            cursor += (ulong)wordSize;
        }

        // AT_RANDOM: appended automatically, points at 16 non-zero bytes.
        Assert.Equal(InitialStackBuilder.AtRandom, memory.Read(cursor, wordSize));
        cursor += (ulong)wordSize;
        ulong randomAddr = memory.Read(cursor, wordSize);
        cursor += (ulong)wordSize;
        var randomBytes = new byte[16];
        for (var i = 0; i < 16; i++) randomBytes[i] = (byte)memory.Read(randomAddr + (ulong)i, 1);
        Assert.Contains(randomBytes, b => b != 0);

        // AT_NULL terminator.
        Assert.Equal(0UL, memory.Read(cursor, wordSize));
        cursor += (ulong)wordSize;
        Assert.Equal(0UL, memory.Read(cursor, wordSize));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void EmptyArgvEnvpAuxv_StillProducesAlignedSpAndTerminators(int wordSize) {
        var memory = new FlatMemory(0x10000, InitialStackBuilderTests.StackTop - 0x10000);

        ulong sp = InitialStackBuilder.BuildInitialStack(
            memory, InitialStackBuilderTests.StackTop, wordSize, [], [], []
        );

        Assert.Equal(0UL, sp % 16);
        Assert.Equal(0UL, memory.Read(sp, wordSize));                         // argc == 0
        Assert.Equal(0UL, memory.Read(sp + (ulong)wordSize, wordSize));       // argv[] NULL only
        Assert.Equal(0UL, memory.Read(sp + (ulong)(2 * wordSize), wordSize)); // envp[] NULL only

        // AT_RANDOM pair then AT_NULL pair.
        ulong auxvStart = sp + (ulong)(3 * wordSize);
        Assert.Equal(InitialStackBuilder.AtRandom, memory.Read(auxvStart, wordSize));
        Assert.Equal(0UL, memory.Read(auxvStart + (ulong)(2 * wordSize), wordSize));
    }
}