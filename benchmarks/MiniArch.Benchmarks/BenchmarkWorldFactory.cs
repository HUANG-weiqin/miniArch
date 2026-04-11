using Arch.Core;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

using MiniEntity = MiniArch.Core.Entity;
using MiniWorld = MiniArch.Core.World;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;

public static class BenchmarkWorldFactory
{
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
            state.World.Add(entity, new Damage(i + 4, i + 5));
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
