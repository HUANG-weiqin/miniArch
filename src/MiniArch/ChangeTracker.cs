using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// Typed change tracker for a single component type. Uses double-buffered
/// <see cref="TypedChange{T}"/>[] as the write log:
///   - <see cref="ActiveLog"/> is the current-tick append log (slot-indexed).
///   - <see cref="SpareLog"/> receives the drained buffer on swap.
///   - <see cref="SlotByEntityPlusOne"/>[entity.Id] — 0 means clean,
///     (slot+1) means dirty with that slot; merges IsDirty/SlotByEntity into
///     one int[] to halve random writes on the Set hot path.
/// </summary>
internal sealed class ChangeTracker<T> where T : unmanaged
{
    // ── Double-buffered TypedChange<T> logs ──
    // ActiveLog: written into during current tick; swapped to SpareLog on drain.
    internal TypedChange<T>[] ActiveLog = [];
    internal TypedChange<T>[] SpareLog = [];
    internal int DirtyCount;

    // ── Merged entity-indexed array ──
    // 0 = clean; slot+1 = dirty and the ActiveLog slot.
    internal int[] SlotByEntityPlusOne = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureCapacity(int id)
    {
        if (id < SlotByEntityPlusOne.Length) return;
        var newSize = Math.Max(id + 1, SlotByEntityPlusOne.Length == 0 ? 64 : SlotByEntityPlusOne.Length * 2);
        Array.Resize(ref SlotByEntityPlusOne, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureLogCapacity(int slot)
    {
        if (slot < ActiveLog.Length) return;
        var newSize = Math.Max(slot + 1, ActiveLog.Length == 0 ? 64 : ActiveLog.Length * 2);
        Array.Resize(ref ActiveLog, newSize);
        if (SpareLog.Length < newSize)
            Array.Resize(ref SpareLog, newSize);
    }

    /// <summary>
    /// Drain the current tick's changes: swap buffers, clear dirty flags,
    /// return a span over the completed log (valid until next drain).
    /// </summary>
    internal ReadOnlySpan<TypedChange<T>> Drain()
    {
        var count = DirtyCount;
        if (count == 0) return ReadOnlySpan<TypedChange<T>>.Empty;

        // Current write log becomes the drained result
        var drained = ActiveLog;

        // Swap: spare becomes active for next tick
        ActiveLog = SpareLog;
        SpareLog = drained;

        // Clear SlotByEntityPlusOne for drained entities
        for (var i = 0; i < count; i++)
            SlotByEntityPlusOne[drained[i].Entity.Id] = 0;

        DirtyCount = 0;
        return new ReadOnlySpan<TypedChange<T>>(drained, 0, count);
    }

    /// <summary>
    /// Reset dirty state for next tick (swap + clear, no return value).
    /// </summary>
    internal void Reset()
    {
        var count = DirtyCount;
        if (count == 0) return;

        var drained = ActiveLog;
        ActiveLog = SpareLog;
        SpareLog = drained;

        for (var i = 0; i < count; i++)
            SlotByEntityPlusOne[drained[i].Entity.Id] = 0;

        DirtyCount = 0;
    }

}
