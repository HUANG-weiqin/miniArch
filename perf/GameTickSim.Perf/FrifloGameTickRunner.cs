using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using MiniArchBenchmarks.GameTick;

namespace GameTickSim;

// ============================================================
// Friflo-specific component definitions (IComponent required)
// ============================================================

// Note: Using IComponent instead of ITag because AddComponent<T>() requires IComponent.
// Friflo separates tags and components; here we use empty components for archetype compatibility.
public record struct FrifloPlayerTag : IComponent;
public record struct FrifloEnemyTag : IComponent;
public record struct FrifloNpcTag : IComponent;
public record struct FrifloProjectileTag : IComponent;

public record struct FrifloTeam(int Value) : IComponent;
public record struct FrifloLevel(int Value) : IComponent;
public record struct FrifloGold(int Value) : IComponent;
public record struct FrifloFaction(int Value) : IComponent;
public record struct FrifloDamage : IComponent { public int Base; public int Bonus; }
public record struct FrifloArmor(int Value) : IComponent;
public record struct FrifloSpeed(float Value) : IComponent;
public record struct FrifloSightRange(float Value) : IComponent;
public record struct FrifloAttackRange(float Value) : IComponent;

public record struct FrifloPosition : IComponent { public float X; public float Y; public float Z; }
public record struct FrifloVelocity : IComponent { public float X; public float Y; public float Z; }
public record struct FrifloRotation : IComponent { public float Pitch; public float Yaw; public float Roll; }
public record struct FrifloScale : IComponent { public float X; public float Y; public float Z; }
public record struct FrifloHealth : IComponent { public float Current; public float Max; }
public record struct FrifloMana : IComponent { public float Current; public float Max; }
public record struct FrifloStamina : IComponent { public float Current; public float Max; }

public record struct FrifloColor(float R, float G, float B, float A) : IComponent;
public record struct FrifloSize : IComponent { public float W; public float H; public float D; }

public record struct FrifloMeshId(int Value) : IComponent;
public record struct FrifloRenderLayer(int Value) : IComponent;

public record struct FrifloResistance(int Fire, int Ice, int Poison, int Lightning) : IComponent;
public record struct FrifloAttribute(int Str, int Dex, int Int, int Wis) : IComponent;
public record struct FrifloAbilities(int Id0, int Id1, int Id2, int Id3, float Cd0, float Cd1, float Cd2, float Cd3) : IComponent;

public record struct FrifloDamageEvent : IComponent { public int Amount; }
public record struct FrifloDebuff : IComponent { public float Timer; }
public record struct FrifloLifetime : IComponent { public float Value; }
public record struct FrifloBossTag : IComponent;
public record struct FrifloAiState : IComponent { public int Kind; public float Timer; }
public record struct FrifloBurningTag : IComponent;
public record struct FrifloPoisonedTag : IComponent;
public record struct FrifloStatusTimer : IComponent { public float Remaining; }
public record struct FrifloBuffRemaining : IComponent { public float Value; }
public record struct FrifloDeadTag : IComponent;
public record struct FrifloBuffEffect : IComponent { public int Type; public float Remaining; }

// ============================================================
// FrifloGameTickWorldFactory
// ============================================================

public static class FrifloGameTickWorldFactory
{
    public static EntityStore CreateWorld()
    {
        var store = new EntityStore();

        for (var i = 0; i < GameTickData.PlayerCount; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new FrifloPlayerTag());
            e.AddComponent(new FrifloTeam(i & 3));
            e.AddComponent(new FrifloPosition { X = i, Y = i + 1, Z = i + 2 });
            e.AddComponent(new FrifloVelocity { X = 1 });
            e.AddComponent(new FrifloRotation { Pitch = i % 360 });
            e.AddComponent(new FrifloHealth { Current = 100, Max = 100 });
            e.AddComponent(new FrifloMana { Current = 50, Max = 50 });
            e.AddComponent(new FrifloStamina { Current = 100, Max = 100 });
            e.AddComponent(new FrifloScale { X = 1 });
            e.AddComponent(new FrifloAttackRange(10));
            e.AddComponent(new FrifloSightRange(50));
            e.AddComponent(new FrifloGold(i));
            e.AddComponent(new FrifloLevel(1));
            e.AddComponent(new FrifloFaction(1));
            e.AddComponent(new FrifloArmor(10));
            e.AddComponent(new FrifloSpeed(5));
            e.AddComponent(new FrifloColor(1, 1, 1, 1));
        }

