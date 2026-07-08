using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

internal interface IChangeTrackerControl
{
    ComponentType ComponentType { get; }
    void ClearSlot(int entityId);
    unsafe void CaptureBaselineRaw(Entity entity, byte* source);
    void Clear();
}

/// <summary>
/// Typed boundary-diff tracker for a single component type. It keeps a
/// per-entity baseline and scans the current world when changes are read.
/// This keeps Set&lt;T&gt; and CommandStream.Set&lt;T&gt; completely free of value
/// tracking work.
/// </summary>
internal sealed class ChangeTracker<T> : IChangeTrackerControl where T : unmanaged
{
    private static readonly QueryDescription Query = new QueryDescription().With<T>();

    private readonly World _world;

    // BaselineValues[entityId] = value at last ClearAll/arming/structural add.
    // BaselineVersions[entityId] == 0 means no baseline for that entity slot.
    internal T[] BaselineValues = [];
    internal int[] BaselineVersions = [];

    internal ValueChange<T>[] ChangesBuffer = [];
    internal int ChangeCount;

    internal ChangeTracker(World world)
    {
        _world = world;
    }

    ComponentType IChangeTrackerControl.ComponentType => Component<T>.ComponentType;

    void IChangeTrackerControl.ClearSlot(int entityId)
    {
        if ((uint)entityId < (uint)BaselineVersions.Length)
            BaselineVersions[entityId] = 0;

        ChangeCount = 0;
    }

    unsafe void IChangeTrackerControl.CaptureBaselineRaw(Entity entity, byte* source)
    {
        var value = Unsafe.ReadUnaligned<T>(source);
        CaptureBaseline(entity, in value);
    }

    void IChangeTrackerControl.Clear() => Clear();

    /// <summary>
    /// Pre-allocate all internal buffers to handle up to <paramref name="maxEntityId"/>
    /// entities.
    /// </summary>
    internal void PreSize(int maxEntityId)
    {
        if (maxEntityId < 0)
            return;

        var size = Math.Max(maxEntityId + 1, 64);
        if (size <= BaselineVersions.Length)
            return;

        Array.Resize(ref BaselineValues, size);
        Array.Resize(ref BaselineVersions, size);
        Array.Resize(ref ChangesBuffer, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureEntityCapacity(int maxEntityId)
    {
        if (maxEntityId < BaselineVersions.Length)
            return;

        var newSize = Math.Max(maxEntityId + 1, BaselineVersions.Length == 0 ? 64 : BaselineVersions.Length * 2);
        Array.Resize(ref BaselineValues, newSize);
        Array.Resize(ref BaselineVersions, newSize);

        if (ChangesBuffer.Length < newSize)
            Array.Resize(ref ChangesBuffer, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureChangeCapacity(int slot)
    {
        if (slot < ChangesBuffer.Length) return;
        var newSize = Math.Max(slot + 1, ChangesBuffer.Length == 0 ? 64 : ChangesBuffer.Length * 2);
        Array.Resize(ref ChangesBuffer, newSize);
    }

    /// <summary>
    /// Captures the current value as baseline without recording a change.
    /// Used by structural creation/addition paths so a later value mutation can
    /// diff against the component's initial value without touching Set&lt;T&gt;.
    /// </summary>
    internal void CaptureBaseline(Entity entity, in T value)
    {
        EnsureEntityCapacity(entity.Id);
        BaselineValues[entity.Id] = value;
        BaselineVersions[entity.Id] = entity.Version;
        ChangeCount = 0;
    }

    /// <summary>
    /// Non-destructive read-only view of changes from baseline to current world state.
    /// </summary>
    internal ReadOnlySpan<ValueChange<T>> Read()
    {
        RebuildChanges();
        var count = ChangeCount;
        if (count == 0) return ReadOnlySpan<ValueChange<T>>.Empty;
        return new ReadOnlySpan<ValueChange<T>>(ChangesBuffer, 0, count);
    }

    /// <summary>
    /// Moves the baseline to the current world state.
    /// </summary>
    internal void Clear()
    {
        CaptureAllBaselines();
        ChangeCount = 0;
    }

    internal void CaptureAllBaselines()
    {
        if (BaselineVersions.Length > 0)
            Array.Clear(BaselineVersions, 0, BaselineVersions.Length);

        foreach (var chunk in _world.Query(in Query).GetChunks())
        {
            var values = chunk.GetSpan<T>();
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
                CaptureBaseline(entities[i], in values[i]);
        }
    }

    private void RebuildChanges()
    {
        ChangeCount = 0;
        foreach (var chunk in _world.Query(in Query).GetChunks())
        {
            var values = chunk.GetSpan<T>();
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                var entity = entities[i];
                ref var value = ref values[i];
                EnsureEntityCapacity(entity.Id);

                if (BaselineVersions[entity.Id] != entity.Version)
                {
                    BaselineValues[entity.Id] = value;
                    BaselineVersions[entity.Id] = entity.Version;
                    continue;
                }

                var oldValue = BaselineValues[entity.Id];
                if (EqualityComparer<T>.Default.Equals(oldValue, value))
                    continue;

                var slot = ChangeCount;
                EnsureChangeCapacity(slot);
                ChangesBuffer[slot] = new ValueChange<T>(entity, oldValue, value);
                ChangeCount++;
            }
        }
    }
}
