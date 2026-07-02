using System.Diagnostics;
using System.Runtime.CompilerServices;
using MiniArch;
using MiniArch.Core;

namespace CommandStreamProfile;

// ===================================================================
// Command-line arguments
// ===================================================================
internal sealed record Options(string? Scenario, int WarmupSec, int MeasureSec, bool List, string? ProfileReadyFile, int AttachDelaySec)
{
    public static Options Parse(string[] args)
    {
        string? scenario = null;
        var warmup = 2;
        var measure = 5;
        var list = false;
        string? readyFile = null;
        var attachDelay = 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario" or "-s" when i + 1 < args.Length:
                    scenario = args[++i];
                    break;
                case "--warmup" or "-w" when i + 1 < args.Length && int.TryParse(args[++i], out var w):
                    warmup = Math.Max(0, w);
                    break;
                case "--measure" or "-m" when i + 1 < args.Length && int.TryParse(args[++i], out var m):
                    measure = Math.Max(1, m);
                    break;
                case "--list" or "-l":
                    list = true;
                    break;
                case "--profile-ready-file" when i + 1 < args.Length:
                    readyFile = args[++i];
                    break;
                case "--attach-delay" when i + 1 < args.Length && int.TryParse(args[++i], out var d):
                    attachDelay = Math.Max(0, d);
                    break;
            }
        }

        return new Options(scenario, warmup, measure, list, readyFile, attachDelay);
    }
}

// ===================================================================
// Scenario interface
// ===================================================================
internal interface IScenario : IDisposable
{
    string Name { get; }
    string Description { get; }
    int LiveCount { get; }
    long Checksum { get; }
    /// <summary>Run one tick. Returns cumulative nanosecond counters for each phase.</summary>
    (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick();
}

// ===================================================================
// Shared component types
// ===================================================================
internal struct Position { public float X, Y; }
internal struct Velocity { public float X, Y; }
internal struct Health { public float Value; }
internal readonly record struct TagA;
internal readonly record struct TagB;
internal readonly record struct TagC;
internal readonly record struct TagD;

// ===================================================================
// Benchmark runner
// ===================================================================
internal sealed record ScenarioResult(
    string Name,
    double TicksPerSecond,
    double MillisecondsPerTick,
    double RecordPercent,
    double SubmitPercent,
    double SnapshotPercent,
    double ClearPercent,
    int LiveCount,
    long HeapDelta,
    int GcCount);

internal static class BenchmarkRunner
{
    private static readonly long SampleIntervalTicks = Stopwatch.Frequency / 100; // ~10ms samples

    public static ScenarioResult Run(IScenario scenario, int warmupSec, int measureSec)
    {
        var sw = new Stopwatch();

        // Warmup
        var warmupEnd = warmupSec > 0 ? Stopwatch.GetTimestamp() + (long)warmupSec * Stopwatch.Frequency : 0L;
        while (Stopwatch.GetTimestamp() < warmupEnd)
            scenario.RunTick();

        // Measure
        var measureEnd = Stopwatch.GetTimestamp() + (long)measureSec * Stopwatch.Frequency;
        long totalRecordNs = 0, totalSubmitNs = 0, totalSnapshotNs = 0, totalClearNs = 0;
        long tickCount = 0;
        long startMem = 0, endMem = 0;
        var startGc = GC.CollectionCount(0);
        var nextSample = Stopwatch.GetTimestamp() + SampleIntervalTicks;

        while (Stopwatch.GetTimestamp() < measureEnd)
        {
            var (rNs, sNs, snNs, cNs) = scenario.RunTick();
            totalRecordNs += rNs;
            totalSubmitNs += sNs;
            totalSnapshotNs += snNs;
            totalClearNs += cNs;
            tickCount++;

            // Sample memory at roughly regular intervals (not per-tick to avoid overhead)
            if (Stopwatch.GetTimestamp() >= nextSample)
            {
                endMem = GC.GetTotalMemory(false);
                nextSample = Stopwatch.GetTimestamp() + SampleIntervalTicks;
            }
        }

        if (tickCount == 0)
            return new ScenarioResult(scenario.Name, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var elapsedSec = measureSec;
        var ticksPerSec = tickCount / (double)elapsedSec;
        var msPerTick = (elapsedSec * 1000.0) / tickCount;

        var totalNs = totalRecordNs + totalSubmitNs + totalSnapshotNs + totalClearNs;
        var recordPct = totalNs > 0 ? totalRecordNs * 100.0 / totalNs : 0;
        var submitPct = totalNs > 0 ? totalSubmitNs * 100.0 / totalNs : 0;
        var snapshotPct = totalNs > 0 ? totalSnapshotNs * 100.0 / totalNs : 0;
        var clearPct = totalNs > 0 ? totalClearNs * 100.0 / totalNs : 0;

        var heapDelta = endMem - startMem;
        var gcCount = GC.CollectionCount(0) - startGc;

        return new ScenarioResult(
            scenario.Name, ticksPerSec, msPerTick,
            recordPct, submitPct, snapshotPct, clearPct,
            scenario.LiveCount, heapDelta, gcCount);
    }
}

// ===================================================================
// Common helpers for stopwatch-based phase timing
// ===================================================================
internal static class PhaseTimer
{
    // High-precision wrappers — subtract the overhead of StartNew/Elapsed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long EndNs(long start)
    {
        var elapsed = Stopwatch.GetTimestamp() - start;
        return elapsed * 1_000_000L / Stopwatch.Frequency;
    }
}

// ===================================================================
// Scenario: existing-set
// Record Set<Position> on N existing entities, then Submit.
// ===================================================================
internal sealed class ExistingSetScenario : IScenario
{
    private const int EntityCount = 10_000;

