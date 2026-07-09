namespace MiniArch.Core;

internal readonly struct QueryFilter : IEquatable<QueryFilter>
{
    public QueryFilter(QueryComponentSet required, QueryComponentSet excluded, QueryComponentSet any, bool exact = false)
    {
        Required = required;
        Excluded = excluded;
        Any = any;
        Exact = exact;
    }

    public QueryComponentSet Required { get; }

    public QueryComponentSet Excluded { get; }

    public QueryComponentSet Any { get; }

    public bool Exact { get; }

    public bool Equals(QueryFilter other) => Required.Equals(other.Required)
        && Excluded.Equals(other.Excluded)
        && Any.Equals(other.Any)
        && Exact == other.Exact;

    public override bool Equals(object? obj) => obj is QueryFilter other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Required, Excluded, Any, Exact);
}
