using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Creates an empty entity.
    /// </summary>
    public Entity Create()
    {
        AssertNotDisposed();
        var archetype = GetOrCreateArchetype(Signature.Empty)!;
        return CreateInArchetype(archetype, out _);
    }

    /// <summary>
    /// Creates many empty entities.
    /// </summary>
    public void CreateMany(Span<Entity> entities)
    {
        AssertNotDisposed();
        if (entities.Length == 0)
        {
            return;
        }

        if (_freeIdCount == 0)
        {
            CreateManyFresh(entities);
            return;
        }

        var reusedCount = Math.Min(entities.Length, _freeIdCount);
        var startId = AppendEntitySlots(entities.Length - reusedCount);

        var archetype = GetOrCreateArchetype(Signature.Empty)!;
        var startRow = archetype.ReserveRows(entities.Length);
        Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[1];
        ranges[0] = new EntityBatchRange(startRow, entities.Length);
        WriteCreatedEntitiesAndLocations(archetype, entities, ranges, reusedCount, startId);
    }

    /// <summary>
    /// Ensures entity metadata capacity.
    /// </summary>
    public void EnsureCapacity(int entityCapacity)
    {
        AssertNotDisposed();
        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        }

        if (_records.Length < entityCapacity)
        {
            Array.Resize(ref _records, entityCapacity);
        }

        EnsureDestroyScratchCapacity(entityCapacity);
    }

    private void EnsureEntityCapacity(int requiredCount)
    {
        if (requiredCount <= _records.Length) return;
        var newLength = Math.Max(requiredCount, _records.Length * 2);
        Array.Resize(ref _records, newLength);
    }

    /// <summary>
    /// Destroys an entity and, if it has children, its entire descendant subtree.
    /// </summary>
    /// <remarks>
    /// Destruction cascades through the hierarchy in post-order (children before
    /// parent) so that no orphaned child is left behind. If the entity has no
    /// children, only itself is destroyed. Call <see cref="RemoveChild"/> first if
    /// you need to destroy a parent while preserving its subtree.
    /// </remarks>
    public void Destroy(Entity entity)
    {
        AssertNotDisposed();

        if (!_hierarchy.HasChildren(this, entity))
        {
            DestroySingle(entity);
            return;
        }

        _destroyOrderScratch.Clear();
        if (++_destroyCurrentGen == 0)
        {
            Array.Clear(_destroyVisitedGen);
            _destroyCurrentGen = 1;
        }

        try
        {
            _hierarchy.CollectDestroySubtree(this, entity, _destroyVisitedGen, _destroyCurrentGen, _destroyOrderScratch);
            if (_destroyOrderScratch.Count == 0)
            {
                throw new InvalidOperationException($"Cannot destroy entity {entity}: it is no longer alive. The entity may have already been destroyed.");
            }

            for (var index = 0; index < _destroyOrderScratch.Count; index++)
            {
                DestroySingle(_destroyOrderScratch[index]);
            }
        }
        finally
        {
            _destroyOrderScratch.Clear();
        }
    }

    internal bool TryGetLocation(Entity entity, out EntityInfo info)
    {
        AssertNotDisposed();
        if (entity.Id < 0 || entity.Id >= _entitySlotCount)
        {
            info = default;
            return false;
        }

        ref var record = ref _records[entity.Id];
        if (!record.IsOccupied || record.Version != entity.Version)
        {
            info = default;
            return false;
        }

        info = new EntityInfo(record.Version, record.Archetype, record.RowIndex);
        return true;
    }

    /// <summary>
    /// Returns whether an entity is alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(Entity entity)
    {
        AssertNotDisposed();

        if ((uint)entity.Id >= (uint)_entitySlotCount)
            return false;

        ref var record = ref _records[entity.Id];
        return record.IsOccupied && record.Version == entity.Version;
    }

    [Conditional("DEBUG")]
    private void AssertValidEntityId(int id, Entity entity)
    {
        if ((uint)id >= (uint)_entitySlotCount)
            ThrowInvalidEntity(entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetRecord(Entity entity, out EntityRecord record)
    {
        AssertNotDisposed();
        if ((uint)entity.Id >= (uint)_entitySlotCount) { record = default; return false; }
        ref var r = ref _records[entity.Id];
        if (!r.IsOccupied || r.Version != entity.Version) { record = default; return false; }
        record = r;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EntityRecord RequireLocation(Entity entity)
    {
        var id = entity.Id;
        AssertValidEntityId(id, entity);

        ref var record = ref _records[id];
        if (!record.IsOccupied || record.Version != entity.Version)
        {
            ThrowStaleEntity(entity);
        }

        return record;
    }

    [DoesNotReturn]
    private void ThrowInvalidEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} does not exist. The entity may have never been created, or its id is invalid.");
    }

    [DoesNotReturn]
    private void ThrowStaleEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} is no longer alive. It may have been destroyed in a previous frame or the handle is stale.");
    }

    private void DestroySingle(Entity entity)
    {
        var info = RequireLocation(entity);
        var arch = info.Archetype!;
        arch.RemoveAt(info.RowIndex, out var movedEntity);
        if (_hierarchy.HasAnyRelations(entity))
        {
            _hierarchy.RemoveDestroyed(entity);
        }

        ref var record = ref _records[entity.Id];
        record = default;
        record.Version = entity.Version + 1;
        PushFreeIdUnsafe(entity.Id, entity.Version + 1);

        if (movedEntity.IsValid)
        {
            ref var movedRecord = ref _records[movedEntity.Id];
            movedRecord.Archetype = info.Archetype;
            movedRecord.RowIndex = info.RowIndex;
        }
    }

    private Entity CreateInArchetype(Archetype archetype, out int rowIndex)
    {
        var id = AcquireEntityIdUnsafe(out var version);
        var entity = new Entity(id, version);
        rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;
        return entity;
    }

    private int AppendEntitySlots(int newEntityCount)
    {
        var startId = _entitySlotCount;
        if (newEntityCount == 0)
        {
            return startId;
        }

        var requiredCount = startId + newEntityCount;
        EnsureBatchCapacity(requiredCount, newEntityCount);
        EnsureEntityCapacity(requiredCount);
        _entitySlotCount = requiredCount;
        // Initialize new slots: version=1, location=empty
        var newSlots = _records.AsSpan(startId, newEntityCount);
        for (var i = 0; i < newSlots.Length; i++)
        {
            newSlots[i] = new EntityRecord { Version = 1 };
        }
        return startId;
    }

    private void EnsureBatchCapacity(int requiredCount, int batchCount)
    {
        if (_records.Length >= requiredCount)
        {
            return;
        }

        var targetCapacity = requiredCount + (batchCount / 2);
        if (targetCapacity < requiredCount)
        {
            targetCapacity = requiredCount;
        }

        EnsureCapacity(targetCapacity);
    }

    private void CreateManyFresh(Span<Entity> entities)
    {
        var startId = AppendEntitySlots(entities.Length);

        var archetype = GetOrCreateArchetype(Signature.Empty)!;
        var startRow = archetype.ReserveRows(entities.Length);
        Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[1];
        ranges[0] = new EntityBatchRange(startRow, entities.Length);
        WriteCreatedEntitiesAndLocations(archetype, entities, ranges, 0, startId);
    }

    private void WriteCreatedEntitiesAndLocations(Archetype archetype, Span<Entity> entities, ReadOnlySpan<EntityBatchRange> ranges, int reusedCount, int startId)
    {
        if (archetype.IsChunked)
            WriteCreatedEntitiesAndLocationsChunked(archetype, entities, ranges, reusedCount, startId);
        else
            WriteCreatedEntitiesAndLocationsFlat(archetype, entities, ranges, reusedCount, startId);
    }

    private void WriteCreatedEntitiesAndLocationsFlat(Archetype archetype, Span<Entity> entities, ReadOnlySpan<EntityBatchRange> ranges, int reusedCount, int startId)
    {
        var freeIds = _freeIds;
        var freeIndex = _freeIdCount;
        var entityIndex = 0;
        var nextId = startId;

        foreach (var range in ranges)
        {
            var chunkEntities = archetype.GetReservedEntities(range.StartRow, range.Count);
            var rowOffset = 0;

            for (; rowOffset < range.Count && entityIndex < reusedCount; rowOffset++)
            {
                var recycled = freeIds[--freeIndex];
                var entity = new Entity(recycled.Id, recycled.Version);
                entities[entityIndex++] = entity;
                chunkEntities[rowOffset] = entity;
                ref var record = ref _records[entity.Id];
                record.Archetype = archetype;
                record.RowIndex = range.StartRow + rowOffset;
            }

            for (; rowOffset < range.Count; rowOffset++)
            {
                var entity = new Entity(nextId++, 1);
                entities[entityIndex++] = entity;
                chunkEntities[rowOffset] = entity;
                ref var record = ref _records[entity.Id];
                record.Archetype = archetype;
                record.RowIndex = range.StartRow + rowOffset;
            }
        }

        _freeIdCount = freeIndex;
    }

    // Chunked archetypes store rows across multiple segment arrays, so the
    // single-Span fast path in the Flat variant does not apply. Fall back to
    // per-row WriteEntityAt, which maps a global row to (segment, local) and
    // works in both modes. Called only when an archetype has already been
    // promoted to chunked storage (large entity counts on dense signatures).
    private void WriteCreatedEntitiesAndLocationsChunked(Archetype archetype, Span<Entity> entities, ReadOnlySpan<EntityBatchRange> ranges, int reusedCount, int startId)
    {
        var freeIds = _freeIds;
        var freeIndex = _freeIdCount;
        var entityIndex = 0;
        var nextId = startId;

        foreach (var range in ranges)
        {
            var rowOffset = 0;

            for (; rowOffset < range.Count && entityIndex < reusedCount; rowOffset++)
            {
                var recycled = freeIds[--freeIndex];
                var entity = new Entity(recycled.Id, recycled.Version);
                entities[entityIndex++] = entity;
                archetype.WriteEntityAt(range.StartRow + rowOffset, entity);
                ref var record = ref _records[entity.Id];
                record.Archetype = archetype;
                record.RowIndex = range.StartRow + rowOffset;
            }

            for (; rowOffset < range.Count; rowOffset++)
            {
                var entity = new Entity(nextId++, 1);
                entities[entityIndex++] = entity;
                archetype.WriteEntityAt(range.StartRow + rowOffset, entity);
                ref var record = ref _records[entity.Id];
                record.Archetype = archetype;
                record.RowIndex = range.StartRow + rowOffset;
            }
        }

        _freeIdCount = freeIndex;
    }

    private readonly object _entityIdLock = new();

    private int AcquireEntityIdUnsafe(out int version)
    {
        if (_freeIdCount > 0)
        {
            var recycled = PopFreeIdUnsafe();
            version = recycled.Version;
            return recycled.Id;
        }

        var id = _entitySlotCount;
        EnsureEntityCapacity(_entitySlotCount + 1);
        _records[_entitySlotCount] = new EntityRecord { Version = 1 };
        _entitySlotCount++;
        EnsureDestroyScratchCapacity(_entitySlotCount);
        version = 1;
        return id;
    }

    private void PushFreeIdUnsafe(int id, int version)
    {
        if (_freeIdCount == _freeIds.Length)
        {
            var newCapacity = _freeIds.Length == 0 ? 4 : _freeIds.Length * 2;
            Array.Resize(ref _freeIds, newCapacity);
        }

        _freeIds[_freeIdCount++] = new RecycledEntity(id, version);
    }

    private RecycledEntity PopFreeIdUnsafe()
    {
        return _freeIds[--_freeIdCount];
    }

    internal Entity ReserveDeferredEntity()
    {
        lock (_entityIdLock)
        {
            return ReserveDeferredEntityUnsafe();
        }
    }

    /// <summary>
    /// Reserves an entity id without taking the world-level allocator lock.
    /// The caller must guarantee no other thread is reserving or mutating this world.
    /// </summary>
    internal Entity ReserveDeferredEntityUnsafe()
    {
        var id = AcquireEntityIdUnsafe(out var version);
        return new Entity(id, version);
    }

    internal void ReleaseReservedEntity(Entity entity)
    {
        if (entity.Id < 0 || entity.Id >= _entitySlotCount)
        {
            throw new InvalidOperationException($"Entity {entity} is not a valid deferred entity. The entity handle may be invalid or already materialized.");
        }

        ref var record = ref _records[entity.Id];
        if (record.IsOccupied || record.Version != entity.Version)
        {
            throw new InvalidOperationException($"Entity {entity} is not a deferred reserved entity. It may have already been materialized or was never reserved via CommandStream.");
        }

        var nextVersion = entity.Version + 1;
        record.Version = nextVersion;
        PushFreeIdUnsafe(entity.Id, nextVersion);
    }

    /// <summary>
    /// Releases the reserved id if (and only if) <paramref name="entity"/> is still
    /// in the reserved (not yet materialized, not yet released) state. Returns false
    /// for alive/free/destroyed entities without throwing —use this on cleanup paths
    /// where the entity state is not known a priori.
    /// </summary>
    internal bool TryReleaseReserved(Entity entity)
    {
        if ((uint)entity.Id >= (uint)_entitySlotCount) return false;
        ref var record = ref _records[entity.Id];
        if (record.IsOccupied || record.Version != entity.Version) return false;

        var nextVersion = entity.Version + 1;
        record.Version = nextVersion;
        PushFreeIdUnsafe(entity.Id, nextVersion);
        return true;
    }

    private void EnsureDestroyScratchCapacity(int entityCount)
    {
        if (entityCount <= 0)
        {
            return;
        }

        _destroyOrderScratch.EnsureCapacity(entityCount);
        if (_destroyVisitedGen.Length < entityCount)
        {
            Array.Resize(ref _destroyVisitedGen, Math.Max(entityCount, _destroyVisitedGen.Length * 2));
        }
    }

    private void EnsureReplayReservation(Entity entity)
    {
        if ((uint)entity.Id < (uint)_entitySlotCount &&
            !_records[entity.Id].IsOccupied &&
            _records[entity.Id].Version == entity.Version)
        {
            RemoveFromFreeList(entity);
            return;
        }

        if (entity.Id == _entitySlotCount && entity.Version == 1)
        {
            ReserveReplayFreshSlot(entity);
            return;
        }

        var reserved = ReserveDeferredEntity();
        if (reserved != entity)
        {
            throw new InvalidOperationException($"Replay failed: expected to reserve entity {entity} but got {reserved} instead. The source and target worlds may be out of sync.");
        }
    }

    private void ReserveReplayFreshSlot(Entity entity)
    {
        EnsureEntityCapacity(entity.Id + 1);
        _records[entity.Id] = new EntityRecord { Version = entity.Version };
        _entitySlotCount = entity.Id + 1;
        EnsureDestroyScratchCapacity(_entitySlotCount);
    }

    private void RemoveFromFreeList(Entity entity)
    {
        for (var i = 0; i < _freeIdCount; i++)
        {
            if (_freeIds[i].Id == entity.Id && _freeIds[i].Version == entity.Version)
            {
                _freeIds[i] = _freeIds[_freeIdCount - 1];
                _freeIdCount--;
                return;
            }
        }
    }

    internal readonly record struct RecycledEntity(int Id, int Version);

}
