using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class CommandStreamTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Submit_applies_created_entity_components_and_preserves_reserved_handle()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var entity = stream.Create();
        stream.Add(entity, new Position(1, 2));
        stream.Add(entity, new Velocity(3, 4));

        Assert.False(world.IsAlive(entity));
        Assert.True(stream.Submit());

        Assert.True(world.IsAlive(entity));
        Assert.True(world.TryGet(entity, out Position position));
        Assert.True(world.TryGet(entity, out Velocity velocity));
        Assert.Equal(new Position(1, 2), position);
        Assert.Equal(new Velocity(3, 4), velocity);
    }

    [Fact]
    public void Submit_applies_existing_entity_commands_and_allows_reuse()
    {
        var world = new World();
        var entity = world.Create(new Position(0, 0), new Velocity(5, 6));
        var stream = new CommandStream(world);

        stream.Set(entity, new Position(7, 8));
        stream.Remove<Velocity>(entity);
        stream.Add(entity, new Health(9));
        Assert.True(stream.Submit());

        Assert.True(world.TryGet(entity, out Position position));
        Assert.Equal(new Position(7, 8), position);
        Assert.False(world.TryGet<Velocity>(entity, out _));
        Assert.True(world.TryGet(entity, out Health health));
        Assert.Equal(new Health(9), health);

        stream.Set(entity, new Health(10));
        Assert.True(stream.Submit());
        Assert.True(world.TryGet(entity, out health));
        Assert.Equal(new Health(10), health);
    }

    [Fact]
    public void Submit_destroy_releases_created_entity_or_destroys_existing_entity()
    {
        var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(3, 4));
        stream.Destroy(created);
        stream.Destroy(existing);

        Assert.True(stream.Submit());

        Assert.False(world.IsAlive(created));
        Assert.False(world.IsAlive(existing));
    }

    [Fact]
    public void Snapshot_builds_frame_delta_without_mutating_world()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Set(existing, new Position(3, 4));

        var delta = stream.Snapshot();

        Assert.False(world.IsAlive(created));
        Assert.True(world.TryGet(existing, out Position position));
        Assert.Equal(new Position(0, 0), position);

        world.Replay(delta);
        Assert.True(world.IsAlive(created));
        Assert.True(world.TryGet(created, out position));
        Assert.Equal(new Position(1, 2), position);
        Assert.True(world.TryGet(existing, out position));
        Assert.Equal(new Position(3, 4), position);
    }

    [Fact]
    public void Submit_returns_false_when_stream_is_empty()
    {
        var world = new World();
        var stream = new CommandStream(world);

        Assert.False(stream.Submit());
    }

    [Fact]
    public void Link_and_Unlink_apply_hierarchy_changes()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        var stream = new CommandStream(world);

        stream.Link(parent, child);
        Assert.True(stream.Submit());

        Assert.True(world.TryGetParent(child, out var p));
        Assert.Equal(parent, p);

        stream.Unlink(child);
        Assert.True(stream.Submit());
        Assert.False(world.TryGetParent(child, out _));
    }

    [Fact]
    public void Link_skipped_for_destroyed_existing_entity()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.Link(parent, child);
        stream.Destroy(child);
        Assert.True(stream.Submit());

        Assert.False(world.IsAlive(child));
        // Parent should have no children since child was destroyed before link applied
    }

    [Fact]
    public void Clone_copies_all_components_to_created_entity()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2), new Velocity(3, 4));
        var stream = new CommandStream(world);

        var clone = stream.Clone(entity);
        Assert.True(stream.Submit());

        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(world.TryGet(clone, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);

        // Source unchanged
        Assert.True(world.TryGet(entity, out Position sp));
        Assert.Equal(new Position(1, 2), sp);
    }

    [Fact]
    public void Clone_deep_copies_children_hierarchy()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child1 = world.Create(new Velocity(3, 4));
        var child2 = world.Create(new Health(100));
        world.Link(parent, child1);
        world.Link(parent, child2);

        var stream = new CommandStream(world);
        var clone = stream.Clone(parent);
        Assert.True(stream.Submit());

        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);

        // Clone should have children
        var cloneChildren = new List<Entity>();
        foreach (var c in world.Hierarchy.EnumerateChildren(world, clone))
        {
            cloneChildren.Add(c);
        }
        Assert.Equal(2, cloneChildren.Count);
    }

    [Fact]
    public void Clone_snapshot_builds_replayable_delta()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2), new Velocity(3, 4));
        var stream = new CommandStream(world);

        var clone = stream.Clone(entity);
        var delta = stream.Snapshot();

        // Clone not yet materialized
        Assert.False(world.IsAlive(clone));

        // Replay into replica
        var replica = new World();
        var replicaEntity = replica.Create(new Position(1, 2), new Velocity(3, 4));
        replica.Replay(delta);

        Assert.True(replica.IsAlive(clone));
        Assert.True(replica.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(replica.TryGet(clone, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_submits_and_returns_delta()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Set(existing, new Position(3, 4));

        var delta = await stream.SubmitAndSnapshotAsync();

        // Changes applied to world
        Assert.True(world.IsAlive(created));
        Assert.True(world.TryGet(created, out Position p1));
        Assert.Equal(new Position(1, 2), p1);
        Assert.True(world.TryGet(existing, out Position p2));
        Assert.Equal(new Position(3, 4), p2);

        // Delta is replayable
        var replica = new World();
        var replicaExisting = replica.Create(new Position(0, 0));
        replica.Replay(delta);

        Assert.True(replica.IsAlive(created));
        Assert.True(replica.TryGet(existing, out Position rp));
        Assert.Equal(new Position(3, 4), rp);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_returns_empty_for_empty_stream()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var delta = await stream.SubmitAndSnapshotAsync();
        Assert.Empty(delta.CreatedEntities);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_stream_reusable_after_call()
    {
        var world = new World();
        var stream = new CommandStream(world);

        stream.Create();
        await stream.SubmitAndSnapshotAsync();

        // Should be able to use stream again
        var e = stream.Create();
        stream.Add(e, new Position(5, 6));
        await stream.SubmitAndSnapshotAsync();

        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(5, 6), p);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_includes_hierarchy_in_delta()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        var stream = new CommandStream(world);

        stream.Link(parent, child);
        var delta = await stream.SubmitAndSnapshotAsync();

        // Verify link was applied
        Assert.True(world.TryGetParent(child, out var p));
        Assert.Equal(parent, p);

        // Verify delta contains link command (need to check internal structure)
        Assert.NotEmpty(delta.LinkCommands);
    }
}
