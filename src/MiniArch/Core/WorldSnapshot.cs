using System.Collections.Concurrent;
using System.IO.Hashing;
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
    private const int FormatVersion = 4;

    private static readonly ConcurrentDictionary<Type, ColumnCodec> ColumnCodecs = new();

    [ThreadStatic] private static List<(Entity Entity, Archetype Archetype, int Row)>? _csEntries;
    [ThreadStatic] private static List<(int ChildId, int ParentId)>? _csRelations;

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

        // Buffer body in MemoryStream so we can compute CRC32 before writing
        // the final stream. The body is everything after the header (magic+version).
        using var bodyStream = new MemoryStream();
        using var bodyWriter = new BinaryWriter(bodyStream, Encoding.UTF8, leaveOpen: true);

        bodyWriter.Write(world.ChunkCapacity);
        bodyWriter.Write(world.EntitySlotCount);
        bodyWriter.Write(schemaEntries.Count);
        bodyWriter.Write(persistedArchetypes.Count);
        bodyWriter.Write(world.Hierarchy.CountLiveRelations(world));

        foreach (var record in world.EntityRecords)
        {
            bodyWriter.Write(record.Version);
        }

        foreach (var schemaEntry in schemaEntries)
        {
            ComponentSchemaCodec.WriteSchemaName(bodyWriter, schemaEntry.SchemaName);
        }

        foreach (var archetype in persistedArchetypes)
        {
            WriteArchetype(bodyWriter, world, archetype, schemaEntries);
        }

        foreach (var (child, parent) in world.Hierarchy.EnumerateLiveRelations(world))
        {
            bodyWriter.Write(child.Id);
            bodyWriter.Write(parent.Id);
        }

        world.WriteFreeList(bodyWriter);
        bodyWriter.Flush();

        // Compute CRC32 over body bytes (avoid ToArray() copy — use GetBuffer directly)
        var bodyBuffer = bodyStream.GetBuffer();
        var bodyLength = checked((int)bodyStream.Length);
        var crc = Crc32.HashToUInt32(new ReadOnlySpan<byte>(bodyBuffer, 0, bodyLength));

        // Write header + body + CRC to output stream
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(bodyBuffer, 0, bodyLength);
        writer.Write(crc);
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

        // Read entire stream into a byte array
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var snapshotBytes = ms.ToArray();
        var snapshotOffset = 0;

        // Read magic + version using BinaryReader semantics (little-endian)
        if (snapshotBytes.Length < 8)
            throw new InvalidDataException(
                $"Snapshot data is too short ({snapshotBytes.Length} bytes). " +
                $"Expected at least 8 bytes for magic header + format version.");
        var magic = BitConverter.ToInt32(snapshotBytes, snapshotOffset); snapshotOffset += 4;
        if (magic != Magic)
            throw new InvalidDataException("Snapshot magic header does not match MiniArch snapshot format.");
        var formatVersion = BitConverter.ToInt32(snapshotBytes, snapshotOffset); snapshotOffset += 4;

        if (formatVersion < 3 || formatVersion > FormatVersion)
            throw new InvalidDataException($"Unsupported snapshot format version {formatVersion}.");

        byte[] bodyBytes;
        if (formatVersion >= 4)
        {
            var bodyLen = snapshotBytes.Length - snapshotOffset - sizeof(uint);
            if (bodyLen < 0)
                throw new InvalidDataException("Snapshot too short for v4 format (missing CRC32 trailer).");

            bodyBytes = new byte[bodyLen];
            Buffer.BlockCopy(snapshotBytes, snapshotOffset, bodyBytes, 0, bodyLen);

            var crcOffset = snapshotOffset + bodyLen;
            var storedCrc = BitConverter.ToUInt32(snapshotBytes, crcOffset);

            var computedCrc = Crc32.HashToUInt32(bodyBytes);
            if (computedCrc != storedCrc)
            {
                throw new InvalidDataException(
                    $"WorldSnapshot corrupted: CRC mismatch at offset {snapshotOffset}. " +
                    $"Expected 0x{storedCrc:X8}, computed 0x{computedCrc:X8}.");
            }
        }
        else
        {
            // v3: no CRC, body is the rest of the stream
            bodyBytes = new byte[snapshotBytes.Length - snapshotOffset];
            Buffer.BlockCopy(snapshotBytes, snapshotOffset, bodyBytes, 0, bodyBytes.Length);
        }

        try
        {
            // Parse body bytes using BinaryReader over a MemoryStream
            using var bodyStream = new MemoryStream(bodyBytes);
            using var reader = new BinaryReader(bodyStream, Encoding.UTF8, leaveOpen: true);

            var chunkCapacity = reader.ReadInt32();
            var entitySlotCount = reader.ReadInt32();

            if (chunkCapacity <= 0)
                throw new InvalidDataException($"Snapshot chunk capacity ({chunkCapacity}) must be positive.");

            // Prevent OOM from malicious snapshots specifying a huge slot count.
            // 256M slots × 4 bytes/slot = ~1 GB for slot versions array, which is
            // a practical upper bound for any realistic game world.
            const int maxReasonableSlots = 256 * 1024 * 1024;
            if (entitySlotCount < 0 || entitySlotCount > maxReasonableSlots)
                throw new InvalidDataException(
                    $"Snapshot entity slot count ({entitySlotCount}) is out of range. " +
                    $"Maximum allowed is {maxReasonableSlots}.");

            var schemaCount = reader.ReadInt32();
            var archetypeCount = reader.ReadInt32();
            var hierarchyLinkCount = reader.ReadInt32();

            // Prevent OOM from malicious snapshots with excessive schema count.
            // 64K component types is far beyond any realistic game; each entry
            // would allocate a resolved Type reference in the schemaTypes array.
            const int maxReasonableSchemas = 65536;
            if (schemaCount < 0 || schemaCount > maxReasonableSchemas)
                throw new InvalidDataException(
                    $"Snapshot schema count ({schemaCount}) is out of range. " +
                    $"Maximum allowed is {maxReasonableSchemas}.");

            // Prevent OOM from malicious snapshots with excessive archetype count.
            // 256K archetypes × minimum overhead ~64 bytes = ~16 MB — beyond any
            // realistic component combinatorics.
            const int maxReasonableArchetypes = 262144;
            if (archetypeCount < 0 || archetypeCount > maxReasonableArchetypes)
                throw new InvalidDataException(
                    $"Snapshot archetype count ({archetypeCount}) is out of range. " +
                    $"Maximum allowed is {maxReasonableArchetypes}.");

            // Prevent OOM from malicious snapshots with excessive hierarchy links.
            // Each link stores 2 ints; 256M links at 8 bytes/link = 2 GB worst case
            // for the slot versions array alone. Cap at entitySlotCount (already bounded).
            if (hierarchyLinkCount < 0 || hierarchyLinkCount > entitySlotCount)
                throw new InvalidDataException(
                    $"Snapshot hierarchy link count ({hierarchyLinkCount}) is out of range. " +
                    $"Maximum allowed is {entitySlotCount} (entity slot count).");

            var slotVersions = new int[entitySlotCount];
            for (var index = 0; index < slotVersions.Length; index++)
            {
                slotVersions[index] = reader.ReadInt32();
            }

            var schemaTypes = new Type[schemaCount];
            var seenSchemaNames = new HashSet<string>(StringComparer.Ordinal);
            var seenSchemaTypes = new HashSet<Type>();
            for (var index = 0; index < schemaTypes.Length; index++)
            {
                var schemaName = ComponentSchemaCodec.ReadSchemaName(reader, nameof(WorldSnapshot));
                if (!seenSchemaNames.Add(schemaName))
                    throw new InvalidDataException($"Duplicate schema type name in WorldSnapshot: '{schemaName}'.");

                var componentType = ComponentSchemaCodec.ResolveSchemaType(schemaName, nameof(WorldSnapshot));
                if (!seenSchemaTypes.Add(componentType))
                {
                    throw new InvalidDataException(
                        $"Duplicate component type in WorldSnapshot after resolution: " +
                        $"'{schemaName}' resolves to '{ComponentSchemaCodec.GetDisplayName(componentType)}'.");
                }

                ComponentSchemaCodec.EnsureImportableComponentType(componentType, schemaName, nameof(WorldSnapshot));
                EnsureSnapshotSupported(componentType);
                schemaTypes[index] = componentType;
            }

            var payloadOffset = reader.BaseStream.Position;
            ValidateSnapshotPayload(reader, schemaTypes, slotVersions, archetypeCount, hierarchyLinkCount);
            reader.BaseStream.Position = payloadOffset;

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

            var loadedEntityIds = new HashSet<int>();
            for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
            {
                ReadArchetype(reader, world, schemaComponentTypes, slotVersions, loadedEntityIds);
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
                world.AddChildFromSnapshot(parent, child);
            }

            world.ReadFreeList(reader);
            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new InvalidDataException(
                    $"WorldSnapshot has {reader.BaseStream.Length - reader.BaseStream.Position} " +
                    "unexpected trailing byte(s).");
            }

            return world;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("WorldSnapshot data is truncated or malformed.", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("WorldSnapshot numeric field is too large or malformed.", ex);
        }
    }

    /// <summary>
    /// Computes a deterministic SHA-256 checksum of the live world state.
    /// Hashes non-empty archetypes in signature-sorted order with entity IDs
    /// sorted within each archetype. Does NOT include empty archetypes or
    /// free-list state (use <see cref="ComputeCanonicalChecksum"/> for that).
    /// <para/>
    /// Stable across peers driven by the same delta sequence: peers naturally
    /// share archetype signature order and entity layout. Use to detect state
    /// divergence between lockstep peers.
    /// <para/>
    /// For cross-path comparison (e.g. live world vs snapshot-loaded world),
    /// use <see cref="ComputeCanonicalChecksum"/> which sorts all entities by
    /// id globally and includes the free list.
    /// </summary>
    public static byte[] ComputeChecksum(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var persisted = CollectChecksumArchetypes(world);

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

            var entitySpan = arch.GetEntities();
            var entityCount = entitySpan.Length;
            AppendInt(hash, entityCount);

            var ids = new int[entityCount];
            for (var i = 0; i < entityCount; i++)
                ids[i] = entitySpan[i].Id;
            Array.Sort(ids);

            foreach (var id in ids)
                AppendInt(hash, id);

            for (var col = 0; col < sig.Length; col++)
            {
                var feeder = new HashFeeder(hash);
                arch.FeedColumnData(col, entityCount, ref feeder);
            }
        }

        AppendHierarchyRelations(hash, world);

        return hash.GetCurrentHash();
    }

    private readonly struct HashFeeder : Archetype.ISpanFeeder
    {
        private readonly IncrementalHash _hash;
        public HashFeeder(IncrementalHash hash) => _hash = hash;
        public void Feed(ReadOnlySpan<byte> span) => _hash.AppendData(span);
    }

    private static void AppendInt(IncrementalHash hash, int v)
    {
        hash.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(ref v)));
    }

    private static void AppendHierarchyRelations(IncrementalHash hash, World world)
    {
        var relations = _csRelations ??= [];
        relations.Clear();
        foreach (var (child, parent) in world.Hierarchy.EnumerateLiveRelations(world))
            relations.Add((child.Id, parent.Id));
        relations.Sort((a, b) => a.ChildId.CompareTo(b.ChildId));

        AppendInt(hash, relations.Count);
        foreach (var (childId, parentId) in relations)
        {
            AppendInt(hash, childId);
            AppendInt(hash, parentId);
        }
    }

    /// <summary>
    /// Computes a canonical SHA-256 checksum of the world's <b>logical state</b>:
    /// alive entities (id + version sorted globally by entity.Id), their
    /// components (type + value), hierarchy relations, and free-list entries
    /// (id + version). Two worlds with the same logical content produce the
    /// same hash regardless of internal layout, slot count, archetype order,
    /// or empty archetypes. Slower than <see cref="ComputeChecksum"/>.
    /// <para/>
    /// Use this when comparing worlds that arrived at the same logical state
    /// via different construction paths (e.g. live world vs snapshot-loaded
    /// world, or two sessions with different entity creation histories).
    /// </summary>
    public static byte[] ComputeCanonicalChecksum(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var entries = _csEntries ??= [];
        entries.Clear();
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
                var feeder = new HashFeeder(hash);
                arch.FeedRowData(col, row, ref feeder);
            }
        }

        AppendHierarchyRelations(hash, world);

        var freeList = world.FreeList;
        AppendInt(hash, freeList.Length);
        for (var i = 0; i < freeList.Length; i++)
        {
            AppendInt(hash, freeList[i].Id);
            AppendInt(hash, freeList[i].Version);
        }

        return hash.GetCurrentHash();
    }

    private static List<Archetype> CollectPersistedArchetypes(World world)
    {
        // Archetypes are collected in signature-sorted order
        // (world.Archetypes = _archetypeSnapshot, sorted by PublishArchetypeSnapshot).
        // Empty archetypes are preserved because they affect future query order.
        var archetypes = new List<Archetype>(world.Archetypes.Length);
        foreach (var archetype in world.Archetypes)
        {
            archetypes.Add(archetype);
        }

        return archetypes;
    }

    /// <summary>
    /// Collects non-empty archetypes in signature-sorted order for checksum computation.
    /// Empty archetypes are excluded because they carry no entity data and may be
    /// transient artifacts (e.g. mutation phases during rollback windows) that
    /// shouldn't affect the checksum identity.
    /// <para/>
    /// Unlike Save (which uses <see cref="CollectPersistedArchetypes"/>), empty
    /// archetypes are filtered here. This is necessary because
    /// <c>World.RestoreState</c> does not remove mutation-created empty archetypes,
    /// so including them would
    /// cause pre-capture and post-restore checksums to differ.
    /// </summary>
    private static List<Archetype> CollectChecksumArchetypes(World world)
    {
        var archetypes = new List<Archetype>(world.Archetypes.Length);
        foreach (var archetype in world.Archetypes)
        {
            if (archetype.EntityCount == 0)
                continue;
            archetypes.Add(archetype);
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
                var componentType = ComponentRegistry.Shared.GetType(components[index]);
                if (!seenTypes.Add(componentType))
                {
                    continue;
                }

                EnsureSnapshotSupported(componentType);
                entries.Add(new SchemaEntry(componentType, ComponentSchemaCodec.GetSchemaName(componentType), -1));
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

    private static void ValidateSnapshotPayload(
        BinaryReader reader,
        Type[] schemaTypes,
        int[] slotVersions,
        int archetypeCount,
        int hierarchyLinkCount)
    {
        var loadedEntityIds = new HashSet<int>();

        for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
        {
            ValidateArchetypePayload(reader, schemaTypes, slotVersions, loadedEntityIds);
        }

        ValidateHierarchyPayload(reader, slotVersions, loadedEntityIds, hierarchyLinkCount);
        ValidateFreeListPayload(reader, slotVersions, loadedEntityIds);

        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException(
                $"WorldSnapshot has {reader.BaseStream.Length - reader.BaseStream.Position} " +
                "unexpected trailing byte(s).");
        }
    }

    private static void ValidateArchetypePayload(
        BinaryReader reader,
        Type[] schemaTypes,
        int[] slotVersions,
        HashSet<int> loadedEntityIds)
    {
        var componentCount = reader.ReadInt32();
        if (componentCount < 0 || componentCount > schemaTypes.Length)
        {
            throw new InvalidDataException(
                $"Snapshot archetype component count ({componentCount}) is out of range [0, {schemaTypes.Length}].");
        }

        var fileOrderedComponentTypes = new Type[componentCount];
        var seenComponentTypes = new HashSet<Type>();
        for (var componentIndex = 0; componentIndex < componentCount; componentIndex++)
        {
            var schemaIndex = reader.ReadInt32();
            if ((uint)schemaIndex >= (uint)schemaTypes.Length)
            {
                throw new InvalidDataException(
                    $"Snapshot archetype schema index {schemaIndex} is out of range [0, {schemaTypes.Length}).");
            }

            var componentType = schemaTypes[schemaIndex];
            if (!seenComponentTypes.Add(componentType))
            {
                throw new InvalidDataException(
                    $"Snapshot archetype contains duplicate component schema index {schemaIndex}.");
            }

            fileOrderedComponentTypes[componentIndex] = componentType;
        }

        var rowCount = reader.ReadInt32();
        if (rowCount < 0 || rowCount > slotVersions.Length)
        {
            throw new InvalidDataException(
                $"Snapshot archetype entity count ({rowCount}) is out of range [0, {slotVersions.Length}].");
        }

        for (var row = 0; row < rowCount; row++)
        {
            var entityId = reader.ReadInt32();
            ValidateLoadedEntityId(entityId, slotVersions, loadedEntityIds);
        }

        for (var fileColumnIndex = 0; fileColumnIndex < fileOrderedComponentTypes.Length; fileColumnIndex++)
        {
            var byteCount = GetColumnPayloadByteCount(rowCount, fileOrderedComponentTypes[fileColumnIndex]);
            SkipBytes(reader, byteCount);
        }
    }

    private static long GetColumnPayloadByteCount(int rowCount, Type componentType)
    {
        return checked((long)rowCount * ComponentSizeCache.GetSize(componentType));
    }

    private static void ValidateLoadedEntityId(int entityId, int[] slotVersions, HashSet<int> loadedEntityIds)
    {
        if ((uint)entityId >= (uint)slotVersions.Length)
            throw new InvalidDataException(
                $"Entity id {entityId} out of range [0, {slotVersions.Length}) in snapshot.");
        if (slotVersions[entityId] <= 0)
            throw new InvalidDataException(
                $"Entity id {entityId} has non-positive version {slotVersions[entityId]} in snapshot slot table.");
        if (!loadedEntityIds.Add(entityId))
            throw new InvalidDataException($"Duplicate entity id {entityId} in snapshot archetype rows.");
    }

    private static void ValidateHierarchyPayload(
        BinaryReader reader,
        int[] slotVersions,
        HashSet<int> loadedEntityIds,
        int hierarchyLinkCount)
    {
        var parentByChild = new Dictionary<int, int>();
        for (var linkIndex = 0; linkIndex < hierarchyLinkCount; linkIndex++)
        {
            var childId = reader.ReadInt32();
            var parentId = reader.ReadInt32();
            ValidateHierarchyEndpoint("child", childId, slotVersions, loadedEntityIds);
            ValidateHierarchyEndpoint("parent", parentId, slotVersions, loadedEntityIds);

            if (childId == parentId)
                throw new InvalidDataException($"Hierarchy relation for entity id {childId} cannot parent an entity to itself.");
            if (parentByChild.ContainsKey(childId))
                throw new InvalidDataException($"Hierarchy child id {childId} appears more than once in snapshot.");
            if (WouldCreateHierarchyCycle(childId, parentId, parentByChild))
                throw new InvalidDataException($"Hierarchy relation child={childId}, parent={parentId} creates a cycle.");

            parentByChild[childId] = parentId;
        }
    }

    private static void ValidateHierarchyEndpoint(
        string role,
        int entityId,
        int[] slotVersions,
        HashSet<int> loadedEntityIds)
    {
        if ((uint)entityId >= (uint)slotVersions.Length)
            throw new InvalidDataException(
                $"Hierarchy {role} id {entityId} out of range [0, {slotVersions.Length}) in snapshot.");
        if (!loadedEntityIds.Contains(entityId))
            throw new InvalidDataException($"Hierarchy {role} id {entityId} is not a live entity in snapshot.");
    }

    private static bool WouldCreateHierarchyCycle(int childId, int parentId, Dictionary<int, int> parentByChild)
    {
        var current = parentId;
        while (true)
        {
            if (current == childId)
                return true;
            if (!parentByChild.TryGetValue(current, out current))
                return false;
        }
    }

    private static void ValidateFreeListPayload(
        BinaryReader reader,
        int[] slotVersions,
        HashSet<int> loadedEntityIds)
    {
        var freeIdCount = reader.ReadInt32();
        if (freeIdCount < 0 || freeIdCount > slotVersions.Length)
        {
            throw new InvalidDataException(
                $"Snapshot free-list count ({freeIdCount}) is out of range [0, {slotVersions.Length}].");
        }

        var seenFreeIds = new HashSet<int>();
        for (var i = 0; i < freeIdCount; i++)
        {
            var id = reader.ReadInt32();
            var version = reader.ReadInt32();
            if ((uint)id >= (uint)slotVersions.Length)
                throw new InvalidDataException(
                    $"Corrupt snapshot: free-list entity id {id} is out of range " +
                    $"(entity slot count: {slotVersions.Length}).");
            if (!seenFreeIds.Add(id))
                throw new InvalidDataException($"Duplicate free-list entity id {id} in snapshot.");
            if (loadedEntityIds.Contains(id))
                throw new InvalidDataException($"Free-list entity id {id} is also present as a live entity in snapshot.");
            if (version <= 0)
                throw new InvalidDataException($"Free-list entity id {id} has non-positive version {version} in snapshot.");
            if (version != slotVersions[id])
                throw new InvalidDataException(
                    $"Free-list entity id {id} version {version} does not match slot version {slotVersions[id]}.");
        }
    }

    private static void SkipBytes(BinaryReader reader, long byteCount)
    {
        var stream = reader.BaseStream;
        var remaining = stream.Length - stream.Position;
        if (byteCount > remaining)
        {
            throw new InvalidDataException(
                $"Snapshot component payload is truncated: expected {byteCount} byte(s), got {remaining}.");
        }

        stream.Position += byteCount;
    }

    private static void ReadArchetype(
        BinaryReader reader,
        World world,
        ComponentType[] schemaComponentTypes,
        int[] slotVersions,
        HashSet<int> loadedEntityIds)
    {
        var componentCount = reader.ReadInt32();
        if (componentCount < 0 || componentCount > schemaComponentTypes.Length)
        {
            throw new InvalidDataException(
                $"Snapshot archetype component count ({componentCount}) is out of range [0, {schemaComponentTypes.Length}].");
        }

        var fileOrderedComponentTypes = new ComponentType[componentCount];
        var seenComponentTypes = new HashSet<ComponentType>();

        for (var componentIndex = 0; componentIndex < componentCount; componentIndex++)
        {
            var schemaIndex = reader.ReadInt32();
            if ((uint)schemaIndex >= (uint)schemaComponentTypes.Length)
            {
                throw new InvalidDataException(
                    $"Snapshot archetype schema index {schemaIndex} is out of range [0, {schemaComponentTypes.Length}).");
            }

            var componentType = schemaComponentTypes[schemaIndex];
            if (!seenComponentTypes.Add(componentType))
            {
                throw new InvalidDataException(
                    $"Snapshot archetype contains duplicate component schema index {schemaIndex}.");
            }

            fileOrderedComponentTypes[componentIndex] = componentType;
        }

        var archetype = world.GetOrCreateArchetype(new Signature(fileOrderedComponentTypes));
        var rowCount = reader.ReadInt32();

        if (rowCount < 0 || rowCount > slotVersions.Length)
        {
            throw new InvalidDataException(
                $"Snapshot archetype entity count ({rowCount}) is out of range [0, {slotVersions.Length}].");
        }

        var entities = new Entity[rowCount];

        for (var row = 0; row < rowCount; row++)
        {
            var entityId = reader.ReadInt32();
            ValidateLoadedEntityId(entityId, slotVersions, loadedEntityIds);
            entities[row] = new Entity(entityId, slotVersions[entityId]);
        }

        var startRow = archetype.AllocateRows(rowCount);
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

    private static void EnsureSnapshotSupported(Type componentType)
    {
        if (componentType.ContainsGenericParameters || componentType.IsByRefLike)
        {
            throw new NotSupportedException(
                $"Component {ComponentSchemaCodec.GetDisplayName(componentType)} is not supported by WorldSnapshot because it does not satisfy the unmanaged component constraint.");
        }

        if (ComponentSchemaCodec.TryFindManagedMemberType(componentType, out var managedType))
        {
            throw new NotSupportedException(
                $"Component {ComponentSchemaCodec.GetDisplayName(componentType)} is not supported by WorldSnapshot because it contains managed member type {ComponentSchemaCodec.GetDisplayName(managedType)}.");
        }

        if (!ComponentSchemaCodec.SatisfiesUnmanagedConstraint(componentType))
        {
            throw new NotSupportedException(
                $"Component {ComponentSchemaCodec.GetDisplayName(componentType)} is not supported by WorldSnapshot because it does not satisfy the unmanaged component constraint.");
        }

        _ = GetColumnCodec(componentType);
    }

    private delegate void ColumnWriter(BinaryWriter writer, Archetype archetype, int columnIndex, ReadOnlySpan<int> sortedRows);

    private delegate void ColumnReader(BinaryReader reader, Archetype archetype, int columnIndex, int count);

    private sealed record ColumnCodec(ColumnWriter Write, ColumnReader Read);

    private sealed record SchemaEntry(Type ComponentType, string SchemaName, int SchemaIndex);
}
