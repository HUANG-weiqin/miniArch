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
            Array.Sort(components);

            var uniqueCount = 1;
            for (var i = 1; i < components.Length; i++)
            {
                if (components[i] != components[uniqueCount - 1])
                {
                    components[uniqueCount] = components[i];
                    uniqueCount++;
                }
            }

            if (uniqueCount != components.Length)
            {
                Array.Resize(ref components, uniqueCount);
            }
        }

        return new QueryComponentSet(components);
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

    private ComponentType[] Components => _components ?? Array.Empty<ComponentType>();
}
