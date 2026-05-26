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

}
