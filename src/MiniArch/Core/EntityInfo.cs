namespace MiniArch.Core;

/// <summary>
/// Entity location metadata.
/// </summary>
/// <param name="Version">The entity version.</param>
/// <param name="Archetype">The owning archetype.</param>
/// <param name="ChunkIndex">The chunk index.</param>
/// <param name="RowIndex">The row index.</param>
public readonly record struct EntityInfo(int Version, Archetype Archetype, int ChunkIndex, int RowIndex);
