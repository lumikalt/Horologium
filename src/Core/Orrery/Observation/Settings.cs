namespace Orrery.Observation;

/// <summary>
///     A named, typed configuration parameter.
///     <para>
///         Settings are declared during Building, given values before Running,
///         and locked once the simulation starts. Attempting to write after
///         lock throws immediately — this prevents accidental mid-run mutation.
///     </para>
/// </summary>
public sealed class Setting<T> : ILockable {
    private T _value;

    public Setting(string name, T defaultValue, string description = "") {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description;
        _value = defaultValue;
    }

    public string Name { get; }
    public string Description { get; }

    /// <summary>
    ///     The current value of this setting.
    ///     Throws on write if the setting has been locked.
    /// </summary>
    public T Value {
        get => _value;
        set {
            if (IsLocked)
                throw new InvalidOperationException(
                    $"Setting '{Name}' is locked and cannot be changed once " +
                    $"the simulation is running."
                );
            _value = value;
        }
    }

    public bool IsLocked { get; private set; }

    void ILockable.Lock() => IsLocked = true;

    public override string ToString() => $"{Name} = {_value}";
}