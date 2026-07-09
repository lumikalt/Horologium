using System.Collections.Concurrent;

namespace RiscV32.Trace;

/// <summary>
/// Bounded producer–consumer hand-off that moves trace formatting and I/O off the
/// simulation thread. The simulation thread posts small captured records in commit
/// order; a dedicated consumer thread applies the consume action to each record in
/// that same order, so the resulting output is byte-identical to writing synchronously.
/// <para>
/// The queue is bounded: when the consumer falls behind, <see cref="Post"/> blocks
/// instead of letting the backlog grow without limit. <see cref="Dispose"/> completes
/// the stream, joins the consumer thread, and rethrows any consumer failure.
/// </para>
/// <para>
/// On single-threaded runtimes (browser-wasm) records are consumed inline on the
/// posting thread instead — same output, no thread.
/// </para>
/// </summary>
internal sealed class BackgroundTraceChannel<T> : IDisposable {
    private readonly Action<T> _consume;
    private readonly BlockingCollection<T>? _queue;
    private readonly Thread? _consumer;
    private volatile Exception? _fault;

    public BackgroundTraceChannel(Action<T> consume, string name, int capacity = 1 << 16) {
        ArgumentNullException.ThrowIfNull(consume);
        _consume = consume;

        if (OperatingSystem.IsBrowser()) return; // inline mode

        _queue = new BlockingCollection<T>(capacity);
        _consumer = new Thread(ConsumeAll) { IsBackground = true, Name = name, };
        _consumer.Start();
    }

    /// <summary>Hands one record to the consumer; blocks when the queue is full (backpressure).</summary>
    public void Post(T item) {
        if (_queue is null) {
            _consume(item);
            return;
        }

        ThrowIfFaulted();
        _queue.Add(item);
    }

    /// <summary>
    /// Completes the record stream, waits for the consumer to drain it, and
    /// propagates any consumer exception. Must be called before disposing the
    /// underlying output stream.
    /// </summary>
    public void Dispose() {
        if (_queue is null) return;

        _queue.CompleteAdding();
        _consumer!.Join();
        _queue.Dispose();
        ThrowIfFaulted();
    }

    private void ConsumeAll() {
        try {
            foreach (T item in _queue!.GetConsumingEnumerable()) _consume(item);
        }
        catch (Exception e) {
            _fault = e;
            // Keep draining so a producer blocked on a full queue is released;
            // the fault surfaces on the next Post or on Dispose.
            foreach (T _ in _queue!.GetConsumingEnumerable()) { }
        }
    }

    private void ThrowIfFaulted() {
        if (_fault is { } e)
            throw new IOException($"Background trace consumer failed: {e.Message}", e);
    }
}
