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
        Assert.Equal(QueryProfilingWorkload.Entity, options.Workload);
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
            "--workload", "component-span",
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
        Assert.Equal(QueryProfilingWorkload.ComponentSpan, options.Workload);
        Assert.Equal(QueryProfilingScenario.WithAllAny, options.Scenario);
        Assert.Equal(QueryProfilingTemperature.Hot, options.Temperature);
        Assert.Equal(4096, options.EntityCount);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Duration);
        Assert.Equal(5, options.WarmupIterations);
        Assert.Equal(TimeSpan.Zero, options.StartupDelay);
    }

    [Fact]
    public void TryParse_accepts_workload_entity()
    {
        var args = new[] { "--workload", "entity" };

        var parsed = QueryProfilingOptions.TryParse(args, out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(options);
    }

    [Fact]
    public void TryParse_accepts_workload_component_row_wise()
    {
        var args = new[] { "--workload", "component-row-wise" };

        var parsed = QueryProfilingOptions.TryParse(args, out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(options);
    }

    [Fact]
    public void TryParse_accepts_workload_component_span()
    {
        var args = new[] { "--workload", "component-span" };

        var parsed = QueryProfilingOptions.TryParse(args, out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(options);
    }

    [Fact]
    public void TryParse_rejects_invalid_workload()
    {
        var args = new[] { "--workload", "invalid-workload" };

        var parsed = QueryProfilingOptions.TryParse(args, out _, out var error);

        Assert.False(parsed);
        Assert.NotNull(error);
        Assert.Contains("Unsupported workload", error);
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("component-row-wise")]
    [InlineData("component-span")]
    public void Run_executes_all_workload_types(string workloadValue)
    {
        var workload = workloadValue switch
        {
            "entity" => QueryProfilingWorkload.Entity,
            "component-row-wise" => QueryProfilingWorkload.ComponentRowWise,
            "component-span" => QueryProfilingWorkload.ComponentSpan,
            _ => throw new ArgumentOutOfRangeException(nameof(workloadValue))
        };

        var options = new QueryProfilingOptions(
            workload,
            QueryProfilingScenario.WithAll,
            QueryProfilingTemperature.Hot,
            EntityCount: 1_000,
            Duration: TimeSpan.FromMilliseconds(50),
            WarmupIterations: 1,
            StartupDelay: TimeSpan.Zero);

        var result = QueryProfilingRunner.Run(options, TextWriter.Null, CancellationToken.None);

        Assert.True(result.IterationCount > 0);
        Assert.NotEqual(0, result.TotalChecksum);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Theory]
    [InlineData(QueryProfilingTemperature.Hot)]
    [InlineData(QueryProfilingTemperature.Cold)]
    public void Run_executes_profile_workload(QueryProfilingTemperature temperature)
    {
        var options = new QueryProfilingOptions(
            QueryProfilingWorkload.Entity,
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
