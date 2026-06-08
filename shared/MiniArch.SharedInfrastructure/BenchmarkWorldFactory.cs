using Arch.Core;
using MiniArch.Core;
using DefaultEcs;

namespace MiniArchBenchmarks;

using MiniEntity = MiniArch.Entity;
using MiniQuery = MiniArch.Core.Query;
using MiniQueryDescription = MiniArch.QueryDescription;
using MiniWorld = MiniArch.World;
using MiniComponentType = MiniArch.Core.ComponentType;
using ArchQueryDescription = Arch.Core.QueryDescription;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using DefaultEntity = DefaultEcs.Entity;
using DefaultWorld = DefaultEcs.World;

public static partial class BenchmarkWorldFactory
{
    public static MiniComplexQueryWorldState CreateMiniComplexQueryWorld(int entityCount)
    {
        var world = new MiniWorld();
        var entities = PopulateMiniComplexQueryWorld(world, entityCount);
        return new MiniComplexQueryWorldState(world, entities);
    }

    public static ArchComplexQueryWorldState CreateArchComplexQueryWorld(int entityCount)
    {
        var world = ArchWorld.Create();
        var entities = PopulateArchComplexQueryWorld(world, entityCount);
        return new ArchComplexQueryWorldState(world, entities);
    }

    public static MiniCreateManyWorldState CreateMiniCreateManyRecycledWorld(int entityCount)
    {
        var world = new MiniWorld();
        var created = new MiniEntity[entityCount];
        world.CreateMany(created);

        for (var i = 0; i < created.Length; i++)
        {
            world.Destroy(created[i]);
        }

        return new MiniCreateManyWorldState(world, new MiniEntity[entityCount]);
    }

    public static MiniCreateManyWorldState CreateMiniCreateManyMixedWorld(int entityCount)
    {
        var world = new MiniWorld();
        var created = new MiniEntity[entityCount];
        world.CreateMany(created);

        for (var i = 0; i < entityCount / 2; i++)
        {
            world.Destroy(created[i]);
        }

        return new MiniCreateManyWorldState(world, new MiniEntity[entityCount]);
    }

    public static MiniWorldState CreateMiniEmptyWorld(int entityCount)
    {
        var world = new MiniWorld();
        var entities = new MiniEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = world.Create();
        }

