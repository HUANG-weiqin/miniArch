using System.Diagnostics;
using System.Runtime.CompilerServices;
using Arch.Core;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using ArchQueryDescription = Arch.Core.QueryDescription;
using MiniQuery = MiniArch.Core.Query;
using MiniComponentType = MiniArch.Core.ComponentType;
using ArchWorld = Arch.Core.World;
using MiniWorld = MiniArch.World;

public enum ThroughputWorkload
{
    QueryWithAllEntity,
    QueryWithAllComponentSpan,
    WorldDelta,
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
            "world-delta" => ThroughputWorkload.WorldDelta,
            _ => default
        };

        return value is "query-with-all-entity" or "query-with-all-component-span" or "world-delta";
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
            stderr.WriteLine("Usage: throughput [--workload query-with-all-entity|query-with-all-component-span] [--engine miniarch|arch|both] [--entity-count N] [--duration seconds] [--warmup count] [--repeat count]");
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

                using var throughputCase = ThroughputCaseFactory.Create(options.Workload, engine, options.EntityCount);
                throughputCase.WarmUp(options.WarmupIterations, cancellationToken);
                var result = ExecuteCase(engine, throughputCase, options.Duration, cancellationToken);
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

    private static ThroughputRunResult ExecuteCase(ThroughputEngine engine, IThroughputCase throughputCase, TimeSpan duration, CancellationToken cancellationToken)
    {
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
            ThroughputWorkload.WorldDelta => "world-delta",
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

        if (options.Workload == ThroughputWorkload.WorldDelta && options.Engine != ThroughputEngine.MiniArch)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WorldDelta throughput currently supports MiniArch only.");
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
            (ThroughputWorkload.QueryWithAllComponentSpan, ThroughputEngine.MiniArch) => new MiniQueryComponentSpanThroughputCase(entityCount),
            (ThroughputWorkload.QueryWithAllComponentSpan, ThroughputEngine.Arch) => new ArchQueryComponentSpanThroughputCase(entityCount),
            (ThroughputWorkload.WorldDelta, ThroughputEngine.MiniArch) => new MiniWorldDeltaThroughputCase(entityCount),
            (_, ThroughputEngine.Both) => throw new InvalidOperationException("Throughput case factory expects a concrete engine."),
            _ => throw new ArgumentOutOfRangeException(nameof(workload))
        };
    }

    private sealed class MiniWorldDeltaThroughputCase : IThroughputCase
    {
        private readonly CommandBufferDeltaExecution _execution;

        public MiniWorldDeltaThroughputCase(int entityCount)
        {
            _execution = CommandBufferReplayScenarios.PrepareDelta(new CommandBufferReplayScenarioDefinition(
                "throughput-world-delta-mixed-heavy",
                CommandBufferReplayScenarioKind.MixedHeavy,
                entityCount));
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = RunIteration();
            }
        }

        public long RunIteration()
        {
            _execution.ApplyForward();
            var checksum = CountLiveEntities(_execution.CurrentWorld);
            _execution.ApplyBackward();
            return checksum;
        }

        public void Dispose()
        {
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

    private sealed class MiniQueryComponentSpanThroughputCase : IThroughputCase
    {
        private readonly MiniComplexQueryWorldState _state;

        public MiniQueryComponentSpanThroughputCase(int entityCount)
        {
            _state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(entityCount);
        }

        public void WarmUp(int count, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = ExecuteMiniComponentSpanQuery(_state.WithAllQuery, _state.PositionType, _state.VelocityType);
            }
        }

        public long RunIteration() => ExecuteMiniComponentSpanQuery(_state.WithAllQuery, _state.PositionType, _state.VelocityType);

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

    private static int ExecuteMiniEntityQuery(MiniQuery query)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var count = chunk.Count;
            var entities = chunk.GetEntityStorage();
            for (var row = 0; row < count; row++)
            {
                checksum += entities[row].Id;
            }
        }

        return checksum;
    }

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

    private static int ExecuteMiniComponentSpanQuery(MiniQuery query, MiniComponentType positionType, MiniComponentType velocityType)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var map = chunk.GetComponentIdToColumnMap();
            var posColIdx = map[positionType.Value];
            var velColIdx = map[velocityType.Value];
            ref var posBase = ref chunk.GetComponentRef<Position>(posColIdx);
            ref var velBase = ref chunk.GetComponentRef<Velocity>(velColIdx);
            var count = chunk.Count;
            for (var row = 0; row < count; row++)
            {
                checksum += Unsafe.Add(ref posBase, row).X + Unsafe.Add(ref velBase, row).Y;
            }
        }

        return checksum;
    }

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

    private static int CountLiveEntities(MiniWorld world)
    {
        var total = 0;
        var description = new MiniArch.QueryDescription();
        var query = MiniQuery.Create(world, in description);
        var chunks = query.GetChunkSpan();
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            total += chunks[chunkIndex].GetEntities().Length;
        }

        return total;
    }
}
