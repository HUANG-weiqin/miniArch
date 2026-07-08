using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// A per-subscription log of structural transitions (Create/Destroy/Add/Remove)
/// for entities matching a <see cref="QueryDescription"/> filter.
/// Obtained via <see cref="World.TrackTransitions(MiniArch.QueryDescription)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>TransitionLog</c> is independent — <see cref="Clear"/> affects only this log.
/// Multiple logs for the same filter each track membership independently.
/// </para>
/// <para>
/// After <see cref="World.RestoreState(MiniArch.Core.WorldStateSnapshot)"/> the log
/// self-heals with no stale data and continues tracking post-restore mutations.
/// </para>
/// </remarks>
public sealed class TransitionLog : Core.IChangeQuery
{
    private readonly World _world;
    private readonly QueryDescription _filter;
    private QueryCache? _cache;
    private readonly List<Transition> _transitions = new(256);
    private int _worldGen;

    internal TransitionLog(World world, QueryDescription filter)
    {
        _world = world;
        _filter = filter;
        _worldGen = world._trackingGeneration;
        world.RegisterChangeQuery(this);
    }

    void IChangeQuery.OnWorldRestored(int trackingGeneration)
    {
        // Clear stale accumulations; keep registration alive so post-restore
        // mutations are still observed.
        _transitions.Clear();
        _cache = null;
        _worldGen = trackingGeneration;
    }

    void IChangeQuery.OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype)
    {
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
        }
    }

    private void EnsureUsable()
    {
        if (_world.IsDisposed)
            throw new ObjectDisposedException(nameof(World));

        if (_worldGen == _world._trackingGeneration) return;

        // Self-heal: world state was reset (RestoreState/Dispose).
        // Clear stale accumulations.
        _transitions.Clear();
        _cache = null;
        _worldGen = _world._trackingGeneration;
    }

    /// <summary>
    /// Non-destructive read-only view of accumulated transitions since the last
    /// <see cref="Clear"/> call. The returned span is valid until the next <see cref="Clear"/>.
    /// </summary>
    public ReadOnlySpan<Transition> Transitions
    {
        get
        {
            EnsureUsable();
            return CollectionsMarshal.AsSpan(_transitions);
        }
    }

    /// <summary>
    /// Clears the accumulated transition list for this log only.
    /// </summary>
    public void Clear()
    {
        EnsureUsable();
        _transitions.Clear();
    }
}
