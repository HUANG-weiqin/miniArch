using System.IO;
using System.Security.Cryptography;
using MiniArch.Core;

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

    // ═══════════════════════════════════════════════════════════
    // Core determinism: same deltas → identical worlds
    // ═══════════════════════════════════════════════════════════

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
        // Submit and Replay use different command ordering
        // (Submit = recording order, Replay = canonical Reserved→Released→Created
        // →Link→Unlink→Add→Set→Remove→Destroy). For scenarios that avoid
        // same-frame Remove+Add on the same component type, both must converge.
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

    // ═══════════════════════════════════════════════════════════
    // Targeted scenarios
    // ═══════════════════════════════════════════════════════════

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
        stream.Link(a, b);
        stream.Link(b, c);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 2: restructure A→C, A→D, unlink B
        var d = stream.Create(); stream.Add(d, new Position(4, 4));
        stream.Unlink(b);
        stream.Unlink(c);
        stream.Link(a, c);
        stream.Link(a, d);
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
        stream.Link(parent, child1);
        stream.Link(parent, child2);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 1: clone parent (deep, includes children)
        var clone = stream.Clone(parent);
        stream.Set(clone, new Position(99, 99));
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 2: mutate a clone child
        var cloneChildren = source.GetChildren(clone);
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

    // ═══════════════════════════════════════════════════════════
    // Safety net: divergent target world must reject replay
    // ═══════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════
    // Scenario builder
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a multi-frame scenario that exercises every FrameDelta command
    /// kind while avoiding the documented Remove+Add same-type-same-frame
    /// pattern (which intentionally diverges between Submit and Replay).
    /// Returns the source world (post-Submit) and the delta list.
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
        stream.Link(a, b);
        stream.Link(a, c);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 2: modify existing, create new
        stream.Set(a, new Position(100, 200));
        stream.Set(b, new Position(30, 40));
        stream.Remove<Velocity>(b);
        var e = stream.Create(); stream.Add(e, new Position(50, 60));
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 3: component add (no Remove+Add same-type), create new, link
        stream.Add(d, new Position(55, 66));
        var f = stream.Create(); stream.Add(f, new Health(1));
        stream.Link(a, f);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 4: destroy, add unrelated type, set existing
        stream.Set(d, new Position(88, 99));
        stream.Destroy(b);
        stream.Add(e, new Velocity(1, 1));
        stream.Destroy(c);
        deltas.Add(stream.Snapshot()); stream.Submit();

        // Frame 5: recycle destroyed id (b's slot)
        var recycled = stream.Create(); stream.Add(recycled, new Health(7));
        stream.Link(a, recycled);
        deltas.Add(stream.Snapshot()); stream.Submit();

        return (source, deltas);
    }

    // ═══════════════════════════════════════════════════════════
    // Hash-based state comparison
    // ═══════════════════════════════════════════════════════════

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
}
