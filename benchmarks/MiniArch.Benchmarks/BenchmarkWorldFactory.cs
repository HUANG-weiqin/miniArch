using Arch.Core;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

using MiniEntity = MiniArch.Core.Entity;
using MiniQuery = MiniArch.Core.Query;
using MiniWorld = MiniArch.Core.World;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;

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
        WithAllQuery = BenchmarkWorldFactory.BuildMiniWithAllQuery(world);
        WithAllWithoutQuery = BenchmarkWorldFactory.BuildMiniWithAllWithoutQuery(world);
        WithAllAnyQuery = BenchmarkWorldFactory.BuildMiniWithAllAnyQuery(world);

        _ = WithAllQuery.MatchedArchetypes;
        _ = WithAllWithoutQuery.MatchedArchetypes;
        _ = WithAllAnyQuery.MatchedArchetypes;
    }

    public MiniWorld World;
    public MiniEntity[] Entities;
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
    public QueryDescription WithAllDescription;
    public QueryDescription WithAllWithoutDescription;
    public QueryDescription WithAllAnyDescription;

    public void Dispose()
    {
        World.Dispose();
    }
}

public static partial class BenchmarkWorldFactory
{
    internal static MiniQuery BuildMiniWithAllQuery(MiniWorld world)
    {
        return world.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Build();
    }

    internal static MiniQuery BuildMiniWithAllWithoutQuery(MiniWorld world)
    {
        return world.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Without<ExcludedTag>()
            .Build();
    }

    internal static MiniQuery BuildMiniWithAllAnyQuery(MiniWorld world)
    {
        return world.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Any<AnyTagA>()
            .Or<AnyTagB>()
            .Build();
    }

    internal static QueryDescription BuildArchWithAllDescription()
    {
        return new QueryDescription()
            .WithAll<Position, Velocity, Health, Team>();
    }

    internal static QueryDescription BuildArchWithAllWithoutDescription()
    {
        return new QueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithNone<ExcludedTag>();
    }

    internal static QueryDescription BuildArchWithAllAnyDescription()
    {
        return new QueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithAny<AnyTagA, AnyTagB>();
    }
}
