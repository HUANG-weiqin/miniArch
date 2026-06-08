namespace MiniArch.Core;

/// <summary>
/// Entity location metadata returned by <see cref="MiniArch.World.TryGetLocation"/>.
/// </summary>
public readonly struct EntityInfo
{
    /// <summary>
    /// Gets the entity version.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// Gets the owning archetype.
    /// </summary>
    internal Archetype Archetype { get; }

    /// <summary>
    /// Gets the row index within the archetype storage.
    /// </summary>
    public int RowIndex { get; }

    internal EntityInfo(int version, Archetype? archetype, int rowIndex)
    {
        Version = version;
        Archetype = archetype!;
        RowIndex = rowIndex;
    }
}
