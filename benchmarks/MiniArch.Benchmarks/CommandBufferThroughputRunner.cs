using System.Diagnostics;
using System.Threading;
using Arch.Core;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using ArchCommandBuffer = Arch.Buffer.CommandBuffer;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using MiniEntity = MiniArch.Entity;
using MiniWorld = MiniArch.World;

public enum CommandBufferEngine
{
    MiniCommandBuffer,
    MiniSyncSnapshot,
    MiniAsyncSubmitSnapshot,
    Arch,
}

public readonly record struct CommandBufferRunResult(
    CommandBufferEngine Engine,
    long IterationCount,
    TimeSpan Elapsed)
{
    public double OpsPerSecond => Elapsed.TotalSeconds <= 0
        ? 0
        : IterationCount / Elapsed.TotalSeconds;
}

public sealed record CommandBufferThroughputCase(
    string Name,
    CommandBufferBenchmarkScenario Scenario,
    int EntityCount);

public static class CommandBufferThroughputRunner
{
    public static int RunFromCommandLine(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var diag = false;
        var filteredArgs = new List<string>();
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--diag", StringComparison.OrdinalIgnoreCase))
            {
                diag = true;
            }
            else
            {
                filteredArgs.Add(arg);
            }
        }

        if (diag)
        {
            RunDiagnostic(stdout, cancellationToken);
            return 0;
        }

        if (Array.Exists(args, a => string.Equals(a, "--diag-gc", StringComparison.OrdinalIgnoreCase)))
        {
            RunGcDiagnostic(stdout, cancellationToken);
            return 0;
        }

        if (Array.Exists(args, a => string.Equals(a, "--diag-cont", StringComparison.OrdinalIgnoreCase)))
        {
            RunContinuousDiagnostic(stdout, cancellationToken);
            return 0;
        }

        if (Array.Exists(args, a => string.Equals(a, "--diag-mt", StringComparison.OrdinalIgnoreCase)))
        {
            RunMultiThreadedDiagnostic(stdout, cancellationToken);
            return 0;
        }

        if (Array.Exists(args, a => string.Equals(a, "--diag-compare", StringComparison.OrdinalIgnoreCase)))
        {
            RunGcComparisonDiagnostic(stdout, cancellationToken);
            return 0;
        }

        if (Array.Exists(args, a => string.Equals(a, "--diag-cont-compare", StringComparison.OrdinalIgnoreCase)))
        {
            RunContinuousGcComparison(stdout, cancellationToken);
            return 0;
        }