    private readonly World _world;
    private readonly CommandStream _stream;
    private readonly Entity[] _entities;
    private int _tick;

    public string Name => "existing-set";
    public string Description => $"Record+Submit Set<Position> on {EntityCount} entities per tick";
    public int LiveCount => _entities.Length;

    public long Checksum
    {
        get
        {
            long h = 0;
            for (var i = 0; i < _entities.Length && i < 100; i++)
                h = HashCode.Combine(h, _world.Get<Position>(_entities[i]).X);
            return h;
        }
    }

    public ExistingSetScenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
        _entities = new Entity[EntityCount];
        for (var i = 0; i < EntityCount; i++)
        {
            var e = _world.Create(new Position { X = i, Y = i * 2 });
            _entities[i] = e;
        }
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        _tick++;

        var t0 = PhaseTimer.Start();
        for (var i = 0; i < _entities.Length; i++)
            _stream.Set(_entities[i], new Position { X = _tick + i, Y = _tick - i });
        var t1 = PhaseTimer.Start();

        _stream.Submit();
        var t2 = PhaseTimer.Start();

        return (PhaseTimer.EndNs(t0), PhaseTimer.EndNs(t1), 0, PhaseTimer.EndNs(t2));
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: existing-add-remove
// Add Velocity to half, Remove from other half, then Submit.
// ===================================================================
internal sealed class ExistingAddRemoveScenario : IScenario
{
    private const int EntityCount = 10_000;

    private readonly World _world;
    private readonly CommandStream _stream;
    private readonly Entity[] _entities;
    private int _tick;

    public string Name => "existing-add-remove";
    public string Description => $"Record+Submit Add/Remove<Velocity> on {EntityCount} entities alternating per tick";
    public int LiveCount => _entities.Length;

    public long Checksum => _entities.Length;

    public ExistingAddRemoveScenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
        _entities = new Entity[EntityCount];
        for (var i = 0; i < EntityCount; i++)
        {
            var e = _world.Create(new Position { X = i, Y = i * 2 }, new Health { Value = 100 });
            _entities[i] = e;
        }
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        _tick++;
        var half = EntityCount / 2;

        var t0 = PhaseTimer.Start();
        // Even indices: Add Velocity; odd indices: Remove Velocity (swap every tick)
        if ((_tick & 1) == 0)
        {
            for (var i = 0; i < half; i++)
                _stream.Add(_entities[i], new Velocity { X = _tick, Y = -_tick });
            for (var i = half; i < EntityCount; i++)
                _stream.Remove<Velocity>(_entities[i]);
        }
        else
        {
            for (var i = 0; i < half; i++)
                _stream.Remove<Velocity>(_entities[i]);
            for (var i = half; i < EntityCount; i++)
                _stream.Add(_entities[i], new Velocity { X = _tick, Y = -_tick });
        }
        var t1 = PhaseTimer.Start();

        _stream.Submit();
        var t2 = PhaseTimer.Start();

