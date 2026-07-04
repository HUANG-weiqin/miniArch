namespace MiniArch.Core;

/// <summary>
/// Internal mutable state shared between all copies of an <see cref="EntitySlot"/>.
/// One instance is allocated per <see cref="CommandStream.Track"/> call on a placeholder entity.
/// </summary>
internal sealed class Slot
{
    /// <summary>The current entity value: placeholder before resolution, real after.</summary>
    internal Entity Entity;

    /// <summary>Linked-list pointer for registration in <c>_trackedBySeq</c>. Nulled after resolution.</summary>
    internal Slot? Next;
}
