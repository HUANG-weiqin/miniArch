namespace MiniArch.Core;

internal readonly struct QueryFilter
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
}
