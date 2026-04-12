namespace MiniArch.Ecs;

public readonly struct Entity : IEquatable<Entity>
{
    private readonly MiniArch.Core.Entity _value;

    internal Entity(MiniArch.Core.Entity value)
    {
        _value = value;
    }

    public int Id => _value.Id;

    public int Version => _value.Version;

    public bool IsValid => _value.IsValid;

    public bool MatchesVersion(int version) => _value.MatchesVersion(version);

    internal MiniArch.Core.Entity AsCore() => _value;

    internal static Entity FromCore(MiniArch.Core.Entity value) => new(value);

    public bool Equals(Entity other) => _value.Equals(other._value);

    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString() => _value.ToString();

    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
