using System.Diagnostics;
using System.Runtime.CompilerServices;
using Arch.Core;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using ArchQueryDescription = Arch.Core.QueryDescription;
using MiniQueryCache = MiniArch.Core.QueryCache;
using MiniComponentType = MiniArch.Core.ComponentType;
using ArchWorld = Arch.Core.World;
using ArchEntity = Arch.Core.Entity;
using MiniWorld = MiniArch.World;
using MiniEntity = MiniArch.Entity;

public enum ThroughputWorkload
{
    QueryWithAllEntity,
    QueryWithAllComponentSpan,
    SetSingleComponent,
    SetTwoComponents,
    CreateDestroyPairwise,
    CreateDestroyBatch,
    CreateDestroyPairwiseMulti,
    QueryWithAllEachSpan,
    QueryWithAllComponentSpanWide,
    QueryWithAllEachSpanWide,
}

public enum ThroughputEngine
{
    MiniArch,
    Arch,
    Both,
}

public sealed record ThroughputOptions(
    ThroughputWorkload Workload,
    ThroughputEngine Engine,
    int EntityCount,
    TimeSpan Duration,
    int WarmupIterations,
    int RepeatCount)
{
    public static ThroughputOptions Default { get; } = new(
        ThroughputWorkload.QueryWithAllEntity,
        ThroughputEngine.Both,
        100_000,
        TimeSpan.FromSeconds(10),
        3,
        5);

    public static bool TryParse(string[] args, out ThroughputOptions options, out string? error)
    {
        options = Default;
        error = null;

        var workload = options.Workload;
        var engine = options.Engine;
        var entityCount = options.EntityCount;
        var duration = options.Duration;
        var warmupIterations = options.WarmupIterations;
        var repeatCount = options.RepeatCount;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (i + 1 >= args.Length)
            {
                error = $"Missing value for {arg}.";
                return false;
            }

            var value = args[++i];
            switch (arg)
            {
                case "--workload":
                    if (!TryParseWorkload(value, out workload))
                    {
                        error = $"Unsupported workload '{value}'.";
                        return false;
                    }

                    break;
                case "--engine":
                    if (!TryParseEngine(value, out engine))
                    {
                        error = $"Unsupported engine '{value}'.";
                        return false;
                    }

                    break;
                case "--entity-count":
                    if (!int.TryParse(value, out entityCount) || entityCount <= 0)
                    {
                        error = $"Invalid entity count '{value}'.";
                        return false;
                    }

                    break;
                case "--duration":
                    if (!int.TryParse(value, out var durationSeconds) || durationSeconds <= 0)
                    {
                        error = $"Invalid duration '{value}'.";
                        return false;
                    }

                    duration = TimeSpan.FromSeconds(durationSeconds);
                    break;
                case "--warmup":
                    if (!int.TryParse(value, out warmupIterations) || warmupIterations < 0)
                    {
                        error = $"Invalid warmup '{value}'.";
                        return false;
                    }

                    break;
                case "--repeat":
                    if (!int.TryParse(value, out repeatCount) || repeatCount <= 0)
                    {
                        error = $"Invalid repeat '{value}'.";
                        return false;
                    }

                    break;
                default:
                    error = $"Unknown argument '{arg}'.";
                    return false;
            }
        }

        options = new ThroughputOptions(workload, engine, entityCount, duration, warmupIterations, repeatCount);
        return true;
    }

    private static bool TryParseWorkload(string value, out ThroughputWorkload workload)
    {
        workload = value.ToLowerInvariant() switch
        {
            "query-with-all-entity" => ThroughputWorkload.QueryWithAllEntity,
            "query-with-all-component-span" => ThroughputWorkload.QueryWithAllComponentSpan,
            "set-single-component" => ThroughputWorkload.SetSingleComponent,
            "set-two-components" => ThroughputWorkload.SetTwoComponents,
            "create-destroy-pairwise" => ThroughputWorkload.CreateDestroyPairwise,
            "create-destroy-batch" => ThroughputWorkload.CreateDestroyBatch,
            "create-destroy-pairwise-multi" => ThroughputWorkload.CreateDestroyPairwiseMulti,
            "query-with-all-eachspan" => ThroughputWorkload.QueryWithAllEachSpan,
            "query-with-all-component-span-wide" => ThroughputWorkload.QueryWithAllComponentSpanWide,
            "query-with-all-eachspan-wide" => ThroughputWorkload.QueryWithAllEachSpanWide,
            _ => default
        };

        return value is
            "query-with-all-entity" or
            "query-with-all-component-span" or
            "query-with-all-eachspan" or
            "query-with-all-component-span-wide" or
            "query-with-all-eachspan-wide" or
            "set-single-component" or
            "set-two-components" or
            "create-destroy-pairwise" or
            "create-destroy-pairwise-multi" or
            "create-destroy-batch";
    }

    private static bool TryParseEngine(string value, out ThroughputEngine engine)
    {
        engine = value.ToLowerInvariant() switch
        {
            "miniarch" => ThroughputEngine.MiniArch,
            "arch" => ThroughputEngine.Arch,
            "both" => ThroughputEngine.Both,
            _ => default
        };

        return value is "miniarch" or "arch" or "both";
    }
}