        return new MiniWorldState(world, entities);
    }

    public static MiniWorldState CreateMiniWorldWithPosition(int entityCount)
    {
        var state = CreateMiniEmptyWorld(entityCount);
        for (var i = 0; i < entityCount; i++)
        {
            state.World.Add(state.Entities[i], new Position(i, i));
        }

        return state;
    }

    public static MiniWorldState CreateMiniWorldWithPositionAndVelocity(int entityCount)
    {
        var state = CreateMiniEmptyWorld(entityCount);
        for (var i = 0; i < entityCount; i++)
        {
            state.World.Add(state.Entities[i], new Position(i, i));
            state.World.Add(state.Entities[i], new Velocity(i, i));
        }

        return state;
    }

    public static MiniWorldState CreateMiniWorldWithTenComponents(int entityCount)
    {
        var state = CreateMiniEmptyWorld(entityCount);
        for (var i = 0; i < entityCount; i++)
        {
            var entity = state.Entities[i];
            state.World.Add(entity, new Position(i, i + 1));
            state.World.Add(entity, new Velocity(i + 2, i + 3));
            state.World.Add(entity, new Health(100 + i));
            state.World.Add(entity, new Mana(200 + i));
            state.World.Add(entity, new Armor(300 + i));
            state.World.Add(entity, new DamageRange(i + 4, i + 5));
            state.World.Add(entity, new Team(i % 4));
            state.World.Add(entity, new Cooldown(400 + i));
            state.World.Add(entity, new SpawnTick(500 + i));
            state.World.Add(entity, new Target((i + 1) % Math.Max(entityCount, 1)));
        }

        return state;
    }

    public static ArchWorldState CreateArchEmptyWorld(int entityCount)
    {
        var world = ArchWorld.Create();
        var entities = new ArchEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = world.Create();
        }

        return new ArchWorldState(world, entities);
    }

    public static ArchWorldState CreateArchWorldWithPosition(int entityCount)
    {
        var state = CreateArchEmptyWorld(entityCount);
        for (var i = 0; i < entityCount; i++)
        {
            state.World.Add(state.Entities[i], new Position(i, i));
        }

        return state;
    }

    public static ArchWorldState CreateArchWorldWithPositionAndVelocity(int entityCount)
    {
        var state = CreateArchEmptyWorld(entityCount);
        for (var i = 0; i < entityCount; i++)
        {
            state.World.Add(state.Entities[i], new Position(i, i));
            state.World.Add(state.Entities[i], new Velocity(i, i));
        }

        return state;
    }

    public static ArchCreateManyWorldState CreateArchCreateManyRecycledWorld(int entityCount)
    {
        var world = ArchWorld.Create();
        var created = new ArchEntity[entityCount];
        for (var i = 0; i < created.Length; i++)
        {
            created[i] = world.Create();
        }

        for (var i = 0; i < created.Length; i++)
        {
            world.Destroy(created[i]);
        }

        return new ArchCreateManyWorldState(world, new ArchEntity[entityCount]);
    }

    public static ArchCreateManyWorldState CreateArchCreateManyMixedWorld(int entityCount)
    {
        var world = ArchWorld.Create();
        var created = new ArchEntity[entityCount];
        for (var i = 0; i < created.Length; i++)
        {
            created[i] = world.Create();
        }

        for (var i = 0; i < entityCount / 2; i++)
        {
            world.Destroy(created[i]);
        }

        return new ArchCreateManyWorldState(world, new ArchEntity[entityCount]);
    }

    private static MiniEntity[] PopulateMiniComplexQueryWorld(MiniWorld world, int entityCount)
    {
        var archetypeCounts = GetComplexQueryArchetypeCounts(entityCount);
        var entities = new MiniEntity[entityCount];
        var entityIndex = 0;

        for (var group = 0; group < archetypeCounts.Length; group++)
        {
            for (var i = 0; i < archetypeCounts[group]; i++)
            {
                var entity = CreateMiniComplexEntity(world, group, entityIndex);
                entities[entityIndex] = entity;
                entityIndex++;
            }
        }

        return entities;
    }

    private static ArchEntity[] PopulateArchComplexQueryWorld(ArchWorld world, int entityCount)
    {
        var archetypeCounts = GetComplexQueryArchetypeCounts(entityCount);
        var entities = new ArchEntity[entityCount];
        var entityIndex = 0;

        for (var group = 0; group < archetypeCounts.Length; group++)
        {
            for (var i = 0; i < archetypeCounts[group]; i++)
            {
                var entity = CreateArchComplexEntity(world, group, entityIndex);
                entities[entityIndex] = entity;
                entityIndex++;
            }
        }

        return entities;
    }

    private static MiniEntity[] PopulateMiniWideQueryWorld(MiniWorld world, int entityCount)
    {
        var entities = new MiniEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = CreateMiniWideEntity(world, i);
        }

        return entities;
    }

    private static ArchEntity[] PopulateArchWideQueryWorld(ArchWorld world, int entityCount)
    {
        var entities = new ArchEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = CreateArchWideEntity(world, i);
        }

        return entities;
    }

    private static MiniEntity CreateMiniWideEntity(MiniWorld world, int i)
    {
        return world.Create(
            new Position(i, i + 1),
            new Velocity(i + 2, i + 3),
            new Health(100 + (i % 50)),
            new Team(i % 4),
            new Acceleration(i + 4, i + 5),
            new Mana(i % 100),
            new Shield(50 + (i % 10)),
            new Damage(10 + (i % 5)));
    }

    private static ArchEntity CreateArchWideEntity(ArchWorld world, int i)
    {
        return world.Create(
            new Position(i, i + 1),
            new Velocity(i + 2, i + 3),
            new Health(100 + (i % 50)),
            new Team(i % 4),
            new Acceleration(i + 4, i + 5),
            new Mana(i % 100),
            new Shield(50 + (i % 10)),
            new Damage(10 + (i % 5)));
    }

    private static int[] GetComplexQueryArchetypeCounts(int entityCount)
    {
        if (entityCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCount));
        }

        var counts = new int[5];
        counts[0] = entityCount * 40 / 100;
        counts[1] = entityCount * 20 / 100;
        counts[2] = entityCount * 15 / 100;
        counts[3] = entityCount * 15 / 100;
        counts[4] = entityCount - counts[0] - counts[1] - counts[2] - counts[3];
        return counts;
    }

    private static MiniEntity CreateMiniComplexEntity(MiniWorld world, int group, int entityIndex)
    {
        switch (group)
        {
            case 0:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new AnyTagA(entityIndex));
            case 1:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new ExcludedTag(entityIndex));
            case 2:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new Shield(50 + (entityIndex % 10)),
                    new AnyTagB(entityIndex));
            case 3:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new Shield(80 + (entityIndex % 10)),
                    new Damage(10 + (entityIndex % 5)));
            case 4:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new Damage(20 + (entityIndex % 7)),
                    new ExcludedTag(entityIndex));
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }
    }

    private static ArchEntity CreateArchComplexEntity(ArchWorld world, int group, int entityIndex)
    {
        switch (group)
        {
            case 0:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new AnyTagA(entityIndex));
            case 1:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new ExcludedTag(entityIndex));
            case 2:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new Shield(50 + (entityIndex % 10)),
                    new AnyTagB(entityIndex));
            case 3:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Health(100 + (entityIndex % 50)),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new Shield(80 + (entityIndex % 10)),
                    new Damage(10 + (entityIndex % 5)));
            case 4:
                return world.Create(
                    new Position(entityIndex, entityIndex + 1),
                    new Velocity(entityIndex + 2, entityIndex + 3),
                    new Team(entityIndex % 4),
                    new Acceleration(entityIndex + 4, entityIndex + 5),
                    new Mana(entityIndex % 100),
                    new Mass(1 + (entityIndex % 8)),
                    new Rotation(entityIndex % 360),
                    new Damage(20 + (entityIndex % 7)),
                    new ExcludedTag(entityIndex));
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }
    }
}