        if (Array.Exists(args, a => string.Equals(a, "--diag-snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            RunSnapshotDiagnostic(stdout, cancellationToken);
            return 0;
        }

        var durationSeconds = 3;
        var warmupCount = 3;
        var repeatCount = 5;

        for (var i = 0; i < filteredArgs.Count; i++)
        {
            if (filteredArgs[i] == "--duration" && i + 1 < filteredArgs.Count && int.TryParse(filteredArgs[++i], out var ds) && ds > 0)
            {
                durationSeconds = ds;
            }
            else if (filteredArgs[i] == "--warmup" && i + 1 < filteredArgs.Count && int.TryParse(filteredArgs[++i], out var w) && w >= 0)
            {
                warmupCount = w;
            }
            else if (filteredArgs[i] == "--repeat" && i + 1 < filteredArgs.Count && int.TryParse(filteredArgs[++i], out var r) && r > 0)
            {
                repeatCount = r;
            }
        }

        var duration = TimeSpan.FromSeconds(durationSeconds);
        var cases = new CommandBufferThroughputCase[]
        {
            new("1000/CreateHeavy", CommandBufferBenchmarkScenario.CreateHeavy, 1000),
            new("10000/CreateHeavy", CommandBufferBenchmarkScenario.CreateHeavy, 10000),
            new("10000/DenseExisting", CommandBufferBenchmarkScenario.DenseExisting, 10000),
            new("10000/MixedScript", CommandBufferBenchmarkScenario.MixedScript, 10000),
        };

        Run(cases, duration, warmupCount, repeatCount, stdout, cancellationToken);
        return 0;
    }

    public static void Run(
        CommandBufferThroughputCase[] cases,
        TimeSpan duration,
        int warmupCount,
        int repeatCount,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        output.WriteLine("Command-buffer throughput comparison (full frame: record+submit/playback, setup excluded)");
        output.WriteLine($"DurationSeconds: {duration.TotalSeconds:F0}, Warmup: {warmupCount}, Repeat: {repeatCount}");
        output.WriteLine();

        var engines = new[] { CommandBufferEngine.MiniCommandBuffer, CommandBufferEngine.MiniSyncSnapshot, CommandBufferEngine.MiniAsyncSubmitSnapshot, CommandBufferEngine.Arch };

        output.WriteLine($"{"Case",-28} {"Engine",-22} {"Median ops/s",14} {"Best ops/s",14}");
        output.WriteLine(new string('-', 80));

        foreach (var @case in cases)
        {
            var results = new List<(CommandBufferEngine Engine, double Median, double Best, string Detail)>();

            foreach (var engine in engines)
            {
                var runs = new List<CommandBufferRunResult>(repeatCount);
                for (var repeat = 0; repeat < repeatCount; repeat++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = ExecuteEngine(engine, @case.Scenario, @case.EntityCount, duration, warmupCount, cancellationToken);
                    runs.Add(result);
                }

                var opsPerSecond = runs.Select(r => r.OpsPerSecond).OrderBy(x => x).ToArray();
                var median = Median(opsPerSecond);
                var best = opsPerSecond[^1];
                results.Add((engine, median, best, FormatEngine(engine)));
                output.WriteLine($"{@case.Name,-28} {FormatEngine(engine),-22} {median,14:F0} {best,14:F0}");
            }

            var mini = results.FirstOrDefault(r => r.Engine == CommandBufferEngine.MiniCommandBuffer);
            var arch = results.FirstOrDefault(r => r.Engine == CommandBufferEngine.Arch);
            var miniAsync = results.FirstOrDefault(r => r.Engine == CommandBufferEngine.MiniAsyncSubmitSnapshot);
            var miniSync = results.FirstOrDefault(r => r.Engine == CommandBufferEngine.MiniSyncSnapshot);

            if (mini.Detail is not null && arch.Detail is not null)
            {
                var miniVsArch = ((mini.Median - arch.Median) / arch.Median) * 100d;
                output.WriteLine($"  Mini vs Arch: {miniVsArch:+0.0;-0.0;0.0}%");
            }

            if (miniSync.Detail is not null && mini.Detail is not null)
            {
                var syncVsMini = ((miniSync.Median - mini.Median) / mini.Median) * 100d;
                output.WriteLine($"  Sync+Snp vs Mini Submit: {syncVsMini:+0.0;-0.0;0.0}%");
            }

            if (miniAsync.Detail is not null && miniSync.Detail is not null)
            {
                var asyncVsSync = ((miniAsync.Median - miniSync.Median) / miniSync.Median) * 100d;
                output.WriteLine($"  Async vs Sync+Snp: {asyncVsSync:+0.0;-0.0;0.0}%");
            }

            output.WriteLine();
        }
    }

    private static void RunDiagnostic(TextWriter output, CancellationToken ct)
    {
        const int entityCount = 10000;
        const int warmupCount = 3;
        const int batchSize = 20;

        output.WriteLine("=== Diagnostic: 10000/DenseExisting split timing ===");
        output.WriteLine($"Warmup: {warmupCount}, Batch: {batchSize}");
        output.WriteLine();

        double miniRecordMs = 0, miniSubmitMs = 0;

        for (var w = 0; w < warmupCount; w++)
        {
            var ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(CommandBufferBenchmarkScenario.DenseExisting, entityCount);
            CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(ws, CommandBufferBenchmarkScenario.DenseExisting);

            var archW = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(CommandBufferBenchmarkScenario.DenseExisting, entityCount);
            try { CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(archW, CommandBufferBenchmarkScenario.DenseExisting); }
            finally { archW.Dispose(); }
        }

        for (var i = 0; i < batchSize; i++)
        {
            ct.ThrowIfCancellationRequested();

            {
                var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(CommandBufferBenchmarkScenario.DenseExisting, entityCount);
                var buffer = new CommandBuffer(state.World);
                var sw = Stopwatch.StartNew();
                for (var j = 0; j < state.ExistingEntities.Length; j++)
                {
                    var entity = state.ExistingEntities[j];
                    buffer.Set(entity, new BenchmarkPosition(j + 1, j + 2));
                    buffer.Set(entity, new BenchmarkVelocity(j + 3, j + 4));
                    buffer.Set(entity, new BenchmarkHealth(200 + j));
                    if ((j & 1) == 0) buffer.Remove<BenchmarkHealth>(entity);
                    else buffer.Add(entity, new BenchmarkArmor(300 + j));
                    if ((j & 7) == 0) buffer.Destroy(entity);
                }
                sw.Stop();
                miniRecordMs += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                buffer.Submit();
                sw.Stop();
                miniSubmitMs += sw.Elapsed.TotalMilliseconds;
            }
        }

        output.WriteLine($"  Mini CB record: {miniRecordMs / batchSize:F3} ms avg");
        output.WriteLine($"  Mini CB submit: {miniSubmitMs / batchSize:F3} ms avg");
        output.WriteLine();
    }

    private static CommandBufferRunResult ExecuteMini(
        CommandBufferBenchmarkScenario scenario, int entityCount, TimeSpan duration, int warmupCount, CancellationToken cancellationToken)
    {
        for (var w = 0; w < warmupCount; w++)
        {
            var warmupState = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
            CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(warmupState, scenario);
        }

        var iterations = 0L;
        var totalElapsed = TimeSpan.Zero;

        while (totalElapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
            var sw = Stopwatch.StartNew();
            CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(state, scenario);
            sw.Stop();
            totalElapsed += sw.Elapsed;
            iterations++;
        }

        return new CommandBufferRunResult(CommandBufferEngine.MiniCommandBuffer, iterations, totalElapsed);
    }

    private static CommandBufferRunResult ExecuteEngine(
        CommandBufferEngine engine,
        CommandBufferBenchmarkScenario scenario,
        int entityCount,
        TimeSpan duration,
        int warmupCount,
        CancellationToken cancellationToken)
    {
        return engine switch
        {
            CommandBufferEngine.MiniCommandBuffer => ExecuteMini(scenario, entityCount, duration, warmupCount, cancellationToken),
            CommandBufferEngine.MiniSyncSnapshot => ExecuteMiniSyncSnapshot(scenario, entityCount, duration, warmupCount, cancellationToken),
            CommandBufferEngine.MiniAsyncSubmitSnapshot => ExecuteMiniAsync(scenario, entityCount, duration, warmupCount, cancellationToken),
            CommandBufferEngine.Arch => ExecuteArch(scenario, entityCount, duration, warmupCount, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(engine))
        };
    }

    private static CommandBufferRunResult ExecuteMiniSyncSnapshot(
        CommandBufferBenchmarkScenario scenario, int entityCount, TimeSpan duration, int warmupCount, CancellationToken cancellationToken)
    {
        for (var w = 0; w < warmupCount; w++)
        {
            var warmupState = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
            var buffer = new CommandBuffer(warmupState.World);
            CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, warmupState, scenario);
            buffer.Snapshot();
            buffer.Submit();
        }

        var iterations = 0L;
        var totalElapsed = TimeSpan.Zero;

        while (totalElapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
            var buffer = new CommandBuffer(state.World);
            var sw = Stopwatch.StartNew();
            CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, state, scenario);
            buffer.Snapshot();
            buffer.Submit();
            sw.Stop();
            totalElapsed += sw.Elapsed;
            iterations++;
        }

