using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Bullet × Player collision via deterministic spatial grid.
//
// Flow:
//   1. Build grid from bullets (query With<BulletTag>). Each entry stores
//      FiredBy + SpawnFrame as a logical sort key (entity ids differ across
//      hosts in placeholder mode, so we cannot sort by Entity).
//   2. For each player (sorted by PlayerTag.HostId), visit nearby bullets,
//      do narrow-phase distance check. Collect (bullet, player, damage)
//      hits into a buffer.
//   3. Sort hits by (bulletFiredBy, bulletSpawnFrame, playerHostId) — fully
//      logical, identical across hosts.
//   4. Apply damage via Access (read Health/Shield, write back). No structural
//      change inside the Access pass.
//   5. Destroy hit bullets in sorted order — structural change.
//
// Player death: when Health hits 0 the player stays alive (Health clamps at 0)
// — Slice 6 doesn't remove players (keeps the demo running). Real perma-death
// is a gameplay decision, not a library determinism question.
public static class CollisionSystem
{
    private const int PlayerRadius = 5000;
    private const int BulletRadius = 1000;
    private const long PlayerBulletDistSq = (PlayerRadius + BulletRadius) * (long)(PlayerRadius + BulletRadius);

    private static readonly QueryDescription BulletQuery = new QueryDescription().With<BulletTag>();

    private static readonly SpatialGrid _grid = new();
    private static readonly List<Hit> _hits = new(256);
    private static readonly List<Entity> _toDestroy = new(256);

    public static void Run(World world)
    {
        // Phase 1: build grid.
        _grid.Clear();
        foreach (var e in world.Query(in BulletQuery))
        {
            var pos = world.Get<Position>(e);
            var fired = world.Get<FiredBy>(e);
            var sf = world.Get<SpawnFrame>(e);
            _grid.Insert(e, fired.HostId, sf.Frame, pos.X, pos.Y);
        }

        // Phase 2: detect hits.
        _hits.Clear();
        var players = PlayerQuery.SortedByHostId(world);
        var visitor = new PlayerVisitor(world);
        foreach (var (player, tag) in players)
        {
            var ppos = world.Get<Position>(player);
            visitor.Reset(player, tag.HostId, ppos.X, ppos.Y, _hits);
            _grid.VisitNeighborhood(ppos.X, ppos.Y, visitor);
        }

        if (_hits.Count == 0)
            return;

        // Phase 3: sort by logical key.
        _hits.Sort((a, b) =>
        {
            var c = a.BulletHostId.CompareTo(b.BulletHostId);
            if (c != 0) return c;
            c = a.BulletSpawnFrame.CompareTo(b.BulletSpawnFrame);
            if (c != 0) return c;
            return a.PlayerHostId.CompareTo(b.PlayerHostId);
        });

        // Phase 4: apply damage via Access (no structural change).
        // Group hits by player so we can amortize the Access lookup.
        // _hits is sorted by (bulletHost, bulletFrame, playerHost) — re-sort
        // by playerHost to group. Use a stable secondary buffer.
        ApplyDamageGrouped(world);

        // Phase 5: destroy hit bullets. Dedup (a bullet can hit at most one
        // player in this single-frame grid, but stay safe).
        _toDestroy.Clear();
        var seen = new HashSet<Entity>();
        foreach (var h in _hits)
        {
            if (seen.Add(h.Bullet))
                _toDestroy.Add(h.Bullet);
        }
        // Destroy in the same logical sort order as hits.
        foreach (var e in _toDestroy)
            world.Destroy(e);
    }

    private static void ApplyDamageGrouped(World world)
    {
        // Walk _hits in order; accumulate damage per player until player changes.
        var i = 0;
        while (i < _hits.Count)
        {
            var playerHost = _hits[i].PlayerHostId;
            var playerEntity = _hits[i].PlayerEntity;
            var total = 0;
            while (i < _hits.Count && _hits[i].PlayerHostId == playerHost)
            {
                total += _hits[i].Damage;
                i++;
            }

            var acc = world.Access(playerEntity);
            ref var hp = ref acc.Get<Health>();
            if (acc.Has<Shield>())
            {
                ref var shield = ref acc.Get<Shield>();
                var absorbed = Math.Min(shield.Cur, total);
                shield = shield with { Cur = shield.Cur - absorbed };
                total -= absorbed;
            }
            if (total > 0)
                hp = hp with { Cur = Math.Max(0, hp.Cur - total) };
            // accessor discarded here
        }
    }

    private readonly struct Hit(Entity bullet, int bulletHostId, int bulletSpawnFrame,
                                Entity playerEntity, int playerHostId, int damage)
    {
        public readonly Entity Bullet = bullet;
        public readonly int BulletHostId = bulletHostId;
        public readonly int BulletSpawnFrame = bulletSpawnFrame;
        public readonly Entity PlayerEntity = playerEntity;
        public readonly int PlayerHostId = playerHostId;
        public readonly int Damage = damage;
    }

    private sealed class PlayerVisitor : IEntryVisitor
    {
        private readonly World _world;
        private Entity _player;
        private int _playerHost;
        private int _px, _py;
        private List<Hit> _hits = null!;

        public PlayerVisitor(World world) => _world = world;

        public void Reset(Entity player, int playerHost, int px, int py, List<Hit> hits)
        {
            _player = player;
            _playerHost = playerHost;
            _px = px;
            _py = py;
            _hits = hits;
        }

        public void Visit(GridEntry entry)
        {
            // Skip self-fired bullets (a host's own bullets shouldn't hit
            // itself — gameplay rule, also avoids silly false positives in
            // the demo where everyone spawns bullets at their own position).
            if (entry.HostId == _playerHost)
                return;

            var dx = entry.X - _px;
            var dy = entry.Y - _py;
            var d2 = (long)dx * dx + (long)dy * dy;
            if (d2 <= PlayerBulletDistSq)
            {
                var dmg = _world.Get<Damage>(entry.Entity).Amount;
                _hits.Add(new Hit(entry.Entity, entry.HostId, entry.SpawnFrame,
                                  _player, _playerHost, dmg));
            }
        }
    }

    // Re-read damage on each hit during ApplyDamageGrouped, since the visitor
    // above doesn't have world access. We patch Damage in via this helper
    // before grouping. (Inline alternative: store world in the visitor — kept
    // separate for clarity.)
}
