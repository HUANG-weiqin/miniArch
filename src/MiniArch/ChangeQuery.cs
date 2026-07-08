using System.Collections.Generic;
using System.Reflection;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Multi-component change query. Obtained via <see cref="World.Track()"/>.
/// Tracks structural transitions (create/destroy/add/remove) and, when
/// <see cref="Previous"/> is enabled, captures old/new snapshots via
/// the typed fast path (single Capture&lt;T&gt; + Previous → ValueChanges&lt;T&gt;).
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

    // Typed fast path: when single Capture<T> + Previous, use typed arrays
    private IChangeTrackerControl? _typedTracker;
    private bool _transitionRegistered = true;

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
        _consumed = false;
        _worldGen = _world._trackingGeneration;
        _transitionRegistered = false;

        // Recreate typed fast-path state against the reset world.
        DeactivateTypedTracker();
        RefreshTypedTrackerActivation();
    }

    private bool IsValueOnlyTypedObserver =>
        _hasPrevious && !_hasFilter && _capturedTypes.Count == 1 && _typedTracker is not null;

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
        if (IsValueOnlyTypedObserver)
            DisableTransitionRegistration();
        else
            EnsureTransitionRegistration();
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

        if (_capturedTypes.Count == 1)
            TryActivateTypedTracker<T>();
        else
            DeactivateTypedTracker();

        RefreshTransitionRegistration();

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
        RefreshTypedTrackerActivation();

        return this;
    }

    private void RefreshTypedTrackerActivation()
    {
        if (_hasPrevious && !_hasFilter && _capturedTypes.Count == 1)
            ActivateTypedTrackerForCapturedType();
        else
            DeactivateTypedTracker();

        RefreshTransitionRegistration();
    }

    private void DeactivateTypedTracker()
    {
        if (_typedTracker is null) return;
        _world.RemoveTypedTracker(_typedTracker);
        _typedTracker = null;
    }

    private void ActivateTypedTrackerForCapturedType()
    {
        // Single captured type — activate typed tracker
        if (_typedTracker is not null) return;
        if (!_hasPrevious || _capturedTypes.Count != 1) return;
        if (_hasFilter) return;  // filters disable the typed fast path

        // Use the component type to create the right tracker
        var capturedType = _capturedTypes[0];
        if (!ComponentRegistry.Shared.TryGetType(capturedType, out var runtimeType))
            return; // type not registered, skip typed tracker

        // Use reflection to create ChangeTracker<T> and pre-size it
        var trackerType = typeof(ChangeTracker<>).MakeGenericType(runtimeType);
        var tracker = Activator.CreateInstance(trackerType);
        if (tracker is not IChangeTrackerControl typedTracker) return;

        // Pre-size to world capacity
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        trackerType.GetMethod("PreSize", flags)?.Invoke(tracker, [_world.EntityCapacity - 1]);

        _typedTracker = typedTracker;
        _world.AddTypedTracker(typedTracker);
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
        if (_hasFilter) return;  // filters disable the typed fast path

        // Create typed tracker and pre-size to world capacity
        var tracker = new ChangeTracker<T>();
        tracker.PreSize(_world.EntityCapacity - 1);

        _typedTracker = tracker;
        _world.AddTypedTracker(tracker);
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
        DeactivateTypedTracker();
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
        DeactivateTypedTracker();
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
        DeactivateTypedTracker();
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
    /// Pairs come from the tracker's double-buffered <see cref="TypedChange{T}"/>[]
    /// — zero copy, no construction overhead. Symmetric to <see cref="Transitions"/>
    /// (which covers structural changes).
    /// </summary>
    public ReadOnlySpan<TypedChange<T>> ValueChanges<T>() where T : unmanaged
    {
        EnsureUsable();
        _consumed = true;
        if (!_hasPrevious || _typedTracker is not ChangeTracker<T> tracker)
            return ReadOnlySpan<TypedChange<T>>.Empty;

        return tracker.Drain();
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
            "Cannot modify the filter after Transitions() has been called. " +
            "Configure With/Without/WithAny before the first enumeration.");
    }
}
