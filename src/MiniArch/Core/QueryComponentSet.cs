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

    public QueryComponentSet Add(ComponentType component)
    {
        var components = Components;
        var index = Array.BinarySearch(components, component);
        if (index >= 0)
        {
            return this;
        }

        index = ~index;
        var next = new ComponentType[components.Length + 1];
        Array.Copy(components, 0, next, 0, index);
        next[index] = component;
        Array.Copy(components, index, next, index + 1, components.Length - index);
        return new QueryComponentSet(next);
    }

    public Signature ToSignature()
    {
        if (Components.Length == 0)
        {
            return Signature.Empty;
        }

        var copied = new ComponentType[Components.Length];
        Array.Copy(Components, copied, Components.Length);
        return new Signature(copied);
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

    public override int GetHashCode()
    {
        var hash = 17;
        for (var i = 0; i < Components.Length; i++)
        {
            hash = unchecked(hash * 31 + Components[i].Value);
        }

        return hash;
    }

    public static QueryComponentSet Create(ComponentType component1)
    {
        return new QueryComponentSet(new[] { component1 });
    }

    public static QueryComponentSet Create(ComponentType component1, ComponentType component2)
    {
        if (component1 == component2)
        {
            return Create(component1);
        }

        if (component1.Value > component2.Value)
        {
            (component1, component2) = (component2, component1);
        }

        return new QueryComponentSet(new[] { component1, component2 });
    }

    public static QueryComponentSet Create(ComponentType component1, ComponentType component2, ComponentType component3)
    {
        Span<ComponentType> sorted = stackalloc ComponentType[3];
        sorted[0] = component1;
        sorted[1] = component2;
        sorted[2] = component3;

        for (var i = 1; i < sorted.Length; i++)
        {
            var current = sorted[i];
            var j = i - 1;
            while (j >= 0 && sorted[j].Value > current.Value)
            {
                sorted[j + 1] = sorted[j];
                j--;
            }

            sorted[j + 1] = current;
        }

        var uniqueCount = 1;
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] != sorted[uniqueCount - 1])
            {
                sorted[uniqueCount] = sorted[i];
                uniqueCount++;
            }
        }

        var components = new ComponentType[uniqueCount];
        for (var i = 0; i < uniqueCount; i++)
        {
            components[i] = sorted[i];
        }

        return new QueryComponentSet(components);
    }

    private ComponentType[] Components => _components ?? Array.Empty<ComponentType>();
}