public sealed class MiniWorldState
{
    public MiniWorldState(MiniWorld world, MiniEntity[] entities)
    {
        World = world;
        Entities = entities;
    }

    public MiniWorld World;
    public MiniEntity[] Entities;
}

public sealed class ArchWorldState : IDisposable
{
    public ArchWorldState(ArchWorld world, ArchEntity[] entities)
    {
        World = world;
        Entities = entities;
    }

    public ArchWorld World;
    public ArchEntity[] Entities;

    public void Dispose()
    {
        World.Dispose();
    }
}

public sealed class MiniCreateManyWorldState
{
    public MiniCreateManyWorldState(MiniWorld world, MiniEntity[] buffer)
    {
        World = world;
        Buffer = buffer;
    }

    public MiniWorld World;
    public MiniEntity[] Buffer;
}

public sealed class ArchCreateManyWorldState : IDisposable
{
    public ArchCreateManyWorldState(ArchWorld world, ArchEntity[] buffer)
    {
        World = world;
        Buffer = buffer;
    }

    public ArchWorld World;
    public ArchEntity[] Buffer;

    public void Dispose()
    {
        World.Dispose();
    }
}

public sealed class MiniComplexQueryWorldState
{
    public MiniComplexQueryWorldState(MiniWorld world, MiniEntity[] entities)
    {
        World = world;
        Entities = entities;
        PositionType = world.Components.GetOrCreate<Position>();
        VelocityType = world.Components.GetOrCreate<Velocity>();
        WithAllQuery = BenchmarkWorldFactory.BuildMiniWithAllQuery(world);
        WithAllWithoutQuery = BenchmarkWorldFactory.BuildMiniWithAllWithoutQuery(world);
        WithAllAnyQuery = BenchmarkWorldFactory.BuildMiniWithAllAnyQuery(world);

        _ = WithAllQuery.MatchedArchetypes;
        _ = WithAllWithoutQuery.MatchedArchetypes;
        _ = WithAllAnyQuery.MatchedArchetypes;
    }

    public MiniWorld World;
    public MiniEntity[] Entities;
    public MiniComponentType PositionType;
    public MiniComponentType VelocityType;
    public MiniQuery WithAllQuery;
    public MiniQuery WithAllWithoutQuery;
    public MiniQuery WithAllAnyQuery;
}

