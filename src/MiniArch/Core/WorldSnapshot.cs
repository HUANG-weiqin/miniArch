using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MiniArch.Core;

/// <summary>
/// Persists world state to and from a versioned byte stream (file/network).
/// Designed for <b>cross-process</b> scenarios: save to disk, send over the
/// network, checksum for lockstep verification. Uses versioned encoding
/// (<see cref="FormatVersion"/>) with schema tables, archetype mapping,
/// entity-id remapping, and signed checksums.
/// <para/>
/// <b>NOT</b> for high-frequency in-memory rollback (GGPO frame save/restore).
/// For that, use <see cref="WorldStateSnapshot"/> with
/// <see cref="World.CaptureState"/> / <see cref="World.RestoreState"/>,
/// which achieves zero-allocation after warmup by copying raw memory arrays.
/// </summary>
public static class WorldSnapshot
{
    private const int Magic = 0x4D415243;
    private const int FormatVersion = 3;

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

        world.WriteFreeList(writer);
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
            schemaComponentTypes[index] = ComponentRegistry.Shared.GetOrCreate(schemaTypes[index]);
        }

        for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
        {
            ReadArchetype(reader, world, schemaComponentTypes, slotVersions);
        }

        for (var linkIndex = 0; linkIndex < hierarchyLinkCount; linkIndex++)
        {
            var childId = reader.ReadInt32();
            var parentId = reader.ReadInt32();
            if ((uint)childId >= (uint)slotVersions.Length)
                throw new InvalidDataException(
                    $"Hierarchy child id {childId} out of range [0, {slotVersions.Length}) in snapshot.");
            if ((uint)parentId >= (uint)slotVersions.Length)
                throw new InvalidDataException(
                    $"Hierarchy parent id {parentId} out of range [0, {slotVersions.Length}) in snapshot.");
            var child = new Entity(childId, slotVersions[childId]);
            var parent = new Entity(parentId, slotVersions[parentId]);
            world.LinkSnapshot(parent, child);
        }

        world.ReadFreeList(reader);
        return world;
    }

    /// <summary>
    /// Computes a deterministic SHA-256 checksum of the world state.
    /// For lockstep scenarios (same delta sequence replayed on all peers)
    /// this is stable: archetype creation order, swap-remove history, and
    /// slot allocation all match between peers driven by identical inputs.
    /// Use to detect state divergence between peers.
    /// </summary>
    public static byte[] ComputeChecksum(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var persisted = CollectPersistedArchetypes(world);

        AppendInt(hash, world.EntitySlotCount);
        AppendInt(hash, persisted.Count);

        foreach (var rec in world.EntityRecords)
            AppendInt(hash, rec.Version);

        foreach (var arch in persisted)
        {
            var sig = arch.Signature.AsSpan();
            AppendInt(hash, sig.Length);
            foreach (var ct in sig)
                AppendInt(hash, ct.Value);

            var entities = arch.GetEntities().ToArray();
            var entityCount = entities.Length;
            AppendInt(hash, entityCount);

            var rows = new int[entityCount];
            for (var i = 0; i < entityCount; i++) rows[i] = i;
            Array.Sort(rows, (a, b) => entities[a].Id.CompareTo(entities[b].Id));

            foreach (var r in rows)
                AppendInt(hash, entities[r].Id);

            for (var col = 0; col < sig.Length; col++)
                arch.FeedColumnData(col, entityCount, span => hash.AppendData(span));
        }

        var links = new List<(int ChildId, int ParentId)>();
        foreach (var (child, parent) in world.Hierarchy.EnumerateLiveLinks(world))
            links.Add((child.Id, parent.Id));
        links.Sort((a, b) => a.ChildId.CompareTo(b.ChildId));

        AppendInt(hash, links.Count);
        foreach (var (childId, parentId) in links)
        {
            AppendInt(hash, childId);
            AppendInt(hash, parentId);
        }

        return hash.GetCurrentHash();
    }

    private static void AppendInt(IncrementalHash hash, int v)
    {
        hash.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(ref v)));
    }

    /// <summary>
    /// Computes a canonical SHA-256 checksum of the world's <b>logical state</b>:
    /// alive entities (id + version), their components (type + value), and
    /// hierarchy links. Two worlds with the same logical content produce the
    /// same hash regardless of internal layout, free-list contents, slot count,
    /// or archetype organisation. Slower than <see cref="ComputeChecksum"/>.
    /// </summary>
    public static byte[] ComputeCanonicalChecksum(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var entries = new List<(Entity Entity, Archetype Archetype, int Row)>();
        foreach (var arch in world.Archetypes)
        {
            if (arch.EntityCount == 0) continue;
            var entities = arch.GetEntities();
            for (var row = 0; row < entities.Length; row++)
                entries.Add((entities[row], arch, row));
        }
        entries.Sort((a, b) => a.Entity.Id.CompareTo(b.Entity.Id));

        AppendInt(hash, entries.Count);
        foreach (var (entity, arch, row) in entries)
        {
            AppendInt(hash, entity.Id);
            AppendInt(hash, entity.Version);

            var sig = arch.Signature.AsSpan();
            AppendInt(hash, sig.Length);
            for (var col = 0; col < sig.Length; col++)
            {
                AppendInt(hash, sig[col].Value);
                arch.FeedRowData(col, row, span => hash.AppendData(span));
            }
        }

        var links = new List<(int ChildId, int ParId)>();
        foreach (var (child, parent) in world.Hierarchy.EnumerateLiveLinks(world))
            links.Add((child.Id, parent.Id));
        links.Sort((a, b) => a.ChildId.CompareTo(b.ChildId));

        AppendInt(hash, links.Count);
        foreach (var (cid, pid) in links)
        {
            AppendInt(hash, cid);
            AppendInt(hash, pid);
        }

        return hash.GetCurrentHash();
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

        archetypes.Sort(CompareArchetypesBySignature);
        return archetypes;
    }

    private static int CompareArchetypesBySignature(Archetype a, Archetype b)
    {
        var sa = a.Signature.AsSpan();
        var sb = b.Signature.AsSpan();
        var n = Math.Min(sa.Length, sb.Length);
        for (var i = 0; i < n; i++)
        {
            var cmp = sa[i].Value.CompareTo(sb[i].Value);
            if (cmp != 0) return cmp;
        }
        return sa.Length.CompareTo(sb.Length);
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
                var componentType = ComponentRegistry.Shared.GetType(components[index]);
                if (!seenTypes.Add(componentType))
                {
                    continue;
                }

                EnsureSnapshotSupported(componentType);
                entries.Add(new SchemaEntry(componentType, GetSchemaName(componentType), -1));
            }
        }

        var reg = ComponentRegistry.Shared;
        entries.Sort((left, right) => reg.GetOrCreate(left.ComponentType).Value.CompareTo(reg.GetOrCreate(right.ComponentType).Value));

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
            var runtimeType = ComponentRegistry.Shared.GetType(components[index]);
            writer.Write(schemaIndexByType[runtimeType]);
        }

        var entities = archetype.GetEntities().ToArray();
        var entityCount = entities.Length;
        writer.Write(entityCount);

        // Sort row indices by entity.Id so the byte layout is canonical and
        // independent of internal swap-remove artifacts.
        var sortedRows = new int[entityCount];
        for (var i = 0; i < entityCount; i++) sortedRows[i] = i;
        Array.Sort(sortedRows, (a, b) => entities[a].Id.CompareTo(entities[b].Id));

        foreach (var row in sortedRows)
            writer.Write(entities[row].Id);

        for (var columnIndex = 0; columnIndex < components.Length; columnIndex++)
        {
            var componentType = ComponentRegistry.Shared.GetType(components[columnIndex]);
            GetColumnCodec(componentType).Write(writer, archetype, columnIndex, sortedRows);
        }
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
        var rowCount = reader.ReadInt32();

        if (rowCount < 0)
            throw new InvalidDataException($"Negative entity count {rowCount} in snapshot.");

        var entities = new Entity[rowCount];

        for (var row = 0; row < rowCount; row++)
        {
            var entityId = reader.ReadInt32();
            if ((uint)entityId >= (uint)slotVersions.Length)
                throw new InvalidDataException(
                    $"Entity id {entityId} out of range [0, {slotVersions.Length}) in snapshot.");
            entities[row] = new Entity(entityId, slotVersions[entityId]);
        }

        var startRow = archetype.ReserveRows(rowCount);
        for (var row = 0; row < rowCount; row++)
            archetype.WriteEntityAt(startRow + row, entities[row]);

        for (var fileColumnIndex = 0; fileColumnIndex < fileOrderedComponentTypes.Length; fileColumnIndex++)
        {
            var runtimeComponentType = fileOrderedComponentTypes[fileColumnIndex];
            var runtimeColumnIndex = archetype.GetComponentIndex(runtimeComponentType);
            var runtimeType = ComponentRegistry.Shared.GetType(runtimeComponentType);
            GetColumnCodec(runtimeType).Read(reader, archetype, runtimeColumnIndex, rowCount);
        }

        for (var row = 0; row < entities.Length; row++)
        {
            world.SetSnapshotLocation(entities[row], archetype, startRow + row);
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

    private static void WriteColumnPayload<T>(BinaryWriter writer, Archetype archetype, int columnIndex, ReadOnlySpan<int> sortedRows)
        where T : unmanaged
    {
        archetype.WriteColumnOrderedTo(writer, columnIndex, sortedRows);
    }

    private static void ReadColumnPayload<T>(BinaryReader reader, Archetype archetype, int columnIndex, int count)
        where T : unmanaged
    {
        archetype.ReadColumnFrom(reader, columnIndex, count);
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

    private delegate void ColumnWriter(BinaryWriter writer, Archetype archetype, int columnIndex, ReadOnlySpan<int> sortedRows);

    private delegate void ColumnReader(BinaryReader reader, Archetype archetype, int columnIndex, int count);

    private sealed record ColumnCodec(ColumnWriter Write, ColumnReader Read);

    private sealed record SchemaEntry(Type ComponentType, string SchemaName, int SchemaIndex);
}
