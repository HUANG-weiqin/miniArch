using MiniArch.Core;

namespace MiniArchBenchmarks;

public enum CommandBufferReplayScenarioKind
{
    ExistingHeavy,
    MixedHeavy,
}

public sealed record CommandBufferReplayScenarioDefinition(
    string Name,
    CommandBufferReplayScenarioKind Kind,
    int EntityCount);

public sealed class CommandBufferReplayPlaybackState : IDisposable
{
    internal CommandBufferReplayPlaybackState(CommandBufferReplayScenarioDefinition scenario, World world, CommandBuffer buffer)
    {
        Scenario = scenario;
        World = world;
        Buffer = buffer;
    }

    public CommandBufferReplayScenarioDefinition Scenario { get; }

    public World World { get; }

    public CommandBuffer Buffer { get; }

    public void Dispose()
    {
    }
}

public sealed class CommandBufferReplayExecution : IDisposable
{
    private readonly byte[] _baselineSnapshot;
    private ReverseFrameCommands _reverse;
    private bool _hasReverse;

    internal CommandBufferReplayExecution(
        CommandBufferReplayScenarioDefinition scenario,
        World world,
        FrameCommands frame,
        byte[] baselineSnapshot,
        CommandBufferWorldSummary baselineSummary,
        CommandBufferWorldSummary replayedSummary)
    {
        Scenario = scenario;
        CurrentWorld = world;
        Frame = frame;
        _baselineSnapshot = baselineSnapshot;
        BaselineSummary = baselineSummary;
        ReplayedSummary = replayedSummary;
    }

    public CommandBufferReplayScenarioDefinition Scenario { get; }

    public FrameCommands Frame { get; }

    public CommandBufferWorldSummary BaselineSummary { get; }

    public CommandBufferWorldSummary ReplayedSummary { get; }

    public World CurrentWorld { get; private set; }

    public ReverseFrameCommands Reverse => _hasReverse
        ? _reverse
        : throw new InvalidOperationException("ReplayWithReverse must be called before accessing Reverse.");

    public void StoreReverse(ReverseFrameCommands reverse)
    {
        _reverse = reverse;
        _hasReverse = true;
    }

    public void ClearReverse()
    {
        _hasReverse = false;
    }

    public void ReplayWithReverse()
    {
        var frame = Frame;
        StoreReverse(CurrentWorld.ReplayWithReverse(in frame));
    }

    public void Rewind()
    {
        if (!_hasReverse)
        {
            throw new InvalidOperationException("ReplayWithReverse must be called before Rewind.");
        }

        CurrentWorld.Rewind(in _reverse);
        ClearReverse();
    }

    public void ResetToBaseline()
    {
        _hasReverse = false;
        CurrentWorld = LoadSnapshot(_baselineSnapshot);
    }

    public CommandBufferWorldSummary SummarizeCurrentWorld()
    {
        return CommandBufferReplayScenarios.SummarizeWorld(Scenario.Name, CurrentWorld);
    }

    public void Dispose()
    {
    }

    private static World LoadSnapshot(byte[] snapshot)
    {
        using var stream = new MemoryStream(snapshot, writable: false);
        return WorldSnapshot.Load(stream);
    }
}

public static class CommandBufferReplayScenarios
{
    public static IEnumerable<CommandBufferReplayScenarioDefinition> CreateBenchmarkedScenarios(int entityCount)
    {
        yield return new CommandBufferReplayScenarioDefinition("existing-heavy", CommandBufferReplayScenarioKind.ExistingHeavy, entityCount);
        yield return new CommandBufferReplayScenarioDefinition("mixed-heavy", CommandBufferReplayScenarioKind.MixedHeavy, entityCount);
    }

    public static CommandBufferReplayPlaybackState PreparePlayback(CommandBufferReplayScenarioDefinition scenario)
    {
        var world = CreateBaselineWorld(scenario, out var existingEntities);
        var buffer = new CommandBuffer(world);
        RecordScenario(buffer, existingEntities, scenario);
        return new CommandBufferReplayPlaybackState(scenario, world, buffer);
    }

    public static CommandBufferReplayExecution PrepareReplay(CommandBufferReplayScenarioDefinition scenario)
    {
        using var playback = PreparePlayback(scenario);

        var frame = playback.Buffer.Playback();
        var baselineSnapshot = SaveSnapshot(playback.World);
        var baselineSummary = SummarizeWorld(scenario.Name, playback.World);
        var replayedSummary = SummarizeReplay(scenario.Name, baselineSnapshot, frame);
        var world = LoadSnapshot(baselineSnapshot);

        return new CommandBufferReplayExecution(scenario, world, frame, baselineSnapshot, baselineSummary, replayedSummary);
    }

    public static CommandBufferWorldSummary SummarizeWorld(string scenarioName, World world)
    {
        var snapshots = new List<string>();
        var description = new QueryDescription();
        var query = MiniArch.Core.Query.Create(world, in description);
        var chunks = query.GetChunkSpan();

        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var entities = chunk.GetEntities();
            for (var rowIndex = 0; rowIndex < entities.Length; rowIndex++)
            {
                var entity = entities[rowIndex];
                if (!world.TryGetLocation(entity, out var location))
                {
                    continue;
                }

                snapshots.Add(DescribeEntity(world, entity, location));
            }
        }

