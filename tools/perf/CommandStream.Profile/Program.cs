using System.Diagnostics;
using System.Runtime.CompilerServices;
using MiniArch;
using MiniArch.Core;

namespace CommandStreamProfile;

// ===================================================================
// Command-line arguments
// ===================================================================
internal sealed record Options(
    string? Scenario,
    int WarmupSec,
    int MeasureSec,
    bool List,
    string? ProfileReadyFile,
    string? ProfileStopFile)
{
    public static Options Parse(string[] args)
    {
        string? scenario = null;
        var warmup = 2;
        var measure = 5;
        var list = false;
        string? readyFile = null;
        string? stopFile = null;

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
                case "--profile-stop-file" when i + 1 < args.Length:
                    stopFile = args[++i];
                    break;
            }
        }

        return new Options(scenario, warmup, measure, list, readyFile, stopFile);
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
    double RecordMicrosecondsPerTick,
    double SubmitMicrosecondsPerTick,
    double SnapshotMicrosecondsPerTick,
    double ClearMicrosecondsPerTick,
    double RecordPercent,
    double SubmitPercent,
    double SnapshotPercent,
    double ClearPercent,
    int LiveCount,
    long HeapDelta,
    int GcCount,
    long Checksum);

internal static class BenchmarkRunner
{
    public static ScenarioResult Run(
        IScenario scenario,
        int warmupSec,
        int measureSec,
        string? profileReadyFile,
        string? profileStopFile)
    {
        // Warmup
        var warmupEnd = warmupSec > 0 ? Stopwatch.GetTimestamp() + (long)warmupSec * Stopwatch.Frequency : 0L;
        while (Stopwatch.GetTimestamp() < warmupEnd)
            scenario.RunTick();

        var startMem = GC.GetTotalMemory(false);
        var startGc = GC.CollectionCount(0);

        // In trace mode the marker writes are deliberately inside the measured
        // interval, so an accepted trace cannot contain samples outside it.
        var measureStart = Stopwatch.GetTimestamp();
        if (profileReadyFile != null)
            File.WriteAllText(profileReadyFile, Environment.ProcessId.ToString());

        // Measure
        var measureEnd = measureStart + (long)measureSec * Stopwatch.Frequency;
        long totalRecordNs = 0, totalSubmitNs = 0, totalSnapshotNs = 0, totalClearNs = 0;
        long tickCount = 0;

        while (Stopwatch.GetTimestamp() < measureEnd)
        {
            var (rNs, sNs, snNs, cNs) = scenario.RunTick();
            totalRecordNs += rNs;
            totalSubmitNs += sNs;
            totalSnapshotNs += snNs;
            totalClearNs += cNs;
            tickCount++;
        }

        if (profileStopFile != null)
            File.WriteAllText(profileStopFile, Stopwatch.GetTimestamp().ToString());
        var measureStop = Stopwatch.GetTimestamp();

        if (tickCount == 0)
            throw new InvalidOperationException("Measurement completed without executing a tick.");

        var endMem = GC.GetTotalMemory(false);
        var elapsedSec = (measureStop - measureStart) / (double)Stopwatch.Frequency;
        var ticksPerSec = tickCount / elapsedSec;
        var msPerTick = elapsedSec * 1000.0 / tickCount;

        var recordUsPerTick = totalRecordNs / (double)tickCount / 1_000.0;
        var submitUsPerTick = totalSubmitNs / (double)tickCount / 1_000.0;
        var snapshotUsPerTick = totalSnapshotNs / (double)tickCount / 1_000.0;
        var clearUsPerTick = totalClearNs / (double)tickCount / 1_000.0;

        var totalNs = totalRecordNs + totalSubmitNs + totalSnapshotNs + totalClearNs;
        var recordPct = totalNs > 0 ? totalRecordNs * 100.0 / totalNs : 0;
        var submitPct = totalNs > 0 ? totalSubmitNs * 100.0 / totalNs : 0;
        var snapshotPct = totalNs > 0 ? totalSnapshotNs * 100.0 / totalNs : 0;
        var clearPct = totalNs > 0 ? totalClearNs * 100.0 / totalNs : 0;

        var heapDelta = endMem - startMem;
        var gcCount = GC.CollectionCount(0) - startGc;
        var checksum = scenario.Checksum;

        return new ScenarioResult(
            scenario.Name, ticksPerSec, msPerTick,
            recordUsPerTick, submitUsPerTick, snapshotUsPerTick, clearUsPerTick,
            recordPct, submitPct, snapshotPct, clearPct,
            scenario.LiveCount, heapDelta, gcCount, checksum);
    }
}

