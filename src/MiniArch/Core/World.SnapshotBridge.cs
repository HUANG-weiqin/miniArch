using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    internal void Reset(int entitySlotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entitySlotCount);

        _archetypes.Clear();
        _archetypeByMask.Clear();
        _replayCreateCounts.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _queryFiltersByDescription.Clear();
        _queries.Clear();
        _createArchetypeCacheGeneration++;
        _freeIdCount = 0;
        _destroyVisitedGen = [];
        _destroyCurrentGen = 0;
        _hierarchy.Reset();

        _entitySlotCount = 0;
        EnsureCapacity(entitySlotCount);
        _entitySlotCount = entitySlotCount;
        _records.AsSpan(0, entitySlotCount).Clear();

        if (_freeIds.Length < entitySlotCount)
        {
            Array.Resize(ref _freeIds, entitySlotCount);
        }
    }

    internal void AddChildFromSnapshot(Entity parent, Entity child)
    {
        _hierarchy.AddChildRestored(this, parent, child);
    }

    internal ReadOnlySpan<RecycledEntity> FreeList => _freeIds.AsSpan(0, _freeIdCount);

    internal void SetSnapshotEntityVersion(int entityId, int version)
    {
        ValidateSnapshotEntitySlot(entityId);
        _records[entityId].Version = version;
    }

    internal int GetEntityVersion(int entityId)
    {
        ValidateSnapshotEntitySlot(entityId);
        return _records[entityId].Version;
    }

    internal void SetSnapshotLocation(Entity entity, Archetype archetype, int rowIndex)
    {
        ValidateSnapshotEntitySlot(entity.Id);
        _records[entity.Id].Archetype = archetype;
        _records[entity.Id].RowIndex = rowIndex;
    }

    internal void WriteFreeList(System.IO.BinaryWriter writer)
    {
        writer.Write(_freeIdCount);
        for (var i = 0; i < _freeIdCount; i++)
        {
            writer.Write(_freeIds[i].Id);
            writer.Write(_freeIds[i].Version);
        }
    }

    internal void ReadFreeList(System.IO.BinaryReader reader)
    {
        _freeIdCount = reader.ReadInt32();
        if (_freeIds.Length < _freeIdCount)
            Array.Resize(ref _freeIds, _freeIdCount);
        for (var i = 0; i < _freeIdCount; i++)
            _freeIds[i] = new RecycledEntity(reader.ReadInt32(), reader.ReadInt32());
    }

    internal void CopyFreeIdsFrom(World source)
    {
        var count = source._freeIdCount;
        if (_freeIds.Length < count)
            Array.Resize(ref _freeIds, count);
        Array.Copy(source._freeIds, _freeIds, count);
        _freeIdCount = count;
    }

    private void ValidateSnapshotEntitySlot(int entityId)
    {
        if (entityId < 0 || entityId >= _entitySlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(entityId));
        }
    }
}
