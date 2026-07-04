using MiniArch.Core;

namespace MiniArchTests.Core;

/// <summary>
/// Tests for <see cref="CommandStream.Track"/> and <see cref="EntitySlot"/>
/// resolution via the Submit path.
/// </summary>
public sealed class EntitySlotTests
{
    private readonly record struct Health(int Value);
    private readonly record struct Linked(int Id, Entity Target);

    private static CommandStream MakeStream(World world, bool deferred = true)
    {
        return new CommandStream(world) { DeferredEntities = deferred };
    }

    // ── Submit path ───────────────────────────────────────────────

    [Fact]
    public void Track_Submit_resolves_placeholder_to_real_entity()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        Assert.True(slot.Value.IsPlaceholder);  // before Submit

        stream.Submit();

        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
    }

    [Fact]
    public void Track_Submit_slot_value_usable_for_component_access()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        stream.Add(slot.Value, new Health(42));
        stream.Submit();

        Assert.True(world.TryGet(slot.Value, out Health hp));
        Assert.Equal(42, hp.Value);
    }

    [Fact]
    public void Track_multiple_placeholders_all_resolve_on_Submit()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slotA = stream.Track(stream.Create());
        var slotB = stream.Track(stream.Create());
        var slotC = stream.Track(stream.Create());

        stream.Submit();

        Assert.True(world.IsAlive(slotA.Value));
        Assert.True(world.IsAlive(slotB.Value));
        Assert.True(world.IsAlive(slotC.Value));
        Assert.NotEqual(slotA.Value, slotB.Value);
        Assert.NotEqual(slotB.Value, slotC.Value);
    }

    [Fact]
    public void Track_multiple_trackers_on_same_placeholder_all_resolve()
    {
        var world = new World();
        var stream = MakeStream(world);

        var placeholder = stream.Create();
        var slot1 = stream.Track(placeholder);
        var slot2 = stream.Track(placeholder);
        var slot3 = stream.Track(placeholder);

        stream.Submit();

        Assert.Equal(slot1.Value, slot2.Value);
        Assert.Equal(slot2.Value, slot3.Value);
        Assert.True(world.IsAlive(slot1.Value));
    }

    [Fact]
    public void Track_non_deferred_mode_returns_real_entity_immediately()
    {
        var world = new World();
        var stream = MakeStream(world, deferred: false);

        var slot = stream.Track(stream.Create());
        // Non-deferred: Create returns real entity, slot stores it inline.
        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(slot.Value.Id >= 0);

        stream.Submit();
        Assert.True(world.IsAlive(slot.Value));
    }

    [Fact]
    public void Track_slot_value_survives_across_frames()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        stream.Submit();

        var realEntity = slot.Value;
        Assert.True(world.IsAlive(realEntity));

        // Simulate next frame: create and submit more entities.
        stream.Clear();
        var other = stream.Create();
        stream.Submit();

        // Original slot still holds the same entity.
        Assert.Equal(realEntity, slot.Value);
        Assert.True(world.IsAlive(slot.Value));
    }

    [Fact]
    public void Track_cancelled_entity_slot_stays_placeholder()
    {
        var world = new World();
        var stream = MakeStream(world);

        var e = stream.Create();
        stream.Destroy(e);
        var slot = stream.Track(e);
        stream.Submit();

        var val = slot.Value;
        Assert.True(val.IsPlaceholder,
            $"val={val} IsPlaceholder={val.IsPlaceholder}.");
    }

    [Fact]
    public void Track_default_EntitySlot_returns_default_entity()
    {
        EntitySlot slot = default;
        Assert.Equal(default(Entity), slot.Value);
        Assert.False(slot.HasValue);
    }

    // ── Replay path (lockstep relay) ──────────────────────────────

    [Fact]
    public void Track_Replay_own_delta_resolves_slot()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        stream.Add(slot.Value, new Health(99));

        var delta = stream.Snapshot();
        stream.Clear();

        // Replay own delta —should auto-resolve slot.
        stream.Replay(delta);

        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
        Assert.True(world.TryGet(slot.Value, out Health hp));
        Assert.Equal(99, hp.Value);
    }

    [Fact]
    public void Track_Replay_peer_delta_does_not_resolve_slot()
    {
        // Host A records and tracks.
        var hostA = new World();
        var streamA = MakeStream(hostA);

        var slotA = streamA.Track(streamA.Create());
        var deltaA = streamA.Snapshot();
        streamA.Clear();

        // Host B: has its own stream, replays host A's delta.
        var hostB = new World();
        var streamB = MakeStream(hostB);
        var slotB = streamB.Track(streamB.Create());
        var deltaB = streamB.Snapshot();
        streamB.Clear();

        // Host B replays both deltas. deltaA is NOT streamB's own (OriginStream != streamB).
        streamB.Replay(deltaA);
        // slotB should NOT be resolved yet —deltaB hasn't been replayed.
        Assert.True(slotB.Value.IsPlaceholder);

        streamB.Replay(deltaB);
        // NOW slotB resolves —deltaB is streamB's own.
        Assert.False(slotB.Value.IsPlaceholder);
        Assert.True(hostB.IsAlive(slotB.Value));
    }

    [Fact]
    public void Track_Replay_multiple_deltas_only_resolves_own()
    {
        // Two hosts, each with own stream and tracked entity.
        var worldA = new World();
        var worldB = new World();
        var streamA = MakeStream(worldA);
        var streamB = MakeStream(worldB);

        var slotA = streamA.Track(streamA.Create());
        var slotB = streamB.Track(streamB.Create());

        var deltaA = streamA.Snapshot();
        var deltaB = streamB.Snapshot();
        streamA.Clear();
        streamB.Clear();

        // Each host replays both deltas in order.
        // Host A:
        streamA.Replay(deltaA);  // own —resolves slotA
        streamA.Replay(deltaB);  // peer —no effect on slotA
        Assert.True(worldA.IsAlive(slotA.Value));

        // Host B:
        streamB.Replay(deltaA);  // peer —no effect on slotB
        streamB.Replay(deltaB);  // own —resolves slotB
        Assert.True(worldB.IsAlive(slotB.Value));

        // Slots resolved to different entities (different hosts' entities).
        Assert.NotEqual(slotA.Value, slotB.Value);
    }

    [Fact]
    public void Track_Replay_slot_survives_across_frames()
    {
        var world = new World();
        var stream = MakeStream(world);

        // Frame 1: create + track + snapshot + clear + replay
        var slot = stream.Track(stream.Create());
        stream.Add(slot.Value, new Health(1));
        var delta1 = stream.Snapshot();
        stream.Clear();
        stream.Replay(delta1);

        var realEntity = slot.Value;
        Assert.True(world.IsAlive(realEntity));

        // Frame 2: new create + track + snapshot + clear + replay
        var slot2 = stream.Track(stream.Create());
        stream.Add(slot2.Value, new Health(2));
        var delta2 = stream.Snapshot();
        stream.Clear();
        stream.Replay(delta2);

        // Original slot still valid.
        Assert.Equal(realEntity, slot.Value);
        Assert.True(world.IsAlive(slot.Value));
        Assert.True(world.IsAlive(slot2.Value));
    }

    [Fact]
    public void Track_Replay_deserialized_delta_does_not_trigger_resolution()
    {
        // Simulate receiving own delta back via network (deserialized).
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        var delta = stream.Snapshot();
        stream.Clear();

        // Serialize and deserialize —loses OriginStream.
        var bytes = delta.AsSpan().ToArray();
        var deserialized = FrameDelta.Deserialize(bytes);

        stream.Replay(deserialized);
        // Deserialized delta has OriginStream == null —not recognized as own.
        // Slot is NOT resolved (placeholder still).
        Assert.True(slot.Value.IsPlaceholder);

        // Now replay the ORIGINAL delta —resolves correctly.
        stream.Replay(delta);
        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
    }
}