        for (var i = 0; i < GameTickData.MeleeEnemyCount; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new FrifloEnemyTag());
            e.AddComponent(new FrifloTeam(1));
            e.AddComponent(new FrifloPosition { X = i, Z = i + 1 });
            e.AddComponent(new FrifloVelocity { X = -1 });
            e.AddComponent(new FrifloRotation { Yaw = 180 });
            e.AddComponent(new FrifloHealth { Current = 30 + (i & 31), Max = 30 + (i & 31) });
            e.AddComponent(new FrifloMana { Current = 10, Max = 10 });
            e.AddComponent(new FrifloStamina { Current = 50, Max = 50 });
            e.AddComponent(new FrifloScale { X = 1.5f });
            e.AddComponent(new FrifloAttackRange(2));
            e.AddComponent(new FrifloSightRange(30));
            e.AddComponent(new FrifloGold(10 + (i & 7)));
            e.AddComponent(new FrifloLevel(1 + (i & 3)));
            e.AddComponent(new FrifloFaction(2));
            e.AddComponent(new FrifloArmor(5));
            e.AddComponent(new FrifloSpeed(3));
            e.AddComponent(new FrifloDamage { Base = 5 + (i & 7) });
        }

        for (var i = 0; i < GameTickData.RangedEnemyCount; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new FrifloEnemyTag());
            e.AddComponent(new FrifloTeam(2));
            e.AddComponent(new FrifloPosition { X = i * 2, Z = i * 2 + 5 });
            e.AddComponent(new FrifloVelocity { X = 0 });
            e.AddComponent(new FrifloRotation { Yaw = 90 });
            e.AddComponent(new FrifloHealth { Current = 20 + (i & 15), Max = 20 + (i & 15) });
            e.AddComponent(new FrifloMana { Current = 30, Max = 30 });
            e.AddComponent(new FrifloStamina { Current = 40, Max = 40 });
            e.AddComponent(new FrifloScale { X = 1.2f });
            e.AddComponent(new FrifloAttackRange(15));
            e.AddComponent(new FrifloSightRange(40));
            e.AddComponent(new FrifloGold(15 + (i & 7)));
            e.AddComponent(new FrifloLevel(1 + (i & 2)));
            e.AddComponent(new FrifloFaction(2));
            e.AddComponent(new FrifloArmor(3));
            e.AddComponent(new FrifloSpeed(2));
            e.AddComponent(new FrifloDamage { Base = 8 + (i & 3) });
        }

        for (var i = 0; i < GameTickData.BossEnemyCount; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new FrifloEnemyTag());
            e.AddComponent(new FrifloTeam(3));
            e.AddComponent(new FrifloPosition { X = i * 10, Z = i * 10 + 50 });
            e.AddComponent(new FrifloVelocity { X = 0.5f });
            e.AddComponent(new FrifloRotation { Yaw = 0 });
            e.AddComponent(new FrifloHealth { Current = 500, Max = 500 });
            e.AddComponent(new FrifloMana { Current = 200, Max = 200 });
            e.AddComponent(new FrifloStamina { Current = 200, Max = 200 });
            e.AddComponent(new FrifloScale { X = 3 });
            e.AddComponent(new FrifloAttackRange(20));
            e.AddComponent(new FrifloSightRange(60));
            e.AddComponent(new FrifloGold(100 + i * 10));
            e.AddComponent(new FrifloLevel(10 + i * 2));
            e.AddComponent(new FrifloFaction(2));
            e.AddComponent(new FrifloArmor(20));
            e.AddComponent(new FrifloSpeed(1));
            e.AddComponent(new FrifloDamage { Base = 15 });
            e.AddComponent(new FrifloResistance(10, 10, 5, 5));
            e.AddComponent(new FrifloAttribute(100, 50, 30, 20));
            e.AddComponent(new FrifloAbilities(1, 2, 3, 0, 0, 0, 0, 0));
        }

        for (var i = 0; i < GameTickData.NpcCount; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new FrifloNpcTag());
            e.AddComponent(new FrifloTeam(0));
            e.AddComponent(new FrifloPosition { X = i * 3, Z = i * 3 + 2 });
            e.AddComponent(new FrifloRotation { Yaw = i % 360 });
            e.AddComponent(new FrifloHealth { Current = 100, Max = 100 });
            e.AddComponent(new FrifloMana { Current = 100, Max = 100 });
            e.AddComponent(new FrifloScale { X = 1 });
            e.AddComponent(new FrifloGold(5 + (i & 15)));
            e.AddComponent(new FrifloLevel(1));
            e.AddComponent(new FrifloFaction(1));
            e.AddComponent(new FrifloSpeed(0));
        }

        for (var i = 0; i < GameTickData.PetCount; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new FrifloNpcTag());
            e.AddComponent(new FrifloTeam(i & 1));
            e.AddComponent(new FrifloPosition { X = i * 2 + 1, Z = i * 2 + 1 });
            e.AddComponent(new FrifloVelocity { X = 0.3f });
            e.AddComponent(new FrifloHealth { Current = 50, Max = 50 });
            e.AddComponent(new FrifloScale { X = 0.5f });
            e.AddComponent(new FrifloSightRange(20));
            e.AddComponent(new FrifloLevel(1));
            e.AddComponent(new FrifloSpeed(4));
            e.AddComponent(new FrifloAttackRange(2));
        }

        for (var i = 0; i < GameTickData.ProjectileCount; i++)
        {
            var e = store.CreateEntity();
            e.AddComponent(new FrifloProjectileTag());
            e.AddComponent(new FrifloTeam(i & 1));
            e.AddComponent(new FrifloPosition { X = i * 5, Z = i * 5 });
            e.AddComponent(new FrifloVelocity { X = 2 * (i % 3 == 0 ? -1 : 1), Z = 2 * (i % 2 == 0 ? 1 : -1) });
            e.AddComponent(new FrifloRotation { Yaw = 45 });
            e.AddComponent(new FrifloScale { X = 0.3f });
            e.AddComponent(new FrifloSpeed(10));
            e.AddComponent(new FrifloDamage { Base = 5 + (i & 3) });
            e.AddComponent(new FrifloFaction(i & 1));
        }

        for (var i = 0; i < GameTickData.StaticObjectCount; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i * 5, Z = i * 5 },
                new FrifloRotation { Yaw = i * 45 },
                new FrifloSize { W = 2, H = 2, D = 2 },
                new FrifloColor(i % 255, (i * 2) % 255, (i * 3) % 255, 255),
                new FrifloMeshId(i % 10),
                new FrifloRenderLayer(1));
        }

        for (var i = 0; i < GameTickData.DestructibleCount; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i * 7 + 3, Z = i * 7 + 3 },
                new FrifloHealth { Current = 20 + (i & 31), Max = 20 + (i & 31) },
                new FrifloSize { W = 1, H = 1, D = 1 },
                new FrifloScale { X = 0.8f },
                new FrifloMeshId(100 + (i % 5)),
                new FrifloRenderLayer(1));
        }

        for (var i = 0; i < GameTickData.EnvironmentCount; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i * 3, Z = i * 3 },
                new FrifloSize { W = 10, H = 5, D = 10 },
                new FrifloColor((i * 50) % 255, (i * 30) % 255, 0, 255),
                new FrifloMeshId(200 + (i % 3)),
                new FrifloRenderLayer(0));
        }

        for (var i = 0; i < GameTickData.TrapCount; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i * 12 + 6, Z = i * 12 + 6 },
                new FrifloSize { W = 1, H = 0.5f, D = 1 },
                new FrifloHealth { Current = 10, Max = 10 },
                new FrifloDamage { Base = 5 + (i & 3) },
                new FrifloMeshId(300),
                new FrifloRenderLayer(2));
        }

        for (var i = 0; i < GameTickData.LootDropCount; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i * 8 + 2, Z = i * 8 + 2 },
                new FrifloSize { W = 0.5f, H = 0.5f, D = 0.5f },
                new FrifloGold(1 + (i & 15)),
                new FrifloMeshId(400 + (i % 8)),
                new FrifloRenderLayer(1));
        }

        return store;
    }
}

