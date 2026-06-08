using System.Runtime.CompilerServices;
using DefaultEcs;
using MiniArch.Core;
using MiniArchBenchmarks.GameTick;

namespace GameTickSim;

using MiniWorld = MiniArch.World;
using GAttr = MiniArchBenchmarks.GameTick.Attribute;

public static class MiniGameTickWorldFactory
{
    public static MiniWorld CreateWorld()
    {
        var w = new MiniWorld();

        for (var i = 0; i < GameTickData.PlayerCount; i++)
        {
            var e = w.Create(new PlayerTag(), new Team(i & 3),
                new Position { X = i, Y = i + 1, Z = i + 2 },
                new Velocity { X = 1 },
                new Rotation { Pitch = i % 360 },
                new Health { Current = 100, Max = 100 },
                new Mana { Current = 50, Max = 50 },
                new Stamina { Current = 100, Max = 100 },
                new Scale { X = 1 },
                new AttackRange(10), new SightRange(50),
                new Gold(i), new Level(1),
                new Faction(1), new Armor(10), new Speed(5));
            w.Add(e, new Color(1, 1, 1, 1));
        }

        for (var i = 0; i < GameTickData.MeleeEnemyCount; i++)
        {
            var e = w.Create(new EnemyTag(), new Team(1),
                new Position { X = i, Z = i + 1 },
                new Velocity { X = -1 },
                new Rotation { Yaw = 180 },
                new Health { Current = 30 + (i & 31), Max = 30 + (i & 31) },
                new Mana { Current = 10, Max = 10 },
                new Stamina { Current = 50, Max = 50 },
                new Scale { X = 1.5f },
                new AttackRange(2), new SightRange(30),
                new Gold(10 + (i & 7)), new Level(1 + (i & 3)),
                new Faction(2), new Armor(5), new Speed(3));
            w.Add(e, new Damage { Base = 5 + (i & 7) });
        }

        for (var i = 0; i < GameTickData.RangedEnemyCount; i++)
        {
            var e = w.Create(new EnemyTag(), new Team(2),
                new Position { X = i * 2, Z = i * 2 + 5 },
                new Velocity { X = 0 },
                new Rotation { Yaw = 90 },
                new Health { Current = 20 + (i & 15), Max = 20 + (i & 15) },
                new Mana { Current = 30, Max = 30 },
                new Stamina { Current = 40, Max = 40 },
                new Scale { X = 1.2f },
                new AttackRange(15), new SightRange(40),
                new Gold(15 + (i & 7)), new Level(1 + (i & 2)),
                new Faction(2), new Armor(3), new Speed(2));
            w.Add(e, new Damage { Base = 8 + (i & 3) });
        }

        for (var i = 0; i < GameTickData.BossEnemyCount; i++)
        {
            var e = w.Create(new EnemyTag(), new Team(3),
                new Position { X = i * 10, Z = i * 10 + 50 },
                new Velocity { X = 0.5f },
                new Rotation { Yaw = 0 },
                new Health { Current = 500, Max = 500 },
                new Mana { Current = 200, Max = 200 },
                new Stamina { Current = 200, Max = 200 },
                new Scale { X = 3 },
                new AttackRange(20), new SightRange(60),
                new Gold(100 + i * 10), new Level(10 + i * 2),
                new Faction(2), new Armor(20));
            w.Add(e, new Speed(1));
            w.Add(e, new Damage { Base = 15 });
            w.Add(e, new Resistance(10, 10, 5, 5));
            w.Add(e, new GAttr(100, 50, 30, 20));
            w.Add(e, new Abilities(1, 2, 3, 0, 0, 0, 0, 0));
        }

        for (var i = 0; i < GameTickData.NpcCount; i++)
            w.Create(new NpcTag(), new Team(0),
                new Position { X = i * 3, Z = i * 3 + 2 },
                new Rotation { Yaw = i % 360 },
                new Health { Current = 100, Max = 100 },
                new Mana { Current = 100, Max = 100 },
                new Scale { X = 1 },
                new Gold(5 + (i & 15)), new Level(1),
                new Faction(1), new Speed(0));

        for (var i = 0; i < GameTickData.PetCount; i++)
            w.Create(new NpcTag(), new Team(i & 1),
                new Position { X = i * 2 + 1, Z = i * 2 + 1 },
                new Velocity { X = 0.3f },
                new Health { Current = 50, Max = 50 },
                new Scale { X = 0.5f },
                new SightRange(20),
                new Level(1), new Speed(4),
                new AttackRange(2));

        for (var i = 0; i < GameTickData.ProjectileCount; i++)
            w.Create(new ProjectileTag(), new Team(i & 1),
                new Position { X = i * 5, Z = i * 5 },
                new Velocity { X = 2 * (i % 3 == 0 ? -1 : 1), Z = 2 * (i % 2 == 0 ? 1 : -1) },
                new Rotation { Yaw = 45 },
                new Scale { X = 0.3f },
                new Speed(10), new Damage { Base = 5 + (i & 3) },
                new Faction(i & 1));

        for (var i = 0; i < GameTickData.StaticObjectCount; i++)
            w.Create(new Position { X = i * 5, Z = i * 5 },
                new Rotation { Yaw = i * 45 },
                new Size { W = 2, H = 2, D = 2 },
                new Color(i % 255, (i * 2) % 255, (i * 3) % 255, 255),
                new MeshId(i % 10), new RenderLayer(1));

        for (var i = 0; i < GameTickData.DestructibleCount; i++)
            w.Create(new Position { X = i * 7 + 3, Z = i * 7 + 3 },
                new Health { Current = 20 + (i & 31), Max = 20 + (i & 31) },
                new Size { W = 1, H = 1, D = 1 },
                new Scale { X = 0.8f },
                new MeshId(100 + (i % 5)), new RenderLayer(1));

        for (var i = 0; i < GameTickData.EnvironmentCount; i++)
            w.Create(new Position { X = i * 3, Z = i * 3 },
                new Size { W = 10, H = 5, D = 10 },
                new Color((i * 50) % 255, (i * 30) % 255, 0, 255),
                new MeshId(200 + (i % 3)), new RenderLayer(0));

        for (var i = 0; i < GameTickData.TrapCount; i++)
            w.Create(new Position { X = i * 12 + 6, Z = i * 12 + 6 },
                new Size { W = 1, H = 0.5f, D = 1 },
                new Health { Current = 10, Max = 10 },
                new Damage { Base = 5 + (i & 3) },
                new MeshId(300), new RenderLayer(2));

        for (var i = 0; i < GameTickData.LootDropCount; i++)
            w.Create(new Position { X = i * 8 + 2, Z = i * 8 + 2 },
                new Size { W = 0.5f, H = 0.5f, D = 0.5f },
                new Gold(1 + (i & 15)),
                new MeshId(400 + (i % 8)), new RenderLayer(1));

        return w;
    }
}

