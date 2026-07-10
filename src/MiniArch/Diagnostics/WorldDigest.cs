using MiniArch.Core;

namespace MiniArch.Diagnostics;

/// <summary>
/// Computes per-domain SHA-256 hashes for a <see cref="World"/>, enabling
/// rapid narrowing of lockstep divergence before running the heavier
/// <see cref="WorldDiff.Compare"/>. The total hash includes physical
/// per-archetype row-order data; use <see cref="World.CanonicalChecksum"/>
/// when comparing layout-independent logical state.
/// </summary>
public static class WorldDigest
{
    /// <summary>
    /// Computes a structured digest of <paramref name="world"/> split by state domain.
    /// </summary>
    public static WorldDigestResult Compute(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var occupancyHash = ComputeOccupancyHash(world);
        var freeListHash = ComputeFreeListHash(world);
        var hierarchyHash = ComputeHierarchyHash(world);
        var perComponent = ComputePerComponentHashes(world);
        var perArchetype = ComputePerArchetypeHashes(world);

        var totalHash = DigestHelper.CombineHashes(
            occupancyHash, freeListHash, hierarchyHash,
            CombineTypeDictHash(perComponent),
            CombineIntDictHash(perArchetype));

        return new WorldDigestResult(totalHash, occupancyHash, freeListHash, hierarchyHash,
            perComponent, perArchetype);
    }

    private static byte[] ComputeOccupancyHash(World world)
    {
        using var builder = new HashBuilder();

        // Collect alive entities sorted by ID (slot order = ID order).
        var alive = new List<(int Id, int Version)>();
        var records = world.EntityRecords;
        for (var i = 0; i < records.Length; i++)
        {
            if (records[i].IsOccupied)
                alive.Add((i, records[i].Version));
        }

        builder.Append(alive.Count);
        foreach (var (id, version) in alive)
        {
            builder.Append(id);
            builder.Append(version);
        }

        return builder.GetHashAndReset();
    }

    private static byte[] ComputeFreeListHash(World world)
    {
        using var builder = new HashBuilder();

        var freeList = world.FreeList;
        builder.Append(freeList.Length);
        for (var i = 0; i < freeList.Length; i++)
        {
            builder.Append(freeList[i].Id);
            builder.Append(freeList[i].Version);
        }

        return builder.GetHashAndReset();
    }

    private static byte[] ComputeHierarchyHash(World world)
    {
        using var builder = new HashBuilder();

        var relations = new List<(int ChildId, int ParentId)>();
        foreach (var (child, parent) in world.Hierarchy.EnumerateLiveRelations(world))
            relations.Add((child.Id, parent.Id));
        relations.Sort((a, b) => a.ChildId.CompareTo(b.ChildId));

        builder.Append(relations.Count);
        foreach (var (childId, parentId) in relations)
        {
            builder.Append(childId);
            builder.Append(parentId);
        }

        return builder.GetHashAndReset();
    }

    private static Dictionary<Type, byte[]> ComputePerComponentHashes(World world)
    {
        var componentData = new Dictionary<Type, List<(int EntityId, byte[] Bytes)>>();

        foreach (var arch in world.Archetypes)
        {
            if (arch is null || arch.EntityCount == 0) continue;

            var entities = arch.GetEntities();
            var types = arch.ComponentTypes;

            for (var col = 0; col < types.Count; col++)
            {
                var type = types[col];
                if (!componentData.TryGetValue(type, out var list))
                {
                    list = new List<(int, byte[])>();
                    componentData[type] = list;
                }

                for (var row = 0; row < entities.Length; row++)
                {
                    var bytes = arch.GetComponentBytes(col, row).ToArray();
                    list.Add((entities[row].Id, bytes));
                }
            }
        }

        var result = new Dictionary<Type, byte[]>();
        foreach (var (type, entries) in componentData)
        {
            entries.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));

            using var builder = new HashBuilder();
            builder.Append(entries.Count);
            foreach (var (entityId, bytes) in entries)
            {
                builder.Append(entityId);
                builder.Append(bytes.AsSpan());
            }
            result[type] = builder.GetHashAndReset();
        }

        return result;
    }

    private static Dictionary<int, byte[]> ComputePerArchetypeHashes(World world)
    {
        var result = new Dictionary<int, byte[]>();
        var idx = 0;

        foreach (var arch in world.Archetypes)
        {
            if (arch is null || arch.EntityCount == 0) { idx++; continue; }

            using var builder = new HashBuilder();

            // Signature.
            var sig = arch.Signature.AsSpan();
            builder.Append(sig.Length);
            for (var i = 0; i < sig.Length; i++)
                builder.Append(sig[i].Value);

            // Entity IDs (sorted).
            var entities = arch.GetEntities();
            var ids = new int[entities.Length];
            for (var i = 0; i < entities.Length; i++)
                ids[i] = entities[i].Id;
            Array.Sort(ids);

            builder.Append(ids.Length);
            foreach (var id in ids)
                builder.Append(id);

            // Component data (column by column, row by row).
            for (var col = 0; col < sig.Length; col++)
            {
                for (var row = 0; row < entities.Length; row++)
                {
                    var bytes = arch.GetComponentBytes(col, row);
                    builder.Append(bytes);
                }
            }

            result[idx] = builder.GetHashAndReset();
            idx++;
        }

        return result;
    }

    private static byte[] CombineTypeDictHash(Dictionary<Type, byte[]> dict)
    {
        using var builder = new HashBuilder();
        builder.Append(dict.Count);
        foreach (var kvp in dict)
        {
            var name = kvp.Key.FullName ?? kvp.Key.Name;
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            builder.Append(nameBytes.AsSpan());
            builder.Append(kvp.Value.AsSpan());
        }
        return builder.GetHashAndReset();
    }

    private static byte[] CombineIntDictHash(Dictionary<int, byte[]> dict)
    {
        using var builder = new HashBuilder();
        builder.Append(dict.Count);
        foreach (var kvp in dict)
        {
            builder.Append(kvp.Key);
            builder.Append(kvp.Value.AsSpan());
        }
        return builder.GetHashAndReset();
    }
}