public sealed class ArchComplexQueryWorldState : IDisposable
{
    public ArchComplexQueryWorldState(ArchWorld world, ArchEntity[] entities)
    {
        World = world;
        Entities = entities;
        WithAllDescription = BenchmarkWorldFactory.BuildArchWithAllDescription();
        WithAllWithoutDescription = BenchmarkWorldFactory.BuildArchWithAllWithoutDescription();
        WithAllAnyDescription = BenchmarkWorldFactory.BuildArchWithAllAnyDescription();
    }

    public ArchWorld World;
    public ArchEntity[] Entities;
    public ArchQueryDescription WithAllDescription;
    public ArchQueryDescription WithAllWithoutDescription;
    public ArchQueryDescription WithAllAnyDescription;

    public void Dispose()
    {
        World.Dispose();
    }
}

public sealed class MiniWideQueryWorldState
{
    public MiniWideQueryWorldState(MiniWorld world, MiniEntity[] entities)
    {
        World = world;
        Entities = entities;
        PositionType = world.Components.GetOrCreate<Position>();
        VelocityType = world.Components.GetOrCreate<Velocity>();
        HealthType = world.Components.GetOrCreate<Health>();
        TeamType = world.Components.GetOrCreate<Team>();
        AccelerationType = world.Components.GetOrCreate<Acceleration>();
        ManaType = world.Components.GetOrCreate<Mana>();
        WideQuery = BenchmarkWorldFactory.BuildMiniWideQuery(world);

        _ = WideQuery.MatchedArchetypes;
    }

    public MiniWorld World;
    public MiniEntity[] Entities;
    public MiniComponentType PositionType;
    public MiniComponentType VelocityType;
    public MiniComponentType HealthType;
    public MiniComponentType TeamType;
    public MiniComponentType AccelerationType;
    public MiniComponentType ManaType;
    public MiniQuery WideQuery;
}

public sealed class ArchWideQueryWorldState : IDisposable
{
    public ArchWideQueryWorldState(ArchWorld world, ArchEntity[] entities)
    {
        World = world;
        Entities = entities;
        WideDescription = BenchmarkWorldFactory.BuildArchWideDescription();
    }

    public ArchWorld World;
    public ArchEntity[] Entities;
    public ArchQueryDescription WideDescription;

    public void Dispose()
    {
        World.Dispose();
    }
}

public static partial class BenchmarkWorldFactory
{
    public static MiniWideQueryWorldState CreateMiniWideQueryWorld(int entityCount)
    {
        var world = new MiniWorld();
        var entities = PopulateMiniWideQueryWorld(world, entityCount);
        return new MiniWideQueryWorldState(world, entities);
    }

    public static ArchWideQueryWorldState CreateArchWideQueryWorld(int entityCount)
    {
        var world = ArchWorld.Create();
        var entities = PopulateArchWideQueryWorld(world, entityCount);
        return new ArchWideQueryWorldState(world, entities);
    }

    internal static MiniQuery BuildMiniWithAllQuery(MiniWorld world)
    {
        var description = new MiniQueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>();

        return MiniQuery.Create(world, in description);
    }

    internal static MiniQuery BuildMiniWideQuery(MiniWorld world)
    {
        var description = new MiniQueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .With<Acceleration>()
            .With<Mana>();

        return MiniQuery.Create(world, in description);
    }

    internal static ArchQueryDescription BuildArchWideDescription()
    {
        return new ArchQueryDescription()
            .WithAll<Position, Velocity, Health, Team, Acceleration, Mana>();
    }

    internal static MiniQuery BuildMiniWithAllWithoutQuery(MiniWorld world)
    {
        var description = new MiniQueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Without<ExcludedTag>();

        return MiniQuery.Create(world, in description);
    }

    internal static MiniQuery BuildMiniWithAllAnyQuery(MiniWorld world)
    {
        var description = new MiniQueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .WithAny<AnyTagA>()
            .WithAny<AnyTagB>();

        return MiniQuery.Create(world, in description);
    }

    internal static ArchQueryDescription BuildArchWithAllDescription()
    {
        return new ArchQueryDescription()
            .WithAll<Position, Velocity, Health, Team>();
    }

