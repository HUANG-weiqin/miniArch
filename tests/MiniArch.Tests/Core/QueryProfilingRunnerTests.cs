using MiniArchBenchmarks;

namespace MiniArchTests.Core;

public sealed class QueryProfilingRunnerTests
{
    [Fact]
    public void TryParse_uses_expected_defaults()
    {
        var parsed = QueryProfilingOptions.TryParse(Array.Empty<string>(), out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(QueryProfilingScenario.WithAll, options.Scenario);
        Assert.Equal(QueryProfilingTemperature.Cold, options.Temperature);
        Assert.Equal(100_000, options.EntityCount);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Duration);
        Assert.Equal(3, options.WarmupIterations);
        Assert.Equal(TimeSpan.FromSeconds(3), options.StartupDelay);
    }

    [Fact]
    public void TryParse_accepts_explicit_overrides()
    {
        var args = new[]
        {
            "--scenario", "with-all-any",
            "--temperature", "hot",
            "--entity-count", "4096",
            "--duration", "2",
            "--warmup", "5",
            "--startup-delay", "0"
        };

        var parsed = QueryProfilingOptions.TryParse(args, out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(QueryProfilingScenario.WithAllAny, options.Scenario);
        Assert.Equal(QueryProfilingTemperature.Hot, options.Temperature);
        Assert.Equal(4096, options.EntityCount);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Duration);
        Assert.Equal(5, options.WarmupIterations);
        Assert.Equal(TimeSpan.Zero, options.StartupDelay);
    }

    [Theory]
    [InlineData(QueryProfilingTemperature.Hot)]
    [InlineData(QueryProfilingTemperature.Cold)]
    public void Run_executes_profile_workload(QueryProfilingTemperature temperature)
    {
        var options = new QueryProfilingOptions(
            QueryProfilingScenario.WithAll,
            temperature,
            EntityCount: 1_000,
            Duration: TimeSpan.FromMilliseconds(50),
            WarmupIterations: 1,
            StartupDelay: TimeSpan.Zero);

        var result = QueryProfilingRunner.Run(options, TextWriter.Null, CancellationToken.None);

        Assert.True(result.IterationCount > 0);
        Assert.NotEqual(0, result.TotalChecksum);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        if (temperature == QueryProfilingTemperature.Cold)
        {
            Assert.True(result.RefreshCount > 0);
            return;
        }

        Assert.True(result.RefreshCount >= 0);
    }
}
