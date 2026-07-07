namespace MiniArch;

/// <summary>Indicates whether an entity entered or exited a tracked set.</summary>
/// <example>
/// <code>
/// foreach (var t in hp.Transitions())
///     if (t.Kind == TransitionKind.Entered)
///         SpawnHealthBar(t.Entity);
///     else
///         DestroyHealthBar(t.Entity);
/// </code>
/// </example>
public enum TransitionKind
{
    /// <summary>Entity entered the tracked set.</summary>
    Entered,
    /// <summary>Entity exited the tracked set.</summary>
    Exited,
}

/// <summary>
/// Indicates what structural change caused the <see cref="Transition"/>.
/// </summary>
/// <example>
/// <code>
/// foreach (var t in hp.Transitions())
///     if (t.Kind == TransitionKind.Exited &amp;&amp; t.Cause == TransitionCause.Destroyed)
///         DestroyHealthBar(t.Entity);   // entity gone, clean up completely
///     else if (t.Kind == TransitionKind.Exited)
///         HideHealthBar(t.Entity);      // still exists (e.g. Add&lt;Dead&gt;), might come back
/// </code>
/// </example>
public enum TransitionCause
{
    /// <summary>Entity was created with matching components.</summary>
    Created,
    /// <summary>Entity was destroyed while matching the filter.</summary>
    Destroyed,
    /// <summary>A component was added that caused entry (or an excluded component was removed — only when filter uses <c>Without&lt;&gt;</c>).</summary>
    Added,
    /// <summary>A component was removed that caused exit (or an excluded component was added — only when filter uses <c>Without&lt;&gt;</c>).</summary>
    Removed,
}

/// <summary>
/// A single membership change: an entity either entered or exited the set defined
/// by the change-query's filter since the last call to <see cref="ChangeQuery{T}.Transitions"/>.
/// <see cref="Kind"/> is derived from <see cref="Cause"/> — use <see cref="Cause"/> for precision
/// (e.g. distinguish <see cref="TransitionCause.Destroyed"/> from <see cref="TransitionCause.Removed"/>),
/// or <see cref="Kind"/> for simple enter/exit checks.
/// </summary>
/// <example>
/// <code>
/// foreach (var t in hp.Transitions())
///     if (t.Cause == TransitionCause.Destroyed)
///         CleanUp(t.Entity);          // entity gone
///     else if (t.IsExited)
///         HideHealthBar(t.Entity);    // still alive, just left set
///     else
///         ShowHealthBar(t.Entity);    // entered (Created or Added)
/// </code>
/// </example>
public readonly struct Transition
{
    /// <summary>What structural change caused this transition (authoritative field).</summary>
    public readonly TransitionCause Cause;

    /// <summary>Derived convenience — true for <see cref="TransitionCause.Created"/> or <see cref="TransitionCause.Added"/>.</summary>
    public bool IsEntered => Cause is TransitionCause.Created or TransitionCause.Added;

    /// <summary>Derived convenience — true for <see cref="TransitionCause.Destroyed"/> or <see cref="TransitionCause.Removed"/>.</summary>
    public bool IsExited => Cause is TransitionCause.Destroyed or TransitionCause.Removed;

    /// <summary>Derived convenience — <see cref="TransitionKind.Entered"/> or <see cref="TransitionKind.Exited"/>.</summary>
    public TransitionKind Kind => IsEntered ? TransitionKind.Entered : TransitionKind.Exited;

    /// <summary>The entity whose membership changed.</summary>
    public readonly Entity Entity;

    /// <summary>Creates a transition entry.</summary>
    public Transition(TransitionCause cause, Entity entity)
    {
        Cause = cause;
        Entity = entity;
    }
}