    internal static ArchQueryDescription BuildArchWithAllWithoutDescription()
    {
        return new ArchQueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithNone<ExcludedTag>();
    }

    internal static ArchQueryDescription BuildArchWithAllAnyDescription()
    {
        return new ArchQueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithAny<AnyTagA, AnyTagB>();
    }

    public static DefaultWorldState CreateDefaultWorldWithPosition(int entityCount)
    {
        var world = new DefaultWorld();
        var entities = new DefaultEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = world.CreateEntity();
            entities[i].Set(new Position(i, i));
        }

        return new DefaultWorldState(world, entities);
    }

    public static DefaultWorldState CreateDefaultWorldWithPositionAndVelocity(int entityCount)
    {
        var world = new DefaultWorld();
        var entities = new DefaultEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = world.CreateEntity();
            entities[i].Set(new Position(i, i));
            entities[i].Set(new Velocity(i, i));
        }

        return new DefaultWorldState(world, entities);
    }

    public static DefaultEntityQueryWorldState CreateDefaultComplexQueryWorld(int entityCount)
    {
        var world = new DefaultWorld();
        var entities = CreateDefaultComplexQueryEntities(world, entityCount);
        var entitySet = BuildDefaultWithAllQuery(world);
        return new DefaultEntityQueryWorldState(world, entities, entitySet);
    }

    internal static EntitySet BuildDefaultWithAllQuery(DefaultWorld world)
    {
        return world.GetEntities()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .AsSet();
    }

    private static DefaultEntity[] CreateDefaultComplexQueryEntities(DefaultWorld world, int entityCount)
    {
        var archetypeCounts = GetComplexQueryArchetypeCounts(entityCount);
        var entities = new DefaultEntity[entityCount];
        var entityIndex = 0;

        for (var group = 0; group < archetypeCounts.Length; group++)
        {
            for (var i = 0; i < archetypeCounts[group]; i++)
            {
                entities[entityIndex] = CreateDefaultComplexEntity(world, group, entityIndex);
                entityIndex++;
            }
        }

        return entities;
    }

    private static DefaultEntity CreateDefaultComplexEntity(DefaultWorld world, int group, int entityIndex)
    {
        var entity = world.CreateEntity();
        entity.Set(new Position(entityIndex, entityIndex + 1));
        entity.Set(new Velocity(entityIndex + 2, entityIndex + 3));
        entity.Set(new Team(entityIndex % 4));
        entity.Set(new Health(100 + (entityIndex % 50)));
        entity.Set(new Acceleration(entityIndex + 4, entityIndex + 5));
        entity.Set(new Mana(entityIndex % 100));
        entity.Set(new Mass(1 + (entityIndex % 8)));
        entity.Set(new Rotation(entityIndex % 360));

        switch (group)
        {
            case 0:
                entity.Set(new AnyTagA(entityIndex));
                break;
            case 1:
                entity.Set(new ExcludedTag(entityIndex));
                break;
            case 2:
                entity.Set(new Shield(50 + (entityIndex % 10)));
                entity.Set(new AnyTagB(entityIndex));
                break;
            case 3:
                entity.Set(new Shield(80 + (entityIndex % 10)));
                entity.Set(new Damage(10 + (entityIndex % 5)));
                break;
            case 4:
                entity.Set(new Damage(20 + (entityIndex % 7)));
                entity.Set(new ExcludedTag(entityIndex));
                break;
        }

        return entity;
    }
}

public sealed class DefaultWorldState
{
    public DefaultWorldState(DefaultWorld world, DefaultEntity[] entities)
    {
        World = world;
        Entities = entities;
    }

    public DefaultWorld World;
    public DefaultEntity[] Entities;
}

public sealed class DefaultEntityQueryWorldState : IDisposable
{
    public DefaultEntityQueryWorldState(DefaultWorld world, DefaultEntity[] entities, EntitySet entitySet)
    {
        World = world;
        Entities = entities;
        EntitySet = entitySet;
    }

    public DefaultWorld World;
    public DefaultEntity[] Entities;
    public EntitySet EntitySet;

    public void Dispose()
    {
        World.Dispose();
    }
}
