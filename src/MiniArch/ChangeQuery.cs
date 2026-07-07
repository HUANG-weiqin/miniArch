using System.Collections.Generic;
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

    // Snapshot state: direct-indexed by entity ID (no Dictionary)
    // Uses epoch + version to handle entity ID reuse safely.
    private int _snapEpoch;               // incremented on drain/reset
    private int[] _stampById = [];        // _stampById[id] == _snapEpoch → written this epoch
    private int[] _versionById = [];      // entity version at time of first write
    private int[] _slotById = [];         // slot index + 1 (0 = not dirty)
    private Entity[] _dirtyList = [];     // ordered dirty entities, indexed by slot
    private byte[] _oldValues = [];       // old values, indexed by slot * _snapshotSize
    private byte[] _newValues = [];       // new values, indexed by slot * _snapshotSize
    private int _snapCount;               // number of dirty entities

    // Location storage for Lazy New (read New from live storage in Changes())
    private Core.Archetype?[] _dirtyArchetypes = [];
    private int[] _dirtyRow = [];

    // Cached component index for hot path (avoids repeated TryGetComponentIndex)
    private Core.Archetype? _cachedArchetype;
    private int _cachedColIdx;

    // Reusable buffer for DrainModifiedChunks<T> (grown, never shrunk)
    private ChunkView[] _drainBuffer = [];

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
        _snapCount = 0;
        _snapEpoch++;
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
    private void EnsureEntityCapacity(int id)
    {
        if (id < _stampById.Length) return;
        var newSize = Math.Max(id + 1, _stampById.Length == 0 ? 64 : _stampById.Length * 2);
        Array.Resize(ref _stampById, newSize);
        Array.Resize(ref _versionById, newSize);
        Array.Resize(ref _slotById, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSlotCapacity()
    {
        var needed = (_snapCount + 1) * _snapshotSize;
        if (needed <= _oldValues.Length) return;
        var newSize = Math.Max(needed, _oldValues.Length == 0 ? 256 : _oldValues.Length * 2);
        Array.Resize(ref _oldValues, newSize);
        Array.Resize(ref _newValues, newSize);
        var slotSize = Math.Max(_snapCount + 1, _dirtyList.Length == 0 ? 64 : _dirtyList.Length * 2);
        Array.Resize(ref _dirtyList, slotSize);
        Array.Resize(ref _dirtyArchetypes, slotSize);
        Array.Resize(ref _dirtyRow, slotSize);
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

        // Pre-allocate entity-indexed arrays to world capacity
        var cap = _world.EntityCapacity;
        if (_stampById.Length < cap)
        {
            _stampById = new int[cap];
            _versionById = new int[cap];
            _slotById = new int[cap];
        }

        return this;
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
        EnsureEntityCapacity(id);

        // Already recorded this epoch?
        if (_stampById[id] == _snapEpoch && _versionById[id] == entity.Version)
            return;

        // First write — allocate slot
        EnsureSlotCapacity();
        var slot = _snapCount++;
        _stampById[id] = _snapEpoch;
        _versionById[id] = entity.Version;
        _slotById[id] = slot + 1;
        _dirtyList[slot] = entity;

        // Store location for Lazy New
        _dirtyArchetypes[slot] = archetype;
        _dirtyRow[slot] = row;

        // Capture Old for all captured types
        var oldOff = slot * _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
            {
                var src = archetype.GetComponentBytes(colIdx, row);
                src.CopyTo(new Span<byte>(_oldValues, oldOff + _offsets[i], _typeSizes[i]));
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
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CaptureNew(Entity entity, Core.Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        var id = entity.Id;
        if (id >= _stampById.Length) return;
        if (_stampById[id] != _snapEpoch || _versionById[id] != entity.Version) return;

        var slot = _slotById[id] - 1;
        var newOff = slot * _snapshotSize;

        if (_capturedTypes.Count == 1 && _cachedArchetype == archetype)
        {
            // Fast path: single type, cached index
            var src = archetype.GetComponentBytes(_cachedColIdx, row);
            src.CopyTo(new Span<byte>(_newValues, newOff, _snapshotSize));
        }
        else
        {
            // Multi-type: iterate all captured types
            for (var i = 0; i < _capturedTypes.Count; i++)
            {
                if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
                {
                    var src = archetype.GetComponentBytes(colIdx, row);
                    src.CopyTo(new Span<byte>(_newValues, newOff + _offsets[i], _typeSizes[i]));
                }
            }
        }
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
        if (!_hasPrevious || _snapCount == 0)
            return [];

        // Lazy New: read New values from live component storage
        for (var i = 0; i < _snapCount; i++)
        {
            var arch = _dirtyArchetypes[i];
            if (arch is null) continue; // materialized already (structural escape)
            var row = _dirtyRow[i];
            var newOff = i * _snapshotSize;
            for (var t = 0; t < _capturedTypes.Count; t++)
            {
                if (arch.TryGetComponentIndex(_capturedTypes[t], out var colIdx))
                {
                    var src = arch.GetComponentBytes(colIdx, row);
                    src.CopyTo(new Span<byte>(_newValues, newOff + _offsets[t], _typeSizes[t]));
                }
            }
        }

        var data = new byte[_snapCount * _snapshotSize * 2];
        for (var i = 0; i < _snapCount; i++)
        {
            var srcOff = i * _snapshotSize;
            var dstOff = i * _snapshotSize * 2;
            Buffer.BlockCopy(_oldValues, srcOff, data, dstOff, _snapshotSize);
            Buffer.BlockCopy(_newValues, srcOff, data, dstOff + _snapshotSize, _snapshotSize);
        }

        var typesCopy = _capturedTypes.ToArray();
        var offsetsCopy = (int[])_offsets.Clone();
        var result = new EntityChange[_snapCount];
        for (var i = 0; i < _snapCount; i++)
        {
            var off = i * _snapshotSize * 2;
            result[i] = new EntityChange(
                _dirtyList[i], data, off, off + _snapshotSize,
                _snapshotSize, offsetsCopy, typesCopy);
        }

        _snapCount = 0;
        _snapEpoch++;
        return result;
    }

    // ── IChangeQuery ──

    void IChangeQuery.OnBeforeWrite(Entity entity, Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        if (_hasFilter)
        {
            var cache = _cache ??= _world.Query(_filter).Advanced;
            if (!cache.Matches(archetype)) return;
        }

        var id = entity.Id;
        EnsureEntityCapacity(id);

        // Check if this exact entity version was already recorded this epoch
        if (_stampById[id] == _snapEpoch && _versionById[id] == entity.Version)
            return;  // already recorded, keep first Old

        // First write for this entity in this epoch
        EnsureSlotCapacity();
        var slot = _snapCount++;
        _stampById[id] = _snapEpoch;
        _versionById[id] = entity.Version;
        _slotById[id] = slot + 1;
        _dirtyList[slot] = entity;

        // Capture Old
        var oldOff = slot * _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
            {
                var src = archetype.GetComponentBytes(colIdx, row);
                src.CopyTo(new Span<byte>(_oldValues, oldOff + _offsets[i], _typeSizes[i]));
            }
        }
    }

    void IChangeQuery.OnAfterWrite(Entity entity, Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        if (_hasFilter)
        {
            var cache = _cache ??= _world.Query(_filter).Advanced;
            if (!cache.Matches(archetype)) return;
        }

        var id = entity.Id;
        if (id >= _stampById.Length) return;
        if (_stampById[id] != _snapEpoch || _versionById[id] != entity.Version) return;

        var slot = _slotById[id] - 1;

        // Capture New (always overwrite — we want the LAST value)
        var newOff = slot * _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
            {
                var src = archetype.GetComponentBytes(colIdx, row);
                src.CopyTo(new Span<byte>(_newValues, newOff + _offsets[i], _typeSizes[i]));
            }
        }
    }

    void IChangeQuery.OnBeforeTransition(Entity entity, Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        // Materialize-on-escape: if entity is dirty from a previous Set,
        // read New from live storage before structural change invalidates it.
        var id = entity.Id;
        if (id < _stampById.Length && _stampById[id] == _snapEpoch && _versionById[id] == entity.Version)
        {
            var slot = _slotById[id] - 1;
            if (_dirtyArchetypes[slot] is not null)
            {
                // Entity is dirty from Set — materialize New now for all captured types
                var newOff = slot * _snapshotSize;
                for (var t = 0; t < _capturedTypes.Count; t++)
                {
                    if (archetype.TryGetComponentIndex(_capturedTypes[t], out var colIdx))
                    {
                        var src = archetype.GetComponentBytes(colIdx, row);
                        src.CopyTo(new Span<byte>(_newValues, newOff + _offsets[t], _typeSizes[t]));
                    }
                }
                _dirtyArchetypes[slot] = null; // mark as materialized
            }
        }

        // Structural changes always get their own entry (not per-entity dedup)
        EnsureSlotCapacity();
        var newSlot = _snapCount++;
        _dirtyList[newSlot] = entity;

        var oldOff = newSlot * _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            if (archetype.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
            {
                var src = archetype.GetComponentBytes(colIdx, row);
                src.CopyTo(new Span<byte>(_oldValues, oldOff + _offsets[i], _typeSizes[i]));
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
            if (_snapCount > 0)
                _snapCount--;
        }
    }

    private void WriteNewTransitionSnapshot(Entity entity, Archetype newArch)
    {
        var slot = _snapCount - 1;

        var record = _world.GetRecordFast(entity);
        var newOff = slot * _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            if (newArch.TryGetComponentIndex(_capturedTypes[i], out var colIdx))
            {
                var src = newArch.GetComponentBytes(colIdx, record.RowIndex);
                src.CopyTo(new Span<byte>(_newValues, newOff + _offsets[i], _typeSizes[i]));
            }
        }
    }

    private static void ThrowFilterConsumed()
    {
        throw new InvalidOperationException(
            "Cannot modify the filter after ModifiedChunks<T>() or Transitions() has been called. " +
            "Configure With/Without/WithAny before the first enumeration.");
    }
}
