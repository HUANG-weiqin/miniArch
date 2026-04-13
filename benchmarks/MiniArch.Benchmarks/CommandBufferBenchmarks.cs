using Arch.Core;
using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using ArchComponentType = Arch.Core.ComponentType;
using ArchCommandBuffer = Arch.Buffer.CommandBuffer;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using MiniCommandBuffer = MiniArch.Core.CommandBuffer;
using MiniEntity = MiniArch.Entity;
using MiniWorld = MiniArch.World;

public enum CommandBufferBenchmarkScenario
{
    DenseExisting,
    CreateHeavy,
    MixedScript,
}

public class CommandBufferBenchmarks
{
    [Params(128, 1000, 10000)]
    public int EntityCount { get; set; }

    [Params(
        CommandBufferBenchmarkScenario.DenseExisting,
        CommandBufferBenchmarkScenario.CreateHeavy,
        CommandBufferBenchmarkScenario.MixedScript)]
    public CommandBufferBenchmarkScenario Scenario { get; set; }

    private MiniSharedCommandBufferState _miniState = null!;
    private ArchSharedCommandBufferState _archState = null!;

    [IterationSetup(Target = nameof(MiniArch_CommandBuffer_RecordPlay))]
    public void SetupMiniIteration()
    {
        _miniState = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(Scenario, EntityCount);
    }

    [IterationSetup(Target = nameof(Arch_CommandBuffer_RecordPlay))]
    public void SetupArchIteration()
    {
        _archState = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(Scenario, EntityCount);
    }

    [IterationCleanup(Target = nameof(Arch_CommandBuffer_RecordPlay))]
    public void CleanupArchIteration()
    {
        _archState.Dispose();
    }

    [Benchmark(Description = "MiniArch command buffer record+play")]
    public void MiniArch_CommandBuffer_RecordPlay()
    {
        CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(_miniState, Scenario);
    }

    [Benchmark(Description = "Arch command buffer record+play")]
    public void Arch_CommandBuffer_RecordPlay()
    {
        CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(_archState, Scenario);
    }
}

public class CommandBufferHierarchyBenchmarks
{
    [Params(128, 1000, 10000)]
    public int EntityCount { get; set; }

    private MiniHierarchyCommandBufferState _state = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _state = CommandBufferBenchmarkScenarioFactory.CreateMiniHierarchyState(EntityCount);
    }

    [Benchmark(Description = "MiniArch command buffer hierarchy record+play")]
    public void MiniArch_CommandBuffer_Hierarchy_RecordPlay()
    {
        CommandBufferBenchmarkScenarioFactory.RunMiniHierarchyScenario(_state);
    }
}

public class CommandBufferReplayRewindBenchmarks
{
    [Params(128, 1000, 10000)]
    public int EntityCount { get; set; }

    [Params(
        CommandBufferReplayScenarioKind.ExistingHeavy,
        CommandBufferReplayScenarioKind.MixedHeavy)]
    public CommandBufferReplayScenarioKind Scenario { get; set; }

    private CommandBufferReplayPlaybackState _playbackState = null!;
    private CommandBufferReplayExecution _replayState = null!;
    private CommandBufferReplayExecution _rewindState = null!;
    private CommandBufferReplayExecution _cycleState = null!;

    [IterationSetup(Target = nameof(PlaybackOnly))]
    public void SetupPlaybackOnly()
    {
        _playbackState = CommandBufferReplayScenarios.PreparePlayback(CreateScenario());
    }

    [IterationSetup(Target = nameof(ReplayWithReverse))]
    public void SetupReplayWithReverse()
    {
        _replayState = CommandBufferReplayScenarios.PrepareReplay(CreateScenario());
    }

    [IterationSetup(Target = nameof(RewindOnly))]
    public void SetupRewindOnly()
    {
        _rewindState = CommandBufferReplayScenarios.PrepareReplay(CreateScenario());
        var frame = _rewindState.Frame;
        var reverse = _rewindState.CurrentWorld.ReplayWithReverse(in frame);
        _rewindState.StoreReverse(reverse);
    }

    [IterationSetup(Target = nameof(ReplayWithReverseAndRewind))]
    public void SetupReplayWithReverseAndRewind()
    {
        _cycleState = CommandBufferReplayScenarios.PrepareReplay(CreateScenario());
    }

    [Benchmark(Description = "MiniArch command buffer playback only")]
    public FrameCommands PlaybackOnly()
    {
        return _playbackState.Buffer.Playback();
    }

    [Benchmark(Description = "MiniArch command buffer replay with reverse")]
    public void ReplayWithReverse()
    {
        var frame = _replayState.Frame;
        var reverse = _replayState.CurrentWorld.ReplayWithReverse(in frame);
        _replayState.StoreReverse(reverse);
    }

    [Benchmark(Description = "MiniArch command buffer rewind only")]
    public void RewindOnly()
    {
        var reverse = _rewindState.Reverse;
        _rewindState.CurrentWorld.Rewind(in reverse);
        _rewindState.ClearReverse();
    }

