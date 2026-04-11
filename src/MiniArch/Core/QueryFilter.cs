namespace MiniArch.Core;

internal readonly struct QueryFilter : IEquatable<QueryFilter>
{
    public static QueryFilter Empty { get; } = new(QueryComponentSet.Empty, QueryComponentSet.Empty, QueryComponentSet.Empty);

    public QueryFilter(QueryComponentSet required, QueryComponentSet excluded, QueryComponentSet any)
    {
        Required = required;
        Excluded = excluded;
        Any = any;
    }

    public QueryComponentSet Required { get; }

    public QueryComponentSet Excluded { get; }

    public QueryComponentSet Any { get; }

    public static QueryFilter CreateRequired(ComponentType component1)
    {
        return new QueryFilter(QueryComponentSet.Create(component1), QueryComponentSet.Empty, QueryComponentSet.Empty);
    }

    public static QueryFilter CreateRequired(ComponentType component1, ComponentType component2)
    {
        return new QueryFilter(QueryComponentSet.Create(component1, component2), QueryComponentSet.Empty, QueryComponentSet.Empty);
    }

    public static QueryFilter CreateRequired(ComponentType component1, ComponentType component2, ComponentType component3)
    {
        return new QueryFilter(QueryComponentSet.Create(component1, component2, component3), QueryComponentSet.Empty, QueryComponentSet.Empty);
    }

    public bool Equals(QueryFilter other)
    {
        return Required.Equals(other.Required)
            && Excluded.Equals(other.Excluded)
            && Any.Equals(other.Any);
    }

    public override bool Equals(object? obj) => obj is QueryFilter other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Required, Excluded, Any);
}
