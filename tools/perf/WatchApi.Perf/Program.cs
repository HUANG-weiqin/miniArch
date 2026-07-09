using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MiniArch;

namespace WatchApi.Perf;

// ── Component types used by API scenarios ────────────────────────

readonly struct CompInt : IEquatable<CompInt>
{
    public readonly int Value;
    public CompInt(int value) => Value = value;
    public bool Equals(CompInt other) => Value == other.Value;
}

readonly struct MarkerComp : IEquatable<MarkerComp>
{
    public readonly int Value;
    public MarkerComp(int value) => Value = value;
    public bool Equals(MarkerComp other) => Value == other.Value;
}

// ── Handler structs (must consume inputs to prevent JIT elision) ─

struct ChangeHandler : IChangeHandler<CompInt>
{
    public long Checksum;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void OnChange(World world, Entity entity, in CompInt oldValue, in CompInt newValue)
    {
        Checksum += entity.Id + (long)oldValue.Value + (long)newValue.Value;
    }
}

struct ProjectedChangeHandler : IChangeHandler<CompInt, int>
{
    public long Checksum;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Project(in CompInt component) => component.Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void OnChange(World world, Entity entity, int oldValue, int newValue)
    {
        Checksum += entity.Id + (long)oldValue + (long)newValue;
    }
}

struct TransHandler : ITransitionHandler
{
    public long Checksum;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void OnChange(World world, Entity entity, TransitionKind kind)
    {
        Checksum += entity.Id + (long)kind;
    }
}



// ── Program entry point ─────────────────────────────────────────

