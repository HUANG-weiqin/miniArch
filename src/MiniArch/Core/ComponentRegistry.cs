using System.Collections.ObjectModel;

namespace MiniArch.Core;

/// <summary>
/// Maps component types to runtime ids.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<Type, ComponentType> _typeToId = new();
    private readonly List<Type> _idToType = new();

    /// <summary>
    /// Gets or creates the id for <typeparamref name="T" />.
    /// </summary>
    public ComponentType GetOrCreate<T>() => GetOrCreate(typeof(T));

    /// <summary>
    /// Gets or creates the id for a type.
    /// </summary>
    public ComponentType GetOrCreate(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (_typeToId.TryGetValue(type, out var existing))
        {
            return existing;
        }

        var id = new ComponentType(_idToType.Count);
        _typeToId[type] = id;
        _idToType.Add(type);
        return id;
    }

    /// <summary>
    /// Tries to get the id for a type.
    /// </summary>
    public bool TryGetId(Type type, out ComponentType id) => _typeToId.TryGetValue(type, out id);

    /// <summary>
    /// Tries to get the type for an id.
    /// </summary>
    public bool TryGetType(ComponentType id, out Type type)
    {
        if (!id.IsValid || id.Value >= _idToType.Count)
        {
            type = null!;
            return false;
        }

        type = _idToType[id.Value];
        return true;
    }

    /// <summary>
    /// Gets the type for an id.
    /// </summary>
    public Type GetType(ComponentType id)
    {
        if (!TryGetType(id, out var type))
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        return type;
    }

    /// <summary>
    /// Gets the registered type map.
    /// </summary>
    public IReadOnlyDictionary<Type, ComponentType> RegisteredTypes => new ReadOnlyDictionary<Type, ComponentType>(_typeToId);
}
