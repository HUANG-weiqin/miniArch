namespace MiniArch;

/// <summary>
/// Runtime entity handle.
/// </summary>
/// <param name="Id">The entity slot id.</param>
/// <param name="Version">The entity version.</param>
/// <remarks>
/// Entity has no World association. Using an Entity from one World in
/// another World's APIs is undefined behavior —if the slot+version
/// coincidentally match, operations silently affect the wrong entity.
/// </remarks>
public readonly record struct Entity(int Id, int Version) : IComparable<Entity>
{
    /// <summary>
    /// Gets whether the handle has a non-default shape (Id >= 0 and Version > 0).
    /// This does NOT check whether the entity is alive in any world.
    /// Use <c>world.IsAlive(entity)</c> for liveness checks.
    /// </summary>
    public bool IsValid => Id >= 0 && Version > 0;

    /// <summary>
    /// Whether this entity is a lockstep placeholder (deferred create).
    /// Placeholders have Id == -1 and Version >= 0 (the version is the seq number).
    /// Distinguished from <see cref="IsUnmappedSentinel"/> by the Version sign.
    /// </summary>
    public bool IsPlaceholder => Id == -1 && Version >= 0;

    /// <summary>
    /// Whether this entity is an unmapped sentinel (unoccupied slot in the
    /// placeholder-to-local mapping table). Sentinels have Id == -1 and Version == -1.
    /// </summary>
    internal bool IsUnmappedSentinel => Id == -1 && Version < 0;

    /// <summary>
    /// Returns a compact display string.
    /// </summary>
    public override string ToString() => $"Entity({Id}, v{Version})";

    /// <summary>
    /// Compares by Id then Version. Used for sorted deduplication.
    /// </summary>
    public int CompareTo(Entity other)
    {
        var idComparison = Id.CompareTo(other.Id);
        return idComparison != 0 ? idComparison : Version.CompareTo(other.Version);
    }
}
