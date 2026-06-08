namespace MiniArch.Core;

/// <summary>
/// Runtime component id.
/// </summary>
/// <param name="Value">The component id value.</param>
internal readonly record struct ComponentType(int Value) : IComparable<ComponentType>
{
    /// <summary>
    /// Compares ids by value.
    /// </summary>
    public int CompareTo(ComponentType other) => Value.CompareTo(other.Value);

    /// <summary>
    /// Gets whether the id is non-negative.
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// Converts to the underlying id value.
    /// </summary>
    public static implicit operator int(ComponentType type) => type.Value;

    /// <summary>
    /// Creates an id from a value.
    /// </summary>
    public static explicit operator ComponentType(int value) => new(value);
}
