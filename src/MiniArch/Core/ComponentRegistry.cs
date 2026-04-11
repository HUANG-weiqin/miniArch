using System.Collections.ObjectModel;

namespace MiniArch.Core;

public sealed class ComponentRegistry
{
    private readonly Dictionary<Type, ComponentType> _typeToId = new();
    private readonly List<Type> _idToType = new();

    public ComponentType GetOrCreate<T>() => GetOrCreate(typeof(T));

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

    public bool TryGetId(Type type, out ComponentType id) => _typeToId.TryGetValue(type, out id);

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

    public Type GetType(ComponentType id)
    {
        if (!TryGetType(id, out var type))
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        return type;
    }

    public IReadOnlyDictionary<Type, ComponentType> RegisteredTypes => new ReadOnlyDictionary<Type, ComponentType>(_typeToId);
}
