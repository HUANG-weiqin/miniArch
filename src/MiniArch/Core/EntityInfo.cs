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
    public Archetype Archetype { get; }

    /// <summary>
    /// Gets the chunk directly. Null if the entity is not alive.
    /// </summary>
    public Chunk? Chunk { get; }

    /// <summary>
    /// Gets the row index within the chunk.
    /// </summary>
    public int RowIndex { get; }

    internal EntityInfo(int version, Archetype? archetype, Chunk? chunk, int rowIndex)
    {
        Version = version;
        Archetype = archetype!;
        Chunk = chunk;
        RowIndex = rowIndex;
    }
}
