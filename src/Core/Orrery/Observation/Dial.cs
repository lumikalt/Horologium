namespace Orrery.Observation;

/// <summary>
/// A named derived metric expressed as a function over other values.
/// <para>
/// A Dial does not store data — it computes on demand from a Func&lt;double&gt;.
/// This means it always reflects the current state of whatever it references.
/// </para>
/// <para>
/// Examples:
/// <list type="bullet">
/// <item><c>IPC  = () => retired.Value / (double)cycles.Value</c></item>
/// <item><c>CPI  = () => cycles.Value  / (double)retired.Value</c></item>
/// <item><c>Util = () => busyCycles.Value / (double)totalCycles.Value * 100.0</c></item>
/// </list>
/// </para>
/// </summary>
public sealed class Dial {
    private readonly Func<double> _expression;

    public string Name { get; }
    public string Description { get; }

    public Dial(string name, Func<double> expression, string description = "") {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(expression);
        Name = name;
        Description = description;
        _expression = expression;
    }

    /// <summary>Evaluates and returns the current value of this dial.</summary>
    public double Read() => _expression();

    public override string ToString() => $"{Name} = {Read():F4}";
}