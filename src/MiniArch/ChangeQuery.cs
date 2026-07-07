using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Multi-component change query. Obtained via <see cref="World.Track()"/>.
/// Tracks structural transitions (create/destroy/add/remove) and, when
/// <see cref="Previous"/> is enabled, captures old/new snapshots of all
/// captured component types for each Set or structural change.
/// </summary>
/// <remarks>
/// <para>
/// Unlike single-type change queries, this query captures <b>any subset</b> of
/// component values via <see cref="Capture{T}"/>. Filtering (With/Without/WithAny)
/// is independent of capture — a captured type is NOT automatically added to the filter.
/// </para>
/// <para>
/// Fluent methods throw <see cref="InvalidOperationException"/> if called
/// after the first enumeration method (Transitions, ModifiedChunks&lt;T&gt;,
/// or Changes) has been invoked.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var q = world.Track()
///     .Capture&lt;Position&gt;().Capture&lt;Velocity&gt;()
///     .With&lt;Player&gt;()
///     .Previous();
///
/// foreach (var c in q.Changes())
/// {
///     ref readonly var oldPos = ref c.Old.Get&lt;Position&gt;();
///     ref readonly var newPos = ref c.New.Get&lt;Position&gt;();
///     // ...
/// }
/// </code>
/// </example>
public sealed class ChangeQuery : IChangeQuery
{
    private readonly World _world;
    private QueryDescription _filter = new();
    private readonly List<Transition> _transitions = new(256);
    private bool _hasPrevious;
    private bool _consumed;

    // ── Captured type state ──
    private readonly List<ComponentType> _capturedTypes = new();
    private readonly List<int> _typeSizes = new();  // Unsafe.SizeOf<T>() per captured type
    private int[] _offsets = [];                     // precomputed byte offsets per captured type
    private int _snapshotSize;                        // sum of _typeSizes
    private QueryCache? _cache;
    private bool _hasFilter;                          // false when no With/Without/WithAny → skip Matches()

    // Per-type cursor for ModifiedChunks<T>
    private readonly Dictionary<int, long> _valueCursors = new();

    // Snapshot state: per-entity indexed arrays + dirty list for iteration
    // Shadow: indexed by entity ID × _snapshotSize, stores old values
    // Dirty tracking: _isDirty[id] + _dirtyEntities list for O(K) iteration
    // Location: _dirtyArchetypes[_dirtyEntities[idx].Id], _dirtyRows[...] for Lazy New
    private byte[] _shadowValues = [];       // old values, indexed by entity.Id * _snapshotSize
    private bool[] _isDirty = [];             // _isDirty[id] → entity was written this tick
    private int[] _dirtyList = [];            // ordered dirty entity IDs for O(K) iteration
    private Core.Archetype?[] _dirtyArchetypes = []; // archetype per entity (indexed by entity.Id)
    private int[] _dirtyRows = [];            // row per entity (indexed by entity.Id)
    private Entity[] _dirtyEntityMap = [];    // entity per entity (indexed by entity.Id)
    private int _dirtyCount;                  // number of dirty entities in _dirtyList

    // Typed fast path: when single Capture<T> + Previous, use typed arrays
    // instead of byte[] + archetype/row storage
    private object? _typedTracker;  // ChangeTracker<T> (boxed generic)

    // Cached component index for hot path (avoids repeated TryGetComponentIndex)
    private Core.Archetype? _cachedArchetype;
    private int _cachedColIdx;

    // Reusable buffer for DrainModifiedChunks<T> (grown, never shrunk)
    private ChunkView[] _drainBuffer = [];

    // Reusable buffers for DrainChanges() (grown, never shrunk)
    private byte[] _drainData = [];
    private EntityChange[] _drainResult = [];
    private ComponentType[] _cachedTypesCopy = [];  // cached once, immutable after config
    private int[] _cachedOffsetsCopy = [];           // cached once, immutable after config

    private int _worldGen;  // captured at construction; compared on self-heal

    internal ChangeQuery(World world)
    {
        _world = world;
        _worldGen = world._trackingGeneration;
    }

