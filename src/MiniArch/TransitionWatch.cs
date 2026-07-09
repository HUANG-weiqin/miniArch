using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// A pull-based watch that tracks structural transitions (entities entering or exiting
/// a query filter) by comparing the current world against the last <see cref="Snapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Snapshot"/> to record which entities match the filter. Then call
/// <see cref="Diff"/> to discover entities that entered or exited since the snapshot.
/// </para>
/// <para>
/// Exited transitions report the entity version at snapshot time. Entered transitions
/// report the current entity version.
/// </para>
/// <para>
/// If an entity is destroyed and a new entity is created at the same id (with the same
/// filter match), no net transition is reported (id-based semantics for simplicity).
/// </para>
/// <para>
/// Membership is tracked via dense epoch arrays keyed by entity.Id, avoiding per-Diff
/// heap allocation after warmup. Each id stores the last epoch it was touched; comparing
/// the stored epoch against the current snapshot/current epoch gives O(1) membership.
/// No per-Diff clearing is needed — consecutive snapshots replace the baseline by
/// bumping the snapshot epoch, so old marks become stale automatically.
/// </para>
/// </remarks>
public sealed class TransitionWatch<THandler>
    where THandler : struct, ITransitionHandler
{
    private Entity[] _snapshotEntities = [];
    private int _snapshotCount;
    private int[] _snapshotMarks = [];
    private int _snapshotEpoch;
    private readonly QueryDescription _query;
    private THandler _handler;
    private bool _hasSnapshot;

    // Reusable state for Diff — zero per-call heap allocation (except array growth).
    private int[] _currentMarks = [];
    private int _currentEpoch;
    private Entity[] _currentEntities = [];
    private int _currentCount;

    private struct TransitionEntry
    {
        public Entity Entity;
        public TransitionKind Kind;
    }

    private TransitionEntry[] _buffer = [];

    internal TransitionWatch(QueryDescription query, THandler handler)
    {
        _query = query;
        _handler = handler;
    }

    /// <summary>
    /// Gets or sets the handler.
    /// </summary>
    public ref THandler Handler => ref _handler;

    /// <summary>
    /// Records which entities currently match the watch's query filter.
    /// Must be called before <see cref="Diff"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Snapshot(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // Bump snapshot epoch; if it wraps, reset marks to zero.
        unchecked { _snapshotEpoch++; }
        if (_snapshotEpoch == 0)
        {
            Array.Clear(_snapshotMarks);
            _snapshotEpoch = 1;
        }

        _snapshotCount = 0;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                var entity = entities[i];
                EnsureMarkCapacity(ref _snapshotMarks, entity.Id);
                _snapshotMarks[entity.Id] = _snapshotEpoch;

                if (_snapshotCount >= _snapshotEntities.Length)
                    Array.Resize(ref _snapshotEntities, Math.Max(_snapshotCount + 1, _snapshotEntities.Length * 2));
                _snapshotEntities[_snapshotCount++] = entity;
            }
        }

        _hasSnapshot = true;
    }

    /// <summary>
    /// Scans the current world and calls <see cref="ITransitionHandler.OnChange"/>
    /// for each entity that entered or exited the filter since the snapshot.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="Snapshot"/> has not been called.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Diff(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!_hasSnapshot)
            throw new InvalidOperationException(
                "TransitionWatch.Diff requires a prior Snapshot call. Call Snapshot(World) before Diff.");

        // Bump current epoch; if it wraps, reset marks to zero.
        unchecked { _currentEpoch++; }
        if (_currentEpoch == 0)
        {
            Array.Clear(_currentMarks);
            _currentEpoch = 1;
        }

        // Phase 1: Scan current query into reusable _currentMarks and _currentEntities.
        // No pre-clear needed: the epoch bump above invalidates all previous marks.
        _currentCount = 0;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                var entity = entities[i];
                EnsureMarkCapacity(ref _currentMarks, entity.Id);
                _currentMarks[entity.Id] = _currentEpoch;

                if (_currentCount >= _currentEntities.Length)
                    Array.Resize(ref _currentEntities, Math.Max(_currentCount + 1, _currentEntities.Length * 2));
                _currentEntities[_currentCount++] = entity;
            }
        }

        var bufferCount = 0;

        // Phase 2a: Exited — snapshot entities not marked with current epoch.
        for (var si = 0; si < _snapshotCount; si++)
        {
            var id = _snapshotEntities[si].Id;
            if ((uint)id < (uint)_currentMarks.Length && _currentMarks[id] == _currentEpoch)
                continue;

            if (bufferCount >= _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(bufferCount + 1, _buffer.Length * 2));
            _buffer[bufferCount++] = new TransitionEntry
            {
                Entity = _snapshotEntities[si],
                Kind = TransitionKind.Exited
            };
        }

        // Phase 2b: Entered — current entities not marked with snapshot epoch.
        for (var ci = 0; ci < _currentCount; ci++)
        {
            var id = _currentEntities[ci].Id;
            if ((uint)id < (uint)_snapshotMarks.Length && _snapshotMarks[id] == _snapshotEpoch)
                continue;

            if (bufferCount >= _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(bufferCount + 1, _buffer.Length * 2));
            _buffer[bufferCount++] = new TransitionEntry
            {
                Entity = _currentEntities[ci],
                Kind = TransitionKind.Entered
            };
        }

        // Phase 3: dispatch callbacks (collected buffer is stable, safe for handler to mutate world).
        for (var i = 0; i < bufferCount; i++)
        {
            ref var entry = ref _buffer[i];
            _handler.OnChange(world, entry.Entity, entry.Kind);
        }
    }

    // ── Mark helpers (small, inlineable) ─────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureMarkCapacity(ref int[] marks, int id)
    {
        if ((uint)id >= (uint)marks.Length)
        {
            var newSize = Math.Max(id + 1, marks.Length * 2);
            Array.Resize(ref marks, newSize);
        }
    }
}