static class Program
{
    static void Main(string[] args)
    {
        var entityCount = 10000;
        var warmupSeconds = 1.0;
        var durationSeconds = 3.0;
        string? scenarioFilter = null;
        var listOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list":
                    listOnly = true;
                    break;
                case "--scenario" when i + 1 < args.Length:
                    scenarioFilter = args[++i];
                    break;
                case "--entity-count" when i + 1 < args.Length:
                    entityCount = int.Parse(args[++i]);
                    break;
                case "--warmup-seconds" when i + 1 < args.Length:
                    warmupSeconds = double.Parse(args[++i]);
                    break;
                case "--duration-seconds" when i + 1 < args.Length:
                    durationSeconds = double.Parse(args[++i]);
                    break;
                // Legacy flags — accepted but ignored (always seconds mode)
                case "--iterations" when i + 1 < args.Length:
                case "--warmup" when i + 1 < args.Length:
                    i++;
                    break;
            }
        }

        var scenarios = new (string Name, Action<int, double, double> Run)[]
        {
            ("change-quick-nochange", RunChangeQuickNoChange),
            ("change-quick-allchanged", RunChangeQuickAllChanged),
            ("change-projected-nochange", RunProjectedNoChange),
            ("change-projected-allchanged", RunProjectedAllChanged),
            ("transition-nochange", RunTransitionNoChange),
            ("transition-all-entered", RunTransitionAllEntered),
            ("transition-all-exited", RunTransitionAllExited),
            ("transition-churn-1pct", RunTransitionChurn1Pct),

        };

        if (listOnly)
        {
            Console.WriteLine("Available scenarios:");
            foreach (var (name, _) in scenarios)
                Console.WriteLine($"  {name}");
            return;
        }

        Console.WriteLine("=== WatchApi.Perf ===");
        Console.WriteLine($"Mode: duration | WarmupSeconds: {warmupSeconds:F1} | DurationSeconds: {durationSeconds:F1}");
        Console.WriteLine();

        foreach (var (name, run) in scenarios)
        {
            if (scenarioFilter != null &&
                !name.Contains(scenarioFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            run(entityCount, warmupSeconds, durationSeconds);
            Console.WriteLine();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    static void PrintResult(string name, int entities, long ops,
        double elapsedMs, long totalBytes, long checksum)
    {
        var opsPerSec = elapsedMs > 0 ? ops / (elapsedMs / 1000.0) : 0;
        var bytesPerOp = ops > 0 ? (double)totalBytes / ops : 0;

        Console.WriteLine(
            $"[{name,-30}] " +
            $"entities={entities,8}  " +
            $"ops={ops,8}  " +
            $"elapsed={elapsedMs,10:F2} ms  " +
            $"ops/s={opsPerSec,12:F1}  " +
            $"alloc={totalBytes,12} B  " +
            $"alloc/op={bytesPerOp,10:F1} B  " +
            $"cksum={checksum,12}");
    }

    static void GcCollectFull()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// Run <paramref name="step"/> repeatedly until <paramref name="seconds"/> have elapsed.
    /// Returns the number of steps completed.
    /// </summary>
    static long WarmupTime(Action step, double seconds)
    {
        if (seconds <= 0) return 0;
        var target = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long count = 0;
        while (Stopwatch.GetTimestamp() < target)
        {
            step();
            count++;
        }
        return count;
    }

    /// <summary>
    /// Run <paramref name="step"/> (which returns the checksum delta for this op)
    /// repeatedly until <paramref name="seconds"/> have elapsed.
    /// Returns (op count, elapsed ms, allocated bytes, total checksum).
    /// </summary>
    static (long Ops, double ElapsedMs, long AllocatedBytes, long Checksum) MeasureTime(
        Func<long> step, double seconds)
    {
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var startTicks = Stopwatch.GetTimestamp();
        var targetTicks = (long)(seconds * Stopwatch.Frequency);

        long checksum = 0;
        long ops = 0;

        while (Stopwatch.GetTimestamp() - startTicks < targetTicks)
        {
            checksum += step();
            ops++;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
        var afterBytes = GC.GetAllocatedBytesForCurrentThread();
        var elapsedMs = (double)elapsedTicks / Stopwatch.Frequency * 1000;

        return (ops, elapsedMs, afterBytes - beforeBytes, checksum);
    }

    // ── Scenario: change-quick-nochange ───────────────────────────

    static void RunChangeQuickNoChange(int entityCount, double warmupSeconds, double durationSeconds)
    {
        using var world = new World();
        var entities = new Entity[entityCount];
        for (var i = 0; i < entityCount; i++)
            entities[i] = world.Create(new CompInt(i));

        var handler = new ChangeHandler();
        var watch = world.Watch<CompInt, ChangeHandler>();
        watch.Handler = handler;

        // Warmup — stabilize internal buffers/HashSets
        WarmupTime(() =>
        {
            watch.Snapshot(world);
            watch.Diff(world);
            watch.Handler.Checksum = 0;
        }, warmupSeconds);

        GcCollectFull();

        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            watch.Snapshot(world);
            watch.Diff(world);
            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            return c;
        }, durationSeconds);

        PrintResult("change-quick-nochange", entityCount, ops, elapsedMs, allocBytes, checksum);
    }

    // ── Scenario: change-quick-allchanged ─────────────────────────

    static void RunChangeQuickAllChanged(int entityCount, double warmupSeconds, double durationSeconds)
    {
        using var world = new World();
        var entities = new Entity[entityCount];
        for (var i = 0; i < entityCount; i++)
            entities[i] = world.Create(new CompInt(0));

        var handler = new ChangeHandler();
        var watch = world.Watch<CompInt, ChangeHandler>();
        watch.Handler = handler;

        // Warmup — stabilize internal buffers and exercise all-changed path
        int warmupStep = 0;
        WarmupTime(() =>
        {
            watch.Snapshot(world);
            for (var e = 0; e < entityCount; e++)
                world.Set(entities[e], new CompInt(warmupStep + 1));
            watch.Diff(world);
            watch.Handler.Checksum = 0;
            warmupStep++;
        }, warmupSeconds);

        GcCollectFull();

        int measureStep = 0;
        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            watch.Snapshot(world);
            for (var e = 0; e < entityCount; e++)
                world.Set(entities[e], new CompInt(measureStep + 1));
            watch.Diff(world);
            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            measureStep++;
            return c;
        }, durationSeconds);

        PrintResult("change-quick-allchanged", entityCount, ops, elapsedMs, allocBytes, checksum);
    }

    // ── Scenario: change-projected-nochange ───────────────────────

    static void RunProjectedNoChange(int entityCount, double warmupSeconds, double durationSeconds)
    {
        using var world = new World();
        var entities = new Entity[entityCount];
        for (var i = 0; i < entityCount; i++)
            entities[i] = world.Create(new CompInt(i));

        var handler = new ProjectedChangeHandler();
        var watch = world.Watch<CompInt, int, ProjectedChangeHandler>();
        watch.Handler = handler;

        WarmupTime(() =>
        {
            watch.Snapshot(world);
            watch.Diff(world);
            watch.Handler.Checksum = 0;
        }, warmupSeconds);

        GcCollectFull();

        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            watch.Snapshot(world);
            watch.Diff(world);
            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            return c;
        }, durationSeconds);

        PrintResult("change-projected-nochange", entityCount, ops, elapsedMs, allocBytes, checksum);
    }

    // ── Scenario: change-projected-allchanged ─────────────────────

    static void RunProjectedAllChanged(int entityCount, double warmupSeconds, double durationSeconds)
    {
        using var world = new World();
        var entities = new Entity[entityCount];
        for (var i = 0; i < entityCount; i++)
            entities[i] = world.Create(new CompInt(0));

        var handler = new ProjectedChangeHandler();
        var watch = world.Watch<CompInt, int, ProjectedChangeHandler>();
        watch.Handler = handler;

        int warmupStep = 0;
        WarmupTime(() =>
        {
            watch.Snapshot(world);
            for (var e = 0; e < entityCount; e++)
                world.Set(entities[e], new CompInt(warmupStep + 1));
            watch.Diff(world);
            watch.Handler.Checksum = 0;
            warmupStep++;
        }, warmupSeconds);

        GcCollectFull();

        int measureStep = 0;
        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            watch.Snapshot(world);
            for (var e = 0; e < entityCount; e++)
                world.Set(entities[e], new CompInt(measureStep + 1));
            watch.Diff(world);
            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            measureStep++;
            return c;
        }, durationSeconds);

        PrintResult("change-projected-allchanged", entityCount, ops, elapsedMs, allocBytes, checksum);
    }

    // ── Scenario: transition-nochange ─────────────────────────────

    static void RunTransitionNoChange(int entityCount, double warmupSeconds, double durationSeconds)
    {
        using var world = new World();
        for (var i = 0; i < entityCount; i++)
            world.Create(new MarkerComp(i));

        var handler = new TransHandler();
        var watch = world.Watch<TransHandler>(new QueryDescription().With<MarkerComp>());
        watch.Handler = handler;

        WarmupTime(() =>
        {
            watch.Snapshot(world);
            watch.Diff(world);
            watch.Handler.Checksum = 0;
        }, warmupSeconds);

        GcCollectFull();

        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            watch.Snapshot(world);
            watch.Diff(world);
            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            return c;
        }, durationSeconds);

        PrintResult("transition-nochange", entityCount, ops, elapsedMs, allocBytes, checksum);
    }

    // ── Scenario: transition-all-entered ──────────────────────────

    static void RunTransitionAllEntered(int entityCount, double warmupSeconds, double durationSeconds)
    {
        using var world = new World();
        var entities = new Entity[entityCount];
        for (var i = 0; i < entityCount; i++)
            entities[i] = world.Create(new MarkerComp(i));

        var handler = new TransHandler();
        var watch = world.Watch<TransHandler>(new QueryDescription().With<MarkerComp>());
        watch.Handler = handler;

        // Each warmup iteration:
        //   1. Remove MarkerComp from all (they exit relative to next Snapshot)
        //   2. Snapshot baseline (0 matching)
        //   3. Add MarkerComp back to all (they enter)
        //   4. Diff → N entered
        WarmupTime(() =>
        {
            for (var e = 0; e < entityCount; e++)
                world.Remove<MarkerComp>(entities[e]);

            watch.Snapshot(world);

            for (var e = 0; e < entityCount; e++)
                world.Add(entities[e], new MarkerComp(0));

            watch.Diff(world);
            watch.Handler.Checksum = 0;
        }, warmupSeconds);

        GcCollectFull();

        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            for (var e = 0; e < entityCount; e++)
                world.Remove<MarkerComp>(entities[e]);

            watch.Snapshot(world);

            for (var e = 0; e < entityCount; e++)
                world.Add(entities[e], new MarkerComp(0));

            watch.Diff(world);
            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            return c;
        }, durationSeconds);

        PrintResult("transition-all-entered", entityCount, ops, elapsedMs, allocBytes, checksum);
    }

    // ── Scenario: transition-all-exited ───────────────────────────

    static void RunTransitionAllExited(int entityCount, double warmupSeconds, double durationSeconds)
    {
        using var world = new World();
        var entities = new Entity[entityCount];
        for (var i = 0; i < entityCount; i++)
            entities[i] = world.Create(new MarkerComp(i));

        var handler = new TransHandler();
        var watch = world.Watch<TransHandler>(new QueryDescription().With<MarkerComp>());
        watch.Handler = handler;

        // Each iteration:
        //   1. Snapshot baseline (N matching)
        //   2. Remove MarkerComp from all (they exit)
        //   3. Diff → N exited
        //   4. Add MarkerComp back (reset for next iteration)
        WarmupTime(() =>
        {
            watch.Snapshot(world);

            for (var e = 0; e < entityCount; e++)
                world.Remove<MarkerComp>(entities[e]);

            watch.Diff(world);

            for (var e = 0; e < entityCount; e++)
                world.Add(entities[e], new MarkerComp(0));

            watch.Handler.Checksum = 0;
        }, warmupSeconds);

        GcCollectFull();

        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            watch.Snapshot(world);

            for (var e = 0; e < entityCount; e++)
                world.Remove<MarkerComp>(entities[e]);

            watch.Diff(world);

            for (var e = 0; e < entityCount; e++)
                world.Add(entities[e], new MarkerComp(0));

            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            return c;
        }, durationSeconds);

        PrintResult("transition-all-exited", entityCount, ops, elapsedMs, allocBytes, checksum);
    }

    // ── Scenario: transition-churn-1pct ───────────────────────────

    static void RunTransitionChurn1Pct(int entityCount, double warmupSeconds, double durationSeconds)
    {
        var churnCount = Math.Max(1, entityCount / 100);

        using var world = new World();
        var entities = new Entity[entityCount + churnCount];
        for (var i = 0; i < entityCount; i++)
            entities[i] = world.Create(new MarkerComp(i));

        // Extra entities that don't have MarkerComp (they enter during churn)
        for (var i = 0; i < churnCount; i++)
            entities[entityCount + i] = world.Create(); // no MarkerComp

        var handler = new TransHandler();
        var watch = world.Watch<TransHandler>(new QueryDescription().With<MarkerComp>());
        watch.Handler = handler;

        // Each iteration:
        //   1. Snapshot baseline (entityCount matching from main pool)
        //   2. Remove from churnCount main entities (they exit)
        //   3. Add to churnCount extra entities (they enter)
        //   4. Diff → churnCount exited + churnCount entered
        //   5. Reset: re-add to main, remove from extra

        int warmupCursor = 0;
        WarmupTime(() =>
        {
            watch.Snapshot(world);

            for (var t = 0; t < churnCount; t++)
            {
                var idx = (warmupCursor + t) % entityCount;
                world.Remove<MarkerComp>(entities[idx]);
            }

            for (var t = 0; t < churnCount; t++)
                world.Add(entities[entityCount + t], new MarkerComp(0));

            watch.Diff(world);

            // Reset
            for (var t = 0; t < churnCount; t++)
            {
                var idx = (warmupCursor + t) % entityCount;
                world.Add(entities[idx], new MarkerComp(0));
            }

            for (var t = 0; t < churnCount; t++)
                world.Remove<MarkerComp>(entities[entityCount + t]);

            warmupCursor = (warmupCursor + churnCount) % entityCount;
            watch.Handler.Checksum = 0;
        }, warmupSeconds);

        GcCollectFull();

        int cursor = 0;
        var (ops, elapsedMs, allocBytes, checksum) = MeasureTime(() =>
        {
            watch.Snapshot(world);

            for (var t = 0; t < churnCount; t++)
            {
                var idx = (cursor + t) % entityCount;
                world.Remove<MarkerComp>(entities[idx]);
            }

            for (var t = 0; t < churnCount; t++)
                world.Add(entities[entityCount + t], new MarkerComp(0));

            watch.Diff(world);

            // Reset
            for (var t = 0; t < churnCount; t++)
            {
                var idx = (cursor + t) % entityCount;
                world.Add(entities[idx], new MarkerComp(0));
            }

            for (var t = 0; t < churnCount; t++)
                world.Remove<MarkerComp>(entities[entityCount + t]);

            cursor = (cursor + churnCount) % entityCount;

            var c = watch.Handler.Checksum;
            watch.Handler.Checksum = 0;
            return c;
        }, durationSeconds);

        PrintResult("transition-churn-1pct", entityCount, ops, elapsedMs, allocBytes, checksum);
    }




}