        return new CommandBufferRunResult(CommandBufferEngine.MiniSyncSnapshot, iterations, totalElapsed);
    }

    private static CommandBufferRunResult ExecuteMiniAsync(
        CommandBufferBenchmarkScenario scenario, int entityCount, TimeSpan duration, int warmupCount, CancellationToken cancellationToken)
    {
        for (var w = 0; w < warmupCount; w++)
        {
            var warmupState = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
            var buffer = new CommandBuffer(warmupState.World);
            CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, warmupState, scenario);
            buffer.SubmitAndSnapshotAsync().Wait();
        }

        var iterations = 0L;
        var totalElapsed = TimeSpan.Zero;

        while (totalElapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
            var buffer = new CommandBuffer(state.World);
            var sw = Stopwatch.StartNew();
            CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, state, scenario);
            buffer.SubmitAndSnapshotAsync().Wait();
            sw.Stop();
            totalElapsed += sw.Elapsed;
            iterations++;
        }

        return new CommandBufferRunResult(CommandBufferEngine.MiniAsyncSubmitSnapshot, iterations, totalElapsed);
    }

    private static CommandBufferRunResult ExecuteArch(
        CommandBufferBenchmarkScenario scenario, int entityCount, TimeSpan duration, int warmupCount, CancellationToken cancellationToken)
    {
        for (var w = 0; w < warmupCount; w++)
        {
            var warmupState = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
            try
            {
                CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(warmupState, scenario);
            }
            finally
            {
                warmupState.Dispose();
            }
        }

        var iterations = 0L;
        var totalElapsed = TimeSpan.Zero;

        while (totalElapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
            var sw = Stopwatch.StartNew();
            CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(state, scenario);
            sw.Stop();
            totalElapsed += sw.Elapsed;
            state.Dispose();
            iterations++;
        }

        return new CommandBufferRunResult(CommandBufferEngine.Arch, iterations, totalElapsed);
    }

    private static double Median(double[] sorted)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length % 2 == 1) return sorted[sorted.Length / 2];
        return (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2d;
    }

    private static string FormatEngine(CommandBufferEngine engine) => engine switch
    {
        CommandBufferEngine.MiniCommandBuffer => "Mini Submit",
        CommandBufferEngine.MiniSyncSnapshot => "Mini Sync+Snp",
        CommandBufferEngine.MiniAsyncSubmitSnapshot => "Mini Async+Snp",
        CommandBufferEngine.Arch => "Arch CommandBuffer",
        _ => engine.ToString()
    };

    private static void RunGcDiagnostic(TextWriter output, CancellationToken ct)
    {
        const int entityCount = 10000;
        const int batchSize = 30;
        const int warmupCount = 5;

        var scenarios = new[] { CommandBufferBenchmarkScenario.DenseExisting, CommandBufferBenchmarkScenario.MixedScript };

        output.WriteLine("=== GC Diagnostic: FastCB Record/Submit split + GC stats ===");
        output.WriteLine($"EntityCount: {entityCount}, Warmup: {warmupCount}, Batch: {batchSize}");
        output.WriteLine();

        foreach (var scenario in scenarios)
        {
            output.WriteLine($"--- {scenario} ---");

            for (var w = 0; w < warmupCount; w++)
            {
                var ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(ws, scenario);
            }

            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();

            double totalRecordMs = 0, totalSubmitMs = 0;
            long totalGen0 = 0, totalGen1 = 0, totalGen2 = 0;
            long totalGen0Record = 0;
            long totalAllocatedBytes = 0;
            long totalAllocatedRecord = 0;

            for (var i = 0; i < batchSize; i++)
            {
                ct.ThrowIfCancellationRequested();

                var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                var buffer = new CommandBuffer(state.World);

                var gen0Before = GC.CollectionCount(0);
                var gen1Before = GC.CollectionCount(1);
                var gen2Before = GC.CollectionCount(2);
                var allocBefore = GC.GetTotalAllocatedBytes(true);

                var sw = Stopwatch.StartNew();
                CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, state, scenario);
                sw.Stop();
                var recordMs = sw.Elapsed.TotalMilliseconds;

                var gen0AfterRecord = GC.CollectionCount(0);
                var gen1AfterRecord = GC.CollectionCount(1);
                var gen2AfterRecord = GC.CollectionCount(2);
                var allocAfterRecord = GC.GetTotalAllocatedBytes(true);

                sw.Restart();
                buffer.Submit();
                sw.Stop();
                var submitMs = sw.Elapsed.TotalMilliseconds;

                var gen0After = GC.CollectionCount(0);
                var gen1After = GC.CollectionCount(1);
                var gen2After = GC.CollectionCount(2);
                var allocAfter = GC.GetTotalAllocatedBytes(true);

                totalRecordMs += recordMs;
                totalSubmitMs += submitMs;
                totalGen0Record += gen0AfterRecord - gen0Before;
                totalGen0 += gen0After - gen0Before;
                totalGen1 += gen1After - gen1Before;
                totalGen2 += gen2After - gen2Before;
                totalAllocatedRecord += allocAfterRecord - allocBefore;
                totalAllocatedBytes += allocAfter - allocBefore;
            }

            output.WriteLine($"  Record avg: {totalRecordMs / batchSize:F3} ms  (Gen0: {(double)totalGen0Record / batchSize:F1}, alloc: {totalAllocatedRecord / (double)batchSize / 1024:F0} KB)");
            output.WriteLine($"  Submit avg: {totalSubmitMs / batchSize:F3} ms");
            output.WriteLine($"  Total avg:  {(totalRecordMs + totalSubmitMs) / batchSize:F3} ms");
            output.WriteLine($"  Gen0 collections: {totalGen0} total, {(double)totalGen0 / batchSize:F1} per iter");
            output.WriteLine($"  Gen1 collections: {totalGen1} total, {(double)totalGen1 / batchSize:F1} per iter");
            output.WriteLine($"  Gen2 collections: {totalGen2} total, {(double)totalGen2 / batchSize:F1} per iter");
            output.WriteLine($"  Allocated: {totalAllocatedBytes / batchSize:N0} bytes per iter ({totalAllocatedBytes / (double)batchSize / 1024:F0} KB)");
            output.WriteLine();
        }
    }

    private static void RunSnapshotDiagnostic(TextWriter output, CancellationToken ct)
    {
        const int entityCount = 10000;
        const int warmupCount = 5;
        const int batchSize = 30;
        var scenarios = new[] { CommandBufferBenchmarkScenario.DenseExisting, CommandBufferBenchmarkScenario.MixedScript };

        output.WriteLine("=== Snapshot Diagnostic: Submit vs Submit+Snapshot vs SubmitAndSnapshotAsync ===");
        output.WriteLine($"EntityCount: {entityCount}, Warmup: {warmupCount}, Batch: {batchSize}");
        output.WriteLine();

        foreach (var scenario in scenarios)
        {
            output.WriteLine($"--- {scenario} ---");

            for (var w = 0; w < warmupCount; w++)
            {
                ct.ThrowIfCancellationRequested();
                var ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                var cb = new CommandBuffer(ws.World);
                CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(cb, ws, scenario);
                cb.Submit();

                ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                cb = new CommandBuffer(ws.World);
                CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(cb, ws, scenario);
                cb.Snapshot();
                cb.Submit();

                ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                cb = new CommandBuffer(ws.World);
                CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(cb, ws, scenario);
                cb.SubmitAndSnapshotAsync().Wait();
            }

            double submitOnlyMs = 0;
            double submitSnapshotMs = 0;
            double asyncSyncMs = 0;
            double asyncTotalMs = 0;

            for (var i = 0; i < batchSize; i++)
            {
                ct.ThrowIfCancellationRequested();

                {
                    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                    var buffer = new CommandBuffer(state.World);
                    CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, state, scenario);
                    var sw = Stopwatch.StartNew();
                    buffer.Submit();
                    sw.Stop();
                    submitOnlyMs += sw.Elapsed.TotalMilliseconds;
                }

                {
                    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                    var buffer = new CommandBuffer(state.World);
                    CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, state, scenario);
                    var sw = Stopwatch.StartNew();
                    buffer.Snapshot();
                    buffer.Submit();
                    sw.Stop();
                    submitSnapshotMs += sw.Elapsed.TotalMilliseconds;
                }

                {
                    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                    var buffer = new CommandBuffer(state.World);
                    CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(buffer, state, scenario);
                    var sw = Stopwatch.StartNew();
                    var task = buffer.SubmitAndSnapshotAsync();
                    var syncMs = sw.Elapsed.TotalMilliseconds;
                    task.Wait();
                    sw.Stop();
                    asyncSyncMs += syncMs;
                    asyncTotalMs += sw.Elapsed.TotalMilliseconds;
                }
            }

            var snapshotOverhead = submitSnapshotMs - submitOnlyMs;
            var asyncOverhead = asyncSyncMs - submitOnlyMs;

            output.WriteLine($"  Submit only:        {submitOnlyMs / batchSize:F3} ms avg");
            output.WriteLine($"  Submit + Snapshot:  {submitSnapshotMs / batchSize:F3} ms avg  (snapshot overhead: +{snapshotOverhead / batchSize:F3} ms)");
            output.WriteLine($"  Async sync path:    {asyncSyncMs / batchSize:F3} ms avg  (async overhead: +{asyncOverhead / batchSize:F3} ms)");
            output.WriteLine($"  Async total (await):{asyncTotalMs / batchSize:F3} ms avg");
            output.WriteLine();
        }
    }

    private static void RunMultiThreadedDiagnostic(TextWriter output, CancellationToken ct)
    {
        const int entityCount = 10000;
        const int warmupCount = 3;
        const int durationSeconds = 3;
        var threadCounts = new[] { 1, 2, 4 };
        var scenarios = new[] { CommandBufferBenchmarkScenario.CreateHeavy, CommandBufferBenchmarkScenario.DenseExisting, CommandBufferBenchmarkScenario.MixedScript };
        var duration = TimeSpan.FromSeconds(durationSeconds);

        output.WriteLine("=== Multi-threaded FastCB throughput (fixed duration) ===");
        output.WriteLine($"EntityCount: {entityCount}, Warmup: {warmupCount}, Duration: {durationSeconds}s");
        output.WriteLine();

        foreach (var scenario in scenarios)
        {
            output.WriteLine($"--- {scenario} ---");
            output.WriteLine($"{"Threads",-8} {"iterations",12} {"total ms",12} {"ops/s",12} {"speedup",10}");
            output.WriteLine(new string('-', 56));

            double singleThreadOps = 0;

            foreach (var threadCount in threadCounts)
            {
                for (var w = 0; w < warmupCount; w++)
                {
                    ct.ThrowIfCancellationRequested();
                    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                    RunMultiThreadedIteration(state, scenario, threadCount);
                }

                var iterations = 0L;
                var totalElapsed = TimeSpan.Zero;

                while (totalElapsed < duration)
                {
                    ct.ThrowIfCancellationRequested();
                    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                    var sw = Stopwatch.StartNew();
                    RunMultiThreadedIteration(state, scenario, threadCount);
                    sw.Stop();
                    totalElapsed += sw.Elapsed;
                    iterations++;
                }

                var totalMs = totalElapsed.TotalMilliseconds;
                var opsPerSec = iterations / totalElapsed.TotalSeconds;

                if (threadCount == 1) singleThreadOps = opsPerSec;
                var speedup = opsPerSec / singleThreadOps;

                output.WriteLine($"{threadCount,-8} {iterations,12} {totalMs,12:F1} {opsPerSec,12:F0} {speedup,10:F2}x");
            }

            output.WriteLine();
        }
    }

    private static void RunMultiThreadedIteration(MiniSharedCommandBufferState state, CommandBufferBenchmarkScenario scenario, int threadCount)
    {
        var buffers = new CommandBuffer[threadCount];
        for (var t = 0; t < threadCount; t++)
            buffers[t] = new CommandBuffer(state.World);

        switch (scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting:
                RunMultiThreadedDenseExisting(buffers, state);
                break;
            case CommandBufferBenchmarkScenario.CreateHeavy:
                RunMultiThreadedCreateHeavy(buffers, state);
                break;
            case CommandBufferBenchmarkScenario.MixedScript:
                RunMultiThreadedMixedScript(buffers, state);
                break;
        }

        foreach (var buffer in buffers)
            buffer.Submit();
    }

    private static void RunMultiThreadedDenseExisting(CommandBuffer[] buffers, MiniSharedCommandBufferState state)
    {
        var entityCount = state.EntityCount;
        var entities = state.ExistingEntities;
        var threadCount = buffers.Length;
        var perThread = entityCount / threadCount;
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var start = t * perThread;
            var count = t == threadCount - 1 ? entityCount - start : perThread;
            var buffer = buffers[t];
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    var idx = start + i;
                    var entity = entities[idx];
                    buffer.Set(entity, new BenchmarkPosition(idx + 1, idx + 2));
                    buffer.Set(entity, new BenchmarkVelocity(idx + 3, idx + 4));
                    buffer.Set(entity, new BenchmarkHealth(200 + idx));
                    if ((idx & 1) == 0) buffer.Remove<BenchmarkHealth>(entity);
                    else buffer.Add(entity, new BenchmarkArmor(300 + idx));
                    if ((idx & 7) == 0) buffer.Destroy(entity);
                }
            });
            threads[t].Start();
        }

        for (var t = 0; t < threadCount; t++)
            threads[t].Join();
    }

    private static void RunMultiThreadedCreateHeavy(CommandBuffer[] buffers, MiniSharedCommandBufferState state)
    {
        var entityCount = state.EntityCount;
        var threadCount = buffers.Length;
        var perThread = entityCount / threadCount;
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var start = t * perThread;
            var count = t == threadCount - 1 ? entityCount - start : perThread;
            var buffer = buffers[t];
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    var idx = start + i;
                    var entity = buffer.Create();
                    buffer.Add(entity, new BenchmarkPosition(idx + 1, idx + 2));
                    buffer.Add(entity, new BenchmarkVelocity(idx + 3, idx + 4));
                    buffer.Add(entity, new BenchmarkHealth(200 + idx));
                    if ((idx & 1) == 0) buffer.Remove<BenchmarkVelocity>(entity);
                    if ((idx & 3) == 0) buffer.Destroy(entity);
                }
            });
            threads[t].Start();
        }

        for (var t = 0; t < threadCount; t++)
            threads[t].Join();
    }

    private static void RunMultiThreadedMixedScript(CommandBuffer[] buffers, MiniSharedCommandBufferState state)
    {
        var entityCount = state.EntityCount;
        var entities = state.ExistingEntities;
        var threadCount = buffers.Length;
        var perThread = entityCount / threadCount;
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var start = t * perThread;
            var count = t == threadCount - 1 ? entityCount - start : perThread;
            var buffer = buffers[t];
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    var idx = start + i;
                    if ((idx & 1) == 0)
                    {
                        var entity = entities[idx];
                        buffer.Set(entity, new BenchmarkPosition(idx + 1, idx + 2));
                        buffer.Set(entity, new BenchmarkVelocity(idx + 3, idx + 4));
                        if ((idx & 3) == 0) buffer.Remove<BenchmarkHealth>(entity);
                        else buffer.Set(entity, new BenchmarkHealth(300 + idx));
                        if ((idx & 7) == 0) buffer.Destroy(entity);
                    }
                    else
                    {
                        var entity = buffer.Create();
                        buffer.Add(entity, new BenchmarkPosition(idx + 11, idx + 12));
                        buffer.Add(entity, new BenchmarkVelocity(idx + 13, idx + 14));
                        buffer.Add(entity, new BenchmarkHealth(400 + idx));
                        if ((idx & 3) == 1) buffer.Remove<BenchmarkVelocity>(entity);
                        if ((idx & 7) == 1) buffer.Destroy(entity);
                    }
                }
            });
            threads[t].Start();
        }

        for (var t = 0; t < threadCount; t++)
            threads[t].Join();
    }

    private static void RunGcComparisonDiagnostic(TextWriter output, CancellationToken ct)
    {
        const int entityCount = 10000;
        const int batchSize = 30;
        const int warmupCount = 5;

        var scenarios = new[] { CommandBufferBenchmarkScenario.DenseExisting, CommandBufferBenchmarkScenario.MixedScript, CommandBufferBenchmarkScenario.CreateHeavy };

        output.WriteLine("=== GC Comparison: Mini vs Arch (10000 entities) ===");
        output.WriteLine($"Warmup: {warmupCount}, Batch: {batchSize}");
        output.WriteLine();

        foreach (var scenario in scenarios)
        {
            output.WriteLine($"--- {scenario} ---");
            output.WriteLine($"{"",-22} {"Mini",14} {"Arch",14} {"Mini vs Arch",14}");
            output.WriteLine(new string('-', 66));

            for (var w = 0; w < warmupCount; w++)
            {
                var ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(ws, scenario);

                var archWs = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
                try { CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(archWs, scenario); }
                finally { archWs.Dispose(); }
            }

            double miniTotalMs = 0, archTotalMs = 0;
            long miniTotalAlloc = 0, archTotalAlloc = 0;
            int miniGen0 = 0, miniGen1 = 0, miniGen2 = 0;
            int archGen0 = 0, archGen1 = 0, archGen2 = 0;

            for (var i = 0; i < batchSize; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Mini
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();

                    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                    var gen0Before = GC.CollectionCount(0);
                    var gen1Before = GC.CollectionCount(1);
                    var gen2Before = GC.CollectionCount(2);
                    var allocBefore = GC.GetTotalAllocatedBytes(true);

                    var sw = Stopwatch.StartNew();
                    CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(state, scenario);
                    sw.Stop();

                    miniTotalMs += sw.Elapsed.TotalMilliseconds;
                    miniTotalAlloc += GC.GetTotalAllocatedBytes(true) - allocBefore;
                    miniGen0 += GC.CollectionCount(0) - gen0Before;
                    miniGen1 += GC.CollectionCount(1) - gen1Before;
                    miniGen2 += GC.CollectionCount(2) - gen2Before;
                }

                // Arch
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();

                    var archState = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
                    var gen0Before = GC.CollectionCount(0);
                    var gen1Before = GC.CollectionCount(1);
                    var gen2Before = GC.CollectionCount(2);
                    var allocBefore = GC.GetTotalAllocatedBytes(true);

                    var sw = Stopwatch.StartNew();
                    CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(archState, scenario);
                    sw.Stop();
                    archState.Dispose();

                    archTotalMs += sw.Elapsed.TotalMilliseconds;
                    archTotalAlloc += GC.GetTotalAllocatedBytes(true) - allocBefore;
                    archGen0 += GC.CollectionCount(0) - gen0Before;
                    archGen1 += GC.CollectionCount(1) - gen1Before;
                    archGen2 += GC.CollectionCount(2) - gen2Before;
                }
            }

            var miniTimePerIter = miniTotalMs / batchSize;
            var archTimePerIter = archTotalMs / batchSize;
            var timeRatio = (miniTimePerIter / archTimePerIter - 1) * 100;

            var miniKbPerIter = miniTotalAlloc / (double)batchSize / 1024;
            var archKbPerIter = archTotalAlloc / (double)batchSize / 1024;
            var allocRatio = (miniKbPerIter / archKbPerIter - 1) * 100;

            output.WriteLine($"{"Time (ms/iter)",-22} {miniTimePerIter,14:F3} {archTimePerIter,14:F3} {timeRatio,+13:F1}%");
            output.WriteLine($"{"Alloc (KB/iter)",-22} {miniKbPerIter,14:F0} {archKbPerIter,14:F0} {allocRatio,+13:F1}%");
            output.WriteLine($"{"Gen0/iter",-22} {(double)miniGen0 / batchSize,14:F2} {(double)archGen0 / batchSize,14:F2}");
            output.WriteLine($"{"Gen1/iter",-22} {(double)miniGen1 / batchSize,14:F2} {(double)archGen1 / batchSize,14:F2}");
            output.WriteLine($"{"Gen2/iter",-22} {(double)miniGen2 / batchSize,14:F2} {(double)archGen2 / batchSize,14:F2}");
            output.WriteLine();
        }
    }

    private static void RunContinuousGcComparison(TextWriter output, CancellationToken ct)
    {
        const int entityCount = 10000;
        const int batchSize = 100;
        const int warmupCount = 10;

        var scenarios = new[] { CommandBufferBenchmarkScenario.DenseExisting, CommandBufferBenchmarkScenario.MixedScript, CommandBufferBenchmarkScenario.CreateHeavy };

        output.WriteLine("=== Continuous GC Comparison: Mini vs Arch (no forced GC between iterations) ===");
        output.WriteLine($"EntityCount: {entityCount}, Warmup: {warmupCount}, Batch: {batchSize}");
        output.WriteLine();

        foreach (var scenario in scenarios)
        {
            output.WriteLine($"--- {scenario} ---");
            output.WriteLine($"{"",-18} {"Mini",12} {"Arch",12} {"Ratio",10}");
            output.WriteLine(new string('-', 54));

            // warmup both
            for (var w = 0; w < warmupCount; w++)
            {
                var ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(ws, scenario);
                var archWs = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
                try { CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(archWs, scenario); }
                finally { archWs.Dispose(); }
            }

            // Interleave Mini/Arch to avoid order bias: run half batches alternating
            long miniGen0 = 0, miniGen1 = 0, miniGen2 = 0, archGen0 = 0, archGen1 = 0, archGen2 = 0;
            long miniAlloc = 0, archAlloc = 0;
            double miniTimeMs = 0, archTimeMs = 0;
            var halfSize = batchSize / 2;

            for (var batch = 0; batch < 2; batch++)
            {
                ct.ThrowIfCancellationRequested();

                // Mini
                var g0 = GC.CollectionCount(0);
                var g1 = GC.CollectionCount(1);
                var g2 = GC.CollectionCount(2);
                var alloc = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < halfSize; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var s = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                    CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(s, scenario);
                }
                sw.Stop();
                miniTimeMs += sw.Elapsed.TotalMilliseconds;
                miniAlloc += GC.GetTotalAllocatedBytes(true) - alloc;
                miniGen0 += GC.CollectionCount(0) - g0;
                miniGen1 += GC.CollectionCount(1) - g1;
                miniGen2 += GC.CollectionCount(2) - g2;

                // Arch
                g0 = GC.CollectionCount(0);
                g1 = GC.CollectionCount(1);
                g2 = GC.CollectionCount(2);
                alloc = GC.GetTotalAllocatedBytes(true);
                sw = Stopwatch.StartNew();
                for (var i = 0; i < halfSize; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var s = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
                    CommandBufferBenchmarkScenarioFactory.RunArchSharedScenario(s, scenario);
                    s.Dispose();
                }
                sw.Stop();
                archTimeMs += sw.Elapsed.TotalMilliseconds;
                archAlloc += GC.GetTotalAllocatedBytes(true) - alloc;
                archGen0 += GC.CollectionCount(0) - g0;
                archGen1 += GC.CollectionCount(1) - g1;
                archGen2 += GC.CollectionCount(2) - g2;
            }

            var miniTime = miniTimeMs / batchSize;
            var archTime = archTimeMs / batchSize;
            var miniKb = miniAlloc / (double)batchSize / 1024;
            var archKb = archAlloc / (double)batchSize / 1024;
            var timeRatio = (miniTime / archTime - 1) * 100;
            var allocRatio = (miniKb / archKb - 1) * 100;

            output.WriteLine($"{"Time (ms)",-18} {miniTime,12:F3} {archTime,12:F3} {timeRatio,+9:F1}%");
            output.WriteLine($"{"Alloc (KB)",-18} {miniKb,12:F0} {archKb,12:F0} {allocRatio,+9:F1}%");
            output.WriteLine($"{"Ops/s",-18} {batchSize / (miniTimeMs / 1000d),12:F0} {batchSize / (archTimeMs / 1000d),12:F0}");
            output.WriteLine($"{"Gen0/iter",-18} {(double)miniGen0 / batchSize,12:F2} {(double)archGen0 / batchSize,12:F2}");
            output.WriteLine($"{"Gen1/iter",-18} {(double)miniGen1 / batchSize,12:F2} {(double)archGen1 / batchSize,12:F2}");
            output.WriteLine($"{"Gen2/iter",-18} {(double)miniGen2 / batchSize,12:F2} {(double)archGen2 / batchSize,12:F2}");
            output.WriteLine();
        }
    }

    private static void RunContinuousDiagnostic(TextWriter output, CancellationToken ct)
    {
        const int entityCount = 10000;
        const int batchSize = 100;
        const int warmupCount = 10;

        var scenarios = new[] { CommandBufferBenchmarkScenario.DenseExisting, CommandBufferBenchmarkScenario.MixedScript };

        output.WriteLine("=== Continuous Diagnostic: No forced GC, continuous iterations ===");
        output.WriteLine($"EntityCount: {entityCount}, Warmup: {warmupCount}, Batch: {batchSize}");
        output.WriteLine();

        foreach (var scenario in scenarios)
        {
            output.WriteLine($"--- {scenario} ---");

            for (var w = 0; w < warmupCount; w++)
            {
                var ws = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(ws, scenario);
            }

            var gen0Before = GC.CollectionCount(0);
            var gen1Before = GC.CollectionCount(1);
            var gen2Before = GC.CollectionCount(2);
            var allocBefore = GC.GetTotalAllocatedBytes(true);

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < batchSize; i++)
            {
                ct.ThrowIfCancellationRequested();
                var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
                CommandBufferBenchmarkScenarioFactory.RunMiniSharedScenario(state, scenario);
            }
            sw.Stop();

            var gen0After = GC.CollectionCount(0);
            var gen1After = GC.CollectionCount(1);
            var gen2After = GC.CollectionCount(2);
            var allocAfter = GC.GetTotalAllocatedBytes(true);

            var totalMs = sw.Elapsed.TotalMilliseconds;
            var perIterMs = totalMs / batchSize;
            var opsPerSec = batchSize / (totalMs / 1000d);

            output.WriteLine($"  Total: {totalMs:F1} ms, Per iter: {perIterMs:F3} ms, Ops/s: {opsPerSec:F0}");
            output.WriteLine($"  Gen0: {gen0After - gen0Before} ({(double)(gen0After - gen0Before) / batchSize:F1}/iter)");
            output.WriteLine($"  Gen1: {gen1After - gen1Before} ({(double)(gen1After - gen1Before) / batchSize:F1}/iter)");
            output.WriteLine($"  Gen2: {gen2After - gen2Before}");
            output.WriteLine($"  Allocated: {(allocAfter - allocBefore) / (double)batchSize / 1024:F0} KB/iter");
            output.WriteLine();
        }
    }
}
