namespace MiniArch.Ecs;

/// <summary>
/// Reusable user-facing query description.
/// </summary>
public readonly struct QueryDescription : IEquatable<QueryDescription>
{
    private readonly MiniArch.Core.QueryDescription _value;

    /// <summary>
    /// Gets the required component types.
    /// </summary>
    public IReadOnlyList<Type> RequiredTypes => _value.RequiredTypes;

    /// <summary>
    /// Gets the excluded component types.
    /// </summary>
    public IReadOnlyList<Type> ExcludedTypes => _value.ExcludedTypes;

    /// <summary>
    /// Gets the optional any-match component types.
    /// </summary>
    public IReadOnlyList<Type> AnyTypes => _value.AnyTypes;

    /// <summary>
    /// Gets the underlying advanced query description.
    /// </summary>
    public MiniArch.Core.QueryDescription Advanced => _value;

    /// <summary>
    /// Adds a required component type.
    /// </summary>
    public QueryDescription With<T>() => new(_value.With<T>());

    /// <summary>
    /// Adds an excluded component type.
    /// </summary>
    public QueryDescription Without<T>() => new(_value.Without<T>());

    /// <summary>
    /// Adds an any-match component type.
    /// </summary>
    public QueryDescription WithAny<T>() => new(_value.WithAny<T>());

    /// <summary>
    /// Alias for <see cref="WithAny{T}()"/>.
    /// </summary>
    public QueryDescription Or<T>() => WithAny<T>();

    /// <summary>
    /// Compares two descriptions by value.
    /// </summary>
    public bool Equals(QueryDescription other) => _value.Equals(other._value);

    /// <summary>
    /// Compares against an object.
    /// </summary>
    public override bool Equals(object? obj) => obj is QueryDescription other && Equals(other);

    /// <summary>
    /// Gets the hash code.
    /// </summary>
    public override int GetHashCode() => _value.GetHashCode();

    internal MiniArch.Core.QueryDescription ToCore() => _value;

    private QueryDescription(MiniArch.Core.QueryDescription value)
    {
        _value = value;
    }
}
