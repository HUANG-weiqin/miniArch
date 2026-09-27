using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MiniArch.Core;

/// <summary>
/// Persists world state to and from a canonical, versioned byte stream.
/// </summary>
/// <remarks>
/// This format is intended for persistence and cross-process transfer. For
/// high-frequency in-memory rollback, use <see cref="WorldStateSnapshot"/>.
/// </remarks>
public static class WorldSnapshot
{
    private const int Magic = 0x4D415243;
    private const int FormatVersion = 5;
    private const int MaxReasonableSlots = 256 * 1024 * 1024;
    private const int MaxReasonableSchemas = 65_536;
    private const int MaxReasonableArchetypes = 262_144;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly ConcurrentDictionary<Type, Lazy<ComponentPlan>> ComponentPlans = new();

    /// <summary>
    /// Writes a version-5 canonical world snapshot followed by its CRC32 trailer.
    /// </summary>
    public static void Save(Stream stream, World world)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        if (!stream.CanWrite)
            throw new ArgumentException("Snapshot stream must be writable.", nameof(stream));

        var crc = new Crc32();
        var sink = new StreamCrcSink(stream, crc);
        WriteCanonicalWorld(ref sink, world);

        Span<byte> trailer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, crc.GetCurrentHashAsUInt32());
        stream.Write(trailer);
    }

    /// <summary>
    /// Reads a version-5 canonical world snapshot.
    /// </summary>
    public static World Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Snapshot stream must be readable.", nameof(stream));

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var length = checked((int)copy.Length);
        if (length < 12)
            throw new InvalidDataException("WorldSnapshot is truncated before the v5 header and CRC32 trailer.");

        var bytes = new ReadOnlySpan<byte>(copy.GetBuffer(), 0, length);
        var magic = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (magic != Magic)
            throw new InvalidDataException("Snapshot magic header does not match MiniArch snapshot format.");

        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(int)..]);
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported snapshot format version {version}; only version {FormatVersion} is accepted.");

        var payload = bytes[..^sizeof(uint)];
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^sizeof(uint)..]);
        var computedCrc = Crc32.HashToUInt32(payload);
        if (storedCrc != computedCrc)
        {
            throw new InvalidDataException(
                $"WorldSnapshot corrupted: CRC mismatch. Expected 0x{storedCrc:X8}, computed 0x{computedCrc:X8}.");
        }

        try
        {
            var validated = ValidateCanonicalSnapshot(payload);
            return ConstructWorld(payload, validated);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("WorldSnapshot numeric field is too large or malformed.", ex);
        }
    }

    /// <summary>
    /// Computes the legacy lockstep projection checksum. It includes slot
    /// versions, non-empty archetypes, live hierarchy, and component values,
    /// but excludes chunk capacity, empty archetypes, and the free list.
    /// </summary>
    public static byte[] ComputeChecksum(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sink = new HashSink(hash);
        var archetypes = BuildChecksumArchetypes(world);

        WriteInt32(ref sink, world.EntitySlotCount);
        WriteInt32(ref sink, archetypes.Count);
        foreach (var record in world.EntityRecords)
            WriteInt32(ref sink, record.Version);

        foreach (var entry in archetypes)
        {
            WriteInt32(ref sink, entry.Columns.Length);
            for (var index = 0; index < entry.Columns.Length; index++)
                WriteLengthPrefixedBytes(ref sink, entry.Columns[index].Schema.Plan.IdentityBytes);

            var entityCount = entry.Archetype.EntityCount;
            WriteInt32(ref sink, entityCount);
            // Preserve physical-row sensitivity while keeping every component value
            // associated with the entity in the same row.
            for (var row = 0; row < entityCount; row++)
                WriteInt32(ref sink, entry.Archetype.GetEntity(row).Id);

            for (var column = 0; column < entry.Columns.Length; column++)
            {
                var component = entry.Columns[column];
                for (var row = 0; row < entityCount; row++)
                {
                    component.Schema.Plan.WriteValue(
                        ref sink,
                        entry.Archetype.GetComponentBytes(component.RuntimeColumnIndex, row));
                }
            }
        }

        WriteHierarchy(ref sink, world);
        return hash.GetCurrentHash();
    }

    /// <summary>
    /// Computes SHA256 over the exact canonical version-5 snapshot payload,
    /// including its magic and version but excluding the CRC32 trailer.
    /// </summary>
    public static byte[] ComputeCanonicalChecksum(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sink = new HashSink(hash);
        WriteCanonicalWorld(ref sink, world);
        return hash.GetCurrentHash();
    }

    private static void WriteCanonicalWorld<TSink>(ref TSink sink, World world)
        where TSink : struct, IByteSink
    {
        // Validate and sort persisted non-empty archetypes before Save emits
        // its first byte. The resulting model contains metadata only, never a
        // materialized snapshot payload.
        var layout = BuildCanonicalLayout(world);

        WriteInt32(ref sink, Magic);
        WriteInt32(ref sink, FormatVersion);
        WriteInt32(ref sink, world.ChunkCapacity);
        WriteInt32(ref sink, world.EntitySlotCount);
        WriteInt32(ref sink, layout.Schemas.Length);
        WriteInt32(ref sink, layout.Archetypes.Length);
        WriteInt32(ref sink, layout.Relations.Length);

        foreach (var record in world.EntityRecords)
            WriteInt32(ref sink, record.Version);

        for (var index = 0; index < layout.Schemas.Length; index++)
        {
            var schema = layout.Schemas[index];
            WriteLengthPrefixedBytes(ref sink, schema.Plan.IdentityBytes);
            WriteLengthPrefixedBytes(ref sink, schema.Plan.SchemaShapeBytes);
        }

        for (var index = 0; index < layout.Archetypes.Length; index++)
            WriteCanonicalArchetype(ref sink, layout.Archetypes[index]);

        for (var index = 0; index < layout.Relations.Length; index++)
        {
            WriteInt32(ref sink, layout.Relations[index].ChildId);
            WriteInt32(ref sink, layout.Relations[index].ParentId);
        }

        var freeList = world.FreeList;
        WriteInt32(ref sink, freeList.Length);
        for (var index = 0; index < freeList.Length; index++)
            WriteInt32(ref sink, freeList[index].Id);
    }

    private static void WriteCanonicalArchetype<TSink>(ref TSink sink, CanonicalArchetype entry)
        where TSink : struct, IByteSink
    {
        WriteInt32(ref sink, entry.Columns.Length);
        for (var index = 0; index < entry.Columns.Length; index++)
            WriteInt32(ref sink, entry.Columns[index].Schema.SchemaIndex);

        WriteInt32(ref sink, entry.SortedRows.Length);
        for (var index = 0; index < entry.SortedRows.Length; index++)
            WriteInt32(ref sink, entry.Archetype.GetEntity(entry.SortedRows[index]).Id);

        for (var column = 0; column < entry.Columns.Length; column++)
        {
            var component = entry.Columns[column];
            for (var row = 0; row < entry.SortedRows.Length; row++)
            {
                component.Schema.Plan.WriteValue(
                    ref sink,
                    entry.Archetype.GetComponentBytes(component.RuntimeColumnIndex, entry.SortedRows[row]));
            }
        }
    }

    private static CanonicalLayout BuildCanonicalLayout(World world)
    {
        var archetypes = world.Archetypes;
        var types = new HashSet<Type>();
        var nonEmptyCount = 0;
        for (var archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            var archetype = archetypes[archetypeIndex];
            if (archetype.EntityCount == 0)
                continue;

            nonEmptyCount++;
            var componentTypes = archetype.ComponentTypes;
            for (var componentIndex = 0; componentIndex < componentTypes.Count; componentIndex++)
                types.Add(componentTypes[componentIndex]);
        }

        var schemas = new SchemaEntry[types.Count];
        var schemaOffset = 0;
        foreach (var type in types)
        {
            var plan = GetComponentPlan(type);
            schemas[schemaOffset++] = new SchemaEntry(type, plan, -1);
        }

        Array.Sort(schemas, static (left, right) =>
            StringComparer.Ordinal.Compare(left.Plan.Identity, right.Plan.Identity));
        for (var index = 0; index < schemas.Length; index++)
        {
            if (index > 0 && StringComparer.Ordinal.Equals(schemas[index - 1].Plan.Identity, schemas[index].Plan.Identity))
                throw new NotSupportedException($"WorldSnapshot requires unique stable component identities; '{schemas[index].Plan.Identity}' is duplicated.");
            schemas[index].SchemaIndex = index;
        }

        var schemaByType = new Dictionary<Type, SchemaEntry>(schemas.Length);
        for (var index = 0; index < schemas.Length; index++)
            schemaByType.Add(schemas[index].ComponentType, schemas[index]);

        var canonicalArchetypes = new CanonicalArchetype[nonEmptyCount];
        var canonicalIndex = 0;
        for (var archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            var archetype = archetypes[archetypeIndex];
            if (archetype.EntityCount == 0)
                continue;

            var runtimeSignature = archetype.Signature.AsSpan();
            var componentTypes = archetype.ComponentTypes;
            var columns = new CanonicalColumn[runtimeSignature.Length];
            for (var runtimeColumn = 0; runtimeColumn < runtimeSignature.Length; runtimeColumn++)
                columns[runtimeColumn] = new CanonicalColumn(schemaByType[componentTypes[runtimeColumn]], runtimeColumn);
            Array.Sort(columns, static (left, right) => left.Schema.SchemaIndex.CompareTo(right.Schema.SchemaIndex));

            var rows = new int[archetype.EntityCount];
            for (var row = 0; row < rows.Length; row++)
                rows[row] = row;
            Array.Sort(rows, (left, right) => archetype.GetEntity(left).Id.CompareTo(archetype.GetEntity(right).Id));
            canonicalArchetypes[canonicalIndex++] = new CanonicalArchetype(archetype, columns, rows);
        }

        Array.Sort(canonicalArchetypes, static (left, right) => CompareSignatures(left.Columns, right.Columns));
        for (var index = 1; index < canonicalArchetypes.Length; index++)
        {
            if (CompareSignatures(canonicalArchetypes[index - 1].Columns, canonicalArchetypes[index].Columns) == 0)
                throw new InvalidOperationException("World contains duplicate archetype signatures.");
        }

        var relations = CollectRelations(world);
        return new CanonicalLayout(schemas, canonicalArchetypes, relations);
    }

    private static List<CanonicalArchetype> BuildChecksumArchetypes(World world)
    {
        var result = new List<CanonicalArchetype>();
        foreach (var archetype in world.Archetypes)
        {
            if (archetype.EntityCount == 0)
                continue;

            var componentTypes = archetype.ComponentTypes;
            var columns = new CanonicalColumn[componentTypes.Count];
            for (var column = 0; column < columns.Length; column++)
            {
                var type = componentTypes[column];
                var plan = GetComponentPlan(type);
                columns[column] = new CanonicalColumn(
                    new SchemaEntry(type, plan, -1),
                    column);
            }
            Array.Sort(columns, static (left, right) =>
                StringComparer.Ordinal.Compare(left.Schema.Plan.Identity, right.Schema.Plan.Identity));
            result.Add(new CanonicalArchetype(archetype, columns, Array.Empty<int>()));
        }
        result.Sort(static (left, right) => CompareIdentities(left.Columns, right.Columns));
        return result;
    }

    private static int CompareSignatures(CanonicalColumn[] left, CanonicalColumn[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = left[index].Schema.SchemaIndex.CompareTo(right[index].Schema.SchemaIndex);
            if (comparison != 0)
                return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentities(CanonicalColumn[] left, CanonicalColumn[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = StringComparer.Ordinal.Compare(left[index].Schema.Plan.Identity, right[index].Schema.Plan.Identity);
            if (comparison != 0)
                return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static (int ChildId, int ParentId)[] CollectRelations(World world)
    {
        var relations = new List<(int ChildId, int ParentId)>();
        foreach (var (child, parent) in world.Hierarchy.EnumerateLiveRelations(world))
            relations.Add((child.Id, parent.Id));
        relations.Sort(static (left, right) => left.ChildId.CompareTo(right.ChildId));
        return relations.ToArray();
    }

    private static void WriteHierarchy<TSink>(ref TSink sink, World world)
        where TSink : struct, IByteSink
    {
        var relations = CollectRelations(world);
        WriteInt32(ref sink, relations.Length);
        for (var index = 0; index < relations.Length; index++)
        {
            WriteInt32(ref sink, relations[index].ChildId);
            WriteInt32(ref sink, relations[index].ParentId);
        }
    }

    private static ValidatedSnapshot ValidateCanonicalSnapshot(ReadOnlySpan<byte> payload)
    {
        var reader = new SnapshotReader(payload);
        reader.Skip(2 * sizeof(int), "snapshot header");

        var chunkCapacity = reader.ReadInt32("chunk capacity");
        var slotCount = reader.ReadInt32("entity slot count");
        var schemaCount = reader.ReadInt32("schema count");
        var archetypeCount = reader.ReadInt32("archetype count");
        var hierarchyCount = reader.ReadInt32("hierarchy count");
        ValidateHeaderCounts(chunkCapacity, slotCount, schemaCount, archetypeCount, hierarchyCount);

        var slotBytes = checked((long)slotCount * sizeof(int));
        if (slotBytes > reader.Remaining)
            throw new InvalidDataException($"Snapshot slot version table is truncated: expected {slotBytes} byte(s), got {reader.Remaining}.");

        for (var index = 0; index < slotCount; index++)
        {
            var value = reader.ReadInt32("slot version");
            if (value <= 0)
                throw new InvalidDataException($"Snapshot entity slot {index} has non-positive version {value}.");
        }

        var schemas = new SchemaEntry[schemaCount];
        var resolvedTypes = new HashSet<Type>();
        string? previousIdentity = null;
        for (var index = 0; index < schemas.Length; index++)
        {
            var identity = reader.ReadString("schema identity", ComponentSchemaCodec.MaxSchemaNameUtf8Bytes);
            if (previousIdentity is not null && StringComparer.Ordinal.Compare(previousIdentity, identity) >= 0)
                throw new InvalidDataException("Snapshot schema identities must be strictly increasing in ordinal order.");
            previousIdentity = identity;

            var type = ComponentSchemaCodec.ResolveSchemaType(identity, nameof(WorldSnapshot));
            if (!resolvedTypes.Add(type))
                throw new InvalidDataException($"Duplicate component type in WorldSnapshot after resolution: '{identity}'.");
            ComponentSchemaCodec.EnsureImportableComponentType(type, identity, nameof(WorldSnapshot));

            ComponentPlan plan;
            try
            {
                plan = GetComponentPlan(type);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidDataException($"WorldSnapshot schema '{identity}' is not supported by the canonical component codec.", ex);
            }

            if (!StringComparer.Ordinal.Equals(plan.Identity, identity))
                throw new InvalidDataException($"WorldSnapshot resolved schema identity '{identity}' to a different exact type identity.");

            var shape = reader.ReadLengthPrefixedBytes("schema shape");
            if (!shape.SequenceEqual(plan.SchemaShapeBytes))
                throw new InvalidDataException($"WorldSnapshot schema shape does not match resolved type '{identity}'.");
            schemas[index] = new SchemaEntry(type, plan, index);
        }

        var liveSlots = new bool[slotCount];
        var usedSchemas = new bool[schemaCount];
        int[]? previousSignature = null;
        for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
        {
            var signature = ReadAndValidateSignature(ref reader, schemaCount, usedSchemas);
            if (previousSignature is not null && CompareSignatures(previousSignature, signature) >= 0)
                throw new InvalidDataException("Snapshot archetype signatures must be strictly increasing lexicographically.");
            previousSignature = signature;

            var rowCount = reader.ReadInt32("archetype row count");
            if (rowCount < 0 || rowCount > slotCount)
                throw new InvalidDataException($"Snapshot archetype entity count ({rowCount}) is out of range [0, {slotCount}].");

            var previousEntityId = -1;
            for (var row = 0; row < rowCount; row++)
            {
                var entityId = reader.ReadInt32("entity id");
                if ((uint)entityId >= (uint)slotCount)
                    throw new InvalidDataException($"Entity id {entityId} is out of range [0, {slotCount}) in snapshot.");
                if (entityId <= previousEntityId)
                    throw new InvalidDataException("Snapshot archetype entity ids must be strictly increasing.");
                if (liveSlots[entityId])
                    throw new InvalidDataException($"Duplicate live entity id {entityId} in snapshot.");
                liveSlots[entityId] = true;
                previousEntityId = entityId;
            }

            for (var column = 0; column < signature.Length; column++)
            {
                var plan = schemas[signature[column]].Plan;
                var byteCount = checked((long)rowCount * plan.WireWidth);
                if (byteCount > reader.Remaining)
                    throw new InvalidDataException($"Snapshot component payload is truncated: expected {byteCount} byte(s), got {reader.Remaining}.");
                for (var row = 0; row < rowCount; row++)
                    plan.ValidateValue(ref reader);
            }
        }

        for (var index = 0; index < usedSchemas.Length; index++)
        {
            if (!usedSchemas[index])
                throw new InvalidDataException($"Snapshot schema index {index} is not used by any archetype.");
        }

        ValidateHierarchy(ref reader, hierarchyCount, liveSlots);
        ValidateFreeList(ref reader, slotCount, liveSlots);
        if (reader.Remaining != 0)
            throw new InvalidDataException($"WorldSnapshot has {reader.Remaining} unexpected trailing byte(s).");

        return new ValidatedSnapshot(chunkCapacity, slotCount, schemas);
    }

    private static void ValidateHeaderCounts(
        int chunkCapacity,
        int slotCount,
        int schemaCount,
        int archetypeCount,
        int hierarchyCount)
    {
        if (chunkCapacity <= 0 || chunkCapacity > World.MaxChunkCapacity)
            throw new InvalidDataException($"Snapshot chunk capacity ({chunkCapacity}) is out of range [1, {World.MaxChunkCapacity}].");
        if (slotCount < 0 || slotCount > MaxReasonableSlots)
            throw new InvalidDataException($"Snapshot entity slot count ({slotCount}) is out of range [0, {MaxReasonableSlots}].");
        if (schemaCount < 0 || schemaCount > MaxReasonableSchemas)
            throw new InvalidDataException($"Snapshot schema count ({schemaCount}) is out of range [0, {MaxReasonableSchemas}].");
        if (archetypeCount < 0 || archetypeCount > MaxReasonableArchetypes)
            throw new InvalidDataException($"Snapshot archetype count ({archetypeCount}) is out of range [0, {MaxReasonableArchetypes}].");
        if (hierarchyCount < 0 || hierarchyCount > slotCount)
            throw new InvalidDataException($"Snapshot hierarchy count ({hierarchyCount}) is out of range [0, {slotCount}].");
    }

    private static int[] ReadAndValidateSignature(
        ref SnapshotReader reader,
        int schemaCount,
        bool[] usedSchemas)
    {
        var componentCount = reader.ReadInt32("archetype component count");
        if (componentCount < 0 || componentCount > schemaCount)
            throw new InvalidDataException($"Snapshot archetype component count ({componentCount}) is out of range [0, {schemaCount}].");

        var signature = new int[componentCount];
        var previous = -1;
        for (var index = 0; index < signature.Length; index++)
        {
            var schemaIndex = reader.ReadInt32("archetype schema index");
            if ((uint)schemaIndex >= (uint)schemaCount)
                throw new InvalidDataException($"Snapshot archetype schema index {schemaIndex} is out of range [0, {schemaCount}).");
            if (schemaIndex <= previous)
                throw new InvalidDataException("Snapshot archetype schema indices must be strictly increasing.");
            signature[index] = schemaIndex;
            usedSchemas[schemaIndex] = true;
            previous = schemaIndex;
        }
        return signature;
    }

    private static int CompareSignatures(int[] left, int[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
                return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static void ValidateHierarchy(
        ref SnapshotReader reader,
        int hierarchyCount,
        bool[] liveSlots)
    {
        var parentByChild = new Dictionary<int, int>(hierarchyCount);
        var previousChild = -1;
        for (var index = 0; index < hierarchyCount; index++)
        {
            var child = reader.ReadInt32("hierarchy child id");
            var parent = reader.ReadInt32("hierarchy parent id");
            if (child <= previousChild)
                throw new InvalidDataException("Snapshot hierarchy child ids must be strictly increasing.");
            ValidateHierarchyEndpoint("child", child, liveSlots);
            ValidateHierarchyEndpoint("parent", parent, liveSlots);
            if (child == parent)
                throw new InvalidDataException($"Hierarchy relation for entity id {child} cannot parent an entity to itself.");
            if (WouldCreateHierarchyCycle(child, parent, parentByChild))
                throw new InvalidDataException($"Hierarchy relation child={child}, parent={parent} creates a cycle.");
            parentByChild.Add(child, parent);
            previousChild = child;
        }
    }

    private static void ValidateHierarchyEndpoint(string role, int entityId, bool[] liveSlots)
    {
        if ((uint)entityId >= (uint)liveSlots.Length)
            throw new InvalidDataException($"Hierarchy {role} id {entityId} is out of range [0, {liveSlots.Length}).");
        if (!liveSlots[entityId])
            throw new InvalidDataException($"Hierarchy {role} id {entityId} is not a live entity in snapshot.");
    }

    private static bool WouldCreateHierarchyCycle(int child, int parent, Dictionary<int, int> parentByChild)
    {
        var current = parent;
        while (true)
        {
            if (current == child)
                return true;
            if (!parentByChild.TryGetValue(current, out current))
                return false;
        }
    }

    private static void ValidateFreeList(
        ref SnapshotReader reader,
        int slotCount,
        bool[] liveSlots)
    {
        var count = reader.ReadInt32("free-list count");
        if (count < 0 || count > slotCount)
            throw new InvalidDataException($"Snapshot free-list count ({count}) is out of range [0, {slotCount}].");
        var seen = new bool[slotCount];
        for (var index = 0; index < count; index++)
        {
            var id = reader.ReadInt32("free-list entity id");
            if ((uint)id >= (uint)slotCount)
                throw new InvalidDataException($"Free-list entity id {id} is out of range [0, {slotCount}).");
            if (seen[id])
                throw new InvalidDataException($"Duplicate free-list entity id {id} in snapshot.");
            if (liveSlots[id])
                throw new InvalidDataException($"Free-list entity id {id} is also present as a live entity in snapshot.");
            seen[id] = true;
        }
    }

    private static World ConstructWorld(ReadOnlySpan<byte> payload, ValidatedSnapshot validated)
    {
        var reader = new SnapshotReader(payload);
        reader.Skip(2 * sizeof(int), "snapshot header");
        _ = reader.ReadInt32("chunk capacity");
        _ = reader.ReadInt32("entity slot count");
        var schemaCount = reader.ReadInt32("schema count");
        var archetypeCount = reader.ReadInt32("archetype count");
        var hierarchyCount = reader.ReadInt32("hierarchy count");

        var slotVersions = new int[validated.SlotCount];
        for (var index = 0; index < slotVersions.Length; index++)
            slotVersions[index] = reader.ReadInt32("slot version");
        for (var index = 0; index < schemaCount; index++)
        {
            _ = reader.ReadString("schema identity", ComponentSchemaCodec.MaxSchemaNameUtf8Bytes);
            _ = reader.ReadLengthPrefixedBytes("schema shape");
        }

        var runtimeTypes = new ComponentType[validated.Schemas.Length];
        for (var index = 0; index < runtimeTypes.Length; index++)
            runtimeTypes[index] = ComponentRegistry.Shared.GetOrCreate(validated.Schemas[index].ComponentType);

        World? world = null;
        try
        {
            world = new World(validated.ChunkCapacity, validated.SlotCount);
            world.Reset(validated.SlotCount);
            for (var index = 0; index < slotVersions.Length; index++)
                world.SetSnapshotEntityVersion(index, slotVersions[index]);

            for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
                ConstructArchetype(ref reader, world, validated.Schemas, runtimeTypes, slotVersions);

            for (var index = 0; index < hierarchyCount; index++)
            {
                var childId = reader.ReadInt32("hierarchy child id");
                var parentId = reader.ReadInt32("hierarchy parent id");
                world.AddChildFromSnapshot(
                    new Entity(parentId, slotVersions[parentId]),
                    new Entity(childId, slotVersions[childId]));
            }

            var freeCount = reader.ReadInt32("free-list count");
            var freeIds = new int[freeCount];
            for (var index = 0; index < freeIds.Length; index++)
                freeIds[index] = reader.ReadInt32("free-list entity id");
            world.SetSnapshotFreeList(freeIds);
            world.RecalculateReservedCount();
            return world;
        }
        catch
        {
            world?.Dispose();
            throw;
        }
    }

    private static void ConstructArchetype(
        ref SnapshotReader reader,
        World world,
        SchemaEntry[] schemas,
        ComponentType[] runtimeTypes,
        int[] slotVersions)
    {
        var componentCount = reader.ReadInt32("archetype component count");
        var schemaIndices = new int[componentCount];
        var signature = new ComponentType[componentCount];
        for (var index = 0; index < componentCount; index++)
        {
            var schemaIndex = reader.ReadInt32("archetype schema index");
            schemaIndices[index] = schemaIndex;
            signature[index] = runtimeTypes[schemaIndex];
        }

        var archetype = world.GetOrCreateArchetype(new Signature(signature));
        var rowCount = reader.ReadInt32("archetype row count");
        var startRow = archetype.AllocateRows(rowCount);
        for (var row = 0; row < rowCount; row++)
        {
            var id = reader.ReadInt32("entity id");
            var entity = new Entity(id, slotVersions[id]);
            archetype.WriteEntityAt(startRow + row, entity);
            world.SetSnapshotLocation(entity, archetype, startRow + row);
        }

        for (var column = 0; column < schemaIndices.Length; column++)
        {
            var schemaIndex = schemaIndices[column];
            var runtimeColumn = archetype.GetComponentIndex(runtimeTypes[schemaIndex]);
            var plan = schemas[schemaIndex].Plan;
            for (var row = 0; row < rowCount; row++)
                plan.ReadValue(ref reader, archetype.GetWritableComponentBytes(runtimeColumn, startRow + row));
        }
    }

    private static ComponentPlan GetComponentPlan(Type componentType)
    {
        return ComponentPlans.GetOrAdd(
            componentType,
            static type => new Lazy<ComponentPlan>(
                () => BuildComponentPlan(type),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static ComponentPlan BuildComponentPlan(Type componentType)
    {
        var identity = GetStableIdentity(componentType);
        var identityBytes = GetStrictUtf8Bytes(identity);
        if (identityBytes.Length > ComponentSchemaCodec.MaxSchemaNameUtf8Bytes)
        {
            throw new NotSupportedException(
                $"WorldSnapshot component type identity '{identity}' exceeds the {ComponentSchemaCodec.MaxSchemaNameUtf8Bytes}-byte UTF-8 limit.");
        }

        var definitions = new Dictionary<Type, TypeDefinition>();
        var definition = BuildTypeDefinition(
            componentType,
            componentType,
            new HashSet<Type>(),
            definitions);
        return new ComponentPlan(identity, identityBytes, definition.ShapeBytes, definition.WireWidth, definition.Leaves);
    }

    private static TypeDefinition BuildTypeDefinition(
        Type type,
        Type rootType,
        HashSet<Type> recursionStack,
        Dictionary<Type, TypeDefinition> definitions)
    {
        if (definitions.TryGetValue(type, out var cached))
            return cached;

        EnsurePlanType(type, rootType);
        var nativeSize = ComponentSizeCache.GetSize(type);
        if (nativeSize <= 0)
            throw new NotSupportedException($"WorldSnapshot type '{GetTypeDisplayName(rootType)}' has invalid CLR size {nativeSize}.");

        if (TryGetPrimitiveKind(type, out var primitiveKind))
        {
            var shape = EncodeRecord(payload =>
            {
                payload.WriteByte((byte)ShapeKind.Primitive);
                payload.WriteByte((byte)primitiveKind);
            });
            var primitive = new TypeDefinition(
                shape,
                PrimitiveWidth(primitiveKind),
                nativeSize,
                [new LeafOp(0, primitiveKind)]);
            definitions.Add(type, primitive);
            return primitive;
        }

        if (type.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(type);
            if (!TryGetPrimitiveKind(underlying, out var underlyingKind) || underlyingKind == PrimitiveKind.Boolean)
                throw new NotSupportedException($"WorldSnapshot enum '{GetTypeDisplayName(type)}' has an unsupported underlying type.");
            var shape = EncodeRecord(payload =>
            {
                payload.WriteByte((byte)ShapeKind.Enum);
                payload.WriteBytes(EncodePrimitiveShape(underlyingKind));
            });
            var enumeration = new TypeDefinition(
                shape,
                PrimitiveWidth(underlyingKind),
                nativeSize,
                [new LeafOp(0, underlyingKind)]);
            definitions.Add(type, enumeration);
            return enumeration;
        }

        if (!recursionStack.Add(type))
            throw new NotSupportedException($"WorldSnapshot type '{GetTypeDisplayName(rootType)}' has a recursive value shape through '{GetTypeDisplayName(type)}'.");
        try
        {
            if (type.StructLayoutAttribute?.Value == LayoutKind.Auto)
                throw new NotSupportedException($"WorldSnapshot type '{GetTypeDisplayName(type)}' uses LayoutKind.Auto.");

            var fields = GetInstanceFields(type);
            var physicalFields = ValidatePhysicalFields(type, fields, nativeSize, rootType);
            var inline = type.GetCustomAttribute<InlineArrayAttribute>(inherit: false);
            TypeDefinition definition;
            if (inline is not null)
            {
                definition = BuildInlineArrayDefinition(
                    type,
                    nativeSize,
                    fields,
                    physicalFields,
                    inline,
                    rootType,
                    recursionStack,
                    definitions);
            }
            else
            {
                definition = BuildStructDefinition(
                    type,
                    nativeSize,
                    fields,
                    physicalFields,
                    rootType,
                    recursionStack,
                    definitions);
            }
            definitions.Add(type, definition);
            return definition;
        }
        finally
        {
            recursionStack.Remove(type);
        }
    }

    private static TypeDefinition BuildInlineArrayDefinition(
        Type type,
        int nativeSize,
        FieldInfo[] fields,
        Dictionary<FieldInfo, PhysicalField> physicalFields,
        InlineArrayAttribute inline,
        Type rootType,
        HashSet<Type> recursionStack,
        Dictionary<Type, TypeDefinition> definitions)
    {
        if (inline.Length <= 0 || fields.Length != 1)
            throw new NotSupportedException($"InlineArray type '{GetTypeDisplayName(type)}' must have a positive length and exactly one instance field.");

        var field = fields[0];
        var physical = physicalFields[field];
        var element = BuildTypeDefinition(
            field.FieldType,
            rootType,
            recursionStack,
            definitions);
        var elementsEnd = checked((long)physical.Offset + (long)inline.Length * element.NativeSize);
        if (elementsEnd > nativeSize)
            throw new NotSupportedException($"InlineArray type '{GetTypeDisplayName(type)}' has elements outside its CLR layout bounds.");

        var leaves = RepeatLeaves(element.Leaves, physical.Offset, inline.Length, element.NativeSize);
        var wireWidth = checked(element.WireWidth * inline.Length);
        var shape = EncodeRecord(payload =>
        {
            payload.WriteByte((byte)ShapeKind.InlineArray);
            payload.WriteString(field.Name);
            payload.WriteInt32(inline.Length);
            payload.WriteString(GetStableIdentity(field.FieldType));
            payload.WriteBytes(element.ShapeBytes);
        });
        return new TypeDefinition(shape, wireWidth, nativeSize, leaves);
    }

    private static TypeDefinition BuildStructDefinition(
        Type type,
        int nativeSize,
        FieldInfo[] fields,
        Dictionary<FieldInfo, PhysicalField> physicalFields,
        Type rootType,
        HashSet<Type> recursionStack,
        Dictionary<Type, TypeDefinition> definitions)
    {
        Array.Sort(fields, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var leaves = new List<LeafOp>();
        var fieldShapes = new byte[fields.Length][];
        var wireWidth = 0;

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var physical = physicalFields[field];
            var fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>(inherit: false);
            if (fixedBuffer is not null)
            {
                if (fixedBuffer.Length <= 0)
                    throw new NotSupportedException($"Fixed buffer field '{GetTypeDisplayName(type)}.{field.Name}' has a non-positive length.");
                var element = BuildTypeDefinition(
                    fixedBuffer.ElementType,
                    rootType,
                    recursionStack,
                    definitions);
                var expandedSize = checked((long)fixedBuffer.Length * element.NativeSize);
                if (expandedSize > physical.Size || (long)physical.Offset + expandedSize > nativeSize)
                    throw new NotSupportedException($"Fixed buffer field '{GetTypeDisplayName(type)}.{field.Name}' exceeds its CLR layout bounds.");
                leaves.AddRange(RepeatLeaves(element.Leaves, physical.Offset, fixedBuffer.Length, element.NativeSize));
                wireWidth = checked(wireWidth + fixedBuffer.Length * element.WireWidth);
                fieldShapes[index] = EncodeRecord(payload =>
                {
                    payload.WriteByte((byte)FieldShapeKind.FixedBuffer);
                    payload.WriteString(field.Name);
                    payload.WriteInt32(fixedBuffer.Length);
                    payload.WriteString(GetStableIdentity(fixedBuffer.ElementType));
                    payload.WriteBytes(element.ShapeBytes);
                });
            }
            else
            {
                var child = BuildTypeDefinition(
                    field.FieldType,
                    rootType,
                    recursionStack,
                    definitions);
                for (var leafIndex = 0; leafIndex < child.Leaves.Length; leafIndex++)
                {
                    leaves.Add(new LeafOp(
                        checked(physical.Offset + child.Leaves[leafIndex].ManagedOffset),
                        child.Leaves[leafIndex].Kind));
                }
                wireWidth = checked(wireWidth + child.WireWidth);
                fieldShapes[index] = EncodeRecord(payload =>
                {
                    payload.WriteByte((byte)FieldShapeKind.Field);
                    payload.WriteString(field.Name);
                    payload.WriteString(GetStableIdentity(field.FieldType));
                    payload.WriteBytes(child.ShapeBytes);
                });
            }
        }

        var shape = EncodeRecord(payload =>
        {
            payload.WriteByte((byte)ShapeKind.Struct);
            payload.WriteInt32(fieldShapes.Length);
            for (var index = 0; index < fieldShapes.Length; index++)
                payload.WriteBytes(fieldShapes[index]);
        });
        return new TypeDefinition(shape, wireWidth, nativeSize, leaves.ToArray());
    }

    private static Dictionary<FieldInfo, PhysicalField> ValidatePhysicalFields(
        Type ownerType,
        FieldInfo[] fields,
        int ownerSize,
        Type rootType)
    {
        var byField = new Dictionary<FieldInfo, PhysicalField>(fields.Length);
        var ranges = new PhysicalField[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            EnsurePlanType(
                field.FieldType,
                rootType,
                $"{GetTypeDisplayName(ownerType)}.{field.Name}");
            var offset = GetManagedFieldOffset(ownerType, field, rootType);
            var size = ComponentSizeCache.GetSize(field.FieldType);
            var end = checked((long)offset + size);
            if (size <= 0 || end > ownerSize)
                throw new NotSupportedException($"Field '{GetTypeDisplayName(ownerType)}.{field.Name}' is outside its CLR layout bounds.");
            var physical = new PhysicalField(field, offset, size);
            ranges[index] = physical;
            byField.Add(field, physical);
        }

        Array.Sort(ranges, static (left, right) =>
        {
            var result = left.Offset.CompareTo(right.Offset);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Field.Name, right.Field.Name);
        });
        for (var index = 1; index < ranges.Length; index++)
        {
            if (ranges[index].Offset < checked((long)ranges[index - 1].Offset + ranges[index - 1].Size))
            {
                throw new NotSupportedException(
                    $"WorldSnapshot type '{GetTypeDisplayName(ownerType)}' has overlapping physical fields " +
                    $"'{ranges[index - 1].Field.Name}' and '{ranges[index].Field.Name}'.");
            }
        }
        return byField;
    }

    private static FieldInfo[] GetInstanceFields(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    private static LeafOp[] RepeatLeaves(LeafOp[] elementLeaves, int firstOffset, int length, int stride)
    {
        var leaves = new LeafOp[checked(elementLeaves.Length * length)];
        var destination = 0;
        for (var elementIndex = 0; elementIndex < length; elementIndex++)
        {
            var elementOffset = checked(firstOffset + elementIndex * stride);
            for (var leafIndex = 0; leafIndex < elementLeaves.Length; leafIndex++)
            {
                leaves[destination++] = new LeafOp(
                    checked(elementOffset + elementLeaves[leafIndex].ManagedOffset),
                    elementLeaves[leafIndex].Kind);
            }
        }
        return leaves;
    }

    private static void EnsurePlanType(Type type, Type rootType, string? fieldPath = null)
    {
        var location = fieldPath is null
            ? $"WorldSnapshot type '{GetTypeDisplayName(rootType)}'"
            : $"WorldSnapshot type '{GetTypeDisplayName(rootType)}' at field '{fieldPath}'";
        if (type.IsPointer || type.IsFunctionPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
            throw new NotSupportedException($"{location} contains unsupported pointer-sized type '{GetTypeDisplayName(type)}'.");
        if (type.ContainsGenericParameters || type.IsByRef || type.IsByRefLike || !type.IsValueType)
            throw new NotSupportedException($"{location} contains unsupported type '{GetTypeDisplayName(type)}'.");
        if (!ComponentSchemaCodec.SatisfiesUnmanagedConstraint(type))
            throw new NotSupportedException($"{location} does not have a closed unmanaged value shape.");
    }

    private static int GetManagedFieldOffset(Type ownerType, FieldInfo field, Type rootType)
    {
        try
        {
            var method = new DynamicMethod(
                $"WorldSnapshotOffset_{ownerType.Name}_{field.Name}",
                typeof(IntPtr),
                Type.EmptyTypes,
                typeof(WorldSnapshot).Module,
                skipVisibility: true)
            {
                InitLocals = true
            };
            var il = method.GetILGenerator();
            var owner = il.DeclareLocal(ownerType);
            il.Emit(OpCodes.Ldloca_S, owner);
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Ldloca_S, owner);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Ret);
            var offset = ((Func<IntPtr>)method.CreateDelegate(typeof(Func<IntPtr>)))().ToInt64();
            if (offset < 0 || offset > int.MaxValue)
                throw new NotSupportedException($"Field '{GetTypeDisplayName(ownerType)}.{field.Name}' has invalid CLR offset {offset}.");
            return (int)offset;
        }
        catch (Exception ex) when (ex is not NotSupportedException && ex is ArgumentException or InvalidOperationException or InvalidProgramException or MemberAccessException or TypeLoadException or System.Security.SecurityException)
        {
            throw new NotSupportedException(
                $"Could not obtain the actual CLR offset of field '{GetTypeDisplayName(ownerType)}.{field.Name}' " +
                $"for WorldSnapshot component '{GetTypeDisplayName(rootType)}'.",
                ex);
        }
    }

    private static byte[] EncodePrimitiveShape(PrimitiveKind kind) => EncodeRecord(payload =>
    {
        payload.WriteByte((byte)ShapeKind.Primitive);
        payload.WriteByte((byte)kind);
    });

    private static byte[] EncodeRecord(Action<ShapeEncoder> writePayload)
    {
        var payload = new ShapeEncoder();
        writePayload(payload);
        var result = new byte[checked(sizeof(int) + payload.WrittenCount)];
        BinaryPrimitives.WriteInt32LittleEndian(result, payload.WrittenCount);
        payload.WrittenSpan.CopyTo(result.AsSpan(sizeof(int)));
        return result;
    }

    private static bool TryGetPrimitiveKind(Type type, out PrimitiveKind kind)
    {
        if (type == typeof(bool)) kind = PrimitiveKind.Boolean;
        else if (type == typeof(sbyte)) kind = PrimitiveKind.SByte;
        else if (type == typeof(byte)) kind = PrimitiveKind.Byte;
        else if (type == typeof(short)) kind = PrimitiveKind.Int16;
        else if (type == typeof(ushort)) kind = PrimitiveKind.UInt16;
        else if (type == typeof(char)) kind = PrimitiveKind.Char;
        else if (type == typeof(int)) kind = PrimitiveKind.Int32;
        else if (type == typeof(uint)) kind = PrimitiveKind.UInt32;
        else if (type == typeof(float)) kind = PrimitiveKind.Single;
        else if (type == typeof(long)) kind = PrimitiveKind.Int64;
        else if (type == typeof(ulong)) kind = PrimitiveKind.UInt64;
        else if (type == typeof(double)) kind = PrimitiveKind.Double;
        else
        {
            kind = default;
            return false;
        }
        return true;
    }

    private static int PrimitiveWidth(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Boolean or PrimitiveKind.SByte or PrimitiveKind.Byte => 1,
        PrimitiveKind.Int16 or PrimitiveKind.UInt16 or PrimitiveKind.Char => 2,
        PrimitiveKind.Int32 or PrimitiveKind.UInt32 or PrimitiveKind.Single => 4,
        PrimitiveKind.Int64 or PrimitiveKind.UInt64 or PrimitiveKind.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string GetStableIdentity(Type type)
    {
        return type.AssemblyQualifiedName
            ?? throw new NotSupportedException($"WorldSnapshot type '{GetTypeDisplayName(type)}' has no exact AssemblyQualifiedName.");
    }

    private static string GetTypeDisplayName(Type type) => type.FullName ?? type.Name;

    private static byte[] GetStrictUtf8Bytes(string value)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new NotSupportedException("WorldSnapshot metadata contains a string that is not valid Unicode.", ex);
        }
    }

    private static void WriteInt32<TSink>(ref TSink sink, int value)
        where TSink : struct, IByteSink
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        sink.Write(bytes);
    }

    private static void WriteLengthPrefixedBytes<TSink>(ref TSink sink, ReadOnlySpan<byte> bytes)
        where TSink : struct, IByteSink
    {
        WriteInt32(ref sink, bytes.Length);
        sink.Write(bytes);
    }

    private interface IByteSink
    {
        void Write(ReadOnlySpan<byte> bytes);
    }

    private readonly struct StreamCrcSink(Stream stream, Crc32 crc) : IByteSink
    {
        public void Write(ReadOnlySpan<byte> bytes)
        {
            stream.Write(bytes);
            crc.Append(bytes);
        }
    }

    private readonly struct HashSink(IncrementalHash hash) : IByteSink
    {
        public void Write(ReadOnlySpan<byte> bytes) => hash.AppendData(bytes);
    }

    private sealed class ComponentPlan(
        string identity,
        byte[] identityBytes,
        byte[] schemaShapeBytes,
        int wireWidth,
        LeafOp[] leaves)
    {
        internal string Identity { get; } = identity;
        internal byte[] IdentityBytes { get; } = identityBytes;
        internal byte[] SchemaShapeBytes { get; } = schemaShapeBytes;
        internal int WireWidth { get; } = wireWidth;
        private LeafOp[] Leaves { get; } = leaves;

        internal void WriteValue<TSink>(ref TSink sink, ReadOnlySpan<byte> cell)
            where TSink : struct, IByteSink
        {
            Span<byte> encoded = stackalloc byte[sizeof(ulong)];
            ref var source = ref MemoryMarshal.GetReference(cell);
            for (var index = 0; index < Leaves.Length; index++)
            {
                var leaf = Leaves[index];
                ref var value = ref Unsafe.Add(ref source, leaf.ManagedOffset);
                var width = PrimitiveWidth(leaf.Kind);
                switch (leaf.Kind)
                {
                    case PrimitiveKind.Boolean:
                        encoded[0] = value == 0 ? (byte)0 : (byte)1;
                        break;
                    case PrimitiveKind.SByte:
                    case PrimitiveKind.Byte:
                        encoded[0] = value;
                        break;
                    case PrimitiveKind.Int16:
                    case PrimitiveKind.UInt16:
                    case PrimitiveKind.Char:
                        BinaryPrimitives.WriteUInt16LittleEndian(encoded, Unsafe.ReadUnaligned<ushort>(ref value));
                        break;
                    case PrimitiveKind.Int32:
                    case PrimitiveKind.UInt32:
                    case PrimitiveKind.Single:
                        BinaryPrimitives.WriteUInt32LittleEndian(encoded, Unsafe.ReadUnaligned<uint>(ref value));
                        break;
                    case PrimitiveKind.Int64:
                    case PrimitiveKind.UInt64:
                    case PrimitiveKind.Double:
                        BinaryPrimitives.WriteUInt64LittleEndian(encoded, Unsafe.ReadUnaligned<ulong>(ref value));
                        break;
                    default:
                        throw new InvalidOperationException("Unknown WorldSnapshot primitive leaf kind.");
                }
                sink.Write(encoded[..width]);
            }
        }

        internal void ValidateValue(ref SnapshotReader reader)
        {
            for (var index = 0; index < Leaves.Length; index++)
            {
                var kind = Leaves[index].Kind;
                if (kind == PrimitiveKind.Boolean)
                {
                    var value = reader.ReadByte("component bool");
                    if (value > 1)
                        throw new InvalidDataException($"WorldSnapshot contains noncanonical bool value {value}; expected 0 or 1.");
                }
                else
                {
                    reader.Skip(PrimitiveWidth(kind), "component leaf");
                }
            }
        }

        internal void ReadValue(ref SnapshotReader reader, Span<byte> cell)
        {
            cell.Clear();
            ref var destination = ref MemoryMarshal.GetReference(cell);
            for (var index = 0; index < Leaves.Length; index++)
            {
                var leaf = Leaves[index];
                ref var value = ref Unsafe.Add(ref destination, leaf.ManagedOffset);
                switch (leaf.Kind)
                {
                    case PrimitiveKind.Boolean:
                        value = reader.ReadByte("component bool");
                        break;
                    case PrimitiveKind.SByte:
                    case PrimitiveKind.Byte:
                        value = reader.ReadByte("component byte");
                        break;
                    case PrimitiveKind.Int16:
                    case PrimitiveKind.UInt16:
                    case PrimitiveKind.Char:
                        Unsafe.WriteUnaligned(ref value, reader.ReadUInt16("component 16-bit leaf"));
                        break;
                    case PrimitiveKind.Int32:
                    case PrimitiveKind.UInt32:
                    case PrimitiveKind.Single:
                        Unsafe.WriteUnaligned(ref value, reader.ReadUInt32("component 32-bit leaf"));
                        break;
                    case PrimitiveKind.Int64:
                    case PrimitiveKind.UInt64:
                    case PrimitiveKind.Double:
                        Unsafe.WriteUnaligned(ref value, reader.ReadUInt64("component 64-bit leaf"));
                        break;
                    default:
                        throw new InvalidOperationException("Unknown WorldSnapshot primitive leaf kind.");
                }
            }
        }
    }

    private ref struct SnapshotReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _offset;

        internal int Remaining => _bytes.Length - _offset;

        internal byte ReadByte(string field)
        {
            EnsureRemaining(1, field);
            return _bytes[_offset++];
        }

        internal ushort ReadUInt16(string field)
        {
            var span = ReadBytes(sizeof(ushort), field);
            return BinaryPrimitives.ReadUInt16LittleEndian(span);
        }

        internal uint ReadUInt32(string field)
        {
            var span = ReadBytes(sizeof(uint), field);
            return BinaryPrimitives.ReadUInt32LittleEndian(span);
        }

        internal ulong ReadUInt64(string field)
        {
            var span = ReadBytes(sizeof(ulong), field);
            return BinaryPrimitives.ReadUInt64LittleEndian(span);
        }

        internal int ReadInt32(string field)
        {
            var span = ReadBytes(sizeof(int), field);
            return BinaryPrimitives.ReadInt32LittleEndian(span);
        }

        internal string ReadString(string field, int maximumByteCount)
        {
            var byteCount = ReadInt32($"{field} length");
            if (byteCount < 0)
                throw new InvalidDataException($"WorldSnapshot {field} has negative length {byteCount}.");
            if (byteCount > maximumByteCount)
                throw new InvalidDataException($"WorldSnapshot {field} length ({byteCount}) exceeds the maximum of {maximumByteCount} byte(s).");
            var value = ReadBytes(byteCount, field);
            try
            {
                return StrictUtf8.GetString(value);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"WorldSnapshot {field} is not valid UTF-8.", ex);
            }
        }

        internal ReadOnlySpan<byte> ReadLengthPrefixedBytes(string field)
        {
            var byteCount = ReadInt32($"{field} length");
            if (byteCount < 0)
                throw new InvalidDataException($"WorldSnapshot {field} has negative length {byteCount}.");
            return ReadBytes(byteCount, field);
        }

        internal void Skip(int byteCount, string field) => _ = ReadBytes(byteCount, field);

        private ReadOnlySpan<byte> ReadBytes(int byteCount, string field)
        {
            if (byteCount < 0 || byteCount > Remaining)
                throw new InvalidDataException($"WorldSnapshot is truncated while reading {field}: expected {byteCount} byte(s), got {Remaining}.");
            var result = _bytes.Slice(_offset, byteCount);
            _offset += byteCount;
            return result;
        }

        private void EnsureRemaining(int byteCount, string field)
        {
            if (Remaining < byteCount)
                throw new InvalidDataException($"WorldSnapshot is truncated while reading {field}: expected {byteCount} byte(s), got {Remaining}.");
        }
    }

    private sealed class ShapeEncoder
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        internal int WrittenCount => _buffer.WrittenCount;
        internal ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

        internal void WriteByte(byte value)
        {
            _buffer.GetSpan(1)[0] = value;
            _buffer.Advance(1);
        }

        internal void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.GetSpan(sizeof(int)), value);
            _buffer.Advance(sizeof(int));
        }

        internal void WriteString(string value)
        {
            var bytes = GetStrictUtf8Bytes(value);
            WriteInt32(bytes.Length);
            WriteBytes(bytes);
        }

        internal void WriteBytes(ReadOnlySpan<byte> value)
        {
            value.CopyTo(_buffer.GetSpan(value.Length));
            _buffer.Advance(value.Length);
        }
    }

    private sealed class SchemaEntry(
        Type componentType,
        ComponentPlan plan,
        int schemaIndex)
    {
        internal Type ComponentType { get; } = componentType;
        internal ComponentPlan Plan { get; } = plan;
        internal int SchemaIndex { get; set; } = schemaIndex;
    }

    private sealed record CanonicalColumn(SchemaEntry Schema, int RuntimeColumnIndex);
    private sealed record CanonicalArchetype(Archetype Archetype, CanonicalColumn[] Columns, int[] SortedRows);
    private sealed record CanonicalLayout(
        SchemaEntry[] Schemas,
        CanonicalArchetype[] Archetypes,
        (int ChildId, int ParentId)[] Relations);
    private sealed record ValidatedSnapshot(int ChunkCapacity, int SlotCount, SchemaEntry[] Schemas);
    private sealed record TypeDefinition(byte[] ShapeBytes, int WireWidth, int NativeSize, LeafOp[] Leaves);
    private readonly record struct LeafOp(int ManagedOffset, PrimitiveKind Kind);
    private readonly record struct PhysicalField(FieldInfo Field, int Offset, int Size);

    private enum ShapeKind : byte
    {
        Primitive = 1,
        Enum = 2,
        Struct = 3,
        InlineArray = 4
    }

    private enum FieldShapeKind : byte
    {
        Field = 1,
        FixedBuffer = 2
    }

    private enum PrimitiveKind : byte
    {
        Boolean = 1,
        SByte = 2,
        Byte = 3,
        Int16 = 4,
        UInt16 = 5,
        Char = 6,
        Int32 = 7,
        UInt32 = 8,
        Single = 9,
        Int64 = 10,
        UInt64 = 11,
        Double = 12
    }
}
