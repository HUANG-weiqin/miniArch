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
}