public readonly record struct ThroughputRunResult(
    ThroughputEngine Engine,
    long IterationCount,
    TimeSpan Elapsed,
    long TotalChecksum)
{
    public double OpsPerSecond => Elapsed.TotalSeconds <= 0
        ? 0
        : IterationCount / Elapsed.TotalSeconds;
}

public sealed class ThroughputEngineSummary
{
    public ThroughputEngineSummary(ThroughputEngine engine, IReadOnlyList<ThroughputRunResult> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("At least one run is required.", nameof(runs));
        }

        Engine = engine;
        Runs = runs;

        var ops = runs.Select(run => run.OpsPerSecond).OrderBy(value => value).ToArray();
        AverageOpsPerSecond = ops.Average();
        BestOpsPerSecond = ops[^1];
        MedianOpsPerSecond = ops.Length % 2 == 1
            ? ops[ops.Length / 2]
            : (ops[(ops.Length / 2) - 1] + ops[ops.Length / 2]) / 2d;
    }

    public ThroughputEngine Engine { get; }

    public IReadOnlyList<ThroughputRunResult> Runs { get; }

    public double AverageOpsPerSecond { get; }

    public double MedianOpsPerSecond { get; }

    public double BestOpsPerSecond { get; }
}

public sealed class ThroughputComparisonSummary
{
    private ThroughputComparisonSummary(double miniArchAverageOpsPerSecond, double archAverageOpsPerSecond, double relativeDifferencePercent)
    {
        MiniArchAverageOpsPerSecond = miniArchAverageOpsPerSecond;
        ArchAverageOpsPerSecond = archAverageOpsPerSecond;
        RelativeDifferencePercent = relativeDifferencePercent;
    }

    public double MiniArchAverageOpsPerSecond { get; }

    public double ArchAverageOpsPerSecond { get; }

    public double RelativeDifferencePercent { get; }

    public static ThroughputComparisonSummary Create(ThroughputEngineSummary miniArch, ThroughputEngineSummary arch)
    {
        ArgumentNullException.ThrowIfNull(miniArch);
        ArgumentNullException.ThrowIfNull(arch);

        var relativeDifference = arch.AverageOpsPerSecond == 0
            ? 0
            : ((miniArch.AverageOpsPerSecond - arch.AverageOpsPerSecond) / arch.AverageOpsPerSecond) * 100d;

        return new ThroughputComparisonSummary(
            miniArch.AverageOpsPerSecond,
            arch.AverageOpsPerSecond,
            relativeDifference);
    }
}

public sealed record ThroughputReport(
    ThroughputWorkload Workload,
    IReadOnlyList<ThroughputEngineSummary> EngineSummaries,
    ThroughputComparisonSummary? Comparison);

public static class ThroughputRunner
{
    public static int RunFromCommandLine(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (!ThroughputOptions.TryParse(args, out var options, out var error))
        {
            stderr.WriteLine(error);
            stderr.WriteLine("Usage: throughput [--workload query-with-all-entity|query-with-all-component-span|query-with-all-eachspan|query-with-all-component-span-wide|query-with-all-eachspan-wide|set-single-component|set-two-components|create-destroy-pairwise|create-destroy-batch] [--engine miniarch|arch|both] [--entity-count N] [--duration seconds] [--warmup count] [--repeat count]");
            return 1;
        }

        Run(options, stdout, cancellationToken);
        return 0;
    }

