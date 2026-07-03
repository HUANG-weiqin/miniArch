using CommandBufferGamePerf;

namespace MiniArch.Tests.Core;

public sealed class CommandBufferGamePerfTests
{
    [Fact]
    public void MiniArchScenarioMaintainsSteadyLiveCount()
    {
        using var scenario = new MiniArchCommandStreamSteadyCombatWorld();

        for (var i = 0; i < 8; i++)
        {
            scenario.RunTick();
        }

        Assert.Equal(ScenarioDefaults.InitialLiveCount, scenario.LiveCount);
        Assert.NotEqual(0u, scenario.Checksum);
    }
}
