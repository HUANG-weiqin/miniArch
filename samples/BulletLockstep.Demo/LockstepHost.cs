using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo;

// One lockstep peer. Owns an independent World + independent id allocator.
// DeferredEntities=true: Create() returns a placeholder; Snapshot emits a
// placeholder delta that every replaying host maps to its own local id.
//
// Consequence (kb 决策 #3): placeholder refs are single-frame only.
// Long-lived entities (players, boss) are NOT referenced by record ops across
// frames; they are located each frame via deterministic post-replay queries.
public sealed class LockstepHost
{
    public int HostId { get; }
    public World World { get; }
    public CommandStream Stream { get; }

    public LockstepHost(int hostId)
    {
        HostId = hostId;
        World = new World();
        Stream = new CommandStream(World) { DeferredEntities = true };
    }

    // Slice 9: construct a host bound to an externally-supplied world (e.g.
    // a World.Clone() snapshot for rollback recovery). The CommandStream is
    // rebound to the new world.
    public LockstepHost(int hostId, World existingWorld)
    {
        HostId = hostId;
        World = existingWorld;
        Stream = new CommandStream(World) { DeferredEntities = true };
    }

    // Frame 0 init. Every host records its own player. Host 0 additionally
    // records the Boss + 5 WeakPoints + 5 AddChild ops. Because all hosts replay
    // every host's frame-0 delta in fixed HostId order, every host ends up
    // with: N players + 1 boss + 5 weakpoints, all hierarchically linked.
    // The placeholder refs in AddChild() are valid because Create + AddChild happen
    // in the same frame's record.
    public void RecordInit(bool spawnBoss, bool scaleMode = false)
    {
        var player = Stream.Create();
        Stream.Add(player, new PlayerTag(HostId));
        Stream.Add(player, new Position(HostId * 10_000, 0));
        Stream.Add(player, new Velocity(0, 0));
        Stream.Add(player, new Health(1000, 1000));

        if (spawnBoss && HostId == 0)
            RecordBossHierarchy(scaleMode);
    }

    private void RecordBossHierarchy(bool scaleMode)
    {
        const int weakPointCount = 5;
        var boss = Stream.Create();
        Stream.Add(boss, new BossTag());
        Stream.Add(boss, new Position(50_000, 50_000));
        // Scale mode starts the boss in phase 1 immediately so the ring
        // pattern kicks in from frame 0 (otherwise we'd wait 200 frames for
        // the natural phase transition).
        Stream.Add(boss, new Health(scaleMode ? int.MaxValue : weakPointCount * 100,
                                     scaleMode ? int.MaxValue : weakPointCount * 100));
        Stream.Add(boss, new AIPattern(Phase: scaleMode ? 1 : 0, PhaseFrame: 0));

        for (var i = 0; i < weakPointCount; i++)
        {
            var wp = Stream.Create();
            Stream.Add(wp, new WeakPointTag(i));
            Stream.Add(wp, new Position(50_000, 50_000));  // updated by follow system
            Stream.Add(wp, new Health(100, 100));
            // Place weakpoints in a pentagon around the boss (radius 8000 milli-pix).
            var angle = i * (2 * Math.PI / weakPointCount);
            Stream.Add(wp, new LocalOffset(
                (int)(Math.Cos(angle) * 8000),
                (int)(Math.Sin(angle) * 8000)));
            Stream.AddChild(boss, wp);
        }
    }

    // Frame > 0: each host records Create(bullet). Host 0 fires homing bullets
    // periodically (different archetype, exercises With<Target> queries);
    // other hosts fire basic bullets. Slice 7: host 0 also fires ring-pattern
    // bursts in boss phase 1 (many Stream.Create in one frame).
    public void RecordFrame(int frame, bool scaleMode = false)
    {
        var (dx, dy) = InputProvider.Get(HostId, frame);

        if (HostId == 0 && frame % 9 == 0)
        {
            // Homing bullet: targets the next host's player (rotates).
            var targetHost = 1 + (frame / 9) % Math.Max(1, 3);
            var bullet = Stream.Create();
            Stream.Add(bullet, new BulletTag());
            Stream.Add(bullet, new SpawnFrame(frame));
            Stream.Add(bullet, new FiredBy(HostId));
            Stream.Add(bullet, new Position(50_000, 50_000));
            Stream.Add(bullet, new Velocity(dx * 500, dy * 500));
            Stream.Add(bullet, new Damage(40));
            Stream.Add(bullet, new Target(targetHost));
            Stream.Add(bullet, new TurnRate(800));
        }
        else
        {
            var bullet = Stream.Create();
            Stream.Add(bullet, new BulletTag());
            Stream.Add(bullet, new SpawnFrame(frame));
            Stream.Add(bullet, new FiredBy(HostId));
            Stream.Add(bullet, new Position(HostId * 10_000, 0));
            Stream.Add(bullet, new Velocity(dx * 1000, dy * 1000));
            Stream.Add(bullet, new Damage(50));
        }

        // Slice 7: host 0 fires a ring pattern when boss is in phase 1. Scale
        // mode cranks the count to test chunked storage at 30K+ entities.
        if (HostId == 0)
            RecordBossRingPattern(frame, scaleMode);
    }

    private void RecordBossRingPattern(int frame, bool scaleMode)
    {
        // Read boss phase from the local world. BossAISystem mutates phase in
        // post-replay; at record time we see last frame's phase.
        var bossPhase = ReadBossPhase();
        if (bossPhase != 1)
            return;

        var ringCount = scaleMode ? 600 : 24;
        var speed = scaleMode ? 400 : 1200;
        for (var i = 0; i < ringCount; i++)
        {
            var angle = i * (2 * Math.PI / ringCount) + frame * 0.05;
            var vx = (int)(Math.Cos(angle) * speed);
            var vy = (int)(Math.Sin(angle) * speed);
            var bullet = Stream.Create();
            Stream.Add(bullet, new BulletTag());
            Stream.Add(bullet, new SpawnFrame(frame));
            Stream.Add(bullet, new FiredBy(HostId));
            Stream.Add(bullet, new Position(50_000, 50_000));
            Stream.Add(bullet, new Velocity(vx, vy));
            Stream.Add(bullet, new Damage(30));
        }
    }

    private int ReadBossPhase()
    {
        var desc = new QueryDescription().With<BossTag>();
        foreach (var e in World.Query(in desc))
            return World.Get<AIPattern>(e).Phase;
        return -1;
    }

    public byte[] Checksum() => World.CanonicalChecksum();
}