    public static ThroughputReport Run(ThroughputOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        Validate(options);

        output.WriteLine("MiniArch throughput workload");
        output.WriteLine($"Workload: {FormatWorkload(options.Workload)}");
        output.WriteLine($"Engine: {FormatEngine(options.Engine)}");
        output.WriteLine($"EntityCount: {options.EntityCount}");
        output.WriteLine($"DurationSeconds: {options.Duration.TotalSeconds:F0}");
        output.WriteLine($"WarmupIterations: {options.WarmupIterations}");
        output.WriteLine($"RepeatCount: {options.RepeatCount}");

        var summaries = new List<ThroughputEngineSummary>();
        foreach (var engine in ExpandEngines(options.Engine))
        {
            var runs = new List<ThroughputRunResult>(options.RepeatCount);
            for (var repeat = 0; repeat < options.RepeatCount; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = ThroughputCaseFactory.CreateAndRun(
                    options.Workload, engine, options.EntityCount,
                    options.WarmupIterations, options.Duration, cancellationToken);
                runs.Add(result);

                output.WriteLine($"{FormatEngine(engine)} repeat {repeat + 1}/{options.RepeatCount}: iterations={result.IterationCount}, elapsedMs={result.Elapsed.TotalMilliseconds:F0}, opsPerSecond={result.OpsPerSecond:F2}, checksum={result.TotalChecksum}");
            }

            var summary = new ThroughputEngineSummary(engine, runs);
            summaries.Add(summary);
            output.WriteLine($"{FormatEngine(engine)} summary: avgOpsPerSecond={summary.AverageOpsPerSecond:F2}, medianOpsPerSecond={summary.MedianOpsPerSecond:F2}, bestOpsPerSecond={summary.BestOpsPerSecond:F2}");
        }

        var comparison = BuildComparison(summaries);
        if (comparison is not null)
        {
            output.WriteLine($"Comparison: miniarchAvgOpsPerSecond={comparison.MiniArchAverageOpsPerSecond:F2}, archAvgOpsPerSecond={comparison.ArchAverageOpsPerSecond:F2}, relativeDifferencePercent={comparison.RelativeDifferencePercent:F2}");
        }

        return new ThroughputReport(options.Workload, summaries, comparison);
    }

    internal static ThroughputRunResult WarmupAndMeasure<T>(
        ThroughputEngine engine,
        T throughputCase,
        int warmupIterations,
        TimeSpan duration,
        CancellationToken cancellationToken)
        where T : IThroughputCase, IDisposable
    {
        for (var i = 0; i < warmupIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = throughputCase.RunIteration();
        }

        var stopwatch = Stopwatch.StartNew();
        long iterations = 0;
        long checksum = 0;

        while (stopwatch.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checksum += throughputCase.RunIteration();
            iterations++;
        }

        stopwatch.Stop();
        return new ThroughputRunResult(engine, iterations, stopwatch.Elapsed, checksum);
    }

    private static ThroughputComparisonSummary? BuildComparison(IReadOnlyList<ThroughputEngineSummary> summaries)
    {
        var miniArch = summaries.SingleOrDefault(summary => summary.Engine == ThroughputEngine.MiniArch);
        var arch = summaries.SingleOrDefault(summary => summary.Engine == ThroughputEngine.Arch);
        if (miniArch is null || arch is null)
        {
            return null;
        }

        return ThroughputComparisonSummary.Create(miniArch, arch);
    }

    private static IReadOnlyList<ThroughputEngine> ExpandEngines(ThroughputEngine engine)
    {
        return engine == ThroughputEngine.Both
            ? new[] { ThroughputEngine.MiniArch, ThroughputEngine.Arch }
            : new[] { engine };
    }

    private static string FormatWorkload(ThroughputWorkload workload)
    {
        return workload switch
        {
            ThroughputWorkload.QueryWithAllEntity => "query-with-all-entity",
            ThroughputWorkload.QueryWithAllComponentSpan => "query-with-all-component-span",
            ThroughputWorkload.SetSingleComponent => "set-single-component",
            ThroughputWorkload.SetTwoComponents => "set-two-components",
            ThroughputWorkload.CreateDestroyPairwise => "create-destroy-pairwise",
            ThroughputWorkload.CreateDestroyBatch => "create-destroy-batch",
            ThroughputWorkload.CreateDestroyPairwiseMulti => "create-destroy-pairwise-multi",
            ThroughputWorkload.QueryWithAllEachSpan => "query-with-all-eachspan",
            ThroughputWorkload.QueryWithAllComponentSpanWide => "query-with-all-component-span-wide",
            ThroughputWorkload.QueryWithAllEachSpanWide => "query-with-all-eachspan-wide",
            _ => throw new ArgumentOutOfRangeException(nameof(workload))
        };
    }

    private static string FormatEngine(ThroughputEngine engine)
    {
        return engine switch
        {
            ThroughputEngine.MiniArch => "miniarch",
            ThroughputEngine.Arch => "arch",
            ThroughputEngine.Both => "both",
            _ => throw new ArgumentOutOfRangeException(nameof(engine))
        };
    }

    private static void Validate(ThroughputOptions options)
    {
        if (options.EntityCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "EntityCount must be positive.");
        }

        if (options.Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Duration must be positive.");
        }

        if (options.WarmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WarmupIterations must be non-negative.");
        }

        if (options.RepeatCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RepeatCount must be positive.");
        }
    }
}

internal interface IThroughputCase : IDisposable
{
    void WarmUp(int count, CancellationToken cancellationToken);

    long RunIteration();
}

