using Arch.Core;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

using MiniEntity = MiniArch.Core.Entity;
using MiniWorld = MiniArch.Core.World;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;

public static class BenchmarkWorldFactory
{
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
