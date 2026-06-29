using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Owns entity storage and queries.
/// </summary>
/// <remarks>
/// <b>Threading model</b> (read the following before touching a World from
/// multiple threads):
/// <list type="bullet">
/// <item><b>Structural changes</b> — <see cref="Create"/>, <see cref="Destroy"/>,
/// <see cref="Add{T}"/>, <see cref="Set{T}"/>, <see cref="Remove{T}"/>,
/// <see cref="Link"/>, <see cref="Clone(Entity)"/> — are <b>not</b> thread-safe
/// and must be issued from a single thread (typically the main game thread)
/// or deferred through <c>CommandStream</c>.</item>
/// <item><b>Reads</b> — <see cref="Has{T}"/>, <see cref="Get{T}"/>,
/// <see cref="TryGet{T}"/>, query iteration via <c>ForEachChunkParallel</c> —
/// may run in parallel with other readers, but <b>not</b> concurrent with a
/// structural change. Snapshot the archetype list (via Query) before
/// dispatching parallel work.</item>
/// <item><c>ReserveDeferredEntity</c> takes a lock and is safe to call from
/// background threads (e.g. async snapshot building); this is the only
/// write path that is internally synchronized.</item>
/// </list>
/// </remarks>
public sealed partial class World : IDisposable
{
    private const int DefaultChunkCapacity = 128;

    private readonly Dictionary<Signature, Archetype> _archetypes = new();
    // Mask-keyed cache for zero-allocation archetype lookup on the Replay path.
    // Only populated for archetypes whose ComponentMask is canonical — i.e. every
    // component id is < 512 (PopCount(mask) == component count). Archetypes with
    // high-id components are intentionally absent from this cache and always go
    // through the Signature-keyed dictionary above, preserving the library's
    // "arbitrary component id range" contract.
    private readonly Dictionary<ComponentMask, Archetype> _archetypeByMask = new();

    // Reused scratch for the Replay pre-scan pass (counts Creates per archetype
    // so we can pre-size archetype storage and avoid per-Create doubling
    // allocations). Cleared at the start of every Replay call; never allocated
    // per-call in steady state.
    private readonly Dictionary<Archetype, int> _replayCreateCounts = new();

    // Placeholder → local real entity mapping for FrameDelta replay.
    // Indexed by placeholder seq (Entity.Version when Id == -1).
    // Reused across ReplayCore calls — never allocated per-call in steady state.
    // mapLen resets to 0 at the start of each ReplayCore call to prevent stale
    // mappings from previous frames leaking into the current replay.
    private Entity[] _replayPlaceholderMap = [];

    private readonly HierarchyTable _hierarchy = new();
    private EntityRecord[] _records;
    private int _entitySlotCount;
    private Dictionary<QueryDescription, QueryFilter> _queryFiltersByDescription = new();
    private Dictionary<QueryFilter, MiniArch.Core.Query> _queries = new();
    private Archetype[] _archetypeSnapshot = Array.Empty<Archetype>();
    private readonly int _chunkCapacity;
    private RecycledEntity[] _freeIds;
    private int _freeIdCount;

    private volatile int _createArchetypeCacheGeneration;
    private readonly List<Entity> _destroyOrderScratch;
    private int[] _destroyVisitedGen = [];
    private int _destroyCurrentGen;

    private bool _disposed;

    /// <summary>
    /// Creates a world.
    /// </summary>
    public World(int chunkCapacity = DefaultChunkCapacity, int entityCapacity = 64)
    {
        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity), "Chunk capacity must be positive.");
        }

        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity), "Entity capacity must be non-negative.");
        }

        _chunkCapacity = chunkCapacity;
        _records = new EntityRecord[entityCapacity];
        _entitySlotCount = 0;
        _freeIds = entityCapacity == 0 ? Array.Empty<RecycledEntity>() : new RecycledEntity[entityCapacity];
        _destroyVisitedGen = entityCapacity == 0 ? [] : new int[entityCapacity];
        _destroyOrderScratch = new List<Entity>(entityCapacity);
    }

    /// <summary>
    /// Releases owned runtime state.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archetypes.Clear();
        _archetypeByMask.Clear();
        _replayCreateCounts.Clear();
        _queryFiltersByDescription.Clear();
        _queries.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _entitySlotCount = 0;
        _records = Array.Empty<EntityRecord>();
        _freeIdCount = 0;
        _destroyVisitedGen = [];
        _destroyCurrentGen = 0;
        _hierarchy.Reset();
        _createArchetypeCacheGeneration = int.MaxValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
