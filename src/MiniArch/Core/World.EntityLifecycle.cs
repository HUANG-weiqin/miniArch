using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    // Below this count, Destroy falls back to individual Destroy calls.
    // The batch machinery (gen tracking, scratch arrays, archetype grouping)
    // costs ~0.4 us fixed; at R <= 8 that overhead exceeds the per-entity savings
    // from bulk storage operations. Semantic equivalence is guaranteed by the
    // child-first cascade order in Destroy.
    internal const int SmallDestroyThreshold = 8;

    /// <summary>
    /// Creates an empty entity (no components). Use this when the component set
    /// is not known at creation time — add components afterward via
    /// <see cref="Add{T}"/>.
    /// </summary>
    public Entity CreateEmpty()
    {
        AssertNotDisposed();
        var archetype = GetOrCreateArchetype(Signature.Empty)!;
        return CreateInArchetype(archetype, out _);
    }

    /// <summary>
    /// Ensures entity metadata capacity.
    /// </summary>
    public void EnsureCapacity(int entityCapacity)
    {
        AssertNotDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(entityCapacity);

        if (_records.Length < entityCapacity)
        {
            Array.Resize(ref _records, entityCapacity);
        }

        EnsureDestroyScratchCapacity(entityCapacity);
    }

    private void EnsureEntityCapacity(int requiredCount)
    {
        if (requiredCount <= _records.Length) return;
        const int maxArray = 0x7FFFFFC7;
        var newLength = Math.Max(requiredCount, Math.Min(_records.Length * 2, maxArray));
        Array.Resize(ref _records, newLength);
    }

    /// <summary>
    /// Destroys an entity and, if it has children, its entire descendant subtree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Destruction cascades through the hierarchy in post-order (children before
    /// parent) so that no orphaned child is left behind. If the entity has no
    /// children, only itself is destroyed. Call <see cref="RemoveChild"/> first if
    /// you need to destroy a parent while preserving its subtree.
    /// </para>
    /// <para><b>When to use:</b> destroying a single entity.</para>
    /// <para><b>When NOT to use:</b> destroying many entities — use
    /// <see cref="Destroy(ReadOnlySpan{Entity})"/> for batch optimization,
    /// or <see cref="Clear(in QueryDescription)"/> for archetype-level teardown.</para>
    /// </remarks>
    public void Destroy(Entity entity)
    {
        AssertNotDisposed();

        if (!_hierarchy.HasChildren(this, entity))
        {
            DestroyAliveLeaf(entity, RequireLocation(entity));
            return;
        }

        if (++_destroyCurrentGen == 0)
        {
            Array.Clear(_destroyVisitedGen);
            _destroyCurrentGen = 1;
        }

        BeginStructChange();
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
            EndStructChange();
            _destroyOrderScratch.Clear();
        }
    }

    internal void DestroyIfAlive(Entity entity)
    {
        if (!TryGetAliveRecord(entity, out var info))
            return;

        if (_hierarchy.HasChildrenUnchecked(entity))
        {
            Destroy(entity);
            return;
        }

        DestroyAliveLeaf(entity, info);
    }

    private void DestroyAliveLeaf(Entity entity, EntityRecord info)
    {
        BeginStructChange();
#if DEBUG
        try
        {
#endif
        DestroySingle(entity, info);
#if DEBUG
        }
        finally
        {
            EndStructChange();
        }
#else
        EndStructChange();
#endif
    }

    /// <summary>
    /// Destroys multiple entities in one structural batch. Stale/dead handles are skipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The destroy order is identical to running <c>if (world.IsAlive(e)) world.Destroy(e)</c>
    /// for each supplied entity: hierarchy cascades are collected child-first, duplicates are
    /// ignored after their first destruction, and free-list order is preserved. Storage removal
    /// is then grouped per archetype so full-archetype clears use <c>ResetCount</c> and partial
    /// clears use unordered hole-fill compaction.
    /// </para>
    /// <para><b>When to use:</b> you have a batch of entity handles to destroy and want the
    /// storage optimization (1.3-2.3× vs for-loop). Hierarchy cascade is preserved.
    /// For query-based destroy, see <see cref="Destroy(in QueryDescription)"/>.
    /// For archetype-level teardown without cascade, see <see cref="Clear(in QueryDescription)"/>.</para>
    /// </remarks>
    public void Destroy(ReadOnlySpan<Entity> entities)
    {
        AssertNotDisposed();
        var length = entities.Length;
        if (length == 0)
        {
            return;
        }

        // Small batches: skip the collection/grouping machinery and destroy
        // individually. Semantic equivalence holds because Destroy's child-first
        // cascade produces the same free-list order as the batch path.
        if (length <= SmallDestroyThreshold)
        {
            // The input may come directly from ChunkView.GetEntities(). Each
            // Destroy can swap-remove into that backing array, so snapshot the
            // handles before the first structural mutation.
            Span<Entity> stableEntities = stackalloc Entity[length];
            entities.CopyTo(stableEntities);
            for (var i = 0; i < length; i++)
            {
                if (IsAlive(stableEntities[i]))
                    Destroy(stableEntities[i]);
            }
            return;
        }

        BeginDestroyCollection();
        try
        {
            for (var i = 0; i < length; i++)
            {
                CollectDestroyRootIfAlive(entities[i]);
            }

            DestroyCollectedEntities();
        }
        catch
        {
            _destroyOrderScratch.Clear();
            throw;
        }
    }

    /// <summary>
    /// Destroys all entities matching <paramref name="description"/> in one structural batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching entities are first materialized from the query snapshot, then destroyed with the
    /// same cascade and batching rules as <see cref="Destroy(ReadOnlySpan{Entity})"/>.
    /// Descendants are destroyed even when they do not match the query.
    /// </para>
    /// <para><b>When to use:</b> destroying all entities matching a component set,
    /// when hierarchy cascade is needed. See <see cref="Clear(in QueryDescription)"/>
    /// for a faster variant without cascade.</para>
    /// </remarks>
    public void Destroy(in QueryDescription description)
    {
        AssertNotDisposed();

        var query = GetAdvancedQuery(in description);
        var archetypes = query.GetArchetypeSpan();
        if (archetypes.Length == 0)
        {
            return;
        }

        BeginDestroyCollection();
        try
        {
            for (var archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
            {
                var entities = archetypes[archetypeIndex].GetEntities();
                for (var row = 0; row < entities.Length; row++)
                {
                    CollectDestroyRootIfAlive(entities[row]);
                }
            }

            DestroyCollectedEntities();
        }
        catch
        {
            _destroyOrderScratch.Clear();
            throw;
        }
    }

    /// <summary>
    /// Clears all entities from every archetype matching <paramref name="description"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Destroy(in QueryDescription)"/>, which collects and
    /// processes individual entities (cascade, dedup, compaction), this method
    /// resets each matched archetype directly. It is significantly faster
    /// (~4× vs for-loop, ~2× vs <c>Destroy(query)</c>) but makes three
    /// simplifying assumptions the caller must understand:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>No hierarchy cascade.</b> Entities in matched archetypes that have
    /// children in <i>non-matched</i> archetypes will leave those children
    /// orphaned. The orphaned children remain alive but their parent reference
    /// becomes stale — <see cref="IsAlive(Entity)"/> on the dead parent handle
    /// returns <c>false</c>, but <see cref="TryGetParent"/> will still report
    /// the old handle. Do not use this method if matched entities may have
    /// descendants outside the query.
    /// </item>
    /// <item>
    /// <b>No hierarchy unlink from parents.</b> If an entity being cleared has
    /// a parent that survives (in a non-matched archetype), the parent's child
    /// list retains a stale slot. The slot is never freed, causing a minor
    /// memory leak in the hierarchy slot pool. <see cref="HasChildren"/> may
    /// return <c>true</c> for the parent until it is itself destroyed.
    /// </item>
    /// <item>
    /// <b>No per-entity validation.</b> All entities in matched archetypes are
    /// killed unconditionally. This is safe because a query always matches
    /// entire archetypes, but means the caller cannot exclude specific entities.
    /// </item>
    /// </list>
    /// <para>
    /// <b>When to use:</b> bulk scene teardown, archetype recycling, stress
    /// tests — any scenario where you are certain no hierarchy crosses the
    /// query boundary.
    /// </para>
    /// <para>
    /// <b>When NOT to use:</b> use <see cref="Destroy(in QueryDescription)"/>
    /// instead if any matched entity might have children outside the query.
    /// </para>
    /// </remarks>
    public void Clear(in QueryDescription description)
    {
        AssertNotDisposed();

        var query = GetAdvancedQuery(in description);
        var archetypes = query.GetArchetypeSpan();
        if (archetypes.Length == 0)
            return;

        // Single pass: count total entities for free-list capacity
        var totalKill = 0;
        for (var i = 0; i < archetypes.Length; i++)
            totalKill += archetypes[i].EntityCount;

        if (totalKill == 0)
            return;

        EnsureFreeIdCapacity(_freeIdCount + totalKill);

        for (var ai = 0; ai < archetypes.Length; ai++)
        {
            var archetype = archetypes[ai];
            var entities = archetype.GetEntities();

            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var nextVersion = NextEntityVersion(entity);

                _hierarchy.ClearHierarchyState(entity);

                ref var record = ref _records[entity.Id];
                record = default;
                record.Version = nextVersion;

                _freeIds[_freeIdCount++] = new RecycledEntity(entity.Id, nextVersion);
            }

            archetype.ResetCount();
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
    /// <remarks>
    /// Intentionally omits <see cref="AssertNotDisposed"/>: after Dispose,
    /// <c>_entitySlotCount</c> is 0 so the bounds check returns false for all
    /// IDs — semantically correct (nothing is alive) without the branch overhead.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(Entity entity)
    {
        if ((uint)entity.Id >= (uint)_entitySlotCount)
            return false;

        ref var record = ref _records[entity.Id];
        return record.IsOccupied && record.Version == entity.Version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetAliveRecord(Entity entity, out EntityRecord record)
    {
        if ((uint)entity.Id >= (uint)_entitySlotCount)
        {
            record = default;
            return false;
        }

        ref var data = ref MemoryMarshal.GetArrayDataReference(_records);
        record = Unsafe.Add(ref data, entity.Id);
        return record.IsOccupied && record.Version == entity.Version;
    }

    private void AssertValidEntityId(int id, Entity entity)
    {
        if ((uint)id >= (uint)_entitySlotCount)
            ThrowInvalidEntity(entity);
    }

    /// <summary>
    /// Unchecked direct record access for hot paths that have already validated
    /// entity existence. Caller guarantees <c>entity.Id</c> is in range and the
    /// entity is alive (slot occupied, version matches).
    /// <para/>
    /// Used by <c>ComponentStore&lt;T&gt;.ApplyToWorld</c> where the Submit order
    /// ensures <c>ApplyComponentStores</c> runs before <c>ApplyDestroys</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntityRecord GetRecordFast(Entity entity)
    {
        AssertNotDisposed();
        if ((uint)entity.Id >= (uint)_entitySlotCount)
            throw new InvalidOperationException(
                $"GetRecordFast: id {entity.Id} out of range [0, {_entitySlotCount}).");
        ref var data = ref MemoryMarshal.GetArrayDataReference(_records);
        return Unsafe.Add(ref data, entity.Id);
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
    private static void ThrowInvalidEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} does not exist. The entity may have never been created, or its id is invalid.");
    }

    [DoesNotReturn]
    private static void ThrowStaleEntity(Entity entity)
    {
        throw new InvalidOperationException($"Entity {entity} is no longer alive. It may have been destroyed in a previous frame or the handle is stale.");
    }

    private void DestroySingle(Entity entity)
    {
        DestroySingle(entity, RequireLocation(entity));
    }

    private void DestroySingle(Entity entity, EntityRecord info)
    {
        var arch = info.Archetype!;

        arch.RemoveAt(info.RowIndex, out var movedEntity);
        KillEntityRecord(entity);

        if (movedEntity.IsValid)
        {
            ref var movedRecord = ref _records[movedEntity.Id];
            movedRecord.Archetype = info.Archetype;
            movedRecord.RowIndex = info.RowIndex;
        }
    }

    private void BeginDestroyCollection()
    {
        _destroyOrderScratch.Clear();

        if (++_destroyCurrentGen == 0)
        {
            Array.Clear(_destroyVisitedGen);
            _destroyCurrentGen = 1;
        }

        EnsureDestroyScratchCapacity(_entitySlotCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsMarkedForDestroy(int entityId)
    {
        return (uint)entityId < (uint)_destroyVisitedGen.Length &&
               _destroyVisitedGen[entityId] == _destroyCurrentGen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkForDestroy(int entityId)
    {
        if ((uint)entityId < (uint)_destroyVisitedGen.Length)
        {
            _destroyVisitedGen[entityId] = _destroyCurrentGen;
        }
    }

    private void CollectDestroyRootIfAlive(Entity entity)
    {
        if (!IsAlive(entity))
        {
            return;
        }

        if (IsMarkedForDestroy(entity.Id))
        {
            return;
        }

        if (_hierarchy.HasChildren(this, entity))
        {
            _hierarchy.CollectDestroySubtree(this, entity, _destroyVisitedGen, _destroyCurrentGen, _destroyOrderScratch);
            return;
        }

        MarkForDestroy(entity.Id);
        _destroyOrderScratch.Add(entity);
    }

    private void DestroyCollectedEntities()
    {
        // NOTE: O(N × groupCount) scan. In practice groupCount is 1-3 (entities
        // cluster in shared archetypes). At 100K entities × 1K archetypes this is
        // ~10ms —acceptable for batch destroy (level unload, world reset).
        // A Dictionary<Archetype,int> fallback (threshold 128) was evaluated but
        // rejected: the code complexity of a mid-loop switch to Dictionary isn't
        // worth the rare worst case. See .knowledge/kb-hardening-roadmap.md §M2.1.
        var destroyCount = _destroyOrderScratch.Count;
        if (destroyCount == 0)
        {
            _destroyOrderScratch.Clear();
            return;
        }

        EnsureDestroyBatchCapacity(destroyCount);

        var groupArchetypes = _destroyGroupArchetypes;
        var groupCounts = _destroyGroupCounts;
        var groupFirstCandidateIndices = _destroyGroupFirstCandidate;
        var candidateNextIndices = _destroyCandidateNext;
        var candidateRows = _destroyCandidateRows;
        var compactRows = _destroyCompactRows;
        var fullGroups = _destroyFullGroups;
        var groupCount = 0;

        for (var i = 0; i < destroyCount; i++)
        {
            var entity = _destroyOrderScratch[i];
            var info = RequireLocation(entity);
            var archetype = info.Archetype!;

            // Linear scan: groupCount is typically 1-3, so this beats a Dictionary
            // and allocates nothing.
            var groupIndex = -1;
            for (var j = 0; j < groupCount; j++)
            {
                if (ReferenceEquals(groupArchetypes[j], archetype))
                {
                    groupIndex = j;
                    break;
                }
            }
            if (groupIndex < 0)
            {
                groupIndex = groupCount++;
                groupArchetypes[groupIndex] = archetype;
                groupCounts[groupIndex] = 0;
                groupFirstCandidateIndices[groupIndex] = -1;
            }

            groupCounts[groupIndex]++;
            candidateRows[i] = info.RowIndex;
            candidateNextIndices[i] = groupFirstCandidateIndices[groupIndex];
            groupFirstCandidateIndices[groupIndex] = i;
        }

        MarkAndResetFullyDestroyedArchetypes(
            groupArchetypes.AsSpan(0, groupCount),
            groupCounts.AsSpan(0, groupCount),
            fullGroups.AsSpan(0, groupCount));

        CompactPartiallyDestroyedArchetypes(
            groupArchetypes.AsSpan(0, groupCount),
            groupCounts.AsSpan(0, groupCount),
            groupFirstCandidateIndices.AsSpan(0, groupCount),
            candidateNextIndices,
            candidateRows,
            fullGroups.AsSpan(0, groupCount),
            compactRows);

        for (var i = 0; i < destroyCount; i++)
        {
            KillEntityRecord(_destroyOrderScratch[i]);
        }

        // Clear archetype references so they can be GC'd if the archetype is
        // later removed from the world. Value-type arrays (int/bool) are
        // overwritten on next use and don't need clearing.
        Array.Clear(groupArchetypes, 0, groupCount);

        _destroyOrderScratch.Clear();
    }

    private static void MarkAndResetFullyDestroyedArchetypes(
        ReadOnlySpan<Archetype> groupArchetypes,
        ReadOnlySpan<int> groupCounts,
        Span<bool> fullGroups)
    {
        for (var i = 0; i < groupArchetypes.Length; i++)
        {
            var archetype = groupArchetypes[i];
            var isFullGroup = groupCounts[i] == archetype.EntityCount;
            fullGroups[i] = isFullGroup;
            if (isFullGroup)
            {
                archetype.ResetCount();
            }
        }
    }

    private void CompactPartiallyDestroyedArchetypes(
        ReadOnlySpan<Archetype> groupArchetypes,
        ReadOnlySpan<int> groupCounts,
        ReadOnlySpan<int> groupFirstCandidateIndices,
        int[] candidateNextIndices,
        int[] candidateRows,
        ReadOnlySpan<bool> fullGroups,
        int[] compactRows)
    {
        for (var groupIndex = 0; groupIndex < groupArchetypes.Length; groupIndex++)
        {
            if (fullGroups[groupIndex])
            {
                continue;
            }

            var archetype = groupArchetypes[groupIndex];
            var entityCount = archetype.EntityCount;
            var removeCount = groupCounts[groupIndex];

            // Sparse: per-entity RemoveAt using current entity record lookups.
            // O(R) — no archetype scan, no sort. Same algorithm as guarded
            // for-loop: look up current row from record, RemoveAt, update
            // moved entity's record. Each RemoveAt is O(1) swap-remove.
            //
            // Crossover at R ≈ M/5: per-entity costs R×~40 cycles (RemoveAt +
            // record lookup + update), dense scan costs M×~2 cycles + R×~30.
            // 40R = 2M + 30R → R = M/5.
            if ((long)removeCount * 5 < entityCount)
            {
                for (var ci = groupFirstCandidateIndices[groupIndex]; ci >= 0; ci = candidateNextIndices[ci])
                {
                    ref var record = ref _records[_destroyOrderScratch[ci].Id];
                    var row = record.RowIndex;
                    archetype.RemoveAt(row, out var movedEntity);
                    if (movedEntity.IsValid)
                    {
                        ref var movedRecord = ref _records[movedEntity.Id];
                        movedRecord.Archetype = archetype;
                        movedRecord.RowIndex = row;
                    }
                }
                continue;
            }

            // Dense: single-pass hole-fill compact. O(M) scan.
            var rowCount = 0;
            for (var candidateIndex = groupFirstCandidateIndices[groupIndex]; candidateIndex >= 0; candidateIndex = candidateNextIndices[candidateIndex])
            {
                compactRows[rowCount++] = candidateRows[candidateIndex];
            }

            var markGen = NextDestroyRowMarkGen(entityCount);
            archetype.CompactRemoveRows(compactRows.AsSpan(0, rowCount), _destroyRowMarks, markGen, _records);
        }
    }

    private int NextDestroyRowMarkGen(int rowCapacity)
    {
        if (_destroyRowMarks.Length < rowCapacity)
        {
            Array.Resize(ref _destroyRowMarks, rowCapacity);
        }

        if (++_destroyRowMarkGen == 0)
        {
            Array.Clear(_destroyRowMarks);
            _destroyRowMarkGen = 1;
        }

        return _destroyRowMarkGen;
    }

    private void KillEntityRecord(Entity entity)
    {
        var nextVersion = NextEntityVersion(entity);
        if (_hierarchy.HasAnyRelations(entity))
        {
            _hierarchy.RemoveDestroyed(entity);
        }

        ref var record = ref _records[entity.Id];
        record = default;
        record.Version = nextVersion;
        PushFreeIdUnsafe(entity.Id, nextVersion);
    }

    private Entity CreateInArchetype(Archetype archetype, out int rowIndex)
    {
        // Storage growth can allocate and copy. Complete it before consuming an
        // entity id so a preparation failure cannot expose a half-created record.
        archetype.EnsureCapacity(archetype.EntityCount + 1);
        Entity entity;
        BeginStructChange();
#if DEBUG
        try
        {
#endif
        var id = AcquireEntityIdUnsafe(out var version);
        entity = new Entity(id, version);
        rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;
#if DEBUG
        }
        finally
        {
            EndStructChange();
        }
#else
        EndStructChange();
#endif
        return entity;
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
            const int maxArray = 0x7FFFFFC7;
            var newCapacity = Math.Min(
                _freeIds.Length == 0 ? 4 : _freeIds.Length * 2, maxArray);
            Array.Resize(ref _freeIds, newCapacity);
        }

        _freeIds[_freeIdCount++] = new RecycledEntity(id, version);
    }

    private void EnsureFreeIdCapacity(int required)
    {
        if (_freeIds.Length >= required)
            return;

        const int maxArray = 0x7FFFFFC7;
        var newCapacity = _freeIds.Length;
        if (newCapacity == 0)
            newCapacity = 4;
        while (newCapacity < required)
        {
            if (newCapacity > maxArray / 2) { newCapacity = maxArray; break; }
            newCapacity *= 2;
        }
        Array.Resize(ref _freeIds, newCapacity);
    }

    private RecycledEntity PopFreeIdUnsafe()
    {
        return _freeIds[--_freeIdCount];
    }

    /// <summary>
    /// Removes the free-list entry <c>(id, version)</c> from its current position
    /// and re-appends it at the end (preserving all other entries' relative order).
    /// If no matching entry exists (the slot was reused by a subsequent Create),
    /// this is a no-op.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="CommandStreamCore.Submit"/> to align the free-list order
    /// of cancelled pending entities with the batch-order Release ops emitted in
    /// the wire. See B6 in <c>kb-code-review-findings.md</c>.
    /// <para/>
    /// <b>Performance note:</b> O(N) scan + O(N) shift. Same rationale as
    /// <see cref="RemoveFromFreeList"/> —a Dictionary index was evaluated but
    /// rejected. Realistic cancel batch size per frame is &lt;1000; beyond that
    /// the frame itself is already too heavy. Keep as-is.
    /// </remarks>
    internal void RepushFreeEntry(int id, int version)
    {
        for (var i = 0; i < _freeIdCount; i++)
        {
            if (_freeIds[i].Id == id && _freeIds[i].Version == version)
            {
                // Remove from current position (shift survivors left).
                _freeIdCount--;
                Array.Copy(_freeIds, i + 1, _freeIds, i, _freeIdCount - i);
                // Re-append at end.
                PushFreeIdUnsafe(id, version);
                return;
            }
        }
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
        _reservedCount++;
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

        var nextVersion = NextEntityVersion(entity);
        record.Version = nextVersion;
        PushFreeIdUnsafe(entity.Id, nextVersion);
        _reservedCount--;
        ReservedReleaseEpoch++;
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

        var nextVersion = NextEntityVersion(entity);
        record.Version = nextVersion;
        PushFreeIdUnsafe(entity.Id, nextVersion);
        _reservedCount--;
        ReservedReleaseEpoch++;
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

        if (_destroyRowMarks.Length < entityCount)
        {
            Array.Resize(ref _destroyRowMarks, Math.Max(entityCount, _destroyRowMarks.Length * 2));
        }

        // Note: batch scratch arrays (_destroyGroupArchetypes, etc.) are sized
        // lazily in EnsureDestroyBatchCapacity(destroyCount) — only as large as
        // the actual destroy batch, not the total entity slot count.
    }

    private void EnsureDestroyBatchCapacity(int count)
    {
        if (_destroyGroupArchetypes.Length >= count) return;

        var newLen = Math.Max(count, _destroyGroupArchetypes.Length * 2);
        if (newLen < 16) newLen = 16;

        Array.Resize(ref _destroyGroupArchetypes, newLen);
        Array.Resize(ref _destroyGroupCounts, newLen);
        Array.Resize(ref _destroyGroupFirstCandidate, newLen);
        Array.Resize(ref _destroyCandidateNext, newLen);
        Array.Resize(ref _destroyCandidateRows, newLen);
        Array.Resize(ref _destroyCompactRows, newLen);
        Array.Resize(ref _destroyFullGroups, newLen);
    }

    private void EnsureReplayReservation(Entity entity)
    {
        if ((uint)entity.Id < (uint)_entitySlotCount &&
            !_records[entity.Id].IsOccupied &&
            _records[entity.Id].Version == entity.Version)
        {
            RemoveFromFreeList(entity);
            _reservedCount++;
            return;
        }

        if (entity.Id == _entitySlotCount && entity.Version == 1)
        {
            ReserveReplayFreshSlot(entity);
            _reservedCount++;
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

    /// <remarks>
    /// Scans from the tail because the common case is removing the most
    /// recently pushed entry (stack-top, the entity just reserved).
    /// Reverse scan makes that a single iteration.
    /// <para/>
    /// Survivors are shifted left (not swap-removed) to preserve free-list
    /// order. This is essential: Submit's Create pops from the end via
    /// <see cref="PopFreeIdUnsafe"/> (LIFO), so a swap-remove here would
    /// reorder survivors and diverge from Submit's free-list state when
    /// Reserve+Release are interleaved within one frame (cancelled pending
    /// entities). Shift keeps Replay's free-list order identical to Submit's.
    /// <para/>
    /// <b>Performance note:</b> O(N) scan + O(N) Array.Copy shift. This is
    /// intentionally NOT optimized with a Dictionary index —the callers
    /// (Replay Reserve ops, Submit cancel) are batch operations where
    /// realistic batch size is &lt;1000 per frame. At 100K entries the shift
    /// is ~0.5ms (memmove), but 100K Reserve ops in one frame is itself
    /// impractical. A Dictionary index would add O(1) overhead to the hot
    /// Push/Pop paths for marginal benefit. Keep as-is.
    /// See .knowledge/kb-hardening-roadmap.md §M2.3.
    /// </remarks>
    private void RemoveFromFreeList(Entity entity)
    {
        for (var i = _freeIdCount - 1; i >= 0; i--)
        {
            if (_freeIds[i].Id == entity.Id && _freeIds[i].Version == entity.Version)
            {
                _freeIdCount--;
                Array.Copy(_freeIds, i + 1, _freeIds, i, _freeIdCount - i);
                return;
            }
        }
    }

    /// <summary>
    /// Places a previously-reserved entity into an archetype at an already-allocated
    /// row. Used by the CommandStream CreateMany bulk materialization fast path,
    /// which pre-allocates all rows via <see cref="Archetype.AllocateRows"/> and
    /// then fills them in a tight loop. Performs the same record update and
    /// reserved-count guard as <see cref="PlaceEntityInArchetype"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MaterializeReservedEntityAt(Entity entity, Archetype archetype, int rowIndex)
    {
        archetype.WriteEntityAt(rowIndex, entity);
        ref var record = ref _records[entity.Id];
        if (!record.IsOccupied && record.Version == entity.Version)
            _reservedCount--;
        record.Archetype = archetype;
        record.RowIndex = rowIndex;
    }

    /// <summary>
    /// Returns <c>entity.Version + 1</c>, wrapping to 1 on overflow past <see cref="int.MaxValue"/>.
    /// Must be called before any mutation (archetype RemoveAt, hierarchy cleanup, record write).
    /// </summary>
    private static int NextEntityVersion(Entity entity)
    {
        return entity.Version == int.MaxValue ? 1 : entity.Version + 1;
    }

    internal readonly record struct RecycledEntity(int Id, int Version);

}
