using Arch.Core;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

using MiniEntity = MiniArch.Core.Entity;
using MiniWorld = MiniArch.Core.World;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;

public static class BenchmarkWorldFactory
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
                var entity = world.Create();
                entities[entityIndex] = entity;
                AddMiniComplexEntity(world, entity, group, entityIndex++);
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
                var entity = world.Create();
                entities[entityIndex] = entity;
                AddArchComplexEntity(world, entity, group, entityIndex++);
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

    private static void AddMiniComplexEntity(MiniWorld world, MiniEntity entity, int group, int entityIndex)
    {
        AddMiniSharedPrefix(world, entity, entityIndex);

        switch (group)
        {
            case 0:
                world.Add(entity, new AnyTagA(entityIndex));
                break;
            case 1:
                world.Add(entity, new ExcludedTag(entityIndex));
                break;
            case 2:
                world.Add(entity, new Shield(50 + (entityIndex % 10)));
                world.Add(entity, new AnyTagB(entityIndex));
                break;
            case 3:
                world.Add(entity, new Shield(80 + (entityIndex % 10)));
                world.Add(entity, new Damage(10 + (entityIndex % 5)));
                break;
            case 4:
                world.Add(entity, new Damage(20 + (entityIndex % 7)));
                world.Add(entity, new ExcludedTag(entityIndex));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }

        AddMiniRequiredComponents(world, entity, entityIndex, includeHealth: group != 4);
    }

    private static void AddArchComplexEntity(ArchWorld world, ArchEntity entity, int group, int entityIndex)
    {
        AddArchSharedPrefix(world, entity, entityIndex);

        switch (group)
        {
            case 0:
                world.Add(entity, new AnyTagA(entityIndex));
                break;
            case 1:
                world.Add(entity, new ExcludedTag(entityIndex));
                break;
            case 2:
                world.Add(entity, new Shield(50 + (entityIndex % 10)));
                world.Add(entity, new AnyTagB(entityIndex));
                break;
            case 3:
                world.Add(entity, new Shield(80 + (entityIndex % 10)));
                world.Add(entity, new Damage(10 + (entityIndex % 5)));
                break;
            case 4:
                world.Add(entity, new Damage(20 + (entityIndex % 7)));
                world.Add(entity, new ExcludedTag(entityIndex));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }

        AddArchRequiredComponents(world, entity, entityIndex, includeHealth: group != 4);
    }

    private static void AddMiniSharedPrefix(MiniWorld world, MiniEntity entity, int entityIndex)
    {
        world.Add(entity, new Acceleration(entityIndex + 4, entityIndex + 5));
        world.Add(entity, new Mana(entityIndex % 100));
        world.Add(entity, new Mass(1 + (entityIndex % 8)));
        world.Add(entity, new Rotation(entityIndex % 360));
    }

    private static void AddMiniRequiredComponents(MiniWorld world, MiniEntity entity, int entityIndex, bool includeHealth)
    {
        world.Add(entity, new Position(entityIndex, entityIndex + 1));
        world.Add(entity, new Velocity(entityIndex + 2, entityIndex + 3));
        world.Add(entity, new Team(entityIndex % 4));

        if (includeHealth)
        {
            world.Add(entity, new Health(100 + (entityIndex % 50)));
        }
    }

    private static void AddArchSharedPrefix(ArchWorld world, ArchEntity entity, int entityIndex)
    {
        world.Add(entity, new Acceleration(entityIndex + 4, entityIndex + 5));
        world.Add(entity, new Mana(entityIndex % 100));
        world.Add(entity, new Mass(1 + (entityIndex % 8)));
        world.Add(entity, new Rotation(entityIndex % 360));
    }

    private static void AddArchRequiredComponents(ArchWorld world, ArchEntity entity, int entityIndex, bool includeHealth)
    {
        world.Add(entity, new Position(entityIndex, entityIndex + 1));
        world.Add(entity, new Velocity(entityIndex + 2, entityIndex + 3));
        world.Add(entity, new Team(entityIndex % 4));

        if (includeHealth)
        {
            world.Add(entity, new Health(100 + (entityIndex % 50)));
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
    }

    public MiniWorld World;
    public MiniEntity[] Entities;
}

public sealed class ArchComplexQueryWorldState : IDisposable
{
    public ArchComplexQueryWorldState(ArchWorld world, ArchEntity[] entities)
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
