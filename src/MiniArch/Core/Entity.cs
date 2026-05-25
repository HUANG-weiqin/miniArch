namespace MiniArch;

/// <summary>
/// Runtime entity handle.
/// </summary>
/// <param name="Id">The entity slot id.</param>
/// <param name="Version">The entity version.</param>
public readonly record struct Entity(int Id, int Version)
{
    /// <summary>
    /// Gets whether the handle has a non-default shape (Id >= 0 and Version > 0).
    /// This does NOT check whether the entity is alive in any world.
    /// Use <c>world.IsAlive(entity)</c> for liveness checks.
    /// </summary>
    public bool IsValid => Id >= 0 && Version > 0;

    /// <summary>
    /// Gets whether the version matches.
    /// </summary>
    public bool MatchesVersion(int version) => Version == version;

    /// <summary>
    /// Returns a compact display string.
    /// </summary>
    public override string ToString() => $"Entity({Id}, v{Version})";
}
