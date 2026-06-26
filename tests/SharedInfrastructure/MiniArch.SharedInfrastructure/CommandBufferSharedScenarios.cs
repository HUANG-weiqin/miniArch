using MiniArch.Core;
using MiniEntity = MiniArch.Entity;
using MiniWorld = MiniArch.World;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using ArchCommandBuffer = Arch.Buffer.CommandBuffer;
using ArchComponentType = Arch.Core.ComponentType;
using ArchQueryDescription = Arch.Core.QueryDescription;

namespace MiniArchBenchmarks;

public sealed record CommandBufferSharedScenario(
    string Name,
    CommandBufferBenchmarkScenario Scenario,
    int EntityCount);

public sealed record CommandBufferWorldSummary(
    string ScenarioName,
    int LiveEntityCount,
    string Digest);

public static class CommandBufferSharedScenarios
{
    public static IEnumerable<CommandBufferSharedScenario> CreateParityScenarios()
    {
        foreach (var entityCount in new[] { 32, 128, 512 })
        {
            yield return new CommandBufferSharedScenario($"DenseExisting-{entityCount}", CommandBufferBenchmarkScenario.DenseExisting, entityCount);
            yield return new CommandBufferSharedScenario($"CreateHeavy-{entityCount}", CommandBufferBenchmarkScenario.CreateHeavy, entityCount);
            yield return new CommandBufferSharedScenario($"MixedScript-{entityCount}", CommandBufferBenchmarkScenario.MixedScript, entityCount);
        }
    }