        return (PhaseTimer.EndNs(t0), PhaseTimer.EndNs(t1), 0, PhaseTimer.EndNs(t2));
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: create-small4
// Create and Add 4 small-id components per entity via mask path.
// ===================================================================
internal sealed class CreateSmall4Scenario : IScenario
{
    private const int BatchSize = 500;

    private readonly World _world;
    private readonly CommandStream _stream;

    public string Name => "create-small4";
    public string Description => $"Create {BatchSize} entities/tick with 4 small-id components (Position+Vitality+TagA+TagB)";
    public int LiveCount => _world.EntityCount;

    public long Checksum => _stream.GetHashCode();

    public CreateSmall4Scenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        var t0 = PhaseTimer.Start();
        for (var i = 0; i < BatchSize; i++)
        {
            var e = _stream.Create();
            _stream.Add(e, new Position { X = i, Y = -i });
            _stream.Add(e, new Health { Value = 100 });
            _stream.Add(e, new TagA());
            _stream.Add(e, new TagB());
        }
        var t1 = PhaseTimer.Start();

        _stream.Submit();
        var t2 = PhaseTimer.Start();

        return (PhaseTimer.EndNs(t0), PhaseTimer.EndNs(t1), 0, PhaseTimer.EndNs(t2));
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: create-duplicates
// Same component written twice to same pending entity → tests dedup path.
// ===================================================================
internal sealed class CreateDuplicatesScenario : IScenario
{
    private const int BatchSize = 500;

    private readonly World _world;
    private readonly CommandStream _stream;

    public string Name => "create-duplicates";
    public string Description => $"Create {BatchSize} entities/tick with overlapping Set<Position> to exercise dedup";
    public int LiveCount => _world.EntityCount;

    public long Checksum => _stream.GetHashCode();

    public CreateDuplicatesScenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        var t0 = PhaseTimer.Start();
        for (var i = 0; i < BatchSize; i++)
        {
            var e = _stream.Create();
            _stream.Add(e, new Position { X = i, Y = -i });
            _stream.Set(e, new Position { X = i + 1, Y = -i - 1 }); // overwrite last-wins
            _stream.Add(e, new TagA());
            _stream.Add(e, new TagC());
        }
        var t1 = PhaseTimer.Start();

        _stream.Submit();
        var t2 = PhaseTimer.Start();

        return (PhaseTimer.EndNs(t0), PhaseTimer.EndNs(t1), 0, PhaseTimer.EndNs(t2));
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: create-destroy
// Each tick: create N, then destroy the oldest N (via stream → cancel path).
// Keeps entity count flat.
// ===================================================================
internal sealed class CreateDestroyScenario : IScenario
{
    private const int EntityPool = 2_000;
    private const int BatchSize = 500;

    private readonly World _world;
    private readonly CommandStream _stream;
    private readonly Entity[] _pool;
    private int _poolIndex;

    public string Name => "create-destroy";
    public string Description => $"Create {BatchSize} + destroy {BatchSize} existing per tick, steady-state";
    public int LiveCount => _world.EntityCount;

    public long Checksum => _stream.GetHashCode();

    public CreateDestroyScenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
        _pool = new Entity[EntityPool];
        for (var i = 0; i < EntityPool; i++)
        {
            var e = _world.Create(new Position { X = i, Y = -i }, new Health { Value = 100 });
            _pool[i] = e;
        }
        _poolIndex = 0;
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        var t0 = PhaseTimer.Start();

        // Replace oldest entities in the pool with newly created pending entities.
        for (var i = 0; i < BatchSize; i++)
        {
            var idx = (_poolIndex + i) % EntityPool;
            var oldEntity = _pool[idx];
            var newEntity = _stream.Create();
            _stream.Add(newEntity, new Position { X = _poolIndex + i, Y = -_poolIndex - i });
            _stream.Add(newEntity, new Health { Value = 100 });

            _stream.Destroy(oldEntity);
            _pool[idx] = newEntity;
        }
        _poolIndex = (_poolIndex + BatchSize) % EntityPool;

        var t1 = PhaseTimer.Start();

        _stream.Submit();
        var t2 = PhaseTimer.Start();

        return (PhaseTimer.EndNs(t0), PhaseTimer.EndNs(t1), 0, PhaseTimer.EndNs(t2));
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: snapshot-only
// Record Set then Snapshot + Clear (no Submit).
// ===================================================================
internal sealed class SnapshotOnlyScenario : IScenario
{
    private const int EntityCount = 10_000;
    private const int OpsPerTick = 1_000;

