using System.Reflection;
using MiniArch.Core;
using MiniArch.Diagnostics;

namespace MiniArchTests.Core;

file readonly record struct MemoryToken(int Value);

public sealed class FreeListAndDestroyScratchTests
{
    [Fact]
    public void RepushFreeEntry_preserves_order_and_rejects_stale_generation()
    {
        using var world = new World();
        var first = world.CreateEmpty();
        var middle = world.CreateEmpty();
        var last = world.CreateEmpty();
        world.Destroy(first);
        world.Destroy(middle);
        world.Destroy(last);

        var reusedLast = world.CreateEmpty();
        var reusedMiddle = world.CreateEmpty();
        world.Destroy(reusedMiddle);
        world.Destroy(reusedLast);
        Assert.Equal(new[] { first.Id, middle.Id, last.Id }, world.FreeList.ToArray());

        world.RepushFreeEntry(middle.Id, middle.Version + 1);
        Assert.Equal(new[] { first.Id, middle.Id, last.Id }, world.FreeList.ToArray());

        world.RepushFreeEntry(middle.Id, reusedMiddle.Version + 1);
        Assert.Equal(new[] { first.Id, last.Id, middle.Id }, world.FreeList.ToArray());

        var newestMiddle = world.CreateEmpty();
        world.RepushFreeEntry(middle.Id, newestMiddle.Version);
        Assert.Equal(new[] { first.Id, last.Id }, world.FreeList.ToArray());
        Assert.Equal(new Entity(middle.Id, reusedMiddle.Version + 1), newestMiddle);
        Assert.Equal(last.Id, world.CreateEmpty().Id);
        Assert.Equal(first.Id, world.CreateEmpty().Id);
    }

    [Fact]
    public void FreeIds_preserve_clone_persistence_and_rollback_parity()
    {
        using var world = new World();
        var live = world.Create(new MemoryToken(10));
        var first = world.Create(new MemoryToken(20));
        var second = world.Create(new MemoryToken(30));
        world.Destroy(first);
        world.Destroy(second);
        var snapshot = world.CaptureState();

        using var clone = world.Clone();
        using var bytes = new MemoryStream();
        WorldSnapshot.Save(bytes, world);
        bytes.Position = 0;
        using var loaded = WorldSnapshot.Load(bytes);

        AssertParity(world, clone);
        AssertParity(world, loaded);
        world.Create(new MemoryToken(99));
        world.Destroy(live);
        world.RestoreState(snapshot);
        AssertParity(world, clone);
        AssertParity(world, loaded);

        var expected = world.CreateEmpty();
        Assert.Equal(expected, clone.CreateEmpty());
        Assert.Equal(expected, loaded.CreateEmpty());
    }

    [Fact]
    public void Diagnostics_observe_version_difference_with_identical_free_ids()
    {
        using var left = new World();
        using var right = new World();
        var leftEntity = left.CreateEmpty();
        var rightEntity = right.CreateEmpty();
        left.Destroy(leftEntity);
        right.Destroy(rightEntity);
        var reused = right.CreateEmpty();
        right.Destroy(reused);

        Assert.Equal(left.FreeList.ToArray(), right.FreeList.ToArray());
        var diff = WorldDiff.Compare(left, right);
        var slot = Assert.Single(diff.FreeListDiff!.SlotDiffs);
        Assert.Equal(FreeSlotDiffKind.VersionMismatch, slot.Kind);
        Assert.Equal(leftEntity.Id, slot.SlotIdA);
        Assert.Equal(leftEntity.Version + 1, slot.VersionA);
        Assert.Equal(reused.Version + 1, slot.VersionB);
        Assert.NotEqual(WorldDigest.Compute(left).FreeList, WorldDigest.Compute(right).FreeList);
    }

    [Fact]
    public void Query_destroy_does_not_initialize_batch_destroy_scratch()
    {
        using var world = new World(entityCapacity: 32);
        var description = new QueryDescription().With<MemoryToken>();
        for (var i = 0; i < 12; i++)
            world.Create(new MemoryToken(i));
        var survivor = world.CreateEmpty();

        world.Destroy(in description);

        Assert.Equal(1, world.EntityCount);
        Assert.True(world.IsAlive(survivor));
        Assert.Equal(0, ScratchLength(world, "_destroyVisitedGen"));
        Assert.Equal(0, ScratchLength(world, "_destroyGroupArchetypes"));
        Assert.Equal(0, DestroyOrderCapacity(world));
    }