    private void EnsureUsable()
    {
        if (_world.IsDisposed)
            throw new ObjectDisposedException(nameof(World));

        if (_worldGen == _world._trackingGeneration) return;

        // Self-heal: world state was reset (RestoreState/Dispose).
        // Clear stale accumulations and re-register dispatch paths.
        _transitions.Clear();

        // Reset dirty state
        for (var i = 0; i < _dirtyCount; i++)
        {
            var id = _dirtyList[i];
            if (id < _isDirty.Length)
                _isDirty[id] = false;
        }
        _dirtyCount = 0;

        _cache = null;
        _consumed = false;
        _cachedArchetype = null;

        // Reset per-type cursors to current epoch.
        var epoch = _world.CurrentWriteEpoch;
        // Struct enumerator, zero alloc. Fine at self-heal rate.
        foreach (var key in _valueCursors.Keys)
            _valueCursors[key] = epoch;

        _worldGen = _world._trackingGeneration;

        // Re-register to receive future dispatch events.
        _world.RegisterChangeQuery(this);
        foreach (var ct in _capturedTypes)
            _world.ActivateTracking(ct);
        if (_hasPrevious)
            _world.ActivatePreviousTracking(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureShadowCapacity(int id)
    {
        if (id < _isDirty.Length) return;
        var newSize = Math.Max(id + 1, _isDirty.Length == 0 ? 64 : _isDirty.Length * 2);
        Array.Resize(ref _isDirty, newSize);
        Array.Resize(ref _shadowValues, newSize * _snapshotSize);
        Array.Resize(ref _dirtyArchetypes, newSize);
        Array.Resize(ref _dirtyRows, newSize);
        Array.Resize(ref _dirtyEntityMap, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureDirtyListCapacity()
    {
        if (_dirtyCount < _dirtyList.Length) return;
        var newSize = Math.Max(_dirtyCount + 1, _dirtyList.Length == 0 ? 64 : _dirtyList.Length * 2);
        Array.Resize(ref _dirtyList, newSize);
    }

    /// <summary>
    /// Registers component type <typeparamref name="T"/> for value capture.
    /// Calling <see cref="Changes"/> will include Old/New snapshots of this
    /// component. Does NOT add <typeparamref name="T"/> to the filter —
    /// use <see cref="With{TU}"/> if filtering is needed.
    /// </summary>
    public ChangeQuery Capture<T>() where T : unmanaged
    {
        EnsureUsable();
        if (_consumed)
            throw new InvalidOperationException("Cannot Capture after enumeration has started.");
        var ct = Component<T>.ComponentType;
        if (_capturedTypes.Contains(ct)) return this;
        _capturedTypes.Add(ct);
        _typeSizes.Add(Unsafe.SizeOf<T>());
        _world.ActivateTracking(ct);

        // Rebuild offset table
        _offsets = new int[_typeSizes.Count];
        var off = 0;
        for (var i = 0; i < _typeSizes.Count; i++)
        {
            _offsets[i] = off;
            off += _typeSizes[i];
        }
        _snapshotSize = off;

        // Init value cursor for ModifiedChunks
        if (!_valueCursors.ContainsKey(ct.Value))
            _valueCursors[ct.Value] = 0;

        // Try to activate typed fast path (single type + Previous)
        TryActivateTypedTracker<T>();

        return this;
    }

    /// <summary>
    /// Enables old-value snapshot capture. When enabled, each Set or structural
    /// change on a captured type produces an <see cref="EntityChange"/> entry
    /// with both Old and New snapshots. Off by default (zero overhead when off).
    /// </summary>
    public ChangeQuery Previous()
    {
        EnsureUsable();
        if (_consumed)
            throw new InvalidOperationException("Cannot enable Previous after enumeration has started.");
        _hasPrevious = true;
        _world.ActivatePreviousTracking(this);

        // Pre-allocate shadow and dirty arrays to world capacity
        var cap = _world.EntityCapacity;
        if (_isDirty.Length < cap)
        {
            _isDirty = new bool[cap];
            _shadowValues = new byte[cap * _snapshotSize];
            _dirtyArchetypes = new Core.Archetype?[cap];
            _dirtyRows = new int[cap];
            _dirtyEntityMap = new Entity[cap];
        }

        // Try to activate typed fast path (in case Capture<T> was called before Previous)
        // Note: tracker is also activated from Capture<T>() when Previous is already set
        if (_capturedTypes.Count == 1)
        {
            // Need to call generic method with the captured type
            // Use cached captured type info
            ActivateTypedTrackerForCapturedType();
        }

        return this;
    }

    private void ActivateTypedTrackerForCapturedType()
    {
        // Single captured type — activate typed tracker
        if (_typedTracker is not null) return;
        if (!_hasPrevious || _capturedTypes.Count != 1) return;
        if (_hasFilter) return;  // filters require byte[] path for structural changes

        // Use the component type to create the right tracker
        var capturedType = _capturedTypes[0];
        if (!ComponentRegistry.Shared.TryGetType(capturedType, out var runtimeType))
            return; // type not registered, skip typed tracker

        var trackerType = typeof(ChangeTracker<>).MakeGenericType(runtimeType);
        var tracker = Activator.CreateInstance(trackerType);
        if (tracker is null) return;

        // Pre-allocate
        var cap = _world.EntityCapacity;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        trackerType.GetMethod("EnsureCapacity", flags)?.Invoke(tracker, [cap - 1]);

        _typedTracker = tracker;
        _world._activeTypedTracker = tracker;
    }

    /// <summary>
    /// Activates the typed fast path when there's exactly one captured type
    /// and Previous() is enabled. Creates a ChangeTracker&lt;T&gt; that stores
    /// old/new values in typed T[] arrays, matching hand-written manual code.
    /// </summary>
    private void TryActivateTypedTracker<T>() where T : unmanaged
    {
        if (!_hasPrevious || _capturedTypes.Count != 1) return;
        if (_typedTracker is not null) return;  // already activated
        if (_hasFilter) return;  // filters require byte[] path for structural changes

        // Create typed tracker
        var tracker = new ChangeTracker<T>();

        // Pre-allocate to world capacity
        tracker.EnsureCapacity(_world.EntityCapacity - 1);

        _typedTracker = tracker;
        _world._activeTypedTracker = tracker;
    }

    /// <summary>
    /// Inline capture: read Old from storage BEFORE write. Called from ApplyTypedSet fast path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CaptureOld(Entity entity, Core.Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        if (_hasFilter)
        {
            var cache = _cache ??= _world.Query(_filter).Advanced;
            if (!cache.Matches(archetype)) return;
        }

        var id = entity.Id;
        EnsureShadowCapacity(id);

        // Already captured this tick? Skip.
        if (_isDirty[id]) return;
        _isDirty[id] = true;

        // Add to dirty list (just store entity ID — archetype/row indexed by id)
        EnsureDirtyListCapacity();
        _dirtyList[_dirtyCount++] = id;

        // Store location for Lazy New (indexed by entity id)
        EnsureShadowCapacity(id);  // ensure archetype/row arrays are big enough
        _dirtyArchetypes[id] = archetype;
        _dirtyRows[id] = row;
        _dirtyEntityMap[id] = entity;

        // Capture Old for all captured types into shadow[id]
        var shadowOff = id * _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
            {
                var src = archetype.GetComponentBytes(colIdx, row);
                src.CopyTo(new Span<byte>(_shadowValues, shadowOff + _offsets[i], _typeSizes[i]));
            }
        }

        // Cache first type's index for CaptureNew fast path
        if (_capturedTypes.Count == 1)
        {
            archetype.TryGetComponentIndex(_capturedTypes[0], out var ci);
            _cachedArchetype = archetype;
            _cachedColIdx = ci;
        }
    }

    /// <summary>
    /// Inline capture: read New from storage AFTER write. Called from ApplyTypedSet fast path.
    /// Skips filter/archetype checks — archetype cannot change during Set.
    /// No-op with Lazy New (New is read in DrainChanges).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CaptureNew(Entity entity, Core.Archetype archetype, int row)
    {
        // Lazy New: no-op. New values are read from live storage in DrainChanges.
    }

    /// <summary>
    /// Adds a required component to the filter.
    /// </summary>
    public ChangeQuery With<TU>() where TU : unmanaged
    {
        EnsureUsable();
        if (_consumed)
            throw new InvalidOperationException("Cannot modify filter after enumeration started.");
        _filter = _filter.With<TU>();
        _cache = null;
        _hasFilter = true;
        return this;
    }

    /// <summary>
    /// Adds an excluded component to the filter.
    /// </summary>
    public ChangeQuery Without<TU>() where TU : unmanaged
    {
        EnsureUsable();
        if (_consumed)
        {
            ThrowFilterConsumed();
        }

        _filter = _filter.Without<TU>();
        _cache = null;
        _hasFilter = true;
        return this;
    }

    /// <summary>
    /// Adds an any-match component to the filter.
    /// </summary>
    public ChangeQuery WithAny<TU>() where TU : unmanaged
    {
        EnsureUsable();
        if (_consumed)
        {
            ThrowFilterConsumed();
        }

        _filter = _filter.WithAny<TU>();
        _cache = null;
        _hasFilter = true;
        return this;
    }

    /// <summary>
    /// Returns all structural transitions (create/destroy/add/remove) since the
    /// last call. The internal buffer is auto-cleared after enumeration.
    /// </summary>
    public IEnumerable<Transition> Transitions()
    {
        EnsureUsable();
        _consumed = true;
        var result = _transitions.ToArray();
        _transitions.Clear();
        return result;
    }

    /// <summary>
    /// Returns chunks whose component <typeparamref name="T"/> was written since
    /// the last call and whose archetype matches this query's filter.
    /// <typeparamref name="T"/> must have been captured via <see cref="Capture{T}"/>
    /// prior to this call.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <typeparamref name="T"/> was not captured.
    /// </exception>
    public IEnumerable<ChunkView> ModifiedChunks<T>() where T : unmanaged
    {
        EnsureUsable();
        _consumed = true;
        var ct = Component<T>.ComponentType;
        if (!_capturedTypes.Contains(ct))
            throw new InvalidOperationException(
                $"Component {typeof(T).Name} was not captured. Call .Capture<{typeof(T).Name}>() first.");

        var query = _world.Query(_filter);
        var snapshotEpoch = _world.CurrentWriteEpoch;
        var cursor = _valueCursors[ct.Value];
        var result = new List<ChunkView>();
        var chunks = query.GetChunks().ToArray();
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            var chunk = chunks[ci];
            var arch = chunk.Archetype;
            if (!arch.TryGetComponentIndex(ct, out var col))
                continue;
            var versions = arch._columnVersions;
            if (versions is not null && versions[col] > cursor)
                result.Add(chunk);
        }
        _valueCursors[ct.Value] = snapshotEpoch;
        return result;
    }

    /// <summary>
    /// Zero-allocation variant of <see cref="ModifiedChunks{T}"/>.
    /// Returns a span over an internal reusable buffer. The span is valid until
    /// the next call to any drain method on this <see cref="ChangeQuery"/> instance.
    /// </summary>
    public ReadOnlySpan<ChunkView> DrainModifiedChunks<T>() where T : unmanaged
    {
        EnsureUsable();
        _consumed = true;
        var ct = Component<T>.ComponentType;
        if (!_capturedTypes.Contains(ct))
            throw new InvalidOperationException(
                $"Component {typeof(T).Name} was not captured. Call .Capture<{typeof(T).Name}>() first.");

        var query = _world.Query(_filter);
        var snapshotEpoch = _world.CurrentWriteEpoch;
        var cursor = _valueCursors[ct.Value];
        var chunks = query.GetChunks();
        var count = 0;

        // Ensure buffer capacity
        if (_drainBuffer.Length < chunks.Length)
            _drainBuffer = new ChunkView[chunks.Length];

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            var chunk = chunks[ci];
            var arch = chunk.Archetype;
            if (!arch.TryGetComponentIndex(ct, out var col))
                continue;
            var versions = arch._columnVersions;
            if (versions is not null && versions[col] > cursor)
                _drainBuffer[count++] = chunk;
        }
        _valueCursors[ct.Value] = snapshotEpoch;
        return new ReadOnlySpan<ChunkView>(_drainBuffer, 0, count);
    }

