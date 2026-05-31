using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class DebugMetricsTests
{
    private readonly record struct C1(int Value);
    private readonly record struct C2(int Value);
    private readonly record struct C3(int Value);
    private readonly record struct C4(int Value);
    private readonly record struct C5(int Value);

    [Fact]
    public void CommandBuffer_debug_report_identifies_overflow_grow_slab_and_snapshot_pressure()
    {
        var world = new World(entityCapacity: 0);
        var buffer = new CommandBuffer(world);
        var existing = new Entity[70];
        world.CreateMany(existing);

        for (var i = 0; i < existing.Length; i++)
        {
            buffer.Set(existing[i], new C1(i));
        }

        var created = buffer.Create();
        buffer.Add(created, new C1(1));
        buffer.Add(created, new C2(2));
        buffer.Add(created, new C3(3));
        buffer.Add(created, new C4(4));
        buffer.Add(created, new C5(5));

        _ = buffer.Snapshot();

        var metrics = buffer.GetDebugMetrics();
        var report = buffer.GetDebugReport();

#if DEBUG
        Assert.True(metrics.IsEnabled);
        Assert.Equal(70, metrics.RecordedSetCount);
        Assert.Equal(1, metrics.RecordedCreateCount);
        Assert.Equal(5, metrics.RecordedAddCount);
        Assert.True(metrics.CreatedInlineOverflowAllocationCount >= 1);
        Assert.True(metrics.OpsPoolGrowCount >= 2);
        Assert.True(metrics.OpsLookupGrowCount >= 2);
        Assert.True(metrics.SlabRentCount >= 1);
        Assert.True(metrics.SnapshotDeepCopyBytes > 0);
        Assert.Contains("MiniArch CommandBuffer Debug Metrics", report);
        Assert.Contains("created_inline_overflow_allocations", report);
        Assert.Contains("ops_pool_grow_count", report);
        Assert.Contains("slab_rent_count", report);
        Assert.Contains("snapshot_deep_copy_bytes", report);
#else
        Assert.False(metrics.IsEnabled);
        Assert.Contains("disabled", report);
#endif
    }

    [Fact]
    public void World_debug_report_identifies_entity_metadata_and_destroy_scratch_growth()
    {
        var world = new World(entityCapacity: 0);

        world.EnsureCapacity(32);

        var metrics = world.GetDebugMetrics();
        var report = world.GetDebugReport();

#if DEBUG
        Assert.True(metrics.IsEnabled);
        Assert.True(metrics.EntityCapacityGrowCount >= 1);
        Assert.True(metrics.DestroyScratchGrowCount >= 1);
        Assert.Equal(32, metrics.EntityCapacity);
        Assert.Equal(32, metrics.MaxEntityCapacity);
        Assert.Contains("MiniArch World Debug Metrics", report);
        Assert.Contains("entity_capacity_grow_count", report);
        Assert.Contains("destroy_scratch_grow_count", report);
        Assert.Contains("last_entity_capacity", report);
#else
        Assert.False(metrics.IsEnabled);
        Assert.Contains("disabled", report);
#endif
    }
}
