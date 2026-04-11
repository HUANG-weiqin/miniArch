using System.Diagnostics;
using System.Reflection;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

using MiniQuery = MiniArch.Core.Query;
using MiniWorld = MiniArch.Core.World;

public enum QueryProfilingScenario
{
    WithAll,
    WithAllWithout,
    WithAllAny
}

public enum QueryProfilingTemperature
{
    Hot,
    Cold
}

public sealed record QueryProfilingOptions(
    QueryProfilingScenario Scenario,
    QueryProfilingTemperature Temperature,
    int EntityCount,
    TimeSpan Duration,
    int WarmupIterations,
    TimeSpan StartupDelay)
{
    public static QueryProfilingOptions Default { get; } = new(
        QueryProfilingScenario.WithAll,
        QueryProfilingTemperature.Cold,
        100_000,
        TimeSpan.FromSeconds(15),
        3,
        TimeSpan.FromSeconds(3));

    public static bool TryParse(string[] args, out QueryProfilingOptions options, out string? error)
    {
        options = Default;
        error = null;

        var scenario = options.Scenario;
        var temperature = options.Temperature;
        var entityCount = options.EntityCount;
        var duration = options.Duration;
        var warmupIterations = options.WarmupIterations;
        var startupDelay = options.StartupDelay;

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
                case "--scenario":
                    if (!TryParseScenario(value, out scenario))
                    {
                        error = $"Unsupported scenario '{value}'.";
                        return false;
                    }

                    break;
                case "--temperature":
                    if (!TryParseTemperature(value, out temperature))
                    {
                        error = $"Unsupported temperature '{value}'.";
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
                case "--startup-delay":
                    if (!int.TryParse(value, out var startupDelaySeconds) || startupDelaySeconds < 0)
                    {
                        error = $"Invalid startup delay '{value}'.";
                        return false;
                    }

                    startupDelay = TimeSpan.FromSeconds(startupDelaySeconds);
                    break;
                default:
                    error = $"Unknown argument '{arg}'.";
                    return false;
            }
        }

        options = new QueryProfilingOptions(scenario, temperature, entityCount, duration, warmupIterations, startupDelay);
        return true;
    }

    private static bool TryParseScenario(string value, out QueryProfilingScenario scenario)
    {
        scenario = value.ToLowerInvariant() switch
        {
            "with-all" => QueryProfilingScenario.WithAll,
            "with-all-without" => QueryProfilingScenario.WithAllWithout,
            "with-all-any" => QueryProfilingScenario.WithAllAny,
            _ => default
        };

        return value is "with-all" or "with-all-without" or "with-all-any";
    }

    private static bool TryParseTemperature(string value, out QueryProfilingTemperature temperature)
    {
        temperature = value.ToLowerInvariant() switch
        {
            "hot" => QueryProfilingTemperature.Hot,
            "cold" => QueryProfilingTemperature.Cold,
            _ => default
        };

        return value is "hot" or "cold";
    }
}

public readonly record struct QueryProfilingResult(
    long IterationCount,
    long TotalChecksum,
    TimeSpan Elapsed,
    long RefreshCount);

public static class QueryProfilingRunner
{
    private static readonly ConstructorInfo QueryConstructor = typeof(MiniQuery)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(ctor => ctor.GetParameters().Length == 2);

