using System.Collections;

namespace MiniArch.Core;

public sealed class Signature : IEquatable<Signature>, IEnumerable<ComponentType>
{
    private readonly ComponentType[] _components;
    private readonly int _hashCode;

    public static Signature Empty { get; } = new(Array.Empty<ComponentType>());

    public Signature(IEnumerable<ComponentType> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        _components = Normalize(components);
        _hashCode = ComputeHashCode(_components);
    }

    public Signature(params ComponentType[] components)
        : this((IEnumerable<ComponentType>)components)
    {
    }

    public int Count => _components.Length;

    public ReadOnlySpan<ComponentType> AsSpan() => _components;

    public bool Contains(ComponentType component) => Array.BinarySearch(_components, component) >= 0;

    public Signature Add(ComponentType component)
    {
        if (Contains(component))
        {
            return this;
        }

        var next = new ComponentType[_components.Length + 1];
        var index = Array.BinarySearch(_components, component);
        if (index < 0)
        {
            index = ~index;
        }

        Array.Copy(_components, 0, next, 0, index);
        next[index] = component;
        Array.Copy(_components, index, next, index + 1, _components.Length - index);
        return new Signature(next);
    }

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
        return new Signature(next);
    }

    public IEnumerator<ComponentType> GetEnumerator() => ((IEnumerable<ComponentType>)_components).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

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

    public override bool Equals(object? obj) => obj is Signature other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public override string ToString() => $"[{string.Join(", ", _components.Select(component => component.Value))}]";

    private static ComponentType[] Normalize(IEnumerable<ComponentType> components)
    {
        return components.Distinct().OrderBy(component => component.Value).ToArray();
    }

    private static int ComputeHashCode(ReadOnlySpan<ComponentType> components)
    {
        var hash = 17;
        for (var i = 0; i < components.Length; i++)
        {
            hash = unchecked(hash * 31 + components[i].Value);
        }

        return hash;
    }
}
