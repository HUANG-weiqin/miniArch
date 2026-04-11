namespace MiniArch.Core;

public readonly record struct ComponentType(int Value) : IComparable<ComponentType>
{
    public int CompareTo(ComponentType other) => Value.CompareTo(other.Value);

    public bool IsValid => Value >= 0;

    public static implicit operator int(ComponentType type) => type.Value;

    public static explicit operator ComponentType(int value) => new(value);
}
