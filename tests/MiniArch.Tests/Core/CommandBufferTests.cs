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
        Assert.False(merged.IsEmpty);

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
        Assert.False(merged.IsEmpty);

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
        Assert.False(merged.IsEmpty);

        world.Replay(merged);
        Assert.False(world.TryGetParent(child, out _));
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
        Assert.False(merged.IsEmpty);

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
        Assert.False(firstMerge.IsEmpty);

        var b3 = new CommandBuffer(world);
        var e2 = b3.Create();
        b3.Add(e2, new Velocity(10, 20));
        var delta3 = b3.Snapshot();

        var secondMerge = FrameDelta.Merge(firstMerge, delta3);

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
        Assert.False(firstMerge.IsEmpty);

        var b3 = new CommandBuffer(world);
        b3.Set(e, new Position(30, 30));
        var delta3 = b3.Snapshot();

        var secondMerge = FrameDelta.Merge(firstMerge, delta3);
        Assert.False(secondMerge.IsEmpty);

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
        Assert.False(firstMerge.IsEmpty);

        var b3 = new CommandBuffer(world);
        b3.Add(e, new Position(5, 6));
        var delta3 = b3.Snapshot();

        var secondMerge = FrameDelta.Merge(firstMerge, delta3);
        Assert.False(secondMerge.IsEmpty);

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
        Assert.False(secondMerge.IsEmpty);

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
        Assert.False(merged.IsEmpty);

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
    public void CrossWorld_replay_created_entity_with_multiple_components()
    {
        var world = new World();
        var buffer = new CommandBuffer(world);

        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        buffer.Add(e, new Velocity(3, 4));
        buffer.Add(e, new Health(100));
        var delta = buffer.Snapshot();

        var replica = new World();
        replica.Replay(delta);
        Assert.True(replica.IsAlive(e));
        Assert.True(replica.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(replica.TryGet(e, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);
        Assert.True(replica.TryGet(e, out Health h));
        Assert.Equal(new Health(100), h);
    }

    [Fact]
    public void CrossWorld_replay_existing_entity_set()
    {
        var source = new World();
        var e = source.Create(new Position(10, 20));
        var buffer = new CommandBuffer(source);
        buffer.Set(e, new Position(30, 40));
        var delta = buffer.Snapshot();

        var replica = new World();
        var replicaE = replica.Create(new Position(10, 20));
        replica.Replay(delta);

        Assert.True(replica.TryGet(replicaE, out Position p));
        Assert.Equal(new Position(30, 40), p);
    }

    [Fact]
    public void CrossWorld_replay_existing_entity_add_and_remove()
    {
        var source = new World();
        var e = source.Create(new Position(1, 1));
        var buffer = new CommandBuffer(source);
        buffer.Add(e, new Velocity(5, 6));
        buffer.Remove<Position>(e);
        var delta = buffer.Snapshot();

        var replica = new World();
        var replicaE = replica.Create(new Position(1, 1));
        replica.Replay(delta);

        Assert.False(replica.TryGet<Position>(replicaE, out _));
        Assert.True(replica.TryGet(replicaE, out Velocity v));
        Assert.Equal(new Velocity(5, 6), v);
    }

    [Fact]
    public void CrossWorld_replay_destroy_existing_entity()
    {
        var source = new World();
        var e = source.Create(new Position(1, 2));
        var buffer = new CommandBuffer(source);
        buffer.Destroy(e);
        var delta = buffer.Snapshot();

        var replica = new World();
        var replicaE = replica.Create(new Position(1, 2));
        replica.Replay(delta);

        Assert.False(replica.IsAlive(replicaE));
    }

    [Fact]
    public void CrossWorld_replay_link_and_unlink()
    {
        var source = new World();
        var parent = source.Create();
        var child = source.Create();
        var buffer = new CommandBuffer(source);
        buffer.Link(parent, child);
        var delta = buffer.Snapshot();

        var replica = new World();
        var replicaParent = replica.Create();
        var replicaChild = replica.Create();
        replica.Replay(delta);

        Assert.True(replica.TryGetParent(replicaChild, out var p));
        Assert.Equal(replicaParent, p);

        var buffer2 = new CommandBuffer(source);
        buffer2.Unlink(child);
        var delta2 = buffer2.Snapshot();

        replica.Replay(delta2);
        Assert.False(replica.TryGetParent(replicaChild, out _));
    }

    [Fact]
    public void CrossWorld_replay_mixed_created_and_existing()
    {
        var source = new World();
        var existing = source.Create(new Position(10, 20));
        var buffer = new CommandBuffer(source);

        var created = buffer.Create();
        buffer.Add(created, new Position(100, 200));
        buffer.Set(existing, new Position(30, 40));
        buffer.Add(existing, new Velocity(5, 6));
        var delta = buffer.Snapshot();

        var replica = new World();
        var replicaExisting = replica.Create(new Position(10, 20));
        replica.Replay(delta);

        Assert.True(replica.IsAlive(created));
        Assert.True(replica.TryGet(created, out Position cp));
        Assert.Equal(new Position(100, 200), cp);

        Assert.True(replica.TryGet(replicaExisting, out Position ep));
        Assert.Equal(new Position(30, 40), ep);
        Assert.True(replica.TryGet(replicaExisting, out Velocity ev));
        Assert.Equal(new Velocity(5, 6), ev);
    }

    [Fact]
    public void CrossWorld_replay_destroy_then_reuse_id_does_not_ressurect()
    {
        var source = new World();
        var e = source.Create(new Position(1, 2));
        var buffer = new CommandBuffer(source);
        buffer.Destroy(e);
        var delta = buffer.Snapshot();

        source.Destroy(e);
        var recycled = source.Create(new Position(99, 99));

        var replica = new World();
        var replicaE = replica.Create(new Position(1, 2));
        replica.Replay(delta);
        Assert.False(replica.IsAlive(replicaE));
    }

    [Fact]
    public void CrossWorld_replay_batch_created_entities_query_visible()
    {
        const int N = 200;
        var source = new World();
        var buffer = new CommandBuffer(source);
        var entities = new Entity[N];

        for (int i = 0; i < N; i++)
        {
            entities[i] = buffer.Create();
            buffer.Add(entities[i], new Position(i, i + 1));
            if ((i & 1) == 0)
                buffer.Add(entities[i], new Velocity(i * 10, i * 20));
        }
        var delta = buffer.Snapshot();

        var replica = new World();
        replica.Replay(delta);

        var query = replica.Query(new QueryDescription().With<Position>());
        int count = 0;
        foreach (var chunk in query.GetChunks())
            count += chunk.Count;
        Assert.Equal(N, count);

        var posVelQuery = replica.Query(new QueryDescription().With<Position>().With<Velocity>());
        int posVelCount = 0;
        foreach (var chunk in posVelQuery.GetChunks())
            posVelCount += chunk.Count;
        Assert.Equal(N / 2, posVelCount);
    }

    [Fact]
    public async Task CrossWorld_replay_submit_delta_not_snapshot()
    {
        var source = new World();
        var existing = source.Create(new Position(10, 20));
        var buffer = new CommandBuffer(source);

        var created = buffer.Create();
        buffer.Add(created, new Velocity(1, 2));
        buffer.Set(existing, new Position(30, 40));
        buffer.Remove<Position>(existing);

        var delta = await buffer.SubmitAndSnapshotAsync();

        var replica = new World();
        var replicaExisting = replica.Create(new Position(10, 20));
        replica.Replay(delta!);

        Assert.True(replica.IsAlive(created));
        Assert.True(replica.TryGet(created, out Velocity cv));
        Assert.Equal(new Velocity(1, 2), cv);

        Assert.False(replica.TryGet<Position>(replicaExisting, out _));
    }

    [Fact]
    public void CrossWorld_replay_create_empty_entity()
    {
        var source = new World();
        var buffer = new CommandBuffer(source);
        var e = buffer.Create();
        var delta = buffer.Snapshot();

        var replica = new World();
        replica.Replay(delta);
        Assert.True(replica.IsAlive(e));
        Assert.False(replica.TryGet<Position>(e, out _));
    }

    [Fact]
    public void CrossWorld_replay_create_then_destroy_becomes_release()
    {
        var source = new World();
        var buffer = new CommandBuffer(source);
        var e = buffer.Create();
        buffer.Add(e, new Position(1, 2));
        buffer.Destroy(e);
        var delta = buffer.Snapshot();

        var replica = new World();
        replica.Replay(delta);
        Assert.False(replica.IsAlive(e));
    }

    [Fact]
    public void CrossWorld_replay_submit_path_produces_same_result_as_source()
    {
        var source = new World();
        var existing = source.Create(new Position(10, 20), new Velocity(5, 6));
        var buffer = new CommandBuffer(source);

        var created1 = buffer.Create();
        buffer.Add(created1, new Position(100, 200));
        buffer.Add(created1, new Velocity(10, 20));

        var created2 = buffer.Create();
        buffer.Add(created2, new Health(50));

        buffer.Set(existing, new Position(30, 40));
        buffer.Remove<Velocity>(existing);

        var delta = buffer.Snapshot();
        buffer.Submit();

        var replica = new World();
        var replicaExisting = replica.Create(new Position(10, 20), new Velocity(5, 6));
        replica.Replay(delta);

        Assert.True(replica.IsAlive(created1));
        Assert.Equal(source.TryGet(created1, out Position _), replica.TryGet(created1, out Position _));

        Assert.True(replica.IsAlive(created2));
        Assert.True(replica.TryGet(created2, out Health rh));
        Assert.Equal(new Health(50), rh);

        Assert.True(replica.TryGet(replicaExisting, out Position rep));
        Assert.Equal(new Position(30, 40), rep);
        Assert.False(replica.TryGet<Velocity>(replicaExisting, out _));
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

    private static void AssertEntityStateEquals(World expected, World actual, Entity entity)
    {
        Assert.Equal(expected.IsAlive(entity), actual.IsAlive(entity));
        if (!expected.IsAlive(entity)) return;

        Assert.Equal(expected.TryGet(entity, out Position sp), actual.TryGet(entity, out Position rp));
        if (expected.TryGet(entity, out sp)) Assert.Equal(sp, rp);

        Assert.Equal(expected.TryGet(entity, out Velocity sv), actual.TryGet(entity, out Velocity rv));
        if (expected.TryGet(entity, out sv)) Assert.Equal(sv, rv);

        Assert.Equal(expected.TryGet(entity, out Health sh), actual.TryGet(entity, out Health rh));
        if (expected.TryGet(entity, out sh)) Assert.Equal(sh, rh);

        Assert.Equal(expected.TryGetParent(entity, out var sp2), actual.TryGetParent(entity, out var rp2));
    }

    [Fact]
    public void CrossWorld_multi_frame_ordered_replay_produces_identical_world()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();
        var cb = new CommandBuffer(source);
        var tracked = new List<Entity>();

        // === Frame 1: seed world with diverse entities ===
        var a = cb.Create(); cb.Add(a, new Position(1, 2));
        var b = cb.Create(); cb.Add(b, new Position(3, 4)); cb.Add(b, new Velocity(5, 6));
        var c = cb.Create();
        var d = cb.Create(); cb.Add(d, new Health(100));
        var e = cb.Create(); cb.Add(e, new Position(10, 20)); cb.Add(e, new Velocity(30, 40)); cb.Add(e, new Health(50));
        cb.Link(a, b);
        cb.Link(a, c);
        tracked.AddRange([a, b, c, d, e]);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // === Frame 2: modify existing + create + destroy + link/unlink ===
        cb.Set(a, new Position(100, 200));
        cb.Add(a, new Velocity(7, 8));
        cb.Set(b, new Position(30, 40));
        cb.Remove<Velocity>(b);
        cb.Destroy(c);
        var f = cb.Create(); cb.Add(f, new Position(50, 60));
        cb.Link(a, f);
        cb.Unlink(b);
        tracked.Add(f);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // === Frame 3: create+destroy same frame, re-add component, complex hierarchy ===
        var ghost = cb.Create(); cb.Add(ghost, new Position(999, 999)); cb.Destroy(ghost);
        cb.Remove<Health>(e);
        cb.Add(d, new Position(55, 66));
        cb.Add(e, new Health(77));
        cb.Set(f, new Velocity(1, 1));
        var g = cb.Create();
        cb.Add(g, new Velocity(9, 10));
        cb.Link(f, g);
        tracked.Add(g);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // === Frame 4: component cycling, overwrite via add, destroy leaf ===
        cb.Remove<Position>(d);
        cb.Add(d, new Position(88, 99));
        cb.Set(a, new Velocity(200, 300));
        cb.Remove<Velocity>(f);
        cb.Set(e, new Position(111, 222));
        cb.Add(e, new Velocity(333, 444));
        cb.Destroy(b);
        cb.Unlink(g);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // === Frame 5: recycled ID, modify survivors, re-link ===
        var h = cb.Create(); cb.Add(h, new Position(42, 42)); cb.Add(h, new Health(1));
        cb.Set(d, new Position(1, 1));
        cb.Remove<Health>(e);
        cb.Set(g, new Velocity(100, 200));
        cb.Link(a, h);
        cb.Link(a, g);
        tracked.Add(h);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // === Replay all frames into empty world ===
        var replica = new World();
        foreach (var delta in deltas)
            replica.Replay(delta);

        foreach (var entity in tracked)
            AssertEntityStateEquals(source, replica, entity);

        // ghost was create+destroy same frame - never alive in either world
        Assert.False(source.IsAlive(ghost));
        Assert.False(replica.IsAlive(ghost));

        // entity count parity: count alive entities via query
        var srcAll = source.Query(new QueryDescription());
        int srcCount = 0;
        foreach (var chunk in srcAll.GetChunks())
            srcCount += chunk.Count;

        var repAll = replica.Query(new QueryDescription());
        int repCount = 0;
        foreach (var chunk in repAll.GetChunks())
            repCount += chunk.Count;

        Assert.Equal(srcCount, repCount);
    }

    [Fact]
    public void CrossWorld_multi_frame_replay_with_recycled_id_mutation()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();
        var cb = new CommandBuffer(source);

        // Frame 1: create and immediately destroy
        var victim = cb.Create(); cb.Add(victim, new Position(1, 2)); cb.Add(victim, new Velocity(3, 4));
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 2: destroy victim, ID goes to free list
        cb.Destroy(victim);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 3: new entity reuses victim's ID (different version)
        var recycled = cb.Create(); cb.Add(recycled, new Health(100));
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 4: mutate recycled entity
        cb.Add(recycled, new Position(50, 60));
        cb.Set(recycled, new Health(200));
        deltas.Add(cb.Snapshot());
        cb.Submit();

        var replica = new World();
        foreach (var delta in deltas)
            replica.Replay(delta);

        Assert.False(replica.IsAlive(victim));
        Assert.True(replica.IsAlive(recycled));
        Assert.True(replica.TryGet(recycled, out Health h));
        Assert.Equal(new Health(200), h);
        Assert.True(replica.TryGet(recycled, out Position p));
        Assert.Equal(new Position(50, 60), p);
        Assert.False(replica.TryGet<Velocity>(recycled, out _));
    }

    [Fact]
    public void CrossWorld_multi_frame_replay_hierarchy_evolution()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();
        var cb = new CommandBuffer(source);

        // Frame 1: create tree A->B->C
        var a = cb.Create(); cb.Add(a, new Position(1, 1));
        var b = cb.Create(); cb.Add(b, new Position(2, 2));
        var c = cb.Create(); cb.Add(c, new Position(3, 3));
        cb.Link(a, b);
        cb.Link(b, c);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 2: unlink B from A, link C directly to A, add D as child of A
        var d = cb.Create(); cb.Add(d, new Position(4, 4));
        cb.Unlink(b);
        cb.Unlink(c);
        cb.Link(a, c);
        cb.Link(a, d);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 3: destroy B (leaf now), verify others survive
        cb.Destroy(b);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 4: destroy A (should cascade to C, D)
        cb.Destroy(a);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        var replica = new World();
        foreach (var delta in deltas)
            replica.Replay(delta);

        Assert.False(replica.IsAlive(a));
        Assert.False(replica.IsAlive(b));
        Assert.False(replica.IsAlive(c));
        Assert.False(replica.IsAlive(d));
    }

    [Fact]
    public void CrossWorld_1000_frame_fuzz_replay_produces_identical_world()
    {
        const int Frames = 1000;
        var seed = (int)DateTime.UtcNow.Ticks;
        var rng = new Random(seed);
        try
        {

        var source = new World();
        var cb = new CommandBuffer(source);
        var deltas = new List<FrameDelta>(Frames);
        var alive = new List<Entity>();
        var created = new List<Entity>();

        void PruneDead()
        {
            for (int i = alive.Count - 1; i >= 0; i--)
                if (!source.IsAlive(alive[i]))
                    alive.RemoveAt(i);
        }

        Entity PickAlive()
        {
            return alive[rng.Next(alive.Count)];
        }

        for (int frame = 0; frame < Frames; frame++)
        {
            PruneDead();
            var opsThisFrame = rng.Next(1, 8);

            for (int op = 0; op < opsThisFrame; op++)
            {
                var kind = alive.Count == 0 ? 0 : rng.Next(100);
                if (kind < 35 || alive.Count == 0)
                {
                    var e = cb.Create();
                    var compCount = rng.Next(4);
                    if (compCount > 0) cb.Add(e, new Position(rng.Next(), rng.Next()));
                    if (compCount > 1) cb.Add(e, new Velocity(rng.Next(), rng.Next()));
                    if (compCount > 2) cb.Add(e, new Health(rng.Next()));
                    alive.Add(e);
                    created.Add(e);
                }
                else if (kind < 48)
                {
                    var e = PickAlive();
                    cb.Destroy(e);
                    alive.Remove(e);
                }
                else if (kind < 62)
                {
                    cb.Set(PickAlive(), new Position(rng.Next(), rng.Next()));
                }
                else if (kind < 74)
                {
                    cb.Add(PickAlive(), new Velocity(rng.Next(), rng.Next()));
                }
                else if (kind < 84)
                {
                    cb.Add(PickAlive(), new Health(rng.Next()));
                }
                else if (kind < 90)
                {
                    cb.Remove<Position>(PickAlive());
                }
                else if (kind < 95)
                {
                    cb.Remove<Velocity>(PickAlive());
                }
                else
                {
                    cb.Remove<Health>(PickAlive());
                }
            }

            deltas.Add(cb.Snapshot());
            cb.Submit();
        }

        var replica = new World();
        foreach (var delta in deltas)
            replica.Replay(delta);

        foreach (var entity in created)
            AssertEntityStateEquals(source, replica, entity);

        Assert.Equal(CountAllEntities(source), CountAllEntities(replica));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Fuzz test failed with seed={seed}. Use this seed to reproduce.", ex);
        }
    }

    private static int CountAllEntities(World world)
    {
        int count = 0;
        foreach (var chunk in world.Query(new QueryDescription()).GetChunks())
            count += chunk.Count;
        return count;
    }

    // ═══════════════════════════════════════════════════════════
    // Clone → Snapshot → Replay cross-world tests
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Clone_snapshot_can_be_replayed_into_another_world()
    {
        var source = new World();
        var entity = source.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandBuffer(source);

        var clone = buffer.Clone(entity);
        var delta = buffer.Snapshot();

        var replica = new World();
        var replicaEntity = replica.Create(new Position(1, 2), new Velocity(3, 4));
        replica.Replay(delta);

        Assert.True(replica.IsAlive(clone));
        Assert.True(replica.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(replica.TryGet(clone, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);

        // Source entity unchanged
        Assert.True(replica.IsAlive(replicaEntity));
        Assert.True(replica.TryGet(replicaEntity, out Position sp));
        Assert.Equal(new Position(1, 2), sp);
    }

    [Fact]
    public void Clone_deep_with_children_snapshot_replay()
    {
        var source = new World();
        var parent = source.Create(new Position(1, 2));
        var child1 = source.Create(new Velocity(3, 4));
        var child2 = source.Create(new Health(100));
        source.Link(parent, child1);
        source.Link(parent, child2);
        var buffer = new CommandBuffer(source);

        var clone = buffer.Clone(parent);
        var delta = buffer.Snapshot();

        var replica = new World();
        var rParent = replica.Create(new Position(1, 2));
        var rChild1 = replica.Create(new Velocity(3, 4));
        var rChild2 = replica.Create(new Health(100));
        replica.Link(rParent, rChild1);
        replica.Link(rParent, rChild2);
        replica.Replay(delta);

        Assert.True(replica.IsAlive(clone));
        Assert.True(replica.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);

        var cloneChildren = replica.GetChildren(clone);
        Assert.Equal(2, cloneChildren.Count);
    }

    [Fact]
    public void Clone_mixed_with_other_commands_snapshot_replay()
    {
        var source = new World();
        var existing = source.Create(new Position(10, 20));
        var toClone = source.Create(new Velocity(5, 6), new Health(100));
        var buffer = new CommandBuffer(source);

        var clone = buffer.Clone(toClone);
        var created = buffer.Create();
        buffer.Add(created, new Position(100, 200));
        buffer.Set(existing, new Position(30, 40));
        var delta = buffer.Snapshot();

        var replica = new World();
        var rExisting = replica.Create(new Position(10, 20));
        var rToClone = replica.Create(new Velocity(5, 6), new Health(100));
        replica.Replay(delta);

        // Clone is alive with correct components
        Assert.True(replica.IsAlive(clone));
        Assert.True(replica.TryGet(clone, out Velocity cv));
        Assert.Equal(new Velocity(5, 6), cv);
        Assert.True(replica.TryGet(clone, out Health ch));
        Assert.Equal(new Health(100), ch);

        // Created entity
        Assert.True(replica.IsAlive(created));
        Assert.True(replica.TryGet(created, out Position cp));
        Assert.Equal(new Position(100, 200), cp);

        // Existing entity was set
        Assert.True(replica.TryGet(rExisting, out Position ep));
        Assert.Equal(new Position(30, 40), ep);
    }

    [Fact]
    public void Clone_then_destroy_in_same_buffer_replay_is_noop()
    {
        var source = new World();
        var entity = source.Create(new Position(1, 2));
        var buffer = new CommandBuffer(source);

        var clone = buffer.Clone(entity);
        buffer.Destroy(clone);
        var delta = buffer.Snapshot();

        Assert.Empty(delta.CreatedEntities);
        Assert.Empty(delta.DestroyedEntities);
        Assert.Single(delta.ReservedEntities);
        Assert.Single(delta.ReleasedEntities);

        var replica = new World();
        var rEntity = replica.Create(new Position(1, 2));
        replica.Replay(delta);

        Assert.True(replica.IsAlive(rEntity));
        Assert.False(replica.IsAlive(clone));
    }

    [Fact]
    public void Clone_then_set_in_same_buffer_replays_correctly()
    {
        // Clone and then Set on the clone within the same buffer session.
        // This tests that replay correctly materializes the clone with the final component values.
        var source = new World();
        var entity = source.Create(new Position(1, 2), new Velocity(3, 4));

        var buffer = new CommandBuffer(source);
        var clone = buffer.Clone(entity);
        buffer.Set(clone, new Position(99, 99));
        buffer.Add(clone, new Health(100));
        var delta = buffer.Snapshot();

        Assert.Single(delta.CreatedEntities);
        // Set and Add on a deferred entity fold into CreatedState, not separate commands

        var replica = new World();
        var rEntity = replica.Create(new Position(1, 2), new Velocity(3, 4));
        replica.Replay(delta);

        Assert.True(replica.IsAlive(clone));
        Assert.True(replica.TryGet(clone, out Position p));
        Assert.Equal(new Position(99, 99), p);
        Assert.True(replica.TryGet(clone, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);
        Assert.True(replica.TryGet(clone, out Health h));
        Assert.Equal(new Health(100), h);
    }

    [Fact]
    public void CrossWorld_multi_frame_clone_replay_produces_identical_world()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();
        var cb = new CommandBuffer(source);
        var tracked = new List<Entity>();

        // Frame 1: seed world with linked entities
        var a = cb.Create(); cb.Add(a, new Position(1, 2));
        var b = cb.Create(); cb.Add(b, new Position(3, 4)); cb.Add(b, new Velocity(5, 6));
        cb.Link(a, b);
        tracked.AddRange([a, b]);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 2: clone a (which has child b via link)
        var cloneA = cb.Clone(a);
        tracked.Add(cloneA);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 3: modify clone, destroy original child
        cb.Set(cloneA, new Position(99, 99));
        cb.Add(cloneA, new Health(100));
        cb.Destroy(b);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Frame 4: clone the clone (which now has Position+Health and a child)
        var clone2 = cb.Clone(cloneA);
        cb.Remove<Health>(clone2);
        tracked.Add(clone2);
        deltas.Add(cb.Snapshot());
        cb.Submit();

        // Replay all frames into empty world
        var replica = new World();
        foreach (var delta in deltas)
            replica.Replay(delta);

        foreach (var entity in tracked)
            AssertEntityStateEquals(source, replica, entity);

        Assert.Equal(CountAllEntities(source), CountAllEntities(replica));
    }

    // ═══════════════════════════════════════════════════════════
    // Merge CreatedEntity cross-world replay bug regression
    // ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════
    // Entity version alias safeguards
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Stale_destroy_does_not_affect_same_id_pending_entity()
    {
        // Destroy with a stale handle (same id, wrong version) must not
        // affect a pending entity that reuses the same id.
        var world = new World();

        var victim = world.Create(new Position(1, 2));
        var staleHandle = victim;
        world.Destroy(victim);

        var buffer = new CommandBuffer(world);
        var created = buffer.Create();
        buffer.Add(created, new Position(10, 20));

        // Stale handle has same id but older version
        buffer.Destroy(staleHandle);

        buffer.Submit();

        Assert.True(world.IsAlive(created));
        Assert.True(world.TryGet(created, out Position p));
        Assert.Equal(new Position(10, 20), p);
    }

    [Fact]
    public void Stale_set_does_not_overwrite_recycled_entity_ops()
    {
        var world = new World();

        var original = world.Create(new Position(1, 2));
        var staleHandle = original;
        world.Destroy(original);

        // Recycled entity gets same id, new version
        var recycled = world.Create(new Position(10, 20));

        var buffer = new CommandBuffer(world);
        buffer.Set(recycled, new Position(30, 40));
        // Stale handle with old version should NOT overwrite recycled's op
        buffer.Set(staleHandle, new Position(99, 99));

        buffer.Submit();

        Assert.True(world.TryGet(recycled, out Position p));
        Assert.Equal(new Position(30, 40), p);
    }

    [Fact]
    public void Stale_add_on_existing_entity_does_not_interfere()
    {
        var world = new World();

        var original = world.Create(new Velocity(5, 6));
        var staleHandle = original;
        world.Destroy(original);

        var recycled = world.Create(new Position(10, 20));

        var buffer = new CommandBuffer(world);
        buffer.Add(recycled, new Velocity(7, 8));
        // Stale handle Add should target dead entity, not recycled
        buffer.Add(staleHandle, new Health(100));

        buffer.Submit();

        Assert.True(world.TryGet(recycled, out Position p));
        Assert.Equal(new Position(10, 20), p);
        Assert.True(world.TryGet(recycled, out Velocity v));
        Assert.Equal(new Velocity(7, 8), v);
        Assert.False(world.TryGet<Health>(recycled, out _));
    }

    [Fact]
    public void Stale_destroy_after_recycled_create_does_not_cascade_to_descendants()
    {
        var world = new World();

        var victim = world.Create(new Position(1, 2));
        var staleHandle = victim;
        world.Destroy(victim);

        var buffer = new CommandBuffer(world);

        var parent = buffer.Create();
        buffer.Add(parent, new Position(10, 20));
        var child = buffer.Create();
        buffer.Add(child, new Velocity(1, 2));
        buffer.Link(parent, child);

        // Stale destroy must not cascade to mark child destroyed
        buffer.Destroy(staleHandle);

        buffer.Submit();

        Assert.True(world.IsAlive(parent));
        Assert.True(world.IsAlive(child));
        Assert.True(world.TryGet(parent, out Position p));
        Assert.Equal(new Position(10, 20), p);
    }

    [Fact]
    public async Task Stale_ops_in_async_path_do_not_corrupt_world()
    {
        var world = new World();

        var original = world.Create(new Position(1, 2));
        var staleHandle = original;
        world.Destroy(original);

        var recycled = world.Create(new Position(10, 20));

        var buffer = new CommandBuffer(world);
        buffer.Set(recycled, new Position(30, 40));
        buffer.Set(staleHandle, new Position(99, 99));

        var delta = await buffer.SubmitAndSnapshotAsync();

        // Source world: recycled has Position(30,40), not corrupted by stale handle
        Assert.True(world.TryGet(recycled, out Position p));
        Assert.Equal(new Position(30, 40), p);
    }

    [Fact]
    public void Snapshot_stale_destroy_uses_correct_version()
    {
        var world = new World();
        var stale = world.Create(new Position(1, 1));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(stale);
        world.Destroy(stale);
        var recycled = world.Create(new Position(2, 2));

        Assert.Equal(stale.Id, recycled.Id);
        Assert.NotEqual(stale.Version, recycled.Version);

        var delta = buffer.Snapshot();
        Assert.Contains(stale, delta.DestroyedEntities);
        Assert.DoesNotContain(recycled, delta.DestroyedEntities);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_stale_destroy_uses_correct_version()
    {
        var world = new World();
        var stale = world.Create(new Position(1, 1));
        var buffer = new CommandBuffer(world);

        buffer.Destroy(stale);
        world.Destroy(stale);
        var recycled = world.Create(new Position(2, 2));

        Assert.Equal(stale.Id, recycled.Id);
        Assert.NotEqual(stale.Version, recycled.Version);

        var delta = await buffer.SubmitAndSnapshotAsync();
        Assert.Contains(stale, delta.DestroyedEntities);
        Assert.DoesNotContain(recycled, delta.DestroyedEntities);
    }

    [Fact]
    public void Stale_destroy_on_created_entity_falls_through_to_existing_destroy()
    {
        // When a stale handle doesn't match any created state (version mismatch),
        // the destroy should fall through to AddExistingDestroy.
        // The world then skips the dead entity during Submit.
        var world = new World();

        var victim = world.Create(new Position(1, 2));
        var staleHandle = victim;
        world.Destroy(victim);

        var buffer = new CommandBuffer(world);
        // No pending entity with this id/version pair
        buffer.Destroy(staleHandle);

        // Must not throw
        buffer.Submit();
    }
}
