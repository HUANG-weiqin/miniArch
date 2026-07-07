using System.Buffers;
using System.Diagnostics;
using System.Linq;
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
/// <item><b>Structural changes</b> —<see cref="Create"/>, <see cref="Destroy"/>,
/// <see cref="Add{T}"/>, <see cref="Set{T}"/>, <see cref="Remove{T}"/>,
/// <see cref="AddChild"/>, <see cref="Clone(Entity)"/> —are <b>not</b> thread-safe
/// and must be issued from a single thread (typically the main game thread)
/// or deferred through <c>CommandStream</c>.</item>
/// <item><b>Reads</b> —<see cref="Has{T}"/>, <see cref="Get{T}"/>,
/// <see cref="TryGet{T}"/>, query iteration via <c>ForEachChunkParallel</c> —
/// may run in parallel with other readers, but <b>not</b> concurrent with a
/// structural change. Snapshot the archetype list (via Query) before
/// dispatching parallel work.</item>
    /// <item><c>ReserveDeferredEntity</c> takes a lock and is safe to call from
    /// background threads (e.g. async snapshot building); this is the only
    /// write path that is internally synchronized. Multiple <c>CommandStream</c>
    /// instances must not concurrently reserve ids against the same world.</item>
/// </list>
/// </remarks>
public sealed partial class World : IDisposable
{
    private const int DefaultChunkCapacity = 128;

    private readonly Dictionary<Signature, Archetype> _archetypes = new();
    // Mask-keyed cache for zero-allocation archetype lookup on the Replay path.
    // Only populated for archetypes whose ComponentMask is canonical —i.e. every
    // component id is < 512 (PopCount(mask) == component count). Archetypes with
    // high-id components are intentionally absent from this cache and always go
    // through the Signature-keyed dictionary above, preserving the library's
    // "arbitrary component id range" contract.
    private readonly Dictionary<ComponentMask, Archetype> _archetypeByMask = new();

    // Hash-keyed cache for non-canonical masks (component ids >= 512).
    // Avoids allocating a Signature object on repeated lookup. Each bucket
    // holds archetypes whose normalized component set produced the same hash;
    // collisions are rare but handled via full SequenceEqual.
    private readonly Dictionary<int, List<Archetype>> _archetypeByHash = new();

    // Reused scratch for the Replay pre-scan pass (counts Creates per archetype
    // so we can pre-size archetype storage and avoid per-Create doubling
    // allocations). Cleared at the start of every Replay call; never allocated
    // per-call in steady state.
    private readonly Dictionary<Archetype, int> _replayCreateCounts = new();

#if DEBUG
    internal int _reservedCount;
    internal int _deferredEpoch;
#endif

    // Placeholder —local real entity mapping for FrameDelta replay.
    // Indexed by placeholder seq (Entity.Version when Id == -1).
    // Reused across ReplayCore calls —never allocated per-call in steady state.
    // mapLen resets to 0 at the start of each ReplayCore call to prevent stale
    // mappings from previous frames leaking into the current replay.
    private Entity[] _replayPlaceholderMap = [];
    // Number of valid entries in _replayPlaceholderMap (power-of-two array length).
    // Snapshot by Replay() to populate the internal mapping for TryResolvePlaceholder.
    private int _replayMapCount;

    private readonly HierarchyTable _hierarchy = new();
    private EntityRecord[] _records;
    private int _entitySlotCount;
    private Dictionary<QueryDescription, QueryFilter> _queryFiltersByDescription = new();
    private Dictionary<QueryFilter, MiniArch.Core.QueryCache> _queries = new();
    private Archetype[] _archetypeSnapshot = Array.Empty<Archetype>();
    private readonly int _chunkCapacity;
    private RecycledEntity[] _freeIds;
    private int _freeIdCount;

    private volatile int _createArchetypeCacheGeneration;
    private readonly List<Entity> _destroyOrderScratch;
    private int[] _destroyVisitedGen = [];
    private int _destroyCurrentGen;

    private bool _disposed;

    // ── Change tracking ─────────────────────────────────────────────
    internal volatile int _trackingGeneration;                 // incremented on RestoreState/Dispose for cursor self-heal
    private readonly List<WeakReference<Core.IChangeQuery>> _changeQueries = new();
    internal object? _activeTypedTracker;                       // ChangeTracker<T> for single-type fast path (boxed generic)

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
        _archetypeByHash.Clear();
        _replayCreateCounts.Clear();
        _queryFiltersByDescription.Clear();
        _queries.Clear();
        _archetypeSnapshot = Array.Empty<Archetype>();
        _entitySlotCount = 0;
        _records = Array.Empty<EntityRecord>();
        _freeIdCount = 0;
        _destroyVisitedGen = [];
        _destroyCurrentGen = 0;
        _replayPlaceholderMap = [];
        _replayMapCount = 0;
        _stateSnapshotPool.Clear();
        _hierarchy.Reset();
        _createArchetypeCacheGeneration = int.MaxValue;