        snapshots.Sort(StringComparer.Ordinal);
        return new CommandBufferWorldSummary(scenarioName, snapshots.Count, string.Join("\n", snapshots));
    }

    private static World CreateBaselineWorld(CommandBufferReplayScenarioDefinition scenario, out Entity[] existingEntities)
    {
        var world = new World();
        existingEntities = new Entity[scenario.EntityCount];

        for (var index = 0; index < scenario.EntityCount; index++)
        {
            var entity = world.Create(
                new BenchmarkPosition(index, index + 1),
                new BenchmarkVelocity(index + 2, index + 3),
                new BenchmarkHealth(100 + index));

            if ((index & 3) == 0)
            {
                world.Add(entity, new BenchmarkArmor(200 + index));
            }

            existingEntities[index] = entity;
        }

        return world;
    }

    private static void RecordScenario(CommandBuffer buffer, Entity[] existingEntities, CommandBufferReplayScenarioDefinition scenario)
    {
        switch (scenario.Kind)
        {
            case CommandBufferReplayScenarioKind.ExistingHeavy:
                RecordExistingHeavy(buffer, existingEntities);
                return;
            case CommandBufferReplayScenarioKind.MixedHeavy:
                RecordMixedHeavy(buffer, existingEntities, scenario.EntityCount);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private static void RecordExistingHeavy(CommandBuffer buffer, Entity[] existingEntities)
    {
        for (var index = 0; index < existingEntities.Length; index++)
        {
            var entity = existingEntities[index];
            buffer.Set(entity, new BenchmarkPosition(index + 10, index + 11));
            buffer.Set(entity, new BenchmarkVelocity(index + 12, index + 13));
            buffer.Set(entity, new BenchmarkHealth(300 + index));

            if ((index & 1) == 0)
            {
                buffer.Remove<BenchmarkArmor>(entity);
            }
            else
            {
                buffer.Add(entity, new BenchmarkArmor(400 + index));
            }

            if ((index & 7) == 0)
            {
                buffer.Destroy(entity);
            }
        }
    }

    private static void RecordMixedHeavy(CommandBuffer buffer, Entity[] existingEntities, int entityCount)
    {
        for (var index = 0; index < entityCount; index++)
        {
            var existing = existingEntities[index];
            buffer.Set(existing, new BenchmarkPosition(index + 20, index + 21));

            if ((index & 1) == 0)
            {
                buffer.Remove<BenchmarkHealth>(existing);
                buffer.Add(existing, new BenchmarkArmor(500 + index));
            }
            else
            {
                buffer.Set(existing, new BenchmarkHealth(600 + index));
            }

            if ((index & 5) == 0)
            {
                buffer.Destroy(existing);
            }

            var created = buffer.Create();
            buffer.Add(created, new BenchmarkPosition(index + 100, index + 101));
            buffer.Add(created, new BenchmarkVelocity(index + 102, index + 103));

            if ((index & 1) == 0)
            {
                buffer.Add(created, new BenchmarkHealth(700 + index));
            }
            else
            {
                buffer.Set(created, new BenchmarkArmor(800 + index));
            }

            if ((index & 3) == 0)
            {
                buffer.Remove<BenchmarkVelocity>(created);
            }

            if ((index & 7) == 0)
            {
                buffer.Destroy(created);
            }

            var transient = buffer.Create();
            buffer.Add(transient, new BenchmarkPosition(index + 200, index + 201));
            buffer.Destroy(transient);
        }
    }

    private static CommandBufferWorldSummary SummarizeReplay(string scenarioName, byte[] baselineSnapshot, FrameCommands frame)
    {
        using var stream = new MemoryStream(baselineSnapshot, writable: false);
        var world = WorldSnapshot.Load(stream);
        world.Replay(in frame);
        return SummarizeWorld(scenarioName, world);
    }

    private static byte[] SaveSnapshot(World world)
    {
        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        return stream.ToArray();
    }

    private static World LoadSnapshot(byte[] snapshot)
    {
        using var stream = new MemoryStream(snapshot, writable: false);
        return WorldSnapshot.Load(stream);
    }

    private static string DescribeEntity(World world, Entity entity, EntityInfo location)
    {
        var chunk = location.Archetype.GetChunk(location.ChunkIndex);
        var parts = new List<string>(6)
        {
            $"entity({entity.Id},{entity.Version})"
        };

        if (world.TryGetParent(entity, out var parent))
        {
            parts.Add($"parent({parent.Id},{parent.Version})");
        }

        if (TryGetComponent(world, location, chunk, world.Components.GetOrCreate<BenchmarkPosition>(), out BenchmarkPosition position))
        {
            parts.Add($"Position({position.X},{position.Y})");
        }

        if (TryGetComponent(world, location, chunk, world.Components.GetOrCreate<BenchmarkVelocity>(), out BenchmarkVelocity velocity))
        {
            parts.Add($"Velocity({velocity.X},{velocity.Y})");
        }

        if (TryGetComponent(world, location, chunk, world.Components.GetOrCreate<BenchmarkHealth>(), out BenchmarkHealth health))
        {
            parts.Add($"Health({health.Value})");
        }

        if (TryGetComponent(world, location, chunk, world.Components.GetOrCreate<BenchmarkArmor>(), out BenchmarkArmor armor))
        {
            parts.Add($"Armor({armor.Value})");
        }

        return string.Join("|", parts);
    }

    private static bool TryGetComponent<T>(World world, EntityInfo location, Chunk chunk, ComponentType componentType, out T component)
    {
        if (!location.Archetype.Signature.Contains(componentType))
        {
            component = default!;
            return false;
        }

        component = chunk.GetComponent<T>(componentType, location.RowIndex);
        return true;
    }
}
