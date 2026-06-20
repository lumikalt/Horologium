namespace Orrery.Observation;

/// <summary>
/// Implemented by Setting&lt;T&gt; so Gear can lock all settings
/// without needing reflection or dynamic dispatch on the generic type.
/// </summary>
public interface ILockable {
    void Lock();
}