        // Invalidate any remaining change queries
        _trackingGeneration++;
    }

    internal bool IsDisposed => _disposed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AssertNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AssertAlive(Entity entity)
    {
        if ((uint)entity.Id >= (uint)_entitySlotCount)
            throw new InvalidOperationException($"Entity {entity} is not alive.");
        ref var record = ref _records[entity.Id];
        if (!record.IsOccupied || record.Version != entity.Version)
            throw new InvalidOperationException($"Entity {entity} is not alive.");
    }

    /// <summary>
    /// Gets the entity metadata capacity.
    /// </summary>
    public int EntityCapacity => _records.Length;

    /// <summary>
    /// Gets the number of currently alive entities.
    /// </summary>
    public int EntityCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
#if DEBUG
            return _entitySlotCount - _freeIdCount - _reservedCount;
#else
            return _entitySlotCount - _freeIdCount;
#endif
        }
    }

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
            result[i++] = new ArchetypeStats(arch.EntityCount, arch.Capacity, arch.ComponentTypes.ToArray());
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

    internal void RegisterChangeQuery(Core.IChangeQuery query)
    {
        _changeQueries.Add(new WeakReference<Core.IChangeQuery>(query));
    }

    private void AppendTransition(Entity e, Core.Archetype? old, Core.Archetype? @new)
    {
        for (var i = _changeQueries.Count - 1; i >= 0; i--)
        {
            var weakRef = _changeQueries[i];
            if (weakRef.TryGetTarget(out var query))
                query.OnTransition(e, old, @new);
            else
            {
                // Swap-remove dead weak ref (O(1)).
                _changeQueries[i] = _changeQueries[_changeQueries.Count - 1];
                _changeQueries.RemoveAt(_changeQueries.Count - 1);
            }
        }
    }

    /// <summary>
    /// Creates a multi-component change query. Register component types for value
    /// capture via <see cref="ChangeQuery.Capture{T}"/> and configure the filter
    /// with <c>With</c>/<c>Without</c>/<c>WithAny</c> before enumerating.
    /// </summary>
    public ChangeQuery Track()
    {
        AssertNotDisposed();
        var query = new ChangeQuery(this);
        RegisterChangeQuery(query);
        return query;
    }

    /// <summary>
    /// Adds a child to a parent.
    /// </summary>
    public void AddChild(Entity parent, Entity child)
    {
        AssertNotDisposed();
        _hierarchy.AddChild(this, parent, child);
    }

    /// <summary>
    /// Removes a child.
    /// </summary>
    public void RemoveChild(Entity child)
    {
        AssertNotDisposed();
        RequireLocation(child);
        _hierarchy.RemoveChild(child);
    }

    /// <summary>
    /// Tries to get a parent entity.
    /// </summary>
    public bool TryGetParent(Entity child, out Entity parent)
    {
        AssertNotDisposed();
        return _hierarchy.TryGetParent(this, child, out parent);
    }

    /// <summary>
    /// Returns a zero-allocation enumerable over the live children of
    /// <paramref name="parent"/>. Use <c>foreach</c> to iterate.
    /// </summary>
    public ChildrenEnumerable EnumerateChildren(Entity parent)
    {
        AssertNotDisposed();
        return _hierarchy.EnumerateChildren(this, parent);
    }

    /// <summary>
    /// Whether <paramref name="entity"/> has at least one live child.
    /// </summary>
    public bool HasChildren(Entity entity)
    {
        AssertNotDisposed();
        return _hierarchy.HasChildren(this, entity);
    }

    /// <summary>
    /// Tries to read a component directly from an entity.
    /// </summary>
    public bool TryGet<T>(Entity entity, out T component) where T : unmanaged
    {
        AssertNotDisposed();
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
        AssertNotDisposed();
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
        AssertNotDisposed();
        AssertAlive(entity);
        ref var record = ref _records[entity.Id];
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
        AssertNotDisposed();
        AssertAlive(entity);
        ref var record = ref _records[entity.Id];
        var arch = record.Archetype!;
        return ref arch.GetComponentRefAt<T>(arch.GetComponentIndexFast(GetComponentType<T>()), record.RowIndex);
    }

    /// <summary>
    /// Creates a cached <see cref="EntityAccessor"/> for multiple component
    /// reads/writes on the same entity. The entity's (archetype,chunk,row) lookup
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
        AssertNotDisposed();
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
    /// Parent-child relations within the subtree are preserved; the clone root has no parent.
    /// </summary>
    /// <param name="source">The entity to clone. Must be alive.</param>
    /// <returns>A new entity that is the root of the cloned subtree.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="source"/> is no longer alive.</exception>
    public Entity Clone(Entity source)
    {
        AssertNotDisposed();
        var root = CloneSingle(source);
        DeepCloneChildren(source, root);
        return root;
    }

    private Entity CloneSingle(Entity source)
    {
        var sourceInfo = RequireLocation(source);
        var archetype = sourceInfo.Archetype!;
        var entity = CreateInArchetype(archetype, out var destRow);
        archetype.CopySharedComponentsFrom(sourceInfo.Archetype!, sourceInfo.RowIndex, destRow);
        return entity;
    }

    private void DeepCloneChildren(Entity sourceRoot, Entity cloneRoot)
    {
        if (!_hierarchy.HasChildren(this, sourceRoot)) return;

        var stack = ArrayPool<CloneWork>.Shared.Rent(16);
        var stackCount = 0;
        try
        {
            PushPooled(ref stack, ref stackCount, new CloneWork(sourceRoot, cloneRoot));
            while (stackCount > 0)
            {
                var work = stack[--stackCount];
                foreach (var child in _hierarchy.EnumerateChildren(this, work.Source))
                {
                    var cloneChild = CloneSingle(child);
                    _hierarchy.AddChild(this, work.CloneEntity, cloneChild);
                    PushPooled(ref stack, ref stackCount, new CloneWork(child, cloneChild));
                }
            }
        }
        finally
        {
            ArrayPool<CloneWork>.Shared.Return(stack);
        }
    }

    /// <summary>
    /// Creates an independent, <b>ID-preserving</b> deep copy of this world.
    /// Entity IDs match the source exactly — component data, hierarchy, and
    /// free list are deep-copied, but <see cref="Entity"/> references inside
    /// component fields carry the source world's IDs. If the clone is used
    /// with a different <see cref="World"/> instance (different ID space),
    /// those embedded references will be stale.
    /// <para/>
    /// For persistence or cross-process use, prefer
    /// <see cref="Core.WorldSnapshot.Save"/> / <see cref="Core.WorldSnapshot.Load"/>,
    /// which remaps entity IDs for the target world.
    /// For in-memory rollback, use <see cref="CaptureState"/> / <see cref="RestoreState"/>.
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
        archetype._owner = this;
        _archetypes.Add(signature, archetype);
        CacheArchetypeByMaskIfCanonical(signature, archetype);
        PublishArchetypeSnapshot(archetype);
        return archetype;
    }

    // Populate the mask cache. Only called when an archetype is freshly created,
    // so the single dictionary write is amortized across all subsequent lookups.
    // High-id archetypes (mask not canonical) are intentionally left out —see
    // the comment on _archetypeByMask.
    private void CacheArchetypeByMaskIfCanonical(Signature signature, Archetype archetype)
    {
        if (IsMaskCanonical(signature.ComponentMask, signature.Count))
        {
            _archetypeByMask[signature.ComponentMask] = archetype;
        }
    }

    // A ComponentMask is "canonical" for a signature when every component id is
    // representable in the 512-bit mask —i.e. the popcount of the mask equals
    // the component count. If any id is >= 512 its bit is dropped from the mask
    // and popcount would be smaller than the actual count, so the mask is not a
    // unique key for that set.
    internal static bool IsMaskCanonical(ComponentMask mask, int componentCount)
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
    /// Unified archetype resolver. Hides the canonical/non-canonical bifurcation.
    /// </summary>
    /// <remarks>
    /// Canonical masks (all IDs &lt; 512) go through the fast <see cref="_archetypeByMask"/>
    /// dictionary. Non-canonical masks use a hash-bypass index (<see cref="_archetypeByHash"/>)
    /// to avoid allocating a <see cref="Signature"/> on repeated lookup.
    /// <para/>
    /// The input <paramref name="types"/> span is never modified — the non-canonical
    /// path copies to a scratch buffer for sorting.
    /// </remarks>
    internal Archetype GetOrCreateArchetype(ComponentMask mask, scoped ReadOnlySpan<ComponentType> types)
    {
        // ── Fast path: canonical mask (all IDs < 512) ──
        if (IsMaskCanonical(mask, types.Length))
        {
            if (_archetypeByMask.TryGetValue(mask, out var arch))
                return arch;

            // First-seen canonical: create Signature, populates both caches.
            return CreateArchetypeCore(types);
        }

        // ── Non-canonical path: some ID >= 512 (or duplicates) ──
        return GetOrCreateArchetypeNonCanonical(mask, types);
    }

    /// <summary>
    /// Handles non-canonical archetype resolution with zero-allocation on
    /// repeated lookup via a hash-bypass index.
    /// </summary>
    private Archetype GetOrCreateArchetypeNonCanonical(
        ComponentMask mask, ReadOnlySpan<ComponentType> types)
    {
        // Copy to scratch and normalize (sort + dedup).
        // The caller's span is never modified.
        ComponentType[]? rented = null;
        Span<ComponentType> scratch = types.Length <= 64
            ? stackalloc ComponentType[types.Length]
            : (rented = ArrayPool<ComponentType>.Shared.Rent(types.Length)).AsSpan(0, types.Length);
        try
        {
            types.CopyTo(scratch);
            var uniqueCount = SpanSorting.SortAndDeduplicate(scratch);
            var normalized = scratch[..uniqueCount];

            // After removing duplicates, the mask may now be canonical.
            // Defensive — CommandStream/Replay inputs are already deduplicated,
            // but the guarantee is cheap to uphold.
            if (IsMaskCanonical(mask, uniqueCount))
            {
                if (_archetypeByMask.TryGetValue(mask, out var arch))
                    return arch;
                return CreateArchetypeCore(normalized);
            }

            // True non-canonical: hash-bypass lookup (zero alloc on hit).
            var hash = SpanSorting.CombineHashCodes(normalized);
            if (_archetypeByHash.TryGetValue(hash, out var bucket))
            {
                foreach (var candidate in bucket)
                {
                    if (candidate.Signature.AsSpan().SequenceEqual(normalized))
                        return candidate;
                }
            }

            // Miss: create Signature (one-time allocation per unique set).
            var array = normalized.ToArray();
            var signature = Signature.CreateNormalized(array);
            var archetype = GetOrCreateArchetype(signature);

            // Add to hash cache.
            if (bucket is null)
            {
                bucket = new List<Archetype>(1);
                _archetypeByHash[hash] = bucket;
            }
            bucket.Add(archetype);

            return archetype;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<ComponentType>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Creates an Archetype from an unsorted, potentially-duplicated span.
    /// </summary>
    private Archetype CreateArchetypeCore(ReadOnlySpan<ComponentType> types)
    {
        if (types.Length == 0)
            return GetOrCreateArchetype(Signature.Empty);

        var array = types.ToArray();
        return GetOrCreateArchetype(new Signature(array));
    }

    private static Type[] ResolveComponentTypes(Signature signature)
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
    private static ComponentType GetComponentType<T>()
    {
        return Component<T>.ComponentType;
    }

    /// <summary>
    /// Attempts to resolve a placeholder entity (<c>Entity(-1, seq)</c>) to its real
    /// local entity ID after the most recent <see cref="CommandStreamCore.Replay(FrameDelta, bool)"/> call.
    /// </summary>
    /// <remarks>
    /// <b>Placeholders are valid only against the replay that produced them.</b>
    /// A subsequent <see cref="CommandStreamCore.Replay(FrameDelta, bool)"/> call replaces the
    /// internal mapping table, causing the same placeholder handle to resolve to a
    /// different real entity (or to fail if the seq is out of range).
    ///
    /// To safely refer to an entity across frames, resolve the placeholder immediately
    /// after replay via <see cref="CommandStream.Track"/> and <see cref="CommandStreamCore.Replay(FrameDelta, bool)"/>.
    /// </remarks>
    /// <param name="placeholder">A placeholder handle (<c>Entity(-1, seq)</c>).</param>
    /// <param name="real">When this method returns, the resolved real entity;
    /// or <c>default</c> if the placeholder could not be resolved.</param>
    /// <returns><c>true</c> if the placeholder was resolved to a valid real entity;
    /// <c>false</c> if the placeholder is out of range or unmapped.</returns>
    internal bool TryResolvePlaceholder(Entity placeholder, out Entity real)
    {
        if (!placeholder.IsPlaceholder)
        {
            real = placeholder;
            return true;
        }

        var seq = placeholder.Version;
        if ((uint)seq >= (uint)_replayMapCount)
        {
            real = default;
            return false;
        }

        var mapped = _replayPlaceholderMap[seq];
        if (mapped.IsUnmappedSentinel)
        {
            real = default;
            return false;
        }

        real = mapped;
        return true;
    }

    internal unsafe void ReplayCore(FrameDelta delta)
    {
        AssertNotDisposed();

        // Pre-scan: size archetype storage and the entity record array up-front
        // so the main pass never hits a doubling resize on the hot Create path.
        PreScanForCapacity(delta);

        var map = _replayPlaceholderMap;
        var mapLen = 0;

        // Pin the backing buffer once for the entire replay. Every Create op
        // reads component data from this buffer via direct pointer arithmetic,
        // and Add/Set ops do the same —sharing one pin avoids per-op fixed
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
                        if (raw.IsPlaceholder)
                        {
                            // Placeholder: allocate a fresh local id.
                            var real = ReserveDeferredEntityUnsafe();
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
                    {
                        var placeholderMap = new ReadOnlySpan<Entity>(map, 0, mapLen);
                        ReplayCreateOpResolved(ResolveReplayEntity(decoder.Entity, map, mapLen), ref decoder, bufPtr, placeholderMap);
                        break;
                    }

                    case DeltaOpKind.AddChild:
                    {
                        var child = ResolveReplayEntity(decoder.Entity, map, mapLen);
                        var parent = ResolveReplayEntity(decoder.ReadExtraEntity(), map, mapLen);
                        AddChild(parent, child);
                        break;
                    }

                    case DeltaOpKind.RemoveChild:
                        RemoveChild(ResolveReplayEntity(decoder.Entity, map, mapLen));
                        break;

                    case DeltaOpKind.Add:
                    {
                        var entity = ResolveReplayEntity(decoder.Entity, map, mapLen);
                        var comp = decoder.ReadComponentType();
                        var dataSize = decoder.ReadVarint();
                        var dataStart = decoder.CurrentPosition;
                        _ = decoder.ReadBytes(dataSize);
                        var src = bufPtr + dataStart;
                        if (EntityFieldResolver.GetOffsets(comp).Length > 0)
                        {
                            // Cannot mutate the delta buffer (the same FrameDelta may be
                            // replayed into multiple worlds). Use a pooled scratch buffer
                            // instead of stackalloc to avoid per-op stack accumulation.
                            var pooled = ArrayPool<byte>.Shared.Rent(dataSize);
                            try
                            {
                                fixed (byte* pScratch = pooled)
                                {
                                    Unsafe.CopyBlockUnaligned(pScratch, src, (uint)dataSize);
                                    EntityFieldResolver.ResolveInPlace(
                                        new Span<byte>(pScratch, dataSize), comp,
                                        new ReadOnlySpan<Entity>(map, 0, mapLen));
                                    var loc = RequireLocation(entity);
                                    ApplyRawAdd(entity, loc, comp, pScratch);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(pooled);
                            }
                        }
                        else
                        {
                            var loc = RequireLocation(entity);
                            ApplyRawAdd(entity, loc, comp, src);
                        }
                        break;
                    }

                    case DeltaOpKind.Set:
                    {
                        var entity = ResolveReplayEntity(decoder.Entity, map, mapLen);
                        var comp = decoder.ReadComponentType();
                        var dataSize = decoder.ReadVarint();
                        var dataStart = decoder.CurrentPosition;
                        _ = decoder.ReadBytes(dataSize);
                        var src = bufPtr + dataStart;
                        if (EntityFieldResolver.GetOffsets(comp).Length > 0)
                        {
                            var pooled = ArrayPool<byte>.Shared.Rent(dataSize);
                            try
                            {
                                fixed (byte* pScratch = pooled)
                                {
                                    Unsafe.CopyBlockUnaligned(pScratch, src, (uint)dataSize);
                                    EntityFieldResolver.ResolveInPlace(
                                        new Span<byte>(pScratch, dataSize), comp,
                                        new ReadOnlySpan<Entity>(map, 0, mapLen));
                                    var loc = RequireLocation(entity);
                                    ApplyRawSet(entity, loc, comp, pScratch);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(pooled);
                            }
                        }
                        else
                        {
                            var loc = RequireLocation(entity);
                            ApplyRawSet(entity, loc, comp, src);
                        }
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
        _replayMapCount = mapLen;
    }

    // ── Replay placeholder helpers ─────────────────────────────────────

    private static void EnsurePlaceholderMap(ref Entity[] map, ref int mapLen, int seq)
    {
        if (seq < mapLen) return;
        var newLen = map.Length == 0 ? 64 : map.Length;
        while (newLen <= seq) newLen *= 2;
        Array.Resize(ref map, newLen);
        for (var i = mapLen; i < newLen; i++)
            map[i] = new Entity(-1, -1); // IsUnmappedSentinel: not yet mapped
        mapLen = newLen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Entity ResolveReplayEntity(Entity wireEntity, Entity[] map, int mapLen)
    {
        if (wireEntity.Id >= 0) return wireEntity;
        if ((uint)wireEntity.Version >= (uint)mapLen ||
            map[wireEntity.Version].IsUnmappedSentinel)
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
    /// main pass's natural growth —they are rare and preserve the library's
    /// support for arbitrary component id ranges.
    /// </remarks>
    private void PreScanForCapacity(FrameDelta delta)
    {
        _replayCreateCounts.Clear();
        var maxEntityId = -1;

        var scanner = delta.GetDecoder();
        while (scanner.MoveNext())
        {
            switch (scanner.Kind)
            {
                case DeltaOpKind.Reserve:
                {
                    // Reserve allocates new entity slots. Track the id so the
                    // _records array is pre-grown for the coming Create.
                    var id = scanner.Entity.Id;
                    if (id >= 0 && id > maxEntityId) maxEntityId = id;
                    break;
                }

                case DeltaOpKind.Create:
                {
                    var id = scanner.Entity.Id;
                    if (id >= 0 && id > maxEntityId) maxEntityId = id;

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
                    }
                    break;
                }

                case DeltaOpKind.AddChild:
                {
                    var parent = scanner.ReadExtraEntity();
                    if (parent.Id >= 0 && parent.Id > maxEntityId) maxEntityId = parent.Id;
                    break;
                }

                case DeltaOpKind.Add:
                case DeltaOpKind.Set:
                    scanner.SkipData();
                    break;

                case DeltaOpKind.Remove:
                    scanner.ReadComponentType();
                    break;

                // Release / RemoveChild / Destroy: entity must already exist.
                // Do NOT track maxEntityId — prevents OOM from malicious
                // Destroy(Entity(100M,1)) pre-growing _records.
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
    private unsafe void ReplayCreateOpResolved(Entity entity, ref FrameDelta.OpDecoder decoder, byte* bufPtr,
        scoped ReadOnlySpan<Entity> placeholderMap)
    {
        var compCount = decoder.ReadVarint();

        if (compCount == 0)
        {
            MaterializeEmptyReservedEntity(entity);
            return;
        }

        // ComponentType is a 4-byte struct, so 32 entries cost 128 bytes of
        // stack per span (256 bytes total) —well within limits. Entities with
        // more components spill to ArrayPool, which is amortized zero-allocation.
        const int MaxStackComponents = 32;

        if (compCount <= MaxStackComponents)
        {
            Span<ComponentType> types = stackalloc ComponentType[compCount];
            Span<int> offsets = stackalloc int[compCount];
            Span<int> sizes = stackalloc int[compCount];
            ReplayCreateOpCore(ref decoder, bufPtr, entity, compCount, types, offsets, sizes, placeholderMap);
        }
        else
        {
            var poolTypes = ArrayPool<ComponentType>.Shared.Rent(compCount);
            var poolOffsets = ArrayPool<int>.Shared.Rent(compCount);
            var poolSizes = ArrayPool<int>.Shared.Rent(compCount);
            try
            {
                ReplayCreateOpCore(
                    ref decoder, bufPtr, entity, compCount,
                    poolTypes.AsSpan(0, compCount), poolOffsets.AsSpan(0, compCount),
                    poolSizes.AsSpan(0, compCount), placeholderMap);
            }
            finally
            {
                ArrayPool<ComponentType>.Shared.Return(poolTypes);
                ArrayPool<int>.Shared.Return(poolOffsets);
                ArrayPool<int>.Shared.Return(poolSizes);
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
        scoped Span<ComponentType> types, scoped Span<int> offsets, scoped Span<int> sizes,
        scoped ReadOnlySpan<Entity> placeholderMap)
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
            sizes[i] = size;
            offsets[i] = decoder.CurrentPosition;
            decoder.ReadBytes(size);
        }

        var archetype = GetOrCreateArchetype(builder.ToMask(), types[..compCount]);

        // Write components into archetype columns, resolving embedded Entity
        // refs on the fly. We cannot mutate the delta buffer because the same
        // FrameDelta may be replayed into multiple worlds; instead, component
        // payloads that contain Entity fields are copied to a stack scratch,
        // resolved, and written from the scratch.
        var rowIndex = PlaceEntityInArchetype(entity, archetype);

        // Find the largest component that has Entity fields —that's the scratch
        // size needed. Hoist stackalloc out of the loop below.
        int scratchSize = 0;
        for (var i = 0; i < compCount; i++)
        {
            if (EntityFieldResolver.GetOffsets(types[i]).Length > 0 && sizes[i] > scratchSize)
                scratchSize = sizes[i];
        }

        if (scratchSize > 0)
        {
            byte* scratch = stackalloc byte[scratchSize];
            for (var i = 0; i < compCount; i++)
            {
                var colIdx = archetype.GetComponentIndexFast(types[i]);
                var src = bufPtr + offsets[i];
                if (EntityFieldResolver.GetOffsets(types[i]).Length > 0)
                {
                    Unsafe.CopyBlockUnaligned(scratch, src, (uint)sizes[i]);
                    EntityFieldResolver.ResolveInPlace(
                        new Span<byte>(scratch, sizes[i]), types[i], placeholderMap);
                    src = scratch;
                }
                archetype.WriteComponentRaw(colIdx, rowIndex, src);
            }
        }
        else
        {
            for (var i = 0; i < compCount; i++)
            {
                var colIdx = archetype.GetComponentIndexFast(types[i]);
                archetype.WriteComponentRawNoTrack(colIdx, rowIndex, bufPtr + offsets[i]);
            }
        }
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
#if DEBUG
        // Only decrement if the entity was in reserved state
        // (Archetype==null, Version matches). Malformed deltas that Create
        // without a prior Reserve skip the increment, so don't decrement.
        if (!record.IsOccupied && record.Version == entity.Version)
            _reservedCount--;
#endif
        record.Archetype = archetype;
        record.RowIndex = rowIndex;
        AppendTransition(entity, null, archetype);
        return rowIndex;
    }

    /// <summary>
    /// Materializes a previously-reserved entity with no components. The entity
    /// must already have been reserved via <see cref="ReserveDeferredEntityUnsafe"/>
    /// or a Replay Reserve op —this method does not re-check reservation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MaterializeEmptyReservedEntity(Entity entity)
    {
        var archetype = GetOrCreateArchetype(Signature.Empty);
        PlaceEntityInArchetype(entity, archetype);
    }

    /// <summary>
    /// Materializes a reserved entity writing component data from a raw byte buffer.
    /// Avoids per-type store lookups —caller provides direct pointers.
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
            archetype.WriteComponentRawNoTrack(colIdx, rowIndex, buffer + offsets[i]);
        }
    }


    // ──────────────────────────────────────────────
    //  Tier 1 in-memory rollback snapshot
    // ──────────────────────────────────────────────

    // Pool of recycled WorldStateSnapshot instances. Each CaptureState pops
    // one (or constructs a new one when empty); each RestoreState pushes the
    // incoming snapshot back. Pool size self-stabilises at the peak number
    // of simultaneously live snapshots, so a GGPO-style N-frame rollback
    // window pays zero allocation in steady state.
    //
    // Stack<WorldStateSnapshot> is chosen over a single spare slot so that
    // callers may capture multiple frames ahead before restoring them out of
    // order on misprediction - the previous single-spare design silently
    // broke at rollback depth > 1.
    private readonly Stack<WorldStateSnapshot> _stateSnapshotPool = new();

    /// <summary>
    /// Captures the world's current mutable state into an opaque handle
    /// for later rollback via <see cref="RestoreState"/>.
    /// <para/>
    /// Multiple snapshots may be live simultaneously; each call returns an
    /// independent handle drawn from the world's pool. In steady state
    /// (warm pool sized to peak concurrent usage) this method allocates zero
    /// GC memory. Suitable for GGPO-style 60fps frame save/restore cycles at
    /// &lt;1000 entities, including rollback windows deeper than 1 frame.
    /// </summary>
    public WorldStateSnapshot CaptureState()
    {
        AssertNotDisposed();
        var snap = _stateSnapshotPool.Count > 0
            ? _stateSnapshotPool.Pop()
            : new WorldStateSnapshot();
        snap.Clear();
        snap._isRecycled = false;

        // Records
        snap.EnsureRecordsCapacity(_entitySlotCount);
        Array.Copy(_records, snap.Records, _entitySlotCount);
        snap.EntitySlotCount = _entitySlotCount;

        // Free ids
        snap.EnsureFreeIdsCapacity(_freeIdCount);
        for (var i = 0; i < _freeIdCount; i++)
            snap.FreeEntities[i] = new FreeEntityEntry(_freeIds[i].Id, _freeIds[i].Version);
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

        snap._sourceWorld = this;
        return snap;
    }

    /// <summary>
    /// Restores the world to a previously captured state. The snapshot is
    /// recycled internally and should not be used after this call: its
    /// <see cref="WorldStateSnapshot.IsRecycled"/> flag becomes <c>true</c>
    /// and it is returned to the world's pool for reuse by the next
    /// <see cref="CaptureState"/>.
    /// <para/>
    /// After restore, all query caches and archetype transition caches are
    /// invalidated and will rebuild on next use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="snapshot"/> has already been restored
    /// (its <see cref="WorldStateSnapshot.IsRecycled"/> is <c>true</c>).
    /// </exception>
    public void RestoreState(WorldStateSnapshot snapshot)
    {
        AssertNotDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot._isRecycled)
        {
            throw new InvalidOperationException(
                "Cannot RestoreState: the snapshot has already been restored " +
                "(or was never produced by CaptureState). Call CaptureState to " +
                "obtain a fresh handle before restoring.");
        }

        if (!ReferenceEquals(snapshot._sourceWorld, this))
        {
            throw new InvalidOperationException(
                "RestoreState: snapshot was captured from a different World instance. " +
                "Snapshots are tied to their originating world and cannot be shared.");
        }

        // Records
        if (_records.Length < snapshot.EntitySlotCount)
            Array.Resize(ref _records, snapshot.EntitySlotCount);
        Array.Copy(snapshot.Records, _records, snapshot.EntitySlotCount);
        _entitySlotCount = snapshot.EntitySlotCount;

        // Free ids
        if (_freeIds.Length < snapshot.FreeIdCount)
            Array.Resize(ref _freeIds, snapshot.FreeIdCount);
        for (var i = 0; i < snapshot.FreeIdCount; i++)
            _freeIds[i] = new RecycledEntity(snapshot.FreeEntities[i].Id, snapshot.FreeEntities[i].Version);
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

        // Clear replay state — stale after rollback.
        // ReplayCore rebuilds these from scratch on next replay;
        // stale mappings from a pre-rollback replay must not survive.
        _replayPlaceholderMap = [];
        _replayMapCount = 0;
        _replayCreateCounts.Clear();

        // Invalidate all caches
        _createArchetypeCacheGeneration++;

        // Reset change tracking — prediction-era accumulations are stale.
        _trackingGeneration++;
        _changeQueries.Clear();
        _activeTypedTracker = null;

        // Recycle snapshot to the pool for the next CaptureState.
        snapshot._isRecycled = true;
        snapshot.Clear();
        _stateSnapshotPool.Push(snapshot);
    }
    private readonly record struct CloneWork(Entity Source, Entity CloneEntity);

    /// <summary>
    /// Computes a SHA-256 checksum of the entire world state.
    /// Stable across peers driven by the same delta sequence — use to detect
    /// lockstep divergence. Returns 32 raw bytes; use
    /// <c>Convert.ToHexString(world.Checksum())</c> for a hex string.
    /// </summary>
    public byte[] Checksum() => Core.WorldSnapshot.ComputeChecksum(this);

    /// <summary>
    /// Computes a canonical SHA-256 checksum that is identical for any two
    /// worlds with the same logical state, regardless of how they were built
    /// (replay, snapshot-load, or manual construction). Slower than
    /// <see cref="Checksum"/>; use only for comparing worlds from different
    /// construction paths (e.g. client snapshot vs server live state).
    /// </summary>
    public byte[] CanonicalChecksum() => Core.WorldSnapshot.ComputeCanonicalChecksum(this);

    internal void Reset(int entitySlotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entitySlotCount);

        _archetypes.Clear();
        _archetypeByMask.Clear();
        _archetypeByHash.Clear();
        _replayCreateCounts.Clear();
        _replayPlaceholderMap = [];
        _replayMapCount = 0;
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
        {
            var id = reader.ReadInt32();
            var version = reader.ReadInt32();
            if ((uint)id >= (uint)_entitySlotCount)
                throw new InvalidOperationException(
                    $"Corrupt snapshot: free-list entity id {id} is out of range " +
                    $"(entity slot count: {_entitySlotCount}).");
            _freeIds[i] = new RecycledEntity(id, version);
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushPooled<T>(ref T[] array, ref int count, T value)
    {
        if ((uint)count >= (uint)array.Length)
            GrowPooled(ref array);
        array[count++] = value;
    }

    private static void GrowPooled<T>(ref T[] array)
    {
        var next = ArrayPool<T>.Shared.Rent(array.Length == 0 ? 16 : array.Length * 2);
        if (array.Length > 0)
        {
            Array.Copy(array, next, array.Length);
            ArrayPool<T>.Shared.Return(array);
        }
        array = next;
    }
}

