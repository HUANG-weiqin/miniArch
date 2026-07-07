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
public sealed class ChangeQuery<T> : IChangeQuery where T : unmanaged
{
    private readonly World _world;
    private readonly ComponentType _type;
    private QueryDescription _filter;
    private readonly List<Transition> _transitions;
    private Core.QueryCache? _cache;
    private long _valueCursor;
    private bool _consumed;

    internal ChangeQuery(World world)
    {
        _world = world;
        _type = Component<T>.ComponentType;
        _filter = new QueryDescription().With<T>();
        _transitions = new List<Transition>(256);
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
    /// Add&lt;Dead&gt; fires <see cref="TransitionKind.Exited"/> — entity left {HP, !Dead}.
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
            _transitions.Add(new Transition(TransitionKind.Entered, cause, entity));
        }
        else if (oldMatch && !newMatch)
        {
            var cause = newArchetype is null ? TransitionCause.Destroyed : TransitionCause.Removed;
            _transitions.Add(new Transition(TransitionKind.Exited, cause, entity));
        }
        // both match or neither match: membership unchanged -> skip
    }
}
