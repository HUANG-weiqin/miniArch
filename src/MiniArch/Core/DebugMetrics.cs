namespace MiniArch
{
    /// <summary>
    /// Snapshot of debug-only world metrics.
    /// </summary>
    public readonly record struct WorldDebugMetrics(
        bool IsEnabled,
        int EntityCapacityGrowCount,
        int DestroyScratchGrowCount,
        int EntityCapacity,
        int MaxEntityCapacity,
        int LastEntityCapacityBefore,
        int LastEntityCapacityAfter,
        int EntitySlotCount,
        int DestroyOrderScratchCapacity,
        int DestroyVisitedScratchCapacity,
        int LastDestroyOrderScratchCapacityBefore,
        int LastDestroyOrderScratchCapacityAfter,
        int LastDestroyVisitedScratchCapacityBefore,
        int LastDestroyVisitedScratchCapacityAfter);
}

namespace MiniArch.Core
{
    /// <summary>
    /// Snapshot of debug-only command buffer metrics.
    /// </summary>
    public readonly record struct CommandBufferDebugMetrics(
        bool IsEnabled,
        int RecordedSetCount,
        int RecordedCreateCount,
        int RecordedAddCount,
        int RecordedRemoveCount,
        int RecordedDestroyCount,
        int OpsPoolGrowCount,
        int OpsLookupGrowCount,
        int CreatedStatePoolGrowCount,
        int CreatedStateLookupGrowCount,
        int ExistingDestroyGrowCount,
        int SlabRentCount,
        long SlabRentBytes,
        int SnapshotDeepCopyCount,
        long SnapshotDeepCopyBytes,
        int OpsPoolCapacity,
        int OpsLookupCapacity,
        int CreatedStatePoolCapacity,
        int CreatedStateLookupCapacity,
        int ExistingDestroyCapacity,
        int SlabCount,
        int CurrentSlabOffset);
}