    public static CommandBufferWorldSummary ExecuteMiniArchPlay(CommandBufferSharedScenario scenario)
    {
        var world = new MiniWorld();
        var existing = scenario.Scenario is CommandBufferBenchmarkScenario.CreateHeavy
            ? Array.Empty<MiniEntity>()
            : CreateMiniBaselineEntities(world, scenario.EntityCount);
        var buffer = new CommandStream(world);

        switch (scenario.Scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting:
                RecordMiniDenseExisting(buffer, existing);
                break;
            case CommandBufferBenchmarkScenario.CreateHeavy:
                RecordMiniCreateHeavy(buffer, scenario.EntityCount);
                break;
            case CommandBufferBenchmarkScenario.MixedScript:
                RecordMiniMixedScript(buffer, existing, scenario.EntityCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        buffer.Submit();
        return SummarizeMiniWorld(scenario.Name, world);
    }

    public static CommandBufferWorldSummary ExecuteArchPlayback(CommandBufferSharedScenario scenario)
    {
        using var world = ArchWorld.Create();
        var existing = scenario.Scenario is CommandBufferBenchmarkScenario.CreateHeavy
            ? Array.Empty<ArchEntity>()
            : CreateArchBaselineEntities(world, scenario.EntityCount);
        var buffer = new ArchCommandBuffer(Math.Max(16, scenario.EntityCount * 8));

        switch (scenario.Scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting:
                RecordArchDenseExisting(buffer, existing);
                break;
            case CommandBufferBenchmarkScenario.CreateHeavy:
                RecordArchCreateHeavy(buffer, scenario.EntityCount);
                break;
            case CommandBufferBenchmarkScenario.MixedScript:
                RecordArchMixedScript(buffer, existing, scenario.EntityCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        buffer.Playback(world, true);
        return SummarizeArchWorld(scenario.Name, world);
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

    private static void RecordMiniDenseExisting(CommandStream buffer, MiniEntity[] existing)
    {
        for (var i = 0; i < existing.Length; i++)
        {
            var entity = existing[i];
            buffer.Set(entity, new BenchmarkPosition(i + 10, i + 11));
            buffer.Set(entity, new BenchmarkVelocity(i + 12, i + 13));
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

    private static void RecordArchDenseExisting(ArchCommandBuffer buffer, ArchEntity[] existing)
    {
        for (var i = 0; i < existing.Length; i++)
        {
            var entity = existing[i];
            buffer.Set(entity, new BenchmarkPosition(i + 10, i + 11));
            buffer.Set(entity, new BenchmarkVelocity(i + 12, i + 13));
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

    private static void RecordMiniCreateHeavy(CommandStream buffer, int entityCount)
    {
        for (var i = 0; i < entityCount; i++)
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

    private static void RecordArchCreateHeavy(ArchCommandBuffer buffer, int entityCount)
    {
        for (var i = 0; i < entityCount; i++)
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
    }

    private static void RecordMiniMixedScript(CommandStream buffer, MiniEntity[] existing, int entityCount)
    {
        for (var i = 0; i < entityCount; i++)
        {
            if ((i & 1) == 0)
            {
                var entity = existing[i];
                buffer.Set(entity, new BenchmarkPosition(i + 10, i + 11));
                buffer.Set(entity, new BenchmarkVelocity(i + 12, i + 13));

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
                buffer.Add(entity, new BenchmarkPosition(i + 20, i + 21));
                buffer.Add(entity, new BenchmarkVelocity(i + 22, i + 23));
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

    private static void RecordArchMixedScript(ArchCommandBuffer buffer, ArchEntity[] existing, int entityCount)
    {
        for (var i = 0; i < entityCount; i++)
        {
            if ((i & 1) == 0)
            {
                var entity = existing[i];
                buffer.Set(entity, new BenchmarkPosition(i + 10, i + 11));
                buffer.Set(entity, new BenchmarkVelocity(i + 12, i + 13));

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
                buffer.Add(entity, new BenchmarkPosition(i + 20, i + 21));
                buffer.Add(entity, new BenchmarkVelocity(i + 22, i + 23));
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

    private static CommandBufferWorldSummary SummarizeMiniWorld(string scenarioName, MiniWorld world)
    {
        var seen = new HashSet<(int Id, int Version)>();
        var snapshots = new List<string>();
        var description = new QueryDescription();
        var query = MiniArch.Core.Query.Create(world, in description);
        var chunks = query.GetArchetypeSpan();

        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var entities = chunk.GetEntities();
            for (var row = 0; row < entities.Length; row++)
            {
                var entity = entities[row];
                if (!seen.Add((entity.Id, entity.Version)))
                {
                    continue;
                }

                if (!world.TryGetLocation(entity, out var location))
                {
                    continue;
                }

                snapshots.Add(DescribeMiniEntity(world, location));
            }
        }

        snapshots.Sort(StringComparer.Ordinal);
        return new CommandBufferWorldSummary(scenarioName, snapshots.Count, string.Join("\n", snapshots));
    }

    private static CommandBufferWorldSummary SummarizeArchWorld(string scenarioName, ArchWorld world)
    {
        var seen = new HashSet<(int Id, int Version)>();
        var snapshots = new List<string>();
        var query = world.Query(new ArchQueryDescription());

        foreach (var chunk in query)
        {
            for (var row = 0; row < chunk.Count; row++)
            {
                var entity = chunk.Entity(row);
                if (!seen.Add((entity.Id, entity.Version)))
                {
                    continue;
                }

                if (!world.IsAlive(entity))
                {
                    continue;
                }

                snapshots.Add(DescribeArchEntity(world, entity));
            }
        }

        snapshots.Sort(StringComparer.Ordinal);
        return new CommandBufferWorldSummary(scenarioName, snapshots.Count, string.Join("\n", snapshots));
    }

    private static string DescribeMiniEntity(MiniWorld world, EntityInfo location)
    {
        var arch = location.Archetype;
        var parts = new List<string>(4);

        if (TryGetComponent(world, location, arch, ComponentRegistry.Shared.GetOrCreate<BenchmarkPosition>(), out BenchmarkPosition position))
        {
            parts.Add($"Position({position.X},{position.Y})");
        }

        if (TryGetComponent(world, location, arch, ComponentRegistry.Shared.GetOrCreate<BenchmarkVelocity>(), out BenchmarkVelocity velocity))
        {
            parts.Add($"Velocity({velocity.X},{velocity.Y})");
        }

        if (TryGetComponent(world, location, arch, ComponentRegistry.Shared.GetOrCreate<BenchmarkHealth>(), out BenchmarkHealth health))
        {
            parts.Add($"Health({health.Value})");
        }

        if (TryGetComponent(world, location, arch, ComponentRegistry.Shared.GetOrCreate<BenchmarkArmor>(), out BenchmarkArmor armor))
        {
            parts.Add($"Armor({armor.Value})");
        }

        return string.Join("|", parts.Count == 0 ? ["empty"] : parts);
    }

    private static string DescribeArchEntity(ArchWorld world, ArchEntity entity)
    {
        var parts = new List<string>(4);

        if (world.Has<BenchmarkPosition>(entity))
        {
            var position = world.Get<BenchmarkPosition>(entity);
            parts.Add($"Position({position.X},{position.Y})");
        }

        if (world.Has<BenchmarkVelocity>(entity))
        {
            var velocity = world.Get<BenchmarkVelocity>(entity);
            parts.Add($"Velocity({velocity.X},{velocity.Y})");
        }

        if (world.Has<BenchmarkHealth>(entity))
        {
            var health = world.Get<BenchmarkHealth>(entity);
            parts.Add($"Health({health.Value})");
        }

        if (world.Has<BenchmarkArmor>(entity))
        {
            var armor = world.Get<BenchmarkArmor>(entity);
            parts.Add($"Armor({armor.Value})");
        }

        return string.Join("|", parts.Count == 0 ? ["empty"] : parts);
    }

    private static bool TryGetComponent<T>(MiniWorld world, EntityInfo location, Archetype archetype, ComponentType componentType, out T component) where T : unmanaged
    {
        if (!location.Archetype.Signature.Contains(componentType))
        {
            component = default!;
            return false;
        }

        var columnIndex = archetype.GetComponentIndex(componentType);
        component = archetype.GetComponentAt<T>(columnIndex, location.RowIndex);
        return true;
    }
}