    [Benchmark(Description = "MiniArch command buffer replay with reverse plus rewind")]
    public void ReplayWithReverseAndRewind()
    {
        var frame = _cycleState.Frame;
        var reverse = _cycleState.CurrentWorld.ReplayWithReverse(in frame);
        _cycleState.CurrentWorld.Rewind(in reverse);
    }

    private CommandBufferReplayScenarioDefinition CreateScenario()
    {
        return new CommandBufferReplayScenarioDefinition(GetScenarioName(), Scenario, EntityCount);
    }

    private string GetScenarioName()
    {
        return Scenario switch
        {
            CommandBufferReplayScenarioKind.ExistingHeavy => "existing-heavy",
            CommandBufferReplayScenarioKind.MixedHeavy => "mixed-heavy",
            _ => throw new ArgumentOutOfRangeException(nameof(Scenario))
        };
    }
}

public class CommandBufferWorldDeltaBenchmarks
{
    [Params(10_000, 100_000)]
    public int EntityCount { get; set; }

    [Params(
        CommandBufferReplayScenarioKind.ExistingHeavy,
        CommandBufferReplayScenarioKind.MixedHeavy)]
    public CommandBufferReplayScenarioKind Scenario { get; set; }

    private CommandBufferReplayPlaybackState _playbackState = null!;
    private CommandBufferDeltaExecution _forwardState = null!;
    private CommandBufferDeltaExecution _backwardState = null!;
    private CommandBufferDeltaExecution _cycleState = null!;

    [IterationSetup(Target = nameof(PlaybackDeltaOnly))]
    public void SetupPlaybackDeltaOnly()
    {
        _playbackState = CommandBufferReplayScenarios.PreparePlayback(CreateScenario());
    }

    [IterationSetup(Target = nameof(ApplyDeltaForwardOnly))]
    public void SetupApplyDeltaForwardOnly()
    {
        _forwardState = CommandBufferReplayScenarios.PrepareDelta(CreateScenario());
    }

    [IterationSetup(Target = nameof(ApplyDeltaBackwardOnly))]
    public void SetupApplyDeltaBackwardOnly()
    {
        _backwardState = CommandBufferReplayScenarios.PrepareDelta(CreateScenario());
        _backwardState.ApplyForward();
    }

    [IterationSetup(Target = nameof(ApplyDeltaForwardAndBackward))]
    public void SetupApplyDeltaForwardAndBackward()
    {
        _cycleState = CommandBufferReplayScenarios.PrepareDelta(CreateScenario());
    }

    [Benchmark(Description = "MiniArch world delta playback only")]
    public WorldDelta PlaybackDeltaOnly()
    {
        return _playbackState.Buffer.PlaybackDelta();
    }

    [Benchmark(Description = "MiniArch world delta apply forward only")]
    public void ApplyDeltaForwardOnly()
    {
        _forwardState.ApplyForward();
    }

    [Benchmark(Description = "MiniArch world delta apply backward only")]
    public void ApplyDeltaBackwardOnly()
    {
        _backwardState.ApplyBackward();
    }

    [Benchmark(Description = "MiniArch world delta apply forward plus backward")]
    public void ApplyDeltaForwardAndBackward()
    {
        _cycleState.ApplyForward();
        _cycleState.ApplyBackward();
    }

    private CommandBufferReplayScenarioDefinition CreateScenario()
    {
        return new CommandBufferReplayScenarioDefinition(GetScenarioName(), Scenario, EntityCount);
    }

    private string GetScenarioName()
    {
        return Scenario switch
        {
            CommandBufferReplayScenarioKind.ExistingHeavy => "world-delta-existing-heavy",
            CommandBufferReplayScenarioKind.MixedHeavy => "world-delta-mixed-heavy",
            _ => throw new ArgumentOutOfRangeException(nameof(Scenario))
        };
    }
}

internal static class CommandBufferBenchmarkScenarioFactory
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
        var buffer = new MiniCommandBuffer(state.World);
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

        buffer.Play();
    }

    private static void RunMiniDenseExisting(MiniSharedCommandBufferState state)
    {
        var buffer = new MiniCommandBuffer(state.World);
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

        buffer.Play();
    }

    private static void RunMiniCreateHeavy(MiniSharedCommandBufferState state)
    {
        var buffer = new MiniCommandBuffer(state.World);
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

        buffer.Play();
    }

    private static void RunMiniMixedScript(MiniSharedCommandBufferState state)
    {
        var buffer = new MiniCommandBuffer(state.World);
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

        buffer.Play();
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
}

internal sealed class MiniSharedCommandBufferState
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

internal sealed class ArchSharedCommandBufferState : IDisposable
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

internal sealed class MiniHierarchyCommandBufferState
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

internal readonly record struct BenchmarkPosition(int X, int Y);
internal readonly record struct BenchmarkVelocity(int X, int Y);
internal readonly record struct BenchmarkHealth(int Value);
internal readonly record struct BenchmarkArmor(int Value);

