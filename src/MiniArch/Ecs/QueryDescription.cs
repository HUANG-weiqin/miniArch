namespace MiniArch.Ecs;

public readonly struct QueryDescription : IEquatable<QueryDescription>
{
    private readonly MiniArch.Core.QueryDescription _value;

    public IReadOnlyList<Type> RequiredTypes => _value.RequiredTypes;

    public IReadOnlyList<Type> ExcludedTypes => _value.ExcludedTypes;

    public IReadOnlyList<Type> AnyTypes => _value.AnyTypes;

    public MiniArch.Core.QueryDescription Advanced => _value;

    public QueryDescription With<T>() => new(_value.With<T>());

    public QueryDescription Without<T>() => new(_value.Without<T>());

    public QueryDescription WithAny<T>() => new(_value.WithAny<T>());

    public QueryDescription Or<T>() => WithAny<T>();

    public bool Equals(QueryDescription other) => _value.Equals(other._value);

    public override bool Equals(object? obj) => obj is QueryDescription other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    internal MiniArch.Core.QueryDescription ToCore() => _value;

    private QueryDescription(MiniArch.Core.QueryDescription value)
    {
        _value = value;
    }
}