    private readonly World _world;
    private readonly CommandStream _stream;
    private readonly Entity[] _entities;
    private int _tick;

    public string Name => "snapshot-only";
    public string Description => $"Record {OpsPerTick} Set + Snapshot + Clear per tick (no Submit)";
    public int LiveCount => _entities.Length;

    public long Checksum => _stream.GetHashCode();

    public SnapshotOnlyScenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
        _entities = new Entity[EntityCount];
        for (var i = 0; i < EntityCount; i++)
        {
            var e = _world.Create(new Position { X = i, Y = i * 2 }, new Health { Value = 100 });
            _entities[i] = e;
        }
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        _tick++;

        var t0 = PhaseTimer.Start();
        for (var i = 0; i < OpsPerTick; i++)
            _stream.Set(_entities[i], new Position { X = _tick + i, Y = _tick - i });
        var t1 = PhaseTimer.Start();

        _stream.Snapshot();
        var t2 = PhaseTimer.Start();

        _stream.Clear();
        var t3 = PhaseTimer.Start();

        return (PhaseTimer.EndNs(t0), 0, PhaseTimer.EndNs(t1), PhaseTimer.EndNs(t2));
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Entry point
// ===================================================================
public static class Program
{
    public static void Main(string[] args)
    {
        var opts = Options.Parse(args);

        if (opts.List)
        {
            Console.WriteLine("Available scenarios:");
            foreach (var s in Registry.All.OrderBy(s => s.Name))
                Console.WriteLine($"  {s.Name,-24} {s.Description}");
            Console.WriteLine();
            Console.WriteLine("Run all:  dotnet run -c Release --project tools/perf/CommandStream.Profile");
            Console.WriteLine("Run one:  dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4");
            Console.WriteLine("Profile:  dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4 --profile-ready-file profile.pid --attach-delay 3");
            return;
        }

        var all = Registry.All.ToArray();
        var scenarios = string.IsNullOrEmpty(opts.Scenario)
            ? all
            : all.Where(s => s.Name == opts.Scenario).ToArray();

        if (scenarios.Length == 0)
        {
            Console.Error.WriteLine($"Unknown scenario '{opts.Scenario}'. Use --list to see available scenarios.");
            Environment.Exit(1);
        }

        // Print PID and optionally signal external profiler
        Console.WriteLine($"Process ID: {Environment.ProcessId}");
        if (opts.ProfileReadyFile != null)
        {
            File.WriteAllText(opts.ProfileReadyFile, Environment.ProcessId.ToString());
            if (opts.AttachDelaySec > 0)
            {
                Console.WriteLine($"Waiting {opts.AttachDelaySec}s for profiler to attach...");
                Thread.Sleep(opts.AttachDelaySec * 1000);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Warmup: {opts.WarmupSec}s, Measure: {opts.MeasureSec}s");
        Console.WriteLine();
        Console.WriteLine($"{"Scenario",-24} {"Ticks/s",10} {"ms/tick",9} {"Record%",8} {"Submit%",9} {"Snap%",8} {"Clear%",8} {"Live",8} {"Heap Δ",10} {"GC",6}");
        Console.WriteLine(new string('-', 108));

        foreach (var scenario in scenarios)
        {
            using var sc = scenario;
            var result = BenchmarkRunner.Run(sc, opts.WarmupSec, opts.MeasureSec);
            Console.WriteLine(
                $"{result.Name,-24} {result.TicksPerSecond,10:F1} {result.MillisecondsPerTick,9:F4} " +
                $"{result.RecordPercent,7:F1}% {result.SubmitPercent,7:F1}% {result.SnapshotPercent,7:F1}% {result.ClearPercent,7:F1}% " +
                $"{result.LiveCount,8:N0} {FormatBytes(result.HeapDelta),10} {result.GcCount,6}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (Math.Abs(bytes) >= 1024L * 1024 * 100)
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        if (Math.Abs(bytes) >= 1024 * 100)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes:N0} B";
    }
}

// ===================================================================
// Scenario registry
// ===================================================================
internal static class Registry
{
    public static IEnumerable<IScenario> All
    {
        get
        {
            yield return new ExistingSetScenario();
            yield return new ExistingAddRemoveScenario();
            yield return new CreateSmall4Scenario();
            yield return new CreateDuplicatesScenario();
            yield return new CreateDestroyScenario();
            yield return new SnapshotOnlyScenario();
        }
    }
}