public static class DefaultGameTickWorldFactory
{
    public static DefaultEcs.World CreateWorld()
    {
        var w = new DefaultEcs.World();

        for (var i = 0; i < GameTickData.PlayerCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new PlayerTag()); e.Set(new Team(i & 3));
            e.Set(new Position { X = i, Y = i + 1, Z = i + 2 });
            e.Set(new Velocity { X = 1 });
            e.Set(new Rotation { Pitch = i % 360 });
            e.Set(new Health { Current = 100, Max = 100 });
            e.Set(new Mana { Current = 50, Max = 50 });
            e.Set(new Stamina { Current = 100, Max = 100 });
            e.Set(new Scale { X = 1 });
            e.Set(new AttackRange(10)); e.Set(new SightRange(50));
            e.Set(new Gold(i)); e.Set(new Level(1));
            e.Set(new Faction(1)); e.Set(new Armor(10)); e.Set(new Speed(5));
            e.Set(new Color(1, 1, 1, 1));
        }

        for (var i = 0; i < GameTickData.MeleeEnemyCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new EnemyTag()); e.Set(new Team(1));
            e.Set(new Position { X = i, Z = i + 1 });
            e.Set(new Velocity { X = -1 });
            e.Set(new Rotation { Yaw = 180 });
            e.Set(new Health { Current = 30 + (i & 31), Max = 30 + (i & 31) });
            e.Set(new Mana { Current = 10, Max = 10 });
            e.Set(new Stamina { Current = 50, Max = 50 });
            e.Set(new Scale { X = 1.5f });
            e.Set(new AttackRange(2)); e.Set(new SightRange(30));
            e.Set(new Gold(10 + (i & 7))); e.Set(new Level(1 + (i & 3)));
            e.Set(new Faction(2)); e.Set(new Armor(5)); e.Set(new Speed(3));
            e.Set(new Damage { Base = 5 + (i & 7) });
        }

        for (var i = 0; i < GameTickData.RangedEnemyCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new EnemyTag()); e.Set(new Team(2));
            e.Set(new Position { X = i * 2, Z = i * 2 + 5 });
            e.Set(new Velocity { X = 0 });
            e.Set(new Rotation { Yaw = 90 });
            e.Set(new Health { Current = 20 + (i & 15), Max = 20 + (i & 15) });
            e.Set(new Mana { Current = 30, Max = 30 });
            e.Set(new Stamina { Current = 40, Max = 40 });
            e.Set(new Scale { X = 1.2f });
            e.Set(new AttackRange(15)); e.Set(new SightRange(40));
            e.Set(new Gold(15 + (i & 7))); e.Set(new Level(1 + (i & 2)));
            e.Set(new Faction(2)); e.Set(new Armor(3)); e.Set(new Speed(2));
            e.Set(new Damage { Base = 8 + (i & 3) });
        }

        for (var i = 0; i < GameTickData.BossEnemyCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new EnemyTag()); e.Set(new Team(3));
            e.Set(new Position { X = i * 10, Z = i * 10 + 50 });
            e.Set(new Velocity { X = 0.5f });
            e.Set(new Rotation { Yaw = 0 });
            e.Set(new Health { Current = 500, Max = 500 });
            e.Set(new Mana { Current = 200, Max = 200 });
            e.Set(new Stamina { Current = 200, Max = 200 });
            e.Set(new Scale { X = 3 });
            e.Set(new AttackRange(20)); e.Set(new SightRange(60));
            e.Set(new Gold(100 + i * 10)); e.Set(new Level(10 + i * 2));
            e.Set(new Faction(2)); e.Set(new Armor(20)); e.Set(new Speed(1));
            e.Set(new Damage { Base = 15 });
            e.Set(new Resistance(10, 10, 5, 5));
            e.Set(new GAttr(100, 50, 30, 20));
            e.Set(new Abilities(1, 2, 3, 0, 0, 0, 0, 0));
        }

        for (var i = 0; i < GameTickData.NpcCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new NpcTag()); e.Set(new Team(0));
            e.Set(new Position { X = i * 3, Z = i * 3 + 2 });
            e.Set(new Rotation { Yaw = i % 360 });
            e.Set(new Health { Current = 100, Max = 100 });
            e.Set(new Mana { Current = 100, Max = 100 });
            e.Set(new Scale { X = 1 });
            e.Set(new Gold(5 + (i & 15))); e.Set(new Level(1));
            e.Set(new Faction(1)); e.Set(new Speed(0));
        }

        for (var i = 0; i < GameTickData.PetCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new NpcTag()); e.Set(new Team(i & 1));
            e.Set(new Position { X = i * 2 + 1, Z = i * 2 + 1 });
            e.Set(new Velocity { X = 0.3f });
            e.Set(new Health { Current = 50, Max = 50 });
            e.Set(new Scale { X = 0.5f });
            e.Set(new SightRange(20));
            e.Set(new Level(1)); e.Set(new Speed(4));
            e.Set(new AttackRange(2));
        }

        for (var i = 0; i < GameTickData.ProjectileCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new ProjectileTag()); e.Set(new Team(i & 1));
            e.Set(new Position { X = i * 5, Z = i * 5 });
            e.Set(new Velocity { X = 2 * (i % 3 == 0 ? -1 : 1), Z = 2 * (i % 2 == 0 ? 1 : -1) });
            e.Set(new Rotation { Yaw = 45 });
            e.Set(new Scale { X = 0.3f });
            e.Set(new Speed(10)); e.Set(new Damage { Base = 5 + (i & 3) });
            e.Set(new Faction(i & 1));
        }

        for (var i = 0; i < GameTickData.StaticObjectCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Position { X = i * 5, Z = i * 5 });
            e.Set(new Rotation { Yaw = i * 45 });
            e.Set(new Size { W = 2, H = 2, D = 2 });
            e.Set(new Color(i % 255, (i * 2) % 255, (i * 3) % 255, 255));
            e.Set(new MeshId(i % 10)); e.Set(new RenderLayer(1));
        }

        for (var i = 0; i < GameTickData.DestructibleCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Position { X = i * 7 + 3, Z = i * 7 + 3 });
            e.Set(new Health { Current = 20 + (i & 31), Max = 20 + (i & 31) });
            e.Set(new Size { W = 1, H = 1, D = 1 });
            e.Set(new Scale { X = 0.8f });
            e.Set(new MeshId(100 + (i % 5))); e.Set(new RenderLayer(1));
        }

        for (var i = 0; i < GameTickData.EnvironmentCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Position { X = i * 3, Z = i * 3 });
            e.Set(new Size { W = 10, H = 5, D = 10 });
            e.Set(new Color((i * 50) % 255, (i * 30) % 255, 0, 255));
            e.Set(new MeshId(200 + (i % 3))); e.Set(new RenderLayer(0));
        }

        for (var i = 0; i < GameTickData.TrapCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Position { X = i * 12 + 6, Z = i * 12 + 6 });
            e.Set(new Size { W = 1, H = 0.5f, D = 1 });
            e.Set(new Health { Current = 10, Max = 10 });
            e.Set(new Damage { Base = 5 + (i & 3) });
            e.Set(new MeshId(300)); e.Set(new RenderLayer(2));
        }

        for (var i = 0; i < GameTickData.LootDropCount; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Position { X = i * 8 + 2, Z = i * 8 + 2 });
            e.Set(new Size { W = 0.5f, H = 0.5f, D = 0.5f });
            e.Set(new Gold(1 + (i & 15)));
            e.Set(new MeshId(400 + (i % 8))); e.Set(new RenderLayer(1));
        }

        return w;
    }
}

