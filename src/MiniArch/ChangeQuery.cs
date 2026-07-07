using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Stateful cursor over changes to component <typeparamref name="T"/>. Hold one instance per
/// consuming system; each enumeration call auto-advances its cursor. Obtain via <see cref="World.Track{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// By default the cursor tracks entities that have component <typeparamref name="T"/>. Use the
/// <see cref="With{TU}"/>, <see cref="Without{TU}"/>, and <see cref="WithAny{TU}"/> fluent methods
/// to narrow the tracked set. Fluent methods throw <see cref="InvalidOperationException"/> if called
/// after the first call to either <see cref="ModifiedChunks"/> or <see cref="Transitions"/>.
/// </para>
/// <para>
/// After a snapshot save/load, observer state resets. Call <see cref="World.Track{T}"/> again
/// to obtain a fresh cursor; discard cursors from before the restore.
/// </para>
/// </remarks>
/// <example>
/// Track HP changes for alive enemies only:
/// <code>
/// var hp = world.Track&lt;HP&gt;().Without&lt;Dead&gt;().With&lt;Enemy&gt;();
///
/// // Transitions: Entered when entity enters {HP, !Dead, Enemy},
/// // Exited when entity leaves it (e.g. Add&lt;Dead&gt; fires Exited).
/// foreach (var t in hp.Transitions())
///     ToggleHealthBar(t.Entity, t.Kind == TransitionKind.Entered);
///
/// // ModifiedChunks: HP-value changes in matching archetypes.
/// foreach (var chunk in hp.ModifiedChunks())
///     foreach (ref var h in chunk.GetSpan&lt;HP&gt;())
///         UpdateDamageNumber(chunk.GetEntityId(ref h), h.Value);
/// </code>
/// </example>
public sealed class ChangeQuery<T> : IChangeQuery, IValueChangeSink<T> where T : unmanaged
{
    private readonly World _world;
    private readonly ComponentType _type;
    private QueryDescription _filter;
    private readonly List<Transition> _transitions;
    private readonly List<ValueChange<T>> _valueChanges;
    private Core.QueryCache? _cache;
    private long _valueCursor;
    private bool _consumed;
    private bool _trackPrevious;

    internal ChangeQuery(World world)
    {
        _world = world;
        _type = Component<T>.ComponentType;
        _filter = new QueryDescription().With<T>();
        _transitions = new List<Transition>(256);
        _valueChanges = new List<ValueChange<T>>(256);
    }