internal static class ThroughputCaseFactory
{
    public static IThroughputCase Create(ThroughputWorkload workload, ThroughputEngine engine, int entityCount)
    {
        return (workload, engine) switch
        {
            (ThroughputWorkload.QueryWithAllEntity, ThroughputEngine.MiniArch) => new MiniQueryEntityThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllEntity, ThroughputEngine.Arch) => new ArchQueryEntityThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllComponentSpan, ThroughputEngine.MiniArch) => new MiniQueryComponentRefThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllComponentSpan, ThroughputEngine.Arch) => new ArchQueryComponentSpanThroughputCase(entityCount),
            (ThroughputWorkload.SetSingleComponent, ThroughputEngine.MiniArch) => new MiniSetSingleComponentThroughputCase(entityCount),
            (ThroughputWorkload.SetSingleComponent, ThroughputEngine.Arch) => new ArchSetSingleComponentThroughputCase(entityCount),
            (ThroughputWorkload.SetTwoComponents, ThroughputEngine.MiniArch) => new MiniSetTwoComponentsThroughputCase(entityCount),
            (ThroughputWorkload.SetTwoComponents, ThroughputEngine.Arch) => new ArchSetTwoComponentsThroughputCase(entityCount),
            (ThroughputWorkload.CreateDestroyPairwise, ThroughputEngine.MiniArch) => new MiniCreateDestroyPairwiseThroughputCase(entityCount),
            (ThroughputWorkload.CreateDestroyPairwise, ThroughputEngine.Arch) => new ArchCreateDestroyPairwiseThroughputCase(entityCount),
            (ThroughputWorkload.CreateDestroyBatch, ThroughputEngine.MiniArch) => new MiniCreateDestroyBatchThroughputCase(entityCount),
            (ThroughputWorkload.CreateDestroyBatch, ThroughputEngine.Arch) => new ArchCreateDestroyBatchThroughputCase(entityCount),
            (ThroughputWorkload.CreateDestroyPairwiseMulti, ThroughputEngine.MiniArch) => new MiniCreateDestroyPairwiseMultiThroughputCase(entityCount),
            (ThroughputWorkload.CreateDestroyPairwiseMulti, ThroughputEngine.Arch) => new ArchCreateDestroyPairwiseMultiThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllEachSpan, ThroughputEngine.Arch) => new ArchQueryEachSpanThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllEachSpanWide, ThroughputEngine.Arch) => new ArchWideQueryEachSpanThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllComponentSpanWide, ThroughputEngine.MiniArch) => new MiniWideQueryComponentSpanThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllComponentSpanWide, ThroughputEngine.Arch) => new ArchWideQueryComponentSpanThroughputCase(entityCount),
            (_, ThroughputEngine.Both) => throw new InvalidOperationException("Throughput case factory expects a concrete engine."),
            _ => throw new ArgumentOutOfRangeException(nameof(workload))
        };
    }

    public static ThroughputRunResult CreateAndRun(
        ThroughputWorkload workload,
        ThroughputEngine engine,
        int entityCount,
        int warmupIterations,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        switch ((workload, engine))
        {
            case (ThroughputWorkload.QueryWithAllEntity, ThroughputEngine.MiniArch):
                using (var c = new MiniQueryEntityThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.QueryWithAllEntity, ThroughputEngine.Arch):
                using (var c = new ArchQueryEntityThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.QueryWithAllComponentSpan, ThroughputEngine.MiniArch):
                using (var c = new MiniQueryComponentRefThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.QueryWithAllComponentSpan, ThroughputEngine.Arch):
                using (var c = new ArchQueryComponentSpanThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.SetSingleComponent, ThroughputEngine.MiniArch):
                using (var c = new MiniSetSingleComponentThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.SetSingleComponent, ThroughputEngine.Arch):
                using (var c = new ArchSetSingleComponentThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.SetTwoComponents, ThroughputEngine.MiniArch):
                using (var c = new MiniSetTwoComponentsThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.SetTwoComponents, ThroughputEngine.Arch):
                using (var c = new ArchSetTwoComponentsThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.CreateDestroyPairwise, ThroughputEngine.MiniArch):
                using (var c = new MiniCreateDestroyPairwiseThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.CreateDestroyPairwise, ThroughputEngine.Arch):
                using (var c = new ArchCreateDestroyPairwiseThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.CreateDestroyBatch, ThroughputEngine.MiniArch):
                using (var c = new MiniCreateDestroyBatchThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.CreateDestroyBatch, ThroughputEngine.Arch):
                using (var c = new ArchCreateDestroyBatchThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.CreateDestroyPairwiseMulti, ThroughputEngine.MiniArch):
                using (var c = new MiniCreateDestroyPairwiseMultiThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.CreateDestroyPairwiseMulti, ThroughputEngine.Arch):
                using (var c = new ArchCreateDestroyPairwiseMultiThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.QueryWithAllEachSpan, ThroughputEngine.Arch):
                using (var c = new ArchQueryEachSpanThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.QueryWithAllEachSpanWide, ThroughputEngine.Arch):
                using (var c = new ArchWideQueryEachSpanThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.QueryWithAllComponentSpanWide, ThroughputEngine.MiniArch):
                using (var c = new MiniWideQueryComponentSpanThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (ThroughputWorkload.QueryWithAllComponentSpanWide, ThroughputEngine.Arch):
                using (var c = new ArchWideQueryComponentSpanThroughputCase(entityCount))
                    return ThroughputRunner.WarmupAndMeasure(engine, c, warmupIterations, duration, cancellationToken);
            case (_, ThroughputEngine.Both):
                throw new InvalidOperationException("Throughput case factory expects a concrete engine.");
            default:
                throw new ArgumentOutOfRangeException(nameof(workload));
        }
    }

    private sealed class MiniQueryEntityThroughputCase : IThroughputCase
    {
        private readonly MiniComplexQueryWorldState _state;

        public MiniQueryEntityThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniEntityQuery(_state.WithAllQuery);
            }
        }

        public long RunIteration() => ExecuteMiniEntityQuery(_state.WithAllQuery);

        public void Dispose()
        {
        }
    }

    private sealed class ArchQueryEntityThroughputCase : IThroughputCase
    {
        private readonly ArchComplexQueryWorldState _state;

        public ArchQueryEntityThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateArchComplexQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteArchEntityQuery(_state.World, _state.WithAllDescription);
            }
        }

        public long RunIteration() => ExecuteArchEntityQuery(_state.World, _state.WithAllDescription);

        public void Dispose()
        {
            _state.Dispose();
        }
    }

    private sealed class MiniQueryComponentRefThroughputCase : IThroughputCase
    {
        private readonly MiniComplexQueryWorldState _state;

        public MiniQueryComponentRefThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniComponentRefQuery(_state.WithAllQuery, _state.PositionType, _state.VelocityType);
            }
        }

        public long RunIteration() => ExecuteMiniComponentRefQuery(_state.WithAllQuery, _state.PositionType, _state.VelocityType);

        public void Dispose()
        {
        }
    }

    private sealed class ArchQueryComponentSpanThroughputCase : IThroughputCase
    {
        private readonly ArchComplexQueryWorldState _state;

        public ArchQueryComponentSpanThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateArchComplexQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteArchComponentSpanQuery(_state.World, _state.WithAllDescription);
            }
        }

        public long RunIteration() => ExecuteArchComponentSpanQuery(_state.World, _state.WithAllDescription);

        public void Dispose()
        {
            _state.Dispose();
        }
    }

    private sealed class ArchQueryEachSpanThroughputCase : IThroughputCase
    {
        private readonly ArchComplexQueryWorldState _state;

        public ArchQueryEachSpanThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateArchComplexQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteArchEachSpanQuery(_state.World, _state.WithAllDescription);
            }
        }

        public long RunIteration() => ExecuteArchEachSpanQuery(_state.World, _state.WithAllDescription);

        public void Dispose()
        {
            _state.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniEntityQuery(MiniQueryCache query)
    {
        var checksum = 0;
        var archetypes = query.GetArchetypeSpan();
        for (var archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            var archetype = archetypes[archetypeIndex];
            var count = archetype.EntityCount;
            var entities = archetype.GetEntityStorageUnsafe();
            for (var row = 0; row < count; row++)
            {
                checksum += entities[row].Id;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchEntityQuery(ArchWorld world, ArchQueryDescription description)
    {
        var checksum = 0;
        var query = world.Query(in description);
        foreach (var chunk in query)
        {
            for (var row = 0; row < chunk.Count; row++)
            {
                checksum += chunk.Entity(row).Id;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniComponentRefQuery(MiniQueryCache query, MiniComponentType positionType, MiniComponentType velocityType)
    {
        var checksum = 0;
        var archetypes = query.GetArchetypeSpan();
        for (var archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            var archetype = archetypes[archetypeIndex];
            var posColIdx = archetype.GetComponentIndexFast(positionType);
            var velColIdx = archetype.GetComponentIndexFast(velocityType);
            ref var posBase = ref archetype.GetComponentRef<Position>(posColIdx);
            ref var velBase = ref archetype.GetComponentRef<Velocity>(velColIdx);
            var count = archetype.EntityCount;
            for (var row = count - 1; row >= 0; row--)
            {
                checksum += Unsafe.Add(ref posBase, row).X + Unsafe.Add(ref velBase, row).Y;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchComponentSpanQuery(ArchWorld world, ArchQueryDescription description)
    {
        var checksum = 0;
        var query = world.Query(in description);
        foreach (var chunk in query)
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var row = 0; row < positions.Length; row++)
            {
                checksum += positions[row].X + velocities[row].Y;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchEachSpanQuery(ArchWorld world, ArchQueryDescription description)
    {
        var checksum = 0;
        var query = world.Query(in description);
        foreach (var chunk in query)
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var row = 0; row < positions.Length; row++)
            {
                checksum += positions[row].X + velocities[row].Y;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchWideEachSpanQuery(ArchWorld world, ArchQueryDescription description)
    {
        var checksum = 0;
        var query = world.Query(in description);
        foreach (var chunk in query)
        {
            chunk.GetSpan<Position, Velocity, Health, Team, Acceleration, Mana>(
                out var positions, out var velocities, out var healths,
                out var teams, out var accelerations, out var manas);
            for (var row = 0; row < chunk.Count; row++)
            {
                checksum += positions[row].X
                          + velocities[row].Y
                          + healths[row].Value
                          + teams[row].Value
                          + accelerations[row].X
                          + manas[row].Value;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniWideComponentSpanQuery(MiniQueryCache query,
        MiniComponentType posType, MiniComponentType velType,
        MiniComponentType healthType, MiniComponentType teamType,
        MiniComponentType accelType, MiniComponentType manaType)
    {
        var checksum = 0;
        var archetypes = query.GetArchetypeSpan();
        for (var archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            var archetype = archetypes[archetypeIndex];
            var positions = archetype.GetComponentSpan<Position>(posType);
            var velocities = archetype.GetComponentSpan<Velocity>(velType);
            var healths = archetype.GetComponentSpan<Health>(healthType);
            var teams = archetype.GetComponentSpan<Team>(teamType);
            var accelerations = archetype.GetComponentSpan<Acceleration>(accelType);
            var manas = archetype.GetComponentSpan<Mana>(manaType);
            var count = archetype.EntityCount;
            for (var row = 0; row < count; row++)
            {
                checksum += positions[row].X
                          + velocities[row].Y
                          + healths[row].Value
                          + teams[row].Value
                          + accelerations[row].X
                          + manas[row].Value;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchWideComponentSpanQuery(ArchWorld world, ArchQueryDescription description)
    {
        var checksum = 0;
        var query = world.Query(in description);
        foreach (var chunk in query)
        {
            chunk.GetSpan<Position, Velocity, Health, Team, Acceleration, Mana>(
                out var positions, out var velocities, out var healths,
                out var teams, out var accelerations, out var manas);
            for (var row = 0; row < chunk.Count; row++)
            {
                checksum += positions[row].X
                          + velocities[row].Y
                          + healths[row].Value
                          + teams[row].Value
                          + accelerations[row].X
                          + manas[row].Value;
            }
        }

        return checksum;
    }

    private sealed class MiniSetSingleComponentThroughputCase : IThroughputCase
    {
        private readonly MiniWorldState _state;

        public MiniSetSingleComponentThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateMiniWorldWithPosition(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniSetSingle(_state.World, _state.Entities);
            }
        }

        public long RunIteration()
        {
            ExecuteMiniSetSingle(_state.World, _state.Entities);
            return (long)GetMiniSetSingle(_state.World, _state.Entities);
        }

        public void Dispose() { }
    }

    private sealed class ArchSetSingleComponentThroughputCase : IThroughputCase
    {
        private readonly ArchWorldState _state;

        public ArchSetSingleComponentThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateArchWorldWithPosition(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteArchSetSingle(_state.World, _state.Entities);
            }
        }

        public long RunIteration()
        {
            ExecuteArchSetSingle(_state.World, _state.Entities);
            return (long)GetArchSetSingle(_state.World, _state.Entities);
        }

        public void Dispose() => _state.Dispose();
    }

    private sealed class MiniSetTwoComponentsThroughputCase : IThroughputCase
    {
        private readonly MiniWorldState _state;

        public MiniSetTwoComponentsThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateMiniWorldWithPositionAndVelocity(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniSetTwo(_state.World, _state.Entities);
            }
        }

        public long RunIteration()
        {
            ExecuteMiniSetTwo(_state.World, _state.Entities);
            return (long)GetMiniSetTwo(_state.World, _state.Entities);
        }

        public void Dispose() { }
    }

    private sealed class ArchSetTwoComponentsThroughputCase : IThroughputCase
    {
        private readonly ArchWorldState _state;

        public ArchSetTwoComponentsThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateArchWorldWithPositionAndVelocity(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteArchSetTwo(_state.World, _state.Entities);
            }
        }

        public long RunIteration()
        {
            ExecuteArchSetTwo(_state.World, _state.Entities);
            return (long)GetArchSetTwo(_state.World, _state.Entities);
        }

        public void Dispose() => _state.Dispose();
    }

    private sealed class MiniCreateDestroyPairwiseThroughputCase : IThroughputCase
    {
        private readonly int _entityCount;
        private MiniWorld _world;

        public MiniCreateDestroyPairwiseThroughputCase(int entityCount)
        {
            _entityCount = entityCount;
            _world = new MiniWorld(entityCapacity: entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniCreateDestroyPairwise(_world, _entityCount);
            }
        }

        public long RunIteration() => ExecuteMiniCreateDestroyPairwise(_world, _entityCount);

        public void Dispose() { }
    }

    private sealed class ArchCreateDestroyPairwiseThroughputCase : IThroughputCase
    {
        private readonly int _entityCount;

        public ArchCreateDestroyPairwiseThroughputCase(int entityCount)
        {
            _entityCount = entityCount;
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var world = ArchWorld.Create();
                _ = ExecuteArchCreateDestroyPairwise(world, _entityCount);
            }
        }

        public long RunIteration()
        {
            using var world = ArchWorld.Create();
            return ExecuteArchCreateDestroyPairwise(world, _entityCount);
        }

        public void Dispose() { }
    }

    private sealed class MiniCreateDestroyPairwiseMultiThroughputCase : IThroughputCase
    {
        private readonly int _entityCount;
        private MiniWorld _world;

        public MiniCreateDestroyPairwiseMultiThroughputCase(int entityCount)
        {
            _entityCount = entityCount;
            _world = new MiniWorld(entityCapacity: entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniCreateDestroyPairwiseMulti(_world, _entityCount);
            }
        }

        public long RunIteration() => ExecuteMiniCreateDestroyPairwiseMulti(_world, _entityCount);

        public void Dispose() { }
    }

    private sealed class ArchCreateDestroyPairwiseMultiThroughputCase : IThroughputCase
    {
        private readonly int _entityCount;

        public ArchCreateDestroyPairwiseMultiThroughputCase(int entityCount)
        {
            _entityCount = entityCount;
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var world = ArchWorld.Create();
                _ = ExecuteArchCreateDestroyPairwiseMulti(world, _entityCount);
            }
        }

        public long RunIteration()
        {
            using var world = ArchWorld.Create();
            return ExecuteArchCreateDestroyPairwiseMulti(world, _entityCount);
        }

        public void Dispose() { }
    }

    private sealed class MiniCreateDestroyBatchThroughputCase : IThroughputCase
    {
        private readonly int _entityCount;
        private MiniWorld _world;

        public MiniCreateDestroyBatchThroughputCase(int entityCount)
        {
            _entityCount = entityCount;
            _world = new MiniWorld(entityCapacity: entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniCreateDestroyBatch(_world, _entityCount);
            }
        }

        public long RunIteration() => ExecuteMiniCreateDestroyBatch(_world, _entityCount);

        public void Dispose() { }
    }

    private sealed class ArchCreateDestroyBatchThroughputCase : IThroughputCase
    {
        private readonly int _entityCount;

        public ArchCreateDestroyBatchThroughputCase(int entityCount)
        {
            _entityCount = entityCount;
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var world = ArchWorld.Create();
                _ = ExecuteArchCreateDestroyBatch(world, _entityCount);
            }
        }

        public long RunIteration()
        {
            using var world = ArchWorld.Create();
            return ExecuteArchCreateDestroyBatch(world, _entityCount);
        }

        public void Dispose() { }
    }

    private sealed class ArchWideQueryEachSpanThroughputCase : IThroughputCase
    {
        private readonly ArchWideQueryWorldState _state;

        public ArchWideQueryEachSpanThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteArchWideEachSpanQuery(_state.World, _state.WideDescription);
            }
        }

        public long RunIteration() => ExecuteArchWideEachSpanQuery(_state.World, _state.WideDescription);

        public void Dispose() => _state.Dispose();
    }

    private sealed class MiniWideQueryComponentSpanThroughputCase : IThroughputCase
    {
        private readonly MiniWideQueryWorldState _state;

        public MiniWideQueryComponentSpanThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateMiniWideQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniWideComponentSpanQuery(
                    _state.WideQuery,
                    _state.PositionType, _state.VelocityType,
                    _state.HealthType, _state.TeamType,
                    _state.AccelerationType, _state.ManaType);
            }
        }

        public long RunIteration() => ExecuteMiniWideComponentSpanQuery(
            _state.WideQuery,
            _state.PositionType, _state.VelocityType,
            _state.HealthType, _state.TeamType,
            _state.AccelerationType, _state.ManaType);

        public void Dispose() { }
    }

    private sealed class ArchWideQueryComponentSpanThroughputCase : IThroughputCase
    {
        private readonly ArchWideQueryWorldState _state;

        public ArchWideQueryComponentSpanThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteArchWideComponentSpanQuery(_state.World, _state.WideDescription);
            }
        }

        public long RunIteration() => ExecuteArchWideComponentSpanQuery(_state.World, _state.WideDescription);

        public void Dispose() => _state.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniSetSingle(MiniWorld world, MiniEntity[] entities)
    {
        var checksum = 0;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Set(entities[i], new Position(i + 1, i + 1));
            checksum += 1;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float GetMiniSetSingle(MiniWorld world, MiniEntity[] entities)
    {
        float sum = 0;
        for (var i = 0; i < entities.Length; i++)
            sum += world.Get<Position>(entities[i]).X;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchSetSingle(ArchWorld world, ArchEntity[] entities)
    {
        var checksum = 0;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Set(entities[i], new Position(i + 1, i + 1));
            checksum += 1;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float GetArchSetSingle(ArchWorld world, ArchEntity[] entities)
    {
        float sum = 0;
        for (var i = 0; i < entities.Length; i++)
            sum += world.Get<Position>(entities[i]).X;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniSetTwo(MiniWorld world, MiniEntity[] entities)
    {
        var checksum = 0;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Set(entities[i], new Position(i + 1, i + 1));
            world.Set(entities[i], new Velocity(i + 2, i + 2));
            checksum += 1;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float GetMiniSetTwo(MiniWorld world, MiniEntity[] entities)
    {
        float sum = 0;
        for (var i = 0; i < entities.Length; i++)
            sum += world.Get<Position>(entities[i]).X;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchSetTwo(ArchWorld world, ArchEntity[] entities)
    {
        var checksum = 0;
        for (var i = 0; i < entities.Length; i++)
        {
            world.Set(entities[i], new Position(i + 1, i + 1));
            world.Set(entities[i], new Velocity(i + 2, i + 2));
            checksum += 1;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float GetArchSetTwo(ArchWorld world, ArchEntity[] entities)
    {
        float sum = 0;
        for (var i = 0; i < entities.Length; i++)
            sum += world.Get<Position>(entities[i]).X;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniCreateDestroyPairwise(MiniWorld world, int count)
    {
        var checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var entity = world.Create(new Position(i, i));
            world.Destroy(entity);
            checksum += entity.Id;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchCreateDestroyPairwise(ArchWorld world, int count)
    {
        var checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var entity = world.Create<Position>(new Position(i, i));
            world.Destroy(entity);
            checksum += entity.Id;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniCreateDestroyPairwiseMulti(MiniWorld world, int count)
    {
        var checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var phase = i & 3;
            MiniEntity entity;
            switch (phase)
            {
                case 0:
                    entity = world.Create(new Position(i, i));
                    break;
                case 1:
                    entity = world.Create(new Position(i, i), new Velocity(i, i));
                    break;
                case 2:
                    entity = world.Create(new Position(i, i), new Health(i));
                    break;
                default:
                    entity = world.Create(new Position(i, i), new Velocity(i, i), new Health(i));
                    break;
            }

            world.Destroy(entity);
            checksum += entity.Id;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchCreateDestroyPairwiseMulti(ArchWorld world, int count)
    {
        var checksum = 0;
        for (var i = 0; i < count; i++)
        {
            var phase = i & 3;
            ArchEntity entity;
            switch (phase)
            {
                case 0:
                    entity = world.Create<Position>(new Position(i, i));
                    break;
                case 1:
                    entity = world.Create<Position, Velocity>(new Position(i, i), new Velocity(i, i));
                    break;
                case 2:
                    entity = world.Create<Position, Health>(new Position(i, i), new Health(i));
                    break;
                default:
                    entity = world.Create<Position, Velocity, Health>(new Position(i, i), new Velocity(i, i), new Health(i));
                    break;
            }

            world.Destroy(entity);
            checksum += entity.Id;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteMiniCreateDestroyBatch(MiniWorld world, int count)
    {
        var entities = new MiniEntity[count];
        var checksum = 0;
        for (var i = 0; i < count; i++)
        {
            entities[i] = world.Create();
            checksum += entities[i].Id;
        }

        for (var i = 0; i < count; i++)
        {
            world.Destroy(entities[i]);
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ExecuteArchCreateDestroyBatch(ArchWorld world, int count)
    {
        var entities = new ArchEntity[count];
        var checksum = 0;
        for (var i = 0; i < count; i++)
        {
            entities[i] = world.Create();
            checksum += entities[i].Id;
        }

        for (var i = 0; i < count; i++)
        {
            world.Destroy(entities[i]);
        }

        return checksum;
    }
}