public static class MiniGameTickRunner
{
    private static readonly MiniArch.QueryDescription MovementDesc = new MiniArch.QueryDescription().With<Position>().With<Velocity>();
    private static readonly MiniArch.QueryDescription HealthDesc = new MiniArch.QueryDescription().With<Health>();
    private static readonly MiniArch.QueryDescription DamageEventDesc = new MiniArch.QueryDescription().With<DamageEvent>();
    private static readonly MiniArch.QueryDescription DebuffDesc = new MiniArch.QueryDescription().With<Debuff>();
    private static readonly MiniArch.QueryDescription AllEntitiesDesc = new MiniArch.QueryDescription().With<Position>();
    private static readonly MiniArch.QueryDescription ManaDesc = new MiniArch.QueryDescription().With<Mana>();
    private static readonly MiniArch.QueryDescription StaminaDesc = new MiniArch.QueryDescription().With<Stamina>();
    private static readonly MiniArch.QueryDescription AttackRangeDesc = new MiniArch.QueryDescription().With<Position>().With<AttackRange>();
    private static readonly MiniArch.QueryDescription TransformDesc = new MiniArch.QueryDescription().With<Position>().With<Rotation>().With<Scale>();
    private static readonly MiniArch.Entity[] DestroyScratch = new MiniArch.Entity[GameTickData.SpawnPerTick];
    private static readonly MiniArch.Entity[] DebuffScratch = new MiniArch.Entity[GameTickData.DebuffPerTick];
    private static MiniArch.Entity[] _entityPool = [];
    private static int _entityCount;
    private static int _debuffIndex;
    private static readonly Random _rng = new(42);

    public static void Initialize(MiniWorld world)
    {
        var total = 0;
        foreach (var chunk in world.Query(AllEntitiesDesc).GetChunks())
            total += chunk.Count;
        _entityPool = new MiniArch.Entity[total];
        var idx = 0;
        foreach (var chunk in world.Query(AllEntitiesDesc).GetChunks())
        {
            var entities = chunk.GetEntities();
            for (var row = 0; row < chunk.Count; row++)
                _entityPool[idx++] = entities[row];
        }
        _entityCount = total;
        _debuffIndex = 0;
    }

