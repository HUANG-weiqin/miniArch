namespace MiniArch.Core;

/// <summary>
/// Entity location metadata kept on the stack during structural-change code paths.
/// </summary>
internal readonly record struct EntityLocation(Archetype Archetype, int RowIndex);