    /// <summary>
    /// Adds a required component to the change-query filter.
    /// Throws <see cref="InvalidOperationException"/> if called after the first
    /// <see cref="ModifiedChunks"/> or <see cref="Transitions"/> enumeration.
    /// </summary>
    /// <example>
    /// <code>
    /// var hp = world.Track&lt;HP&gt;().With&lt;Enemy&gt;();    // only enemy HP changes
    /// </code>
    /// </example>
    public ChangeQuery<T> With<TU>() where TU : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException(
                "Cannot modify the filter after ModifiedChunks() or Transitions() has been called. " +
                "Configure With/Without/WithAny before the first enumeration.");
        _filter = _filter.With<TU>();
        _cache = null;
        return this;
    }

    /// <summary>
    /// Adds an excluded component to the change-query filter.
    /// Throws <see cref="InvalidOperationException"/> if called after the first
    /// <see cref="ModifiedChunks"/> or <see cref="Transitions"/> enumeration.
    /// </summary>
    /// <example>
    /// <code>
    /// var hp = world.Track&lt;HP&gt;().Without&lt;Dead&gt;();  // alive entities only
    /// </code>
    /// </example>
    public ChangeQuery<T> Without<TU>() where TU : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException(
                "Cannot modify the filter after ModifiedChunks() or Transitions() has been called. " +
                "Configure With/Without/WithAny before the first enumeration.");
        _filter = _filter.Without<TU>();
        _cache = null;
        return this;
    }

    /// <summary>
    /// Adds an any-match component to the change-query filter.
    /// Throws <see cref="InvalidOperationException"/> if called after the first
    /// <see cref="ModifiedChunks"/> or <see cref="Transitions"/> enumeration.
    /// </summary>
    /// <example>
    /// <code>
    /// var buff = world.Track&lt;Burning&gt;().WithAny&lt;FireResistance&gt;();
    /// // tracks entities with Burning that also have either FireResistance or ... (any-match list)
    /// </code>
    /// </example>
    public ChangeQuery<T> WithAny<TU>() where TU : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException(
                "Cannot modify the filter after ModifiedChunks() or Transitions() has been called. " +
                "Configure With/Without/WithAny before the first enumeration.");
        _filter = _filter.WithAny<TU>();
        _cache = null;
        return this;
    }

    /// <summary>
    /// Enables per-write old-value capture. Each <c>World.Set&lt;T&gt;</c> on the tracked
    /// type produces a <see cref="ValueChange{T}"/> record containing the value before and
    /// after the write. The records are auto-cleared on each <see cref="Changes"/> call.
    /// Throws <see cref="InvalidOperationException"/> if called after any enumeration.
    /// </summary>
    /// <remarks>
    /// Previous values are captured for <see cref="World.Set{T}"/>, CommandStream.Set,
    /// and replay (FrameDelta) Set operations.
    /// <see cref="EntityAccessor.Set{T}"/> does NOT trigger capture — use
    /// <see cref="World.Set{T}"/> instead when previous values are needed.
    /// </remarks>
    /// <example>
    /// <code>
    /// var hp = world.Track&lt;HP&gt;().WithPreviousValues();
    ///
    /// foreach (var c in hp.Changes())
    ///     ShowDamageNumber(c.Entity, c.OldValue.Value - c.NewValue.Value);
    /// </code>
    /// </example>
    public ChangeQuery<T> WithPreviousValues()
    {
        if (_consumed)
            throw new InvalidOperationException(
                "Cannot enable previous-value tracking after enumeration has started.");
        if (_trackPrevious) return this;
        _trackPrevious = true;
        _world.GetOrCreateValueChangeBucket<T>().Register(this);
        return this;
    }

    /// <summary>
    /// Returns the <see cref="ValueChange{T}"/> records accumulated since the last
    /// call. Each record holds the entity, the old value before <c>World.Set&lt;T&gt;</c>,
    /// and the new value. The internal buffer is auto-cleared after enumeration.
    /// Returns an empty array if no changes occurred or if <see cref="WithPreviousValues"/>
    /// was not configured.
    /// </summary>
    /// <remarks>
    /// Covers <c>World.Set&lt;T&gt;</c>, CommandStream.Set, and replay (FrameDelta) Set
    /// operations. <see cref="EntityAccessor.Set{T}"/> does NOT produce records —
    /// use <c>World.Set&lt;T&gt;</c> instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// foreach (var c in hp.Changes())
    ///     AnimateDamage(c.Entity, c.OldValue.Value, c.NewValue.Value);
    /// </code>
    /// </example>
    public IEnumerable<ValueChange<T>> Changes()
    {
        _consumed = true;
        var result = _valueChanges.ToArray();
        _valueChanges.Clear();
        return result;
    }

    /// <summary>
    /// Returns chunks whose component <typeparamref name="T"/> was written since the last call
    /// and whose archetype matches this cursor's filter.
    /// Materializes eagerly; cursor advances regardless of consumer enumeration depth.
    /// </summary>
    /// <example>
    /// Iterate chunks with modified HP values:
    /// <code>
    /// foreach (var chunk in hp.ModifiedChunks())
    /// {
    ///     ref var h = ref chunk.GetComponentAt&lt;HP&gt;(0);
    ///     UpdateHealthBar(chunk.GetEntityId(0), h.Value);
    /// }
    /// </code>
    /// Consumed chunks are tracked so the next call only returns newly-written chunks.
    /// </example>
    public IEnumerable<ChunkView> ModifiedChunks()
    {
        _consumed = true;
        var query = _world.Query(_filter);
        var snapshotEpoch = _world.CurrentWriteEpoch;
        var result = new List<ChunkView>();
        var chunks = query.GetChunks().ToArray();
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            var chunk = chunks[ci];
            var arch = chunk.Archetype;
            if (!arch.TryGetComponentIndex(_type, out var col))
                continue;

            var versions = arch._columnVersions;
            if (versions is not null && versions[col] > _valueCursor)
                result.Add(chunk);
        }

        _valueCursor = snapshotEpoch;
        return result;
    }

    /// <summary>
    /// Returns entities that entered or exited the set matching this cursor's filter
    /// since the last call. The internal list is auto-cleared after enumeration.
    /// Materializes eagerly; cursor advances regardless of consumer enumeration depth.
    /// </summary>
    /// <example>
    /// React to membership changes:
    /// <code>
    /// foreach (var t in hp.Transitions())
    ///     if (t.Kind == TransitionKind.Entered)
    ///         SpawnHealthBar(t.Entity);
    ///     else
    ///         DestroyHealthBar(t.Entity);
    /// </code>
    /// The list is auto-cleared — subsequent calls return only new transitions.
    /// </example>
    public IEnumerable<Transition> Transitions()
    {
        _consumed = true;
        var result = _transitions.ToArray();
        _transitions.Clear();
        return result;
    }

    // ── IChangeQuery ────────────────────────────────────────────────

    void IChangeQuery.OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype)
    {
        _consumed = true;
        var cache = _cache ??= _world.Query(_filter).Advanced;
        var oldMatch = oldArchetype is { } o && cache.Matches(o);
        var newMatch = newArchetype is { } n && cache.Matches(n);
        if (!oldMatch && newMatch)
        {
            var cause = oldArchetype is null ? TransitionCause.Created : TransitionCause.Added;
            _transitions.Add(new Transition(cause, entity));
        }
        else if (oldMatch && !newMatch)
        {
            var cause = newArchetype is null ? TransitionCause.Destroyed : TransitionCause.Removed;
            _transitions.Add(new Transition(cause, entity));
        }
        // both match or neither match: membership unchanged -> skip
    }

    // ── IValueChangeSink<T> ──────────────────────────────────────────

    bool IValueChangeSink<T>.Matches(Core.Archetype archetype)
    {
        var cache = _cache ??= _world.Query(_filter).Advanced;
        return cache.Matches(archetype);
    }

    void IValueChangeSink<T>.OnValueChange(Entity entity, in T oldValue, in T newValue)
    {
        _valueChanges.Add(new ValueChange<T>(entity, in oldValue, in newValue));
    }
}