#if DEBUG
        if (_disposed) throw new ObjectDisposedException(nameof(World));
#endif
    }

    /// <summary>
    /// Gets the entity metadata capacity.
    /// </summary>
    public int EntityCapacity => _records.Length;

    /// <summary>
    /// Gets the number of currently alive entities.
    /// </summary>
    public int EntityCount => _entitySlotCount - _freeIdCount;

    /// <summary>
    /// Gets a snapshot of world-level statistics.
    /// </summary>
    public WorldStats GetStats() => new(EntityCount, _records.Length, _freeIdCount, _archetypes.Count);

    /// <summary>
    /// Gets archetype-level statistics for all archetypes.
    /// </summary>
    public ArchetypeStats[] GetArchetypeStats()
    {
        var result = new ArchetypeStats[_archetypes.Count];
        var i = 0;
        foreach (var arch in _archetypes.Values)
        {
            result[i++] = new ArchetypeStats(arch.EntityCount, arch.Capacity, arch.ComponentTypes);
        }

        return result;
    }

    internal int ChunkCapacity => _chunkCapacity;

    internal int EntitySlotCount => _entitySlotCount;

    internal ReadOnlySpan<EntityRecord> EntityRecords => _records.AsSpan(0, _entitySlotCount);

    internal Archetype[] Archetypes => Volatile.Read(ref _archetypeSnapshot);

    internal HierarchyTable Hierarchy => _hierarchy;

    internal int ArchetypeCount => Volatile.Read(ref _archetypeSnapshot).Length;

    internal int ArchetypeCacheGeneration => _createArchetypeCacheGeneration;

    /// <summary>
    /// Links a child to a parent.
    /// </summary>
    public void Link(Entity parent, Entity child)
    {
        ThrowIfDisposed();
        _hierarchy.Link(this, parent, child);
    }

    /// <summary>
    /// Unlinks a child.
    /// </summary>
    public void Unlink(Entity child)
    {
        ThrowIfDisposed();
        GetRequiredLocation(child);
        _hierarchy.Unlink(child);
    }

    /// <summary>
    /// Tries to get a parent entity.
    /// </summary>
    public bool TryGetParent(Entity child, out Entity parent)
    {
        ThrowIfDisposed();
        return _hierarchy.TryGetParent(this, child, out parent);
    }

    /// <summary>
    /// Gets the direct children of an entity.
    /// </summary>
    public List<Entity> GetChildren(Entity parent)
    {
        ThrowIfDisposed();
        return _hierarchy.GetChildren(this, parent);
    }

    /// <summary>
    /// Tries to read a component directly from an entity.
    /// </summary>
    public bool TryGet<T>(Entity entity, out T component) where T : unmanaged
    {
        ThrowIfDisposed();
        if (!TryGetLocation(entity, out var info))
        {
            component = default!;
            return false;
        }

        var componentType = GetComponentType<T>();
        if (!info.Archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            component = default!;
            return false;
        }

        component = info.Archetype
            .GetComponentAt<T>(componentIndex, info.RowIndex);
        return true;
    }

    /// <summary>
    /// Checks whether an entity has a specific component.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>(Entity entity) where T : unmanaged
    {
        ThrowIfDisposed();
        if ((uint)entity.Id >= (uint)_entitySlotCount)
            return false;

        ref var record = ref _records[entity.Id];
        if (!record.IsOccupied || record.Version != entity.Version)
            return false;

        return record.Archetype!.TryGetComponentIndex(Component<T>.ComponentType, out _);
    }

    /// <summary>
    /// Gets a component directly without version or bounds checks.
    /// Use only when the entity is known to be alive and the component is known to exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Get<T>(Entity entity) where T : unmanaged
    {
#if DEBUG
        if ((uint)entity.Id >= (uint)_entitySlotCount)
            throw new InvalidOperationException($"Entity {entity} is not alive.");
#endif
        ref var record = ref _records[entity.Id];
#if DEBUG
        if (!record.IsOccupied || record.Version != entity.Version)
            throw new InvalidOperationException($"Entity {entity} is not alive.");
#endif
        var arch = record.Archetype!;
        return arch.GetComponentAt<T>(arch.GetComponentIndexFast(GetComponentType<T>()), record.RowIndex);
    }

    /// <summary>
    /// Gets a component by reference directly without version or bounds checks.
    /// Use only when the entity is known to be alive and the component is known to exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetRef<T>(Entity entity) where T : unmanaged
    {
#if DEBUG
        if ((uint)entity.Id >= (uint)_entitySlotCount)
            throw new InvalidOperationException($"Entity {entity} is not alive.");
#endif
        ref var record = ref _records[entity.Id];
#if DEBUG
        if (!record.IsOccupied || record.Version != entity.Version)
            throw new InvalidOperationException($"Entity {entity} is not alive.");
#endif
        var arch = record.Archetype!;
        return ref arch.GetComponentRefAt<T>(arch.GetComponentIndexFast(GetComponentType<T>()), record.RowIndex);
    }

    /// <summary>
    /// Creates a cached <see cref="EntityAccessor"/> for multiple component
    /// reads/writes on the same entity. The entity→(archetype,chunk,row) lookup
    /// is performed once; subsequent <see cref="EntityAccessor.Get{T}"/>,
    /// <see cref="EntityAccessor.Set{T}"/>, and <see cref="EntityAccessor.Has{T}"/>
    /// calls on the returned accessor skip the entity lookup entirely.
    /// </summary>
    /// <remarks>
    /// Discard the accessor before any structural change (Add/Remove) that may
    /// move the entity to a different archetype.
    /// </remarks>
    public EntityAccessor Access(Entity entity)
    {
        ThrowIfDisposed();
        if (!TryGetLocation(entity, out var info))
        {
            throw new InvalidOperationException(
                $"Entity '{entity}' is not alive.");
        }

        return new EntityAccessor(info.Archetype, info.RowIndex);
    }

    /// <summary>
    /// Deep-clones an entity and its entire child subtree.
    /// All component data is copied into new entities in the same archetypes.
    /// Parent-child links within the subtree are preserved; the clone root has no parent.
    /// </summary>
    /// <param name="source">The entity to clone. Must be alive.</param>
    /// <returns>A new entity that is the root of the cloned subtree.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="source"/> is no longer alive.</exception>
    public Entity Clone(Entity source)
    {
        ThrowIfDisposed();
        var root = CloneSingle(source);
        DeepCloneChildren(source, root);
        return root;
    }

    private Entity CloneSingle(Entity source)
    {
        var sourceInfo = GetRequiredLocation(source);
        var archetype = sourceInfo.Archetype!;
        var entity = CreateInArchetype(archetype, out var destRow);
        archetype.CopySharedComponentsFrom(sourceInfo.Archetype!, sourceInfo.RowIndex, destRow);
        return entity;
    }

    private void DeepCloneChildren(Entity sourceRoot, Entity cloneRoot)
    {
        if (!_hierarchy.HasChildren(sourceRoot)) return;

        var stack = ArrayPool<CloneWork>.Shared.Rent(16);
        var stackCount = 0;
        try
        {
            ArrayPoolUtil.PushPooled(ref stack, ref stackCount, new CloneWork(sourceRoot, cloneRoot));
            while (stackCount > 0)
            {
                var work = stack[--stackCount];
                foreach (var child in _hierarchy.EnumerateChildren(this, work.Source))
                {
                    var cloneChild = CloneSingle(child);
                    _hierarchy.Link(this, work.CloneEntity, cloneChild);
                    ArrayPoolUtil.PushPooled(ref stack, ref stackCount, new CloneWork(child, cloneChild));
                }
            }
        }
        finally
        {
            ArrayPool<CloneWork>.Shared.Return(stack);
        }
    }

    /// <summary>
    /// Creates a snapshot-equivalent clone of this world as a brand-new,
    /// independent <see cref="World"/> (fresh archetype caches, own capacity).
    /// Use for branching simulations or materializing a long-lived checkpoint.
    /// <para/>
    /// <b>Not</b> the right tool for high-frequency in-place rollback (GGPO-style
    /// 60fps save/restore): each call allocates a new world. For that, use
    /// <see cref="CaptureState"/> / <see cref="RestoreState"/> which recycles a
    /// single opaque handle and allocates zero GC memory in steady state.
    /// </summary>
    public World Clone()
    {
        return WorldClone.Clone(this);
    }

    internal Archetype GetOrCreateArchetype(Signature signature)
    {
        if (_archetypes.TryGetValue(signature, out var archetype))
        {
            return archetype;
        }

        archetype = new Archetype(signature, ResolveComponentTypes(signature), _chunkCapacity);
        _archetypes.Add(signature, archetype);
        CacheArchetypeByMaskIfCanonical(signature, archetype);
        PublishArchetypeSnapshot(archetype);
        return archetype;
    }

    // Populate the mask cache. Only called when an archetype is freshly created,
    // so the single dictionary write is amortized across all subsequent lookups.
    // High-id archetypes (mask not canonical) are intentionally left out — see
    // the comment on _archetypeByMask.
    private void CacheArchetypeByMaskIfCanonical(Signature signature, Archetype archetype)
    {
        if (IsMaskCanonical(signature.ComponentMask, signature.Count))
        {
            _archetypeByMask[signature.ComponentMask] = archetype;
        }
    }

    // A ComponentMask is "canonical" for a signature when every component id is
    // representable in the 512-bit mask — i.e. the popcount of the mask equals
    // the component count. If any id is >= 512 its bit is dropped from the mask
    // and popcount would be smaller than the actual count, so the mask is not a
    // unique key for that set.
    private static bool IsMaskCanonical(ComponentMask mask, int componentCount)
    {
        var bitCount = BitOperations.PopCount(mask.B0)
                     + BitOperations.PopCount(mask.B1)
                     + BitOperations.PopCount(mask.B2)
                     + BitOperations.PopCount(mask.B3)
                     + BitOperations.PopCount(mask.B4)
                     + BitOperations.PopCount(mask.B5)
                     + BitOperations.PopCount(mask.B6)
                     + BitOperations.PopCount(mask.B7);
        return bitCount == componentCount;
    }

    /// <summary>
    /// Zero-allocation archetype lookup keyed by ComponentMask. Used by the
    /// Replay path where components are decoded from a byte buffer and we want
    /// to avoid constructing a Signature just to look the archetype up.
    /// </summary>
    /// <remarks>
    /// <b>Caller contract:</b> <paramref name="types"/> must contain only ids
    /// &lt; 512 — i.e. the caller must have verified the mask is canonical.
    /// Violating this would cause two different high-id signatures to collide
    /// on the same mask and silently return the wrong archetype.
    /// </remarks>
    internal Archetype GetOrCreateArchetypeByMask(ComponentMask mask, ReadOnlySpan<ComponentType> types)
    {
        if (_archetypeByMask.TryGetValue(mask, out var archetype))
        {
            return archetype;
        }

        // Miss: build a Signature (one-time alloc per unique component set) and
        // route through the canonical path, which populates the mask cache.
        var typesArray = types.ToArray();
        var signature = new Signature(typesArray);
        return GetOrCreateArchetype(signature);
    }

    private Type[] ResolveComponentTypes(Signature signature)
    {
        var componentCount = signature.Count;
        if (componentCount == 0)
        {
            return Array.Empty<Type>();
        }

        var types = new Type[componentCount];
        var components = signature.AsSpan();
        for (var index = 0; index < componentCount; index++)
        {
            types[index] = ComponentRegistry.Shared.GetType(components[index]);
        }

        return types;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ComponentType GetComponentType<T>()
    {
        return Component<T>.ComponentType;
    }

    /// <summary>
    /// Replays a frame delta into this world: reserves entities, materializes created entities,
    /// applies hierarchy link/unlink, add/set/remove components, and destroys entities in standard order.
    /// </summary>
    /// <remarks>
    /// Supports both placeholder deltas (<see cref="CommandStream.Snapshot"/>) and real-id deltas
    /// (<see cref="CommandStream.SubmitAndSnapshotAsync"/>). Placeholder entities (<c>Id == -1</c>)
    /// allocate fresh local ids via <c>ReserveDeferredEntityBatch</c> and are mapped through a
    /// per-replay <c>seq → local real</c> table. Real-id entities go through
    /// <see cref="EnsureReplayReservation"/> to verify allocator synchronization.
    /// </remarks>
    public void Replay(FrameDelta delta) => ReplayCore(delta);

    private unsafe void ReplayCore(FrameDelta delta)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(delta);

        // Pre-scan: size archetype storage and the entity record array up-front
        // so the main pass never hits a doubling resize on the hot Create path.
        PreScanForCapacity(delta);

        var map = _replayPlaceholderMap;
        var mapLen = 0;

        // Pin the backing buffer once for the entire replay. Every Create op
        // reads component data from this buffer via direct pointer arithmetic,
        // and Add/Set ops do the same — sharing one pin avoids per-op fixed
        // overhead.
        fixed (byte* bufPtr = delta._buffer)
        {
            var decoder = delta.GetDecoder();
            while (decoder.MoveNext())
            {
                switch (decoder.Kind)
                {
                    case DeltaOpKind.Reserve:
                    {
                        var raw = decoder.Entity;
                        if (raw.Id < 0)
                        {
                            // Placeholder: allocate a fresh local id.
                            var real = ReserveDeferredEntityBatch();
                            EnsurePlaceholderMap(ref map, ref mapLen, raw.Version);
                            map[raw.Version] = real;
                        }
                        else
                        {
                            EnsureReplayReservation(raw);
                        }
                        break;
                    }

                    case DeltaOpKind.Release:
                        ReleaseReservedEntity(ResolveReplayEntity(decoder.Entity, map, mapLen));
                        break;

                    case DeltaOpKind.Create:
                        ReplayCreateOpResolved(ResolveReplayEntity(decoder.Entity, map, mapLen), ref decoder, bufPtr);
                        break;

                    case DeltaOpKind.Link:
                    {
                        var child = ResolveReplayEntity(decoder.Entity, map, mapLen);
                        var parent = ResolveReplayEntity(decoder.ReadExtraEntity(), map, mapLen);
                        Link(parent, child);
                        break;
                    }

                    case DeltaOpKind.Unlink:
                        Unlink(ResolveReplayEntity(decoder.Entity, map, mapLen));
                        break;

                    case DeltaOpKind.Add:
                    case DeltaOpKind.Set:
                    {
                        var entity = ResolveReplayEntity(decoder.Entity, map, mapLen);
                        var comp = decoder.ReadComponentType();
                        var dataSize = decoder.ReadVarint();
                        var dataStart = decoder.CurrentPosition;
                        _ = decoder.ReadBytes(dataSize);
                        ApplyRawAddOrSet(entity, comp, bufPtr + dataStart);
                        break;
                    }

                    case DeltaOpKind.Remove:
                    {
                        var entity = ResolveReplayEntity(decoder.Entity, map, mapLen);
                        var comp = decoder.ReadComponentType();
                        RemoveBoxed(entity, comp);
                        break;
                    }

                    case DeltaOpKind.Destroy:
                    {
                        var entity = ResolveReplayEntity(decoder.Entity, map, mapLen);
                        if (IsAlive(entity))
                            Destroy(entity);
                        break;
                    }

                    default:
                        throw new InvalidOperationException(
                            $"Unknown FrameDelta operation kind: 0x{(byte)decoder.Kind:X2}");
                }
            }
        }

        _replayPlaceholderMap = map;
    }

    // ── Replay placeholder helpers ─────────────────────────────────────

    private void EnsurePlaceholderMap(ref Entity[] map, ref int mapLen, int seq)
    {
        if (seq < mapLen) return;
        var newLen = map.Length == 0 ? 64 : map.Length;
        while (newLen <= seq) newLen *= 2;
        Array.Resize(ref map, newLen);
        for (var i = mapLen; i < newLen; i++)
            map[i] = new Entity(-1, -1); // sentinel: not yet mapped
        mapLen = newLen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Entity ResolveReplayEntity(Entity wireEntity, Entity[] map, int mapLen)
    {
        if (wireEntity.Id >= 0) return wireEntity;
        if ((uint)wireEntity.Version >= (uint)mapLen ||
            map[wireEntity.Version].Id < 0)
            throw new InvalidOperationException(
                $"Unresolved placeholder entity seq={wireEntity.Version} in FrameDelta replay. " +
                "The delta is malformed: a placeholder appears without a preceding Reserve op.");
        return map[wireEntity.Version];
    }

    /// <summary>
    /// Walks the delta once (read-only) to count Creates per existing archetype
    /// and find the max entity id, then pre-sizes archetype storage and the
    /// entity record array. This eliminates the cascade of doubling allocations
    /// that would otherwise happen inside <see cref="Archetype.AddEntity"/> and
    /// <see cref="EnsureCapacity"/> on the hot Create path.
    /// </summary>
    /// <remarks>
    /// The pre-scan computes a ComponentMask per Create payload to identify the
    /// target archetype without allocating a Signature. Creates with non-canonical
    /// masks (any component id &gt;= 512) are skipped here and rely on the
    /// main pass's natural growth — they are rare and preserve the library's
    /// support for arbitrary component id ranges.
    /// </remarks>
    private void PreScanForCapacity(FrameDelta delta)
    {
        _replayCreateCounts.Clear();
        var maxEntityId = -1;

        var scanner = delta.GetDecoder();
        while (scanner.MoveNext())
        {
            var id = scanner.Entity.Id;
            // Placeholder entities (Id < 0) have not been allocated yet;
            // their real ids are assigned during the main replay pass.
            // Skip them for maxEntityId tracking — the entity record array
            // will grow on-demand inside ReserveDeferredEntityBatch.
            if (id >= 0 && id > maxEntityId) maxEntityId = id;

            switch (scanner.Kind)
            {
                case DeltaOpKind.Create:
                {
                    var compCount = scanner.ReadVarint();
                    if (compCount <= 0) break;

                    var builder = new MaskBuilder();
                    for (var i = 0; i < compCount; i++)
                    {
                        builder.SetBit(scanner.ReadComponentType().Value);
                        var size = scanner.ReadVarint();
                        scanner.ReadBytes(size);
                    }

                    if (builder.BitsSet == compCount)
                    {
                        var mask = builder.ToMask();
                        if (_archetypeByMask.TryGetValue(mask, out var arch))
                        {
                            _replayCreateCounts[arch] =
                                _replayCreateCounts.GetValueOrDefault(arch, 0) + 1;
                        }
                        // Archetype not yet in cache: first encounter will create it
                        // in the main pass; pre-sizing is skipped for that one.
                    }
                    break;
                }

                case DeltaOpKind.Link:
                {
                    var parent = scanner.ReadExtraEntity();
                    if (parent.Id >= 0 && parent.Id > maxEntityId) maxEntityId = parent.Id;
                    break;
                }

                case DeltaOpKind.Add:
                case DeltaOpKind.Set:
                    // Advance past component type + size-prefixed payload.
                    scanner.SkipData();
                    break;

                case DeltaOpKind.Remove:
                    // Advance past the component type only.
                    scanner.ReadComponentType();
                    break;

                // Reserve / Release / Unlink / Destroy carry no payload beyond
                // the entity that MoveNext has already consumed.
            }
        }

        if (maxEntityId >= _records.Length)
        {
            EnsureCapacity(maxEntityId + 1);
        }

        foreach (var (arch, count) in _replayCreateCounts)
        {
            arch.EnsureCapacity(arch.EntityCount + count);
        }
    }

    /// <summary>
    /// Materializes a single Create op with a pre-resolved entity.
    /// The entity may have been resolved from a placeholder via
    /// <see cref="ResolveReplayEntity"/> in <see cref="ReplayCore"/>.
    /// </summary>
    private unsafe void ReplayCreateOpResolved(Entity entity, ref FrameDelta.OpDecoder decoder, byte* bufPtr)
    {
        var compCount = decoder.ReadVarint();
        if (compCount < 0)
            throw new InvalidOperationException("Corrupt FrameDelta: negative component count.");

        if (compCount == 0)
        {
            MaterializeReservedEntityCore(entity, Signature.Empty, Array.Empty<RawComponentValue>());
            return;
        }

        // ComponentType is a 4-byte struct, so 32 entries cost 128 bytes of
        // stack per span (256 bytes total) — well within limits. Entities with
        // more components spill to ArrayPool, which is amortized zero-allocation.
        const int MaxStackComponents = 32;

        if (compCount <= MaxStackComponents)
        {
            Span<ComponentType> types = stackalloc ComponentType[compCount];
            Span<int> offsets = stackalloc int[compCount];
            ReplayCreateOpCore(ref decoder, bufPtr, entity, compCount, types, offsets);
        }
        else
        {
            var poolTypes = ArrayPool<ComponentType>.Shared.Rent(compCount);
            var poolOffsets = ArrayPool<int>.Shared.Rent(compCount);
            try
            {
                ReplayCreateOpCore(
                    ref decoder, bufPtr, entity, compCount,
                    poolTypes.AsSpan(0, compCount), poolOffsets.AsSpan(0, compCount));
            }
            finally
            {
                ArrayPool<ComponentType>.Shared.Return(poolTypes);
                ArrayPool<int>.Shared.Return(poolOffsets);
            }
        }
    }

    /// <summary>
    /// Body of <see cref="ReplayCreateOpResolved"/>. Takes the scratch spans as scoped
    /// parameters so the stackalloc path can pass stack memory without the
    /// compiler worrying about escape.
    /// </summary>
    private unsafe void ReplayCreateOpCore(
        ref FrameDelta.OpDecoder decoder, byte* bufPtr,
        Entity entity, int compCount,
        scoped Span<ComponentType> types, scoped Span<int> offsets)
    {
        // One pass over the Create payload: read type, build mask, record the
        // data offset, advance past data. Data is written back below once the
        // archetype (and therefore the row) is known.
        var builder = new MaskBuilder();
        for (var i = 0; i < compCount; i++)
        {
            var t = decoder.ReadComponentType();
            types[i] = t;
            builder.SetBit(t.Value);

            var size = decoder.ReadVarint();
            offsets[i] = decoder.CurrentPosition;
            decoder.ReadBytes(size);
        }

        Archetype archetype;
        if (builder.BitsSet == compCount)
        {
            // Canonical mask: every id < 512, safe to use the mask cache.
            archetype = GetOrCreateArchetypeByMask(builder.ToMask(), types);
        }
        else
        {
            // Non-canonical mask (one or more ids >= 512): cannot use the mask
            // cache without colliding. Fall back to the Signature-keyed dictionary
            // so high-id archetypes are looked up correctly. This allocates once
            // per unique component set (amortized zero across replays) and is the
            // same path the structural-change APIs already use.
            archetype = GetOrCreateArchetype(new Signature(types.ToArray()));
        }

        // Direct write from delta buffer into archetype columns. No intermediate
        // RawComponentValue[] and no per-type box.
        MaterializeReservedEntityRaw(entity, archetype, types, offsets, bufPtr);
    }

    internal void MaterializeReservedEntity(
        Entity entity,
        IReadOnlyList<RawComponentValue> components,
        bool reservationChecked = false)
    {
        if (!reservationChecked)
        {
            EnsureReplayReservation(entity);
        }

        var signature = BuildReplaySignature(components);
        MaterializeReservedEntityCore(entity, signature, components);
    }

    /// <summary>
    /// Adds an entity to an archetype and records its location. Shared prologue
    /// for all MaterializeReservedEntity* variants.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PlaceEntityInArchetype(Entity entity, Archetype archetype)
    {
        var rowIndex = archetype.AddEntity(entity);
        ref var record = ref _records[entity.Id];
        record.Archetype = archetype;
        record.RowIndex = rowIndex;
        return rowIndex;
    }

    internal unsafe void MaterializeReservedEntityDirect(Entity entity, Archetype archetype, ReadOnlySpan<RawComponentValue> components)
    {
        var rowIndex = PlaceEntityInArchetype(entity, archetype);

        for (var index = 0; index < components.Length; index++)
        {
            ref readonly var component = ref components[index];
            var columnIndex = archetype.GetComponentIndexFast(component.ComponentType);
            fixed (byte* ptr = component.Data)
            {
                archetype.WriteComponentRaw(columnIndex, rowIndex, ptr + component.DataOffset);
            }
        }
    }

    /// <summary>
    /// Materializes a reserved entity writing component data from a raw byte buffer.
    /// Avoids per-type store lookups — caller provides direct pointers.
    /// </summary>
    internal unsafe void MaterializeReservedEntityRaw(
        Entity entity, Archetype archetype,
        ReadOnlySpan<ComponentType> types,
        ReadOnlySpan<int> offsets,
        byte* buffer)
    {
        var rowIndex = PlaceEntityInArchetype(entity, archetype);

        for (var i = 0; i < types.Length; i++)
        {
            var colIdx = archetype.GetComponentIndexFast(types[i]);
            archetype.WriteComponentRaw(colIdx, rowIndex, buffer + offsets[i]);
        }
    }

    private void MaterializeReservedEntityCore(
        Entity entity,
        Signature signature,
        IReadOnlyList<RawComponentValue> components)
    {
        var archetype = GetOrCreateArchetype(signature);
        var rowIndex = PlaceEntityInArchetype(entity, archetype);

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            var columnIndex = archetype.GetComponentIndexFast(component.ComponentType);
            unsafe
            {
                fixed (byte* ptr = component.Data)
                {
                    archetype.WriteComponentRaw(columnIndex, rowIndex, ptr + component.DataOffset);
                }
            }
        }
    }

    private static Signature BuildReplaySignature(IReadOnlyList<RawComponentValue> components)
    {
        if (components.Count == 0)
        {
            return Signature.Empty;
        }

        var componentTypes = new ComponentType[components.Count];
        for (var index = 0; index < components.Count; index++)
        {
            componentTypes[index] = components[index].ComponentType;
        }

        return new Signature(componentTypes);
    }


    // ──────────────────────────────────────────────
    //  Tier 1 in-memory rollback snapshot
    // ──────────────────────────────────────────────

    private WorldStateSnapshot? _stateSnapshotSpare;

    /// <summary>
    /// Captures the world's current mutable state into an opaque handle
    /// for later rollback via <see cref="RestoreState"/>.
    /// <para/>
    /// The snapshot is recycled after restore — in steady state (warm cache)
    /// this method allocates zero GC memory. Suitable for GGPO-style 60fps
    /// frame save/restore cycles at &lt;1000 entities.
    /// </summary>
    public WorldStateSnapshot CaptureState()
    {
        ThrowIfDisposed();
        var snap = _stateSnapshotSpare ?? new WorldStateSnapshot();
        snap.Clear();

        // Records
        snap.EnsureRecordsCapacity(_entitySlotCount);
        Array.Copy(_records, snap.Records, _entitySlotCount);
        snap.EntitySlotCount = _entitySlotCount;

        // Free ids
        snap.EnsureFreeIdsCapacity(_freeIdCount);
        for (var i = 0; i < _freeIdCount; i++)
        {
            snap.FreeIds[i] = _freeIds[i].Id;
            snap.FreeIdVersions[i] = _freeIds[i].Version;
        }
        snap.FreeIdCount = _freeIdCount;

        // Per-archetype data
        snap.EnsureArchetypeBackupsCapacity(_archetypes.Count);
        var backupIdx = 0;
        foreach (var arch in _archetypes.Values)
        {
            if (arch.EntityCount == 0) continue;
            ref var entry = ref snap.ArchetypeBackups[backupIdx];
            if (!arch.IsChunked)
                ArchetypeBackupEntry.CopyFromNonChunked(arch, ref entry);
            else
                ArchetypeBackupEntry.CopyFromChunked(arch, ref entry);
            backupIdx++;
        }
        snap.ArchetypeBackupCount = backupIdx;

        // Hierarchy
        _hierarchy.CaptureState(snap);

        _stateSnapshotSpare = null;
        return snap;
    }

    /// <summary>
    /// Restores the world to a previously captured state. The snapshot is
    /// recycled internally and should not be used after this call (it becomes
    /// the world's spare for the next <see cref="CaptureState"/>).
    /// <para/>
    /// After restore, all query caches and archetype transition caches are
    /// invalidated and will rebuild on next use.
    /// </summary>
    public void RestoreState(WorldStateSnapshot snapshot)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);

        // Records
        if (_records.Length < snapshot.EntitySlotCount)
            Array.Resize(ref _records, snapshot.EntitySlotCount);
        Array.Copy(snapshot.Records, _records, snapshot.EntitySlotCount);
        _entitySlotCount = snapshot.EntitySlotCount;

        // Free ids
        if (_freeIds.Length < snapshot.FreeIdCount)
            Array.Resize(ref _freeIds, snapshot.FreeIdCount);
        for (var i = 0; i < snapshot.FreeIdCount; i++)
            _freeIds[i] = new RecycledEntity(snapshot.FreeIds[i], snapshot.FreeIdVersions[i]);
        _freeIdCount = snapshot.FreeIdCount;

        // Reset all archetypes to empty, then restore backed-up ones.
        // This handles prediction-created archetypes that have no backup.
        foreach (var arch in _archetypes.Values)
            arch.ResetCount();

        for (var i = 0; i < snapshot.ArchetypeBackupCount; i++)
        {
            ref var entry = ref snapshot.ArchetypeBackups[i];
            entry.RestoreTo(entry.Archetype);
        }

        // Hierarchy
        _hierarchy.RestoreState(snapshot);

        // Invalidate all caches
        _createArchetypeCacheGeneration++;

        // Recycle snapshot as spare for next CaptureState
        _stateSnapshotSpare = snapshot;
    }

    private readonly record struct CloneWork(Entity Source, Entity CloneEntity);

}