    /// <summary>
    /// Returns all old/new snapshot pairs accumulated since the last call.
    /// <see cref="Previous"/> must have been enabled before enumeration started.
    /// Returns an empty array when no changes occurred.
    /// </summary>
    /// <remarks>
    /// The returned array shares internal byte storage — consume it before the
    /// next <see cref="Changes"/> call (which clears the internal buffer).
    /// </remarks>
    public EntityChange[] Changes()
    {
        EnsureUsable();
        _consumed = true;
        if (!_hasPrevious)
            return [];

        // Typed fast path: single Capture<T> + Previous
        // Convert typed tracker data to EntityChange format for compatibility
        if (_typedTracker is not null)
        {
            return DrainTypedTrackerToEntityChanges();
        }

        if (_dirtyCount == 0)
            return [];

        // Ensure types/offsets copies are cached
        if (_cachedTypesCopy.Length == 0)
        {
            _cachedTypesCopy = _capturedTypes.ToArray();
            _cachedOffsetsCopy = (int[])_offsets.Clone();
        }

        // Build result: Old from shadow, New from live storage
        var data = new byte[_dirtyCount * _snapshotSize * 2];
        var result = new EntityChange[_dirtyCount];
        for (var i = 0; i < _dirtyCount; i++)
        {
            var id = _dirtyList[i];
            var entity = _dirtyEntityMap[id];
            var shadowOff = id * _snapshotSize;
            var dstOff = i * _snapshotSize * 2;

            // Copy Old from shadow
            Buffer.BlockCopy(_shadowValues, shadowOff, data, dstOff, _snapshotSize);

            // Read New from live storage (Lazy New)
            var arch = _dirtyArchetypes[id];
            if (arch is not null)
            {
                var row = _dirtyRows[id];
                for (var t = 0; t < _capturedTypes.Count; t++)
                {
                    if (arch.TryGetComponentIndex(_capturedTypes[t], out var colIdx))
                    {
                        var src = arch.GetComponentBytes(colIdx, row);
                        src.CopyTo(new Span<byte>(data, dstOff + _snapshotSize + _offsets[t], _typeSizes[t]));
                    }
                }
            }

            result[i] = new EntityChange(
                entity, data, dstOff, dstOff + _snapshotSize,
                _snapshotSize, _cachedOffsetsCopy, _cachedTypesCopy);
        }

        // Reset dirty state
        for (var i = 0; i < _dirtyCount; i++)
            _isDirty[_dirtyList[i]] = false;
        _dirtyCount = 0;
        return result;
    }

