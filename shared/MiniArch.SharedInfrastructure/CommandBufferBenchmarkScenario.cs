using Arch.Core;
using MiniArch.Core;
using DefaultEcs;

namespace MiniArchBenchmarks;

using ArchComponentType = Arch.Core.ComponentType;
using ArchCommandBuffer = Arch.Buffer.CommandBuffer;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using MiniEntity = MiniArch.Entity;
using MiniWorld = MiniArch.World;
using DefaultEntity = DefaultEcs.Entity;
using DefaultWorld = DefaultEcs.World;

public enum CommandBufferBenchmarkScenario
{
    DenseExisting,
    CreateHeavy,
    MixedScript,
}

public static class CommandBufferBenchmarkScenarioFactory
{
    public static MiniSharedCommandBufferState CreateMiniSharedState(CommandBufferBenchmarkScenario scenario, int entityCount)
    {
        var world = new MiniWorld();
        var existingEntities = scenario is CommandBufferBenchmarkScenario.CreateHeavy
            ? Array.Empty<MiniEntity>()
            : CreateMiniBaselineEntities(world, entityCount);

        return new MiniSharedCommandBufferState(world, existingEntities, entityCount, Math.Max(16, entityCount * 8));
    }

    public static ArchSharedCommandBufferState CreateArchSharedState(CommandBufferBenchmarkScenario scenario, int entityCount)
    {
        var world = ArchWorld.Create();
        var existingEntities = scenario is CommandBufferBenchmarkScenario.CreateHeavy
            ? Array.Empty<ArchEntity>()
            : CreateArchBaselineEntities(world, entityCount);

        return new ArchSharedCommandBufferState(world, existingEntities, entityCount, Math.Max(16, entityCount * 8));
    }

    public static MiniHierarchyCommandBufferState CreateMiniHierarchyState(int entityCount)
    {
        var world = new MiniWorld();
        var parent = world.Create();
        var children = new MiniEntity[entityCount];

        for (var i = 0; i < entityCount; i++)
        {
            var child = world.Create(new BenchmarkPosition(i, i + 1), new BenchmarkVelocity(i + 2, i + 3), new BenchmarkHealth(100 + i));
            world.Link(parent, child);
            children[i] = child;
        }

        return new MiniHierarchyCommandBufferState(world, parent, children, Math.Max(16, entityCount * 8));
    }

