using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniArch.Core;

/// <summary>
/// Saves and loads world snapshots.
/// </summary>
public static class WorldSnapshot
{
    private const int Magic = 0x4D415243;
    private const int FormatVersion = 2;

    private static readonly ConcurrentDictionary<Type, ColumnCodec> ColumnCodecs = new();

    /// <summary>
    /// Writes a world snapshot.
    /// </summary>
    public static void Save(Stream stream, World world)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);

        if (!stream.CanWrite)
        {
            throw new ArgumentException("Snapshot stream must be writable.", nameof(stream));
        }

        var persistedArchetypes = CollectPersistedArchetypes(world);
        var schemaEntries = BuildSchemaEntries(world, persistedArchetypes);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(world.ChunkCapacity);
        writer.Write(world.EntitySlotCount);
        writer.Write(schemaEntries.Count);
        writer.Write(persistedArchetypes.Count);
        writer.Write(world.Hierarchy.CountLiveLinks(world));

        foreach (var record in world.EntityRecords)
        {
            writer.Write(record.Version);
        }

        foreach (var schemaEntry in schemaEntries)
        {
            writer.Write(schemaEntry.SchemaName);
        }

        foreach (var archetype in persistedArchetypes)
        {
            WriteArchetype(writer, world, archetype, schemaEntries);
        }

        foreach (var (child, parent) in world.Hierarchy.EnumerateLiveLinks(world))
        {
            writer.Write(child.Id);
            writer.Write(parent.Id);
        }
    }

    /// <summary>
    /// Reads a world snapshot.
    /// </summary>
    public static World Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException("Snapshot stream must be readable.", nameof(stream));
        }

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        EnsureHeader(reader);

        var chunkCapacity = reader.ReadInt32();
        var entitySlotCount = reader.ReadInt32();
        var schemaCount = reader.ReadInt32();
        var archetypeCount = reader.ReadInt32();
        var hierarchyLinkCount = reader.ReadInt32();

        var slotVersions = new int[entitySlotCount];
        for (var index = 0; index < slotVersions.Length; index++)
        {
            slotVersions[index] = reader.ReadInt32();
        }

        var schemaTypes = new Type[schemaCount];
        for (var index = 0; index < schemaTypes.Length; index++)
        {
            var schemaName = reader.ReadString();
            var componentType = Type.GetType(schemaName, throwOnError: true)!;
            EnsureSnapshotSupported(componentType);
            schemaTypes[index] = componentType;
        }

        var world = new World(chunkCapacity, entitySlotCount);
        world.Reset(entitySlotCount);

        for (var index = 0; index < slotVersions.Length; index++)
        {
            world.SetSnapshotEntityVersion(index, slotVersions[index]);
        }

        var schemaComponentTypes = new ComponentType[schemaTypes.Length];
        for (var index = 0; index < schemaTypes.Length; index++)
        {
            schemaComponentTypes[index] = world.Components.GetOrCreate(schemaTypes[index]);
        }

        for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
        {
            ReadArchetype(reader, world, schemaComponentTypes, slotVersions);
        }

        for (var linkIndex = 0; linkIndex < hierarchyLinkCount; linkIndex++)
        {
            var childId = reader.ReadInt32();
            var parentId = reader.ReadInt32();
            var child = new Entity(childId, slotVersions[childId]);
            var parent = new Entity(parentId, slotVersions[parentId]);
            world.LinkSnapshot(parent, child);
        }

        world.RebuildFreeIdStack();
        return world;
    }

    private static void EnsureHeader(BinaryReader reader)
    {
        var magic = reader.ReadInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException("Snapshot magic header does not match MiniArch snapshot format.");
        }

        var formatVersion = reader.ReadInt32();
        if (formatVersion != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported snapshot format version {formatVersion}.");
        }
    }

    private static List<Archetype> CollectPersistedArchetypes(World world)
    {
        var archetypes = new List<Archetype>();
        foreach (var archetype in world.Archetypes)
        {
            if (archetype.EntityCount > 0)
            {
                archetypes.Add(archetype);
            }
        }

        return archetypes;
    }

    private static List<SchemaEntry> BuildSchemaEntries(World world, IReadOnlyList<Archetype> archetypes)
    {
        var entries = new List<SchemaEntry>();
        var seenTypes = new HashSet<Type>();

        foreach (var archetype in archetypes)
        {
            var components = archetype.Signature.AsSpan();
            for (var index = 0; index < components.Length; index++)
            {
                var componentType = world.Components.GetType(components[index]);
                if (!seenTypes.Add(componentType))
                {
                    continue;
                }

                EnsureSnapshotSupported(componentType);
                entries.Add(new SchemaEntry(componentType, GetSchemaName(componentType), -1));
            }
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.SchemaName, right.SchemaName));

        for (var index = 0; index < entries.Count; index++)
        {
            entries[index] = entries[index] with { SchemaIndex = index };
        }

        return entries;
    }

    private static void WriteArchetype(BinaryWriter writer, World world, Archetype archetype, IReadOnlyList<SchemaEntry> schemaEntries)
    {
        var components = archetype.Signature.AsSpan();
        writer.Write(components.Length);

        var schemaIndexByType = new Dictionary<Type, int>(schemaEntries.Count);
        foreach (var entry in schemaEntries)
        {
            schemaIndexByType[entry.ComponentType] = entry.SchemaIndex;
        }

        for (var index = 0; index < components.Length; index++)
        {
            var runtimeType = world.Components.GetType(components[index]);
            writer.Write(schemaIndexByType[runtimeType]);
        }

        var persistedChunks = CountPersistedChunks(archetype);
        writer.Write(persistedChunks);

        foreach (var chunk in archetype.Chunks)
        {
            if (chunk.Count == 0)
            {
                continue;
            }

            writer.Write(chunk.Count);

            foreach (var entity in chunk.GetEntities())
            {
                writer.Write(entity.Id);
            }

            for (var columnIndex = 0; columnIndex < components.Length; columnIndex++)
            {
                var componentType = world.Components.GetType(components[columnIndex]);
                GetColumnCodec(componentType).Write(writer, chunk, columnIndex, chunk.Count);
            }
        }
    }

    private static int CountPersistedChunks(Archetype archetype)
    {
        var count = 0;
        foreach (var chunk in archetype.Chunks)
        {
            if (chunk.Count > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static void ReadArchetype(
        BinaryReader reader,
        World world,
        ComponentType[] schemaComponentTypes,
        int[] slotVersions)
    {
        var componentCount = reader.ReadInt32();
        var fileOrderedComponentTypes = new ComponentType[componentCount];

        for (var componentIndex = 0; componentIndex < componentCount; componentIndex++)
        {
            var schemaIndex = reader.ReadInt32();
            fileOrderedComponentTypes[componentIndex] = schemaComponentTypes[schemaIndex];
        }

        var archetype = world.GetOrCreateArchetype(new Signature(fileOrderedComponentTypes));
        var chunkCount = reader.ReadInt32();

        for (var chunkOrdinal = 0; chunkOrdinal < chunkCount; chunkOrdinal++)
        {
            var rowCount = reader.ReadInt32();
            var entities = new Entity[rowCount];

            for (var row = 0; row < rowCount; row++)
            {
                var entityId = reader.ReadInt32();
                entities[row] = new Entity(entityId, slotVersions[entityId]);
            }

            var chunk = archetype.ImportSnapshotChunk(entities, out var chunkIndex);

            for (var fileColumnIndex = 0; fileColumnIndex < fileOrderedComponentTypes.Length; fileColumnIndex++)
            {
                var runtimeComponentType = fileOrderedComponentTypes[fileColumnIndex];
                var runtimeColumnIndex = archetype.GetComponentIndex(runtimeComponentType);
                var runtimeType = world.Components.GetType(runtimeComponentType);
                GetColumnCodec(runtimeType).Read(reader, chunk, runtimeColumnIndex, rowCount);
            }

            for (var row = 0; row < entities.Length; row++)
            {
                world.SetSnapshotLocation(entities[row], archetype, chunkIndex, row);
            }
        }
    }

    private static ColumnCodec GetColumnCodec(Type componentType)
    {
        return ColumnCodecs.GetOrAdd(componentType, static type =>
        {
            var writeMethod = typeof(WorldSnapshot)
                .GetMethod(nameof(WriteColumnPayload), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type);
            var readMethod = typeof(WorldSnapshot)
                .GetMethod(nameof(ReadColumnPayload), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type);

            return new ColumnCodec(
                (ColumnWriter)Delegate.CreateDelegate(typeof(ColumnWriter), writeMethod),
                (ColumnReader)Delegate.CreateDelegate(typeof(ColumnReader), readMethod));
        });
    }

    private static void WriteColumnPayload<T>(BinaryWriter writer, Chunk chunk, int columnIndex, int count)
        where T : unmanaged
    {
        chunk.WriteColumnTo<T>(writer, columnIndex, count);
    }

    private static void ReadColumnPayload<T>(BinaryReader reader, Chunk chunk, int columnIndex, int count)
        where T : unmanaged
    {
        chunk.ReadColumnFrom<T>(reader, columnIndex, count);
    }

    private static string GetSchemaName(Type componentType)
    {
        return componentType.AssemblyQualifiedName ?? componentType.FullName ?? componentType.Name;
    }

    private static void EnsureSnapshotSupported(Type componentType)
    {
        if (TryFindManagedMemberType(componentType, out var managedType))
        {
            throw new NotSupportedException(
                $"Component {componentType.FullName ?? componentType.Name} is not supported by WorldSnapshot because it contains managed member type {managedType.Name.ToLowerInvariant()}.");
        }

        _ = GetColumnCodec(componentType);
    }

    private static bool TryFindManagedMemberType(Type type, out Type managedType)
    {
        if (!type.IsValueType)
        {
            managedType = type;
            return true;
        }

        if (type.IsPrimitive || type.IsEnum || type.IsPointer)
        {
            managedType = null!;
            return false;
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var fieldType = field.FieldType;
            if (!fieldType.IsValueType)
            {
                managedType = fieldType;
                return true;
            }

            if (TryFindManagedMemberType(fieldType, out managedType))
            {
                return true;
            }
        }

        managedType = null!;
        return false;
    }

    private delegate void ColumnWriter(BinaryWriter writer, Chunk chunk, int columnIndex, int count);

    private delegate void ColumnReader(BinaryReader reader, Chunk chunk, int columnIndex, int count);

    private sealed record ColumnCodec(ColumnWriter Write, ColumnReader Read);

    private sealed record SchemaEntry(Type ComponentType, string SchemaName, int SchemaIndex);
}
