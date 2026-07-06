namespace MiniArch;

/// <summary>Indicates whether an entity entered or exited a tracked set.</summary>
/// <example>
/// <code>
/// foreach (var t in hp.Transitions())
///     if (t.Kind == TransitionKind.Entered)
///         SpawnHealthBar(t.Entity);   // entity now has HP (and matches filter)
///     else
///         DestroyHealthBar(t.Entity); // entity lost HP or gained an excluded component
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

    /// <summary>The entity whose membership changed.</summary>
    public readonly Entity Entity;

    /// <summary>Creates a transition entry.</summary>
    public Transition(TransitionKind kind, Entity entity)
    {
        Kind = kind;
        Entity = entity;
    }
}
