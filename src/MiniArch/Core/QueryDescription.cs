using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Reusable query filter description.
/// </summary>
public readonly struct QueryDescription : IEquatable<QueryDescription>
{
    private readonly QueryDescriptionTypeSet _required;
    private readonly QueryDescriptionTypeSet _excluded;
    private readonly QueryDescriptionTypeSet _any;

    /// <summary>
    /// Gets required component types.
    /// </summary>
    public IReadOnlyList<Type> RequiredTypes => _required.GetTypes();

    /// <summary>
    /// Gets excluded component types.
    /// </summary>
    public IReadOnlyList<Type> ExcludedTypes => _excluded.GetTypes();

    /// <summary>
    /// Gets any-match component types.
    /// </summary>
    public IReadOnlyList<Type> AnyTypes => _any.GetTypes();

    /// <summary>
    /// Adds a required type.
    /// </summary>
    public QueryDescription With<T>() where T : unmanaged
    {
        var required = _required.Add(typeof(T));
        return required.Equals(_required) ? this : new QueryDescription(required, _excluded, _any);
    }

    /// <summary>
    /// Adds an excluded type.
    /// </summary>
    public QueryDescription Without<T>() where T : unmanaged
    {
        var excluded = _excluded.Add(typeof(T));
        return excluded.Equals(_excluded) ? this : new QueryDescription(_required, excluded, _any);
    }

    /// <summary>
    /// Adds an any-match type.
    /// </summary>
    public QueryDescription WithAny<T>() where T : unmanaged
    {
        var any = _any.Add(typeof(T));
        return any.Equals(_any) ? this : new QueryDescription(_required, _excluded, any);
    }

    /// <summary>
    /// Compares two descriptions by value.
    /// </summary>
    public bool Equals(QueryDescription other) => _required.Equals(other._required)
        && _excluded.Equals(other._excluded)
        && _any.Equals(other._any);

    /// <summary>
    /// Compares against an object.
    /// </summary>
    public override bool Equals(object? obj) => obj is QueryDescription other && Equals(other);

    /// <summary>
    /// Gets the hash code computed from the three type sets.
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(_required, _excluded, _any);

    /// <summary>
    /// Returns whether two query descriptions are equal.
    /// </summary>
    public static bool operator ==(QueryDescription left, QueryDescription right) => left.Equals(right);

    /// <summary>
    /// Returns whether two query descriptions are not equal.
    /// </summary>
    public static bool operator !=(QueryDescription left, QueryDescription right) => !left.Equals(right);

    internal ReadOnlySpan<Type> GetRequiredTypes() => _required.AsSpan();

    internal ReadOnlySpan<Type> GetExcludedTypes() => _excluded.AsSpan();

    internal ReadOnlySpan<Type> GetAnyTypes() => _any.AsSpan();

    private QueryDescription(QueryDescriptionTypeSet required, QueryDescriptionTypeSet excluded, QueryDescriptionTypeSet any)
    {
        _required = required;
        _excluded = excluded;
        _any = any;
    }

    private readonly struct QueryDescriptionTypeSet : IEquatable<QueryDescriptionTypeSet>
    {
        private static readonly TypeHandleComparer Comparer = new();
        private readonly Type[]? _types;

        public ReadOnlySpan<Type> AsSpan() => _types ?? Array.Empty<Type>();

        internal Type[] GetTypes() => _types?.ToArray() ?? Array.Empty<Type>();

        public QueryDescriptionTypeSet Add(Type type)
        {
            var types = _types ?? Array.Empty<Type>();
            var index = Array.BinarySearch(types, type, Comparer);
            if (index >= 0)
            {
                return this;
            }

            index = ~index;
            var next = new Type[types.Length + 1];
            Array.Copy(types, 0, next, 0, index);
            next[index] = type;
            Array.Copy(types, index, next, index + 1, types.Length - index);
            return new QueryDescriptionTypeSet(next);
        }

        public bool Equals(QueryDescriptionTypeSet other)
        {
            var left = AsSpan();
            var right = other.AsSpan();
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is QueryDescriptionTypeSet other && Equals(other);

        public override int GetHashCode()
        {
            var span = AsSpan();
            int hash = 17;
            for (int i = 0; i < span.Length; i++)
                hash = unchecked(hash * 31 + span[i].TypeHandle.GetHashCode());
            return hash;
        }

        private QueryDescriptionTypeSet(Type[] types)
        {
            _types = types;
        }

        private sealed class TypeHandleComparer : IComparer<Type>
        {
            public int Compare(Type? x, Type? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x is null)
                {
                    return -1;
                }

                if (y is null)
                {
                    return 1;
                }

                return x.TypeHandle.Value.ToInt64().CompareTo(y.TypeHandle.Value.ToInt64());
            }
        }
    }
}
