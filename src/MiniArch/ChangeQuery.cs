using System.Collections.Generic;
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
public sealed class ChangeQuery<T> where T : unmanaged
{
    private readonly World _world;
    private readonly ComponentType _type;
    private QueryDescription _filter;
    private long _valueCursor;
    private long _transitionCursor;
    private int _transitionLogGeneration;
    private bool _consumed;

    internal ChangeQuery(World world)
    {
        _world = world;
        _type = Component<T>.ComponentType;
        _filter = new QueryDescription().With<T>();
    }

    /// <summary>
    /// Adds a required component to the change-query filter.
    /// Throws <see cref="InvalidOperationException"/> if called after the first
    /// <see cref="ModifiedChunks"/> or <see cref="Transitions"/> enumeration.
    /// </summary>
    public ChangeQuery<T> With<TU>() where TU : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException(
                "Cannot modify the filter after ModifiedChunks() or Transitions() has been called. " +
                "Configure With/Without/WithAny before the first enumeration.");
        _filter = _filter.With<TU>();
        return this;
    }

    /// <summary>
    /// Adds an excluded component to the change-query filter.
    /// Throws <see cref="InvalidOperationException"/> if called after the first
    /// <see cref="ModifiedChunks"/> or <see cref="Transitions"/> enumeration.
    /// </summary>
    public ChangeQuery<T> Without<TU>() where TU : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException(
                "Cannot modify the filter after ModifiedChunks() or Transitions() has been called. " +
                "Configure With/Without/WithAny before the first enumeration.");
        _filter = _filter.Without<TU>();
        return this;
    }

    /// <summary>
    /// Adds an any-match component to the change-query filter.
    /// Throws <see cref="InvalidOperationException"/> if called after the first
    /// <see cref="ModifiedChunks"/> or <see cref="Transitions"/> enumeration.
    /// </summary>
    public ChangeQuery<T> WithAny<TU>() where TU : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException(
                "Cannot modify the filter after ModifiedChunks() or Transitions() has been called. " +
                "Configure With/Without/WithAny before the first enumeration.");
        _filter = _filter.WithAny<TU>();
        return this;
    }

    /// <summary>
    /// Returns chunks whose component <typeparamref name="T"/> was written since the last call
    /// and whose archetype matches this cursor's filter.
    /// Materializes eagerly; cursor advances regardless of consumer enumeration depth.
    /// </summary>
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
    /// since the last call.
    /// Materializes eagerly; cursor advances regardless of consumer enumeration depth.
    /// </summary>
    public IEnumerable<Transition> Transitions()
    {
        _consumed = true;
        // Build (or reuse) a QueryCache from the filter so we can use its
        // pre-computed mask-based archetype matching.
        var cache = _world.Query(_filter).Advanced;
        var log = _world.GetTransitionLogInternal();
        var end = (long)log.Count;

        // If the log was cleared (ClearTransitionLog), reset cursor to start.
        if (_transitionLogGeneration != _world.TransitionLogGeneration)
        {
            _transitionCursor = 0;
            _transitionLogGeneration = _world.TransitionLogGeneration;
        }

        var result = new List<Transition>();
        for (long i = _transitionCursor; i < end; i++)
        {
            var entry = log[(int)i];
            var oldMatch = entry.OldArchetype is { } o && cache.Matches(o);
            var newMatch = entry.NewArchetype is { } n && cache.Matches(n);
            if (!oldMatch && newMatch)
                result.Add(new Transition(TransitionKind.Entered, entry.Entity));
            else if (oldMatch && !newMatch)
                result.Add(new Transition(TransitionKind.Exited, entry.Entity));
            // both match or neither match: membership in the filtered set unchanged → skip
        }

        _transitionCursor = end;
        return result;
    }
}
