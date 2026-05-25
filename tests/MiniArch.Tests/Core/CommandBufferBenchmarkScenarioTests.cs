using MiniArchBenchmarks;
namespace MiniArchTests.Core;

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

    public static IEnumerable<object[]> ReplayScenarios()
    {
        foreach (var scenario in CommandBufferReplayScenarios.CreateBenchmarkedScenarios(32))
        {
            yield return new object[] { scenario };
        }
    }
}
