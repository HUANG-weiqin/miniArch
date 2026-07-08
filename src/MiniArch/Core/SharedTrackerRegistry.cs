using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// World-owned registry of ChangeTracker&lt;T&gt; instances.
/// One tracker per component type; shared by all ChangeQuery consumers.
/// Uses Component&lt;T&gt;.ComponentType.Value as a dense index.
/// Non-thread-safe (matches World's threading model).
/// </summary>
internal sealed class SharedTrackerRegistry
{
    // Growable array indexed by componentType.Value.
    // null = no tracker for that type.
    private IChangeTrackerControl?[] _trackers = new IChangeTrackerControl?[32];

    // Fast-path: true when at least one tracker exists. Avoids the array
    // access and type lookup in GetTracker<T> when no tracking is active.
    internal bool _hasTrackers;

    /// <summary>
    /// Read-only check for whether any tracker exists at all.
    /// When false, no component type has a tracker, so callers can
    /// skip the generic GetTracker&lt;T&gt;() call entirely.
    /// </summary>
    internal bool HasAnyTrackers => _hasTrackers;

    // ── Single tracker API ──────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChangeTracker<T>? GetTracker<T>() where T : unmanaged
    {
        if (!_hasTrackers) return null;
        var typeId = Component<T>.ComponentType.Value;
        if ((uint)typeId < (uint)_trackers.Length)
        {
            var tracker = _trackers[typeId];
            if (tracker is not null)
                return Unsafe.As<ChangeTracker<T>>(tracker);
        }
        return null;
    }

    /// <summary>
    /// Gets or creates the shared <see cref="ChangeTracker{T}"/> for component type T.
    /// When <paramref name="entityCapacity"/> is positive, the tracker is pre-sized
    /// to handle entity IDs up to <c>entityCapacity - 1</c>, eliminating the initial
    /// growth allocations on first Set.
    /// </summary>
    internal ChangeTracker<T> GetOrCreateTracker<T>(int entityCapacity = -1) where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        if ((uint)typeId >= (uint)_trackers.Length)
            Array.Resize(ref _trackers, Math.Max(typeId + 1, _trackers.Length * 2));

        if (_trackers[typeId] is ChangeTracker<T> existing)
        {
            if (entityCapacity > 0)
                existing.PreSize(entityCapacity - 1);
            _hasTrackers = true;
            return existing;
        }

        var tracker = new ChangeTracker<T>();
        _trackers[typeId] = tracker;
        _hasTrackers = true;

        // Pre-size to avoid initial growth on first Set
        if (entityCapacity > 0)
            tracker.PreSize(entityCapacity - 1);

        return tracker;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasTracker(int typeId)
    {
        return (uint)typeId < (uint)_trackers.Length && _trackers[typeId] is not null;
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Clears all trackers. Called on Dispose and RestoreState.
    /// </summary>
    internal void Clear()
    {
        Array.Clear(_trackers, 0, _trackers.Length);
        _hasTrackers = false;
    }

    /// <summary>
    /// Clears pending value-change logs but keeps tracker registrations alive.
    /// Used by RestoreState so existing queries keep observing mutations that
    /// happen immediately after rollback.
    /// </summary>
    internal void ClearChanges()
    {
        for (var i = 0; i < _trackers.Length; i++)
            _trackers[i]?.Clear();
    }

    internal void ClearSlot(int entityId, int typeId)
    {
        if ((uint)typeId < (uint)_trackers.Length && _trackers[typeId] is { } tracker)
            tracker.ClearSlot(entityId);
    }

    internal void ClearAllSlots(int entityId)
    {
        for (var i = 0; i < _trackers.Length; i++)
            _trackers[i]?.ClearSlot(entityId);
    }
}
