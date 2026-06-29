namespace MiniArch.Core;

internal static class WorldClone
{
    public static World Clone(World source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var entitySlotCount = source.EntitySlotCount;
        var target = new World(source.ChunkCapacity, entitySlotCount);
        target.Reset(entitySlotCount);

        var sourceRecords = source.EntityRecords;
        for (var i = 0; i < sourceRecords.Length; i++)
        {
            target.SetSnapshotEntityVersion(i, sourceRecords[i].Version);
        }

        foreach (var srcArch in source.Archetypes)
        {
            if (srcArch.EntityCount == 0)
            {
                continue;
            }

            var dstArch = target.GetOrCreateArchetype(srcArch.Signature);

            var entities = srcArch.GetEntities();
            var startRow = dstArch.ReserveRows(entities.Length);
            for (var i = 0; i < entities.Length; i++)
                dstArch.WriteEntityAt(startRow + i, entities[i]);
            dstArch.CopyColumnsFrom(srcArch, entities.Length);

            for (var row = 0; row < entities.Length; row++)
            {
                target.SetSnapshotLocation(entities[row], dstArch, startRow + row);
            }
        }

        foreach (var (child, parent) in source.Hierarchy.EnumerateLiveLinks(source))
        {
            target.LinkSnapshot(parent, child);
        }

        target.CopyFreeIdsFrom(source);
        return target;
    }
}