    [Fact]
    public void Destroy_scratch_is_lazy_and_tracks_slots_separately_from_candidates()
    {
        using var world = new World(entityCapacity: 16_384);
        world.EnsureCapacity(32_768);
        Assert.Equal(0, ScratchLength(world, "_destroyVisitedGen"));
        Assert.Equal(0, ScratchLength(world, "_destroyRowMarks"));
        Assert.Equal(0, ScratchLength(world, "_destroyGroupArchetypes"));
        Assert.Equal(0, DestroyOrderCapacity(world));

        var entities = new Entity[16_384];
        for (var i = 0; i < entities.Length; i++)
            entities[i] = world.CreateEmpty();
        Assert.Equal(0, ScratchLength(world, "_destroyVisitedGen"));

        var targets = new Entity[10];
        for (var i = 0; i < 9; i++)
            targets[i] = entities[entities.Length - 1 - i];
        targets[9] = targets[0];
        world.Destroy(targets);

        Assert.Equal(9, world.FreeList.Length);
        Assert.True(ScratchLength(world, "_destroyVisitedGen") >= world.EntitySlotCount);
        Assert.Equal(0, ScratchLength(world, "_destroyRowMarks"));
        Assert.InRange(ScratchLength(world, "_destroyGroupArchetypes"), 1, 16);
        Assert.InRange(DestroyOrderCapacity(world), 10, 16);
        Assert.True(world.IsAlive(entities[0]));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void BUG_Destroy_batch_group_scratch_tracks_archetypes_and_grows_when_needed()
    {
        using var world = new World(entityCapacity: 128);
        var targets = new Entity[128];
        for (var i = 0; i < targets.Length; i++)
            targets[i] = world.Create(new MemoryToken(i));

        world.Destroy(targets);

        string[] groupScratch =
        [
            "_destroyGroupArchetypes", "_destroyGroupCounts",
            "_destroyGroupFirstCandidate", "_destroyFullGroups"
        ];
        foreach (var field in groupScratch)
            Assert.InRange(ScratchLength(world, field), 1, 16);
        Assert.True(ScratchLength(world, "_destroyCandidateNext") >= targets.Length);
        Assert.True(ScratchLength(world, "_destroyCandidateRows") >= targets.Length);
        Assert.True(ScratchLength(world, "_destroyCompactRows") >= targets.Length);

        const int groupCount = 20;
        var grouped = new Entity[groupCount];
        for (var i = 0; i < groupCount; i++)
        {
            var entity = world.CreateEmpty();
            if ((i & 1) != 0) world.Add(entity, new MemoryToken(i));
            if ((i & 2) != 0) world.Add(entity, i);
            if ((i & 4) != 0) world.Add(entity, (long)i);
            if ((i & 8) != 0) world.Add(entity, (short)i);
            if ((i & 16) != 0) world.Add(entity, (byte)i);
            grouped[i] = entity;
        }

        world.Destroy(grouped);

        foreach (var field in groupScratch)
            Assert.InRange(ScratchLength(world, field), groupCount, 32);
        Assert.Equal(0, world.EntityCount);
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Partial_dense_destroy_sizes_row_marks_to_archetype_rows()
    {
        using var world = new World(entityCapacity: 512);
        for (var i = 0; i < 128; i++)
            world.CreateEmpty();
        var tagged = new Entity[64];
        for (var i = 0; i < tagged.Length; i++)
            tagged[i] = world.Create(new MemoryToken(i));

        world.Destroy(tagged.AsSpan(0, 16));

        Assert.InRange(ScratchLength(world, "_destroyRowMarks"), tagged.Length, world.EntitySlotCount - 1);
        Assert.InRange(ScratchLength(world, "_destroyGroupArchetypes"), 16, 16);
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Warm_repeated_batch_destroy_and_create_does_not_allocate()
    {
        using var world = new World(entityCapacity: 64);
        var targets = new Entity[16];
        for (var i = 0; i < targets.Length; i++)
            targets[i] = world.Create(new MemoryToken(i));

        for (var repeat = 0; repeat < 32; repeat++)
            DestroyAndRecreate(world, targets);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repeat = 0; repeat < 64; repeat++)
            DestroyAndRecreate(world, targets);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void DestroyAndRecreate(World world, Entity[] targets)
    {
        world.Destroy(targets);
        for (var i = 0; i < targets.Length; i++)
            targets[i] = world.Create(new MemoryToken(i));
    }

    private static void AssertParity(World expected, World actual)
    {
        Assert.Equal(expected.FreeList.ToArray(), actual.FreeList.ToArray());
        Assert.True(WorldDiff.Compare(expected, actual).AreIdentical);
        Assert.Equal(expected.CanonicalChecksum(), actual.CanonicalChecksum());
        Assert.Equal(WorldDigest.Compute(expected).FreeList, WorldDigest.Compute(actual).FreeList);
    }

    private static int ScratchLength(World world, string name) =>
        ((Array)typeof(World).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(world)!).Length;

    private static int DestroyOrderCapacity(World world) =>
        ((List<Entity>)typeof(World).GetField("_destroyOrderScratch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(world)!).Capacity;
}