    /// <summary>
    /// Drains typed tracker data into EntityChange[] format for compatibility.
    /// Uses reflection to dispatch to the generic implementation.
    /// </summary>
    private EntityChange[] DrainTypedTrackerToEntityChanges()
    {
        if (!ComponentRegistry.Shared.TryGetType(_capturedTypes[0], out var runtimeType))
            return [];

        var method = typeof(ChangeQuery).GetMethod(
            nameof(DrainTypedTrackerToEntityChangesGeneric),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var generic = method.MakeGenericMethod(runtimeType);
        return (EntityChange[])generic.Invoke(this, null)!;
    }

    private EntityChange[] DrainTypedTrackerToEntityChangesGeneric<T>() where T : unmanaged
    {
        var changes = DrainTypedChanges<T>();
        var count = changes.Length;
        if (count == 0)
            return [];

        // Ensure types/offsets copies are cached
        if (_cachedTypesCopy.Length == 0)
        {
            _cachedTypesCopy = _capturedTypes.ToArray();
            _cachedOffsetsCopy = (int[])_offsets.Clone();
        }

        var elemSize = Unsafe.SizeOf<T>();
        var data = new byte[count * _snapshotSize * 2];
        var result = new EntityChange[count];

        for (var i = 0; i < count; i++)
        {
            var dstOff = i * _snapshotSize * 2;
            ref readonly var change = ref changes[i];

            // Copy Old
            Unsafe.CopyBlockUnaligned(
                ref data[dstOff],
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in change.Old)),
                (uint)elemSize);

            // Copy New
            Unsafe.CopyBlockUnaligned(
                ref data[dstOff + _snapshotSize],
                ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in change.New)),
                (uint)elemSize);

            result[i] = new EntityChange(
                change.Entity, data, dstOff, dstOff + _snapshotSize,
                _snapshotSize, _cachedOffsetsCopy, _cachedTypesCopy);
        }

        return result;
    }

    /// <summary>
    /// Zero-allocation variant of <see cref="Changes"/>.
    /// Returns a span over an internal reusable buffer of old/new snapshot pairs.
    /// The span is valid until the next call to any drain method on this
    /// <see cref="ChangeQuery"/> instance.
    /// <see cref="Previous"/> must have been enabled before enumeration started.
    /// </summary>
    public ReadOnlySpan<EntityChange> DrainChanges()
    {
        EnsureUsable();
        _consumed = true;
        if (!_hasPrevious || _dirtyCount == 0)
            return ReadOnlySpan<EntityChange>.Empty;

        // Ensure types/offsets copies are cached
        if (_cachedTypesCopy.Length == 0)
        {
            _cachedTypesCopy = _capturedTypes.ToArray();
            _cachedOffsetsCopy = (int[])_offsets.Clone();
        }

        // Ensure reusable data buffer capacity
        var dataLen = _dirtyCount * _snapshotSize * 2;
        if (_drainData.Length < dataLen)
            _drainData = new byte[dataLen];

        // Build result: Old from shadow, New from live storage
        for (var i = 0; i < _dirtyCount; i++)
        {
            var id = _dirtyList[i];
            var shadowOff = id * _snapshotSize;
            var dstOff = i * _snapshotSize * 2;

            // Copy Old from shadow
            Buffer.BlockCopy(_shadowValues, shadowOff, _drainData, dstOff, _snapshotSize);

            // Read New from live storage (Lazy New)
            var arch = _dirtyArchetypes[id];
            if (arch is not null)
            {
                var row = _dirtyRows[id];
                for (var t = 0; t < _capturedTypes.Count; t++)
                {
                    if (arch.TryGetComponentIndex(_capturedTypes[t], out var colIdx))
                    {
                        var src = arch.GetComponentBytes(colIdx, row);
                        src.CopyTo(new Span<byte>(_drainData, dstOff + _snapshotSize + _offsets[t], _typeSizes[t]));
                    }
                }
            }
        }

        // Ensure reusable result buffer capacity
        if (_drainResult.Length < _dirtyCount)
            _drainResult = new EntityChange[_dirtyCount];

        // Fill result buffer
        for (var i = 0; i < _dirtyCount; i++)
        {
            var off = i * _snapshotSize * 2;
            _drainResult[i] = new EntityChange(
                _dirtyEntityMap[_dirtyList[i]], _drainData, off, off + _snapshotSize,
                _snapshotSize, _cachedOffsetsCopy, _cachedTypesCopy);
        }

        var count = _dirtyCount;

        // Reset dirty state
        for (var i = 0; i < _dirtyCount; i++)
            _isDirty[_dirtyList[i]] = false;
        _dirtyCount = 0;

        return new ReadOnlySpan<EntityChange>(_drainResult, 0, count);
    }

    /// <summary>
    /// Typed fast-path drain for single Capture&lt;T&gt; + Previous.
    /// Returns old/new pairs directly from the tracker's double-buffered
    /// <see cref="TypedChange{T}"/>[] — zero copy, no construction overhead.
    /// </summary>
    public ReadOnlySpan<TypedChange<T>> DrainTypedChanges<T>() where T : unmanaged
    {
        EnsureUsable();
        _consumed = true;
        if (!_hasPrevious || _typedTracker is not ChangeTracker<T> tracker)
            return ReadOnlySpan<TypedChange<T>>.Empty;

        return tracker.Drain();
    }

    // ── IChangeQuery ──

    void IChangeQuery.OnBeforeWrite(Entity entity, Archetype archetype, int row)
    {
        // Same logic as CaptureOld but without inline fast path
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        if (_hasFilter)
        {
            var cache = _cache ??= _world.Query(_filter).Advanced;
            if (!cache.Matches(archetype)) return;
        }

        var id = entity.Id;
        EnsureShadowCapacity(id);

        // Already captured this tick? Skip.
        if (_isDirty[id]) return;
        _isDirty[id] = true;

        // Add to dirty list
        EnsureDirtyListCapacity();
        _dirtyList[_dirtyCount++] = id;

        // Store location for Lazy New
        _dirtyArchetypes[id] = archetype;
        _dirtyRows[id] = row;
        _dirtyEntityMap[id] = entity;

        // Capture Old for all captured types into shadow[id]
        var shadowOff = id * _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
            {
                var src = archetype.GetComponentBytes(colIdx, row);
                src.CopyTo(new Span<byte>(_shadowValues, shadowOff + _offsets[i], _typeSizes[i]));
            }
        }
    }

    void IChangeQuery.OnAfterWrite(Entity entity, Archetype archetype, int row)
    {
        // Lazy New: no-op. New values are read from live storage in DrainChanges.
    }

    void IChangeQuery.OnBeforeTransition(Entity entity, Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        // For structural changes, we always create a new entry (no dedup).
        var id = entity.Id;
        EnsureShadowCapacity(id);

        // Always add to dirty list for structural changes
        EnsureDirtyListCapacity();
        _dirtyList[_dirtyCount++] = id;

        // Store location for Lazy New
        _dirtyArchetypes[id] = archetype;
        _dirtyRows[id] = row;
        _dirtyEntityMap[id] = entity;

        // If entity was not dirty from Set, capture Old now.
        if (!_isDirty[id])
        {
            _isDirty[id] = true;
            var shadowOff = id * _snapshotSize;
            for (var i = 0; i < _capturedTypes.Count; i++)
            {
                if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
                {
                    var src = archetype.GetComponentBytes(colIdx, row);
                    src.CopyTo(new Span<byte>(_shadowValues, shadowOff + _offsets[i], _typeSizes[i]));
                }
            }
        }
    }

    void IChangeQuery.OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype)
    {
        _consumed = true;
        var cache = _cache ??= _world.Query(_filter).Advanced;
        var oldMatch = oldArchetype is { } o && cache.Matches(o);
        var newMatch = newArchetype is { } n && cache.Matches(n);

        if ((!oldMatch && newMatch) || (oldMatch && !newMatch))
        {
            TransitionCause cause;
            if (!oldMatch && newMatch)
                cause = oldArchetype is null ? TransitionCause.Created : TransitionCause.Added;
            else
                cause = newArchetype is null ? TransitionCause.Destroyed : TransitionCause.Removed;

            _transitions.Add(new Transition(cause, entity));

            if (_hasPrevious && oldArchetype is not null && newArchetype is not null)
            {
                WriteNewTransitionSnapshot(entity, newArchetype);
            }
        }
        else if (_hasPrevious && _capturedTypes.Count > 0)
        {
            // Transition did NOT match filter — roll back
            if (_dirtyCount > 0)
            {
                var id = entity.Id;
                if (id < _isDirty.Length)
                    _isDirty[id] = false;
                _dirtyCount--;
            }
        }
    }

    private void WriteNewTransitionSnapshot(Entity entity, Archetype newArch)
    {
        // Update the location for this entity (Old is already in shadow[id])
        var id = entity.Id;
        _dirtyArchetypes[id] = newArch;
        var record = _world.GetRecordFast(entity);
        _dirtyRows[id] = record.RowIndex;
    }

    private static void ThrowFilterConsumed()
    {
        throw new InvalidOperationException(
            "Cannot modify the filter after ModifiedChunks<T>() or Transitions() has been called. " +
            "Configure With/Without/WithAny before the first enumeration.");
    }
}
