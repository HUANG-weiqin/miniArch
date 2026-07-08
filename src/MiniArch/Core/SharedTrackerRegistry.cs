using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// World-owned registry of ChangeTracker&lt;T&gt; instances.
/// One tracker per component type; shared by all consumers.
/// Uses Component&lt;T&gt;.ComponentType.Value as a dense index.
/// Non-thread-safe (matches World's threading model).
/// </summary>
internal sealed class SharedTrackerRegistry
{
    private readonly World _world;

    // Growable array indexed by componentType.Value.
    // null = no tracker for that type.
    private IChangeTrackerControl?[] _trackers = new IChangeTrackerControl?[32];

    internal IChangeTrackerControl?[] RawArray => _trackers;

    internal SharedTrackerRegistry(World world)
    {
        _world = world;
    }

    /// <summary>
    /// Returns true if any tracker exists for the given type id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasTracker(int typeId)
    {
        return (uint)typeId < (uint)_trackers.Length && _trackers[typeId] is not null;
    }

    /// <summary>
    /// Gets the typed tracker for T, or null if no tracker exists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChangeTracker<T>? GetTracker<T>() where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        if ((uint)typeId < (uint)_trackers.Length)
            return _trackers[typeId] as ChangeTracker<T>;
        return null;
    }

    /// <summary>
    /// Gets or creates a tracker for T.
    /// </summary>
    internal ChangeTracker<T> GetOrCreateTracker<T>(out bool created) where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        if ((uint)typeId >= (uint)_trackers.Length)
            Array.Resize(ref _trackers, Math.Max(typeId + 1, _trackers.Length * 2));

        if (_trackers[typeId] is ChangeTracker<T> existing)
        {
            created = false;
            return existing;
        }

        var tracker = new ChangeTracker<T>(_world);
        _trackers[typeId] = tracker;
        created = true;
        return tracker;
    }

    internal ChangeTracker<T> GetOrCreateTracker<T>() where T : unmanaged
        => GetOrCreateTracker<T>(out _);

    /// <summary>
    /// Resets all trackers by moving baselines to current world state.
    /// Keeps trackers alive for post-restore mutations. Used by RestoreState.
    /// </summary>
    internal void ResetAll()
    {
        for (var i = 0; i < _trackers.Length; i++)
            _trackers[i]?.Clear();
    }

    /// <summary>
    /// Clears all tracker references (used by Dispose).
    /// </summary>
    internal void Clear()
    {
        Array.Clear(_trackers, 0, _trackers.Length);
    }

    /// <summary>
    /// Clears the slot for entityId in the tracker for the given component type.
    /// </summary>
    internal void ClearSlot(int entityId, int typeId)
    {
        if ((uint)typeId < (uint)_trackers.Length && _trackers[typeId] is { } tracker)
            tracker.ClearSlot(entityId);
    }

    /// <summary>
    /// Captures a raw component value as the baseline for an existing tracker.
    /// </summary>
    internal unsafe void CaptureBaselineRaw(Entity entity, int typeId, byte* source)
    {
        if ((uint)typeId < (uint)_trackers.Length && _trackers[typeId] is { } tracker)
            tracker.CaptureBaselineRaw(entity, source);
    }

    /// <summary>
    /// Clears the slot for entityId in all trackers.
    /// </summary>
    internal void ClearAllSlots(int entityId)
    {
        for (var i = 0; i < _trackers.Length; i++)
            _trackers[i]?.ClearSlot(entityId);
    }
}