// ===================================================================
// Common helpers for stopwatch-based phase timing
// ===================================================================
internal static class PhaseTimer
{
    // High-precision timestamp wrappers for phase boundaries.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ElapsedNs(long start, long end)
    {
        var elapsed = end - start;
        return elapsed / Stopwatch.Frequency * 1_000_000_000L
            + elapsed % Stopwatch.Frequency * 1_000_000_000L / Stopwatch.Frequency;
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

        return (PhaseTimer.ElapsedNs(t0, t1), PhaseTimer.ElapsedNs(t1, t2), 0, 0);
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: existing-set-multi
// Record Set<Position> across several interleaved archetypes, then Submit.
// ===================================================================
internal sealed class ExistingSetMultiScenario : IScenario
{
    private const int EntityCount = 10_000;

    private readonly World _world;
    private readonly CommandStream _stream;
    private readonly Entity[] _entities;
    private int _tick;

    public string Name => "existing-set-multi";
    public string Description => $"Record+Submit Set<Position> on {EntityCount} entities across 4 interleaved archetypes";
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

    public ExistingSetMultiScenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
        _entities = new Entity[EntityCount];
        for (var i = 0; i < EntityCount; i++)
        {
            _entities[i] = (i & 3) switch
            {
                0 => _world.Create(new Position { X = i, Y = i * 2 }),
                1 => _world.Create(new Position { X = i, Y = i * 2 }, new Velocity { X = 1, Y = -1 }),
                2 => _world.Create(new Position { X = i, Y = i * 2 }, new Health { Value = 100 }),
                _ => _world.Create(
                    new Position { X = i, Y = i * 2 },
                    new Velocity { X = 1, Y = -1 },
                    new Health { Value = 100 }),
            };
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

        return (PhaseTimer.ElapsedNs(t0, t1), PhaseTimer.ElapsedNs(t1, t2), 0, 0);
    }

    public void Dispose() => _world.Dispose();
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

    public long Checksum
    {
        get
        {
            long h = 0;
            for (var i = 0; i < _entities.Length && i < 100; i++)
            {
                var entity = _entities[i];
                h = HashCode.Combine(h, entity.Id, entity.Version, _world.Has<Velocity>(entity));
            }
            return h;
        }
    }

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

        return (PhaseTimer.ElapsedNs(t0, t1), PhaseTimer.ElapsedNs(t1, t2), 0, 0);
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: create-small4
// Create and Add 4 small-id components while replacing an entity pool.
// ===================================================================
internal sealed class CreateSmall4Scenario : IScenario
{
    private const int EntityPool = 2_000;
    private const int BatchSize = 500;

    private readonly World _world;
    private readonly CommandStream _stream;
    private readonly Entity[] _pool;
    private int _poolIndex;

    public string Name => "create-small4";
    public string Description => $"Create {BatchSize} + destroy {BatchSize} existing entities/tick with 4 small-id components, {EntityPool} live";
    public int LiveCount => _world.EntityCount;

    public long Checksum
    {
        get
        {
            long h = 0;
            for (var i = 0; i < _pool.Length && i < 100; i++)
            {
                var entity = _pool[i];
                var position = _world.Get<Position>(entity);
                h = HashCode.Combine(
                    h,
                    entity.Id,
                    entity.Version,
                    position.X,
                    position.Y,
                    _world.Get<Health>(entity).Value,
                    _world.Has<TagA>(entity),
                    _world.Has<TagB>(entity));
            }
            return h;
        }
    }

    public CreateSmall4Scenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
        _pool = new Entity[EntityPool];
        for (var i = 0; i < EntityPool; i++)
        {
            _pool[i] = _world.Create(
                new Position { X = i, Y = -i },
                new Health { Value = 100 },
                new TagA(),
                new TagB());
        }
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        var t0 = PhaseTimer.Start();
        for (var i = 0; i < BatchSize; i++)
        {
            var idx = (_poolIndex + i) % EntityPool;
            var oldEntity = _pool[idx];
            var newEntity = _stream.Create();
            _stream.Add(newEntity, new Position { X = i, Y = -i });
            _stream.Add(newEntity, new Health { Value = 100 });
            _stream.Add(newEntity, new TagA());
            _stream.Add(newEntity, new TagB());
            _stream.Destroy(oldEntity);
            _pool[idx] = newEntity;
        }
        _poolIndex = (_poolIndex + BatchSize) % EntityPool;
        var t1 = PhaseTimer.Start();

        _stream.Submit();
        var t2 = PhaseTimer.Start();

        return (PhaseTimer.ElapsedNs(t0, t1), PhaseTimer.ElapsedNs(t1, t2), 0, 0);
    }

    public void Dispose()
    {
        _world.Dispose();
    }
}

// ===================================================================
// Scenario: create-duplicates
// Same component written twice while replacing an entity pool.
// ===================================================================
internal sealed class CreateDuplicatesScenario : IScenario
{
    private const int EntityPool = 2_000;
    private const int BatchSize = 500;

    private readonly World _world;
    private readonly CommandStream _stream;
    private readonly Entity[] _pool;
    private int _poolIndex;

    public string Name => "create-duplicates";
    public string Description => $"Create {BatchSize} + destroy {BatchSize} existing entities/tick with overlapping Set<Position>, {EntityPool} live";
    public int LiveCount => _world.EntityCount;

    public long Checksum
    {
        get
        {
            long h = 0;
            for (var i = 0; i < _pool.Length && i < 100; i++)
            {
                var entity = _pool[i];
                var position = _world.Get<Position>(entity);
                h = HashCode.Combine(h, entity.Id, entity.Version, position.X, position.Y);
            }
            return h;
        }
    }

    public CreateDuplicatesScenario()
    {
        _world = new World();
        _stream = new CommandStream(_world);
        _pool = new Entity[EntityPool];
        for (var i = 0; i < EntityPool; i++)
        {
            _pool[i] = _world.Create(
                new Position { X = i + 1, Y = -i - 1 },
                new TagA(),
                new TagC());
        }
    }

    public (long recordNs, long submitNs, long snapshotNs, long clearNs) RunTick()
    {
        var t0 = PhaseTimer.Start();
        for (var i = 0; i < BatchSize; i++)
        {
            var idx = (_poolIndex + i) % EntityPool;
            var oldEntity = _pool[idx];
            var newEntity = _stream.Create();
            _stream.Add(newEntity, new Position { X = i, Y = -i });
            _stream.Set(newEntity, new Position { X = i + 1, Y = -i - 1 }); // overwrite last-wins
            _stream.Add(newEntity, new TagA());
            _stream.Add(newEntity, new TagC());
            _stream.Destroy(oldEntity);
            _pool[idx] = newEntity;
        }
        _poolIndex = (_poolIndex + BatchSize) % EntityPool;
        var t1 = PhaseTimer.Start();

        _stream.Submit();
        var t2 = PhaseTimer.Start();

        return (PhaseTimer.ElapsedNs(t0, t1), PhaseTimer.ElapsedNs(t1, t2), 0, 0);
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

    public long Checksum
    {
        get
        {
            long h = 0;
            for (var i = 0; i < _pool.Length && i < 100; i++)
            {
                var entity = _pool[i];
                var position = _world.Get<Position>(entity);
                h = HashCode.Combine(h, entity.Id, entity.Version, position.X, position.Y);
            }
            return h;
        }
    }

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

        return (PhaseTimer.ElapsedNs(t0, t1), PhaseTimer.ElapsedNs(t1, t2), 0, 0);
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
    private FrameDelta _lastSnapshot = new();
    private int _tick;

    public string Name => "snapshot-only";
    public string Description => $"Record {OpsPerTick} Set + Snapshot + Clear per tick (no Submit)";
    public int LiveCount => _entities.Length;

    public long Checksum => HashCode.Combine(_lastSnapshot.DeltaCount, _lastSnapshot.AsSpan().Length);

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

        _lastSnapshot = _stream.Snapshot();
        var t2 = PhaseTimer.Start();

        _stream.Clear();
        var t3 = PhaseTimer.Start();

        return (
            PhaseTimer.ElapsedNs(t0, t1),
            0,
            PhaseTimer.ElapsedNs(t1, t2),
            PhaseTimer.ElapsedNs(t2, t3));
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
            foreach (var registration in Registry.All)
            {
                using var scenario = registration.Factory();
                Console.WriteLine($"  {registration.Name,-24} {scenario.Description}");
            }
            Console.WriteLine();
            Console.WriteLine("Run all:  dotnet run -c Release --project tools/perf/CommandStream.Profile");
            Console.WriteLine("Run one:  dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4");
            Console.WriteLine("Profile:  dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4 --profile-ready-file profile.pid");
            return;
        }

        Console.WriteLine($"Process ID: {Environment.ProcessId}");
        Console.WriteLine();
        Console.WriteLine($"Warmup: {opts.WarmupSec}s, Measure: {opts.MeasureSec}s");
        Console.WriteLine();
        Console.WriteLine(
            $"{"Scenario",-24} {"Ticks/s",10} {"ms/tick",9} " +
            $"{"Rec us/t",9} {"Sub us/t",9} {"Snap us/t",9} {"Clr us/t",9} " +
            $"{"Rec%",7} {"Sub%",7} {"Snap%",7} {"Clr%",7} {"Checksum",12} {"Live",8} {"Heap Δ",10} {"GC",4}");
        Console.WriteLine(new string('-', 158));

        var matched = false;
        var firstScenario = true;
        var profileReadyFile = opts.ProfileReadyFile;
        var profileStopFile = opts.ProfileStopFile;
        foreach (var registration in Registry.All)
        {
            if (!string.IsNullOrEmpty(opts.Scenario) && registration.Name != opts.Scenario)
                continue;

            if (!firstScenario)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            firstScenario = false;

            using var scenario = registration.Factory();
            matched = true;
            var result = BenchmarkRunner.Run(
                scenario,
                opts.WarmupSec,
                opts.MeasureSec,
                profileReadyFile,
                profileStopFile);
            profileReadyFile = null;
            profileStopFile = null;
            Console.WriteLine(
                $"{result.Name,-24} {result.TicksPerSecond,10:F1} {result.MillisecondsPerTick,9:F4} " +
                $"{result.RecordMicrosecondsPerTick,9:F3} {result.SubmitMicrosecondsPerTick,9:F3} " +
                $"{result.SnapshotMicrosecondsPerTick,9:F3} {result.ClearMicrosecondsPerTick,9:F3} " +
                $"{result.RecordPercent,6:F1}% {result.SubmitPercent,6:F1}% {result.SnapshotPercent,6:F1}% {result.ClearPercent,6:F1}% " +
                $"{result.Checksum,12} {result.LiveCount,8:N0} {FormatBytes(result.HeapDelta),10} {result.GcCount,4}");
        }

        if (!matched)
        {
            Console.Error.WriteLine($"Unknown scenario '{opts.Scenario}'. Use --list to see available scenarios.");
            Environment.ExitCode = 1;
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
    public static IEnumerable<(string Name, Func<IScenario> Factory)> All
    {
        get
        {
            yield return ("existing-set", static () => new ExistingSetScenario());
            yield return ("existing-set-multi", static () => new ExistingSetMultiScenario());
            yield return ("existing-add-remove", static () => new ExistingAddRemoveScenario());
            yield return ("create-small4", static () => new CreateSmall4Scenario());
            yield return ("create-duplicates", static () => new CreateDuplicatesScenario());
            yield return ("create-destroy", static () => new CreateDestroyScenario());
            yield return ("snapshot-only", static () => new SnapshotOnlyScenario());
        }
    }
}