/// <summary>
/// Multi-component change query. Obtained via <see cref="World.Track()"/>.
/// Tracks structural transitions (create/destroy/add/remove) and, when
/// <see cref="Previous"/> is enabled, captures old/new snapshots of all
/// captured component types for each Set or structural change.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ChangeQuery{T}"/> (which tracks a single type), this
/// query captures <b>any subset</b> of component values via
/// <see cref="Capture{T}"/>. Filtering (With/Without/WithAny) is independent
/// of capture — a captured type is NOT automatically added to the filter.
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

    // Per-type cursor for ModifiedChunks<T>
    private readonly Dictionary<int, long> _valueCursors = new();

    // Reusable snapshot write buffer (grown, never shrunk)
    private byte[] _snapBuffer = new byte[1024];
    private readonly List<Entity> _snapEntities = new(); // parallel to _snapBuffer entries
    private int _snapCount;    // number of complete (Old+New) entries in buffer

    internal ChangeQuery(World world)
    {
        _world = world;
    }

    /// <summary>
    /// Registers component type <typeparamref name="T"/> for value capture.
    /// Calling <see cref="Changes"/> will include Old/New snapshots of this
    /// component. Does NOT add <typeparamref name="T"/> to the filter —
    /// use <see cref="With{TU}"/> if filtering is needed.
    /// </summary>
    public ChangeQuery Capture<T>() where T : unmanaged
    {
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
        if (_consumed)
            throw new InvalidOperationException("Cannot enable Previous after enumeration has started.");
        _hasPrevious = true;
        return this;
    }

    /// <summary>
    /// Adds a required component to the filter.
    /// </summary>
    public ChangeQuery With<TU>() where TU : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException("Cannot modify filter after enumeration started.");
        _filter = _filter.With<TU>();
        _cache = null;
        return this;
    }

    /// <summary>
    /// Adds an excluded component to the filter.
    /// </summary>
    public ChangeQuery Without<TU>() where TU : unmanaged
    {
        if (_consumed)
        {
            ThrowFilterConsumed();
        }

        _filter = _filter.Without<TU>();
        _cache = null;
        return this;
    }

    /// <summary>
    /// Adds an any-match component to the filter.
    /// </summary>
    public ChangeQuery WithAny<TU>() where TU : unmanaged
    {
        if (_consumed)
        {
            ThrowFilterConsumed();
        }

        _filter = _filter.WithAny<TU>();
        _cache = null;
        return this;
    }

    /// <summary>
    /// Returns all structural transitions (create/destroy/add/remove) since the
    /// last call. The internal buffer is auto-cleared after enumeration.
    /// </summary>
    public IEnumerable<Transition> Transitions()
    {
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
        _consumed = true;
        if (!_hasPrevious || _snapCount == 0)
            return [];

        var entryStride = _snapshotSize * 2;
        var totalSize = _snapCount * entryStride;
        var data = new byte[totalSize];
        Buffer.BlockCopy(_snapBuffer, 0, data, 0, totalSize);

        var typesCopy = _capturedTypes.ToArray();
        var offsetsCopy = (int[])_offsets.Clone();
        var result = new EntityChange[_snapCount];
        for (var i = 0; i < _snapCount; i++)
        {
            var off = i * entryStride;
            result[i] = new EntityChange(
                _snapEntities[i], data, off, off + _snapshotSize,
                _snapshotSize, offsetsCopy, typesCopy);
        }

        _snapCount = 0;
        _snapEntities.Clear();
        return result;
    }

    // ── IChangeQuery ──

    void IChangeQuery.OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype)
    {
        _consumed = true;
        var cache = _cache ??= _world.Query(_filter).Advanced;
        var oldMatch = oldArchetype is { } o && cache.Matches(o);
        var newMatch = newArchetype is { } n && cache.Matches(n);
        if (!oldMatch && newMatch)
        {
            var cause = oldArchetype is null ? TransitionCause.Created : TransitionCause.Added;
            _transitions.Add(new Transition(cause, entity));
            if (_hasPrevious) CaptureTransition(entity);
        }
        else if (oldMatch && !newMatch)
        {
            var cause = newArchetype is null ? TransitionCause.Destroyed : TransitionCause.Removed;
            _transitions.Add(new Transition(cause, entity));
            if (_hasPrevious) CaptureTransition(entity);
        }
    }

    private void CaptureTransition(Entity entity)
    {
        // Old values were captured by OnBeforeTransition.
        // New values need to be read after the entity moved.
        var record = _world.GetRecordFast(entity);
        var arch = record.Archetype;
        if (arch is null) return;

        EnsureSnapBufferCapacity();

        var entryBytes = _snapshotSize * 2;
        var newOff = _snapCount * entryBytes + _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            var colIdx = arch.GetComponentIndexFast(_capturedTypes[i]);
            var src = arch.GetComponentBytes(colIdx, record.RowIndex);
            src.CopyTo(new Span<byte>(_snapBuffer, newOff + _offsets[i], _typeSizes[i]));
        }
        _snapCount++;
    }

    private void EnsureSnapBufferCapacity()
    {
        var entryBytes = _snapshotSize * 2;
        var needed = (_snapCount + 1) * entryBytes;
        if (needed > _snapBuffer.Length)
            Array.Resize(ref _snapBuffer, Math.Max(needed, _snapBuffer.Length * 2));
    }

    private static void ThrowFilterConsumed()
    {
        throw new InvalidOperationException(
            "Cannot modify the filter after ModifiedChunks<T>() or Transitions() has been called. " +
            "Configure With/Without/WithAny before the first enumeration.");
    }

    // ── IChangeQuery pre/post hooks (implemented in Task 4) ──

    void IChangeQuery.OnBeforeWrite(Entity entity, Archetype archetype, int row) { }
    void IChangeQuery.OnAfterWrite(Entity entity, Archetype archetype, int row) { }
    void IChangeQuery.OnBeforeTransition(Entity entity, Archetype archetype, int row) { }
}
