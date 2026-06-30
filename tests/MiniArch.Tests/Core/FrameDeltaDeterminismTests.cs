using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;

namespace MiniArchTests.Core;

/// <summary>
/// Validates that FrameDelta replay is deterministic: the same delta sequence
/// replayed into two independent fresh worlds produces bit-identical observable
/// state. This is the foundational property for lockstep / rollback netcode,
/// save/replay, and cross-process state replication.
///
/// These tests deliberately inspect World internals (EntityRecords, archetype
/// Signature) via InternalsVisibleTo so that ANY divergence surfaces, not just
/// the entities the test happens to track.
/// </summary>
public sealed class FrameDeltaDeterminismTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // ══════════════════════════════════════════════════════════�?
    // Core determinism: same deltas �?identical worlds
    // ══════════════════════════════════════════════════════════�?

    [Fact]
    public void Empty_delta_sequence_keeps_both_worlds_empty()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta> { stream.Snapshot(), stream.Snapshot() };

        var a = new World();
        var b = new World();
        foreach (var d in deltas) { a.Replay(d); b.Replay(d); }

        AssertIdentical(a, b, "two empty-world replays");
    }

    [Fact]
    public void Same_delta_sequence_into_two_fresh_worlds_produces_identical_state()
    {
        var (source, deltas) = BuildComplexScenario();

        var a = new World();
        var b = new World();
        foreach (var d in deltas) { a.Replay(d); b.Replay(d); }

        AssertIdentical(a, b, "two fresh worlds, same delta sequence");
    }

    [Fact]
    public void Submit_on_source_equals_Replay_on_replica_for_safe_patterns()
    {
        // Submit and Replay use different overall ordering (BuildDelta writes
        // sectioned buffer, Submit reorders), but ReplayCore processes ops in
        // temporal order via packed byte buffer. ComponentStore.ApplyToWorld
        // and EmitToDelta iterate _kinds in the same order, so even same-frame
        // Remove+Add on the same component type converges between Submit and
        // Replay. Remaining divergence risk: Hierarchy vs Ops/Destroy relative
        // position differs between BuildDelta and Submit.
        var (source, deltas) = BuildComplexScenario();

        var replica = new World();
        foreach (var d in deltas) replica.Replay(d);

        AssertIdentical(source, replica, "Submit(source) vs Replay(replica)");
    }

    [Fact]
    public void Repeated_replay_into_N_fresh_worlds_all_match()
    {
        var (_, deltas) = BuildComplexScenario();

        var replicas = new World[5];
        for (var i = 0; i < replicas.Length; i++)
        {
            replicas[i] = new World();
            foreach (var d in deltas) replicas[i].Replay(d);
        }

        for (var i = 1; i < replicas.Length; i++)
            AssertIdentical(replicas[0], replicas[i], $"replica[{i}] vs replica[0]");
    }

    // ══════════════════════════════════════════════════════════�?
    // Targeted scenarios
    // ══════════════════════════════════════════════════════════�?

    [Fact]
    public void Determinism_survives_id_recycling()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: create victim
        var victim = stream.Create();
        stream.Add(victim, new Position(1, 2));
        stream.Add(victim, new Velocity(3, 4));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: destroy victim (id returns to free list)
        stream.Destroy(victim);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 3: new entity reuses recycled id, different archetype
        var recycled = stream.Create();
        stream.Add(recycled, new Health(100));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 4: mutate recycled
        stream.Set(recycled, new Health(200));
        stream.Add(recycled, new Position(50, 60));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        var a = new World();
        var b = new World();
        foreach (var d in deltas) { a.Replay(d); b.Replay(d); }

        AssertIdentical(a, b, "after id recycling");
        AssertIdentical(source, a, "source vs replica after id recycling");
    }

    [Fact]
    public void Determinism_survives_hierarchy_evolution()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: build tree A→B→C
        var a = stream.Create(); stream.Add(a, new Position(1, 1));
        var b = stream.Create(); stream.Add(b, new Position(2, 2));
        var c = stream.Create(); stream.Add(c, new Position(3, 3));
        stream.AddChild(a, b);
        stream.AddChild(b, c);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 2: restructure A→C, A→D, RemoveChild B
        var d = stream.Create(); stream.Add(d, new Position(4, 4));
        stream.RemoveChild(b);
        stream.RemoveChild(c);
        stream.AddChild(a, c);
        stream.AddChild(a, d);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 3: destroy leaf B, its slot frees
        stream.Destroy(b);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 4: destroy root A (cascade destroys C, D in source)
        stream.Destroy(a);
        deltas.Add(stream.Snapshot()); stream.Submit();

        var replica1 = new World();
        var replica2 = new World();
        foreach (var delta in deltas)
        { replica1.Replay(delta); replica2.Replay(delta); }

        AssertIdentical(replica1, replica2, "hierarchy evolution");
        AssertIdentical(source, replica1, "source vs replica hierarchy");
    }

    [Fact]
    public void Determinism_survives_clone_with_children()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 0: seed via deltas so replica can replay the same id sequence.
        // (If we used source.Create directly, replica's id allocator would
        // start at 0 and the clone delta referencing id 3 would be rejected.)
        var parent = stream.Create(); stream.Add(parent, new Position(1, 2));
        var child1 = stream.Create(); stream.Add(child1, new Velocity(3, 4));
        var child2 = stream.Create(); stream.Add(child2, new Health(100));
        stream.AddChild(parent, child1);
        stream.AddChild(parent, child2);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 1: clone parent (deep, includes children)
        var clone = stream.Clone(parent);
        stream.Set(clone, new Position(99, 99));
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 2: mutate a clone child
        var cloneChildren = source.EnumerateChildren(clone).ToChildList();
        foreach (var cc in cloneChildren)
            stream.Add(cc, new Health(7));
        deltas.Add(stream.Snapshot()); stream.Submit();

        var replica1 = new World();
        var replica2 = new World();
        foreach (var delta in deltas)
        { replica1.Replay(delta); replica2.Replay(delta); }

        AssertIdentical(replica1, replica2, "after clone");
        AssertIdentical(source, replica1, "source vs replica after clone");
    }

    [Fact]
    public void Determinism_across_batch_spawn_with_varied_archetypes()
    {
        const int N = 256;
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: spawn batch with 4 distinct archetype patterns
        for (var i = 0; i < N; i++)
        {
            var e = stream.Create();
            stream.Add(e, new Position(i, i + 1));
            switch (i & 3)
            {
                case 1: stream.Add(e, new Velocity(i, i)); break;
                case 2: stream.Add(e, new Health(i)); break;
                case 3:
                    stream.Add(e, new Velocity(i, i));
                    stream.Add(e, new Health(i));
                    break;
            }
        }
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 2: prune ~half, mutate rest
        var alive = new List<Entity>();
        var query = source.Query(new QueryDescription());
        foreach (var chunk in query.GetChunks())
            foreach (var e in chunk.GetEntities()) alive.Add(e);

        for (var i = 0; i < alive.Count; i++)
        {
            if ((i & 1) == 0) stream.Destroy(alive[i]);
            else stream.Set(alive[i], new Position(i * 10, i * 20));
        }
        deltas.Add(stream.Snapshot()); stream.Submit();

        var replica1 = new World();
        var replica2 = new World();
        foreach (var delta in deltas)
        { replica1.Replay(delta); replica2.Replay(delta); }

        AssertIdentical(replica1, replica2, "after batch spawn + prune");
        AssertIdentical(source, replica1, "source vs replica after batch spawn");
    }

    // ══════════════════════════════════════════════════════════�?
    // Safety net: divergent target world must reject replay
    // ══════════════════════════════════════════════════════════�?

    [Fact]
    public void Replay_into_world_with_occupied_target_slot_throws()
    {
        // Source creates entity at slot 0; replica also has slot 0 occupied
        // with a DIFFERENT version. EnsureReplayReservation must reject.
        var source = new World();
        var stream = new CommandStream(source);

        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        var delta = stream.Snapshot();

        var divergent = new World();
        // Diverge: create an entity in slot 0 directly (not via the delta)
        divergent.Create(new Position(99, 99));

        var ex = Assert.Throws<InvalidOperationException>(() => divergent.Replay(delta));
        Assert.Contains("out of sync", ex.Message);
    }

    [Fact]
    public void Replay_after_skipping_intermediate_frame_throws()
    {
        // Determinism property: deltas form a strict ordering. Skipping one
        // breaks the entity id/version contract and must be rejected.
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        var a = stream.Create(); stream.Add(a, new Position(1, 2));
        deltas.Add(stream.Snapshot()); stream.Submit();

        stream.Destroy(a);
        deltas.Add(stream.Snapshot()); stream.Submit();

        var b = stream.Create(); stream.Add(b, new Health(100));
        deltas.Add(stream.Snapshot()); stream.Submit();

        var replica = new World();
        replica.Replay(deltas[0]);
        // deliberately skip deltas[1]
        Assert.Throws<InvalidOperationException>(() => replica.Replay(deltas[2]));
    }

    // ══════════════════════════════════════════════════════════�?
    // Scenario builder
    // ══════════════════════════════════════════════════════════�?

    /// <summary>
    /// Builds a multi-frame scenario that exercises every FrameDelta command
    /// kind. ReplayCore processes ops in temporal order (packed byte buffer),
    /// so same-frame Remove+Add on the same component type converges between
    /// Submit and Replay. Returns the source world (post-Submit) and delta list.
    /// </summary>
    private static (World Source, List<FrameDelta> Deltas) BuildComplexScenario()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed world with diverse entities and hierarchy
        var a = stream.Create(); stream.Add(a, new Position(1, 2));
        var b = stream.Create(); stream.Add(b, new Position(3, 4)); stream.Add(b, new Velocity(5, 6));
        var c = stream.Create(); stream.Add(c, new Health(50));
        var d = stream.Create(); stream.Add(d, new Health(100));
        stream.AddChild(a, b);
        stream.AddChild(a, c);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 2: modify existing, create new
        stream.Set(a, new Position(100, 200));
        stream.Set(b, new Position(30, 40));
        stream.Remove<Velocity>(b);
        var e = stream.Create(); stream.Add(e, new Position(50, 60));
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 3: component add (no Remove+Add same-type), create new, AddChild
        stream.Add(d, new Position(55, 66));
        var f = stream.Create(); stream.Add(f, new Health(1));
        stream.AddChild(a, f);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 4: destroy, add unrelated type, set existing
        stream.Set(d, new Position(88, 99));
        stream.Destroy(b);
        stream.Add(e, new Velocity(1, 1));
        stream.Destroy(c);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 5: recycle destroyed id (b's slot)
        var recycled = stream.Create(); stream.Add(recycled, new Health(7));
        stream.AddChild(a, recycled);
        deltas.Add(stream.Snapshot()); stream.Submit();

        return (source, deltas);
    }

    // ══════════════════════════════════════════════════════════�?
    // Hash-based state comparison
    // ══════════════════════════════════════════════════════════�?

    private static void AssertIdentical(World a, World b, string context)
    {
        var ha = HashWorld(a);
        var hb = HashWorld(b);
        if (ha != hb)
        {
            var sa = a.GetStats();
            var sb = b.GetStats();
            Assert.Fail(
                $"Worlds diverge for [{context}].\n" +
                $"  A: ec={sa.EntityCount}, ac={sa.ArchetypeCount}, slots={sa.EntityCapacity}, hash={ha[..16]}\n" +
                $"  B: ec={sb.EntityCount}, ac={sb.ArchetypeCount}, slots={sb.EntityCapacity}, hash={hb[..16]}\n");
        }
    }

    /// <summary>
    /// Hashes a world by piping WorldSnapshot.Save output through SHA256.
    /// For lockstep scenarios (same delta sequence replayed) this is stable:
    /// archetype creation order, swap-remove history, and slot allocation
    /// all match between peers driven by identical inputs.
    /// </summary>
    private static string HashWorld(World w)
    {
        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, w);
        var span = new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        return Convert.ToHexString(SHA256.HashData(span));
    }

    // ══════════════════════════════════════════════════════════�?
    // Serialization round-trip: AsSpan �?Deserialize �?Replay
    // ══════════════════════════════════════════════════════════�?

    [Fact]
    public void Serialize_then_deserialize_of_empty_delta_is_empty()
    {
        var delta = new FrameDelta();
        var wire = delta.AsSpan();
        var restored = FrameDelta.Deserialize(wire);
        Assert.True(restored.IsEmpty);
    }

    [Fact]
    public void CB_single_delta_round_trip_produces_identical_world()
    {
        var source = new World();
        var buffer = new CommandStream(source);

        var a = buffer.Create(); buffer.Add(a, new Position(1, 2));
        var b = buffer.Create(); buffer.Add(b, new Position(3, 4)); buffer.Add(b, new Velocity(5, 6));
        buffer.AddChild(a, b);
        var delta = buffer.Snapshot(); buffer.Submit();

        var wire = delta.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        var target = new World();
        target.Replay(restored);
        AssertIdentical(source, target, "CB single delta round-trip");
    }

    [Fact]
    public void CS_single_delta_round_trip_produces_identical_world()
    {
        var source = new World();
        var stream = new CommandStream(source);

        var a = stream.Create(); stream.Add(a, new Position(10, 20));
        var b = stream.Create(); stream.Add(b, new Health(99));
        stream.AddChild(a, b);
        var delta = stream.Snapshot(); stream.Submit();

        var wire = delta.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        var target = new World();
        target.Replay(restored);
        AssertIdentical(source, target, "CS single delta round-trip");
    }

    [Fact]
    public void CB_destroy_then_recycle_round_trip_preserves_id_allocation()
    {
        var source = new World();

        var b1 = new CommandStream(source);
        var a = b1.Create(); b1.Add(a, new Position(1, 1));
        var d1 = b1.Snapshot(); b1.Submit();

        var b2 = new CommandStream(source);
        b2.Destroy(a);
        var d2 = b2.Snapshot(); b2.Submit();

        var b3 = new CommandStream(source);
        var recycled = b3.Create(); b3.Add(recycled, new Position(9, 9));
        var d3 = b3.Snapshot(); b3.Submit();

        var merged = FrameDelta.Merge(FrameDelta.Merge(d1, d2), d3);
        var wire = merged.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        var target = new World();
        target.Replay(restored);
        AssertIdentical(source, target, "CB merge destroy-recycle round-trip");
    }

    [Fact]
    public void CB_merged_delta_round_trip_produces_identical_world()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();

        var b1 = new CommandStream(source);
        var a = b1.Create(); b1.Add(a, new Position(1, 2));
        deltas.Add(b1.Snapshot()); b1.Submit();

        var b2 = new CommandStream(source);
        b2.Set(a, new Position(10, 20));
        b2.Add(a, new Velocity(3, 4));
        deltas.Add(b2.Snapshot()); b2.Submit();

        var b3 = new CommandStream(source);
        b3.Remove<Velocity>(a);
        b3.Add(a, new Health(100));
        deltas.Add(b3.Snapshot()); b3.Submit();

        var merged = deltas.Aggregate(FrameDelta.Merge);
        var wire = merged.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        var target = new World();
        target.Replay(restored);
        AssertIdentical(source, target, "CB 3-merge round-trip");
    }

    [Fact]
    public void CS_merged_delta_round_trip_produces_identical_world()
    {
        var source = new World();
        var deltas = new List<FrameDelta>();

        var s1 = new CommandStream(source);
        var a = s1.Create(); s1.Add(a, new Position(1, 1));
        deltas.Add(s1.Snapshot()); s1.Submit();

        var s2 = new CommandStream(source);
        s2.Set(a, new Position(55, 66));
        deltas.Add(s2.Snapshot()); s2.Submit();

        var merged = deltas.Aggregate(FrameDelta.Merge);
        var wire = merged.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        var target = new World();
        target.Replay(restored);
        AssertIdentical(source, target, "CS 2-merge round-trip");
    }

    [Fact]
    public void Cross_CB_and_CS_merged_delta_round_trip_is_correct()
    {
        var source = new World();

        var cb = new CommandStream(source);
        var a = cb.Create(); cb.Add(a, new Position(1, 2));
        var cbDelta = cb.Snapshot(); cb.Submit();

        var stream = new CommandStream(source);
        stream.Set(a, new Position(30, 40));
        stream.Add(a, new Velocity(5, 5));
        var csDelta = stream.Snapshot(); stream.Submit();

        var merged = FrameDelta.Merge(cbDelta, csDelta);
        var wire = merged.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        var target = new World();
        target.Replay(restored);
        AssertIdentical(source, target, "CB+CS merge round-trip");
    }

    [Fact]
    public void Complex_multi_frame_scenario_round_trip_is_correct()
    {
        var (source, deltas) = BuildComplexScenario();
        var merged = deltas.Aggregate(FrameDelta.Merge);
        var wire = merged.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        var target = new World();
        target.Replay(restored);
        AssertIdentical(source, target, "complex 5-frame merged round-trip");
    }

    [Fact]
    public void Serialize_deserialize_preserves_DeltaCount()
    {
        var source = new World();
        var buffer = new CommandStream(source);
        var a = buffer.Create(); buffer.Add(a, new Position(1, 2));
        buffer.Set(a, new Position(3, 4));
        buffer.Add(a, new Velocity(5, 6));
        var delta = buffer.Snapshot(); buffer.Submit();

        var count = delta.DeltaCount;
        var wire = delta.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        Assert.Equal(count, restored.DeltaCount);
    }

    [Fact]
    public void Serialize_deserialize_preserves_HasEntity()
    {
        var source = new World();
        var buffer = new CommandStream(source);
        var a = buffer.Create(); buffer.Add(a, new Position(1, 2));
        var b = buffer.Create();
        buffer.AddChild(a, b);
        var delta = buffer.Snapshot(); buffer.Submit();

        Assert.True(delta.HasEntity(a));
        Assert.True(delta.HasEntity(b));

        var wire = delta.AsSpan();
        var restored = FrameDelta.Deserialize(wire);

        Assert.True(restored.HasEntity(a));
        Assert.True(restored.HasEntity(b));
        Assert.False(restored.HasEntity(new Entity(9999, 1)));
    }

    // ══════════════════════════════════════════════════════════�?
    // Malformed / hostile input must fail loud, not silently corrupt state.
    // Lockstep/multiplayer depends on the consumer rejecting bad deltas at
    // the first sign of trouble rather than skipping unknown bytes.
    // ══════════════════════════════════════════════════════════�?

    [Fact]
    public void Deserialize_rejects_unknown_op_kind()
    {
        // 0xFF is not a valid DeltaOpKind (valid range is 0x01..0x09).
        var wire = new byte[] { 0xFF, 0x01, 0x01 };
        Assert.Throws<InvalidOperationException>(() => FrameDelta.Deserialize(wire));
    }

    [Fact]
    public void Deserialize_rejects_zero_op_kind()
    {
        // 0x00 is also not a valid op kind.
        var wire = new byte[] { 0x00, 0x01, 0x01 };
        Assert.Throws<InvalidOperationException>(() => FrameDelta.Deserialize(wire));
    }

    [Fact]
    public void Deserialize_rejects_truncated_varint_at_end_of_buffer()
    {
        // Reserve op (0x01) with entity id varint that has the continuation
        // bit set (0x80) but no following byte.
        var wire = new byte[] { 0x01, 0x80 };
        Assert.Throws<InvalidOperationException>(() => FrameDelta.Deserialize(wire));
    }

    [Fact]
    public void Deserialize_rejects_varint_exceeding_32_bit_range()
    {
        // 5 bytes each with continuation bit set, plus a 6th byte �?a varint
        // wider than 32 bits is not representable as int and must be rejected.
        var wire = new byte[] { 0x01, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01, 0x01 };
        Assert.Throws<InvalidOperationException>(() => FrameDelta.Deserialize(wire));
    }

    [Fact]
    public void Deserialize_rejects_truncated_op_payload()
    {
        // Add op (0x06): entity(2 bytes) + component type (1 byte) + size varint
        // claiming 10 bytes of data, but the buffer ends immediately.
        var wire = new byte[] { 0x06, 0x01, 0x01, 0x01, 10 };
        Assert.Throws<InvalidOperationException>(() => FrameDelta.Deserialize(wire));
    }

    // ══════════════════════════════════════════════════════════�?
    // Hierarchy × Ops × Destroy same-frame convergence
    // (Submit and Replay must produce identical state when these
    // command kinds are mixed within a single frame; previously
    // diverged because BuildDelta wrote Hierarchy before Ops while
    // Submit ran Ops before Hierarchy.)
    // ══════════════════════════════════════════════════════════�?

    [Fact]
    public void Submit_link_and_set_on_same_child_same_frame_converges_with_replay()
    {
        var source = new World();
        var buffer = new CommandStream(source);
        var parent = buffer.Create(); buffer.Add(parent, new Position(0, 0));
        var child = buffer.Create(); buffer.Add(child, new Position(1, 1));
        buffer.AddChild(parent, child);
        buffer.Set(child, new Position(99, 99));
        var delta = buffer.Snapshot(); buffer.Submit();

        var replica = new World();
        replica.Replay(delta);

        AssertIdentical(source, replica, "AddChild + Set same frame");
    }

    [Fact]
    public void Submit_link_parent_then_destroy_parent_same_frame_converges_with_replay()
    {
        var source = new World();

        // Frame 1: establish parent + child linked
        var buffer1 = new CommandStream(source);
        var parent = buffer1.Create(); buffer1.Add(parent, new Position(0, 0));
        var child = buffer1.Create(); buffer1.Add(child, new Position(1, 1));
        buffer1.AddChild(parent, child);
        var delta1 = buffer1.Snapshot(); buffer1.Submit();

        // Frame 2: create parent2, AddChild to child, then destroy parent2 �?all same frame
        var buffer2 = new CommandStream(source);
        var parent2 = buffer2.Create(); buffer2.Add(parent2, new Position(2, 2));
        buffer2.AddChild(parent2, child);
        buffer2.Destroy(parent2);
        var delta2 = buffer2.Snapshot(); buffer2.Submit();

        var replica = new World();
        replica.Replay(delta1);
        replica.Replay(delta2);

        AssertIdentical(source, replica, "AddChild + Destroy parent same frame");
    }

    [Fact]
    public void Submit_create_link_add_combined_same_frame_converges_with_replay()
    {
        var source = new World();
        var buffer = new CommandStream(source);
        var parent = buffer.Create();
        var child = buffer.Create();
        buffer.Add(parent, new Position(10, 20));
        buffer.Add(child, new Position(30, 40));
        buffer.Add(child, new Velocity(5, 5));
        buffer.AddChild(parent, child);
        var delta = buffer.Snapshot(); buffer.Submit();

        var replica = new World();
        replica.Replay(delta);

        AssertIdentical(source, replica, "Create + AddChild + Add same frame");
    }

    [Fact]
    public void Submit_unlink_then_set_same_frame_converges_with_replay()
    {
        var source = new World();

        var buffer1 = new CommandStream(source);
        var parent = buffer1.Create(); buffer1.Add(parent, new Position(0, 0));
        var child = buffer1.Create(); buffer1.Add(child, new Position(1, 1));
        buffer1.AddChild(parent, child);
        var delta1 = buffer1.Snapshot(); buffer1.Submit();

        var buffer2 = new CommandStream(source);
        buffer2.RemoveChild(child);
        buffer2.Set(child, new Position(77, 88));
        var delta2 = buffer2.Snapshot(); buffer2.Submit();

        var replica = new World();
        replica.Replay(delta1);
        replica.Replay(delta2);

        AssertIdentical(source, replica, "RemoveChild + Set same frame");
    }

    [Fact]
    public void Submit_set_then_unlink_same_frame_converges_with_replay()
    {
        // Variant: ops before hierarchy (AddChild) in source recording order.
        var source = new World();

        var buffer1 = new CommandStream(source);
        var parent = buffer1.Create(); buffer1.Add(parent, new Position(0, 0));
        var child = buffer1.Create(); buffer1.Add(child, new Position(1, 1));
        buffer1.AddChild(parent, child);
        var delta1 = buffer1.Snapshot(); buffer1.Submit();

        var buffer2 = new CommandStream(source);
        buffer2.Set(child, new Position(77, 88));
        buffer2.RemoveChild(child);
        var delta2 = buffer2.Snapshot(); buffer2.Submit();

        var replica = new World();
        replica.Replay(delta1);
        replica.Replay(delta2);

        AssertIdentical(source, replica, "Set + RemoveChild same frame");
    }
}
