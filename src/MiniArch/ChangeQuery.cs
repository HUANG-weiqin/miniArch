using System.Collections.Generic;
using System.Reflection;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Multi-component change query. Obtained via <see cref="World.Track()"/>.
/// Tracks structural transitions (create/destroy/add/remove) and, when
/// <see cref="Previous"/> is enabled, captures old/new snapshots for the
/// single captured type (Capture&lt;T&gt; + Previous → ValueChanges&lt;T&gt;).
/// </summary>
/// <remarks>
/// <para>
/// Unlike single-type change queries, this query captures <b>any subset</b> of
/// component values via <see cref="Capture{T}"/>. Filtering (With/Without/WithAny)
/// is independent of capture — a captured type is NOT automatically added to the filter.
/// </para>
/// <para>
/// Fluent methods throw <see cref="InvalidOperationException"/> if called
/// after the first enumeration method (Transitions, ValueChanges)
/// has been invoked.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var q = world.Track()
///     .Capture&lt;Position&gt;()
///     .Previous();
///
/// var changes = q.ValueChanges&lt;Position&gt;();
/// for (var i = 0; i &lt; changes.Length; i++)
/// {
///     ref readonly var c = ref changes[i];
///     // c.Old, c.New, c.Entity
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
    private QueryCache? _cache;
    private bool _hasFilter;                          // false when no With/Without/WithAny → skip Matches()

    // Transition registration state
    private bool _transitionRegistered = true;

    private int _worldGen;  // captured at construction; compared on self-heal

    internal ChangeQuery(World world)
    {
        _world = world;
        _worldGen = world._trackingGeneration;
        // Do NOT register for transitions until a filter is configured.
        // Capture-only without filter is inert — no structural transitions possible.
        _transitionRegistered = false;
    }

    private void EnsureUsable()
    {
        if (_world.IsDisposed)
            throw new ObjectDisposedException(nameof(World));

        if (_worldGen == _world._trackingGeneration) return;

        // Self-heal: world state was reset (RestoreState/Dispose).
        // Clear stale accumulations and re-register dispatch paths.
        _transitions.Clear();
        _consumed = false;
        _worldGen = _world._trackingGeneration;
        _transitionRegistered = false;

        // Re-create shared tracker if needed (RestoreState cleared the registry).
        EnsureTrackerCreatedForCapturedType();

        // Re-establish transition registration.
        RefreshTransitionRegistration();
    }

    private bool IsValueOnlyTypedObserver =>
        _hasPrevious && !_hasFilter && _capturedTypes.Count == 1;

    private void EnsureTransitionRegistration()
    {
        if (_transitionRegistered) return;
        _world.RegisterChangeQuery(this);
        _transitionRegistered = true;
    }

    private void DisableTransitionRegistration()
    {
        if (!_transitionRegistered) return;
        _world.UnregisterChangeQuery(this);
        _transitionRegistered = false;
        _transitions.Clear();
    }

    private void RefreshTransitionRegistration()
    {
        if (_hasFilter)
        {
            // Filter present → transitions can match entities entering/exiting the filter.
            EnsureTransitionRegistration();
        }
        else
        {
            // No filter: without With/Without/WithAny, transitions would always
            // match everything and produce no entered/exited entries.
            // Value-only observers (Previous + single capture) also don't
            // collect transitions — they serve ValueChanges<T> only.
            DisableTransitionRegistration();
        }
    }

    /// <summary>
    /// Registers component type <typeparamref name="T"/> for value capture.
    /// Does NOT add <typeparamref name="T"/> to the filter —
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

        RefreshTransitionRegistration();

        // If Previous() was already called (and _hasPrevious is true), the tracker
        // must be created now — Previous() couldn't create it because _capturedTypes
        // was empty at the time.
        if (_hasPrevious && !_hasFilter && _capturedTypes.Count == 1)
            EnsureTrackerCreatedForCapturedType();

        return this;
    }

    /// <summary>
    /// Enables old-value snapshot capture via the typed fast path.
    /// When enabled, each Set on a single captured type produces a
    /// <see cref="TypedChange{T}"/> entry with both Old and New values.
    /// Off by default (zero overhead when off).
    /// </summary>
    public ChangeQuery Previous()
    {
        EnsureUsable();
        if (_consumed)
            throw new InvalidOperationException("Cannot enable Previous after enumeration has started.");
        _hasPrevious = true;

        // Ensure the shared tracker exists for single-Capture + Previous + no-filter case.
        EnsureTrackerCreatedForCapturedType();

        RefreshTransitionRegistration();
        return this;
    }

    /// <summary>
    /// Ensures the shared tracker exists in the world registry for the
    /// single captured type when Previous is enabled and no filter is set.
    /// </summary>
    private void EnsureTrackerCreatedForCapturedType()
    {
        if (!_hasPrevious || _capturedTypes.Count != 1 || _hasFilter) return;

        var capturedType = _capturedTypes[0];
        if (!ComponentRegistry.Shared.TryGetType(capturedType, out var runtimeType))
            return;

        // Use reflection to call GetOrCreateTracker<T>(entityCapacity) on the shared registry.
        // GetOrCreateSharedTrackers creates the registry lazily — no allocation in no-observer worlds.
        var method = typeof(SharedTrackerRegistry).GetMethod(
            "GetOrCreateTracker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        method.MakeGenericMethod(runtimeType).Invoke(_world.GetOrCreateSharedTrackers(), new object[] { _world.EntityCapacity });
    }

    /// <summary>
    /// Adds a required component to the filter.
    /// </summary>
    public ChangeQuery With<TU>() where TU : unmanaged
    {
        EnsureUsable();
        if (_consumed)
        {
            ThrowFilterConsumed();
        }
        _filter = _filter.With<TU>();
        _cache = null;
        _hasFilter = true;
        RefreshTransitionRegistration();
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
        RefreshTransitionRegistration();
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
        RefreshTransitionRegistration();
        return this;
    }

    /// <summary>
    /// Returns all structural transitions (create/destroy/add/remove) since the
    /// last call. The internal buffer is auto-cleared after enumeration.
    /// </summary>
    public IEnumerable<Transition> Transitions()
    {
        EnsureUsable();
        if (IsValueOnlyTypedObserver)
        {
            _consumed = true;
            return Array.Empty<Transition>();
        }

        _consumed = true;
        var result = _transitions.ToArray();
        _transitions.Clear();
        return result;
    }

    /// <summary>
    /// Returns old/new value change pairs for single Capture&lt;T&gt; + Previous.
    /// Pairs come from the world-owned shared tracker's double-buffered
    /// <see cref="TypedChange{T}"/>[] — zero copy, no construction overhead.
    /// Non-destructive: repeated calls return the same data until
    /// <see cref="ClearChanges{T}"/> is called.
    /// Symmetric to <see cref="Transitions"/> (which covers structural changes).
    /// </summary>
    public ReadOnlySpan<TypedChange<T>> ValueChanges<T>() where T : unmanaged
    {
        EnsureUsable();
        _consumed = true;
        if (!_hasPrevious || _hasFilter || _capturedTypes.Count != 1)
            return ReadOnlySpan<TypedChange<T>>.Empty;
        // Only return data when T exactly matches the single captured component type.
        if (_capturedTypes[0] != Component<T>.ComponentType)
            return ReadOnlySpan<TypedChange<T>>.Empty;
        var registry = _world.SharedTrackers;
        if (registry is null)
            return ReadOnlySpan<TypedChange<T>>.Empty;
        var tracker = registry.GetTracker<T>();
        if (tracker is null)
            return ReadOnlySpan<TypedChange<T>>.Empty;
        return tracker.Read();
    }

    /// <summary>
    /// Clears all tracked value changes for component type <typeparamref name="T"/>.
    /// This is a global clear for the component type: all queries sharing the same
    /// tracker will see empty changes after this call.
    /// </summary>
    public void ClearChanges<T>() where T : unmanaged
    {
        EnsureUsable();
        // Scoped: only acts when T matches the query's single captured type + Previous + no filter.
        // World.ClearChanges<T> remains global (no scoping check).
        if (!_hasPrevious || _hasFilter || _capturedTypes.Count != 1)
            return;
        if (_capturedTypes[0] != Component<T>.ComponentType)
            return;
        var registry = _world.SharedTrackers;
        if (registry is null) return;
        var tracker = registry.GetTracker<T>();
        tracker?.Clear();
    }

    // ── IChangeQuery ──

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
        }
    }

    private static void ThrowFilterConsumed()
    {
        throw new InvalidOperationException(
            "Cannot modify the filter after enumeration has started. " +
            "Configure With/Without/WithAny before the first enumeration.");
    }
}
