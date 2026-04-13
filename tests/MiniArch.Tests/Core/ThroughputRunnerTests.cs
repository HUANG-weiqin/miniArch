using MiniArchBenchmarks;

namespace MiniArchTests.Core;

public sealed class ThroughputRunnerTests
{
    [Fact]
    public void TryParse_uses_expected_defaults()
    {
        var parsed = ThroughputOptions.TryParse(Array.Empty<string>(), out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(ThroughputWorkload.QueryWithAllEntity, options.Workload);
        Assert.Equal(ThroughputEngine.Both, options.Engine);
        Assert.Equal(100_000, options.EntityCount);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Duration);
        Assert.Equal(3, options.WarmupIterations);
        Assert.Equal(5, options.RepeatCount);
    }

    [Fact]
    public void TryParse_accepts_explicit_overrides()
    {
        var args = new[]
        {
            "--workload", "query-with-all-component-span",
            "--engine", "miniarch",
            "--entity-count", "4096",
            "--duration", "2",
            "--warmup", "4",
            "--repeat", "7",
        };

        var parsed = ThroughputOptions.TryParse(args, out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(ThroughputWorkload.QueryWithAllComponentSpan, options.Workload);
        Assert.Equal(ThroughputEngine.MiniArch, options.Engine);
        Assert.Equal(4096, options.EntityCount);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Duration);
        Assert.Equal(4, options.WarmupIterations);
        Assert.Equal(7, options.RepeatCount);
    }

    [Fact]
    public void TryParse_accepts_world_delta_workload()
    {
        var args = new[]
        {
            "--workload", "world-delta",
            "--engine", "miniarch",
            "--entity-count", "50000",
            "--duration", "3",
            "--warmup", "2",
            "--repeat", "4",
        };

        var parsed = ThroughputOptions.TryParse(args, out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(ThroughputWorkload.WorldDelta, options.Workload);
        Assert.Equal(ThroughputEngine.MiniArch, options.Engine);
        Assert.Equal(50000, options.EntityCount);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Duration);
        Assert.Equal(2, options.WarmupIterations);
        Assert.Equal(4, options.RepeatCount);
    }

    [Fact]
    public void Run_executes_workload_for_each_requested_engine()
    {
        var options = new ThroughputOptions(
            ThroughputWorkload.QueryWithAllEntity,
            ThroughputEngine.Both,
            EntityCount: 1_000,
            Duration: TimeSpan.FromMilliseconds(50),
            WarmupIterations: 1,
            RepeatCount: 2);

        var report = ThroughputRunner.Run(options, TextWriter.Null, CancellationToken.None);

        Assert.Equal(2, report.EngineSummaries.Count);
        Assert.Contains(report.EngineSummaries, summary => summary.Engine == ThroughputEngine.MiniArch);
        Assert.Contains(report.EngineSummaries, summary => summary.Engine == ThroughputEngine.Arch);
        Assert.All(report.EngineSummaries, summary =>
        {
            Assert.Equal(2, summary.Runs.Count);
            Assert.True(summary.AverageOpsPerSecond > 0);
            Assert.True(summary.BestOpsPerSecond > 0);
            Assert.True(summary.MedianOpsPerSecond > 0);
        });
        Assert.NotNull(report.Comparison);
    }

    [Fact]
    public void Run_executes_world_delta_workload_for_miniarch()
    {
        var options = new ThroughputOptions(
            ThroughputWorkload.WorldDelta,
            ThroughputEngine.MiniArch,
            EntityCount: 10_000,
            Duration: TimeSpan.FromMilliseconds(50),
            WarmupIterations: 1,
            RepeatCount: 2);

        var report = ThroughputRunner.Run(options, TextWriter.Null, CancellationToken.None);

        var summary = Assert.Single(report.EngineSummaries);
        Assert.Equal(ThroughputEngine.MiniArch, summary.Engine);
        Assert.Equal(2, summary.Runs.Count);
        Assert.True(summary.AverageOpsPerSecond > 0);
        Assert.True(summary.MedianOpsPerSecond > 0);
        Assert.True(summary.BestOpsPerSecond > 0);
        Assert.Null(report.Comparison);
    }

    [Fact]
    public void Comparison_uses_average_ops_per_second()
    {
        var mini = new ThroughputEngineSummary(
            ThroughputEngine.MiniArch,
            new[]
            {
                new ThroughputRunResult(ThroughputEngine.MiniArch, 100, TimeSpan.FromSeconds(1), 1),
                new ThroughputRunResult(ThroughputEngine.MiniArch, 120, TimeSpan.FromSeconds(1), 1),
            });
        var arch = new ThroughputEngineSummary(
            ThroughputEngine.Arch,
            new[]
            {
                new ThroughputRunResult(ThroughputEngine.Arch, 80, TimeSpan.FromSeconds(1), 1),
                new ThroughputRunResult(ThroughputEngine.Arch, 100, TimeSpan.FromSeconds(1), 1),
            });

        var comparison = ThroughputComparisonSummary.Create(mini, arch);

        Assert.NotNull(comparison);
        Assert.Equal(110d, comparison.MiniArchAverageOpsPerSecond);
        Assert.Equal(90d, comparison.ArchAverageOpsPerSecond);
        Assert.InRange(comparison.RelativeDifferencePercent, 22.2d, 22.3d);
    }
}
