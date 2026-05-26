namespace MiniArch.Core;

internal static class WorldClone
{
    public static World Clone(World source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var entitySlotCount = source.EntitySlotCount;
        var target = new World(source.ChunkCapacity, entitySlotCount);
        target.Reset(entitySlotCount);

        var sourceVersions = source.EntityVersions;
        for (var i = 0; i < sourceVersions.Length; i++)
        {
            target.SetSnapshotEntityVersion(i, sourceVersions[i]);
        }

        RegisterComponentTypesInOrder(source, target);

        foreach (var srcArch in source.Archetypes)
        {
            if (srcArch.EntityCount == 0)
            {
                continue;
            }

            var dstArch = target.GetOrCreateArchetype(srcArch.Signature);

            foreach (var srcChunk in srcArch.Chunks)
            {
                if (srcChunk.Count == 0)
                {
                    continue;
                }

                var entities = srcChunk.GetEntities();
                var dstChunk = dstArch.ImportSnapshotChunk(entities, out var dstChunkIdx);

                dstChunk.CopyColumnsFrom(srcChunk, srcChunk.Count);

                for (var row = 0; row < entities.Length; row++)
                {
                    target.SetSnapshotLocation(entities[row], dstArch, dstChunkIdx, row);
                }
            }
        }

        foreach (var (child, parent) in source.Hierarchy.EnumerateLiveLinks(source))
        {
            target.LinkSnapshot(parent, child);
        }

        target.RebuildFreeIdStack();
        return target;
    }

    private static void RegisterComponentTypesInOrder(World source, World target)
    {
        var registered = source.Components.RegisteredTypes;
        if (registered.Count == 0)
        {
            return;
        }

        var sorted = new KeyValuePair<Type, ComponentType>[registered.Count];
        var i = 0;
        foreach (var kvp in registered)
        {
            sorted[i++] = kvp;
        }

        Array.Sort(sorted, static (a, b) => a.Value.Value.CompareTo(b.Value.Value));

        for (var index = 0; index < sorted.Length; index++)
        {
            target.Components.GetOrCreate(sorted[index].Key);
        }
    }
}
