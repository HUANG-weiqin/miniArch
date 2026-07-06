using System.IO;
using MiniArch.Core;

namespace MiniArchTests.Persistence;

public sealed class NetworkSyncTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // Component carrying an Entity field — exercises EntityFieldResolver
    // auto-resolution of placeholder refs during Replay. record struct defaults
    // to LayoutKind.Sequential, which EntityFieldResolver requires.
    private readonly record struct Follow(Entity Target);

    // ── Helpers ────────────────────────────────────────────────────────

    private static Entity E(int id, int version = 1) => new(id, version);

    /// <summary>
    /// After setup:
    ///   Entity 0 — alive, version 1, Position(1,2)
    ///   Entity 1 — dead (in free list), version 2
    ///   Entity 2 — alive, version 1, Position(5,6) + Velocity(7,8)
    ///   Free list: [1] (version 2)
    ///   Slot count = 3
    /// </summary>
    private static World CreateInitialWorld()
    {
        var w = new World();
        w.Create(new Position(1, 2));       // 0
        w.Create(new Velocity(3, 4));        // 1
        w.Create(new Position(5, 6));        // 2
        w.Add(E(2), new Velocity(7, 8));
        w.Destroy(E(1));                     // 1→version 2, pushed to free list
        return w;
    }

    // ── T1: Lockstep 2-host, 1 frame, DeferredEntities ─────────────────

    [Fact]
    public void T1_Lockstep_two_host_single_frame()
    {
        var hostA = CreateInitialWorld();
        var hostB = WorldClone.Clone(hostA);

        var hashBefore = hostA.Checksum();
        Assert.Equal(hashBefore, hostB.Checksum());

        var csA = new CommandStream(hostA) { DeferredEntities = true };
        var f = csA.Create();
        csA.Add(f, new Position(99, 100));
        csA.Set(E(0), new Position(10, 20));
        csA.Add(f, new Velocity(1, 2));
        var deltaA = csA.Snapshot();

        var csB = new CommandStream(hostB) { DeferredEntities = true };
        var g = csB.Create();
        csB.Add(g, new Velocity(200, 201));
        csB.Destroy(E(2));
        csB.Add(g, new Health(50));
        var deltaB = csB.Snapshot();

        new CommandStream(hostA).Replay(deltaA);
        new CommandStream(hostA).Replay(deltaB);

        new CommandStream(hostB).Replay(deltaA);
        new CommandStream(hostB).Replay(deltaB);

        Assert.Equal(hostA.Checksum(), hostB.Checksum());
        Assert.NotEqual(hashBefore, hostA.Checksum());
    }

    // ── T2: Lockstep 2-host, 3 frames ──────────────────────────────────

    [Fact]
    public void T2_Lockstep_three_frames_cumulative()
    {
        var hostA = CreateInitialWorld();
        var hostB = WorldClone.Clone(hostA);

        for (var frame = 0; frame < 3; frame++)
        {
            var csA = new CommandStream(hostA) { DeferredEntities = true };
            var f = csA.Create();
            csA.Add(f, new Position(10 + frame, 20 + frame));
            csA.Add(f, new Velocity(frame, frame));
            var deltaA = csA.Snapshot();

            var csB = new CommandStream(hostB) { DeferredEntities = true };
            if (frame == 0)
                csB.Add(E(2), new Health(0));
            else
                csB.Set(E(2), new Health(frame));
            if (frame % 2 == 0)
            {
                var g = csB.Create();
                csB.Add(g, new Velocity(100 + frame, 200 + frame));
            }
            var deltaB = csB.Snapshot();

            new CommandStream(hostA).Replay(deltaA);
            new CommandStream(hostA).Replay(deltaB);

            new CommandStream(hostB).Replay(deltaA);
            new CommandStream(hostB).Replay(deltaB);

            Assert.Equal(hostA.Checksum(), hostB.Checksum());
        }
    }

    // ── T3: Authoritative server + client prediction + rollback ────────

    [Fact]
    public void T3_Authoritative_server_client_prediction_rollback()
    {
        var server = CreateInitialWorld();

        var serverCs = new CommandStream(server) { DeferredEntities = false };
        var srvCreate = serverCs.Create();
        serverCs.Add(srvCreate, new Position(30, 31));
        serverCs.Add(srvCreate, new Velocity(32, 33));
        serverCs.Destroy(E(0));
        var serverDelta = serverCs.Snapshot();

        // Server must replay its own delta to materialize reserved entities
        new CommandStream(server).Replay(serverDelta);

        // Client starts from same initial state, predicts, then rolls back
        var client = WorldClone.Clone(CreateInitialWorld());
        var snap = client.CaptureState();
        client.Create(new Health(99));
        client.Destroy(E(2));
        client.Create(new Position(111, 222));

        client.RestoreState(snap);
        new CommandStream(client).Replay(serverDelta);

        Assert.Equal(server.Checksum(), client.Checksum());
    }

    // ── T4: Serial real-ID deltas (alternating producer) ───────────────

    [Fact]
    public void T4_Serial_real_id_deltas_alternating()
    {
        var a = CreateInitialWorld();
        var b = WorldClone.Clone(a);

        // Round 1: Host A produces, B replays
        var cs1 = new CommandStream(a) { DeferredEntities = false };
        var e1 = cs1.Create();
        cs1.Add(e1, new Position(50, 51));
        cs1.Add(e1, new Velocity(52, 53));
        cs1.Add(E(2), new Health(10));
        var delta1 = cs1.Snapshot();
        // Source must replay its own delta to materialize
        new CommandStream(a).Replay(delta1);
        new CommandStream(b).Replay(delta1);
        Assert.Equal(a.Checksum(), b.Checksum());

        // Round 2: Host B produces, A replays
        var cs2 = new CommandStream(b) { DeferredEntities = false };
        var e2 = cs2.Create();
        cs2.Add(e2, new Velocity(60, 61));
        cs2.Add(e2, new Health(20));
        cs2.Destroy(E(0));
        var delta2 = cs2.Snapshot();
        // Source must replay its own delta first
        new CommandStream(b).Replay(delta2);
        new CommandStream(a).Replay(delta2);
        Assert.Equal(a.Checksum(), b.Checksum());
    }

    // ── T5: Full state resync via Snapshot ─────────────────────────────

    [Fact]
    public void T5_Snapshot_resync_free_list_ordering()
    {
        var source = CreateInitialWorld();
        var extra = source.Create(new Position(80, 81));
        source.Destroy(E(2));
        source.Destroy(extra);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, source);
        stream.Position = 0;
        var loadedA = WorldSnapshot.Load(stream);
        stream.Position = 0;
        var loadedB = WorldSnapshot.Load(stream);

        Assert.Equal(source.Checksum(), loadedA.Checksum());
        Assert.Equal(loadedA.Checksum(), loadedB.Checksum());

        // Both must allocate the same recycled IDs from their restored free lists
        var a1 = loadedA.Create(new Position(90, 91));
        var a2 = loadedA.Create(new Velocity(92, 93));
        var b1 = loadedB.Create(new Position(90, 91));
        var b2 = loadedB.Create(new Velocity(92, 93));

        Assert.Equal(a1.Id, b1.Id);
        Assert.Equal(a2.Id, b2.Id);
        Assert.Equal(loadedA.Checksum(), loadedB.Checksum());
    }

    // ── T6: Snapshot initial state + multi-frame lockstep ──────────────

    [Fact]
    public void T6_Snapshot_initial_state_plus_lockstep()
    {
        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, CreateInitialWorld());
        stream.Position = 0;

        var hostA = WorldSnapshot.Load(stream);
        stream.Position = 0;
        var hostB = WorldSnapshot.Load(stream);

        Assert.Equal(hostA.Checksum(), hostB.Checksum());

        for (var frame = 0; frame < 2; frame++)
        {
            var csA = new CommandStream(hostA) { DeferredEntities = true };
            var f = csA.Create();
            csA.Add(f, new Position(100 + frame, 200 + frame));
            csA.Add(f, new Velocity(frame, frame + 1));
            var deltaA = csA.Snapshot();

            var csB = new CommandStream(hostB) { DeferredEntities = true };
            var h = csB.Create();
            csB.Add(h, new Health(frame * 10));
            csB.Set(E(0), new Position(300 + frame, 400 + frame));
            var deltaB = csB.Snapshot();

            new CommandStream(hostA).Replay(deltaA);
            new CommandStream(hostA).Replay(deltaB);

            new CommandStream(hostB).Replay(deltaA);
            new CommandStream(hostB).Replay(deltaB);

            Assert.Equal(hostA.Checksum(), hostB.Checksum());
        }
    }

    // ── T7: Heavy create/destroy pressure ──────────────────────────────

    [Fact]
    public void T7_Heavy_create_destroy_pressure()
    {
        var w = new World();

        // Create 10 entities
        var alive = new Entity[10];
        for (var i = 0; i < alive.Length; i++)
            alive[i] = w.Create(new Position(i, i * 10));

        // Destroy 6 specific ones → free list push order: 9(v2),8,7,5,3,1
        // Pop order on next reserve: 1, 3, 5, 7, 8, 9
        w.Destroy(alive[9]);
        w.Destroy(alive[8]);
        w.Destroy(alive[7]);
        w.Destroy(alive[5]);
        w.Destroy(alive[3]);
        w.Destroy(alive[1]);

        // Remaining alive: 0(v1), 2(v1), 4(v1), 6(v1)
        // Free list: 9(v2),8,7,5,3,1 (6 entries)
        // Slot count: 10

        var hostA = WorldClone.Clone(w);
        var hostB = WorldClone.Clone(w);

        // Host A: 15 creates, 5 destroys of own creates, 1 destroy of initial entity
        var csA = new CommandStream(hostA) { DeferredEntities = true };
        var createdA = new Entity[15];
        for (var i = 0; i < createdA.Length; i++)
        {
            createdA[i] = csA.Create();
            csA.Add(createdA[i], new Position(100 + i, 200 + i));
        }
        for (var i = 0; i < 5; i++)
            csA.Destroy(createdA[i * 2]);
        csA.Destroy(new Entity(alive[0].Id, alive[0].Version)); // Entity(0,1)
        var deltaA = csA.Snapshot();

        // Host B: 12 creates, 1 destroy of initial entity, 1 no-op destroy
        var csB = new CommandStream(hostB) { DeferredEntities = true };
        for (var i = 0; i < 12; i++)
        {
            var h = csB.Create();
            csB.Add(h, new Health(i * 5));
        }
        csB.Destroy(new Entity(alive[2].Id, alive[2].Version)); // Entity(2,1)
        csB.Destroy(new Entity(alive[1].Id, alive[1].Version + 1)); // Entity(1,2) already free → no-op
        var deltaB = csB.Snapshot();

        new CommandStream(hostA).Replay(deltaB);
        new CommandStream(hostA).Replay(deltaA);

        new CommandStream(hostB).Replay(deltaB);
        new CommandStream(hostB).Replay(deltaA);

        Assert.Equal(hostA.Checksum(), hostB.Checksum());
    }

    // ── T8: EntityFieldResolver across hosts (component Entity field) ──
    // Each host records a component whose Entity field holds a same-frame
    // placeholder. After all hosts replay all deltas, EntityFieldResolver must
    // have resolved every placeholder to a real id — and the same real id on
    // every host (id allocator synced by replay history). This is the path the
    // soak fuzz does not reach (test components carry no Entity fields).

    [Fact]
    public void T8_Entity_field_placeholder_resolves_across_hosts()
    {
        var hostA = new World();
        var hostB = new World();

        // Host A: leader + follower referencing leader (placeholder→placeholder)
        var csA = new CommandStream(hostA) { DeferredEntities = true };
        var leader = csA.Create();
        csA.Add(leader, new Position(1, 1));
        var follower = csA.Create();
        csA.Add(follower, new Follow { Target = leader });
        var deltaA = csA.Snapshot();
        csA.Clear();

        // Host B: boss + minion referencing boss (its own placeholder, same-frame)
        var csB = new CommandStream(hostB) { DeferredEntities = true };
        var boss = csB.Create();
        csB.Add(boss, new Position(2, 2));
        var minion = csB.Create();
        csB.Add(minion, new Follow { Target = boss });
        var deltaB = csB.Snapshot();
        csB.Clear();

        // Both hosts replay both deltas in fixed hostId order (A then B)
        new CommandStream(hostA).Replay(deltaA);
        new CommandStream(hostA).Replay(deltaB);
        new CommandStream(hostB).Replay(deltaA);
        new CommandStream(hostB).Replay(deltaB);

        // 1. Convergence
        Assert.Equal(hostA.Checksum(), hostB.Checksum());

        // 2. Every Follow.Target must be resolved to a real id (not stale placeholder)
        var followDesc = new QueryDescription().With<Follow>();
        var holdersA = new List<Entity>();
        var holdersB = new List<Entity>();
        foreach (var e in hostA.Query(in followDesc)) holdersA.Add(e);
        foreach (var e in hostB.Query(in followDesc)) holdersB.Add(e);
        Assert.Equal(2, holdersA.Count);
        Assert.Equal(2, holdersB.Count);

        foreach (var h in holdersA)
        {
            var t = hostA.Get<Follow>(h).Target;
            Assert.True(t.Id >= 0, $"Unresolved placeholder leaked through on hostA: holder={h} target={t}");
            Assert.True(hostA.IsAlive(t), $"Follow.Target not alive on hostA: holder={h} target={t}");
        }
        foreach (var h in holdersB)
        {
            var t = hostB.Get<Follow>(h).Target;
            Assert.True(t.Id >= 0, $"Unresolved placeholder leaked through on hostB: holder={h} target={t}");
            Assert.True(hostB.IsAlive(t), $"Follow.Target not alive on hostB: holder={h} target={t}");
        }

        // 3. Same real ids across hosts (allocator synced → deterministic)
        Assert.Equal(hostA.Checksum(), hostB.Checksum()); // already asserted; re-affirm intent
        // Spot-check: host A's follower.Target points to the leader, which has Position(1,1)
        // Find the holder whose Target has Position(1,1) on hostA, confirm hostB agrees.
        Entity ResolveLeaderOn(World w)
        {
            foreach (var h in w.Query(in followDesc))
            {
                var t = w.Get<Follow>(h).Target;
                if (w.Has<Position>(t) && w.Get<Position>(t).X == 1)
                    return t;
            }
            return default;
        }
        var leaderA = ResolveLeaderOn(hostA);
        var leaderB = ResolveLeaderOn(hostB);
        Assert.True(leaderA.Id >= 0, "leader not found on hostA");
        Assert.Equal(leaderA, leaderB); // identical real id across hosts
    }
}
