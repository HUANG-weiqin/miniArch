using System.Security.Cryptography;
using System.Text;

namespace MiniArch.Core;

/// <summary>
/// Maps component types to runtime ids.
/// </summary>
internal sealed class ComponentRegistry
{
    /// <summary>
    /// Global shared registry instance.
    /// </summary>
    internal static ComponentRegistry Shared { get; } = new();

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
    /// Total number of registered component types.
    /// </summary>
    internal int ComponentTypeCount => Volatile.Read(ref _snapshot).IdToType.Length;

    /// <summary>
    /// Returns all registered types in registration order.
    /// Index in the returned array is the <see cref="ComponentType.Value"/>.
    /// </summary>
    internal Type[] GetRegisteredTypes()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var result = new Type[snapshot.IdToType.Length];
        Array.Copy(snapshot.IdToType, result, result.Length);
        return result;
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

    /// <summary>
    /// Computes a SHA-256 fingerprint of the current id→type mapping (full 32 bytes).
    /// For manual/debug use. See <see cref="MiniArch.ComponentSchema"/>.
    /// </summary>
    internal byte[] GetFingerprint() => ComputeFingerprintBytes(Volatile.Read(ref _snapshot).IdToType);

    private static byte[] ComputeFingerprintBytes(Type[] idToType)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(BitConverter.GetBytes(idToType.Length));
        foreach (var type in idToType)
        {
            var name = type.FullName ?? type.Name;
            var nameBytes = Encoding.UTF8.GetBytes(name);
            hash.AppendData(BitConverter.GetBytes(nameBytes.Length));
            hash.AppendData(nameBytes);
        }

        return hash.GetHashAndReset();
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
