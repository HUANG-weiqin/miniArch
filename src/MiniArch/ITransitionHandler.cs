namespace MiniArch;

/// <summary>
/// Indicates whether an entity entered or exited the tracked set.
/// </summary>
public enum TransitionKind
{
    /// <summary>Entity entered the tracked set.</summary>
    Entered,
    /// <summary>Entity exited the tracked set.</summary>
    Exited,
}

/// <summary>
/// Handles structural transitions (entities entering/exiting a query filter).
/// Used by <see cref="TransitionWatch{THandler}"/>.
/// </summary>
public interface ITransitionHandler
{
    /// <summary>
    /// Called for each entity whose membership in the tracked set changed since the snapshot.
    /// </summary>
    void OnChange(World world, Entity entity, TransitionKind kind);
}
