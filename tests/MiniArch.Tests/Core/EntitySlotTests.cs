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

    // ── Struct copy semantics ────────────────────────────────────

    [Fact]
    public void Track_copied_slots_all_resolve_to_same_entity()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());

        // Value-type copies —all share the same Slot reference.
        var copy1 = slot;
        var copy2 = slot;

        Assert.True(slot.Value.IsPlaceholder);
        Assert.True(copy1.Value.IsPlaceholder);
        Assert.True(copy2.Value.IsPlaceholder);

        stream.Submit();

        // All copies see the resolved entity.
        Assert.False(slot.Value.IsPlaceholder);
        Assert.False(copy1.Value.IsPlaceholder);
        Assert.False(copy2.Value.IsPlaceholder);

        Assert.Equal(slot.Value, copy1.Value);
        Assert.Equal(slot.Value, copy2.Value);
        Assert.True(world.IsAlive(slot.Value));
        Assert.True(world.IsAlive(copy1.Value));
        Assert.True(world.IsAlive(copy2.Value));
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
    public void Track_Clear_without_Snapshot_discards_stale_registrations()
    {
        // Regression: Clear() without preceding Snapshot() must not leave
        // tracked slots pending for a future Replay to resolve.
        var world = new World();
        var stream = MakeStream(world);

        var oldSlot = stream.Track(stream.Create());
        stream.Clear();  // abandon frame —no Snapshot was called

        // New frame: create+track a different entity.
        var freshSlot = stream.Track(stream.Create());
        stream.Submit();

        // Old slot is abandoned —it must stay as placeholder.
        Assert.True(oldSlot.Value.IsPlaceholder,
            $"oldSlot={oldSlot.Value} should be placeholder (abandoned frame).");

        // Fresh slot resolved correctly.
        Assert.False(freshSlot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(freshSlot.Value));
    }

    [Fact]
    public void Track_default_EntitySlot_returns_default_entity()
    {
        EntitySlot slot = default;
        Assert.Equal(default(Entity), slot.Value);
        Assert.False(slot.HasValue);
    }

    [Fact]
    public void Track_implicit_conversion_works_before_and_after_Submit()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());

        // Before Submit: implicit conversion returns placeholder.
        Entity before = slot;
        Assert.True(before.IsPlaceholder);

        stream.Add(slot, new Health(7));  // implicit conversion in API call
        stream.Submit();

        // After Submit: implicit conversion returns real entity.
        Entity after = slot;
        Assert.False(after.IsPlaceholder);
        Assert.True(world.IsAlive(after));
        Assert.True(world.TryGet(after, out Health hp));
        Assert.Equal(7, hp.Value);
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

        // Relay-only: Clear done, Replay not yet —slot is still placeholder.
        Assert.True(slot.Value.IsPlaceholder);

        // Replay own delta —explicit resolveSlots triggers slot resolution.
        stream.Replay(delta, resolveSlots: true);

        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
        Assert.True(world.TryGet(slot.Value, out Health hp));
        Assert.Equal(99, hp.Value);
    }

    [Fact]
    public void Track_relay_only_source_replays_own_delta_among_peers()
    {
        // Relay-only: source host records, Snapshot+Clear (no Submit),
        // then replays own delta alongside peer deltas.
        var worldSrc = new World();
        var worldPeer = new World();
        var streamSrc = MakeStream(worldSrc);

        var slot = streamSrc.Track(streamSrc.Create());
        streamSrc.Add(slot.Value, new Health(7));

        var delta = streamSrc.Snapshot();
        streamSrc.Clear();

        // Source: still placeholder until Replay.
        Assert.True(slot.Value.IsPlaceholder);

        // Simulate network: peer receives serialized copy.
        var bytes = delta.AsSpan().ToArray();
        var peerDelta = FrameDelta.FromWire(bytes);
        var streamPeer = MakeStream(worldPeer);

        // Source replays own delta with resolveSlots: true.
        streamSrc.Replay(delta, resolveSlots: true);
        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(worldSrc.IsAlive(slot.Value));

        // Peer replays deserialized copy —no slots to resolve on peer side.
        streamPeer.Replay(peerDelta);
        // Peer's world has the entity too (deterministic replay).
        // Source slot still points to source's local entity.
        Assert.True(worldSrc.IsAlive(slot.Value));
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

        // Host B replays both deltas. deltaA is a peer delta.
        streamB.Replay(deltaA);
        // slotB should NOT be resolved yet —deltaB hasn't been replayed.
        Assert.True(slotB.Value.IsPlaceholder);

        streamB.Replay(deltaB, resolveSlots: true);
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
        streamA.Replay(deltaA, resolveSlots: true);  // own —resolves slotA
        streamA.Replay(deltaB);                       // peer —no effect on slotA
        Assert.True(worldA.IsAlive(slotA.Value));

        // Host B:
        streamB.Replay(deltaA);                       // peer —no effect on slotB
        streamB.Replay(deltaB, resolveSlots: true);  // own —resolves slotB
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
        stream.Replay(delta1, resolveSlots: true);

        var realEntity = slot.Value;
        Assert.True(world.IsAlive(realEntity));

        // Frame 2: new create + track + snapshot + clear + replay
        var slot2 = stream.Track(stream.Create());
        stream.Add(slot2.Value, new Health(2));
        var delta2 = stream.Snapshot();
        stream.Clear();
        stream.Replay(delta2, resolveSlots: true);

        // Original slot still valid.
        Assert.Equal(realEntity, slot.Value);
        Assert.True(world.IsAlive(slot.Value));
        Assert.True(world.IsAlive(slot2.Value));
    }

    [Fact]
    public void Track_Replay_deserialized_delta_resolves_with_explicit_flag()
    {
        // Deserialized delta (e.g. received via network) can still resolve
        // slots —the user just passes resolveSlots: true explicitly.
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        var delta = stream.Snapshot();
        stream.Clear();

        // Serialize and deserialize —simulates network round-trip.
        var bytes = delta.AsSpan().ToArray();
        var deserialized = FrameDelta.FromWire(bytes);

        // Default: no resolution.
        // (Use a fresh world so we can replay the same delta twice without
        // double-applying.)
        var world2 = new World();
        var stream2 = MakeStream(world2);
        var slot2 = stream2.Track(stream2.Create());
        stream2.Snapshot();
        stream2.Clear();
        stream2.Replay(deserialized);
        Assert.True(slot2.Value.IsPlaceholder);

        // Explicit resolveSlots: true triggers resolution.
        stream.Replay(deserialized, resolveSlots: true);
        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
    }
}
