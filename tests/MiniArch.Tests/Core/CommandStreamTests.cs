using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MiniArch.Core;
using MiniArch.Diagnostics;
using MiniQueryCache = MiniArch.Core.QueryCache;
using MiniArch.Tests.Core.TestSupport;

namespace MiniArchTests.Core;

public sealed class CommandStreamTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);
    private readonly record struct SelfRef(Entity Target);
    private readonly record struct SignalPayloadField(long A, long B, long C);
    private readonly record struct C4(int Value);
    private readonly record struct C5(int Value);
    private readonly record struct C6(int Value);
    private readonly record struct C7(int Value);

    private readonly struct PositionCreateManyWriter : ICreateManyWriter<Position>
    {
        public void Write(int index, Entity entity, out Position component1)
        {
            component1 = new Position(index, index + 1);
        }
    }

    private readonly struct SelfRefCreateManyWriter : ICreateManyWriter<SelfRef>
    {
        public void Write(int index, Entity entity, out SelfRef component1)
        {
            component1 = new SelfRef(entity);
        }
    }

    private readonly struct DuplicatePositionCreateManyWriter : ICreateManyWriter<Position, Position>
    {
        public void Write(int index, Entity entity, out Position c1, out Position c2)
        {
            c1 = new Position(index, -1);
            c2 = new Position(index, index + 1000);
        }
    }

    private readonly struct PositionVelocityCreateManyWriter : ICreateManyWriter<Position, Velocity>
    {
        public void Write(int index, Entity entity, out Position c1, out Velocity c2)
        {
            c1 = new Position(index, index + 1);
            c2 = new Velocity(index + 10, index + 20);
        }
    }

    private readonly struct EightComponentCreateManyWriter : ICreateManyWriter<Position, Velocity, Health, SignalPayloadField, C4, C5, C6, C7>
    {
        public void Write(int index, Entity entity, out Position c1, out Velocity c2, out Health c3, out SignalPayloadField c4, out C4 c5, out C5 c6, out C6 c7, out C7 c8)
        {
            c1 = new Position(index, index + 1);
            c2 = new Velocity(index + 2, index + 3);
            c3 = new Health(index + 4);
            c4 = new SignalPayloadField(index, index + 1, index + 2);
            c5 = new C4(index + 5);
            c6 = new C5(index + 6);
            c7 = new C6(index + 7);
            c8 = new C7(index + 8);
        }
    }

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
    public void EntityCount_excludes_reserved_pending_entities()
    {
        var world = new World();

        // Empty world: EntityCount == 0
        Assert.Equal(0, world.EntityCount);

        // Create a live entity directly
        var alive = world.CreateEmpty();
        Assert.True(world.IsAlive(alive));
        Assert.Equal(1, world.EntityCount);

        // Reserve an entity via CommandStream (before Submit)
        var stream = new CommandStream(world);
        var reserved = stream.Create();
        Assert.False(world.IsAlive(reserved));

        // Reserved entity must NOT increase EntityCount
        Assert.Equal(1, world.EntityCount);

        // Submit materializes the reserved entity
        stream.Submit();

        // Now EntityCount should include it
        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void RestoreState_recomputes_reserved_count_after_pending_reservation()
    {
        var world = new World();
        var live = world.CreateEmpty();
        var snapshot = world.CaptureState();

        var stream = new CommandStream(world);
        var reserved = stream.Create();

        Assert.False(world.IsAlive(reserved));
        Assert.Equal(1, world.EntityCount);

        world.RestoreState(snapshot);

        Assert.True(world.IsAlive(live));
        Assert.Equal(1, world.EntityCount);
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

        new CommandStream(world).Replay(delta);
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
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
        var stream = new CommandStream(world);

        stream.AddChild(parent, child);
        Assert.True(stream.Submit());

        Assert.True(world.TryGetParent(child, out var p));
        Assert.Equal(parent, p);

        stream.RemoveChild(child);
        Assert.True(stream.Submit());
        Assert.False(world.TryGetParent(child, out _));
    }

    [Fact]
    public void Link_skipped_for_destroyed_existing_entity()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.AddChild(parent, child);
        stream.Destroy(child);
        Assert.True(stream.Submit());

        Assert.False(world.IsAlive(child));
        // Parent should have no children since child was destroyed before AddChild applied
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
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);

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
        new CommandStream(replica).Replay(delta);

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
        new CommandStream(replica).Replay(delta);

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
        Assert.Empty(delta.CreatedEntities());
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
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
        var stream = new CommandStream(world);

        stream.AddChild(parent, child);
        var delta = await stream.SubmitAndSnapshotAsync();

        // Verify AddChild was applied
        Assert.True(world.TryGetParent(child, out var p));
        Assert.Equal(parent, p);

        // Verify delta contains AddChild command (need to check internal structure)
        Assert.NotEmpty(delta.AddChildCommands());
    }

    [Fact]
    public void Snapshot_excludes_created_destroyed_entity_from_delta()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Destroy(created);

        var delta = stream.Snapshot();

        // Created-then-destroyed entity should be released, not created
        Assert.Contains(created, delta.ReservedEntities());
        Assert.Contains(created, delta.ReleasedEntities());
        Assert.DoesNotContain(created, delta.CreatedEntities().Select(c => c.Entity));
        // Should NOT appear in DestroyedEntities (it was never alive)
        Assert.DoesNotContain(created, delta.DestroyedEntities());
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_excludes_created_destroyed_from_delta()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Destroy(created);

        var delta = await stream.SubmitAndSnapshotAsync();

        Assert.Contains(created, delta.ReservedEntities());
        Assert.Contains(created, delta.ReleasedEntities());
        Assert.DoesNotContain(created, delta.DestroyedEntities());
    }

    [Fact]
    public void Snapshot_excludes_hierarchy_for_destroyed_child()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.AddChild(parent, child);
        stream.Destroy(child);

        var delta = stream.Snapshot();

        // AddChild should be filtered out since child is destroyed
        Assert.Empty(delta.AddChildCommands());
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_excludes_hierarchy_for_destroyed_child()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.AddChild(parent, child);
        stream.Destroy(child);

        var delta = await stream.SubmitAndSnapshotAsync();

        Assert.Empty(delta.AddChildCommands());
    }

    [Fact]
    public void Submit_skips_hierarchy_intent_for_destroyed_parent_even_when_destroy_is_recorded_first()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
        var stream = new CommandStream(world);

        stream.Destroy(parent);
        stream.AddChild(parent, child);

        Assert.True(stream.Submit());
        Assert.False(world.IsAlive(parent));
        Assert.False(world.TryGetParent(child, out _));
    }

    [Fact]
    public void Snapshot_excludes_hierarchy_for_destroyed_created_child()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var stream = new CommandStream(world);

        var child = stream.Create();
        stream.Add(child, new Position(1, 2));
        stream.AddChild(parent, child);
        stream.Destroy(child);

        var delta = stream.Snapshot();

        // AddChild for created-then-destroyed child should be filtered
        Assert.Empty(delta.AddChildCommands());
        Assert.Contains(child, delta.ReleasedEntities());
    }

    // ══════════════════════════════════════════════════════════—
    // FrameDelta structure & properties
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Snapshot_DeltaCount_reflects_all_recorded_commands()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Add(created, new Velocity(3, 4));
        stream.Set(existing, new Position(5, 6));
        stream.Remove<Velocity>(existing);

        var delta = stream.Snapshot();
        // Created entity -- Reserved + Created with 2 components
        // Existing entity -- 1 Set + 1 Remove
        // Total: 1 Reserved + 1 Created + 1 Set + 1 Remove = 4
        Assert.Equal(4, delta.DeltaCount);
    }

    [Fact]
    public void Snapshot_HasEntity_for_created_entity()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        var delta = stream.Snapshot();

        Assert.True(delta.HasEntity(e));
    }

    [Fact]
    public void Snapshot_HasEntity_for_set_target()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.Set(e, new Position(3, 4));
        var delta = stream.Snapshot();

        Assert.True(delta.HasEntity(e));
    }

    [Fact]
    public void Snapshot_HasEntity_for_destroyed_existing()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.Destroy(e);
        var delta = stream.Snapshot();

        Assert.True(delta.HasEntity(e));
    }

    [Fact]
    public void Snapshot_HasEntity_false_for_unknown()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        var delta = stream.Snapshot();

        Assert.False(delta.HasEntity(new Entity(9999, 1)));
    }

    [Fact]
    public void Snapshot_destroy_uses_recorded_entity_version()
    {
        var world = new World();
        var stale = world.Create(new Position(1, 1));
        var stream = new CommandStream(world);

        stream.Destroy(stale);
        world.Destroy(stale);
        var recycled = world.Create(new Position(2, 2));

        Assert.Equal(stale.Id, recycled.Id);
        Assert.NotEqual(stale.Version, recycled.Version);

        var delta = stream.Snapshot();
        Assert.Contains(stale, delta.DestroyedEntities());
        Assert.DoesNotContain(recycled, delta.DestroyedEntities());
    }

    [Fact]
    public void Snapshot_IsEmpty_returns_true_for_empty_stream()
    {
        var world = new World();
        var stream = new CommandStream(world);

        // Nothing recorded -- delta should be empty
        var delta = stream.Snapshot();
        Assert.True(delta.IsEmpty);
        Assert.Equal(0, delta.DeltaCount);
    }

    [Fact]
    public void Snapshot_destroy_preserves_unknown_entity_handle()
    {
        var world = new World();
        var unknown = new Entity(9999, 1);
        var stream = new CommandStream(world);

        stream.Destroy(unknown);
        var delta = stream.Snapshot();

        Assert.Contains(unknown, delta.DestroyedEntities());
    }

    // ══════════════════════════════════════════════════════════—
    // Cross-world replay
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void CrossWorld_replay_created_entity_with_multiple_components()
    {
        var source = new World();
        var stream = new CommandStream(source);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Add(e, new Velocity(3, 4));
        stream.Add(e, new Health(100));
        var delta = stream.Snapshot();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

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
        var stream = new CommandStream(source);

        stream.Set(e, new Position(30, 40));
        var delta = stream.Snapshot();

        var replica = new World();
        var replicaE = replica.Create(new Position(10, 20));
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.TryGet(replicaE, out Position p));
        Assert.Equal(new Position(30, 40), p);
    }

    [Fact]
    public void CrossWorld_replay_existing_entity_add_and_remove()
    {
        var source = new World();
        var e = source.Create(new Position(1, 1));
        var stream = new CommandStream(source);

        stream.Add(e, new Velocity(5, 6));
        stream.Remove<Position>(e);
        var delta = stream.Snapshot();

        var replica = new World();
        var replicaE = replica.Create(new Position(1, 1));
        new CommandStream(replica).Replay(delta);

        Assert.False(replica.TryGet<Position>(replicaE, out _));
        Assert.True(replica.TryGet(replicaE, out Velocity v));
        Assert.Equal(new Velocity(5, 6), v);
    }

    [Fact]
    public void CrossWorld_replay_destroy_existing_entity()
    {
        var source = new World();
        var e = source.Create(new Position(1, 2));
        var stream = new CommandStream(source);

        stream.Destroy(e);
        var delta = stream.Snapshot();

        var replica = new World();
        var replicaE = replica.Create(new Position(1, 2));
        new CommandStream(replica).Replay(delta);

        Assert.False(replica.IsAlive(replicaE));
    }

    [Fact]
    public void CrossWorld_replay_link_and_unlink()
    {
        var source = new World();
        var parent = source.CreateEmpty();
        var child = source.CreateEmpty();
        var stream = new CommandStream(source);

        stream.AddChild(parent, child);
        var delta = stream.Snapshot();

        var replica = new World();
        var replicaParent = replica.CreateEmpty();
        var replicaChild = replica.CreateEmpty();
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.TryGetParent(replicaChild, out var p));
        Assert.Equal(replicaParent, p);

        var stream2 = new CommandStream(source);
        stream2.RemoveChild(child);
        var delta2 = stream2.Snapshot();
        new CommandStream(replica).Replay(delta2);
        Assert.False(replica.TryGetParent(replicaChild, out _));
    }

    [Fact]
    public void CrossWorld_replay_mixed_created_and_existing()
    {
        var source = new World();
        var existing = source.Create(new Position(10, 20));
        var stream = new CommandStream(source);

        var created = stream.Create();
        stream.Add(created, new Position(100, 200));
        stream.Set(existing, new Position(30, 40));
        stream.Add(existing, new Velocity(5, 6));
        var delta = stream.Snapshot();

        var replica = new World();
        var replicaExisting = replica.Create(new Position(10, 20));
        new CommandStream(replica).Replay(delta);

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
        var stream = new CommandStream(source);

        stream.Destroy(e);
        var delta = stream.Snapshot();

        source.Destroy(e);
        var recycled = source.Create(new Position(99, 99));

        Assert.Equal(e.Id, recycled.Id);

        var replica = new World();
        var replicaE = replica.Create(new Position(1, 2));
        new CommandStream(replica).Replay(delta);

        Assert.False(replica.IsAlive(replicaE));
    }

    [Fact]
    public void CrossWorld_replay_batch_created_entities_query_visible()
    {
        const int N = 200;
        var source = new World();
        var stream = new CommandStream(source);
        var entities = new Entity[N];

        for (var i = 0; i < N; i++)
        {
            entities[i] = stream.Create();
            stream.Add(entities[i], new Position(i, i + 1));
            if ((i & 1) == 0)
                stream.Add(entities[i], new Velocity(i * 10, i * 20));
        }
        var delta = stream.Snapshot();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        var query = replica.Query(new QueryDescription().With<Position>());
        var count = 0;
        foreach (var chunk in query.GetChunks())
            count += chunk.Count;
        Assert.Equal(N, count);

        var posVelQuery = replica.Query(new QueryDescription().With<Position>().With<Velocity>());
        var posVelCount = 0;
        foreach (var chunk in posVelQuery.GetChunks())
            posVelCount += chunk.Count;
        Assert.Equal(N / 2, posVelCount);
    }

    [Fact]
    public void CrossWorld_replay_create_empty_entity()
    {
        // Create an entity with no components -- should produce an empty CreatedEntity.
        var source = new World();
        var stream = new CommandStream(source);

        var e = stream.Create();
        // No components -- empty entity is now fully supported
        var delta = stream.Snapshot();

        Assert.NotEmpty(delta.CreatedEntities());
        Assert.True(delta.HasEntity(e));

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.IsAlive(e));
        Assert.False(replica.TryGet<Position>(e, out _));
    }

    [Fact]
    public void CrossWorld_replay_create_then_destroy_releases()
    {
        var source = new World();
        var stream = new CommandStream(source);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Destroy(e);
        var delta = stream.Snapshot();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        Assert.False(replica.IsAlive(e));
    }

    [Fact]
    public void CrossWorld_replay_snapshot_submit_produces_same_result_as_source()
    {
        var source = new World();
        var existing = source.Create(new Position(10, 20), new Velocity(5, 6));
        var stream = new CommandStream(source);

        var created1 = stream.Create();
        stream.Add(created1, new Position(100, 200));
        stream.Add(created1, new Velocity(10, 20));

        var created2 = stream.Create();
        stream.Add(created2, new Health(50));

        stream.Set(existing, new Position(30, 40));
        stream.Remove<Velocity>(existing);

        var delta = stream.Snapshot();
        stream.Submit();

        var replica = new World();
        var replicaExisting = replica.Create(new Position(10, 20), new Velocity(5, 6));
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.IsAlive(created1));
        Assert.True(replica.TryGet(created1, out Position cp1));
        Assert.Equal(new Position(100, 200), cp1);

        Assert.True(replica.IsAlive(created2));
        Assert.True(replica.TryGet(created2, out Health ch));
        Assert.Equal(new Health(50), ch);

        Assert.True(replica.TryGet(replicaExisting, out Position ep));
        Assert.Equal(new Position(30, 40), ep);
        Assert.False(replica.TryGet<Velocity>(replicaExisting, out _));

        // Source state matches
        Assert.True(source.TryGet(created1, out Position sp));
        Assert.Equal(new Position(100, 200), sp);
        Assert.False(source.TryGet<Velocity>(existing, out _));
    }

    [Fact]
    public void CrossWorld_replay_snapshot_after_submit_is_empty()
    {
        var world = new World();
        var stream = new CommandStream(world);

        stream.Create();
        stream.Add(stream.Create(), new Position(1, 2));
        stream.Submit();

        // Snapshot after submit on a fresh (reused) buffer should be empty
        var delta = stream.Snapshot();
        Assert.True(delta.IsEmpty);
    }

    // ══════════════════════════════════════════════════════════—
    // Multiple operations on same entity/component
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Multiple_Set_on_same_existing_component_last_wins()
    {
        var world = new World();
        var e = world.Create(new Position(1, 1));
        var stream = new CommandStream(world);

        stream.Set(e, new Position(10, 10));
        stream.Set(e, new Position(20, 20));
        stream.Set(e, new Position(30, 30));
        Assert.True(stream.Submit());

        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(30, 30), p);
    }

    [Fact]
    public void Add_then_Remove_same_component_existing_entity()
    {
        var world = new World();
        var e = world.CreateEmpty();
        var stream = new CommandStream(world);

        stream.Add(e, new Position(1, 2));
        stream.Remove<Position>(e);
        Assert.True(stream.Submit());

        // Net effect: no Position
        Assert.True(world.IsAlive(e));
        Assert.False(world.TryGet<Position>(e, out _));
    }

    [Fact]
    public void Set_then_second_Set_last_wins()
    {
        var world = new World();
        var e = world.Create(new Position(1, 1));
        var stream = new CommandStream(world);

        stream.Set(e, new Position(99, 99));
        stream.Set(e, new Position(5, 5));
        Assert.True(stream.Submit());

        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(5, 5), p);
    }

    [Fact]
    public void Remove_nonexistent_component_noop()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.Remove<Velocity>(e);
        stream.Remove<Health>(e);
        Assert.True(stream.Submit());

        // Entity still alive with its original component
        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
    }

    [Fact]
    public void Destroy_pending_entity_releases_reservation_and_skips_materialization()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Destroy(e); // Cancel pending entity
        Assert.True(stream.Submit());

        // Entity was reserved then released -- never became alive
        Assert.False(world.IsAlive(e));

        // Verify the ID is reusable (not leaked)
        var fresh = world.Create(new Position(99, 99));
        Assert.Equal(e.Id, fresh.Id);
    }

    [Fact]
    public void Add_then_Add_same_component_pending_entity_merges_into_create()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Add(e, new Position(3, 4)); // Second Add overwrites via batch merge
        Assert.True(stream.Submit());

        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(3, 4), p);
    }

    [Fact]
    public void Set_on_pending_entity_merges_into_create_entry()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Set(e, new Position(99, 99));
        Assert.True(stream.Submit());

        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(99, 99), p);
    }

    // ══════════════════════════════════════════════════════════—
    // Batch operations
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void CreateMany_submit_creates_entities_with_components()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[4];

        stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());

        for (var i = 0; i < entities.Length; i++)
            Assert.False(world.IsAlive(entities[i]));

        Assert.True(stream.Submit());

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(world.IsAlive(entities[i]));
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
        }
    }

    [Fact]
    public void CreateMany_snapshot_replays_created_entities()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var entities = new Entity[4];

        stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
        var delta = stream.Snapshot();

        for (var i = 0; i < entities.Length; i++)
            Assert.False(source.IsAlive(entities[i]));

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(replica.IsAlive(entities[i]));
            Assert.True(replica.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
        }
    }

    [Fact]
    public async Task CreateMany_submit_and_snapshot_async_includes_created_entities()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var entities = new Entity[4];

        stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
        var delta = await stream.SubmitAndSnapshotAsync();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(source.IsAlive(entities[i]));
            Assert.True(replica.IsAlive(entities[i]));
            Assert.True(replica.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
        }
    }

    [Fact]
    public void CreateMany_deferred_submit_resolves_self_references()
    {
        var world = new World();
        var stream = new CommandStream(world) { DeferredEntities = true };
        var placeholders = new Entity[4];

        stream.CreateMany<SelfRef, SelfRefCreateManyWriter>(placeholders, new SelfRefCreateManyWriter());

        for (var i = 0; i < placeholders.Length; i++)
            Assert.True(placeholders[i].IsPlaceholder);

        Assert.True(stream.Submit());

        var count = 0;
        foreach (var chunk in world.Query(new QueryDescription().With<SelfRef>()).GetChunks())
        {
            var entities = chunk.GetEntities();
            var refs = chunk.GetSpan<SelfRef>();
            for (var i = 0; i < chunk.Count; i++)
            {
                Assert.Equal(entities[i], refs[i].Target);
                count++;
            }
        }
        Assert.Equal(4, count);
    }

    [Fact]
    public void CreateMany_deferred_snapshot_replay_resolves_self_references()
    {
        var source = new World();
        var stream = new CommandStream(source) { DeferredEntities = true };
        var placeholders = new Entity[4];

        stream.CreateMany<SelfRef, SelfRefCreateManyWriter>(placeholders, new SelfRefCreateManyWriter());
        var delta = stream.Snapshot();
        stream.Clear();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        var count = 0;
        foreach (var chunk in replica.Query(new QueryDescription().With<SelfRef>()).GetChunks())
        {
            var entities = chunk.GetEntities();
            var refs = chunk.GetSpan<SelfRef>();
            for (var i = 0; i < chunk.Count; i++)
            {
                Assert.Equal(entities[i], refs[i].Target);
                count++;
            }
        }
        Assert.Equal(4, count);
    }

    [Fact]
    public void CreateMany_supports_eight_components()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[3];

        stream.CreateMany<Position, Velocity, Health, SignalPayloadField, C4, C5, C6, C7, EightComponentCreateManyWriter>(entities, new EightComponentCreateManyWriter());
        Assert.True(stream.Submit());

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
            Assert.True(world.TryGet(entities[i], out C7 c7));
            Assert.Equal(new C7(i + 8), c7);
        }
    }

    [Fact]
    public void CreateMany_duplicate_component_types_throws()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[3];

        stream.CreateMany<Position, Position, DuplicatePositionCreateManyWriter>(
            entities, new DuplicatePositionCreateManyWriter());
        Assert.Throws<InvalidOperationException>(() => stream.Submit());
    }

    [Fact]
    public void CreateMany_then_set_on_same_entity_throws()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[3];

        stream.CreateMany<Position, Velocity, PositionVelocityCreateManyWriter>(
            entities, new PositionVelocityCreateManyWriter());
        stream.Set(entities[1], new Velocity(99, 100));
        Assert.Throws<InvalidOperationException>(() => stream.Submit());
    }

    [Fact]
    public void ParallelCommandStream_CreateMany_creates_entities()
    {
        var world = new World();
        var stream = new ParallelCommandStream(world);
        var entities = new Entity[4];

        stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
        Assert.True(stream.Submit());

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
        }
    }

    [Fact]
    public void CreateMany_then_remove_on_same_entity_throws()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[5];

        stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
        stream.Remove<Position>(entities[2]);
        Assert.Throws<InvalidOperationException>(() => stream.Submit());
    }

    [Fact]
    public void CreateMany_then_destroy_one_entity_materializes_only_survivors()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[6];

        stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
        // Destroy one entity in the batch: its batch is cancelled, fast path
        // skips it and materializes only live entities at consecutive rows.
        stream.Destroy(entities[3]);
        Assert.True(stream.Submit());

        Assert.False(world.IsAlive(entities[3]));
        var aliveCount = 0;
        for (var i = 0; i < entities.Length; i++)
        {
            if (i == 3) continue;
            Assert.True(world.IsAlive(entities[i]));
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
            aliveCount++;
        }
        Assert.Equal(5, aliveCount);
    }

    [Fact]
    public void CreateMany_empty_span_is_noop()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var entities = Array.Empty<Entity>();

        stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
        Assert.False(stream.Submit()); // no commands recorded

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void CreateMany_interleaved_with_single_create_materializes_all()
    {
        var world = new World();
        var stream = new CommandStream(world);

        // Single create before CreateMany group
        var single1 = stream.Create();
        stream.Add(single1, new Velocity(10, 20));

        var batch = new Entity[4];
        stream.CreateMany<Position, PositionCreateManyWriter>(batch, new PositionCreateManyWriter());

        // Single create after CreateMany group
        var single2 = stream.Create();
        stream.Add(single2, new Health(99));

        Assert.True(stream.Submit());

        // All entities materialized
        Assert.True(world.IsAlive(single1));
        Assert.True(world.IsAlive(single2));
        for (var i = 0; i < batch.Length; i++)
            Assert.True(world.IsAlive(batch[i]));

        // Verify values
        Assert.True(world.TryGet(single1, out Velocity v));
        Assert.Equal(new Velocity(10, 20), v);
        Assert.True(world.TryGet(single2, out Health h));
        Assert.Equal(new Health(99), h);
        for (var i = 0; i < batch.Length; i++)
        {
            Assert.True(world.TryGet(batch[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
        }
    }

    [Fact]
    public void Batch_create_N_entities_with_varied_components()
    {
        const int N = 128;
        var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[N];

        for (var i = 0; i < N; i++)
        {
            entities[i] = stream.Create();
            stream.Add(entities[i], new Position(i, i + 1));
            if (i % 3 == 0) stream.Add(entities[i], new Velocity(i, i));
            if (i % 5 == 0) stream.Add(entities[i], new Health(100 + i));
        }
        Assert.True(stream.Submit());

        for (var i = 0; i < N; i++)
        {
            Assert.True(world.IsAlive(entities[i]));
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
        }

        var allQuery = world.Query(new QueryDescription());
        var totalCount = 0;
        foreach (var chunk in allQuery.GetChunks())
            totalCount += chunk.Count;
        Assert.Equal(N, totalCount);
    }

    [Fact]
    public void Batch_mixed_operations_produces_correct_world_state()
    {
        var world = new World();
        var entities = new Entity[50];
        for (var i = 0; i < 50; i++)
            entities[i] = world.Create(new Position(i, i + 1));

        var stream = new CommandStream(world);
        for (var i = 0; i < 50; i++)
        {
            if ((i & 1) == 0)
            {
                stream.Set(entities[i], new Position(i + 10, i + 20));
                if ((i & 3) == 0) stream.Remove<Velocity>(entities[i]);
                if ((i & 7) == 0) stream.Destroy(entities[i]);
            }
            else
            {
                var e = stream.Create();
                stream.Add(e, new Position(i + 30, i + 40));
                if ((i & 3) == 1) stream.Remove<Position>(e);
                if ((i & 7) == 1) stream.Destroy(e);
            }
        }
        Assert.True(stream.Submit());

        // Spot checks
        Assert.False(world.IsAlive(entities[0])); // destroyed (i=0, i&7==0)
        Assert.True(world.IsAlive(entities[2]));
        Assert.True(world.TryGet(entities[2], out Position p2));
        Assert.Equal(new Position(12, 22), p2);
    }

    // ══════════════════════════════════════════════════════════—
    // SubmitAndSnapshotAsync deep scenarios
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public async Task SubmitAndSnapshotAsync_with_existing_entity_ops()
    {
        var world = new World();
        var e = world.Create(new Position(10, 20), new Velocity(5, 6));
        var stream = new CommandStream(world);

        stream.Set(e, new Position(30, 40));
        stream.Remove<Velocity>(e);

        var task = stream.SubmitAndSnapshotAsync();
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(30, 40), p);
        Assert.False(world.TryGet<Velocity>(e, out _));

        var delta = await task;
        Assert.NotEmpty(delta.SetCommands());
        Assert.NotEmpty(delta.RemoveCommands());
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_with_destroy()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.Destroy(e);
        var delta = await stream.SubmitAndSnapshotAsync();

        Assert.False(world.IsAlive(e));
        Assert.Single(delta.DestroyedEntities());
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_delta_can_be_replayed_in_another_world()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Add(created, new Velocity(3, 4));
        stream.Set(existing, new Position(99, 99));

        var delta = await stream.SubmitAndSnapshotAsync();

        var replica = new World();
        var rExisting = replica.Create(new Position(0, 0));
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.IsAlive(created));
        Assert.True(replica.TryGet(created, out Position cp));
        Assert.Equal(new Position(1, 2), cp);
        Assert.True(replica.TryGet(created, out Velocity cv));
        Assert.Equal(new Velocity(3, 4), cv);
        Assert.True(replica.TryGet(rExisting, out Position ep));
        Assert.Equal(new Position(99, 99), ep);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_skips_existing_entity_commands_that_become_stale_before_consume()
    {
        var source = new World();
        var replica = new World();

        var sourceVictim = source.Create(new Position(1, 1));
        var replicaVictim = replica.Create(new Position(1, 1));

        var stream = new CommandStream(source);
        stream.Set(sourceVictim, new Position(9, 9));

        source.Destroy(sourceVictim);
        var sourceRecycled = source.Create(new Position(2, 2));

        replica.Destroy(replicaVictim);
        var replicaRecycled = replica.Create(new Position(2, 2));

        var delta = await stream.SubmitAndSnapshotAsync();
        new CommandStream(replica).Replay(delta);

        Assert.True(source.TryGet(sourceRecycled, out Position srcPosition));
        Assert.True(replica.TryGet(replicaRecycled, out Position replicaPosition));
        Assert.Equal(new Position(2, 2), srcPosition);
        Assert.Equal(new Position(2, 2), replicaPosition);
        AssertIdenticalWorlds(source, replica, "SubmitAndSnapshotAsync delayed stale existing Set should be skipped");
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_with_clone_includes_clone_in_delta()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4));
        var stream = new CommandStream(world);

        var clone = stream.Clone(source);
        var delta = await stream.SubmitAndSnapshotAsync();

        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position cp));
        Assert.Equal(new Position(1, 2), cp);

        // Delta should contain the clone
        Assert.True(delta.HasEntity(clone));
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_mixed_crud_scenario()
    {
        var world = new World();
        var entities = new Entity[50];
        for (var i = 0; i < 50; i++)
            entities[i] = world.Create(new Position(i, i + 1));

        var stream = new CommandStream(world);
        for (var i = 0; i < 50; i++)
        {
            if ((i & 1) == 0)
            {
                stream.Set(entities[i], new Position(i + 10, i + 20));
                if ((i & 7) == 0) stream.Destroy(entities[i]);
            }
            else
            {
                var e = stream.Create();
                stream.Add(e, new Position(i + 30, i + 40));
                if ((i & 3) == 1) stream.Remove<Position>(e);
            }
        }
        // AddChild and RemoveChild across different entities in same batch
        var p1 = world.CreateEmpty();
        var c1 = world.CreateEmpty();
        var c2 = world.CreateEmpty();
        stream.AddChild(p1, c1);
        stream.RemoveChild(c2); // RemoveChild on unlinked entity is tested separately

        var delta = await stream.SubmitAndSnapshotAsync();

        Assert.False(world.IsAlive(entities[0])); // i=0 destroyed
        Assert.Equal(new Position(12, 22), world.TryGet(entities[2], out Position p2) ? p2 : default);
        Assert.NotEmpty(delta.AddChildCommands());
        Assert.True(delta.DeltaCount > 0);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_stream_reusable_multiple_cycles()
    {
        var world = new World();
        var stream = new CommandStream(world);

        for (var cycle = 0; cycle < 5; cycle++)
        {
            var e = stream.Create();
            stream.Add(e, new Position(cycle, cycle + 1));
            var delta = await stream.SubmitAndSnapshotAsync();

            Assert.True(world.IsAlive(e));
            Assert.True(world.TryGet(e, out Position p));
            Assert.Equal(new Position(cycle, cycle + 1), p);
            Assert.True(delta.HasEntity(e));
        }
    }

    [Fact]
    public async Task BUG_async_submit_preflights_invalid_component_before_worker_handoff()
    {
        var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.Set(existing, new Velocity(5, 6));

        void SubmitInvalidFrame() => _ = stream.SubmitAndSnapshotAsync();
        Assert.Throws<InvalidOperationException>(SubmitInvalidFrame);

        Assert.Equal(1, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.False(world.TryGet<Velocity>(existing, out _));
        Assert.True(WorldValidator.Validate(world).IsValid);

        var replacement = stream.Create();
        stream.Add(replacement, new Velocity(7, 8));
        var delta = await stream.SubmitAndSnapshotAsync();

        Assert.True(world.IsAlive(replacement));
        Assert.Equal(new Velocity(7, 8), world.Get<Velocity>(replacement));
        Assert.True(delta.HasEntity(replacement));
    }

    // ══════════════════════════════════════════════════════════—
    // SubmitAndSnapshotIntoAsync — async submit+snapshot into existing target
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_submits_and_writes_to_target()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Set(existing, new Position(3, 4));

        await stream.SubmitAndSnapshotIntoAsync(target);

        // Changes applied to world
        Assert.True(world.IsAlive(created));
        Assert.True(world.TryGet(created, out Position p1));
        Assert.Equal(new Position(1, 2), p1);
        Assert.True(world.TryGet(existing, out Position p2));
        Assert.Equal(new Position(3, 4), p2);

        // Target contains the delta
        Assert.True(target.DeltaCount > 0);
        Assert.True(target.HasEntity(created));

        // Target is replayable
        var replica = new World();
        var replicaExisting = replica.Create(new Position(0, 0));
        new CommandStream(replica).Replay(target);
        Assert.True(replica.IsAlive(created));
        Assert.True(replica.TryGet(existing, out Position rp));
        Assert.Equal(new Position(3, 4), rp);
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_clears_target_for_empty_stream()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        // Prime target with some data
        var dummy = stream.Create();
        stream.Add(dummy, new Position(1, 2));
        await stream.SubmitAndSnapshotIntoAsync(target);
        Assert.True(target.DeltaCount > 0);
        Assert.True(target.HasEntity(dummy));

        // Clear stream and call again — target should be cleared
        var stream2 = new CommandStream(world);
        await stream2.SubmitAndSnapshotIntoAsync(target);
        Assert.Empty(target.CreatedEntities());
        Assert.Empty(target.DestroyedEntities());
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_stream_reusable_after_call()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        stream.Create();
        await stream.SubmitAndSnapshotIntoAsync(target);

        // Should be able to use stream again
        var e = stream.Create();
        stream.Add(e, new Position(5, 6));
        await stream.SubmitAndSnapshotIntoAsync(target);

        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(5, 6), p);
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_includes_hierarchy_in_delta()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        stream.AddChild(parent, child);
        await stream.SubmitAndSnapshotIntoAsync(target);

        Assert.True(world.TryGetParent(child, out var p));
        Assert.Equal(parent, p);
        Assert.NotEmpty(target.AddChildCommands());
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_with_existing_entity_ops()
    {
        var world = new World();
        var e = world.Create(new Position(10, 20), new Velocity(5, 6));
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        stream.Set(e, new Position(30, 40));
        stream.Remove<Velocity>(e);

        var task = stream.SubmitAndSnapshotIntoAsync(target);
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(30, 40), p);
        Assert.False(world.TryGet<Velocity>(e, out _));

        await task;
        Assert.NotEmpty(target.SetCommands());
        Assert.NotEmpty(target.RemoveCommands());
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_with_destroy()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        stream.Destroy(e);
        await stream.SubmitAndSnapshotIntoAsync(target);

        Assert.False(world.IsAlive(e));
        Assert.Single(target.DestroyedEntities());
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_delta_can_be_replayed()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Add(created, new Velocity(3, 4));
        stream.Set(existing, new Position(99, 99));

        await stream.SubmitAndSnapshotIntoAsync(target);

        var replica = new World();
        var rExisting = replica.Create(new Position(0, 0));
        new CommandStream(replica).Replay(target);

        Assert.True(replica.IsAlive(created));
        Assert.True(replica.TryGet(created, out Position cp));
        Assert.Equal(new Position(1, 2), cp);
        Assert.True(replica.TryGet(created, out Velocity cv));
        Assert.Equal(new Velocity(3, 4), cv);
        Assert.True(replica.TryGet(rExisting, out Position ep));
        Assert.Equal(new Position(99, 99), ep);
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_mixed_crud_scenario()
    {
        var world = new World();
        var entities = new Entity[30];
        for (var i = 0; i < 30; i++)
            entities[i] = world.Create(new Position(i, i + 1));
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        for (var i = 0; i < 30; i++)
        {
            if ((i & 1) == 0)
            {
                stream.Set(entities[i], new Position(i + 10, i + 20));
                if ((i & 7) == 0) stream.Destroy(entities[i]);
            }
            else
            {
                var e = stream.Create();
                stream.Add(e, new Position(i + 30, i + 40));
                if ((i & 3) == 1) stream.Remove<Position>(e);
            }
        }

        await stream.SubmitAndSnapshotIntoAsync(target);
        Assert.False(world.IsAlive(entities[0])); // i=0 destroyed
        Assert.Equal(new Position(12, 22), world.TryGet(entities[2], out Position p2) ? p2 : default);
        Assert.True(target.DeltaCount > 0);
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_multiple_cycles_reuses_target()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        for (var cycle = 0; cycle < 5; cycle++)
        {
            var e = stream.Create();
            stream.Add(e, new Position(cycle, cycle + 1));
            await stream.SubmitAndSnapshotIntoAsync(target);

            Assert.True(world.IsAlive(e));
            Assert.True(world.TryGet(e, out Position p));
            Assert.Equal(new Position(cycle, cycle + 1), p);
            Assert.True(target.HasEntity(e));
        }
    }

    [Fact]
    public async Task BUG_async_into_preflights_invalid_component_before_worker_handoff()
    {
        var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        var seed = stream.Create();
        stream.Add(seed, new Position(3, 4));
        await stream.SubmitAndSnapshotIntoAsync(target);
        var originalWire = target.AsSpan().ToArray();

        var pending = stream.Create();
        stream.Add(pending, new Velocity(5, 6));
        stream.Set(existing, new Velocity(7, 8));

        void SubmitInvalidFrame() => _ = stream.SubmitAndSnapshotIntoAsync(target);
        Assert.Throws<InvalidOperationException>(SubmitInvalidFrame);

        Assert.Equal(originalWire, target.AsSpan().ToArray());
        Assert.Equal(2, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.False(world.TryGet<Velocity>(existing, out _));
        Assert.True(WorldValidator.Validate(world).IsValid);

        var replacement = stream.Create();
        stream.Add(replacement, new Velocity(9, 10));
        await stream.SubmitAndSnapshotIntoAsync(target);

        Assert.True(world.IsAlive(replacement));
        Assert.Equal(new Velocity(9, 10), world.Get<Velocity>(replacement));
        Assert.True(target.HasEntity(replacement));
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_with_clone_includes_clone_in_delta()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4));
        var stream = new CommandStream(world);
        var target = new FrameDelta();

        var clone = stream.Clone(source);
        await stream.SubmitAndSnapshotIntoAsync(target);

        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position cp));
        Assert.Equal(new Position(1, 2), cp);
        Assert.True(target.HasEntity(clone));
    }

    [Fact]
    public async Task SubmitAndSnapshotIntoAsync_overlapping_tasks_stay_correct()
    {
        // Caller does NOT await between frames: previous task may still be running
        // when the next SubmitAndSnapshotIntoAsync fires. Must not corrupt state.
        var world = new World();
        var stream = new CommandStream(world);
        var targets = new FrameDelta[5];

        var tasks = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            var e = world.CreateEmpty();
            stream.Add(e, new Position(i, i));
            stream.Destroy(e);
            targets[i] = new FrameDelta();
            tasks.Add(stream.SubmitAndSnapshotIntoAsync(targets[i]));
        }

        for (var i = 0; i < tasks.Count; i++)
        {
            await tasks[i];
            Assert.True(targets[i].DeltaCount > 0);
        }
    }

    // ══════════════════════════════════════════════════════════—
    // Clone deep scenarios
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Clone_then_set_in_same_buffer_replays_correctly()
    {
        var source = new World();
        var entity = source.Create(new Position(1, 2), new Velocity(3, 4));
        var stream = new CommandStream(source);

        var clone = stream.Clone(entity);
        stream.Set(clone, new Position(99, 99));
        stream.Add(clone, new Health(100));
        var delta = stream.Snapshot();

        var replica = new World();
        var rEntity = replica.Create(new Position(1, 2), new Velocity(3, 4));
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.IsAlive(clone));
        Assert.True(replica.TryGet(clone, out Position p));
        Assert.Equal(new Position(99, 99), p);
        Assert.True(replica.TryGet(clone, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);
        Assert.True(replica.TryGet(clone, out Health h));
        Assert.Equal(new Health(100), h);
    }

    [Fact]
    public void Clone_then_destroy_in_same_buffer_releases_not_creates()
    {
        var source = new World();
        var entity = source.Create(new Position(1, 2));
        var stream = new CommandStream(source);

        var clone = stream.Clone(entity);
        stream.Destroy(clone);
        var delta = stream.Snapshot();

        // Clone-then-destroy -- released, not created
        Assert.Empty(delta.CreatedEntities());
        Assert.Empty(delta.DestroyedEntities());
        Assert.Contains(clone, delta.ReservedEntities());
        Assert.Contains(clone, delta.ReleasedEntities());

        var replica = new World();
        var rEntity = replica.Create(new Position(1, 2));
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.IsAlive(rEntity));
        Assert.False(replica.IsAlive(clone));
    }

    [Fact]
    public void Clone_deep_with_children_replay_cross_world()
    {
        var source = new World();
        var parent = source.Create(new Position(1, 2));
        var child1 = source.Create(new Velocity(3, 4));
        var child2 = source.Create(new Health(100));
        source.AddChild(parent, child1);
        source.AddChild(parent, child2);
        var stream = new CommandStream(source);

        var clone = stream.Clone(parent);
        var delta = stream.Snapshot();

        var replica = new World();
        var rParent = replica.Create(new Position(1, 2));
        var rChild1 = replica.Create(new Velocity(3, 4));
        var rChild2 = replica.Create(new Health(100));
        replica.AddChild(rParent, rChild1);
        replica.AddChild(rParent, rChild2);
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.IsAlive(clone));
        Assert.True(replica.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);

        var cloneChildren = replica.EnumerateChildren(clone).ToChildList();
        Assert.Equal(2, cloneChildren.Count);
    }

    [Fact]
    public void Clone_mixed_with_other_commands_replay()
    {
        var source = new World();
        var existing = source.Create(new Position(10, 20));
        var toClone = source.Create(new Velocity(5, 6), new Health(100));
        var stream = new CommandStream(source);

        var clone = stream.Clone(toClone);
        var created = stream.Create();
        stream.Add(created, new Position(100, 200));
        stream.Set(existing, new Position(30, 40));
        var delta = stream.Snapshot();

        var replica = new World();
        var rExisting = replica.Create(new Position(10, 20));
        var rToClone = replica.Create(new Velocity(5, 6), new Health(100));
        new CommandStream(replica).Replay(delta);

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

        // Existing entity set
        Assert.True(replica.TryGet(rExisting, out Position ep));
        Assert.Equal(new Position(30, 40), ep);
    }

    [Fact]
    public void Remove_on_pending_clone_removes_component()
    {
        // Remove on a pending entity (created in same batch, including clones)
        // now correctly removes the component from the creation batch.
        var source = new World();
        var entity = source.Create(new Position(1, 2), new Velocity(3, 4));
        var stream = new CommandStream(source);

        var clone = stream.Clone(entity);
        stream.Remove<Velocity>(clone); // Remove Velocity from pending clone
        Assert.True(stream.Submit());

        // Clone should have Position but NOT Velocity
        Assert.True(source.IsAlive(clone));
        Assert.True(source.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.False(source.TryGet<Velocity>(clone, out _));
    }

    [Fact]
    public void BUG_pending_clone_copies_from_resized_batch_buffer()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var source = stream.Create();
        stream.Add(source, new SignalPayloadField(11, 12, 13));
        stream.Add(source, new Position(1, 2));

        // BatchBuf grows from 4096 bytes. Source payloads consume 32 bytes;
        // 505 filler Position components bring the buffer to 4072 bytes.
        // Copying the pending source then writes Position at 4072..4080 and
        // forces a resize when copying the 24-byte SignalPayloadField.
        for (var i = 0; i < 505; i++)
        {
            var filler = stream.Create();
            stream.Add(filler, new Position(i, -i));
        }

        var clone = stream.Clone(source);
        Assert.True(stream.Submit());

        Assert.True(world.TryGet(clone, out Position position));
        Assert.Equal(new Position(1, 2), position);
        Assert.True(world.TryGet(clone, out SignalPayloadField payload));
        Assert.Equal(new SignalPayloadField(11, 12, 13), payload);
    }

    [Fact]
    public void Clone_deep_hierarchy_three_levels_replay()
    {
        var source = new World();
        var grandparent = source.Create(new Position(1, 2));
        var parent = source.Create(new Velocity(3, 4));
        var child = source.Create(new Health(100));
        source.AddChild(grandparent, parent);
        source.AddChild(parent, child);
        var stream = new CommandStream(source);

        var clone = stream.Clone(grandparent);
        Assert.True(stream.Submit());

        Assert.True(source.IsAlive(clone));
        Assert.True(source.TryGet(clone, out Position p));
        Assert.Equal(new Position(1, 2), p);

        // Clone should have nested children
        var cloneChildren = source.EnumerateChildren(clone).ToChildList();
        Assert.Single(cloneChildren);
        var grandChildren = source.EnumerateChildren(cloneChildren[0]).ToChildList();
        Assert.Single(grandChildren);
    }

    // ══════════════════════════════════════════════════════════—
    // Multi-frame ordered replay
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Multi_frame_ordered_replay_produces_identical_world()
    {
        // Tests that multiple frames of CommandStream operations, when snapshot
        // and replayed into a fresh world, produce the same final state.
        // ReplayCore processes ops in temporal order via packed byte buffer;
        // ComponentStore.ApplyToWorld and EmitToDelta iterate _kinds in the
        // same order, so Submit and Replay converge even with Remove+Add
        // same-type patterns in one frame.
        var source = new World();
        var deltas = new List<FrameDelta>();
        var stream = new CommandStream(source);
        var survivors = new List<Entity>();

        // Frame 1: seed world with diverse entities and hierarchy
        var a = stream.Create(); stream.Add(a, new Position(1, 2));
        var b = stream.Create(); stream.Add(b, new Position(3, 4)); stream.Add(b, new Velocity(5, 6));
        var c = stream.Create(); stream.Add(c, new Health(50));
        var d = stream.Create(); stream.Add(d, new Health(100));
        stream.AddChild(a, b);
        stream.AddChild(a, c);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: modify existing, create new
        stream.Set(a, new Position(100, 200));
        stream.Set(b, new Position(30, 40));
        stream.Remove<Velocity>(b);
        var e = stream.Create(); stream.Add(e, new Position(50, 60));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 3: component cycling, create new
        stream.Add(d, new Position(55, 66));
        var f = stream.Create(); stream.Add(f, new Health(1));
        stream.AddChild(a, f);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 4: modify, destroy
        stream.Set(d, new Position(88, 99));  // Use Set instead of Remove+Add pattern
        stream.Destroy(b);
        stream.Add(e, new Velocity(1, 1));
        stream.Destroy(c);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Track surviving entities after all frames
        survivors.Add(a);
        survivors.Add(d);
        survivors.Add(e);
        survivors.Add(f);

        // Replay all frames into empty world
        var replica = new World();
        foreach (var delta in deltas)
            new CommandStream(replica).Replay(delta);

        foreach (var entity in survivors)
            AssertEntityStateEquals(source, replica, entity);

        // Entity count parity
        Assert.Equal(CountAll(source), CountAll(replica));
    }

    [Fact]
    public void Multi_frame_replay_with_recycled_id_mutation()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();
        var stream = new CommandStream(source);

        // Frame 1: create entity
        var victim = stream.Create();
        stream.Add(victim, new Position(1, 2));
        stream.Add(victim, new Velocity(3, 4));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: destroy
        stream.Destroy(victim);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 3: new entity reuses ID
        var recycled = stream.Create();
        stream.Add(recycled, new Health(100));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 4: mutate recycled
        stream.Add(recycled, new Position(50, 60));
        stream.Set(recycled, new Health(200));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        var replica = new World();
        foreach (var delta in deltas)
            new CommandStream(replica).Replay(delta);

        Assert.False(replica.IsAlive(victim));
        Assert.True(replica.IsAlive(recycled));
        Assert.True(replica.TryGet(recycled, out Health h));
        Assert.Equal(new Health(200), h);
        Assert.True(replica.TryGet(recycled, out Position p));
        Assert.Equal(new Position(50, 60), p);
        Assert.False(replica.TryGet<Velocity>(recycled, out _));
    }

    [Fact]
    public void Multi_frame_replay_hierarchy_evolution()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();
        var stream = new CommandStream(source);

        // Frame 1: create tree A->B->C
        var a = stream.Create(); stream.Add(a, new Position(1, 1));
        var b = stream.Create(); stream.Add(b, new Position(2, 2));
        var c = stream.Create(); stream.Add(c, new Position(3, 3));
        stream.AddChild(a, b);
        stream.AddChild(b, c);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: restructure A->C and A->D, RemoveChild B
        var d = stream.Create(); stream.Add(d, new Position(4, 4));
        stream.RemoveChild(b);
        stream.RemoveChild(c);
        stream.AddChild(a, c);
        stream.AddChild(a, d);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 3: destroy leaf B
        stream.Destroy(b);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 4: destroy root A (cascades to children C, D)
        stream.Destroy(a);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        var replica = new World();
        foreach (var delta in deltas)
            new CommandStream(replica).Replay(delta);

        Assert.False(replica.IsAlive(a));
        Assert.False(replica.IsAlive(b));
        Assert.False(replica.IsAlive(c));
        Assert.False(replica.IsAlive(d));
    }

    [Fact]
    public void Multi_frame_clone_replay_produces_identical_world()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();
        var stream = new CommandStream(source);
        var tracked = new List<Entity>();

        // Frame 1: seed with linked entities
        var a = stream.Create(); stream.Add(a, new Position(1, 2));
        var b = stream.Create(); stream.Add(b, new Position(3, 4)); stream.Add(b, new Velocity(5, 6));
        stream.AddChild(a, b);
        tracked.AddRange([a, b]);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: clone A
        var cloneA = stream.Clone(a);
        tracked.Add(cloneA);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 3: modify clone, destroy original child
        stream.Set(cloneA, new Position(99, 99));
        stream.Add(cloneA, new Health(100));
        stream.Destroy(b);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 4: clone the clone, remove component
        var clone2 = stream.Clone(cloneA);
        stream.Remove<Health>(clone2);
        tracked.Add(clone2);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        var replica = new World();
        foreach (var delta in deltas)
            new CommandStream(replica).Replay(delta);

        foreach (var entity in tracked)
            AssertEntityStateEquals(source, replica, entity);

        Assert.Equal(CountAll(source), CountAll(replica));
    }

    // ══════════════════════════════════════════════════════════—
    // Pending batch component assignment correctness
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Interleaved_pending_creates_get_correct_components()
    {
        // Creating two pending entities, then adding components in reverse order
        // must assign components to the correct entity.
        var world = new World();
        var stream = new CommandStream(world);

        var a = stream.Create();
        var b = stream.Create();
        stream.Add(b, new Velocity(10, 20));
        stream.Add(a, new Position(1, 2));

        Assert.True(stream.Submit());

        Assert.True(world.TryGet(a, out Position pa));
        Assert.Equal(new Position(1, 2), pa);
        Assert.False(world.TryGet<Velocity>(a, out _));

        Assert.True(world.TryGet(b, out Velocity vb));
        Assert.Equal(new Velocity(10, 20), vb);
        Assert.False(world.TryGet<Position>(b, out _));
    }

    [Fact]
    public void Interleaved_pending_creates_with_three_entities()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var a = stream.Create();
        var b = stream.Create();
        var c = stream.Create();

        stream.Add(b, new Velocity(1, 2));
        stream.Add(a, new Position(3, 4));
        stream.Add(c, new Health(100));
        stream.Add(b, new Health(200));
        stream.Add(a, new Velocity(5, 6));

        Assert.True(stream.Submit());

        Assert.True(world.TryGet(a, out Position pa));
        Assert.Equal(new Position(3, 4), pa);
        Assert.True(world.TryGet(a, out Velocity va));
        Assert.Equal(new Velocity(5, 6), va);

        Assert.True(world.TryGet(b, out Velocity vb));
        Assert.Equal(new Velocity(1, 2), vb);
        Assert.True(world.TryGet(b, out Health hb));
        Assert.Equal(new Health(200), hb);

        Assert.True(world.TryGet(c, out Health hc));
        Assert.Equal(new Health(100), hc);
    }

    [Fact]
    public void Remove_pending_component_then_create_another_entity()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var a = stream.Create();
        stream.Add(a, new Position(1, 2));
        stream.Remove<Position>(a);

        // After removing from A, create B with components
        var b = stream.Create();
        stream.Add(b, new Velocity(10, 20));

        Assert.True(stream.Submit());

        // A should have no Position
        Assert.True(world.IsAlive(a));
        Assert.False(world.TryGet<Position>(a, out _));

        // B should have Velocity
        Assert.True(world.TryGet(b, out Velocity vb));
        Assert.Equal(new Velocity(10, 20), vb);
    }

    [Fact]
    public void Interleaved_pending_creates_snapshot_correct()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var a = stream.Create();
        var b = stream.Create();
        stream.Add(b, new Velocity(10, 20));
        stream.Add(a, new Position(1, 2));

        var delta = stream.Snapshot();

        // Replay into replica
        var replica = new World();
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.TryGet(a, out Position pa));
        Assert.Equal(new Position(1, 2), pa);
        Assert.False(replica.TryGet<Velocity>(a, out _));

        Assert.True(replica.TryGet(b, out Velocity vb));
        Assert.Equal(new Velocity(10, 20), vb);
        Assert.False(replica.TryGet<Position>(b, out _));
    }

    [Fact]
    public void Pending_entity_duplicate_Add_does_not_create_corrupted_archetype()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var source = world.Create(new Velocity(5, 6));

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Add(e, new Position(3, 4)); // Duplicate Add -- should collapse to one
        stream.Add(e, new Health(100));

        Assert.True(stream.Submit());

        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(3, 4), p);
        Assert.True(world.TryGet(e, out Health h));
        Assert.Equal(new Health(100), h);

        // The source entity with [Velocity] must still be in its own archetype
        Assert.True(world.TryGet(source, out Velocity v));
        Assert.Equal(new Velocity(5, 6), v);
    }

    // ══════════════════════════════════════════════════════════—
    // Fuzz: randomized multi-frame replay
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Pending_cancel_after_later_create_does_not_diverge_replay_allocator()
    {
        var source = new World();
        var replica = new World();
        var stream = new CommandStream(source);

        var cancelled = stream.Create();
        stream.Add(cancelled, new Position(1, 2));

        var survivor = stream.Create();
        stream.Add(survivor, new Health(10));

        stream.Destroy(cancelled);

        var delta = stream.Snapshot();
        stream.Submit();
        new CommandStream(replica).Replay(FrameDelta.FromWire(delta.AsSpan()));

        Assert.False(source.IsAlive(cancelled));
        Assert.True(source.IsAlive(survivor));
        AssertIdenticalWorlds(source, replica, "pending cancel after later create");
    }

    [Fact]
    public void Parallel_recording_keeps_pending_create_component_commands()
    {
        var source = new World();
        var replica = new World();
        var stream = new ParallelCommandStream(source);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Add(created, new Health(10));

        var delta = stream.Snapshot();
        stream.Submit();
        new CommandStream(replica).Replay(FrameDelta.FromWire(delta.AsSpan()));

        Assert.True(source.TryGet(created, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(source.TryGet(created, out Health h));
        Assert.Equal(new Health(10), h);
        AssertIdenticalWorlds(source, replica, "parallel pending create component commands");
    }

    [Fact]
    public void BUG_stale_existing_entity_set_is_skipped_so_submit_matches_replay()
    {
        var source = new World();
        var replica = new World();

        var staleSource = source.Create(new Position(1, 1));
        source.Destroy(staleSource);
        var recycledSource = source.Create(new Velocity(2, 2));

        var staleReplica = replica.Create(new Position(1, 1));
        replica.Destroy(staleReplica);
        var recycledReplica = replica.Create(new Velocity(2, 2));

        Assert.Equal(staleSource.Id, recycledSource.Id);
        Assert.Equal(staleReplica.Id, recycledReplica.Id);
        Assert.NotEqual(staleSource.Version, recycledSource.Version);
        Assert.NotEqual(staleReplica.Version, recycledReplica.Version);

        var stream = new CommandStream(source);
        stream.Set(staleSource, new Velocity(9, 9));

        var delta = stream.Snapshot();
        stream.Submit();
        new CommandStream(replica).Replay(FrameDelta.FromWire(delta.AsSpan()));

        Assert.True(source.TryGet(recycledSource, out Velocity srcVelocity));
        Assert.True(replica.TryGet(recycledReplica, out Velocity replicaVelocity));
        Assert.Equal(new Velocity(2, 2), srcVelocity);
        Assert.Equal(new Velocity(2, 2), replicaVelocity);
        AssertIdenticalWorlds(source, replica, "stale existing Set should be skipped");
    }

    [Fact]
    public void Parallel_recording_skips_stale_existing_entity_component_commands()
    {
        var source = new World();
        var replica = new World();

        var staleSource = source.Create(new Position(1, 1));
        source.Destroy(staleSource);
        var recycledSource = source.Create(new Velocity(2, 2), new Health(3));

        var staleReplica = replica.Create(new Position(1, 1));
        replica.Destroy(staleReplica);
        var recycledReplica = replica.Create(new Velocity(2, 2), new Health(3));

        var stream = new ParallelCommandStream(source);
        stream.Add(staleSource, new Position(9, 9));
        stream.Set(staleSource, new Velocity(8, 8));
        stream.Remove<Health>(staleSource);

        var delta = stream.Snapshot();
        stream.Submit();
        new CommandStream(replica).Replay(FrameDelta.FromWire(delta.AsSpan()));

        Assert.True(source.TryGet(recycledSource, out Velocity srcVelocity));
        Assert.True(source.TryGet(recycledSource, out Health srcHealth));
        Assert.True(replica.TryGet(recycledReplica, out Velocity replicaVelocity));
        Assert.True(replica.TryGet(recycledReplica, out Health replicaHealth));
        Assert.False(source.TryGet<Position>(recycledSource, out _));
        Assert.False(replica.TryGet<Position>(recycledReplica, out _));
        Assert.Equal(new Velocity(2, 2), srcVelocity);
        Assert.Equal(new Velocity(2, 2), replicaVelocity);
        Assert.Equal(new Health(3), srcHealth);
        Assert.Equal(new Health(3), replicaHealth);
        AssertIdenticalWorlds(source, replica, "parallel stale existing component commands should be skipped");
    }

    [Fact]
    public void BUG_existing_entity_that_becomes_stale_before_consume_is_skipped_so_submit_matches_replay()
    {
        var source = new World();
        var replica = new World();

        var sourceVictim = source.Create(new Position(1, 1));
        var replicaVictim = replica.Create(new Position(1, 1));

        var stream = new CommandStream(source);
        stream.Set(sourceVictim, new Position(9, 9));

        source.Destroy(sourceVictim);
        var sourceRecycled = source.Create(new Position(2, 2));

        replica.Destroy(replicaVictim);
        var replicaRecycled = replica.Create(new Position(2, 2));

        Assert.Equal(sourceVictim.Id, sourceRecycled.Id);
        Assert.Equal(replicaVictim.Id, replicaRecycled.Id);
        Assert.NotEqual(sourceVictim.Version, sourceRecycled.Version);
        Assert.NotEqual(replicaVictim.Version, replicaRecycled.Version);

        var delta = stream.Snapshot();
        stream.Submit();
        new CommandStream(replica).Replay(FrameDelta.FromWire(delta.AsSpan()));

        Assert.True(source.TryGet(sourceRecycled, out Position srcPosition));
        Assert.True(replica.TryGet(replicaRecycled, out Position replicaPosition));
        Assert.Equal(new Position(2, 2), srcPosition);
        Assert.Equal(new Position(2, 2), replicaPosition);
        AssertIdenticalWorlds(source, replica, "delayed stale existing Set should be skipped");
    }

    [Fact]
    public void BUG_parallel_destroy_on_pending_entity_does_not_cancel_like_single_threaded()
    {
        // Single-threaded: Destroy on a pending entity cancels it (never materialized)
        // and cascades to pending descendants. Parallel mode should produce the same
        // observable result.
        var worldSt = new World();
        var streamSt = new CommandStream(worldSt);
        var parentSt = streamSt.Create();
        var childSt = streamSt.Create();
        streamSt.AddChild(parentSt, childSt);
        streamSt.Destroy(parentSt);
        streamSt.Submit();

        // Parallel: same operations but using ParallelCommandStream
        var worldPar = new World();
        var streamPar = new ParallelCommandStream(worldPar);
        var parentPar = streamPar.Create();
        var childPar = streamPar.Create();
        streamPar.AddChild(parentPar, childPar);
        streamPar.Destroy(parentPar);
        streamPar.Submit();

        Assert.True(worldSt.EntityCount == 0,
            $"Single-threaded: parent+child cancelled, expected 0 alive, got {worldSt.EntityCount}");
        Assert.True(worldPar.EntityCount == 0,
            $"Parallel: Destroy on pending should also cancel, expected 0 alive, got {worldPar.EntityCount}");
    }

    [Fact]
    public void Fuzz_10000_frames_seed_42_submit_and_replay_stay_in_sync()
    {
        RunFuzz(seed: 42, frames: 10000, syncCheckInterval: 1000);
    }

    [Fact]
    public void Fuzz_frames_submit_and_replay_stay_in_sync()
    {
        for (var s = 0; s <= 5000; s++)
            RunFuzz(s, frames: 30, syncCheckInterval: 10);
        foreach (var s in new[] { 65535, 999999, 2147483647 })
            RunFuzz(s, frames: 30, syncCheckInterval: 10);
    }

    private static void RunFuzz(int seed, int frames, int syncCheckInterval)
    {
        var world = new World();
        var replica = new World();
        var stream = new CommandStream(world);
        var alive = new List<Entity>();
        var rng = new Random(seed);

        for (var frame = 0; frame < frames; frame++)
        {
            alive.RemoveAll(e => !world.IsAlive(e));

            // Build per-frame tracking from actual World state.
            var tracked = new HashSet<Entity>();
            var hasPos = new HashSet<Entity>();
            var hasVel = new HashSet<Entity>();
            foreach (var e in alive)
            {
                if (world.IsAlive(e)) tracked.Add(e);
                if (world.Has<Position>(e)) hasPos.Add(e);
                if (world.Has<Velocity>(e)) hasVel.Add(e);
            }

            var opsThisFrame = rng.Next(1, 8);
            for (var op = 0; op < opsThisFrame; op++)
            {
                var kind = alive.Count == 0 ? 0 : rng.Next(100);
                if (kind < 28 || alive.Count == 0)
                {
                    // Create entity (pending at record time)
                    var e = stream.Create();
                    tracked.Add(e);
                    hasPos.Add(e);
                    if (rng.Next(2) == 0) { stream.Add(e, new Velocity(rng.Next(), rng.Next())); hasVel.Add(e); }
                    alive.Add(e);
                }
                else if (kind < 42)
                {
                    // Destroy existing entity (may be materialized or pending)
                    var i = rng.Next(alive.Count);
                    var e = alive[i];
                    if (tracked.Contains(e))
                    {
                        stream.Destroy(e);
                        tracked.Remove(e);
                    }
                }
                else if (kind < 70)
                {
                    // Set component on materialized entity (must have component)
                    var e = alive[rng.Next(alive.Count)];
                    if (!tracked.Contains(e)) continue;
                    if (hasPos.Contains(e))
                        stream.Set(e, new Position(rng.Next(), rng.Next()));
                    else if (hasVel.Contains(e))
                        stream.Set(e, new Velocity(rng.Next(), rng.Next()));
                    // else: no known component → skip Set
                }
                else
                {
                    // Clone from an entity alive and not destroyed this frame
                    // (tracked ensures we never Clone a Destroy-this-frame entity)
                    var valid = alive.Where(e => tracked.Contains(e)).ToArray();
                    if (valid.Length > 0)
                    {
                        var src = valid[rng.Next(valid.Length)];
                        var clone = stream.Clone(src);
                        alive.Add(clone);
                    }
                }
            }

            var delta = stream.Snapshot();
            stream.Submit();
            try { new CommandStream(replica).Replay(FrameDelta.FromWire(delta.AsSpan())); }
            catch (Exception ex) { throw new InvalidOperationException($"Replay failed during fuzz seed={seed}, frame={frame}.", ex); }
            if ((frame + 1) % syncCheckInterval == 0)
                Assert.Equal(world.EntityCount, replica.EntityCount);
        }

        alive.RemoveAll(s => !world.IsAlive(s));
        Assert.NotNull(alive);
        AssertIdenticalWorlds(world, replica, $"source(Submit) vs replica(Replay) after {frames} frames, seed={seed}");
    }

    // ══════════════════════════════════════════════════════════—
    // Helpers
    // ══════════════════════════════════════════════════════════—

    /// <summary>
    /// Asserts two worlds are bit-identical by hashing WorldSnapshot.Save output
    /// through SHA256. Stable for lockstep scenarios where both worlds are driven
    /// by the same delta sequence (archetype creation order, swap-remove history,
    /// and slot allocation all match).
    /// </summary>
    private static void AssertIdenticalWorlds(World a, World b, string context)
    {
        var ha = HashWorld(a);
        var hb = HashWorld(b);
        if (ha != hb)
        {
            var sa = a.GetStats();
            var sb = b.GetStats();
            Assert.Fail(
                $"Worlds diverge for [{context}].\n" +
                $"  A: ec={sa.EntityCount}, ac={sa.ArchetypeCount}, hash={ha[..16]}\n" +
                $"  B: ec={sb.EntityCount}, ac={sb.ArchetypeCount}, hash={hb[..16]}\n");
        }
    }

    private static string HashWorld(World w)
    {
        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, w);
        var span = new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        return Convert.ToHexString(SHA256.HashData(span));
    }

    private static int CountAll(World world)
    {
        var count = 0;
        foreach (var chunk in world.Query(new QueryDescription()).GetChunks())
            count += chunk.Count;
        return count;
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

        Assert.Equal(expected.TryGetParent(entity, out var parentA), actual.TryGetParent(entity, out var parentB));
        if (expected.TryGetParent(entity, out parentA))
            Assert.Equal(parentA, parentB);
    }

    [Fact]
    public async System.Threading.Tasks.Task SubmitAndSnapshotAsync_reuses_hierarchy_dictionary_in_steady_state()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var parent = world.CreateEmpty();

        async System.Threading.Tasks.Task RunFrame()
        {
            var child = world.CreateEmpty();
            stream.AddChild(parent, child);
            stream.Destroy(child);
            await stream.SubmitAndSnapshotAsync();
        }

        for (var i = 0; i < 2; i++)
            await RunFrame();

        var hierarchyRef = stream.ActiveHierarchyForTesting;
        Assert.NotNull(hierarchyRef);

        await RunFrame();
        await RunFrame();

        Assert.Same(hierarchyRef, stream.ActiveHierarchyForTesting);
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_reuses_frozen_state_in_steady_state()
    {
        var world = new World();
        var stream = new CommandStream(world);

        async Task RunFrameAsync()
        {
            var e = world.CreateEmpty();
            stream.Destroy(e);
            await stream.SubmitAndSnapshotAsync();
        }

        // Prime: frame 1 allocates the first FrozenState; frame 2 reclaims it
        // and starts reusing it as the spare. After frame 2 the active frozen
        // is the same object that will be recycled for every subsequent frame.
        await RunFrameAsync();
        await RunFrameAsync();

        // After steady state the two FrozenState objects alternate roles
        // every frame. Their Stores arrays also alternate. Verify that no
        // new Stores array is allocated after frame 2 (count ≤ 2).
        var field = typeof(CommandStream).GetField("_frozen", BindingFlags.Instance | BindingFlags.NonPublic);
        var storesField = typeof(CommandStream).GetNestedType("FrozenState", BindingFlags.NonPublic)?.GetField("Stores");
        var observed = new List<object?>();

        object? GetStores()
        {
            var frozenObj = field?.GetValue(stream);
            return storesField?.GetValue(frozenObj);
        }

        for (var i = 0; i < 5; i++)
        {
            observed.Add(GetStores());
            await RunFrameAsync();
        }
        observed.Add(GetStores());

        var distinct = observed.Distinct().ToList();
        Assert.True(distinct.Count <= 2,
            $"Expected ≤ 2 distinct Stores arrays (object swap recycles 2), got {distinct.Count}");
    }

    [Fact]
    public async Task SubmitAndSnapshotAsync_overlapping_tasks_stay_correct()
    {
        // Caller does NOT await between frames: previous task may still be running
        // when the next SubmitAndSnapshotAsync fires. We must not corrupt state.
        var world = new World();
        var stream = new CommandStream(world);

        var tasks = new List<Task<FrameDelta>>();
        for (var i = 0; i < 5; i++)
        {
            var e = world.CreateEmpty();
            stream.Add(e, new Position(i, i));
            stream.Destroy(e);
            tasks.Add(stream.SubmitAndSnapshotAsync());
        }

        foreach (var t in tasks)
        {
            var delta = await t;
            Assert.True(delta.DeltaCount > 0);
        }
    }

    [Fact]
    public void Destroy_pending_entity_cascades_to_pending_descendants()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var parent = stream.Create();
        stream.Add(parent, new Position(1, 1));
        var child = stream.Create();
        stream.Add(child, new Position(2, 2));
        var grandchild = stream.Create();
        stream.Add(grandchild, new Position(3, 3));
        stream.AddChild(parent, child);
        stream.AddChild(child, grandchild);

        stream.Destroy(parent);
        stream.Submit();

        Assert.False(world.IsAlive(parent));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandchild));
    }

    [Fact]
    public void Destroy_pending_entity_does_not_destroy_existing_child()
    {
        // Existing (already-alive) child linked under a pending parent: when the
        // pending parent is destroyed, the existing child stays alive. World's
        // hierarchy doesn't know about the pending AddChild, so there's no cascade.
        var world = new World();
        var stream = new CommandStream(world);

        var existingChild = world.Create(new Position(9, 9));
        var parent = stream.Create();
        stream.AddChild(parent, existingChild);

        stream.Destroy(parent);
        stream.Submit();

        Assert.False(world.IsAlive(parent));
        // Existing child survives -- it was never parented in the live world.
        Assert.True(world.IsAlive(existingChild));
    }

    [Fact]
    public void SwapOutState_swaps_all_working_fields()
    {
        var allFields = typeof(CommandStream).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.Name)
            .ToHashSet();

        var nonSwapped = new HashSet<string>
        {
            "_world",
            "_deferredEntities",
            "_pendingTask",
            "_storeCreateLock",
            "_lastCreated",
            "_lastCreatedBatch",
            "_deferredSeq",
            "_pendingBatchDeferredArr",
            "_resolveMapPool",
            "_pendingBatchMin",
            "_pendingBatchMax",
            "_batchCompTotal",
            "_batchBufLen",
            "_maskCache",
            "_maskCacheCount",
            "_maskCacheGeneration",
            "_lastMask",
            "_lastMaskArchetype",
            "_trackedBySeq",
            "_trackedMaxSeq",
            "_replayTrackedBySeq",
            "_replayTrackedMaxSeq",
            "_pendingReplay",
            "_preflightGenerations",
            "_preflightPresence",
            "_preflightEpoch",
        };

        // _frozen and _spareFrozen are the two halves of the state-object swap;
        // _pendingFrozen holds the in-flight task's snapshot.
        var swapped = new HashSet<string>
        {
            "_frozen",
            "_spareFrozen",
            "_pendingFrozen",
        };

        var unclassified = allFields.Where(f => !nonSwapped.Contains(f) && !swapped.Contains(f)).ToList();
        Assert.Empty(unclassified);
    }

    [Fact]
    public void BUG_submit_prevalidates_reserved_pending_slots_before_materialize()
    {
        var world = new World();
        var stream = new CommandStream(world);

        // Record two pending creates with distinguishable components.
        var entity1 = stream.Create();
        stream.Add(entity1, new Position(1, 2));
        var entity2 = stream.Create();
        stream.Add(entity2, new Velocity(3, 4));

        Assert.False(world.IsAlive(entity1));
        Assert.False(world.IsAlive(entity2));

        // Corrupt entity1's reserved slot by releasing it externally.
        // This simulates the slot no longer being reserved before Submit.
        world.ReleaseReservedEntity(entity1);

        // Submit must throw before any materialization occurs.
        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Assert no entities were materialized —pre-validation fired before side effects.
        Assert.False(world.IsAlive(entity1));
        Assert.False(world.IsAlive(entity2));
    }

    [Fact]
    public void BUG_submit_preflights_invalid_add_before_materializing_pending()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.Add(existing, new Position(9, 9));

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());

        Assert.Contains("already has component", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.Equal(new Position(1, 2), world.Get<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void BUG_submit_preflights_invalid_set_before_materializing_pending()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.Set(existing, new Velocity(9, 9));

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());

        Assert.Contains("does not have component", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.Equal(new Position(1, 2), world.Get<Position>(existing));
        Assert.False(world.Has<Velocity>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Submit_revalidates_recorded_set_after_component_is_removed()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.Set(existing, new Position(9, 9));

        world.Remove<Position>(existing);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());

        Assert.Contains("does not have component", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.False(world.Has<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Submit_revalidates_recorded_set_after_missing_component_is_added()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        stream.Set(existing, new Velocity(9, 9));

        world.Add(existing, new Velocity(3, 4));

        Assert.True(stream.Submit());
        Assert.Equal(new Velocity(9, 9), world.Get<Velocity>(existing));
        Assert.Equal(new Position(1, 2), world.Get<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Submit_missing_remove_is_a_noop_and_still_materializes_pending()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.Remove<Velocity>(existing);

        Assert.True(stream.Submit());

        Assert.Equal(2, world.EntityCount);
        Assert.True(world.IsAlive(pending));
        Assert.Equal(new Velocity(3, 4), world.Get<Velocity>(pending));
        Assert.Equal(new Position(1, 2), world.Get<Position>(existing));
        Assert.False(world.Has<Velocity>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Submit_component_preflight_simulates_add_set_remove_in_recording_order()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.Add(existing, new Health(10));
        stream.Set(existing, new Health(20));
        stream.Remove<Health>(existing);

        Assert.True(stream.Submit());
        Assert.False(world.Has<Health>(existing));
        Assert.Equal(new Position(1, 2), world.Get<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Submit_component_preflight_simulates_set_remove_add_set_in_recording_order()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        stream.Set(existing, new Position(3, 4));
        stream.Remove<Position>(existing);
        stream.Add(existing, new Position(5, 6));
        stream.Set(existing, new Position(7, 8));

        Assert.True(stream.Submit());
        Assert.Equal(new Position(7, 8), world.Get<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void BUG_set_preflight_row_cache_is_disabled_when_any_store_is_structural()
    {
        using var world = new World();
        var existing = world.Create(new Health(10));
        var stream = new CommandStream(world);

        stream.Set(existing, new Health(20));
        stream.Add(existing, new Position(3, 4));

        Assert.True(stream.Submit());
        Assert.Equal(new Health(20), world.Get<Health>(existing));
        Assert.Equal(new Position(3, 4), world.Get<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void BUG_submit_preflights_hierarchy_overlay_cycle_before_world_mutation()
    {
        using var world = new World();
        var a = world.Create(new Position(1, 1));
        var b = world.Create(new Position(2, 2));
        var stream = new CommandStream(world);
        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.AddChild(a, b);
        stream.AddChild(b, a);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());

        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.False(world.TryGetParent(a, out _));
        Assert.False(world.TryGetParent(b, out _));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void BUG_submit_preflights_hierarchy_cycle_through_existing_parent_chain()
    {
        using var world = new World();
        var root = world.Create(new Position(1, 1));
        var child = world.Create(new Position(2, 2));
        world.AddChild(root, child);
        var stream = new CommandStream(world);
        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.AddChild(child, root);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());

        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.True(world.TryGetParent(child, out var actualParent));
        Assert.Equal(root, actualParent);
        Assert.False(world.TryGetParent(root, out _));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void BUG_submit_preflights_deferred_hierarchy_cycle_before_reserving_ids()
    {
        using var world = new World();
        var stream = new CommandStream(world) { DeferredEntities = true };
        var a = stream.Create();
        var b = stream.Create();
        stream.Add(a, new Position(1, 1));
        stream.Add(b, new Position(2, 2));
        stream.AddChild(a, b);
        stream.AddChild(b, a);

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());

        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, world.EntityCount);
        Assert.False(world.IsAlive(a));
        Assert.False(world.IsAlive(b));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void BUG_submit_preflights_repeated_add_before_any_world_mutation()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);
        var pending = stream.Create();
        stream.Add(pending, new Velocity(3, 4));
        stream.Add(existing, new Health(10));
        stream.Add(existing, new Health(20));

        var ex = Assert.Throws<InvalidOperationException>(() => stream.Submit());

        Assert.Contains("already has component", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, world.EntityCount);
        Assert.False(world.IsAlive(pending));
        Assert.False(world.Has<Health>(existing));
        Assert.Equal(new Position(1, 2), world.Get<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }

    [Fact]
    public void Submit_repeated_remove_is_idempotent()
    {
        using var world = new World();
        var existing = world.Create(new Position(1, 2), new Velocity(3, 4));
        var stream = new CommandStream(world);

        stream.Remove<Velocity>(existing);
        stream.Remove<Velocity>(existing);

        Assert.True(stream.Submit());
        Assert.False(world.Has<Velocity>(existing));
        Assert.Equal(new Position(1, 2), world.Get<Position>(existing));
        Assert.True(WorldValidator.Validate(world).IsValid);
    }
}

// ── Deferred Create ──────────────────────────────────────────────

public sealed class DeferredCreateTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // Component with an embedded Entity reference —used to test automatic
    // placeholder resolution in both Submit and Replay paths.
    private readonly record struct Linked(int Extra, Entity Target);

    private static CommandStream MakeStream(World world) =>
        new CommandStream(world) { DeferredEntities = true };

    [Fact]
    public void Create_returns_placeholder()
    {
        var world = new World();
        var stream = MakeStream(world);
        var entity = stream.Create();
        Assert.Equal(-1, entity.Id);
    }

    [Fact]
    public void Placeholder_not_alive_before_submit()
    {
        var world = new World();
        var stream = MakeStream(world);
        var entity = stream.Create();
        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void Placeholder_not_alive_after_submit()
    {
        var world = new World();
        var stream = MakeStream(world);
        var entity = stream.Create();
        stream.Submit();
        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void Entity_materialized_after_submit()
    {
        var world = new World();
        var stream = MakeStream(world);
        stream.Create();
        stream.Submit();
        // Verify the entity was materialized: a query finds it
        var found = false;
        foreach (ref readonly var arch in MiniQueryCache.Create(world, new QueryDescription()).GetArchetypeSpan())
            if (arch.GetEntities().Length > 0) found = true;
        Assert.True(found);
    }

    [Fact]
    public void Deferred_with_components()
    {
        var world = new World();
        var stream = MakeStream(world);
        var entity = stream.Create();
        stream.Add(entity, new Position(1, 2));
        stream.Submit();
        var found = 0;
        foreach (ref readonly var arch in MiniQueryCache.Create(world, new QueryDescription()).GetArchetypeSpan())
            found += arch.GetEntities().Length;
        Assert.Equal(1, found);
    }

    [Fact]
    public void Multiple_deferred_creates()
    {
        var world = new World();
        var stream = MakeStream(world);
        stream.Create();
        stream.Create();
        stream.Create();
        stream.Submit();
        var found = 0;
        foreach (ref readonly var arch in MiniQueryCache.Create(world, new QueryDescription()).GetArchetypeSpan())
            found += arch.GetEntities().Length;
        Assert.Equal(3, found);
    }

    [Fact]
    public void Destroy_deferred_before_submit_cancels_materialization()
    {
        var world = new World();
        var stream = MakeStream(world);
        var entity = stream.Create();
        stream.Add(entity, new Position(1, 2));
        stream.Destroy(entity);
        stream.Submit();
        var found = 0;
        foreach (ref readonly var arch in MiniQueryCache.Create(world, new QueryDescription()).GetArchetypeSpan())
            found += arch.GetEntities().Length;
        Assert.Equal(0, found);
    }

    [Fact]
    public void Deferred_entity_increments_seq()
    {
        var world = new World();
        var stream = MakeStream(world);
        var a = stream.Create();
        var b = stream.Create();
        var c = stream.Create();
        Assert.True(a.Version < b.Version);
        Assert.True(b.Version < c.Version);
    }

    [Fact]
    public void Deferred_clone_returns_placeholder()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var stream = MakeStream(world);
        var clone = stream.Clone(source);
        Assert.Equal(-1, clone.Id);
        Assert.False(world.IsAlive(clone));
        stream.Submit();
        var found = 0;
        foreach (ref readonly var arch in MiniQueryCache.Create(world, new QueryDescription()).GetArchetypeSpan())
            found += arch.GetEntities().Length;
        Assert.Equal(2, found);
    }

    // ══════════════════════════════════════════════════════════—
    // Placeholder delta -- Replay (multi-host lockstep core path)
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Placeholder_delta_replays_into_fresh_world()
    {
        var host = new World();
        var stream = MakeStream(host);
        stream.Create();
        stream.Add(new Entity(-1, 0), new Position(10, 20));
        var delta = stream.Snapshot();
        stream.Clear();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        Assert.Equal(1, CountEntities(replica));
        AssertPosition(replica, 10, 20);
    }

    [Fact]
    public void Placeholder_delta_into_two_worlds_produces_identical_state()
    {
        var host = new World();
        var stream = MakeStream(host);
        var a = stream.Create(); stream.Add(a, new Position(1, 2));
        var b = stream.Create(); stream.Add(b, new Position(3, 4)); stream.Add(b, new Velocity(5, 6));
        stream.AddChild(a, b);
        var delta = stream.Snapshot();
        stream.Clear();

        var wire = delta.AsSpan();

        var replicaA = new World();
        new CommandStream(replicaA).Replay(FrameDelta.FromWire(wire));

        var replicaB = new World();
        new CommandStream(replicaB).Replay(FrameDelta.FromWire(wire));

        var statsA = replicaA.GetStats();
        var statsB = replicaB.GetStats();
        Assert.Equal(statsA.EntityCount, statsB.EntityCount);
        Assert.Equal(statsA.ArchetypeCount, statsB.ArchetypeCount);
        Assert.Equal(HashWorld(replicaA), HashWorld(replicaB));
    }

    [Fact]
    public void Placeholder_delta_serialization_roundtrip()
    {
        var host = new World();
        var stream = MakeStream(host);
        var a = stream.Create(); stream.Add(a, new Position(7, 8));
        var b = stream.Create(); stream.Add(b, new Health(99));
        var delta = stream.Snapshot();
        stream.Clear();

        var wire = delta.AsSpan();
        var restored = FrameDelta.FromWire(wire);

        var replica = new World();
        new CommandStream(replica).Replay(restored);

        Assert.Equal(2, CountEntities(replica));
    }

    [Fact]
    public void Multiple_host_deltas_replayed_into_one_world()
    {
        // Simulate 2 hosts, each producing a placeholder delta.
        var host1 = new World();
        var s1 = MakeStream(host1);
        s1.Create(); s1.Add(new Entity(-1, 0), new Position(1, 1));
        var d1 = s1.Snapshot(); s1.Clear();

        var host2 = new World();
        var s2 = MakeStream(host2);
        s2.Create(); s2.Add(new Entity(-1, 0), new Health(50));
        var d2 = s2.Snapshot(); s2.Clear();

        // A third world replays both deltas in order.
        var replica = new World();
        new CommandStream(replica).Replay(d1);
        new CommandStream(replica).Replay(d2);

        Assert.Equal(2, CountEntities(replica));
    }

    [Fact]
    public void Placeholder_hierarchy_replays_correctly()
    {
        var host = new World();
        var stream = MakeStream(host);
        var parent = stream.Create(); stream.Add(parent, new Position(0, 0));
        var child = stream.Create(); stream.Add(child, new Position(1, 1));
        stream.AddChild(parent, child);
        var delta = stream.Snapshot();
        stream.Clear();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        Assert.Equal(2, CountEntities(replica));
        // Find the parent (has Position 0,0) and verify it has children.
        var hasHierarchy = false;
        foreach (ref readonly var arch in MiniQueryCache.Create(replica, new QueryDescription()).GetArchetypeSpan())
        {
            foreach (var entity in arch.GetEntities())
            {
                if (replica.Get<Position>(entity).X == 0)
                {
                    Assert.True(replica.Hierarchy.HasChildren(replica, entity));
                    hasHierarchy = true;
                }
            }
        }
        Assert.True(hasHierarchy, "Expected a parent entity with children after replay.");
    }

    [Fact]
    public void Placeholder_delta_destroy_before_snapshot_not_emitted()
    {
        var host = new World();
        var stream = MakeStream(host);
        var a = stream.Create(); stream.Add(a, new Position(1, 2));
        stream.Destroy(a); // cancel before snapshot
        var b = stream.Create(); stream.Add(b, new Position(3, 4));
        var delta = stream.Snapshot();
        stream.Clear();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        Assert.Equal(1, CountEntities(replica));
    }

    

    // ── Helpers ──────────────────────────────────────────────────

    private static int CountEntities(World w)
    {
        var count = 0;
        foreach (ref readonly var arch in MiniQueryCache.Create(w, new QueryDescription()).GetArchetypeSpan())
            count += arch.GetEntities().Length;
        return count;
    }

    private static void AssertPosition(World w, int expectedX, int expectedY)
    {
        var desc = new QueryDescription().With<Position>();
        foreach (ref readonly var arch in MiniQueryCache.Create(w, in desc).GetArchetypeSpan())
        {
            foreach (var entity in arch.GetEntities())
            {
                var pos = w.Get<Position>(entity);
                Assert.Equal(expectedX, pos.X);
                Assert.Equal(expectedY, pos.Y);
                return;
            }
        }
        Assert.Fail("No entity with Position component found.");
    }

    // ══════════════════════════════════════════════════════════—
    // Embedded Entity ref (automatic placeholder resolution)
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Submit_resolves_embedded_Entity_ref_in_pending_create()
    {
        // Two pending entities, one references the other via an Entity field
        // inside a component struct.
        var world = new World();
        var stream = new CommandStream(world);

        var a = stream.Create();
        var b = stream.Create();
        stream.Add(a, new Position(1, 2));
        // b's component references a —both are placeholders at record time.
        stream.Add(b, new Linked(99, a));

        stream.Submit();

        Assert.True(world.IsAlive(a));
        Assert.True(world.IsAlive(b));
        Assert.True(world.TryGet(b, out Linked linked));
        Assert.Equal(99, linked.Extra);
        // The placeholder Entity(-1, 0) should have been resolved to the real a.
        Assert.Equal(a, linked.Target);
        Assert.True(a.IsValid);
    }

    [Fact]
    public void Submit_resolves_embedded_Entity_ref_from_existing_to_pending()
    {
        // Existing entity gets a component whose Entity field points to a
        // deferred-created entity.
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        var target = stream.Create();
        stream.Add(target, new Health(100));
        stream.Add(existing, new Linked(42, target));

        stream.Submit();

        Assert.True(world.IsAlive(target));
        Assert.True(world.TryGet(existing, out Linked linked));
        Assert.Equal(42, linked.Extra);
        Assert.Equal(target, linked.Target);
    }

    [Fact]
    public void Submit_resolves_Entity_ref_then_Set_on_same_target()
    {
        // Existing entity gets a component referencing the deferred target,
        // then Set the same component on the same entity —both reference
        // must resolve.
        var world = new World();
        var stream = new CommandStream(world);

        var target = stream.Create();
        stream.Add(target, new Health(100));
        stream.Add(target, new Linked(1, target));   // self-reference
        stream.Set(target, new Linked(2, target));   // overwrite via Set

        stream.Submit();

        Assert.True(world.TryGet(target, out Linked linked));
        Assert.Equal(2, linked.Extra);
        Assert.Equal(target, linked.Target);
    }

    [Fact]
    public void Replay_resolves_embedded_Entity_ref_in_pending_create()
    {
        var host = new World();
        var stream = MakeStream(host);

        var a = stream.Create();
        var b = stream.Create();
        stream.Add(a, new Position(10, 20));
        stream.Add(b, new Linked(77, a));

        var delta = stream.Snapshot();
        stream.Clear();

        var replica = new World();
        new CommandStream(replica).Replay(delta);

        // Resolve placeholders to real handles.
        Assert.True(replica.TryResolvePlaceholder(a, out var realA));
        Assert.True(replica.TryResolvePlaceholder(b, out var realB));

        Assert.True(replica.IsAlive(realA));
        Assert.True(replica.IsAlive(realB));
        Assert.True(replica.TryGet(realB, out Linked linked));
        Assert.Equal(77, linked.Extra);
        // The embedded placeholder in Linked.Target must have been resolved
        // to the same realA that b's own entity reference resolved to.
        Assert.Equal(realA, linked.Target);
    }

    [Fact]
    public void Replay_resolves_embedded_Entity_ref_from_existing_to_pending()
    {
        var host = new World();
        var existing = host.Create(new Position(0, 0));
        var stream = MakeStream(host);

        var target = stream.Create();
        stream.Add(target, new Health(99));
        stream.Add(existing, new Linked(55, target));

        var delta = stream.Snapshot();
        stream.Clear();

        var replica = new World();
        var replicaExisting = replica.Create(new Position(0, 0));
        new CommandStream(replica).Replay(delta);

        Assert.True(replica.TryResolvePlaceholder(target, out var realTarget));
        Assert.True(replica.IsAlive(realTarget));
        Assert.True(replica.TryGet(replicaExisting, out Linked linked));
        Assert.Equal(55, linked.Extra);
        Assert.Equal(realTarget, linked.Target);
    }

    [Fact]
    public void Replay_same_delta_into_two_worlds_produces_independent_resolution()
    {
        // Regression: the delta buffer must not be mutated in-place.
        // Each world independently resolves the same placeholder seq → local id.
        var host = new World();
        var stream = MakeStream(host);

        var target = stream.Create();
        stream.Add(target, new Health(10));
        var a = stream.Create();
        stream.Add(a, new Linked(1, target));

        var delta = stream.Snapshot();
        stream.Clear();

        // Replay into world A.
        var worldA = new World();
        new CommandStream(worldA).Replay(delta);

        // Replay the same delta into world B.
        var worldB = new World();
        new CommandStream(worldB).Replay(delta);

        Assert.True(worldA.TryResolvePlaceholder(target, out var targetA));
        Assert.True(worldA.TryResolvePlaceholder(a, out var aA));
        Assert.True(worldA.TryGet(aA, out Linked linkedA));
        Assert.Equal(targetA, linkedA.Target);

        Assert.True(worldB.TryResolvePlaceholder(target, out var targetB));
        Assert.True(worldB.TryResolvePlaceholder(a, out var aB));
        Assert.True(worldB.TryGet(aB, out Linked linkedB));
        Assert.Equal(targetB, linkedB.Target);

        // Both worlds independently resolved to the same IDs (deterministic allocator).
        // But mutations in one world must NOT affect the other.
        worldA.Destroy(targetA);
        Assert.False(worldA.IsAlive(targetA));
        Assert.True(worldB.IsAlive(targetB));
    }

    [Fact]
    public void Submit_resolves_embedded_Entity_ref_after_Destroy_pending()
    {
        // Deferred mode: A references B via component; B is then destroyed.
        // The resolver must throw: A references B via an Entity field, but
        // B's batch was cancelled so the placeholder cannot be resolved.
        var world = new World();
        var stream = MakeStream(world);

        var a = stream.Create(); // Entity(-1,0)
        var b = stream.Create(); // Entity(-1,1)
        stream.Add(a, new Linked(0, b));
        stream.Destroy(b);

        Assert.Throws<InvalidOperationException>(() => stream.Submit());
    }

    [Fact]
    public void Submit_resolves_self_reference_in_pending_entity()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var e = stream.Create();
        stream.Add(e, new Linked(0, e)); // self-reference

        stream.Submit();

        Assert.True(world.IsAlive(e));
        Assert.True(world.TryGet(e, out Linked linked));
        Assert.Equal(e, linked.Target);
    }

    // ══════════════════════════════════════════════════════—
    //  Deferred-mode Submit (DeferredEntities = true)
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void DeferredSubmit_resolves_embedded_ref_between_pending_entities()
    {
        var world = new World();
        var stream = MakeStream(world);

        var target = stream.Create(); // Entity(-1,0)
        var owner = stream.Create();  // Entity(-1,1)
        stream.Add(target, new Health(42));
        stream.Add(owner, new Linked(100, target));

        stream.Submit();

        // After deferred Submit, placeholder handles are stale; verify via query.
        var qTarget = world.Query(new QueryDescription().With<Health>());
        Entity? foundTarget = null;
        foreach (var chunk in qTarget.GetChunks())
        {
            var healths = chunk.GetSpan<Health>();
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (healths[i].Value == 42)
                    foundTarget = entities[i];
            }
        }
        Assert.NotNull(foundTarget);

        var qLinked = world.Query(new QueryDescription().With<Linked>());
        bool verified = false;
        foreach (var chunk in qLinked.GetChunks())
        {
            var linked = chunk.GetSpan<Linked>();
            for (var i = 0; i < chunk.Count; i++)
            {
                Assert.Equal(100, linked[i].Extra);
                Assert.Equal(foundTarget!.Value, linked[i].Target);
                verified = true;
            }
        }
        Assert.True(verified);
    }

    [Fact]
    public void DeferredSubmit_resolves_existing_to_pending_ref()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = MakeStream(world);

        var target = stream.Create();
        stream.Add(target, new Health(77));
        stream.Add(existing, new Linked(50, target));

        stream.Submit();

        Assert.True(world.TryGet(existing, out Linked linked));
        Assert.Equal(50, linked.Extra);
        // The placeholder in Linked.Target must resolve to a live entity.
        Assert.True(world.IsAlive(linked.Target));
    }

    [Fact]
    public void DeferredSubmit_two_pass_delta_passes_Validate()
    {
        var host = new World();
        var stream = MakeStream(host);

        // Multiple entities referencing each other to exercise two-pass emission.
        var a = stream.Create();
        var b = stream.Create();
        stream.Add(a, new Linked(1, b));
        stream.Add(b, new Linked(2, a));

        var delta = stream.Snapshot();

        // Validate checks structural integrity (Reserve before Create, etc.)
        delta.Validate();

        // Ensure all ops are present.
        Assert.True(delta.DeltaCount >= 4); // Reserve(a) + Reserve(b) + Create(a) + Create(b)
    }

    // ══════════════════════════════════════════════════════—
    //  Unsupported layout —fail-fast
    // ══════════════════════════════════════════════════════—

    // Auto-layout component with Entity field —resolution must throw.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private struct BadLinked
    {
        public int X;
        public Entity Target;
    }

    [Fact]
    public void EntityFieldResolver_throws_for_auto_layout_with_Entity_fields()
    {
        // Force registration first (which triggers the scan).
        var ct = Component<BadLinked>.ComponentType;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntityFieldResolver.GetOffsets(ct));
        Assert.Contains("LayoutKind.Auto", ex.Message);
    }

    [Fact]
    public void ResolveInPlace_throws_when_placeholder_seq_exceeds_resolveMap_length()
    {
        var ct = Component<Linked>.ComponentType;
        var linked = new Linked(42, new Entity(-1, 5));
        var data = new byte[Unsafe.SizeOf<Linked>()];
        MemoryMarshal.Write(data, in linked);
        var resolveMap = new Entity[3]; // indices 0-2 only, seq=5 is OOB

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntityFieldResolver.ResolveInPlace(data.AsSpan(), ct, resolveMap.AsSpan()));
        Assert.Contains("seq=5", ex.Message);
    }

    [Fact]
    public void ResolveInPlace_throws_when_placeholder_seq_maps_to_unmapped_sentinel()
    {
        var ct = Component<Linked>.ComponentType;
        var linked = new Linked(42, new Entity(-1, 2));
        var data = new byte[Unsafe.SizeOf<Linked>()];
        MemoryMarshal.Write(data, in linked);
        var resolveMap = new Entity[] { default, default, new Entity(-1, -1) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EntityFieldResolver.ResolveInPlace(data.AsSpan(), ct, resolveMap.AsSpan()));
        Assert.Contains("seq=2", ex.Message);
    }

    // BUG REPROOF HYPOTHESIS: ComponentStore<T>.AppendConcurrent uses
    // `_data.Length` as the gate for slot validity, but performs three
    // independent Array.Resize calls (_data, _entities, _kinds) inside the
    // resize lock. A concurrent appender that reads _data.Length after the
    // first Resize but before the other two completes will exit the wait
    // loop and write _entities[slot] / _kinds[slot] / _data[slot] into
    // arrays that are still the old (shorter) length — producing either an
    // IndexOutOfRangeException or a silent write into a discarded array.
    //
    // The test runs many trials; each trial hammers a fresh ComponentStore
    // with parallel Set commands whose counts cross resize thresholds.
    // A healthy implementation never throws and never loses a Set.
    [Fact]
    public void BUG_parallel_append_concurrent_resize_is_not_atomic()
    {
        var crashCount = 0;
        var lossCount = 0;
        var trials = 200;

        for (var trial = 0; trial < trials; trial++)
        {
            var world = new World();
            var entities = new Entity[1024];
            for (var i = 0; i < entities.Length; i++)
                entities[i] = world.Create(new Position(i, i));

            var stream = new ParallelCommandStream(world);

            Exception? crash = null;
            try
            {
                Parallel.For(0, entities.Length, i =>
                {
                    stream.Set(entities[i], new Position(i + 1, i + 2));
                });
            }
            catch (Exception ex)
            {
                crash = ex;
            }

            // Submit may also throw if the recorded state is corrupt.
            try { stream.Submit(); }
            catch (Exception ex) { crash = crash ?? ex; }

            if (crash != null)
            {
                crashCount++;
                continue;
            }

            for (var i = 0; i < entities.Length; i++)
            {
                if (!world.TryGet(entities[i], out Position p) ||
                    p.X != i + 1 || p.Y != i + 2)
                {
                    lossCount++;
                    break;
                }
            }
        }

        Assert.True(crashCount == 0 && lossCount == 0,
            $"AppendConcurrent race: {crashCount}/{trials} trials crashed, " +
            $"{lossCount}/{trials} trials lost data.");
    }

    // CommandStream operations on entities in chunked archetypes.
    // All paths (Set/Add/Remove) must work when the entity's archetype
    // is chunked, as Materialize calls into the same World APIs.
    [Fact]
    public void Submit_works_on_chunked_archetype_entities()
    {
        var world = new World();
        var stream = new CommandStream(world);

        // Create entities and force the archetype to chunked
        for (var i = 0; i < 20; i++)
        {
            var e = stream.Create();
            stream.Add(e, new Position(i, i + 1));
        }
        Assert.True(stream.Submit());

        // Force chunked mode
        var desc = new QueryDescription().With<Position>();
        var coreQuery = MiniQueryCache.Create(world, in desc);
        var archetype = Assert.Single(coreQuery.MatchedArchetypes);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Verify baseline data via chunks
        var count = 0;
        foreach (var chunk in world.Query(in desc).GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            for (var ci = 0; ci < chunk.Count; ci++)
            {
                Assert.Equal(new Position(count, count + 1), positions[ci]);
                count++;
            }
        }
        Assert.Equal(20, count);

        // Now apply Set/Add/Remove via CommandStream on chunked entities
        stream = new CommandStream(world);
        var sample = world.Create(new Position(0, 0));
        foreach (var e in world.Query(in desc))
        {
            if (e != sample)
            {
                stream.Set(e, new Position(999, 888));
                stream.Add(e, new Velocity(e.Id, e.Id * 2));
            }
        }
        Assert.True(stream.Submit());

        foreach (var e in world.Query(in desc))
        {
            if (e == sample) continue;
            Assert.Equal(new Position(999, 888), world.Get<Position>(e));
            Assert.Equal(new Velocity(e.Id, e.Id * 2), world.Get<Velocity>(e));
        }
    }

    private static string HashWorld(World w)
    {
        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, w);
        var span = new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        return Convert.ToHexString(SHA256.HashData(span));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ParallelCommandStream subclass coverage (reviewer P2)
    //  These mirror single-threaded tests for Track / RemoveChild / Clone.
    //  They exist to lock in the contract that ParallelCommandStream is a
    //  drop-in for CommandStream on these APIs — not just the Add/Set/Remove
    //  fast path that already had coverage.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParallelCommandStream_Track_resolves_placeholder_after_Submit()
    {
        var world = new World();
        var stream = new ParallelCommandStream(world) { DeferredEntities = true };

        var placeholder = stream.Create();
        Assert.True(placeholder.IsPlaceholder);

        var slot = stream.Track(placeholder);
        Assert.Equal(placeholder, slot.Value);

        stream.Add(placeholder, new Position(7, 8));
        Assert.True(stream.Submit());

        // After Submit, the slot's Value must resolve to the real entity, not
        // the placeholder. This is the same contract EntitySlotTests verifies
        // for the single-threaded stream.
        var resolved = slot.Value;
        Assert.True(resolved.IsValid);
        Assert.True(resolved.Id >= 0);
        Assert.True(world.IsAlive(resolved));
        Assert.True(world.TryGet(resolved, out Position p));
        Assert.Equal(new Position(7, 8), p);
    }

    [Fact]
    public void ParallelCommandStream_RemoveChild_detaches_recorded_parent_child()
    {
        var world = new World();
        var stream = new ParallelCommandStream(world);

        var parent = stream.Create();
        var child = stream.Create();
        stream.AddChild(parent, child);

        // Detach before Submit — same recording session.
        stream.RemoveChild(child);

        Assert.True(stream.Submit());

        Assert.True(world.IsAlive(parent));
        Assert.True(world.IsAlive(child));
        Assert.False(world.HasChildren(parent));
        Assert.False(world.TryGetParent(child, out _));
    }

    [Fact]
    public void ParallelCommandStream_Clone_deep_copies_subtree()
    {
        var world = new World();
        // Source subtree exists in the live world before recording.
        var parent = world.Create(new Position(5, 6));
        var child1 = world.Create(new Velocity(1, 1));
        var child2 = world.Create(new Health(50));
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);

        var stream = new ParallelCommandStream(world);
        var clone = stream.Clone(parent);
        Assert.True(stream.Submit());

        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position p));
        Assert.Equal(new Position(5, 6), p);

        var cloneChildren = new List<Entity>();
        foreach (var c in world.Hierarchy.EnumerateChildren(world, clone))
            cloneChildren.Add(c);

        Assert.Equal(2, cloneChildren.Count);
        // Clone's children must carry the same component data as the source children.
        Assert.Contains(cloneChildren, c => world.TryGet(c, out Velocity _));
        Assert.Contains(cloneChildren, c => world.TryGet(c, out Health _));
    }

    // ────────────────────────────────────────────────────────────────────
    // ────────────────────────────────────────────────────────────────────
    //  ParallelCommandStream pending-batch regression tests (CS10)
    //  ParallelCommandStream.Add/Set/Remove on a pending-batch entity MUST
    //  write to the batch buffer (like single-threaded CommandStream does),
    //  NOT to the component store.  The store path materializes the entity
    //  empty and then tries to ApplyToWorld, which throws when the component
    //  doesn't exist on the entity.
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParallelCommandStream_Set_on_pending_entity_applies_component()
    {
        var world = new World();
        var stream = new ParallelCommandStream(world);
        var e = stream.Create();
        stream.Set(e, new Position(1, 2));
        Assert.True(stream.Submit());
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
    }

    [Fact]
    public void ParallelCommandStream_Add_on_pending_entity_does_not_throw_on_second_Add()
    {
        var world = new World();
        var stream = new ParallelCommandStream(world);
        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Add(e, new Position(3, 4)); // second Add on same pending entity
        Assert.True(stream.Submit());
        Assert.True(world.TryGet(e, out Position p));
        Assert.Equal(new Position(3, 4), p); // last write wins
    }

    [Fact]
    public void ParallelCommandStream_Remove_on_pending_entity_skips_component()
    {
        var world = new World();
        var stream = new ParallelCommandStream(world);
        var e = stream.Create();
        // Add a component, then remove it before Submit — should not appear
        stream.Add(e, new Position(1, 2));
        stream.Remove<Position>(e);
        Assert.True(stream.Submit());
        Assert.True(world.IsAlive(e));
        Assert.False(world.TryGet<Position>(e, out _));
    }

    [Fact]
    public void ParallelCommandStream_Clone_then_Add_component_matches_single_threaded()
    {
        var world = new World();
        // Source entity in the world
        var src = world.Create(new Position(10, 20));
        var stream = new ParallelCommandStream(world);
        var clone = stream.Clone(src);
        // Add an additional component that the source didn't have
        stream.Add(clone, new Health(99));
        Assert.True(stream.Submit());
        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position p));
        Assert.Equal(new Position(10, 20), p);
        Assert.True(world.TryGet(clone, out Health h));
        Assert.Equal(new Health(99), h);
    }

    //  CommandStreamCore no longer exposes any recording mutators directly.
    //  These methods live only on CommandStream and ParallelCommandStream.
    //  This test prevents accidental reintroduction of the old public throw-stubs.
    // ────────────────────────────────────────────────────────────────────

    private static readonly string[] MutatorNames =
    [
        "Create", "Track", "Add", "Set", "Remove", "Destroy",
        "AddChild", "RemoveChild", "Clone"
    ];

    [Fact]
    public void CommandStreamCore_does_not_expose_recording_mutators()
    {
        var baseMethods = typeof(CommandStreamCore)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet();

        var forbidden = MutatorNames.Where(m => baseMethods.Contains(m)).ToArray();
        Assert.Empty(forbidden);

        // Also verify the concrete types expose them.
        foreach (var name in MutatorNames)
        {
            Assert.True(
                typeof(CommandStream).GetMethod(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) is not null,
                $"CommandStream should expose '{name}'");
            Assert.True(
                typeof(ParallelCommandStream).GetMethod(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) is not null,
                $"ParallelCommandStream should expose '{name}'");
        }
    }

    // ── TrySetBit regression (ids outside 0..511) ─────────────────────

    /// <summary>
    /// <see cref="CommandStreamCore.TrySetBit"/> must NOT accept <c>id >= 512</c>.
    /// Previously the last branch (<c>id &lt; 448</c>) fell through to
    /// <c>TrySetBitInLane(ref b7, id - 448)</c> without a 512 guard. Since C#
    /// masks ulong shift counts to the lower 6 bits, <c>1UL &lt;&lt; (id - 448)</c>
    /// wraps for <c>id - 448 >= 64</c>, aliasing ids 512..575 into bits 0..63
    /// of b7 — the same range as 448..511.
    /// </summary>
    [Fact]
    public void TrySetBit_rejects_ids_512_and_above()
    {
        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        ulong b4 = 0, b5 = 0, b6 = 0, b7 = 0;

        // id 512: first value past the 512-bit boundary
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 512));
        Assert.Equal(0UL, b7);  // must not alias to bit 0 of b7

        // id 513: verify it doesn't alias to bit 1 of b7 either
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 513));
        Assert.Equal(0UL, b7);

        // id 575: last value before id - 448 = 127, still wraps
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 575));
        Assert.Equal(0UL, b7);

        // id 576: id - 448 = 128 => 1UL << 128 => 1UL << 0 (wraps 64-bit)
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 576));
        Assert.Equal(0UL, b7);

        // id 1024: far outside, should also be rejected
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 1024));
        Assert.Equal(0UL, b7);
    }

    /// <summary>
    /// Ensures that valid ids in the 448..511 range (the lane that was
    /// vulnerable to aliasing) still work correctly after the fix.
    /// </summary>
    [Fact]
    public void TrySetBit_accepts_ids_448_to_511()
    {
        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        ulong b4 = 0, b5 = 0, b6 = 0, b7 = 0;

        // Set bit 448 (bit 0 of b7)
        Assert.True(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 448));
        Assert.Equal(1UL << 0, b7);

        // Set bit 511 (bit 63 of b7)
        Assert.True(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 511));
        Assert.Equal((1UL << 0) | (1UL << 63), b7);

        // Set bit 480 (bit 32 of b7) — middle of the lane
        Assert.True(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 480));
        Assert.Equal((1UL << 0) | (1UL << 32) | (1UL << 63), b7);

        // id 512 again after valid bits are set — must NOT touch b7
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 512));
        Assert.Equal((1UL << 0) | (1UL << 32) | (1UL << 63), b7);
    }

    /// <summary>
    /// Verifies that TrySetBit correctly returns false (already set)
    /// for duplicate calls, including the boundary at 511.
    /// </summary>
    [Fact]
    public void TrySetBit_detects_already_set_at_boundaries()
    {
        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        ulong b4 = 0, b5 = 0, b6 = 0, b7 = 0;

        // Set id 0 (first bit in b0)
        Assert.True(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 0));
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 0));

        // Set id 63 (last bit in b0)
        Assert.True(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 63));
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 63));

        // Set id 447 (last bit in b6)
        Assert.True(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 447));
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 447));

        // Set id 511 (last valid bit in b7)
        Assert.True(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 511));
        Assert.False(CommandStreamCore.TrySetBit(
            ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, 511));

        // Verify all expected bits are set
        Assert.Equal((1UL << 0) | (1UL << 63), b0);
        Assert.Equal(1UL << 63, b6);
        Assert.Equal(1UL << 63, b7);
    }

    /// <summary>
    /// Regression: mask corruption for ComponentType.Value >= 512 simulated
    /// through the MaskBuilder + ComponentMask APIs. This mirrors what
    /// MaterializeFromBatchBuffer does with TrySetBit, and verifies that
    /// setting a high id does not alias into the 448..511 range.
    /// </summary>
    [Fact]
    public void MaskBuilder_setting_high_id_does_not_alias_into_b7()
    {
        var builder = new MaskBuilder();

        // Set a bit well past 511 — must not touch any b7 bits.
        builder.SetBit(512);
        Assert.Equal(0UL, builder.B7);
        Assert.Equal(0, builder.BitsSet); // BitsSet tracks only 0..511

        // Set a real bit in b7 (id 448) to confirm the lane still works.
        builder.SetBit(448);
        Assert.Equal(1UL << 0, builder.B7);
        Assert.Equal(1, builder.BitsSet);

        // Set id 512 again — must not affect b7 or BitsSet.
        builder.SetBit(512);
        Assert.Equal(1UL << 0, builder.B7);
        Assert.Equal(1, builder.BitsSet);
    }
}
