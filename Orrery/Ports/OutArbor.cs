using Orrery.Scheduling;

namespace Orrery.Ports;

/// <summary>
/// The sending end of a typed communication channel between Gears.
///
/// Sending data does not deliver it immediately — it schedules delivery
/// on the Escapement at (currentTick + latency), at phase PortUpdate.
/// This models the pipeline register delay, wire delay, and bus latency uniformly.
/// </summary>
public sealed class OutArbor<T> {
    private readonly Escapement _escapement;

    private InArbor<T>? _bound;
    private int _latency;

    public string Name { get; }

    /// <summary>True if this arbor has been bound to an InArbor.</summary>
    public bool IsBound => _bound is not null;

    public OutArbor(string name, Escapement escapement) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(escapement);
        Name = name;
        _escapement = escapement;
    }

    /// <summary>
    /// Binds this output to a target input with a given latency in ticks.
    /// Latency must be at least 1 — zero-latency ports collapse the
    /// distinction between sender and receiver within a tick, which
    /// breaks phase ordering guarantees.
    /// </summary>
    public void Bind(InArbor<T> target, int latency = 1) {
        ArgumentNullException.ThrowIfNull(target);

        if (latency < 1)
            throw new ArgumentOutOfRangeException(
                nameof(latency),
                $"Arbor '{Name}' → '{target.Name}': latency must be at least 1. " +
                $"Zero-latency ports break phase ordering guarantees."
            );

        if (_bound is not null)
            throw new InvalidOperationException(
                $"OutArbor '{Name}' is already bound to '{_bound.Name}'. " +
                $"An OutArbor may only be bound once."
            );

        _bound = target;
        _latency = latency;
    }

    /// <summary>
    /// Sends data through this arbor. Delivery is scheduled on the Escapement
    /// at (currentTick + latency), phase PortUpdate.
    ///
    /// The sender continues executing immediately — this is not a blocking call.
    /// </summary>
    public void Send(T data) {
        if (_bound is null)
            throw new InvalidOperationException(
                $"OutArbor '{Name}' has not been bound. " +
                $"Call Bind() during the Finalizing lifecycle phase."
            );

        InArbor<T>? target = _bound; // capture for closure
        _escapement.ScheduleAfter(
            () => target.Deliver(data),
            _latency,
            Phase.PortUpdate
        );
    }
}