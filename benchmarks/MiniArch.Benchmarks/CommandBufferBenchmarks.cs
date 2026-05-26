using Arch.Core;
using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using ArchComponentType = Arch.Core.ComponentType;
using ArchCommandBuffer = Arch.Buffer.CommandBuffer;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using MiniEntity = MiniArch.Entity;
using MiniWorld = MiniArch.World;

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

    [Benchmark(Description = "MiniArch command buffer record+submit")]
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
