using Mechanism;
using Orrery.Streaming;
using RiscV32.Memory;

namespace Tests.Orrery;

public class StreamingEngineTests {
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Writes 4-byte little-endian values at consecutive 4-byte slots.
    private static FlatMemory MakeMemory(params uint[] words) {
        var mem = new FlatMemory(words.Length * 4);
        for (var i = 0; i < words.Length; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes(words[i]));
        return mem;
    }

    private static StreamDescriptor UnitStride(ulong baseAddr, int elementBytes, long count) =>
        new(baseAddr, elementBytes, count, elementBytes);

    // ── Basic configure / step / consume ─────────────────────────────────────

    [Fact]
    public void FreshEngine_NoStreamActive() {
        var eng = new StreamingEngine();
        for (var i = 0; i < StreamingEngine.MaxStreams; i++) {
            Assert.False(eng.IsActive(i));
            Assert.False(eng.HasElement(i));
        }
    }

    [Fact]
    public void Configure_ActivatesStream() {
        var eng = new StreamingEngine();
        FlatMemory mem = MakeMemory(1, 2, 3);

        eng.Configure(0, UnitStride(0, 4, 3));
        Assert.True(eng.IsActive(0));
        Assert.False(eng.HasElement(0)); // Step not yet called
    }

    [Fact]
    public void Step_FillsBuffer_WithCorrectElement() {
        var eng = new StreamingEngine(prefetchDepth: 4);
        FlatMemory mem = MakeMemory(10, 20, 30);

        eng.Configure(0, UnitStride(0, 4, 3));
        eng.Step(mem);

        Assert.True(eng.HasElement(0));
        Assert.Equal(10UL, eng.Peek(0));
    }

    [Fact]
    public void Consume_ReturnsAndAdvances() {
        var eng = new StreamingEngine(prefetchDepth: 4);
        FlatMemory mem = MakeMemory(10, 20, 30);

        eng.Configure(0, UnitStride(0, 4, 3));
        eng.Step(mem); eng.Step(mem); eng.Step(mem); // prefetch all 3

        Assert.Equal(10UL, eng.Consume(0));
        Assert.Equal(20UL, eng.Consume(0));
        Assert.Equal(30UL, eng.Consume(0));
        Assert.False(eng.HasElement(0));
    }

    [Fact]
    public void Peek_DoesNotConsumeElement() {
        var eng = new StreamingEngine(prefetchDepth: 4);
        FlatMemory mem = MakeMemory(42);

        eng.Configure(0, UnitStride(0, 4, 1));
        eng.Step(mem);

        Assert.Equal(42UL, eng.Peek(0));
        Assert.Equal(42UL, eng.Peek(0)); // still there
        Assert.True(eng.HasElement(0));
    }

    // ── Prefetch depth cap ────────────────────────────────────────────────────

    [Fact]
    public void Step_StopsFillingAtPrefetchDepth() {
        var eng = new StreamingEngine(prefetchDepth: 2);
        FlatMemory mem = MakeMemory(1, 2, 3, 4, 5);

        eng.Configure(0, UnitStride(0, 4, 5));
        // Call Step more times than prefetchDepth
        for (var i = 0; i < 5; i++) eng.Step(mem);

        // Only 2 slots were prefetched; the 3rd call was a no-op
        Assert.Equal(1UL, eng.Consume(0));
        Assert.Equal(2UL, eng.Consume(0));
        Assert.False(eng.HasElement(0)); // depth cap exhausted the slot
    }

    [Fact]
    public void PrefetchDepth_RefillsAfterConsume() {
        var eng = new StreamingEngine(prefetchDepth: 2);
        FlatMemory mem = MakeMemory(1, 2, 3, 4);

        eng.Configure(0, UnitStride(0, 4, 4));
        eng.Step(mem); eng.Step(mem); // fill to depth

        eng.Consume(0); // open one slot
        eng.Step(mem);  // fills the slot with element 3

        Assert.Equal(2UL, eng.Consume(0));
        Assert.Equal(3UL, eng.Consume(0));
    }

    // ── Exhaustion ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsExhausted_TrueAfterAllConsumed() {
        var eng = new StreamingEngine(prefetchDepth: 4);
        FlatMemory mem = MakeMemory(1, 2);

        eng.Configure(0, UnitStride(0, 4, 2));
        eng.Step(mem); eng.Step(mem);

