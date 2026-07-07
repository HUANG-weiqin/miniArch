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

    void IChangeQuery.OnBeforeWrite(Entity entity, Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        // Check filter: only capture writes matching the query's archetype filter
        var cache = _cache ??= _world.Query(_filter).Advanced;
        if (!cache.Matches(archetype)) return;

        EnsureSnapBufferCapacity();

        // Write Old at (_snapCount * entryBytes)
        var entryBytes = _snapshotSize * 2;
        var oldOff = _snapCount * entryBytes;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            var colIdx = archetype.GetComponentIndexFast(_capturedTypes[i]);
            var src = archetype.GetComponentBytes(colIdx, row);
            src.CopyTo(new Span<byte>(_snapBuffer, oldOff + _offsets[i], _typeSizes[i]));
        }
        _snapEntities.Add(entity);
        // OnAfterWrite will write New and increment _snapCount
    }

    void IChangeQuery.OnAfterWrite(Entity entity, Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        // Must mirror the filter check from OnBeforeWrite to stay in sync.
        var cache = _cache ??= _world.Query(_filter).Advanced;
        if (!cache.Matches(archetype)) return;

        var entryBytes = _snapshotSize * 2;
        var newOff = _snapCount * entryBytes + _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            var colIdx = archetype.GetComponentIndexFast(_capturedTypes[i]);
            var src = archetype.GetComponentBytes(colIdx, row);
            src.CopyTo(new Span<byte>(_snapBuffer, newOff + _offsets[i], _typeSizes[i]));
        }
        _snapCount++;
    }

    void IChangeQuery.OnBeforeTransition(Entity entity, Archetype archetype, int row)
    {
        if (!_hasPrevious || _capturedTypes.Count == 0) return;

        EnsureSnapBufferCapacity();

        // Write Old at (_snapCount * entryBytes)
        var entryBytes = _snapshotSize * 2;
        var oldOff = _snapCount * entryBytes;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            var colIdx = archetype.GetComponentIndexFast(_capturedTypes[i]);
            var src = archetype.GetComponentBytes(colIdx, row);
            src.CopyTo(new Span<byte>(_snapBuffer, oldOff + _offsets[i], _typeSizes[i]));
        }
        _snapEntities.Add(entity);
        // OnTransition will write New and increment _snapCount if matched
    }

    void IChangeQuery.OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype)
    {
        _consumed = true;
        var cache = _cache ??= _world.Query(_filter).Advanced;
        var oldMatch = oldArchetype is { } o && cache.Matches(o);
        var newMatch = newArchetype is { } n && cache.Matches(n);

        if ((!oldMatch && newMatch) || (oldMatch && !newMatch))
        {
            // Matched: add transition entry
            TransitionCause cause;
            if (!oldMatch && newMatch)
                cause = oldArchetype is null ? TransitionCause.Created : TransitionCause.Added;
            else
                cause = newArchetype is null ? TransitionCause.Destroyed : TransitionCause.Removed;

            _transitions.Add(new Transition(cause, entity));

            // Capture snapshot pair only when BOTH old and new archetypes are
            // non-null (Add or Remove). Created (old=null) and Destroyed
            // (new=null) have no meaningful Old or New snapshot respectively.
            if (_hasPrevious && oldArchetype is not null && newArchetype is not null)
            {
                WriteNewTransitionSnapshot(entity, newArchetype);
            }
        }
        else if (_hasPrevious && _capturedTypes.Count > 0)
        {
            // Transition did NOT match filter, but we already called
            // OnBeforeTransition which added an entity to _snapEntities.
            // Roll back the entity list so Changes() indices stay correct.
            if (_snapEntities.Count > 0)
                _snapEntities.RemoveAt(_snapEntities.Count - 1);
        }
    }

    private void WriteNewTransitionSnapshot(Entity entity, Archetype newArch)
    {
        // Old values were written by OnBeforeTransition at _snapCount * entryBytes.
        // Now write New at _snapCount * entryBytes + _snapshotSize.
        var record = _world.GetRecordFast(entity);
        var entryBytes = _snapshotSize * 2;
        var newOff = _snapCount * entryBytes + _snapshotSize;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            var colIdx = newArch.GetComponentIndexFast(_capturedTypes[i]);
            var src = newArch.GetComponentBytes(colIdx, record.RowIndex);
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
}
