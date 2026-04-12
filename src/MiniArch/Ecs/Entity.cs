namespace MiniArch.Ecs;

/// <summary>
/// User-facing entity handle.
/// </summary>
public readonly struct Entity : IEquatable<Entity>
{
    private readonly MiniArch.Core.Entity _value;

    internal Entity(MiniArch.Core.Entity value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the entity slot id.
    /// </summary>
    public int Id => _value.Id;

    /// <summary>
    /// Gets the version used to reject stale handles.
    /// </summary>
    public int Version => _value.Version;

    /// <summary>
    /// Returns whether the handle shape is valid.
    /// </summary>
    public bool IsValid => _value.IsValid;

    /// <summary>
    /// Returns whether the handle matches the given version.
    /// </summary>
    public bool MatchesVersion(int version) => _value.MatchesVersion(version);

    internal MiniArch.Core.Entity AsCore() => _value;

    internal static Entity FromCore(MiniArch.Core.Entity value) => new(value);

    /// <summary>
    /// Compares two entity handles by value.
    /// </summary>
    public bool Equals(Entity other) => _value.Equals(other._value);

    /// <summary>
    /// Compares against an object.
    /// </summary>
    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    /// <summary>
    /// Gets the hash code.
    /// </summary>
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>
    /// Returns a display string.
    /// </summary>
    public override string ToString() => _value.ToString();

    /// <summary>
    /// Compares two handles for equality.
    /// </summary>
    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    /// <summary>
    /// Compares two handles for inequality.
    /// </summary>
    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);
}
