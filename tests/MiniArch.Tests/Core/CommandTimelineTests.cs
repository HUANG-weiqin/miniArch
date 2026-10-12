using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class CommandTimelineTests
{
    private readonly record struct Position(int Value);

    private readonly struct PositionWriter : ICreateManyWriter<Position>
    {
        public void Write(int index, Entity entity, out Position component)
            => component = new Position(index);
    }

    private readonly struct DuplicatePositionWriter : ICreateManyWriter<Position, Position>
    {
        public void Write(int index, Entity entity, out Position first, out Position second)
            => first = second = new Position(index);
    }

    public enum ConsumeMode
    {
        Submit,
        Snapshot,
        SnapshotInto,
        Async,
        AsyncInto
    }

    [Fact]
    public void BUG_Capture_rejects_outstanding_reservations_without_recycling_an_existing_snapshot()
    {
        using var world = new World();
        using var checkpoint = world.CaptureState();
        var stream = new CommandStream(world);
        var reserved = stream.Create();

        Assert.Throws<InvalidOperationException>(() => world.CaptureState());
        Assert.False(checkpoint.IsRecycled);

        stream.Destroy(reserved);
        using var afterCancellation = world.CaptureState();
        Assert.False(afterCancellation.IsRecycled);
        stream.Clear();
        world.RestoreState(checkpoint);
        Assert.True(checkpoint.IsRecycled);
        using var next = world.CaptureState();
        Assert.False(next.IsRecycled);
    }

    [Theory]
    [InlineData(ConsumeMode.Submit)]
    [InlineData(ConsumeMode.Snapshot)]
    [InlineData(ConsumeMode.SnapshotInto)]
    [InlineData(ConsumeMode.Async)]
    [InlineData(ConsumeMode.AsyncInto)]
    public void BUG_Pure_set_frame_is_rejected_after_restore_at_every_consume_entry(ConsumeMode mode)
    {
        using var world = new World();
        var entity = world.Create(new Position(1));
        var stream = new CommandStream(world);
        stream.Set(entity, new Position(2));
        using var checkpoint = world.CaptureState();
        world.RestoreState(checkpoint);

        var target = new FrameDelta();
        target.AddDestroy(new Entity(42, 1));
        var targetBefore = target.AsSpan().ToArray();
        void Consume()
        {
            switch (mode)
            {
                case ConsumeMode.Submit: _ = stream.Submit(); break;
                case ConsumeMode.Snapshot: _ = stream.Snapshot(); break;
                case ConsumeMode.SnapshotInto: stream.SnapshotInto(target); break;
                case ConsumeMode.Async: _ = stream.SubmitAndSnapshotAsync(); break;
                case ConsumeMode.AsyncInto: _ = stream.SubmitAndSnapshotIntoAsync(target); break;
            }
        }

        Assert.Throws<InvalidOperationException>(Consume);
        Assert.Throws<InvalidOperationException>(() => stream.Validate());
        Assert.Throws<InvalidOperationException>(() => stream.Clone(entity));
        Assert.Equal(new Position(1), world.Get<Position>(entity));
        Assert.Equal(targetBefore, target.AsSpan().ToArray());

        stream.Clear();
        stream.Set(entity, new Position(3));
        Assert.True(stream.Submit());
        Assert.Equal(new Position(3), world.Get<Position>(entity));
    }

    [Theory]
    [InlineData(ConsumeMode.Snapshot)]
    [InlineData(ConsumeMode.SnapshotInto)]
    public void BUG_Deferred_snapshot_rejects_cancelled_immediate_batches(ConsumeMode mode)
    {
        using var source = new World();
        using var replica = new World();
        var stream = new CommandStream(source);
        var canceled = stream.Create();
        stream.Destroy(canceled);
        stream.DeferredEntities = true;

        var target = new FrameDelta();
        target.AddDestroy(new Entity(42, 1));
        var targetBefore = target.AsSpan().ToArray();
        var sourceBefore = source.CanonicalChecksum();
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (mode == ConsumeMode.Snapshot)
                _ = stream.Snapshot();
            else
                stream.SnapshotInto(target);
        });
        Assert.Equal(targetBefore, target.AsSpan().ToArray());
        Assert.Equal(sourceBefore, source.CanonicalChecksum());

        // Real-id mode must still mirror the allocator events from cancellation.
        stream.DeferredEntities = false;
        var cancellation = stream.Snapshot();
        Assert.Equal(2, cancellation.DeltaCount);
        cancellation.Validate();
        stream.Clear();
        var replicaStream = new CommandStream(replica);
        replicaStream.Replay(cancellation);
        Assert.Equal(source.CanonicalChecksum(), replica.CanonicalChecksum());

        stream.DeferredEntities = true;
        var next = stream.Track(stream.Create());
        var delta = stream.Snapshot();
        delta.Validate();
        stream.Clear();
        stream.Replay(delta, resolveSlots: true);
        replicaStream.Replay(delta);
        Assert.Equal(new Entity(canceled.Id, canceled.Version + 1), next.Value);
        Assert.Equal(source.CanonicalChecksum(), replica.CanonicalChecksum());
        Assert.Equal(source.CreateEmpty(), replica.CreateEmpty());
    }

    [Fact]
    public void Deferred_draft_can_be_captured_but_not_consumed_after_restore()
    {
        using var world = new World();
        var stream = new CommandStream(world) { DeferredEntities = true };
        stream.Create();
        using var checkpoint = world.CaptureState();
        world.RestoreState(checkpoint);

        Assert.Throws<InvalidOperationException>(() => stream.Snapshot());
        stream.Clear();
        var current = stream.Create();
        Assert.True(current.IsPlaceholder);
        Assert.True(stream.Submit());
        Assert.Equal(1, world.EntityCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BUG_Stale_clear_cannot_release_another_streams_identical_reservation(bool canceled)
    {
        using var world = new World();
        using var checkpoint = world.CaptureState();
        var stale = new CommandStream(world);
        var old = stale.Create();
        if (canceled)
            stale.Destroy(old);

        world.RestoreState(checkpoint);
        var current = new CommandStream(world);
        var fresh = current.Create();
        Assert.Equal(old, fresh);
        Assert.Throws<InvalidOperationException>(() => stale.Create());
        if (!canceled)
            Assert.Throws<InvalidOperationException>(() => stale.Destroy(fresh));
        Assert.Throws<InvalidOperationException>(() => stale.Submit());

        stale.Clear();
        Assert.True(current.Submit());
        Assert.True(world.IsAlive(fresh));

        var rebound = stale.Create();
        Assert.True(stale.Submit());
        Assert.True(world.IsAlive(rebound));
    }

    [Fact]
    public void Empty_and_failed_recording_do_not_bind_a_stream_to_an_old_timeline()
    {
        using var world = new World();
        using var checkpoint = world.CaptureState();
        var stream = new CommandStream(world);
        Assert.Throws<InvalidOperationException>(() => stream.Clone(new Entity(99, 0)));
        stream.CreateMany<Position, PositionWriter>(Array.Empty<Entity>(), new PositionWriter());

        world.RestoreState(checkpoint);
        var entity = world.Create(new Position(1));
        stream.Set(entity, new Position(2));
        Assert.True(stream.Submit());
        Assert.Equal(new Position(2), world.Get<Position>(entity));
    }

    [Fact]
    public void A_frame_pruned_to_empty_can_record_after_restore()
    {
        using var world = new World();
        var entity = world.Create(new Position(1));
        using var checkpoint = world.CaptureState();
        var stream = new CommandStream(world);
        stream.Set(entity, new Position(2));
        world.Destroy(entity);
        Assert.False(stream.Submit());

        world.RestoreState(checkpoint);
        stream.Set(entity, new Position(3));
        Assert.True(stream.Submit());
        Assert.Equal(new Position(3), world.Get<Position>(entity));
    }

    [Fact]
    public void Snapshot_clear_and_own_replay_can_resolve_slots_after_restore()
    {
        using var world = new World();
        using var checkpoint = world.CaptureState();
        var stream = new CommandStream(world) { DeferredEntities = true };
        var placeholder = stream.Create();
        var slot = stream.Track(placeholder);
        stream.Add(placeholder, new Position(7));
        var delta = stream.Snapshot();
        stream.Clear();

        world.RestoreState(checkpoint);
        stream.Replay(delta, resolveSlots: true);
        Assert.True(world.IsAlive(slot.Value));
        Assert.Equal(new Position(7), world.Get<Position>(slot.Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Async_results_remain_replayable_when_awaited_after_restore(int frameCount)
    {
        using var world = new World();
        var entity = world.Create(new Position(0));
        using var checkpoint = world.CaptureState();
        var stream = new CommandStream(world);
        var tasks = new Task<FrameDelta>[frameCount];

        for (var i = 0; i < frameCount; i++)
        {
            stream.Set(entity, new Position(i + 1));
            tasks[i] = stream.SubmitAndSnapshotAsync();
        }

        world.RestoreState(checkpoint);
        stream.Set(entity, new Position(99));
        Assert.True(stream.Submit());
        Assert.Equal(new Position(99), world.Get<Position>(entity));

        var deltas = await Task.WhenAll(tasks);
        for (var i = 0; i < frameCount; i++)
        {
            stream.Replay(deltas[i]);
            Assert.Equal(new Position(i + 1), world.Get<Position>(entity));
        }
    }

    [Theory]
    [InlineData(ConsumeMode.Snapshot, false)]
    [InlineData(ConsumeMode.SnapshotInto, false)]
    [InlineData(ConsumeMode.Async, false)]
    [InlineData(ConsumeMode.AsyncInto, false)]
    [InlineData(ConsumeMode.Snapshot, true)]
    [InlineData(ConsumeMode.SnapshotInto, true)]
    [InlineData(ConsumeMode.Async, true)]
    [InlineData(ConsumeMode.AsyncInto, true)]
    public async Task Prepared_wire_and_typed_apply_preserve_cancelled_batch_parity(
        ConsumeMode mode, bool deferred)
    {
        using var source = new World();
        using var replica = new World();
        var sourceEntity = source.Create(new Position(1));
        var replicaEntity = replica.Create(new Position(1));
        Assert.Equal(sourceEntity, replicaEntity);
        var stream = new CommandStream(source) { DeferredEntities = deferred };
        var canceled = stream.Create();
        stream.Destroy(canceled);
        var live = stream.Create();
        stream.Add(live, new Position(2));
        stream.Set(sourceEntity, new Position(3));

        FrameDelta delta;
        switch (mode)
        {
            case ConsumeMode.Snapshot:
                delta = stream.Snapshot();
                Assert.True(stream.Submit());
                break;
            case ConsumeMode.SnapshotInto:
                delta = new FrameDelta();
                stream.SnapshotInto(delta);
                Assert.True(stream.Submit());
                break;
            case ConsumeMode.Async:
                delta = await stream.SubmitAndSnapshotAsync();
                break;
            default:
                delta = new FrameDelta();
                await stream.SubmitAndSnapshotIntoAsync(delta);
                break;
        }

        delta.Validate();
        new CommandStream(replica).Replay(delta);
        Assert.Equal(source.CanonicalChecksum(), replica.CanonicalChecksum());
        Assert.Equal(source.CreateEmpty(), replica.CreateEmpty());
    }

    [Theory]
    [InlineData(ConsumeMode.Submit, false)]
    [InlineData(ConsumeMode.Snapshot, false)]
    [InlineData(ConsumeMode.SnapshotInto, false)]
    [InlineData(ConsumeMode.Async, false)]
    [InlineData(ConsumeMode.AsyncInto, false)]
    [InlineData(ConsumeMode.Submit, true)]
    [InlineData(ConsumeMode.Snapshot, true)]
    [InlineData(ConsumeMode.SnapshotInto, true)]
    [InlineData(ConsumeMode.Async, true)]
    [InlineData(ConsumeMode.AsyncInto, true)]
    public void BUG_CreateMany_group_contract_is_consistent_across_consumers(
        ConsumeMode mode, bool duplicateTypes)
    {
        using var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[2];
        if (duplicateTypes)
            stream.CreateMany<Position, Position, DuplicatePositionWriter>(entities, new DuplicatePositionWriter());
        else
        {
            stream.CreateMany<Position, PositionWriter>(entities, new PositionWriter());
            stream.Remove<Position>(entities[1]);
        }

        var target = new FrameDelta();
        target.AddDestroy(new Entity(42, 1));
        var targetBefore = target.AsSpan().ToArray();
        void Consume()
        {
            switch (mode)
            {
                case ConsumeMode.Submit: _ = stream.Submit(); break;
                case ConsumeMode.Snapshot: _ = stream.Snapshot(); break;
                case ConsumeMode.SnapshotInto: stream.SnapshotInto(target); break;
                case ConsumeMode.Async: _ = stream.SubmitAndSnapshotAsync(); break;
                case ConsumeMode.AsyncInto: _ = stream.SubmitAndSnapshotIntoAsync(target); break;
            }
        }

        Assert.Throws<InvalidOperationException>(Consume);
        Assert.Equal(0, world.EntityCount);
        Assert.Equal(targetBefore, target.AsSpan().ToArray());
        stream.Clear();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BUG_Recycled_frozen_pending_map_does_not_treat_live_entities_as_pending(bool cloneSource)
    {
        using var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[4];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = stream.Create();
            stream.Add(entities[i], new Position(i));
        }
        await stream.SubmitAndSnapshotAsync();

        world.Destroy(entities[0]);
        stream.Set(entities[2], new Position(27));
        await stream.SubmitAndSnapshotAsync();

        stream.Create();
        stream.Create();
        if (cloneSource)
        {
            var clone = stream.Clone(entities[2]);
            Assert.True(stream.Submit());
            Assert.Equal(new Position(27), world.Get<Position>(clone));
        }
        else
        {
            stream.Set(entities[2], new Position(99));
            Assert.True(stream.Submit());
            Assert.Equal(new Position(99), world.Get<Position>(entities[2]));
        }
    }

    [Fact]
    public void Fully_cancelled_CreateMany_group_does_not_require_a_live_archetype()
    {
        using var world = new World();
        var stream = new CommandStream(world);
        var entities = new Entity[2];
        stream.CreateMany<Position, Position, DuplicatePositionWriter>(entities, new DuplicatePositionWriter());
        foreach (var entity in entities)
            stream.Destroy(entity);

        Assert.True(stream.Submit());
        Assert.Equal(0, world.EntityCount);
    }
}
