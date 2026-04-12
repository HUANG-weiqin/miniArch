using MiniArch.Benchmarks;
namespace MiniArch.Tests.Core;

public sealed class CommandBufferBenchmarkScenarioTests
{
    [Fact]
    public void Replay_scenarios_expose_existing_heavy_and_mixed_heavy_variants()
    {
        var scenarios = CommandBufferReplayScenarios.CreateBenchmarkedScenarios(32)
            .Select(static scenario => scenario.Name)
            .ToArray();

        Assert.Contains("existing-heavy", scenarios);
        Assert.Contains("mixed-heavy", scenarios);
    }

    [Theory]
    [MemberData(nameof(ReplayScenarios))]
    public void Playback_smoke_scenarios_execute_real_replay_and_rewind_api(CommandBufferReplayScenarioDefinition scenario)
    {
        using var playback = CommandBufferReplayScenarios.PreparePlayback(scenario);
        var baseline = CommandBufferReplayScenarios.SummarizeWorld(scenario.Name, playback.World);

        var frame = playback.Buffer.Playback();
        var reverse = playback.World.ReplayWithReverse(in frame);

        Assert.True(frame.CreatedEntities.Count + frame.DestroyedEntities.Count + frame.SetCommands.Count + frame.AddCommands.Count + frame.RemoveCommands.Count > 0);
        Assert.NotEqual(baseline, CommandBufferReplayScenarios.SummarizeWorld(scenario.Name, playback.World));

        playback.World.Rewind(in reverse);

        Assert.Equal(baseline, CommandBufferReplayScenarios.SummarizeWorld(scenario.Name, playback.World));
    }

    [Theory]
    [MemberData(nameof(ReplayScenarios))]
    public void Replay_with_reverse_then_rewind_returns_to_baseline_summary_without_replacing_world(CommandBufferReplayScenarioDefinition scenario)
    {
        using var execution = CommandBufferReplayScenarios.PrepareReplay(scenario);
        var world = execution.CurrentWorld;

        var baseline = execution.BaselineSummary;
        execution.ReplayWithReverse();

        Assert.Same(world, execution.CurrentWorld);
        Assert.Equal(execution.ReplayedSummary, execution.SummarizeCurrentWorld());

        execution.Rewind();

        Assert.Same(world, execution.CurrentWorld);
        Assert.Equal(baseline, execution.SummarizeCurrentWorld());
    }

    [Theory]
    [MemberData(nameof(ReplayScenarios))]
    public void Replay_with_reverse_then_rewind_then_replay_with_reverse_is_stable(CommandBufferReplayScenarioDefinition scenario)
    {
        using var execution = CommandBufferReplayScenarios.PrepareReplay(scenario);

        execution.ReplayWithReverse();
        var firstReplay = execution.SummarizeCurrentWorld();

        execution.Rewind();
        execution.ReplayWithReverse();
        var secondReplay = execution.SummarizeCurrentWorld();

        Assert.Equal(firstReplay, secondReplay);
    }

    public static IEnumerable<object[]> ReplayScenarios()
    {
        foreach (var scenario in CommandBufferReplayScenarios.CreateBenchmarkedScenarios(32))
        {
            yield return new object[] { scenario };
        }
    }
}