        eng.Consume(0);
        Assert.False(eng.IsExhausted(0));
        eng.Consume(0);
        Assert.True(eng.IsExhausted(0));
    }

    [Fact]
    public void FetchStops_AtCountBoundary() {
        var eng = new StreamingEngine(prefetchDepth: 8);
        FlatMemory mem = MakeMemory(1, 2, 3);

        eng.Configure(0, UnitStride(0, 4, 2)); // only 2 elements despite 3 in memory
        // Many steps beyond count
        for (var i = 0; i < 10; i++) eng.Step(mem);

        Assert.Equal(1UL, eng.Consume(0));
        Assert.Equal(2UL, eng.Consume(0));
        Assert.False(eng.HasElement(0));
    }

    // ── Stride variants ───────────────────────────────────────────────────────

    [Fact]
    public void CustomStride_SkipsElements() {
        // Memory: [10, 20, 30, 40] at bytes 0,4,8,12; stride=8 reads indices 0 and 2
        var mem = new FlatMemory(64);
        mem.Load(0, BitConverter.GetBytes(10u));
        mem.Load(4, BitConverter.GetBytes(20u));
        mem.Load(8, BitConverter.GetBytes(30u));
        mem.Load(12, BitConverter.GetBytes(40u));

        var eng = new StreamingEngine(prefetchDepth: 4);
        eng.Configure(0, new StreamDescriptor(0, 4, 2, 8)); // stride=8
        eng.Step(mem); eng.Step(mem);

        Assert.Equal(10UL, eng.Consume(0));
        Assert.Equal(30UL, eng.Consume(0));
    }

    [Fact]
    public void NegativeStride_WalksBackward() {
        // Place values at addresses 12, 8, 4, 0 and walk backward from 12.
        var mem = new FlatMemory(64);
        mem.Load(0, BitConverter.GetBytes(1u));
        mem.Load(4, BitConverter.GetBytes(2u));
        mem.Load(8, BitConverter.GetBytes(3u));
        mem.Load(12, BitConverter.GetBytes(4u));

        var eng = new StreamingEngine(prefetchDepth: 4);
        eng.Configure(0, new StreamDescriptor(12, 4, 3, -4)); // start=12, stride=-4
        eng.Step(mem); eng.Step(mem); eng.Step(mem);

        Assert.Equal(4UL, eng.Consume(0)); // addr 12
        Assert.Equal(3UL, eng.Consume(0)); // addr 8
        Assert.Equal(2UL, eng.Consume(0)); // addr 4
    }

    // ── Byte-width variants ───────────────────────────────────────────────────

    [Fact]
    public void ByteElement_ReadsOneByte() {
        var mem = new FlatMemory(16);
        mem.Load(0, [0xAB, 0xCD, 0xEF,]);

        var eng = new StreamingEngine(prefetchDepth: 4);
        eng.Configure(0, new StreamDescriptor(0, 1, 3, 1));
        eng.Step(mem); eng.Step(mem); eng.Step(mem);

        Assert.Equal(0xABUL, eng.Consume(0));
        Assert.Equal(0xCDUL, eng.Consume(0));
        Assert.Equal(0xEFUL, eng.Consume(0));
    }

    // ── Multiple concurrent streams ────────────────────────────────────────────

    [Fact]
    public void MultipleStreams_IndependentBuffers() {
        var mem = new FlatMemory(64);
        mem.Load(0, BitConverter.GetBytes(100u));
        mem.Load(4, BitConverter.GetBytes(200u));

        var eng = new StreamingEngine(prefetchDepth: 4);
        eng.Configure(0, new StreamDescriptor(0, 4, 1, 4));
        eng.Configure(1, new StreamDescriptor(4, 4, 1, 4));

        eng.Step(mem);

        Assert.Equal(100UL, eng.Consume(0));
        Assert.Equal(200UL, eng.Consume(1));
    }

    [Fact]
    public void AllMaxStreams_Configurable() {
        var mem = new FlatMemory(StreamingEngine.MaxStreams * 4);
        for (var i = 0; i < StreamingEngine.MaxStreams; i++)
            mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));

        var eng = new StreamingEngine(prefetchDepth: 4);
        for (var i = 0; i < StreamingEngine.MaxStreams; i++)
            eng.Configure(i, new StreamDescriptor((ulong)(i * 4), 4, 1, 4));

        for (var step = 0; step < 4; step++) eng.Step(mem);

        for (var i = 0; i < StreamingEngine.MaxStreams; i++)
            Assert.Equal((ulong)(i + 1), eng.Consume(i));
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    [Fact]
    public void Deactivate_StopsPrefetchAndClearsBuffer() {
        var eng = new StreamingEngine(prefetchDepth: 4);
        FlatMemory mem = MakeMemory(1, 2, 3);

        eng.Configure(0, UnitStride(0, 4, 3));
        eng.Step(mem); eng.Step(mem); // 2 elements buffered

        eng.Deactivate(0);

        Assert.False(eng.IsActive(0));
        Assert.False(eng.HasElement(0));

        eng.Step(mem); // should be a no-op
        Assert.False(eng.HasElement(0));
    }

    [Fact]
    public void Deactivate_ThenReconfigure_StartsClean() {
        var eng = new StreamingEngine(prefetchDepth: 4);
        FlatMemory mem = MakeMemory(10, 20);

        eng.Configure(0, UnitStride(0, 4, 2));
        eng.Step(mem); eng.Consume(0); // consume first element
        eng.Deactivate(0);

        // Re-configure from start
        eng.Configure(0, UnitStride(0, 4, 2));
        eng.Step(mem);

        Assert.Equal(10UL, eng.Consume(0)); // back to the first element
    }

    // ── Argument validation ───────────────────────────────────────────────────

    [Fact]
    public void InvalidStreamId_Throws() {
        var eng = new StreamingEngine();
        Assert.Throws<ArgumentOutOfRangeException>(() => eng.IsActive(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => eng.IsActive(StreamingEngine.MaxStreams));
    }

    [Fact]
    public void Peek_EmptyBuffer_Throws() {
        var eng = new StreamingEngine();
        eng.Configure(0, UnitStride(0, 4, 3));
        Assert.Throws<InvalidOperationException>(() => eng.Peek(0));
    }

    [Fact]
    public void Consume_EmptyBuffer_Throws() {
        var eng = new StreamingEngine();
        eng.Configure(0, UnitStride(0, 4, 3));
        Assert.Throws<InvalidOperationException>(() => eng.Consume(0));
    }
}
