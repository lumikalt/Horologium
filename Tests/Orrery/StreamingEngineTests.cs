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
        MakeMemory(1, 2, 3);

        eng.Configure(0, UnitStride(0, 4, 3));
        Assert.True(eng.IsActive(0));
        Assert.False(eng.HasElement(0)); // Step isn't yet called
    }

    [Fact]
    public void Step_FillsBuffer_WithCorrectElement() {
        var eng = new StreamingEngine();
        FlatMemory mem = MakeMemory(10, 20, 30);

        eng.Configure(0, UnitStride(0, 4, 3));
        eng.Step(mem);

        Assert.True(eng.HasElement(0));
        Assert.Equal(10UL, eng.Peek(0));
    }

    [Fact]
    public void Consume_ReturnsAndAdvances() {
        var eng = new StreamingEngine();
        FlatMemory mem = MakeMemory(10, 20, 30);

        eng.Configure(0, UnitStride(0, 4, 3));
        eng.Step(mem);
        eng.Step(mem);
        eng.Step(mem); // prefetch all 3

        Assert.Equal(10UL, eng.Consume(0));
        Assert.Equal(20UL, eng.Consume(0));
        Assert.Equal(30UL, eng.Consume(0));
        Assert.False(eng.HasElement(0));
    }

    [Fact]
    public void Peek_DoesNotConsumeElement() {
        var eng = new StreamingEngine();
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
        var eng = new StreamingEngine(2);
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
        var eng = new StreamingEngine(2);
        FlatMemory mem = MakeMemory(1, 2, 3, 4);

        eng.Configure(0, UnitStride(0, 4, 4));
        eng.Step(mem);
        eng.Step(mem); // fill to depth

        eng.Consume(0); // open one slot
        eng.Step(mem);  // fills the slot with element 3

        Assert.Equal(2UL, eng.Consume(0));
        Assert.Equal(3UL, eng.Consume(0));
    }

    // ── Exhaustion ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsExhausted_TrueAfterAllConsumed() {
        var eng = new StreamingEngine();
        FlatMemory mem = MakeMemory(1, 2);

        eng.Configure(0, UnitStride(0, 4, 2));
        eng.Step(mem);
        eng.Step(mem);

        eng.Consume(0);
        Assert.False(eng.IsExhausted(0));
        eng.Consume(0);
        Assert.True(eng.IsExhausted(0));
    }

    [Fact]
    public void FetchStops_AtCountBoundary() {
        var eng = new StreamingEngine(8);
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

        var eng = new StreamingEngine();
        eng.Configure(0, new StreamDescriptor(0, 4, 2, 8)); // stride=8
        eng.Step(mem);
        eng.Step(mem);

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

        var eng = new StreamingEngine();
        eng.Configure(0, new StreamDescriptor(12, 4, 3, -4)); // start=12, stride=-4
        eng.Step(mem);
        eng.Step(mem);
        eng.Step(mem);

        Assert.Equal(4UL, eng.Consume(0)); // addr 12
        Assert.Equal(3UL, eng.Consume(0)); // addr 8
        Assert.Equal(2UL, eng.Consume(0)); // addr 4
    }

    // ── Byte-width variants ───────────────────────────────────────────────────

    [Fact]
    public void ByteElement_ReadsOneByte() {
        var mem = new FlatMemory(16);
        mem.Load(0, [0xAB, 0xCD, 0xEF,]);

        var eng = new StreamingEngine();
        eng.Configure(0, new StreamDescriptor(0, 1, 3, 1));
        eng.Step(mem);
        eng.Step(mem);
        eng.Step(mem);

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

        var eng = new StreamingEngine();
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

        var eng = new StreamingEngine();
        for (var i = 0; i < StreamingEngine.MaxStreams; i++)
            eng.Configure(i, new StreamDescriptor((ulong)(i * 4), 4, 1, 4));

        for (var step = 0; step < 4; step++) eng.Step(mem);

        for (var i = 0; i < StreamingEngine.MaxStreams; i++) Assert.Equal((ulong)(i + 1), eng.Consume(i));
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    [Fact]
    public void Deactivate_StopsPrefetchAndClearsBuffer() {
        var eng = new StreamingEngine();
        FlatMemory mem = MakeMemory(1, 2, 3);

        eng.Configure(0, UnitStride(0, 4, 3));
        eng.Step(mem);
        eng.Step(mem); // 2 elements buffered

        eng.Deactivate(0);

        Assert.False(eng.IsActive(0));
        Assert.False(eng.HasElement(0));

        eng.Step(mem); // should be a no-op
        Assert.False(eng.HasElement(0));
    }

    [Fact]
    public void Deactivate_ThenReconfigure_StartsClean() {
        var eng = new StreamingEngine();
        FlatMemory mem = MakeMemory(10, 20);

        eng.Configure(0, UnitStride(0, 4, 2));
        eng.Step(mem);
        eng.Consume(0); // consume first element
        eng.Deactivate(0);

        // Re-configure from start
        eng.Configure(0, UnitStride(0, 4, 2));
        eng.Step(mem);

        Assert.Equal(10UL, eng.Consume(0)); // back to the first element
    }

    // ── Static dimension modifiers ────────────────────────────────────────────

    [Fact]
    public void StrideModifier_GrowsInnerStrideEachOuterIteration() {
        // 2D stream: D0 count=3 stride=4, D1 count=2.
        // Modifier on D0: Stride Inc 4 → stride grows by 4 after each D0 wrap.
        // Row 0 (stride=4): words at offsets 0,4,8   → values 1,2,3
        // Row 1 (stride=8): words at offsets 0,8,16  → values 1,3,5
        const int wordCount = 6;
        var mem = new FlatMemory(wordCount * 4);
        for (var i = 0; i < wordCount; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));

        var desc = new StreamDescriptor(
            0,
            4,
            [new StreamDimension(3, 4), new StreamDimension(2, 0),],
            [new StreamModifier(0, StreamModifierTarget.Stride, StreamModifierBehavior.Inc, 4),]
        );
        var eng = new StreamingEngine(8);
        eng.Configure(0, desc);
        for (var i = 0; i < 8; i++) eng.Step(mem);

        // Row 0
        Assert.Equal(1UL, eng.Consume(0));
        Assert.Equal(2UL, eng.Consume(0));
        Assert.Equal(3UL, eng.Consume(0));
        // Row 1 (stride now 8)
        Assert.Equal(1UL, eng.Consume(0));
        Assert.Equal(3UL, eng.Consume(0));
        Assert.Equal(5UL, eng.Consume(0));
        Assert.True(eng.IsExhausted(0));
    }

    [Fact]
    public void OffsetModifier_ShiftsBaseEachOuterIteration() {
        // 2D stream: D0 count=2 stride=4, D1 count=3, base=0x10.
        // Modifier on D0: Offset Inc 8 → base shifts by 8 after each D0 wrap.
        // Row 0 (base=0x10+0):  words[4]=5, words[5]=6
        // Row 1 (base=0x10+8):  words[6]=7, words[7]=8
        // Row 2 (base=0x10+16): words[8]=9, words[9]=10
        const int wordCount = 10;
        var mem = new FlatMemory(wordCount * 4);
        for (var i = 0; i < wordCount; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));

        var desc = new StreamDescriptor(
            0x10,
            4,
            [new StreamDimension(2, 4), new StreamDimension(3, 0),],
            [new StreamModifier(0, StreamModifierTarget.Offset, StreamModifierBehavior.Inc, 8),]
        );
        var eng = new StreamingEngine(8);
        eng.Configure(0, desc);
        for (var i = 0; i < 8; i++) eng.Step(mem);

        Assert.Equal(5UL, eng.Consume(0));
        Assert.Equal(6UL, eng.Consume(0));
        Assert.Equal(7UL, eng.Consume(0));
        Assert.Equal(8UL, eng.Consume(0));
        Assert.Equal(9UL, eng.Consume(0));
        Assert.Equal(10UL, eng.Consume(0));
        Assert.True(eng.IsExhausted(0));
    }

    [Fact]
    public void SizeModifier_CappedAtMaxApplications() {
        // 2D stream: D0 count=1 stride=4, D1 count=4 stride=16.
        // Modifier on D0: Size Inc 1, MaxApplications=2.
        // Row 0 (count=1): addr 0          → 1 element
        // D0 wraps → apply (1 of 2) → count=2
        // Row 1 (count=2): addr 16,20      → 2 elements
        // D0 wraps → apply (2 of 2) → count=3
        // Row 2 (count=3): addr 32,36,40   → 3 elements
        // D0 wraps → capped → count stays 3
        // Row 3 (count=3): addr 48,52,56   → 3 elements
        // Total: 1+2+3+3 = 9 elements
        const int words = 15;
        var mem = new FlatMemory(words * 4);
        for (var i = 0; i < words; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));

        var desc = new StreamDescriptor(
            0, 4,
            [new StreamDimension(1, 4), new StreamDimension(4, 16),],
            [new StreamModifier(0, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 1, 2),]
        );
        var eng = new StreamingEngine(16);
        eng.Configure(0, desc);
        for (var i = 0; i < 16; i++) eng.Step(mem);

        // Row 0: 1 element at offset 0
        Assert.Equal(1UL, eng.Consume(0));
        // Row 1: 2 elements at offsets 16, 20
        Assert.Equal(5UL, eng.Consume(0));
        Assert.Equal(6UL, eng.Consume(0));
        // Row 2: 3 elements at offsets 32, 36, 40
        Assert.Equal(9UL, eng.Consume(0));
        Assert.Equal(10UL, eng.Consume(0));
        Assert.Equal(11UL, eng.Consume(0));
        // Row 3: 3 elements (capped) at offsets 48, 52, 56
        Assert.Equal(13UL, eng.Consume(0));
        Assert.Equal(14UL, eng.Consume(0));
        Assert.Equal(15UL, eng.Consume(0));
        Assert.True(eng.IsExhausted(0));
    }

    [Fact]
    public void VectorMode_Configure_SetsIsVectorMode() {
        var mem = new FlatMemory(4 * 4);
        for (var i = 0; i < 4; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));
        var desc = new StreamDescriptor(0, 4, [new StreamDimension(4, 4),], IsVectorMode: true, VecCfgDim: -1);
        var eng = new StreamingEngine(8);
        eng.Configure(0, desc);
        Assert.True(eng.IsVectorMode(0));
    }

    [Fact]
    public void ScalarMode_Configure_IsNotVectorMode() {
        var desc = new StreamDescriptor(0, 4, [new StreamDimension(4, 4),]);
        var eng = new StreamingEngine(8);
        eng.Configure(0, desc);
        Assert.False(eng.IsVectorMode(0));
    }

    [Fact]
    public void VectorMode_Step_FillsVlElementsPerCall() {
        // 2D: 3 rows × 4 cols (dim0=4 elements, dim1=3 rows). VecCfgDim=0 (innermost).
        // Each Step(vl=4) should enqueue exactly 4 elements (one full row), stopping at dim0 wrap.
        var mem = new FlatMemory(3 * 4 * 4);
        for (var i = 0; i < 12; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));
        var desc = new StreamDescriptor(
            0, 4,
            [new StreamDimension(4, 4), new StreamDimension(3, 16),],
            IsVectorMode: true, VecCfgDim: 0
        );
        var eng = new StreamingEngine(16);
        eng.Configure(0, desc);

        eng.Step(mem, 4);
        Assert.Equal(4, Enumerable.Range(0, 4).Count(_ => eng.HasElement(0)));
        // First row: elements 1–4
        Assert.Equal(1u, (uint)eng.Consume(0));
        Assert.Equal(2u, (uint)eng.Consume(0));
        Assert.Equal(3u, (uint)eng.Consume(0));
        Assert.Equal(4u, (uint)eng.Consume(0));
    }

    [Fact]
    public void VectorMode_Step_StopsAtVecCfgDimBoundary() {
        // 2D: 2 rows × 4 cols. VecCfgDim=0. VL=8 > row size (4).
        // Step with vl=8 should only fill 4 elements (the whole row), not 8.
        var mem = new FlatMemory(2 * 4 * 4);
        for (var i = 0; i < 8; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));
        var desc = new StreamDescriptor(
            0, 4,
            [new StreamDimension(4, 4), new StreamDimension(2, 16),],
            IsVectorMode: true, VecCfgDim: 0
        );
        var eng = new StreamingEngine(16);
        eng.Configure(0, desc);

        eng.Step(mem, 8);
        var count = 0;
        while (eng.HasElement(0)) {
            eng.Consume(0);
            count++;
        }

        Assert.Equal(4, count);

        // Second Step fills the next row.
        eng.Step(mem, 8);
        count = 0;
        while (eng.HasElement(0)) {
            eng.Consume(0);
            count++;
        }

        Assert.Equal(4, count);
        Assert.True(eng.IsExhausted(0));
    }

    [Fact]
    public void ScalarMode_Step_FillsOneElementRegardlessOfVl() {
        var mem = new FlatMemory(4 * 4);
        for (var i = 0; i < 4; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));
        var desc = new StreamDescriptor(0, 4, [new StreamDimension(4, 4),]);
        var eng = new StreamingEngine(16);
        eng.Configure(0, desc);

        eng.Step(mem, 4);
        Assert.True(eng.HasElement(0));
        eng.Consume(0);
        Assert.False(eng.HasElement(0));
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

    // ── Indirect dimension modifiers ─────────────────────────────────────────

    [Fact]
    public void IndirectSizeModifier_Set_VariableInnerCount() {
        // Stream 1 (IndSource): 3 elements [3, 2, 4] at address 0x100.
        // Stream 0 (target): 2D stream, dim0=inner (count=1 placeholder, stride=4), dim1=outer (count=3, stride=0).
        // Indirect modifier on dim0: Size/Set driven by stream 1.
        //
        // Expected sequence:
        //   initial apply: consume IndSource[0]=3 → inner count=3
        //   row 0: 3 elements (addresses 0x000, 0x004, 0x008) → values 1, 2, 3
        //   dim0 wrap → consume IndSource[1]=2 → inner count=2
        //   row 1: 2 elements (addresses 0x000, 0x004) → values 1, 2
        //   dim0 wrap → consume IndSource[2]=4 → inner count=4
        //   row 2: 4 elements (addresses 0x000..0x00C) → values 1, 2, 3, 4
        //   dim0 wrap → IndSource empty (skip) → dim1 wraps → done
        //   total: 9 elements

        const int nWords = 16;
        var mem = new FlatMemory((int)(0x200 + nWords * 4));
        for (var i = 0; i < nWords; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));
        mem.Load(0x100, BitConverter.GetBytes(3u));
        mem.Load(0x104, BitConverter.GetBytes(2u));
        mem.Load(0x108, BitConverter.GetBytes(4u));

        var eng = new StreamingEngine(16);

        // IndSource on stream 1
        eng.Configure(1, new StreamDescriptor(0x100, 4, 3, 4));

        // Target on stream 0; dim 0 = inner, dim 1 = outer
        var desc = new StreamDescriptor(
            0x000, 4,
            [new StreamDimension(1, 4), new StreamDimension(3, 0),],
            [new StreamModifier(0, StreamModifierTarget.Size, StreamModifierBehavior.Set, 0, 0, 1),]
        );
        eng.Configure(0, desc);

        // Step enough cycles to fill everything (stream 1 must step first to prime IndSource).
        for (var i = 0; i < 20; i++) eng.Step(mem);

        // Row 0 (inner count = 3)
        Assert.Equal(1UL, eng.Consume(0));
        Assert.Equal(2UL, eng.Consume(0));
        Assert.Equal(3UL, eng.Consume(0));
        // Row 1 (inner count = 2)
        Assert.Equal(1UL, eng.Consume(0));
        Assert.Equal(2UL, eng.Consume(0));
        // Row 2 (inner count = 4)
        Assert.Equal(1UL, eng.Consume(0));
        Assert.Equal(2UL, eng.Consume(0));
        Assert.Equal(3UL, eng.Consume(0));
        Assert.Equal(4UL, eng.Consume(0));
        Assert.True(eng.IsExhausted(0));
    }

    [Fact]
    public void IndirectSizeModifier_Inc_GrowsInnerCount() {
        // Stream 1 (IndSource): 2 elements [2, 3] at address 0x200.
        // Stream 0: 2D stream, dim0=inner (count=2 initial, stride=4), dim1=outer (count=3, stride=0).
        // Indirect modifier on dim0: Size/Inc driven by stream 1.
        // Inc behavior: new_count = current_count + raw_value
        //
        // initial apply: consume 2 → inner count = 2 + 2 = 4
        // row 0: 4 elements
        // dim0 wrap → consume 3 → inner count = 4 + 3 = 7
        // row 1: 7 elements
        // dim0 wrap → IndSource empty (skip, count stays 7) → dim1 advance
        // row 2: 7 elements
        // dim0 wrap → dim1 wraps → done
        // total: 4 + 7 + 7 = 18 elements

        const int nWords = 32;
        var mem = new FlatMemory((int)(0x300 + nWords * 4));
        for (var i = 0; i < nWords; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes((uint)(i + 1)));
        mem.Load(0x200, BitConverter.GetBytes(2u));
        mem.Load(0x204, BitConverter.GetBytes(3u));

        var eng = new StreamingEngine(32);
        eng.Configure(1, new StreamDescriptor(0x200, 4, 2, 4));
        var desc = new StreamDescriptor(
            0x000, 4,
            [new StreamDimension(2, 4), new StreamDimension(3, 0),],
            [new StreamModifier(0, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 0, 0, 1),]
        );
        eng.Configure(0, desc);
        for (var i = 0; i < 40; i++) eng.Step(mem);

        var values = new List<ulong>();
        while (eng.HasElement(0)) values.Add(eng.Consume(0));

        Assert.Equal(4 + 7 + 7, values.Count);
        Assert.True(eng.IsExhausted(0));
    }
}