// ============================================================
// FrifloGameTickRunner
// ============================================================

public static class FrifloGameTickRunner
{
    private static ArchetypeQuery<FrifloPosition, FrifloVelocity>? _movementQuery;
    private static ArchetypeQuery<FrifloHealth>? _healthQuery;
    private static ArchetypeQuery<FrifloDamageEvent>? _damageEventQuery;
    private static ArchetypeQuery<FrifloDebuff>? _debuffQuery;
    private static ArchetypeQuery<FrifloMana>? _manaQuery;
    private static ArchetypeQuery<FrifloStamina>? _staminaQuery;
    private static ArchetypeQuery<FrifloPosition, FrifloAttackRange>? _attackRangeQuery;
    private static ArchetypeQuery<FrifloPosition, FrifloRotation, FrifloScale>? _transformQuery;

    private static readonly Entity[] FrifloDestroyScratch = new Entity[GameTickData.SpawnPerTick];
    private static readonly Entity[] FrifloDebuffScratch = new Entity[GameTickData.DebuffPerTick];
    private static Entity[] _frifloEntityPool = [];
    private static int _frifloEntityCount;
    private static int _frifloDebuffIndex;
    private static readonly Random _frifloRng = new(42);

    public static void Initialize(EntityStore store)
    {
        _movementQuery   = store.Query<FrifloPosition, FrifloVelocity>();
        _healthQuery     = store.Query<FrifloHealth>();
        _damageEventQuery = store.Query<FrifloDamageEvent>();
        _debuffQuery     = store.Query<FrifloDebuff>();
        _manaQuery       = store.Query<FrifloMana>();
        _staminaQuery    = store.Query<FrifloStamina>();
        _attackRangeQuery  = store.Query<FrifloPosition, FrifloAttackRange>();
        _transformQuery  = store.Query<FrifloPosition, FrifloRotation, FrifloScale>();

        // Build entity pool for debuff + spawn/destroy
        var allQuery = store.Query<FrifloPosition>();
        var total = allQuery.Count;
        _frifloEntityPool = new Entity[total];
        var idx = 0;
        foreach (var chunk in allQuery.Chunks)
        {
            var entities = chunk.Entities;
            for (var row = 0; row < entities.Length; row++)
                _frifloEntityPool[idx++] = store.GetEntityById(entities[row]);
        }
        _frifloEntityCount = total;
        _frifloDebuffIndex = 0;
    }