    public static void RunMiniSharedScenario(MiniSharedCommandBufferState state, CommandBufferBenchmarkScenario scenario)
    {
        switch (scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting:
                RunMiniDenseExisting(state);
                return;
            case CommandBufferBenchmarkScenario.CreateHeavy:
                RunMiniCreateHeavy(state);
                return;
            case CommandBufferBenchmarkScenario.MixedScript:
                RunMiniMixedScript(state);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    public static void RecordMiniSharedScenario(CommandBuffer buffer, MiniSharedCommandBufferState state, CommandBufferBenchmarkScenario scenario)
    {
        switch (scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting:
                RecordMiniDenseExisting(buffer, state);
                return;
            case CommandBufferBenchmarkScenario.CreateHeavy:
                RecordMiniCreateHeavy(buffer, state);
                return;
            case CommandBufferBenchmarkScenario.MixedScript:
                RecordMiniMixedScript(buffer, state);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    public static void RunArchSharedScenario(ArchSharedCommandBufferState state, CommandBufferBenchmarkScenario scenario)
    {
        switch (scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting:
                RunArchDenseExisting(state);
                return;
            case CommandBufferBenchmarkScenario.CreateHeavy:
                RunArchCreateHeavy(state);
                return;
            case CommandBufferBenchmarkScenario.MixedScript:
                RunArchMixedScript(state);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    public static void RunMiniHierarchyScenario(MiniHierarchyCommandBufferState state)
    {
        var buffer = new CommandBuffer(state.World);
        var transientRoot = buffer.Create();

        for (var i = 0; i < state.Children.Length; i++)
        {
            var child = state.Children[i];
            buffer.Set(child, new BenchmarkPosition(i + 10, i + 11));
            buffer.Set(child, new BenchmarkVelocity(i + 12, i + 13));

            if ((i & 1) == 0)
            {
                buffer.Link(transientRoot, child);
            }
            else
            {
                buffer.Unlink(child);
            }

            if ((i & 3) == 0)
            {
                buffer.Add(child, new BenchmarkArmor(300 + i));
            }

            if ((i & 7) == 0)
            {
                buffer.Destroy(child);
            }
        }

        buffer.Submit();
    }

    private static void RunMiniDenseExisting(MiniSharedCommandBufferState state)
    {
        var buffer = new CommandBuffer(state.World);
        RecordMiniDenseExisting(buffer, state);
        buffer.Submit();
    }

    private static void RunMiniCreateHeavy(MiniSharedCommandBufferState state)
    {
        var buffer = new CommandBuffer(state.World);
        for (var i = 0; i < state.EntityCount; i++)
        {
            var entity = buffer.Create();
            buffer.Add(entity, new BenchmarkPosition(i + 1, i + 2));
            buffer.Add(entity, new BenchmarkVelocity(i + 3, i + 4));
            buffer.Add(entity, new BenchmarkHealth(200 + i));

            if ((i & 1) == 0)
            {
                buffer.Remove<BenchmarkVelocity>(entity);
            }

            if ((i & 3) == 0)
            {
                buffer.Destroy(entity);
            }
        }

        buffer.Submit();
    }

    private static void RunMiniMixedScript(MiniSharedCommandBufferState state)
    {
        var buffer = new CommandBuffer(state.World);
        RecordMiniMixedScript(buffer, state);
        buffer.Submit();
    }

    private static void RecordMiniDenseExisting(CommandBuffer buffer, MiniSharedCommandBufferState state)
    {
        for (var i = 0; i < state.ExistingEntities.Length; i++)
        {
            var entity = state.ExistingEntities[i];
            buffer.Set(entity, new BenchmarkPosition(i + 1, i + 2));
            buffer.Set(entity, new BenchmarkVelocity(i + 3, i + 4));
            buffer.Set(entity, new BenchmarkHealth(200 + i));

            if ((i & 1) == 0)
            {
                buffer.Remove<BenchmarkHealth>(entity);
            }
            else
            {
                buffer.Add(entity, new BenchmarkArmor(300 + i));
            }

            if ((i & 7) == 0)
            {
                buffer.Destroy(entity);
            }
        }

    }

    private static void RecordMiniCreateHeavy(CommandBuffer buffer, MiniSharedCommandBufferState state)
    {
        for (var i = 0; i < state.EntityCount; i++)
        {
            var entity = buffer.Create();
            buffer.Add(entity, new BenchmarkPosition(i + 1, i + 2));
            buffer.Add(entity, new BenchmarkVelocity(i + 3, i + 4));
            buffer.Add(entity, new BenchmarkHealth(200 + i));

            if ((i & 1) == 0)
            {
                buffer.Remove<BenchmarkVelocity>(entity);
            }

            if ((i & 3) == 0)
            {
                buffer.Destroy(entity);
            }
        }
    }

    private static void RecordMiniMixedScript(CommandBuffer buffer, MiniSharedCommandBufferState state)
    {
        for (var i = 0; i < state.EntityCount; i++)
        {
            if ((i & 1) == 0)
            {
                var entity = state.ExistingEntities[i];
                buffer.Set(entity, new BenchmarkPosition(i + 1, i + 2));
                buffer.Set(entity, new BenchmarkVelocity(i + 3, i + 4));

                if ((i & 3) == 0)
                {
                    buffer.Remove<BenchmarkHealth>(entity);
                }
                else
                {
                    buffer.Set(entity, new BenchmarkHealth(300 + i));
                }

                if ((i & 7) == 0)
                {
                    buffer.Destroy(entity);
                }
            }
            else
            {
                var entity = buffer.Create();
                buffer.Add(entity, new BenchmarkPosition(i + 11, i + 12));
                buffer.Add(entity, new BenchmarkVelocity(i + 13, i + 14));
                buffer.Add(entity, new BenchmarkHealth(400 + i));

                if ((i & 3) == 1)
                {
                    buffer.Remove<BenchmarkVelocity>(entity);
                }

                if ((i & 7) == 1)
                {
                    buffer.Destroy(entity);
                }
            }
        }
    }

    private static void RunArchDenseExisting(ArchSharedCommandBufferState state)
    {
        var buffer = new ArchCommandBuffer(state.Capacity);
        for (var i = 0; i < state.ExistingEntities.Length; i++)
        {
            var entity = state.ExistingEntities[i];
            buffer.Set(entity, new BenchmarkPosition(i + 1, i + 2));
            buffer.Set(entity, new BenchmarkVelocity(i + 3, i + 4));
            buffer.Set(entity, new BenchmarkHealth(200 + i));

            if ((i & 1) == 0)
            {
                buffer.Remove<BenchmarkHealth>(entity);
            }
            else
            {
                buffer.Add(entity, new BenchmarkArmor(300 + i));
            }

            if ((i & 7) == 0)
            {
                buffer.Destroy(entity);
            }
        }

        buffer.Playback(state.World, true);
    }

    private static void RunArchCreateHeavy(ArchSharedCommandBufferState state)
    {
        var buffer = new ArchCommandBuffer(state.Capacity);
        for (var i = 0; i < state.EntityCount; i++)
        {
            var entity = buffer.Create(Array.Empty<ArchComponentType>());
            buffer.Add(entity, new BenchmarkPosition(i + 1, i + 2));
            buffer.Add(entity, new BenchmarkVelocity(i + 3, i + 4));
            buffer.Add(entity, new BenchmarkHealth(200 + i));

            if ((i & 1) == 0)
            {
                buffer.Remove<BenchmarkVelocity>(entity);
            }

            if ((i & 3) == 0)
            {
                buffer.Destroy(entity);
            }
        }

        buffer.Playback(state.World, true);
    }

    private static void RunArchMixedScript(ArchSharedCommandBufferState state)
    {
        var buffer = new ArchCommandBuffer(state.Capacity);
        for (var i = 0; i < state.EntityCount; i++)
        {
            if ((i & 1) == 0)
            {
                var entity = state.ExistingEntities[i];
                buffer.Set(entity, new BenchmarkPosition(i + 1, i + 2));
                buffer.Set(entity, new BenchmarkVelocity(i + 3, i + 4));

                if ((i & 3) == 0)
                {
                    buffer.Remove<BenchmarkHealth>(entity);
                }
                else
                {
                    buffer.Set(entity, new BenchmarkHealth(300 + i));
                }

                if ((i & 7) == 0)
                {
                    buffer.Destroy(entity);
                }
            }
            else
            {
                var entity = buffer.Create(Array.Empty<ArchComponentType>());
                buffer.Add(entity, new BenchmarkPosition(i + 11, i + 12));
                buffer.Add(entity, new BenchmarkVelocity(i + 13, i + 14));
                buffer.Add(entity, new BenchmarkHealth(400 + i));

                if ((i & 3) == 1)
                {
                    buffer.Remove<BenchmarkVelocity>(entity);
                }

                if ((i & 7) == 1)
                {
                    buffer.Destroy(entity);
                }
            }
        }

        buffer.Playback(state.World, true);
    }

    private static MiniEntity[] CreateMiniBaselineEntities(MiniWorld world, int entityCount)
    {
        var entities = new MiniEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = world.Create(new BenchmarkPosition(i, i + 1), new BenchmarkVelocity(i + 2, i + 3), new BenchmarkHealth(100 + i));
        }

        return entities;
    }

    private static ArchEntity[] CreateArchBaselineEntities(ArchWorld world, int entityCount)
    {
        var entities = new ArchEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            entities[i] = world.Create(new BenchmarkPosition(i, i + 1), new BenchmarkVelocity(i + 2, i + 3), new BenchmarkHealth(100 + i));
        }

        return entities;
    }

    public static DefaultSharedCommandBufferState CreateDefaultSharedState(CommandBufferBenchmarkScenario scenario, int entityCount)
    {
        var world = new DefaultWorld();
        var existingEntities = scenario is CommandBufferBenchmarkScenario.CreateHeavy
            ? Array.Empty<DefaultEntity>()
            : CreateDefaultBaselineEntities(world, entityCount);
        return new DefaultSharedCommandBufferState(world, existingEntities, entityCount);
    }

    public static void RunDefaultSharedScenario(DefaultSharedCommandBufferState state, CommandBufferBenchmarkScenario scenario)
    {
        switch (scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting:
                RunDefaultDenseExisting(state);
                return;
            case CommandBufferBenchmarkScenario.CreateHeavy:
                RunDefaultCreateHeavy(state);
                return;
            case CommandBufferBenchmarkScenario.MixedScript:
                RunDefaultMixedScript(state);
                return;
        }
    }

    private static DefaultEntity[] CreateDefaultBaselineEntities(DefaultWorld world, int entityCount)
    {
        var entities = new DefaultEntity[entityCount];
        for (var i = 0; i < entityCount; i++)
        {
            var entity = world.CreateEntity();
            entity.Set(new BenchmarkPosition(i, i + 1));
            entity.Set(new BenchmarkVelocity(i + 2, i + 3));
            entity.Set(new BenchmarkHealth(100 + i));
            entities[i] = entity;
        }

        return entities;
    }

    private static void RunDefaultDenseExisting(DefaultSharedCommandBufferState state)
    {
        for (var i = 0; i < state.ExistingEntities.Length; i++)
        {
            var entity = state.ExistingEntities[i];
            entity.Set(new BenchmarkPosition(i + 1, i + 2));
            entity.Set(new BenchmarkVelocity(i + 3, i + 4));
            entity.Set(new BenchmarkHealth(200 + i));

            if ((i & 1) == 0)
                entity.Remove<BenchmarkHealth>();
            else
                entity.Set(new BenchmarkArmor(300 + i));

            if ((i & 7) == 0)
                entity.Dispose();
        }
    }

    private static void RunDefaultCreateHeavy(DefaultSharedCommandBufferState state)
    {
        for (var i = 0; i < state.EntityCount; i++)
        {
            var entity = state.World.CreateEntity();
            entity.Set(new BenchmarkPosition(i + 1, i + 2));
            entity.Set(new BenchmarkVelocity(i + 3, i + 4));
            entity.Set(new BenchmarkHealth(200 + i));

            if ((i & 1) == 0)
                entity.Remove<BenchmarkVelocity>();

            if ((i & 3) == 0)
                entity.Dispose();
        }
    }

    private static void RunDefaultMixedScript(DefaultSharedCommandBufferState state)
    {
        for (var i = 0; i < state.EntityCount; i++)
        {
            if ((i & 1) == 0)
            {
                var entity = state.ExistingEntities[i];
                entity.Set(new BenchmarkPosition(i + 1, i + 2));
                entity.Set(new BenchmarkVelocity(i + 3, i + 4));

                if ((i & 3) == 0)
                    entity.Remove<BenchmarkHealth>();
                else
                    entity.Set(new BenchmarkHealth(300 + i));

                if ((i & 7) == 0)
                    entity.Dispose();
            }
            else
            {
                var entity = state.World.CreateEntity();
                entity.Set(new BenchmarkPosition(i + 11, i + 12));
                entity.Set(new BenchmarkVelocity(i + 13, i + 14));
                entity.Set(new BenchmarkHealth(400 + i));

                if ((i & 3) == 1)
                    entity.Remove<BenchmarkVelocity>();

                if ((i & 7) == 1)
                    entity.Dispose();
            }
        }
    }
}

public sealed class MiniSharedCommandBufferState
{
    public MiniSharedCommandBufferState(MiniWorld world, MiniEntity[] existingEntities, int entityCount, int capacity)
    {
        World = world;
        ExistingEntities = existingEntities;
        Capacity = capacity;
        EntityCount = entityCount;
    }

    public MiniWorld World { get; }

    public MiniEntity[] ExistingEntities { get; }

    public int Capacity { get; }

    public int EntityCount { get; }
}

public sealed class ArchSharedCommandBufferState : IDisposable
{
    public ArchSharedCommandBufferState(ArchWorld world, ArchEntity[] existingEntities, int entityCount, int capacity)
    {
        World = world;
        ExistingEntities = existingEntities;
        Capacity = capacity;
        EntityCount = entityCount;
    }

    public ArchWorld World { get; }

    public ArchEntity[] ExistingEntities { get; }

    public int Capacity { get; }

    public int EntityCount { get; }

    public void Dispose()
    {
        World.Dispose();
    }
}

public sealed class MiniHierarchyCommandBufferState
{
    public MiniHierarchyCommandBufferState(MiniWorld world, MiniEntity parent, MiniEntity[] children, int capacity)
    {
        World = world;
        Parent = parent;
        Children = children;
        Capacity = capacity;
    }

    public MiniWorld World { get; }

    public MiniEntity Parent { get; }

    public MiniEntity[] Children { get; }

    public int Capacity { get; }
}

public sealed class DefaultSharedCommandBufferState : IDisposable
{
    public DefaultSharedCommandBufferState(DefaultWorld world, DefaultEntity[] existingEntities, int entityCount)
    {
        World = world;
        ExistingEntities = existingEntities;
        EntityCount = entityCount;
    }

    public DefaultWorld World { get; }

    public DefaultEntity[] ExistingEntities { get; }

    public int EntityCount { get; }

    public void Dispose()
    {
        World.Dispose();
    }
}

public readonly record struct BenchmarkPosition(int X, int Y);
public readonly record struct BenchmarkVelocity(int X, int Y);
public readonly record struct BenchmarkHealth(int Value);
public readonly record struct BenchmarkArmor(int Value);
