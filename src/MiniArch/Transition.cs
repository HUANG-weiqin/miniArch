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
    /// <summary>A component was added that caused entry (or an excluded component was removed).</summary>
    Added,
    /// <summary>A component was removed that caused exit (or an excluded component was added).</summary>
    Removed,
}

/// <summary>
/// A single membership change: an entity either entered or exited the set defined
/// by the change-query's filter since the last call to <see cref="ChangeQuery{T}.Transitions"/>.
/// </summary>
/// <example>
/// <code>
/// foreach (var t in hp.Transitions())
///     if (t.Kind == TransitionKind.Entered)
///         OnHpAdded(t.Entity);
///     else
///         OnHpRemoved(t.Entity);
/// </code>
/// </example>
public readonly struct Transition
{
    /// <summary>Entered or Exited.</summary>
    public readonly TransitionKind Kind;

    /// <summary>What structural change caused this transition.</summary>
    public readonly TransitionCause Cause;

    /// <summary>The entity whose membership changed.</summary>
    public readonly Entity Entity;

    /// <summary>Creates a transition entry.</summary>
    public Transition(TransitionKind kind, TransitionCause cause, Entity entity)
    {
        Kind = kind;
        Cause = cause;
        Entity = entity;
    }
}