    public static int ExecuteTick(EntityStore store)
    {
        var checksum = 0;
        checksum += Movement(store);
        checksum += ManaRegen(store);
        checksum += RemoveDebuffs(store);
        checksum += RangeCheck(store);
        checksum += AddDebuffs(store);
        checksum += StaminaRegen(store);
        checksum += SpawnDamageEvents(store);
        checksum += ProcessDamageEvents(store);
        checksum += UpdateTransforms(store);
        checksum += SpawnDestroyMainPool(store);
        checksum += Regen(store);
        checksum += CleanupDamageEvents(store);
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Movement(EntityStore store)
    {
        var checksum = 0;
        _movementQuery!.ForEachEntity((ref FrifloPosition pos, ref FrifloVelocity vel, Entity _) =>
        {
            pos.X += vel.X;
            pos.Y += vel.Y;
            pos.Z += vel.Z;
            checksum += (int)(pos.X + pos.Y + pos.Z);
        });
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManaRegen(EntityStore store)
    {
        var checksum = 0;
        foreach (var (components, _) in _manaQuery!.Chunks)
        {
            var span = components.Span;
            for (var row = 0; row < span.Length; row++)
            {
                ref var mana = ref span[row];
                if (mana.Current < mana.Max)
                    mana.Current += 0.5f;
                checksum += (int)mana.Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RangeCheck(EntityStore store)
    {
        var checksum = 0;
        _attackRangeQuery!.ForEachEntity((ref FrifloPosition pos, ref FrifloAttackRange range, Entity _) =>
        {
            checksum += (int)(pos.X * pos.Z + range.Value);
        });
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StaminaRegen(EntityStore store)
    {
        var checksum = 0;
        foreach (var (components, _) in _staminaQuery!.Chunks)
        {
            var span = components.Span;
            for (var row = 0; row < span.Length; row++)
            {
                ref var stam = ref span[row];
                if (stam.Current < stam.Max)
                    stam.Current += 0.3f;
                checksum += (int)stam.Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int UpdateTransforms(EntityStore store)
    {
        var checksum = 0;
        _transformQuery!.ForEachEntity((ref FrifloPosition pos, ref FrifloRotation rot, ref FrifloScale scale, Entity _) =>
        {
            checksum += (int)(pos.X + rot.Yaw + scale.X);
        });
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RemoveDebuffs(EntityStore store)
    {
        var total = 0;
        foreach (var chunk in _debuffQuery!.Chunks)
        {
            var entities = chunk.Entities;
            for (var row = 0; row < entities.Length; row++)
                FrifloDebuffScratch[total++] = store.GetEntityById(entities[row]);
        }
        for (var i = 0; i < total; i++)
            FrifloDebuffScratch[i].RemoveComponent<FrifloDebuff>();
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AddDebuffs(EntityStore store)
    {
        var start = _frifloDebuffIndex;
        for (var i = 0; i < GameTickData.DebuffPerTick; i++)
        {
            var idx = (start + i) % _frifloEntityCount;
            _frifloEntityPool[idx].AddComponent(new FrifloDebuff { Timer = 1.0f });
        }
        _frifloDebuffIndex = (start + GameTickData.DebuffPerTick) % _frifloEntityCount;
        return GameTickData.DebuffPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDestroyMainPool(EntityStore store)
    {
        var toDestroy = Math.Min(GameTickData.DestroyPerTick, _frifloEntityCount);
        for (var i = 0; i < toDestroy; i++)
        {
            var idx = _frifloRng.Next(_frifloEntityCount);
            var entity = _frifloEntityPool[idx];
            _frifloEntityCount--;
            _frifloEntityPool[idx] = _frifloEntityPool[_frifloEntityCount];
            entity.DeleteEntity();
        }
        var toCreate = GameTickData.DestroyPerTick;
        if (_frifloEntityPool.Length < _frifloEntityCount + toCreate)
            Array.Resize(ref _frifloEntityPool, _frifloEntityCount + toCreate + 4096);
        for (var i = 0; i < toCreate; i++)
        {
            var e = store.CreateEntity(
                new FrifloPosition { X = i },
                new FrifloHealth { Current = 100, Max = 100 });
            _frifloEntityPool[_frifloEntityCount++] = e;
        }
        return GameTickData.DestroyPerTick * 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SpawnDamageEvents(EntityStore store)
    {
        for (var i = 0; i < GameTickData.SpawnPerTick; i++)
            store.CreateEntity(new FrifloDamageEvent { Amount = 1 });
        return GameTickData.SpawnPerTick;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProcessDamageEvents(EntityStore store)
    {
        var checksum = 0;
        foreach (var (components, _) in _healthQuery!.Chunks)
        {
            var span = components.Span;
            for (var row = 0; row < span.Length; row++)
            {
                ref var health = ref span[row];
                health.Current -= 1;
                checksum += (int)health.Current;
            }
        }
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CleanupDamageEvents(EntityStore store)
    {
        var total = 0;
        foreach (var chunk in _damageEventQuery!.Chunks)
        {
            var entities = chunk.Entities;
            for (var row = 0; row < entities.Length; row++)
                FrifloDestroyScratch[total++] = store.GetEntityById(entities[row]);
        }
        for (var i = 0; i < total; i++)
            FrifloDestroyScratch[i].DeleteEntity();
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Regen(EntityStore store)
    {
        var checksum = 0;
        foreach (var (components, _) in _healthQuery!.Chunks)
        {
            var span = components.Span;
            for (var row = 0; row < span.Length; row++)
            {
                ref var health = ref span[row];
                if (health.Current < health.Max)
                    health.Current++;
                checksum += (int)health.Current;
            }
        }
        return checksum;
    }
}