    public static int ExecuteTick(MiniWorld world)
    {
        var checksum = 0;
        checksum += Movement(world);
        checksum += ManaRegen(world);
        checksum += RemoveDebuffs(world);
        checksum += RangeCheck(world);
        checksum += AddDebuffs(world);
        checksum += StaminaRegen(world);
        checksum += SpawnDamageEvents(world);
        checksum += ProcessDamageEvents(world);
        checksum += UpdateTransforms(world);
        checksum += SpawnDestroyMainPool(world);
        checksum += Regen(world);
        checksum += CleanupDamageEvents(world);
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Movement(MiniWorld world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(MovementDesc).GetChunks())
        {
            var pos = chunk.GetSpan<Position>();
            var vel = chunk.GetSpan<Velocity>();
            for (var row = 0; row < chunk.Count; row++)
            {
                pos[row].X += vel[row].X; pos[row].Y += vel[row].Y; pos[row].Z += vel[row].Z;
                checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManaRegen(MiniWorld world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(ManaDesc).GetChunks())
        {
            var mana = chunk.GetSpan<Mana>();
            for (var row = 0; row < chunk.Count; row++)
            {
                if (mana[row].Current < mana[row].Max)
                    mana[row].Current += 0.5f;
                checksum += (int)mana[row].Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RangeCheck(MiniWorld world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(AttackRangeDesc).GetChunks())
        {
            var pos = chunk.GetSpan<Position>();
            var range = chunk.GetSpan<AttackRange>();
            for (var row = 0; row < chunk.Count; row++)
                checksum += (int)(pos[row].X * pos[row].Z + range[row].Value);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StaminaRegen(MiniWorld world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(StaminaDesc).GetChunks())
        {
            var stam = chunk.GetSpan<Stamina>();
            for (var row = 0; row < chunk.Count; row++)
            {
                if (stam[row].Current < stam[row].Max)
                    stam[row].Current += 0.3f;
                checksum += (int)stam[row].Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int UpdateTransforms(MiniWorld world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(TransformDesc).GetChunks())
        {
            var pos = chunk.GetSpan<Position>();
            var rot = chunk.GetSpan<Rotation>();
            var scale = chunk.GetSpan<Scale>();
            for (var row = 0; row < chunk.Count; row++)
                checksum += (int)(pos[row].X + rot[row].Yaw + scale[row].X);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RemoveDebuffs(MiniWorld world)
    {
        var total = 0;
        foreach (var chunk in world.Query(DebuffDesc).GetChunks())
        {
            var entities = chunk.GetEntities();
            for (var row = 0; row < chunk.Count; row++)
                DebuffScratch[total++] = entities[row];
        }
        for (var i = 0; i < total; i++)
            world.Remove<Debuff>(DebuffScratch[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AddDebuffs(MiniWorld world)
    {
        var start = _debuffIndex;
        for (var i = 0; i < GameTickData.DebuffPerTick; i++)
        {
            var idx = (start + i) % _entityCount;
            world.Add(_entityPool[idx], new Debuff { Timer = 1.0f });
        }
        _debuffIndex = (start + GameTickData.DebuffPerTick) % _entityCount;
        return GameTickData.DebuffPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDestroyMainPool(MiniWorld world)
    {
        var toDestroy = Math.Min(GameTickData.DestroyPerTick, _entityCount);
        for (var i = 0; i < toDestroy; i++)
        {
            var idx = _rng.Next(_entityCount);
            var entity = _entityPool[idx];
            _entityCount--;
            _entityPool[idx] = _entityPool[_entityCount];
            world.Destroy(entity);
        }
        var toCreate = GameTickData.DestroyPerTick;
        if (_entityPool.Length < _entityCount + toCreate)
            Array.Resize(ref _entityPool, _entityCount + toCreate + 4096);
        for (var i = 0; i < toCreate; i++)
        {
            var e = world.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });
            _entityPool[_entityCount++] = e;
        }
        return GameTickData.DestroyPerTick * 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDamageEvents(MiniWorld world)
    {
        for (var i = 0; i < GameTickData.SpawnPerTick; i++)
            world.Create(new DamageEvent { Amount = 1 });
        return GameTickData.SpawnPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProcessDamageEvents(MiniWorld world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(HealthDesc).GetChunks())
        {
            var health = chunk.GetSpan<Health>();
            for (var row = 0; row < chunk.Count; row++)
            {
                health[row].Current -= 1;
                checksum += (int)health[row].Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CleanupDamageEvents(MiniWorld world)
    {
        var total = 0;
        foreach (var chunk in world.Query(DamageEventDesc).GetChunks())
        {
            var entities = chunk.GetEntities();
            for (var row = 0; row < chunk.Count; row++)
                DestroyScratch[total++] = entities[row];
        }
        for (var i = 0; i < total; i++)
            world.Destroy(DestroyScratch[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Regen(MiniWorld world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(HealthDesc).GetChunks())
        {
            var health = chunk.GetSpan<Health>();
            for (var row = 0; row < chunk.Count; row++)
            {
                if (health[row].Current < health[row].Max)
                    health[row].Current++;
                checksum += (int)health[row].Current;
            }
        }
        return checksum;
    }
}

public static class DefaultGameTickRunner
{
    private static EntitySet? _movementSet;
    private static EntitySet? _healthSet;
    private static EntitySet? _damageEventSet;
    private static EntitySet? _debuffSet;
    private static EntitySet? _manaSet;
    private static EntitySet? _staminaSet;
    private static EntitySet? _attackRangeSet;
    private static EntitySet? _transformSet;
    private static readonly DefaultEcs.Entity[] DefaultDestroyScratch = new DefaultEcs.Entity[GameTickData.SpawnPerTick];
    private static readonly DefaultEcs.Entity[] DefaultDebuffScratch = new DefaultEcs.Entity[GameTickData.DebuffPerTick];
    private static DefaultEcs.Entity[] _defaultEntityPool = [];
    private static int _defaultEntityCount;
    private static int _defaultDebuffIndex;
    private static readonly Random _defaultRng = new(42);

    public static void Initialize(DefaultEcs.World world)
    {
        var entities = world.GetEntities().With<Position>().AsSet().GetEntities();
        _defaultEntityPool = new DefaultEcs.Entity[entities.Length];
        for (var i = 0; i < entities.Length; i++)
            _defaultEntityPool[i] = entities[i];
        _defaultEntityCount = entities.Length;
        _defaultDebuffIndex = 0;
    }

    public static int ExecuteTick(DefaultEcs.World world)
    {
        var checksum = 0;
        checksum += Movement(world);
        checksum += ManaRegen(world);
        checksum += RemoveDebuffs(world);
        checksum += RangeCheck(world);
        checksum += AddDebuffs(world);
        checksum += StaminaRegen(world);
        checksum += SpawnDamageEvents(world);
        checksum += ProcessDamageEvents(world);
        checksum += UpdateTransforms(world);
        checksum += SpawnDestroyMainPool(world);
        checksum += Regen(world);
        checksum += CleanupDamageEvents(world);
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Movement(DefaultEcs.World world)
    {
        _movementSet ??= world.GetEntities().With<Position>().With<Velocity>().AsSet();
        var checksum = 0;
        var entities = _movementSet.GetEntities();
        for (var i = 0; i < entities.Length; i++)
        {
            ref var p = ref entities[i].Get<Position>();
            ref var v = ref entities[i].Get<Velocity>();
            p.X += v.X; p.Y += v.Y; p.Z += v.Z;
            checksum += (int)(p.X + p.Y + p.Z);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManaRegen(DefaultEcs.World world)
    {
        _manaSet ??= world.GetEntities().With<Mana>().AsSet();
        var checksum = 0;
        var entities = _manaSet.GetEntities();
        for (var i = 0; i < entities.Length; i++)
        {
            ref var m = ref entities[i].Get<Mana>();
            if (m.Current < m.Max)
                m.Current += 0.5f;
            checksum += (int)m.Current;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RangeCheck(DefaultEcs.World world)
    {
        _attackRangeSet ??= world.GetEntities().With<Position>().With<AttackRange>().AsSet();
        var checksum = 0;
        var entities = _attackRangeSet.GetEntities();
        for (var i = 0; i < entities.Length; i++)
        {
            ref var p = ref entities[i].Get<Position>();
            ref var r = ref entities[i].Get<AttackRange>();
            checksum += (int)(p.X * p.Z + r.Value);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StaminaRegen(DefaultEcs.World world)
    {
        _staminaSet ??= world.GetEntities().With<Stamina>().AsSet();
        var checksum = 0;
        var entities = _staminaSet.GetEntities();
        for (var i = 0; i < entities.Length; i++)
        {
            ref var s = ref entities[i].Get<Stamina>();
            if (s.Current < s.Max)
                s.Current += 0.3f;
            checksum += (int)s.Current;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int UpdateTransforms(DefaultEcs.World world)
    {
        _transformSet ??= world.GetEntities().With<Position>().With<Rotation>().With<Scale>().AsSet();
        var checksum = 0;
        var entities = _transformSet.GetEntities();
        for (var i = 0; i < entities.Length; i++)
        {
            ref var p = ref entities[i].Get<Position>();
            ref var r = ref entities[i].Get<Rotation>();
            ref var s = ref entities[i].Get<Scale>();
            checksum += (int)(p.X + r.Yaw + s.X);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RemoveDebuffs(DefaultEcs.World world)
    {
        _debuffSet ??= world.GetEntities().With<Debuff>().AsSet();
        var entities = _debuffSet.GetEntities();
        var count = entities.Length;
        for (var i = 0; i < count; i++)
            DefaultDebuffScratch[i] = entities[i];
        for (var i = 0; i < count; i++)
            DefaultDebuffScratch[i].Remove<Debuff>();
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AddDebuffs(DefaultEcs.World world)
    {
        var start = _defaultDebuffIndex;
        for (var i = 0; i < GameTickData.DebuffPerTick; i++)
        {
            var idx = (start + i) % _defaultEntityCount;
            _defaultEntityPool[idx].Set(new Debuff { Timer = 1.0f });
        }
        _defaultDebuffIndex = (start + GameTickData.DebuffPerTick) % _defaultEntityCount;
        return GameTickData.DebuffPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDestroyMainPool(DefaultEcs.World world)
    {
        var toDestroy = Math.Min(GameTickData.DestroyPerTick, _defaultEntityCount);
        for (var i = 0; i < toDestroy; i++)
        {
            var idx = _defaultRng.Next(_defaultEntityCount);
            var entity = _defaultEntityPool[idx];
            _defaultEntityCount--;
            _defaultEntityPool[idx] = _defaultEntityPool[_defaultEntityCount];
            entity.Dispose();
        }
        var toCreate = GameTickData.DestroyPerTick;
        if (_defaultEntityPool.Length < _defaultEntityCount + toCreate)
            Array.Resize(ref _defaultEntityPool, _defaultEntityCount + toCreate + 4096);
        for (var i = 0; i < toCreate; i++)
        {
            var e = world.CreateEntity();
            e.Set(new Position { X = i });
            e.Set(new Health { Current = 100, Max = 100 });
            _defaultEntityPool[_defaultEntityCount++] = e;
        }
        return GameTickData.DestroyPerTick * 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDamageEvents(DefaultEcs.World world)
    {
        for (var i = 0; i < GameTickData.SpawnPerTick; i++)
        {
            var e = world.CreateEntity();
            e.Set(new DamageEvent { Amount = 1 });
        }
        return GameTickData.SpawnPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProcessDamageEvents(DefaultEcs.World world)
    {
        _healthSet ??= world.GetEntities().With<Health>().AsSet();
        var checksum = 0;
        var entities = _healthSet.GetEntities();
        for (var i = 0; i < entities.Length; i++)
        {
            ref var h = ref entities[i].Get<Health>();
            h.Current -= 1;
            checksum += (int)h.Current;
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CleanupDamageEvents(DefaultEcs.World world)
    {
        _damageEventSet ??= world.GetEntities().With<DamageEvent>().AsSet();
        var entities = _damageEventSet.GetEntities();
        var count = entities.Length;
        for (var i = 0; i < count; i++)
            DefaultDestroyScratch[i] = entities[i];
        for (var i = 0; i < count; i++)
            DefaultDestroyScratch[i].Dispose();
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Regen(DefaultEcs.World world)
    {
        _healthSet ??= world.GetEntities().With<Health>().AsSet();
        var checksum = 0;
        var entities = _healthSet.GetEntities();
        for (var i = 0; i < entities.Length; i++)
        {
            ref var h = ref entities[i].Get<Health>();
            if (h.Current < h.Max)
                h.Current++;
            checksum += (int)h.Current;
        }
        return checksum;
    }
}

public static class ArchGameTickWorldFactory
{
    public static Arch.Core.World CreateWorld()
    {
        var w = Arch.Core.World.Create();

        for (var i = 0; i < GameTickData.PlayerCount; i++)
        {
            var e = w.Create(new PlayerTag(), new Team(i & 3),
                new Position { X = i, Y = i + 1, Z = i + 2 },
                new Velocity { X = 1 },
                new Rotation { Pitch = i % 360 },
                new Health { Current = 100, Max = 100 },
                new Mana { Current = 50, Max = 50 },
                new Stamina { Current = 100, Max = 100 },
                new Scale { X = 1 },
                new AttackRange(10), new SightRange(50),
                new Gold(i), new Level(1),
                new Faction(1), new Armor(10), new Speed(5));
            w.Add(e, new Color(1, 1, 1, 1));
        }

        for (var i = 0; i < GameTickData.MeleeEnemyCount; i++)
        {
            var e = w.Create(new EnemyTag(), new Team(1),
                new Position { X = i, Z = i + 1 },
                new Velocity { X = -1 },
                new Rotation { Yaw = 180 },
                new Health { Current = 30 + (i & 31), Max = 30 + (i & 31) },
                new Mana { Current = 10, Max = 10 },
                new Stamina { Current = 50, Max = 50 },
                new Scale { X = 1.5f },
                new AttackRange(2), new SightRange(30),
                new Gold(10 + (i & 7)), new Level(1 + (i & 3)),
                new Faction(2), new Armor(5), new Speed(3));
            w.Add(e, new Damage { Base = 5 + (i & 7) });
        }

        for (var i = 0; i < GameTickData.RangedEnemyCount; i++)
        {
            var e = w.Create(new EnemyTag(), new Team(2),
                new Position { X = i * 2, Z = i * 2 + 5 },
                new Velocity { X = 0 },
                new Rotation { Yaw = 90 },
                new Health { Current = 20 + (i & 15), Max = 20 + (i & 15) },
                new Mana { Current = 30, Max = 30 },
                new Stamina { Current = 40, Max = 40 },
                new Scale { X = 1.2f },
                new AttackRange(15), new SightRange(40),
                new Gold(15 + (i & 7)), new Level(1 + (i & 2)),
                new Faction(2), new Armor(3), new Speed(2));
            w.Add(e, new Damage { Base = 8 + (i & 3) });
        }

        for (var i = 0; i < GameTickData.BossEnemyCount; i++)
        {
            var e = w.Create(new EnemyTag(), new Team(3),
                new Position { X = i * 10, Z = i * 10 + 50 },
                new Velocity { X = 0.5f },
                new Rotation { Yaw = 0 },
                new Health { Current = 500, Max = 500 },
                new Mana { Current = 200, Max = 200 },
                new Stamina { Current = 200, Max = 200 },
                new Scale { X = 3 },
                new AttackRange(20), new SightRange(60),
                new Gold(100 + i * 10), new Level(10 + i * 2),
                new Faction(2), new Armor(20));
            w.Add(e, new Speed(1));
            w.Add(e, new Damage { Base = 15 });
            w.Add(e, new Resistance(10, 10, 5, 5));
            w.Add(e, new GAttr(100, 50, 30, 20));
            w.Add(e, new Abilities(1, 2, 3, 0, 0, 0, 0, 0));
        }

        for (var i = 0; i < GameTickData.NpcCount; i++)
        {
            var e = w.Create(new NpcTag(), new Team(0),
                new Position { X = i * 3, Z = i * 3 + 2 },
                new Rotation { Yaw = i % 360 },
                new Health { Current = 100, Max = 100 },
                new Mana { Current = 100, Max = 100 },
                new Scale { X = 1 },
                new Gold(5 + (i & 15)), new Level(1),
                new Faction(1), new Speed(0));
        }

        for (var i = 0; i < GameTickData.PetCount; i++)
        {
            var e = w.Create(new NpcTag(), new Team(i & 1),
                new Position { X = i * 2 + 1, Z = i * 2 + 1 },
                new Velocity { X = 0.3f },
                new Health { Current = 50, Max = 50 },
                new Scale { X = 0.5f },
                new SightRange(20),
                new Level(1), new Speed(4),
                new AttackRange(2));
        }

        for (var i = 0; i < GameTickData.ProjectileCount; i++)
        {
            var e = w.Create(new ProjectileTag(), new Team(i & 1),
                new Position { X = i * 5, Z = i * 5 },
                new Velocity { X = 2 * (i % 3 == 0 ? -1 : 1), Z = 2 * (i % 2 == 0 ? 1 : -1) },
                new Rotation { Yaw = 45 },
                new Scale { X = 0.3f },
                new Speed(10), new Damage { Base = 5 + (i & 3) },
                new Faction(i & 1));
        }

        for (var i = 0; i < GameTickData.StaticObjectCount; i++)
        {
            w.Create(new Position { X = i * 5, Z = i * 5 },
                new Rotation { Yaw = i * 45 },
                new Size { W = 2, H = 2, D = 2 },
                new Color(i % 255, (i * 2) % 255, (i * 3) % 255, 255),
                new MeshId(i % 10), new RenderLayer(1));
        }

        for (var i = 0; i < GameTickData.DestructibleCount; i++)
        {
            w.Create(new Position { X = i * 7 + 3, Z = i * 7 + 3 },
                new Health { Current = 20 + (i & 31), Max = 20 + (i & 31) },
                new Size { W = 1, H = 1, D = 1 },
                new Scale { X = 0.8f },
                new MeshId(100 + (i % 5)), new RenderLayer(1));
        }

        for (var i = 0; i < GameTickData.EnvironmentCount; i++)
        {
            w.Create(new Position { X = i * 3, Z = i * 3 },
                new Size { W = 10, H = 5, D = 10 },
                new Color((i * 50) % 255, (i * 30) % 255, 0, 255),
                new MeshId(200 + (i % 3)), new RenderLayer(0));
        }

        for (var i = 0; i < GameTickData.TrapCount; i++)
        {
            w.Create(new Position { X = i * 12 + 6, Z = i * 12 + 6 },
                new Size { W = 1, H = 0.5f, D = 1 },
                new Health { Current = 10, Max = 10 },
                new Damage { Base = 5 + (i & 3) },
                new MeshId(300), new RenderLayer(2));
        }

        for (var i = 0; i < GameTickData.LootDropCount; i++)
        {
            w.Create(new Position { X = i * 8 + 2, Z = i * 8 + 2 },
                new Size { W = 0.5f, H = 0.5f, D = 0.5f },
                new Gold(1 + (i & 15)),
                new MeshId(400 + (i % 8)), new RenderLayer(1));
        }

        return w;
    }
}

public static class ArchGameTickRunner
{
    private static readonly Arch.Core.QueryDescription MovementDesc = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();
    private static readonly Arch.Core.QueryDescription HealthDesc = new Arch.Core.QueryDescription().WithAll<Health>();
    private static readonly Arch.Core.QueryDescription DamageEventDesc = new Arch.Core.QueryDescription().WithAll<DamageEvent>();
    private static readonly Arch.Core.QueryDescription DebuffDesc = new Arch.Core.QueryDescription().WithAll<Debuff>();
    private static readonly Arch.Core.QueryDescription AllEntitiesDesc = new Arch.Core.QueryDescription().WithAll<Position>();
    private static readonly Arch.Core.QueryDescription ManaDesc = new Arch.Core.QueryDescription().WithAll<Mana>();
    private static readonly Arch.Core.QueryDescription StaminaDesc = new Arch.Core.QueryDescription().WithAll<Stamina>();
    private static readonly Arch.Core.QueryDescription AttackRangeDesc = new Arch.Core.QueryDescription().WithAll<Position, AttackRange>();
    private static readonly Arch.Core.QueryDescription TransformDesc = new Arch.Core.QueryDescription().WithAll<Position, Rotation, Scale>();
    private static readonly Arch.Core.Entity[] ArchDestroyScratch = new Arch.Core.Entity[GameTickData.SpawnPerTick];
    private static readonly Arch.Core.Entity[] ArchDebuffScratch = new Arch.Core.Entity[GameTickData.DebuffPerTick];
    private static Arch.Core.Entity[] _archEntityPool = [];
    private static int _archEntityCount;
    private static int _archDebuffIndex;
    private static readonly Random _archRng = new(42);

    public static void Initialize(Arch.Core.World world)
    {
        var query = world.Query(in AllEntitiesDesc);
        var total = 0;
        foreach (var chunk in query)
            total += chunk.Count;
        _archEntityPool = new Arch.Core.Entity[total];
        var idx = 0;
        query = world.Query(in AllEntitiesDesc);
        foreach (var chunk in query)
        {
            for (var row = 0; row < chunk.Count; row++)
                _archEntityPool[idx++] = chunk.Entity(row);
        }
        _archEntityCount = total;
        _archDebuffIndex = 0;
    }

    public static int ExecuteTick(Arch.Core.World world)
    {
        var checksum = 0;
        checksum += Movement(world);
        checksum += ManaRegen(world);
        checksum += RemoveDebuffs(world);
        checksum += RangeCheck(world);
        checksum += AddDebuffs(world);
        checksum += StaminaRegen(world);
        checksum += SpawnDamageEvents(world);
        checksum += ProcessDamageEvents(world);
        checksum += UpdateTransforms(world);
        checksum += SpawnDestroyMainPool(world);
        checksum += Regen(world);
        checksum += CleanupDamageEvents(world);
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Movement(Arch.Core.World world)
    {
        var checksum = 0;
        var query = world.Query(in MovementDesc);
        foreach (var chunk in query)
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var row = 0; row < chunk.Count; row++)
            {
                positions[row].X += velocities[row].X;
                positions[row].Y += velocities[row].Y;
                positions[row].Z += velocities[row].Z;
                checksum += (int)(positions[row].X + positions[row].Y + positions[row].Z);
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManaRegen(Arch.Core.World world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(in ManaDesc))
        {
            var mana = chunk.GetSpan<Mana>();
            for (var row = 0; row < chunk.Count; row++)
            {
                if (mana[row].Current < mana[row].Max)
                    mana[row].Current += 0.5f;
                checksum += (int)mana[row].Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RangeCheck(Arch.Core.World world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(in AttackRangeDesc))
        {
            var pos = chunk.GetSpan<Position>();
            var range = chunk.GetSpan<AttackRange>();
            for (var row = 0; row < chunk.Count; row++)
                checksum += (int)(pos[row].X * pos[row].Z + range[row].Value);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StaminaRegen(Arch.Core.World world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(in StaminaDesc))
        {
            var stam = chunk.GetSpan<Stamina>();
            for (var row = 0; row < chunk.Count; row++)
            {
                if (stam[row].Current < stam[row].Max)
                    stam[row].Current += 0.3f;
                checksum += (int)stam[row].Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int UpdateTransforms(Arch.Core.World world)
    {
        var checksum = 0;
        foreach (var chunk in world.Query(in TransformDesc))
        {
            var pos = chunk.GetSpan<Position>();
            var rot = chunk.GetSpan<Rotation>();
            var scale = chunk.GetSpan<Scale>();
            for (var row = 0; row < chunk.Count; row++)
                checksum += (int)(pos[row].X + rot[row].Yaw + scale[row].X);
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RemoveDebuffs(Arch.Core.World world)
    {
        var total = 0;
        var query = world.Query(in DebuffDesc);
        foreach (var chunk in query)
        {
            for (var row = 0; row < chunk.Count; row++)
                ArchDebuffScratch[total++] = chunk.Entity(row);
        }
        for (var i = 0; i < total; i++)
            world.Remove<Debuff>(ArchDebuffScratch[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AddDebuffs(Arch.Core.World world)
    {
        var start = _archDebuffIndex;
        for (var i = 0; i < GameTickData.DebuffPerTick; i++)
        {
            var idx = (start + i) % _archEntityCount;
            world.Add(_archEntityPool[idx], new Debuff { Timer = 1.0f });
        }
        _archDebuffIndex = (start + GameTickData.DebuffPerTick) % _archEntityCount;
        return GameTickData.DebuffPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDestroyMainPool(Arch.Core.World world)
    {
        var toDestroy = Math.Min(GameTickData.DestroyPerTick, _archEntityCount);
        for (var i = 0; i < toDestroy; i++)
        {
            var idx = _archRng.Next(_archEntityCount);
            var entity = _archEntityPool[idx];
            _archEntityCount--;
            _archEntityPool[idx] = _archEntityPool[_archEntityCount];
            world.Destroy(entity);
        }
        var toCreate = GameTickData.DestroyPerTick;
        if (_archEntityPool.Length < _archEntityCount + toCreate)
            Array.Resize(ref _archEntityPool, _archEntityCount + toCreate + 4096);
        for (var i = 0; i < toCreate; i++)
        {
            var e = world.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });
            _archEntityPool[_archEntityCount++] = e;
        }
        return GameTickData.DestroyPerTick * 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDamageEvents(Arch.Core.World world)
    {
        for (var i = 0; i < GameTickData.SpawnPerTick; i++)
            world.Create(new DamageEvent { Amount = 1 });
        return GameTickData.SpawnPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProcessDamageEvents(Arch.Core.World world)
    {
        var checksum = 0;
        var query = world.Query(in HealthDesc);
        foreach (var chunk in query)
        {
            var health = chunk.GetSpan<Health>();
            for (var row = 0; row < chunk.Count; row++)
            {
                health[row].Current -= 1;
                checksum += (int)health[row].Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CleanupDamageEvents(Arch.Core.World world)
    {
        var total = 0;
        var query = world.Query(in DamageEventDesc);
        foreach (var chunk in query)
        {
            for (var row = 0; row < chunk.Count; row++)
                ArchDestroyScratch[total++] = chunk.Entity(row);
        }
        for (var i = 0; i < total; i++)
            world.Destroy(ArchDestroyScratch[i]);
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Regen(Arch.Core.World world)
    {
        var checksum = 0;
        var query = world.Query(in HealthDesc);
        foreach (var chunk in query)
        {
            var health = chunk.GetSpan<Health>();
            for (var row = 0; row < chunk.Count; row++)
            {
                if (health[row].Current < health[row].Max)
                    health[row].Current++;
                checksum += (int)health[row].Current;
            }
        }
        return checksum;
    }
}