    private static readonly FieldInfo QueryFilterField = typeof(MiniQuery)
        .GetField("_filter", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Unable to find Query._filter.");

    public static int RunFromCommandLine(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (!QueryProfilingOptions.TryParse(args, out var options, out var error))
        {
            stderr.WriteLine(error);
            stderr.WriteLine("Usage: profile-query [--scenario with-all|with-all-without|with-all-any] [--temperature hot|cold] [--entity-count N] [--duration seconds] [--warmup count] [--startup-delay seconds]");
            return 1;
        }

        var result = Run(options, stdout, cancellationToken);
        stdout.WriteLine($"Completed iterations: {result.IterationCount}");
        stdout.WriteLine($"Total checksum: {result.TotalChecksum}");
        stdout.WriteLine($"Elapsed: {result.Elapsed.TotalMilliseconds:F0} ms");
        stdout.WriteLine($"Refresh count: {result.RefreshCount}");
        return 0;
    }

    public static QueryProfilingResult Run(QueryProfilingOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        Validate(options);

        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(options.EntityCount);
        var template = BuildQuery(state.World, options.Scenario);

        output.WriteLine("MiniArch query profiling workload");
        output.WriteLine($"PID: {Environment.ProcessId}");
        output.WriteLine($"Scenario: {FormatScenario(options.Scenario)}");
        output.WriteLine($"Temperature: {options.Temperature}");
        output.WriteLine($"EntityCount: {options.EntityCount}");
        output.WriteLine($"WarmupIterations: {options.WarmupIterations}");
        output.WriteLine($"DurationSeconds: {options.Duration.TotalSeconds:F0}");

        WarmUp(template, state.World, options, cancellationToken);

        if (options.StartupDelay > TimeSpan.Zero)
        {
            output.WriteLine($"Attach sampler now. Starting in {options.StartupDelay.TotalSeconds:F0}s...");
            Sleep(options.StartupDelay, cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        long iterations = 0;
        long totalChecksum = 0;
        long refreshCount = 0;

        while (stopwatch.Elapsed < options.Duration)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = CreateQueryForIteration(template, state.World, options.Temperature);
            var refreshBefore = query.RefreshCount;
            totalChecksum += Execute(query);
            refreshCount += query.RefreshCount - refreshBefore;
            iterations++;
        }

        stopwatch.Stop();
        return new QueryProfilingResult(iterations, totalChecksum, stopwatch.Elapsed, refreshCount);
    }

    private static void WarmUp(MiniQuery template, MiniWorld world, QueryProfilingOptions options, CancellationToken cancellationToken)
    {
        for (var i = 0; i < options.WarmupIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = CreateQueryForIteration(template, world, options.Temperature);
            Execute(query);
        }
    }

    private static MiniQuery CreateQueryForIteration(MiniQuery template, MiniWorld world, QueryProfilingTemperature temperature)
    {
        return temperature == QueryProfilingTemperature.Hot
            ? template
            : CloneFreshQuery(template, world);
    }

    private static MiniQuery CloneFreshQuery(MiniQuery template, MiniWorld world)
    {
        var filter = QueryFilterField.GetValue(template)
            ?? throw new InvalidOperationException("Unable to read Query._filter.");

        return (MiniQuery)QueryConstructor.Invoke(new object[] { world, filter });
    }

    private static MiniQuery BuildQuery(MiniWorld world, QueryProfilingScenario scenario)
    {
        var builder = world.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>();

        return scenario switch
        {
            QueryProfilingScenario.WithAll => builder.Build(),
            QueryProfilingScenario.WithAllWithout => builder.Without<ExcludedTag>().Build(),
            QueryProfilingScenario.WithAllAny => builder.Any<AnyTagA>().Or<AnyTagB>().Build(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static int Execute(MiniQuery query)
    {
        var checksum = 0;
        foreach (var chunk in query.Chunks)
        {
            for (var row = 0; row < chunk.Count; row++)
            {
                checksum += chunk.GetEntity(row).Id;
            }
        }

        return checksum;
    }

    private static string FormatScenario(QueryProfilingScenario scenario)
    {
        return scenario switch
        {
            QueryProfilingScenario.WithAll => "with-all",
            QueryProfilingScenario.WithAllWithout => "with-all-without",
            QueryProfilingScenario.WithAllAny => "with-all-any",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static void Validate(QueryProfilingOptions options)
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

        if (options.StartupDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "StartupDelay must be non-negative.");
        }
    }

    private static void Sleep(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        Task.Delay(duration, cancellationToken).GetAwaiter().GetResult();
    }
}
