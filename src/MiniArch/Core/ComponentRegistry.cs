namespace MiniArch.Core;

/// <summary>
/// Maps component types to runtime ids.
/// </summary>
public sealed class ComponentRegistry
{
    /// <summary>
    /// Global shared registry instance.
    /// </summary>
    public static ComponentRegistry Shared { get; } = new();

    private readonly object _writeLock = new();
    private RegistrySnapshot _snapshot = new(new Dictionary<Type, ComponentType>(), Array.Empty<Type>());

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

        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.TypeToId.TryGetValue(type, out var existing))
        {
            return existing;
        }

        lock (_writeLock)
        {
            snapshot = _snapshot;
            if (snapshot.TypeToId.TryGetValue(type, out existing))
            {
                return existing;
            }

            var id = new ComponentType(snapshot.IdToType.Length);
            var updatedTypeToId = new Dictionary<Type, ComponentType>(snapshot.TypeToId)
            {
                [type] = id,
            };
            var updatedIdToType = new Type[snapshot.IdToType.Length + 1];
            Array.Copy(snapshot.IdToType, updatedIdToType, snapshot.IdToType.Length);
            updatedIdToType[^1] = type;

            Volatile.Write(ref _snapshot, new RegistrySnapshot(updatedTypeToId, updatedIdToType));
            return id;
        }
    }

    /// <summary>
    /// Tries to get the type for an id.
    /// </summary>
    public bool TryGetType(ComponentType id, out Type type)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (!id.IsValid || id.Value >= snapshot.IdToType.Length)
        {
            type = null!;
            return false;
        }

        type = snapshot.IdToType[id.Value];
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

    private sealed record RegistrySnapshot(IReadOnlyDictionary<Type, ComponentType> TypeToId, Type[] IdToType);
}

/// <summary>
/// Provides cached component type id for <typeparamref name="T" />.
/// </summary>
internal static class Component<T>
{
    internal static readonly ComponentType ComponentType = ComponentRegistry.Shared.GetOrCreate<T>();
}
