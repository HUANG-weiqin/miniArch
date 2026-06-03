using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;

namespace MiniArch.Core;

/// <summary>
/// Sorted set of component ids.
/// </summary>
public sealed class Signature : IEquatable<Signature>, IEnumerable<ComponentType>
{
    private readonly ComponentType[] _components;
    private readonly int _hashCode;
    internal readonly ComponentMask256 Mask;

    /// <summary>
    /// Gets the empty signature.
    /// </summary>
    public static Signature Empty { get; } = new(Array.Empty<ComponentType>(), isNormalized: true);

    /// <summary>
    /// Creates a normalized signature from a sequence.
    /// </summary>
    public Signature(IEnumerable<ComponentType> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        _components = Normalize(components);
        _hashCode = ComputeHashCode(_components);
        Mask = ComponentMask256.FromComponents(_components);
    }

    /// <summary>
    /// Creates a normalized signature from components.
    /// </summary>
    public Signature(params ComponentType[] components)
        : this((IEnumerable<ComponentType>)components)
    {
    }

    private Signature(ComponentType[] components, bool isNormalized)
    {
        _components = components.Length == 0 ? Array.Empty<ComponentType>() : components;
        _hashCode = ComputeHashCode(_components);
        Mask = ComponentMask256.FromComponents(_components);
    }

    internal static Signature CreateNormalized(ComponentType[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return components.Length == 0 ? Empty : new Signature(components, isNormalized: true);
    }

    /// <summary>
    /// Gets the component count.
    /// </summary>
    public int Count => _components.Length;

    /// <summary>
    /// Gets the component span.
    /// </summary>
    public ReadOnlySpan<ComponentType> AsSpan() => _components;

    /// <summary>
    /// Gets a bitmask where bit i is set if component with id i is present.
    /// Only accurate for component ids 0..63; always 0 for ids >= 64.
    /// </summary>
    public long ComponentMask => Mask.L0;

    /// <summary>
    /// Returns whether the signature contains a component.
    /// Uses 256-bit mask for ids 0..255; falls back to array search for ids >= 256.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ComponentType component)
    {
        var id = component.Value;
        if ((uint)id < 256 && !Mask.IsBitSet(id))
        {
            return false;
        }

        return ContainsSlow(component);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ContainsSlow(ComponentType component)
    {
        var c = _components;
        if (c.Length <= 4)
        {
            for (var i = 0; i < c.Length; i++)
            {
                if (c[i] == component)
                {
                    return true;
                }
            }

            return false;
        }

        return Array.BinarySearch(c, component) >= 0;
    }

    /// <summary>
    /// Returns a signature with one component added.
    /// </summary>
    public Signature Add(ComponentType component)
    {
        var index = Array.BinarySearch(_components, component);
        if (index >= 0)
        {
            return this;
        }

        index = ~index;
        var next = new ComponentType[_components.Length + 1];
        Array.Copy(_components, 0, next, 0, index);
        next[index] = component;
        Array.Copy(_components, index, next, index + 1, _components.Length - index);
        return new Signature(next, isNormalized: true);
    }

    /// <summary>
    /// Returns a signature with one component removed.
    /// </summary>
    public Signature Remove(ComponentType component)
    {
        var index = Array.BinarySearch(_components, component);
        if (index < 0)
        {
            return this;
        }

        var next = new ComponentType[_components.Length - 1];
        Array.Copy(_components, 0, next, 0, index);
        Array.Copy(_components, index + 1, next, index, _components.Length - index - 1);
        return new Signature(next, isNormalized: true);
    }

    /// <summary>
    /// Returns an enumerator over the components.
    /// </summary>
    public IEnumerator<ComponentType> GetEnumerator() => ((IEnumerable<ComponentType>)_components).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Compares two signatures by value.
    /// </summary>
    public bool Equals(Signature? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || _hashCode != other._hashCode || _components.Length != other._components.Length)
        {
            return false;
        }

        for (var i = 0; i < _components.Length; i++)
        {
            if (_components[i] != other._components[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares against an object.
    /// </summary>
    public override bool Equals(object? obj) => obj is Signature other && Equals(other);

    /// <summary>
    /// Gets the cached hash code.
    /// </summary>
    public override int GetHashCode() => _hashCode;

    /// <summary>
    /// Returns a display string.
    /// </summary>
    public override string ToString()
    {
        if (_components.Length == 0)
        {
            return "[]";
        }

        var builder = new StringBuilder(_components.Length * 4);
        builder.Append('[');
        builder.Append(_components[0].Value);
        for (var i = 1; i < _components.Length; i++)
        {
            builder.Append(", ");
            builder.Append(_components[i].Value);
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static ComponentType[] Normalize(IEnumerable<ComponentType> components)
    {
        var normalized = CopyToArray(components);
        if (normalized.Length <= 1)
        {
            return normalized.Length == 0 ? Array.Empty<ComponentType>() : normalized;
        }

        var uniqueCount = SpanHelper.SortAndDeduplicate(normalized);

        if (uniqueCount == normalized.Length)
        {
            return normalized;
        }

        var deduplicated = new ComponentType[uniqueCount];
        Array.Copy(normalized, deduplicated, uniqueCount);
        return deduplicated;
    }

    private static ComponentType[] CopyToArray(IEnumerable<ComponentType> components)
    {
        if (components is ComponentType[] array)
        {
            if (array.Length == 0)
            {
                return Array.Empty<ComponentType>();
            }

            var arrayCopy = new ComponentType[array.Length];
            Array.Copy(array, arrayCopy, array.Length);
            return arrayCopy;
        }

        if (components is ICollection<ComponentType> collection)
        {
            if (collection.Count == 0)
            {
                return Array.Empty<ComponentType>();
            }

            var collectionCopy = new ComponentType[collection.Count];
            collection.CopyTo(collectionCopy, 0);
            return collectionCopy;
        }

        using var enumerator = components.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return Array.Empty<ComponentType>();
        }

        var buffer = new ComponentType[4];
        var count = 0;
        do
        {
            if (count == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            buffer[count] = enumerator.Current;
            count++;
        }
        while (enumerator.MoveNext());

        if (count == buffer.Length)
        {
            return buffer;
        }

        var trimmed = new ComponentType[count];
        Array.Copy(buffer, trimmed, count);
        return trimmed;
    }

    private static int ComputeHashCode(ReadOnlySpan<ComponentType> components) =>
        SpanHelper.CombineHashCodes(components);
}
