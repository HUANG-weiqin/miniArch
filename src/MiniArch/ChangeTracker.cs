using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

internal interface IChangeTrackerControl
{
    ComponentType ComponentType { get; }

    void ClearSlot(int entityId);
}

/// <summary>
/// Typed change tracker for a single component type. Uses double-buffered
/// <see cref="TypedChange{T}"/>[] as the write log:
///   - <see cref="ActiveLog"/> is the current-tick append log (slot-indexed).
///   - <see cref="SpareLog"/> receives the drained buffer on swap.
///   - <see cref="SlotByEntityPlusOne"/>[entity.Id] — 0 means clean,
///     (slot+1) means dirty with that slot; merges IsDirty/SlotByEntity into
///     one int[] to halve random writes on the Set hot path.
///   - Pre-sized to entity capacity at creation; no runtime allocation in steady state.
/// </summary>
internal sealed class ChangeTracker<T> : IChangeTrackerControl where T : unmanaged
{
    // ── Double-buffered TypedChange<T> logs ──
    // ActiveLog: written into during current tick; swapped to SpareLog on drain.
    internal TypedChange<T>[] ActiveLog = [];
    internal TypedChange<T>[] SpareLog = [];
    internal int DirtyCount;

    // ── Merged entity-indexed array ──
    // 0 = clean; slot+1 = dirty and the ActiveLog slot.
    internal int[] SlotByEntityPlusOne = [];

    ComponentType IChangeTrackerControl.ComponentType => Component<T>.ComponentType;

    void IChangeTrackerControl.ClearSlot(int entityId)
    {
        if ((uint)entityId < (uint)SlotByEntityPlusOne.Length)
            SlotByEntityPlusOne[entityId] = 0;
    }

    /// <summary>
    /// Pre-allocate all internal buffers to handle up to <paramref name="maxEntityId"/>
    /// entities, eliminating runtime allocations during steady-state operation.
    /// </summary>
    internal void PreSize(int maxEntityId)
    {
        if (maxEntityId < 0)
            return;

        var size = Math.Max(maxEntityId + 1, 64);
        if (size <= SlotByEntityPlusOne.Length)
            return;

        Array.Resize(ref SlotByEntityPlusOne, size);
        Array.Resize(ref ActiveLog, size);
        Array.Resize(ref SpareLog, size);
    }

    internal void EnsureEntityCapacity(int maxEntityId)
    {
        if (maxEntityId < SlotByEntityPlusOne.Length)
            return;

        var newSize = Math.Max(maxEntityId + 1, SlotByEntityPlusOne.Length == 0 ? 64 : SlotByEntityPlusOne.Length * 2);
        Array.Resize(ref SlotByEntityPlusOne, newSize);

        // SpareLog may still back the span returned by the previous drain.
        // Grow the active writer only; the spare buffer can catch up after the next swap.
        if (ActiveLog.Length < newSize)
            Array.Resize(ref ActiveLog, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureLogCapacity(int slot)
    {
        if (slot < ActiveLog.Length) return;
        var newSize = Math.Max(slot + 1, ActiveLog.Length == 0 ? 64 : ActiveLog.Length * 2);
        Array.Resize(ref ActiveLog, newSize);
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
