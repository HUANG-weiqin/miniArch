namespace MiniArch.Core;

internal readonly struct QueryComponentSet : IEquatable<QueryComponentSet>
{
    private readonly ComponentType[] _components;

    private QueryComponentSet(ComponentType[] components)
    {
        _components = components;
    }

    public static QueryComponentSet Empty { get; } = new(Array.Empty<ComponentType>());

    public int Count => Components.Length;

    public ReadOnlySpan<ComponentType> AsSpan() => Components;

    internal static QueryComponentSet CreateFrom(ComponentType[] components)
    {
        if (components.Length == 0)
        {
            return Empty;
        }

        if (components.Length > 1)
        {
            var uniqueCount = SpanSorting.SortAndDeduplicate(components.AsSpan());
            if (uniqueCount != components.Length)
            {
                Array.Resize(ref components, uniqueCount);
            }
        }

        return new QueryComponentSet(components);
    }

    public bool Equals(QueryComponentSet other)
    {
        if (Count != other.Count)
        {
            return false;
        }

        var left = Components;
        var right = other.Components;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is QueryComponentSet other && Equals(other);

    public override int GetHashCode() => SpanSorting.CombineHashCodes(Components);

    private ComponentType[] Components => _components ?? Array.Empty<ComponentType>();
}
