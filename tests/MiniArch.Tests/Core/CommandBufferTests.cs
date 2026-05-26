using MiniArch.Core;
using MiniQuery = MiniArch.Core.Query;

namespace MiniArchTests.Core;

public sealed class CommandBufferTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Submit_applies_creates_and_components()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        buffer.Add(e, new Velocity(3, 4));

        Assert.False(world.IsAlive(e));
        buffer.Submit();
        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
    }

    [Fact]
    public void Submit_applies_set_and_remove_on_existing_entities()
    {
        var world = new World();
        var e = world.Create(new Position(10, 20), new Velocity(5, 6));
        var buffer = new CommandBuffer(world);

        buffer.Set(e, new Position(30, 40));
        buffer.Remove<Velocity>(e);

        buffer.Submit();
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(30, 40), p);
        Assert.False(world.TryGet<Velocity>(e, out _));
    }

    [Fact]
    public void Submit_handles_link_and_unlink()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        var buffer = new CommandBuffer(world);

        buffer.Link(parent, child);
        buffer.Submit();

        Assert.True(world.TryGetParent(child, out var p));
        Assert.Equal(parent, p);

        buffer.Unlink(child);
        buffer.Submit();
        Assert.False(world.TryGetParent(child, out _));
    }

    [Fact]
    public void Submit_returns_false_when_buffer_is_empty()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);
        Assert.False(buffer.Submit());
    }

    [Fact]
    public void Submit_destroy_removes_entity()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(e);
        Assert.True(world.IsAlive(e));
        buffer.Submit();
        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void Submit_destroy_uses_recorded_entity_version()
    {
        var world = new World();
        var stale = world.Create(new Position(1, 2));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(stale);
        world.Destroy(stale);
        var recycled = world.Create(new Position(3, 4));

        Assert.Equal(stale.Id, recycled.Id);
        Assert.NotEqual(stale.Version, recycled.Version);
        buffer.Submit();

        Assert.True(world.IsAlive(recycled));
        Assert.False(world.IsAlive(stale));
    }

    [Fact]
    public void Submit_destroy_does_not_skip_hierarchy_for_recycled_child()
    {
        var world = new World();
        var staleChild = world.Create();
        var buffer = new CommandBuffer(world);

        buffer.Destroy(staleChild);
        world.Destroy(staleChild);
        var recycledChild = world.Create();
        var parent = world.Create();
        buffer.Link(parent, recycledChild);

        Assert.Equal(staleChild.Id, recycledChild.Id);
        Assert.NotEqual(staleChild.Version, recycledChild.Version);
        buffer.Submit();

        Assert.True(world.TryGetParent(recycledChild, out var actualParent));
        Assert.Equal(parent, actualParent);
    }

    [Fact]
    public void Submit_clears_the_buffer_and_allows_reuse()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e1 = buffer.Create();
        buffer.Add(e1, new Position(1, 2));
        buffer.Submit();
        Assert.True(world.IsAlive(e1));

        var e2 = buffer.Create();
        buffer.Add(e2, new Position(3, 4));
        buffer.Submit();
        Assert.True(world.IsAlive(e2));
    }

    [Fact]
    public void Create_destroy_pairs_are_eliminated()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        buffer.Destroy(e);
        buffer.Submit();
        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void Snapshot_does_not_mutate_world()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        var delta = buffer.Snapshot();

        Assert.False(world.IsAlive(e));
        Assert.Single(delta.CreatedEntities);
    }

    [Fact]
    public void Snapshot_frame_can_be_replayed_into_another_world()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        var delta = buffer.Snapshot();

        var replica = new World();
        replica.Replay(delta);
        Assert.True(replica.IsAlive(e));
        Assert.True(replica.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
    }

    [Fact]
    public void Recording_structural_change_does_not_publish_layout_changes_before_submit()
    {
        var world = new World();
        var entity = world.Create();
        world.Components.GetOrCreate<Position>();
        var buffer = new CommandBuffer(world);
        var queryGenerationBefore = GetQueryGeneration(world);
        var archetypeCountBefore = GetArchetypeCount(world);

        buffer.Add(entity, new Position(9, 9));

        Assert.Equal(queryGenerationBefore, GetQueryGeneration(world));
        Assert.Equal(archetypeCountBefore, GetArchetypeCount(world));
    }

    [Fact]
    public void Submit_session_applies_all_commands_together()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var buffer = new CommandBuffer(world);

        var created = buffer.Create();
        buffer.Add(created, new Position(100, 200));
        buffer.Set(existing, new Position(10, 20));
        buffer.Submit();

        Assert.True(world.IsAlive(created));
        Assert.True(world.TryGet(created, out Position cp));
        Assert.Equal(new Position(100, 200), cp);
        Assert.True(world.TryGet(existing, out Position ep));
        Assert.Equal(new Position(10, 20), ep);
    }

    [Fact]
    public void Multiple_snapshots_from_different_buffers_can_be_merged()
    {
        var world = new World();

        var b1 = new CommandBuffer(world);
        var e1 = b1.Create();
        b1.Add(e1, new Position(1, 2));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        var e2 = b2.Create();
        b2.Add(e2, new Velocity(3, 4));
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);
        Assert.Equal(2, merged.CreatedEntities.Count);

        world.Replay(merged);
        Assert.True(world.IsAlive(e1));
        Assert.True(world.IsAlive(e2));
    }

    // FrameDelta.Merge tests

    [Fact]
    public void StaticMerge_two_empty_deltas_produces_empty()
    {
        var a = new FrameDelta();
        var b = new FrameDelta();
        var merged = FrameDelta.Merge(a, b);
        Assert.True(merged.IsEmpty);
    }

    [Fact]
    public void StaticMerge_then_replay_is_equivalent_to_sequential_replay()
    {
        var world = new World();
        var entity = world.Create(new Position(10, 20));

        var b1 = new CommandBuffer(world);
        b1.Set(entity, new Position(30, 40));
        b1.Add(entity, new Velocity(5, 6));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(entity, new Position(50, 60));
        b2.Remove<Velocity>(entity);
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        var mergedWorld = new World();
        var mergedEntity = mergedWorld.Create(new Position(10, 20));
        mergedWorld.Replay(merged);

        Assert.True(mergedWorld.TryGet(mergedEntity, out Position p));
        Assert.Equal(new Position(50, 60), p);
        Assert.False(mergedWorld.TryGet<Velocity>(mergedEntity, out _));
    }

    [Fact]
    public void StaticMerge_Set_then_Set_same_component_keeps_last()
    {
        var world = new World();
        var entity = world.Create(new Position(10, 20));

        var b1 = new CommandBuffer(world);
        b1.Set(entity, new Position(30, 40));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(entity, new Position(50, 60));
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(merged.AddCommands);
        Assert.Single(merged.SetCommands);
        Assert.Empty(merged.RemoveCommands);

        world.Replay(merged);
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(50, 60), p);
    }

    [Fact]
    public void StaticMerge_Add_then_Remove_same_component_cancels()
    {
        var world = new World();
        var entity = world.Create();

        var b1 = new CommandBuffer(world);
        b1.Add(entity, new Position(1, 2));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Remove<Position>(entity);
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(merged.AddCommands);
        Assert.Empty(merged.SetCommands);
        Assert.Empty(merged.RemoveCommands);

        world.Replay(merged);
        Assert.False(world.TryGet<Position>(entity, out _));
    }

    [Fact]
    public void StaticMerge_Set_then_Remove_keeps_Remove()
    {
        var world = new World();
        var entity = world.Create(new Position(10, 20));

        var b1 = new CommandBuffer(world);
        b1.Set(entity, new Position(30, 40));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Remove<Position>(entity);
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(merged.AddCommands);
        Assert.Empty(merged.SetCommands);
        Assert.Single(merged.RemoveCommands);

        world.Replay(merged);
        Assert.False(world.TryGet<Position>(entity, out _));
    }

    [Fact]
    public void StaticMerge_Remove_then_Add_becomes_Set()
    {
        var world = new World();
        var entity = world.Create(new Position(10, 20));

        var b1 = new CommandBuffer(world);
        b1.Remove<Position>(entity);
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Add(entity, new Position(30, 40));
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(merged.AddCommands);
        Assert.Single(merged.SetCommands);
        Assert.Empty(merged.RemoveCommands);

        world.Replay(merged);
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(30, 40), p);
    }

    [Fact]
    public void StaticMerge_Add_then_Set_same_component_keeps_Add_with_latest_data()
    {
        var world = new World();
        var entity = world.Create();

        var b1 = new CommandBuffer(world);
        b1.Add(entity, new Position(1, 2));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(entity, new Position(3, 4));
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Single(merged.AddCommands);
        Assert.Empty(merged.SetCommands);
        Assert.Empty(merged.RemoveCommands);

        world.Replay(merged);
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(3, 4), p);
    }

    [Fact]
    public void StaticMerge_Create_then_Destroy_becomes_Release()
    {
        var world = new World();
        var b1 = new CommandBuffer(world);
        var e = b1.Create();
        b1.Add(e, new Position(1, 2));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Destroy(e);
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(merged.CreatedEntities);
        Assert.Empty(merged.DestroyedEntities);
        Assert.Empty(merged.AddCommands);
        Assert.Single(merged.ReleasedEntities);

        world.Replay(merged);
        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void StaticMerge_Link_then_Unlink_same_child_keeps_Unlink()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();

        var b1 = new CommandBuffer(world);
        b1.Link(parent, child);
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Unlink(child);
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(merged.LinkCommands);
        Assert.Single(merged.UnlinkCommands);
    }

    [Fact]
    public void StaticMerge_folds_component_commands_into_CreatedEntity()
    {
        var world = new World();
        var b1 = new CommandBuffer(world);
        var e = b1.Create();
        b1.Add(e, new Position(1, 2));
        b1.Add(e, new Velocity(3, 4));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(e, new Position(5, 6));
        b2.Remove<Velocity>(e);
        b2.Add(e, new Health(100));
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Single(merged.CreatedEntities);
        Assert.Empty(merged.AddCommands);
        Assert.Empty(merged.SetCommands);
        Assert.Empty(merged.RemoveCommands);

        world.Replay(merged);
        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(5, 6), p);
        Assert.False(world.TryGet<Velocity>(e, out _));
        Assert.True(world.TryGet(e, out Health h));
        Assert.Equal(new Health(100), h);
    }

    [Fact]
    public void StaticMerge_multiple_entities_independent()
    {
        var world = new World();
        var e1 = world.Create(new Position(1, 1));
        var e2 = world.Create(new Position(2, 2));

        var b1 = new CommandBuffer(world);
        b1.Set(e1, new Position(10, 10));
        b1.Add(e2, new Velocity(5, 5));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(e1, new Position(20, 20));
        b2.Remove<Velocity>(e2);
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Single(merged.SetCommands);
        Assert.Empty(merged.AddCommands);
        Assert.Empty(merged.RemoveCommands);

        world.Replay(merged);
        Assert.True(world.TryGet(e1, out Position p1));
        Assert.Equal(new Position(20, 20), p1);
        Assert.False(world.TryGet<Velocity>(e2, out _));
    }

    [Fact]
    public void DeltaCount_returns_sum_of_all_lists()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        cb.Add(e, new Position(1, 2));
        cb.Add(e, new Velocity(3, 4));
        var delta = cb.Snapshot();

        Assert.Equal(2, delta.DeltaCount);
        Assert.False(delta.IsEmpty);
    }

    [Fact]
    public void HasEntity_returns_true_for_created_entity()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        cb.Add(e, new Position(1, 2));
        var delta = cb.Snapshot();

        Assert.True(delta.HasEntity(e));
    }

    [Fact]
    public void HasEntity_returns_true_for_set_target()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var cb = new CommandBuffer(world);
        cb.Set(e, new Position(3, 4));
        var delta = cb.Snapshot();

        Assert.True(delta.HasEntity(e));
    }

    [Fact]
    public void HasEntity_returns_true_for_destroyed_entity()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Destroy(e);
        var delta = cb.Snapshot();

        Assert.True(delta.HasEntity(e));
    }

    [Fact]
    public void Snapshot_destroy_uses_recorded_entity_version()
    {
        var world = new World();
        var stale = world.Create();
        var cb = new CommandBuffer(world);

        cb.Destroy(stale);
        world.Destroy(stale);
        var recycled = world.Create();
        var delta = cb.Snapshot();

        Assert.Contains(stale, delta.DestroyedEntities);
        Assert.DoesNotContain(recycled, delta.DestroyedEntities);
    }

    [Fact]
    public void Snapshot_destroy_preserves_unknown_entity_handle()
    {
        var world = new World();
        var unknown = new Entity(9999, 1);
        var cb = new CommandBuffer(world);

        cb.Destroy(unknown);
        var delta = cb.Snapshot();

        Assert.Contains(unknown, delta.DestroyedEntities);
    }

    [Fact]
    public void HasEntity_returns_false_for_unknown_entity()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        var delta = cb.Snapshot();

        Assert.False(delta.HasEntity(new Entity(9999, 1)));
    }

    [Fact]
    public void MergeOfMerge_created_then_destroy_then_recreate()
    {
        var world = new World();
        var b1 = new CommandBuffer(world);
        var e = b1.Create();
        b1.Add(e, new Position(1, 2));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Destroy(e);
        var delta2 = b2.Snapshot();

        var firstMerge = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(firstMerge.CreatedEntities);
        Assert.Empty(firstMerge.DestroyedEntities);
        Assert.Single(firstMerge.ReservedEntities);
        Assert.Single(firstMerge.ReleasedEntities);

        var b3 = new CommandBuffer(world);
        var e2 = b3.Create();
        b3.Add(e2, new Velocity(10, 20));
        var delta3 = b3.Snapshot();

        var secondMerge = FrameDelta.Merge(firstMerge, delta3);

        Assert.Empty(secondMerge.ReleasedEntities);
        Assert.Empty(secondMerge.ReservedEntities);
        Assert.Single(secondMerge.CreatedEntities);
        Assert.True(secondMerge.HasEntity(e2));

        world.Replay(secondMerge);
        Assert.False(world.IsAlive(e));
        Assert.True(world.IsAlive(e2));
        Assert.True(world.TryGet(e2, out Velocity v));
        Assert.Equal(new Velocity(10, 20), v);
    }

    [Fact]
    public void MergeOfMerge_set_folded_across_three_deltas()
    {
        var world = new World();
        var e = world.Create(new Position(1, 1));

        var b1 = new CommandBuffer(world);
        b1.Set(e, new Position(10, 10));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(e, new Position(20, 20));
        var delta2 = b2.Snapshot();

        var firstMerge = FrameDelta.Merge(delta1, delta2);

        var b3 = new CommandBuffer(world);
        b3.Set(e, new Position(30, 30));
        var delta3 = b3.Snapshot();

        var secondMerge = FrameDelta.Merge(firstMerge, delta3);

        Assert.Single(secondMerge.SetCommands);
        Assert.Equal(1, secondMerge.DeltaCount);

        world.Replay(secondMerge);
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(30, 30), p);
    }

    [Fact]
    public void MergeOfMerge_add_then_remove_then_add_becomes_set()
    {
        var world = new World();
        var e = world.Create();

        var b1 = new CommandBuffer(world);
        b1.Add(e, new Position(1, 2));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Remove<Position>(e);
        var delta2 = b2.Snapshot();

        var firstMerge = FrameDelta.Merge(delta1, delta2);
        Assert.Empty(firstMerge.AddCommands);
        Assert.Empty(firstMerge.SetCommands);
        Assert.Empty(firstMerge.RemoveCommands);
        Assert.True(firstMerge.IsEmpty);

        var b3 = new CommandBuffer(world);
        b3.Add(e, new Position(5, 6));
        var delta3 = b3.Snapshot();

        var secondMerge = FrameDelta.Merge(firstMerge, delta3);
        Assert.Single(secondMerge.AddCommands);
        Assert.Empty(secondMerge.SetCommands);

        world.Replay(secondMerge);
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(5, 6), p);
    }

    [Fact]
    public void MergeOfMerge_created_entity_gets_component_removed_by_third_delta()
    {
        var world = new World();
        var b1 = new CommandBuffer(world);
        var e = b1.Create();
        b1.Add(e, new Position(1, 2));
        b1.Add(e, new Velocity(3, 4));
        b1.Add(e, new Health(100));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(e, new Position(5, 6));
        var delta2 = b2.Snapshot();

        var firstMerge = FrameDelta.Merge(delta1, delta2);

        var b3 = new CommandBuffer(world);
        b3.Remove<Velocity>(e);
        var delta3 = b3.Snapshot();

        var secondMerge = FrameDelta.Merge(firstMerge, delta3);

        Assert.Single(secondMerge.CreatedEntities);
        var created = secondMerge.CreatedEntities[0];
        Assert.Equal(e, created.Entity);

        world.Replay(secondMerge);
        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(5, 6), p);
        Assert.False(world.TryGet<Velocity>(e, out _));
        Assert.True(world.TryGet(e, out Health h));
        Assert.Equal(new Health(100), h);
    }

    [Fact]
    public void StaticMerge_produces_correct_replay_result()
    {
        var world = new World();
        var entity = world.Create(new Position(10, 20));

        var b1 = new CommandBuffer(world);
        b1.Set(entity, new Position(30, 40));
        b1.Add(entity, new Velocity(5, 6));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Set(entity, new Position(50, 60));
        b2.Remove<Velocity>(entity);
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        Assert.Empty(merged.AddCommands);
        Assert.Single(merged.SetCommands);
        Assert.Empty(merged.RemoveCommands);

        var replayWorld = new World();
        var replayEntity = replayWorld.Create(new Position(10, 20));
        replayWorld.Replay(merged);
        Assert.True(replayWorld.TryGet(replayEntity, out Position p));
        Assert.Equal(new Position(50, 60), p);
    }

    [Fact]
    public void FrameDelta_public_api_does_not_expose_mutable_command_storage()
    {
        Assert.False(typeof(FrameDelta).GetProperty("ReleasedEntities")?.CanWrite ?? false,
            "ReleasedEntities should not be publicly settable");
    }

    [Fact]
    public void Snapshot_frame_survives_buffer_reuse()
    {
        var world = new World();
        var entity = world.Create(new Position(0, 0));

        var b1 = new CommandBuffer(world);
        b1.Set(entity, new Position(1, 2));
        var delta1 = b1.Snapshot();

        var b2 = new CommandBuffer(world);
        b2.Add(entity, new Velocity(5, 6));
        var delta2 = b2.Snapshot();

        var merged = FrameDelta.Merge(delta1, delta2);

        b1.Set(entity, new Position(999, 999));
        b1.Snapshot();

        var replica = new World();
        var replicaEntity = replica.Create(new Position(0, 0));
        replica.Replay(merged);
        Assert.True(replica.TryGet(replicaEntity, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(replica.TryGet(replicaEntity, out Velocity v));
        Assert.Equal(new Velocity(5, 6), v);
    }

    private static int GetQueryGeneration(World world)
    {
        var field = typeof(World).GetField("_queryGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to find World._queryGeneration.");
        return (int)field.GetValue(world)!;
    }

    private static int GetArchetypeCount(World world)
    {
        var field = typeof(World).GetField("_archetypes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to find World._archetypes.");
        return ((System.Collections.IDictionary)field.GetValue(world)!).Count;
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_submits_and_returns_delta()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        buffer.Add(e, new Velocity(3, 4));

        var task = buffer.SubmitAndSnapshotAsync();
        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);

        var delta = await task;
        Assert.Single(delta.CreatedEntities);
        Assert.Equal(2, delta.CreatedEntities[0].Components.Length);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_with_existing_entity_ops()
    {
        var world = new World();
        var e = world.Create(new Position(10, 20), new Velocity(5, 6));
        var buffer = new CommandBuffer(world);

        buffer.Set(e, new Position(30, 40));
        buffer.Remove<Velocity>(e);

        var task = buffer.SubmitAndSnapshotAsync();
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(30, 40), p);
        Assert.False(world.TryGet<Velocity>(e, out _));

        var delta = await task;
        Assert.Single(delta.SetCommands);
        Assert.Single(delta.RemoveCommands);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_with_hierarchy()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        var buffer = new CommandBuffer(world);

        buffer.Link(parent, child);

        var task = buffer.SubmitAndSnapshotAsync();
        Assert.True(world.TryGetParent(child, out var p));
        Assert.Equal(parent, p);

        var delta = await task;
        Assert.Single(delta.LinkCommands);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_with_destroy()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(e);

        var task = buffer.SubmitAndSnapshotAsync();
        Assert.False(world.IsAlive(e));

        var delta = await task;
        Assert.Single(delta.DestroyedEntities);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_destroy_uses_recorded_entity_version()
    {
        var world = new World();
        var stale = world.Create(new Position(1, 2));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(stale);
        world.Destroy(stale);
        var recycled = world.Create(new Position(3, 4));

        var task = buffer.SubmitAndSnapshotAsync();
        Assert.True(world.IsAlive(recycled));

        var delta = await task;
        Assert.Contains(stale, delta.DestroyedEntities);
        Assert.DoesNotContain(recycled, delta.DestroyedEntities);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_returns_empty_delta_for_empty_buffer()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var delta = await buffer.SubmitAndSnapshotAsync();
        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_buffer_reusable_after_call()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e1 = buffer.Create();
        buffer.Add(e1, new Position(1, 2));
        var task1 = buffer.SubmitAndSnapshotAsync();
        Assert.True(world.IsAlive(e1));
        await task1;

        var e2 = buffer.Create();
        buffer.Add(e2, new Position(3, 4));
        var task2 = buffer.SubmitAndSnapshotAsync();
        Assert.True(world.IsAlive(e2));
        var delta2 = await task2;
        Assert.Single(delta2.CreatedEntities);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_delta_can_be_replayed_in_another_world()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        buffer.Add(e, new Velocity(3, 4));

        var delta = await buffer.SubmitAndSnapshotAsync();

        var replica = new World();
        replica.Replay(delta);
        Assert.True(replica.IsAlive(e));
        Assert.True(replica.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(replica.TryGet(e, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_mixed_script_scenario()
    {
        var world = new World();
        var entities = new Entity[100];
        for (int i = 0; i < 100; i++)
            entities[i] = world.Create(new Position(i, i + 1));

        var buffer = new CommandBuffer(world);

        for (int i = 0; i < 100; i++)
        {
            if ((i & 1) == 0)
            {
                buffer.Set(entities[i], new Position(i + 10, i + 20));
                if ((i & 3) == 0) buffer.Remove<Velocity>(entities[i]);
                if ((i & 7) == 0) buffer.Destroy(entities[i]);
            }
            else
            {
                var e = buffer.Create();
                buffer.Add(e, new Position(i + 30, i + 40));
                if ((i & 3) == 1) buffer.Remove<Position>(e);
                if ((i & 7) == 1) buffer.Destroy(e);
            }
        }

        var delta = await buffer.SubmitAndSnapshotAsync();

        Assert.False(world.IsAlive(entities[0]));
        Assert.Equal(new Position(12, 22), world.TryGet(entities[2], out Position p2) ? p2 : default);

        Assert.True(delta.DeltaCount > 0);
    }
}
