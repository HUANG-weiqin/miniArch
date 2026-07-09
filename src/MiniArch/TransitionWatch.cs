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
/// </remarks>
public sealed class TransitionWatch<THandler>
    where THandler : struct, ITransitionHandler
{
    private Entity[] _snapshotEntities = [];
    private int _snapshotCount;
    private HashSet<int> _snapshotIds = [];
    private readonly QueryDescription _query;
    private THandler _handler;
    private bool _hasSnapshot;

    // Reusable state for Diff — zero per-call heap allocation (except array growth).
    private HashSet<int> _currentIds = [];
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

        _snapshotCount = 0;
        _snapshotIds.Clear();

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                var entity = entities[i];
                _snapshotIds.Add(entity.Id);
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

        // Phase 1: scan current query, populate reusable _currentIds and _currentEntities.
        _currentIds.Clear();
        _currentCount = 0;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                var entity = entities[i];
                _currentIds.Add(entity.Id);

                if (_currentCount >= _currentEntities.Length)
                    Array.Resize(ref _currentEntities, Math.Max(_currentCount + 1, _currentEntities.Length * 2));
                _currentEntities[_currentCount++] = entity;
            }
        }

        var bufferCount = 0;

        // Phase 2a: Exited — snapshot entities whose id is not in current (fast HashSet lookup).
        for (var si = 0; si < _snapshotCount; si++)
        {
            if (!_currentIds.Contains(_snapshotEntities[si].Id))
            {
                if (bufferCount >= _buffer.Length)
                    Array.Resize(ref _buffer, Math.Max(bufferCount + 1, _buffer.Length * 2));
                _buffer[bufferCount++] = new TransitionEntry
                {
                    Entity = _snapshotEntities[si],
                    Kind = TransitionKind.Exited
                };
            }
        }

        // Phase 2b: Entered — current entities whose id is not in snapshot.
        // O(1) lookup via _snapshotIds HashSet (populated during Snapshot).
        for (var ci = 0; ci < _currentCount; ci++)
        {
            if (_snapshotIds.Contains(_currentEntities[ci].Id))
                continue;

            if (bufferCount >= _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(bufferCount + 1, _buffer.Length * 2));
            _buffer[bufferCount++] = new TransitionEntry
            {
                Entity = _currentEntities[ci],
                Kind = TransitionKind.Entered
            };
        }

        // Phase 3: dispatch callbacks.
        for (var i = 0; i < bufferCount; i++)
        {
            ref var entry = ref _buffer[i];
            _handler.OnChange(world, entry.Entity, entry.Kind);
        }
    }
}
