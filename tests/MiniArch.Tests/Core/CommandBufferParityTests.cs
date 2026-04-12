using MiniArchBenchmarks;

namespace MiniArchTests.Core;

public sealed class CommandBufferParityTests
{
    public static IEnumerable<object[]> SharedScenarios()
    {
        foreach (var scenario in CommandBufferSharedScenarios.CreateParityScenarios())
        {
            yield return new object[] { scenario };
        }
    }

    [Theory]
    [MemberData(nameof(SharedScenarios))]
    public void MiniArch_and_Arch_produce_the_same_structural_summary_for_shared_command_buffer_scenarios(CommandBufferSharedScenario scenario)
    {
        var miniArchSummary = CommandBufferSharedScenarios.ExecuteMiniArchPlay(scenario);
        var archSummary = CommandBufferSharedScenarios.ExecuteArchPlayback(scenario);

        Assert.Equal(miniArchSummary, archSummary);
    }
}
