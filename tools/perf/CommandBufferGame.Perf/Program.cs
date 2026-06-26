using System.Diagnostics;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using MiniCommandStream = MiniArch.Core.CommandStream;
using MiniEntity = MiniArch.Entity;
using MiniQuery = MiniArch.Query;
using MiniQueryDescription = MiniArch.QueryDescription;
using MiniWorld = MiniArch.World;
using ArchCommandBuffer = Arch.Buffer.CommandBuffer;
using ArchComponentType = Arch.Core.ComponentType;
using ArchEntity = Arch.Core.Entity;
using ArchQueryDescription = Arch.Core.QueryDescription;
using ArchWorld = Arch.Core.World;
using FrifloEntity = Friflo.Engine.ECS.Entity;

namespace CommandBufferGamePerf;

public static class Program
{
    public static void Main(string[] args)
    {
        var options = BenchmarkOptions.Parse(args);

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        static void RunIf(bool matches, Func<ICommandBufferGameScenario> factory, BenchmarkOptions opts)
        {
            if (!matches) return;
            using var scenario = factory();
            RunAndPrint(scenario, opts);
        }

        Console.WriteLine("=== CommandBuffer Game Steady-State: MiniArch vs Friflo ===");
        Console.WriteLine($"Actors: {ScenarioDefaults.ActorCount:N0}, Projectiles: {ScenarioDefaults.ProjectileCount:N0}, Spawn/Destroy: {ScenarioDefaults.ProjectileChurnPerTick:N0}/tick");
        Console.WriteLine($"Mutations: {ScenarioDefaults.ActorMutationCount:N0}/tick, Status toggles: {ScenarioDefaults.StatusToggleCount:N0}/tick");
        Console.WriteLine($"Warmup: {options.WarmupSeconds}s, Measure: {options.MeasureSeconds}s{(options.Verbose ? ", Verbose" : "")}");
        Console.WriteLine();
        Console.WriteLine($"{"Engine",-10} | {"Ticks/s",10} | {"ms/tick",9} | {"Checksum",14} | {"Live",8} | {"Heap Î”",12} | {"GC",8} | {"Query",8} | {"Record",8} | {"Apply",8}");
        Console.WriteLine(new string('-', 116));

        RunIf(options.Matches("MiniArch") || options.Matches("MiniStream"), () => new MiniArchCommandStreamSteadyCombatWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        RunIf(options.Matches("Friflo"), () => CreateFrifloScenarioQuietly(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        Console.WriteLine();
        Console.WriteLine("=== ParticleStorm: high structural churn, minimal Set ===");
        Console.WriteLine($"Batch: {ParticleStormDefaults.BatchSize:N0}/tick, Lifespan: {ParticleStormDefaults.StormTickBuffer} ticks, Steady pool: {ParticleStormDefaults.InitialLiveCount - ParticleStormDefaults.EmitterCount:N0}");
        Console.WriteLine($"Emitters: {ParticleStormDefaults.EmitterCount}, Set: {ParticleStormDefaults.EmitterCount}/tick");
        Console.WriteLine();
        Console.WriteLine($"{"Engine",-18} | {"Ticks/s",10} | {"ms/tick",9} | {"Checksum",14} | {"Live",8} | {"Heap Î”",12} | {"GC",8} | {"Query",8} | {"Record",8} | {"Apply",8}");
        Console.WriteLine(new string('-', 122));
        RunIf(options.Matches("MiniArch") || options.Matches("MiniStream") || options.Matches("Storm"), () => new MiniArchCommandStreamParticleStormWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        RunIf(options.Matches("Friflo"), () => CreateFrifloParticleStormQuietly(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        Console.WriteLine();
        Console.WriteLine("=== HeroLight: lightweight per-tick mutations (like the Hero pipeline) ===");
        Console.WriteLine($"Characters: {HeroLightDefaults.CharacterCount:N0}, Requests/tick: {HeroLightDefaults.RequestsPerTick}");
        Console.WriteLine($"Two Submit() per tick, ~{HeroLightDefaults.RequestsPerTick * 2} mutations per tick");
        Console.WriteLine();
        Console.WriteLine($"{"Engine",-18} | {"Ticks/s",10} | {"ms/tick",9} | {"Checksum",14} | {"Live",8} | {"Heap Î”",12} | {"GC",8} | {"Query",8} | {"Record",8} | {"Apply",8}");
        Console.WriteLine(new string('-', 122));
        RunIf(options.Matches("MiniHero") || options.Matches("MiniStream") || options.Matches("Hero"), () => new MiniArchCommandStreamHeroLightWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        RunIf(options.Matches("Friflo") || options.Matches("Hero"), () => new FrifloHeroLightWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        Console.WriteLine();
        Console.WriteLine("Command: dotnet run -c Release --project perf/CommandBufferGame.Perf -- --warmup 3 --measure 10");
    }

    private static void RunAndPrint(ICommandBufferGameScenario scenario, BenchmarkOptions options)
    {
        var result = BenchmarkRunner.Run(scenario, options);
        Console.WriteLine($"{result.Engine,-10} | {result.TicksPerSecond,10:F1} | {result.MillisecondsPerTick,9:F3} | {result.Checksum,14} | {result.LiveCount,8:N0} | {FormatBytes(result.HeapDelta),12} | {result.GcCollections,8} | {result.QueryPercent,7:F1}% | {result.RecordPercent,7:F1}% | {result.ApplyPercent,7:F1}%");
    }

    private static FrifloSteadyCombatWorld CreateFrifloScenarioQuietly()
    {
        var output = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            return new FrifloSteadyCombatWorld();
        }
        finally
        {
            Console.SetOut(output);
        }
    }

    private static FrifloParticleStormWorld CreateFrifloParticleStormQuietly()
    {
        var output = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            return new FrifloParticleStormWorld();
        }
        finally
        {
            Console.SetOut(output);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (Math.Abs(bytes) >= 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }

        if (Math.Abs(bytes) >= 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes} B";
    }
}

public static class ScenarioDefaults
{
    public const int ActorCount = 20_000;
    public const int ProjectileCount = 8_000;
    public const int ProjectileChurnPerTick = 512;
    public const int ActorMutationCount = 2_048;
    public const int StatusToggleCount = 1_024;
    public const int InitialLiveCount = ActorCount + ProjectileCount;
}

public static class ParticleStormDefaults
{
    /// <summary>Entities created (and destroyed) per tick.</summary>
    public const int BatchSize = 4_000;
    /// <summary>Persistent emitter entities with minimal Set mutation.</summary>
    public const int EmitterCount = 100;
    /// <summary>Particle lifespan in ticks. Buffer has this many slots.</summary>
    public const int StormTickBuffer = 2;
    /// <summary>Steady-state live count after warmup.</summary>
    public const int InitialLiveCount = BatchSize * StormTickBuffer + EmitterCount;
}

public readonly record struct BenchmarkOptions(int WarmupSeconds, int MeasureSeconds, bool Verbose, string? ScenarioFilter)
{
    public const int SampleInterval = 100;

    public static BenchmarkOptions Parse(string[] args)
    {
        var warmup = 3;
        var measure = 10;
        var verbose = false;
        string? scenarioFilter = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--warmup" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedWarmup))
            {
                warmup = Math.Max(0, parsedWarmup);
                i++;
                continue;
            }

            if (args[i] == "--measure" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedMeasure))
            {
                measure = Math.Max(1, parsedMeasure);
                i++;
                continue;
            }

            if (args[i] == "--verbose" || args[i] == "-v")
            {
                verbose = true;
                continue;
            }

            if (args[i] == "--scenario" && i + 1 < args.Length)
            {
                scenarioFilter = args[i + 1];
                i++;
                continue;
            }
        }

        return new BenchmarkOptions(warmup, measure, verbose, scenarioFilter);
    }

    public bool Matches(string engine) =>
        ScenarioFilter is null || engine.Contains(ScenarioFilter, StringComparison.OrdinalIgnoreCase);
}

public readonly record struct ScenarioResult(
    string Engine,
    double TicksPerSecond,
    double MillisecondsPerTick,
    long Checksum,
    int LiveCount,
    long HeapDelta,
    string GcCollections,
    double QueryPercent,
    double RecordPercent,
    double ApplyPercent);

public interface ICommandBufferGameScenario : IDisposable
{
    string Engine { get; }
    int LiveCount { get; }
    long Checksum { get; }
    PhaseTicks Phases { get; }
    long RunTick();
    void ResetPhaseCounters();
}

public sealed class PhaseTicks
{
    public long Query;
    public long Record;
    public long Apply;

    public void Clear()
    {
        Query = 0;
        Record = 0;
        Apply = 0;
    }
}

public static class BenchmarkRunner
{

    public static ScenarioResult Run(ICommandBufferGameScenario scenario, BenchmarkOptions options)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < options.WarmupSeconds)
        {
            scenario.RunTick();
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        scenario.ResetPhaseCounters();
        var heapBefore = GC.GetTotalMemory(true);
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);

        long checksum = 0;
        long ticks = 0;

        if (options.Verbose)
        {
            Console.WriteLine();
            Console.WriteLine($"Baseline GC Heap: {heapBefore / (1024.0 * 1024.0):F2} MB");
            Console.WriteLine();
            Console.WriteLine($"{"Round",8} | {"Rounds/s",10} | {"Heap MB",8} | {"dHeap KB",8} | {"WS MB",7} | {"Gen0",5} | {"Gen1",5} | {"Gen2",5}");
            Console.WriteLine(new string('-', 78));
        }

        long lastSampleHeap = heapBefore;
        long lastSampleTicks = 0;
        double lastSampleTime = 0;
        sw.Restart();
        while (sw.Elapsed.TotalSeconds < options.MeasureSeconds)
        {
            checksum += scenario.RunTick();
            ticks++;

            if (options.Verbose && ticks % BenchmarkOptions.SampleInterval == 0)
            {
                var now = sw.Elapsed.TotalSeconds;
                var currentHeap = GC.GetTotalMemory(false);
                var ws = Environment.WorkingSet;
                var roundDelta = ticks - lastSampleTicks;
                var timeDelta = now - lastSampleTime;
                var currentRoundsPerSec = timeDelta > 0 ? roundDelta / timeDelta : 0;
                var dHeapKB = (currentHeap - lastSampleHeap) / 1024.0;
                var heapMB = currentHeap / (1024.0 * 1024.0);
                var wsMB = ws / (1024.0 * 1024.0);
                var c0 = GC.CollectionCount(0) - g0;
                var c1 = GC.CollectionCount(1) - g1;
                var c2 = GC.CollectionCount(2) - g2;

                Console.WriteLine($"{ticks,8} | {currentRoundsPerSec,10:F1} | {heapMB,8:F2} | {dHeapKB,8:F1} | {wsMB,7:F1} | {c0,5} | {c1,5} | {c2,5}");

                lastSampleHeap = currentHeap;
                lastSampleTicks = ticks;
                lastSampleTime = now;
            }
        }

        var elapsed = sw.Elapsed.TotalSeconds;
        var heapAfter = GC.GetTotalMemory(false);
        var gcText = $"{GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}";
        var totalPhaseTicks = Math.Max(1, scenario.Phases.Query + scenario.Phases.Record + scenario.Phases.Apply);

        return new ScenarioResult(
            scenario.Engine,
            ticks / elapsed,
            elapsed * 1000.0 / Math.Max(1, ticks),
            checksum,
            scenario.LiveCount,
            heapAfter - heapBefore,
            gcText,
            scenario.Phases.Query * 100.0 / totalPhaseTicks,
            scenario.Phases.Record * 100.0 / totalPhaseTicks,
            scenario.Phases.Apply * 100.0 / totalPhaseTicks);
    }
}

public sealed class MiniArchCommandStreamSteadyCombatWorld : ICommandBufferGameScenario
{
    private readonly MiniWorld _world = new(128, ScenarioDefaults.InitialLiveCount + ScenarioDefaults.ProjectileChurnPerTick * 4);
    private readonly MiniCommandStream _stream;
    private readonly MiniEntity[] _actors = new MiniEntity[ScenarioDefaults.ActorCount];
    private readonly MiniEntity[] _destroyScratch = new MiniEntity[ScenarioDefaults.ProjectileChurnPerTick];
    private readonly bool[] _burning = new bool[ScenarioDefaults.ActorCount];
    private readonly bool[] _frozen = new bool[ScenarioDefaults.ActorCount];
    private readonly MiniQuery _actorQuery;
    private readonly MiniQuery _projectileQuery;
    private readonly PhaseTicks _phases = new();
    private int _tick;
    private int _liveCount = ScenarioDefaults.InitialLiveCount;
    private long _checksum;

    public MiniArchCommandStreamSteadyCombatWorld()
    {
        _stream = new MiniCommandStream(_world);

        for (var i = 0; i < _actors.Length; i++)
        {
            var entity = _world.Create(new Position(i, i * 2), new Velocity((i & 3) - 1, (i & 7) - 3), new Health(100 + (i % 50)), new Team(i & 3));
            _actors[i] = entity;

            if ((i & 3) == 0)
            {
                _world.Add(entity, new Burning(1));
                _burning[i] = true;
            }

            if ((i & 7) == 0)
            {
                _world.Add(entity, new Frozen(1));
                _frozen[i] = true;
            }
        }

        for (var i = 0; i < ScenarioDefaults.ProjectileCount; i++)
        {
            _world.Create(new Position(i, -i), new Velocity(3, -2), new Damage(5 + (i % 11)), new Lifetime(60 + (i % 120)), new ProjectileTag(1));
        }

        _actorQuery = _world.Query(new MiniQueryDescription().With<Position>().With<Velocity>().With<Health>());
        _projectileQuery = _world.Query(new MiniQueryDescription().With<ProjectileTag>().With<Lifetime>());
    }

    public string Engine => "MiniStream";
    public int LiveCount => _liveCount;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var tickStartChecksum = _checksum;
        var start = Stopwatch.GetTimestamp();
        var destroyed = QueryWorld();
        var afterQuery = Stopwatch.GetTimestamp();
        RecordCommands(destroyed);
        var afterRecord = Stopwatch.GetTimestamp();
        _stream.Submit();
        var afterApply = Stopwatch.GetTimestamp();

        _phases.Query += afterQuery - start;
        _phases.Record += afterRecord - afterQuery;
        _phases.Apply += afterApply - afterRecord;
        _tick++;
        return _checksum - tickStartChecksum;
    }

    public void ResetPhaseCounters() => _phases.Clear();
    public void Dispose() { }

    private int QueryWorld()
    {
        foreach (var chunk in _actorQuery.GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            var health = chunk.GetSpan<Health>();
            for (var i = 0; i < positions.Length; i++)
            {
                _checksum += positions[i].X + velocities[i].VX + health[i].Value;
            }
        }

        var destroyed = 0;
        foreach (var chunk in _projectileQuery.GetChunks())
        {
            var lifetimes = chunk.GetSpan<Lifetime>();
            var entities = chunk.GetEntities();
            for (var i = 0; i < lifetimes.Length; i++)
            {
                _checksum += lifetimes[i].Ticks;
                if (destroyed < _destroyScratch.Length)
                {
                    _destroyScratch[destroyed++] = entities[i];
                }
            }
        }

        return destroyed;
    }

    private void RecordCommands(int destroyed)
    {
        for (var i = 0; i < ScenarioDefaults.ActorMutationCount; i++)
        {
            var index = ActorIndex(i, 17);
            var entity = _actors[index];
            _stream.Set(entity, new Position(_tick + index, index - _tick));
            _stream.Set(entity, new Health(75 + ((_tick + index) % 125)));
            _checksum += entity.Id;
        }

        for (var i = 0; i < ScenarioDefaults.StatusToggleCount; i++)
        {
            var index = ActorIndex(i, 31);
            var entity = _actors[index];
            if (_burning[index])
            {
                _stream.Remove<Burning>(entity);
            }
            else
            {
                _stream.Add(entity, new Burning(_tick));
            }

            _burning[index] = !_burning[index];

            if ((i & 1) == 0)
            {
                if (_frozen[index])
                {
                    _stream.Remove<Frozen>(entity);
                }
                else
                {
                    _stream.Add(entity, new Frozen(_tick));
                }

                _frozen[index] = !_frozen[index];
            }
        }

        for (var i = 0; i < destroyed; i++)
        {
            _stream.Destroy(_destroyScratch[i]);
        }

        for (var i = 0; i < ScenarioDefaults.ProjectileChurnPerTick; i++)
        {
            var entity = _stream.Create();
            _stream.Add(entity, new Position(_tick * 3 + i, -i));
            _stream.Add(entity, new Velocity(3 + (i & 3), -2));
            _stream.Add(entity, new Damage(8 + (i % 13)));
            _stream.Add(entity, new Lifetime(90 + (i % 90)));
            _stream.Add(entity, new ProjectileTag(1));
        }

        _liveCount += ScenarioDefaults.ProjectileChurnPerTick - destroyed;
    }

    private int ActorIndex(int offset, int stride) => (int)(((long)_tick * stride + offset * 13L) % _actors.Length);
}

public sealed class FrifloSteadyCombatWorld : ICommandBufferGameScenario
{
    private readonly EntityStore _store = new();
    private readonly CommandBuffer _buffer;
    private readonly int[] _actors = new int[ScenarioDefaults.ActorCount];
    private readonly int[] _destroyScratch = new int[ScenarioDefaults.ProjectileChurnPerTick];
    private readonly bool[] _burning = new bool[ScenarioDefaults.ActorCount];
    private readonly bool[] _frozen = new bool[ScenarioDefaults.ActorCount];
    private readonly ArchetypeQuery<Position, Velocity, Health> _actorQuery;
    private readonly ArchetypeQuery<ProjectileTag, Lifetime> _projectileQuery;
    private readonly PhaseTicks _phases = new();
    private int _tick;
    private long _checksum;

    public FrifloSteadyCombatWorld()
    {
        _store.EnsureCapacity(ScenarioDefaults.InitialLiveCount + ScenarioDefaults.ProjectileChurnPerTick * 4);

        for (var i = 0; i < _actors.Length; i++)
        {
            var entity = _store.CreateEntity(new Position(i, i * 2), new Velocity((i & 3) - 1, (i & 7) - 3), new Health(100 + (i % 50)), new Team(i & 3));
            _actors[i] = entity.Id;

            if ((i & 3) == 0)
            {
                entity.AddComponent(new Burning(1));
                _burning[i] = true;
            }

            if ((i & 7) == 0)
            {
                entity.AddComponent(new Frozen(1));
                _frozen[i] = true;
            }
        }

        for (var i = 0; i < ScenarioDefaults.ProjectileCount; i++)
        {
            _store.CreateEntity(new Position(i, -i), new Velocity(3, -2), new Damage(5 + (i % 11)), new Lifetime(60 + (i % 120)), new ProjectileTag(1));
        }

        _actorQuery = _store.Query<Position, Velocity, Health>();
        _projectileQuery = _store.Query<ProjectileTag, Lifetime>();
        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;
    }

    public string Engine => "Friflo";
    public int LiveCount => _store.Count;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var tickStartChecksum = _checksum;
        var start = Stopwatch.GetTimestamp();
        var destroyed = QueryWorld();
        var afterQuery = Stopwatch.GetTimestamp();
        RecordCommands(destroyed);
        var afterRecord = Stopwatch.GetTimestamp();
        _buffer.Playback();
        var afterApply = Stopwatch.GetTimestamp();

        _phases.Query += afterQuery - start;
        _phases.Record += afterRecord - afterQuery;
        _phases.Apply += afterApply - afterRecord;
        _tick++;
        return _checksum - tickStartChecksum;
    }

    public void ResetPhaseCounters() => _phases.Clear();
    public void Dispose() { }

    private int QueryWorld()
    {
        foreach (var (positions, velocities, health, entities) in _actorQuery.Chunks)
        {
            var positionSpan = positions.Span;
            var velocitySpan = velocities.Span;
            var healthSpan = health.Span;
            for (var i = 0; i < entities.Length; i++)
            {
                _checksum += positionSpan[i].X + velocitySpan[i].VX + healthSpan[i].Value;
            }
        }

        var destroyed = 0;
        foreach (var (_, lifetimes, entities) in _projectileQuery.Chunks)
        {
            var lifetimeSpan = lifetimes.Span;
            for (var i = 0; i < entities.Length; i++)
            {
                _checksum += lifetimeSpan[i].Ticks;
                if (destroyed < _destroyScratch.Length)
                {
                    _destroyScratch[destroyed++] = entities[i];
                }
            }
        }

        return destroyed;
    }

    private void RecordCommands(int destroyed)
    {
        for (var i = 0; i < ScenarioDefaults.ActorMutationCount; i++)
        {
            var index = ActorIndex(i, 17);
            var entityId = _actors[index];
            _buffer.AddComponent(entityId, new Position(_tick + index, index - _tick));
            _buffer.AddComponent(entityId, new Health(75 + ((_tick + index) % 125)));
            _checksum += entityId;
        }

        for (var i = 0; i < ScenarioDefaults.StatusToggleCount; i++)
        {
            var index = ActorIndex(i, 31);
            var entityId = _actors[index];
            if (_burning[index])
            {
                _buffer.RemoveComponent<Burning>(entityId);
            }
            else
            {
                _buffer.AddComponent(entityId, new Burning(_tick));
            }

            _burning[index] = !_burning[index];

            if ((i & 1) == 0)
            {
                if (_frozen[index])
                {
                    _buffer.RemoveComponent<Frozen>(entityId);
                }
                else
                {
                    _buffer.AddComponent(entityId, new Frozen(_tick));
                }

                _frozen[index] = !_frozen[index];
            }
        }

        for (var i = 0; i < destroyed; i++)
        {
            _buffer.DeleteEntity(_destroyScratch[i]);
        }

        for (var i = 0; i < ScenarioDefaults.ProjectileChurnPerTick; i++)
        {
            var entityId = _buffer.CreateEntity();
            _buffer.AddComponent(entityId, new Position(_tick * 3 + i, -i));
            _buffer.AddComponent(entityId, new Velocity(3 + (i & 3), -2));
            _buffer.AddComponent(entityId, new Damage(8 + (i % 13)));
            _buffer.AddComponent(entityId, new Lifetime(90 + (i % 90)));
            _buffer.AddComponent(entityId, new ProjectileTag(1));
        }
    }

    private int ActorIndex(int offset, int stride) => (int)(((long)_tick * stride + offset * 13L) % _actors.Length);
}

public sealed class ArchSteadyCombatWorld : ICommandBufferGameScenario
{
    private static readonly ArchComponentType[] EmptyTypes = [];
    private readonly ArchWorld _world = ArchWorld.Create();
    private readonly ArchCommandBuffer _buffer = new(ScenarioDefaults.ProjectileChurnPerTick * 8 + ScenarioDefaults.ActorMutationCount * 4);
    private readonly ArchEntity[] _actors = new ArchEntity[ScenarioDefaults.ActorCount];
    private readonly ArchEntity[] _destroyScratch = new ArchEntity[ScenarioDefaults.ProjectileChurnPerTick];
    private readonly bool[] _burning = new bool[ScenarioDefaults.ActorCount];
    private readonly bool[] _frozen = new bool[ScenarioDefaults.ActorCount];
    private readonly ArchQueryDescription _actorQuery = new ArchQueryDescription().WithAll<Position, Velocity, Health>();
    private readonly ArchQueryDescription _projectileQuery = new ArchQueryDescription().WithAll<ProjectileTag, Lifetime>();
    private readonly PhaseTicks _phases = new();
    private int _tick;
    private int _liveCount = ScenarioDefaults.InitialLiveCount;
    private long _checksum;

    public ArchSteadyCombatWorld()
    {
        for (var i = 0; i < _actors.Length; i++)
        {
            var entity = _world.Create<Position, Velocity, Health, Team>(new Position(i, i * 2), new Velocity((i & 3) - 1, (i & 7) - 3), new Health(100 + (i % 50)), new Team(i & 3));
            _actors[i] = entity;

            if ((i & 3) == 0)
            {
                _world.Add(entity, new Burning(1));
                _burning[i] = true;
            }

            if ((i & 7) == 0)
            {
                _world.Add(entity, new Frozen(1));
                _frozen[i] = true;
            }
        }

        for (var i = 0; i < ScenarioDefaults.ProjectileCount; i++)
        {
            _world.Create<Position, Velocity, Damage, Lifetime, ProjectileTag>(new Position(i, -i), new Velocity(3, -2), new Damage(5 + (i % 11)), new Lifetime(60 + (i % 120)), new ProjectileTag(1));
        }
    }

    public string Engine => "Arch";
    public int LiveCount => _liveCount;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var tickStartChecksum = _checksum;
        var start = Stopwatch.GetTimestamp();
        var destroyed = QueryWorld();
        var afterQuery = Stopwatch.GetTimestamp();
        RecordCommands(destroyed);
        var afterRecord = Stopwatch.GetTimestamp();
        _buffer.Playback(_world, true);
        var afterApply = Stopwatch.GetTimestamp();

        _phases.Query += afterQuery - start;
        _phases.Record += afterRecord - afterQuery;
        _phases.Apply += afterApply - afterRecord;
        _tick++;
        return _checksum - tickStartChecksum;
    }

    public void ResetPhaseCounters() => _phases.Clear();

    public void Dispose()
    {
        _buffer.Dispose();
        _world.Dispose();
    }

    private int QueryWorld()
    {
        var actorQuery = _world.Query(in _actorQuery);
        foreach (var chunk in actorQuery)
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            var health = chunk.GetSpan<Health>();
            for (var i = 0; i < chunk.Count; i++)
            {
                _checksum += positions[i].X + velocities[i].VX + health[i].Value;
            }
        }

        var destroyed = 0;
        var projectileQuery = _world.Query(in _projectileQuery);
        foreach (var chunk in projectileQuery)
        {
            var lifetimes = chunk.GetSpan<Lifetime>();
            for (var i = 0; i < chunk.Count; i++)
            {
                _checksum += lifetimes[i].Ticks;
                if (destroyed < _destroyScratch.Length)
                {
                    _destroyScratch[destroyed++] = chunk.Entity(i);
                }
            }
        }

        return destroyed;
    }

    private void RecordCommands(int destroyed)
    {
        for (var i = 0; i < ScenarioDefaults.ActorMutationCount; i++)
        {
            var index = ActorIndex(i, 17);
            var entity = _actors[index];
            _buffer.Set(entity, new Position(_tick + index, index - _tick));
            _buffer.Set(entity, new Health(75 + ((_tick + index) % 125)));
            _checksum += entity.Id;
        }

        for (var i = 0; i < ScenarioDefaults.StatusToggleCount; i++)
        {
            var index = ActorIndex(i, 31);
            var entity = _actors[index];
            if (_burning[index])
            {
                _buffer.Remove<Burning>(entity);
            }
            else
            {
                _buffer.Add(entity, new Burning(_tick));
            }

            _burning[index] = !_burning[index];

            if ((i & 1) == 0)
            {
                if (_frozen[index])
                {
                    _buffer.Remove<Frozen>(entity);
                }
                else
                {
                    _buffer.Add(entity, new Frozen(_tick));
                }

                _frozen[index] = !_frozen[index];
            }
        }

        for (var i = 0; i < destroyed; i++)
        {
            _buffer.Destroy(_destroyScratch[i]);
        }

        for (var i = 0; i < ScenarioDefaults.ProjectileChurnPerTick; i++)
        {
            var entity = _buffer.Create(EmptyTypes);
            _buffer.Add(entity, new Position(_tick * 3 + i, -i));
            _buffer.Add(entity, new Velocity(3 + (i & 3), -2));
            _buffer.Add(entity, new Damage(8 + (i % 13)));
            _buffer.Add(entity, new Lifetime(90 + (i % 90)));
            _buffer.Add(entity, new ProjectileTag(1));
        }

        _liveCount += ScenarioDefaults.ProjectileChurnPerTick - destroyed;
    }

    private int ActorIndex(int offset, int stride) => (int)(((long)_tick * stride + offset * 13L) % _actors.Length);
}

// ============================================================
// ParticleStorm scenarios â€?high structural churn, minimal Set
// ============================================================

public sealed class MiniArchCommandStreamParticleStormWorld : ICommandBufferGameScenario
{
    private readonly MiniWorld _world = new(128, ParticleStormDefaults.InitialLiveCount + ParticleStormDefaults.BatchSize);
    private readonly MiniCommandStream _stream;
    private readonly MiniEntity[][] _stormSlots;
    private readonly MiniEntity[] _emitters;
    private readonly PhaseTicks _phases = new();
    private int _tick;
    private long _checksum;

    public MiniArchCommandStreamParticleStormWorld()
    {
        _stream = new MiniCommandStream(_world);

        _stormSlots = new MiniEntity[ParticleStormDefaults.StormTickBuffer][];
        for (var i = 0; i < _stormSlots.Length; i++)
            _stormSlots[i] = new MiniEntity[ParticleStormDefaults.BatchSize];

        _emitters = new MiniEntity[ParticleStormDefaults.EmitterCount];
        for (var i = 0; i < _emitters.Length; i++)
            _emitters[i] = _world.Create(new Position(i, i), new EmitterTag(i));
    }

    public string Engine => "MiniStream-Storm";
    public int LiveCount => ParticleStormDefaults.EmitterCount + Math.Min(_tick, ParticleStormDefaults.StormTickBuffer) * ParticleStormDefaults.BatchSize;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var tickStart = _checksum;
        var start = Stopwatch.GetTimestamp();

        for (var i = 0; i < _emitters.Length; i++)
            _checksum += _emitters[i].Id + i;
        var afterQuery = Stopwatch.GetTimestamp();

        RecordCommands();
        var afterRecord = Stopwatch.GetTimestamp();

        _stream.Submit();
        var afterApply = Stopwatch.GetTimestamp();

        _phases.Query += afterQuery - start;
        _phases.Record += afterRecord - afterQuery;
        _phases.Apply += afterApply - afterRecord;
        _tick++;
        return _checksum - tickStart;
    }

    public void ResetPhaseCounters() => _phases.Clear();
    public void Dispose() { }

    private void RecordCommands()
    {
        if (_tick >= ParticleStormDefaults.StormTickBuffer)
        {
            var slot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
            for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
                _stream.Destroy(slot[i]);
        }

        for (var i = 0; i < _emitters.Length; i++)
        {
            _stream.Set(_emitters[i], new Position(_tick + i, _tick * 2 + i));
            _stream.Set(_emitters[i], new Alpha((_tick + i) % 256));
        }

        var createSlot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
        for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
        {
            var entity = _stream.Create();
            _stream.Add(entity, new Position(i, _tick));
            _stream.Add(entity, new Velocity((i & 3) - 1, (i & 7) - 3));
            _checksum += i + _tick;

            var type = i % 10;
            if (type < 3)
                _stream.Add(entity, new Alpha((i + _tick) % 256));
            else if (type < 6)
            {
                _stream.Add(entity, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255));
                _stream.Add(entity, new Scale(1 + (i % 10)));
            }
            else if (type < 8)
            {
                _stream.Add(entity, new Alpha((i + _tick) % 256));
                _stream.Add(entity, new Lifetime(2));
            }
            else
            {
                _stream.Add(entity, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255));
                _stream.Add(entity, new Scale(1 + (i % 10)));
                _stream.Add(entity, new Lifetime(2));
            }

            createSlot[i] = entity;
        }
    }
}

public sealed class FrifloParticleStormWorld : ICommandBufferGameScenario
{
    private readonly EntityStore _store = new();
    private readonly CommandBuffer _buffer;
    private readonly int[][] _stormSlots;
    private readonly int[] _emitters;
    private readonly PhaseTicks _phases = new();
    private int _tick;
    private long _checksum;

    public FrifloParticleStormWorld()
    {
        _store.EnsureCapacity(ParticleStormDefaults.InitialLiveCount + ParticleStormDefaults.BatchSize);

        _stormSlots = new int[ParticleStormDefaults.StormTickBuffer][];
        for (var i = 0; i < _stormSlots.Length; i++)
            _stormSlots[i] = new int[ParticleStormDefaults.BatchSize];

        _emitters = new int[ParticleStormDefaults.EmitterCount];
        for (var i = 0; i < _emitters.Length; i++)
        {
            var entity = _store.CreateEntity(new Position(i, i), new EmitterTag(i));
            _emitters[i] = entity.Id;
        }

        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;
    }

    public string Engine => "Friflo-Storm";
    public int LiveCount => ParticleStormDefaults.EmitterCount + Math.Min(_tick, ParticleStormDefaults.StormTickBuffer) * ParticleStormDefaults.BatchSize;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var tickStart = _checksum;
        var start = Stopwatch.GetTimestamp();

        for (var i = 0; i < _emitters.Length; i++)
            _checksum += _emitters[i] + i;
        var afterQuery = Stopwatch.GetTimestamp();

        RecordCommands();
        var afterRecord = Stopwatch.GetTimestamp();

        _buffer.Playback();
        var afterApply = Stopwatch.GetTimestamp();

        _phases.Query += afterQuery - start;
        _phases.Record += afterRecord - afterQuery;
        _phases.Apply += afterApply - afterRecord;
        _tick++;
        return _checksum - tickStart;
    }

    public void ResetPhaseCounters() => _phases.Clear();
    public void Dispose() { }

    private void RecordCommands()
    {
        if (_tick >= ParticleStormDefaults.StormTickBuffer)
        {
            var slot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
            for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
                _buffer.DeleteEntity(slot[i]);
        }

        for (var i = 0; i < _emitters.Length; i++)
        {
            _buffer.AddComponent(_emitters[i], new Position(_tick + i, _tick * 2 + i));
            _buffer.AddComponent(_emitters[i], new Alpha((_tick + i) % 256));
        }

        var createSlot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
        for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
        {
            var entityId = _buffer.CreateEntity();
            _buffer.AddComponent(entityId, new Position(i, _tick));
            _buffer.AddComponent(entityId, new Velocity((i & 3) - 1, (i & 7) - 3));
            _checksum += i + _tick;

            var type = i % 10;
            if (type < 3)
                _buffer.AddComponent(entityId, new Alpha((i + _tick) % 256));
            else if (type < 6)
            {
                _buffer.AddComponent(entityId, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255));
                _buffer.AddComponent(entityId, new Scale(1 + (i % 10)));
            }
            else if (type < 8)
            {
                _buffer.AddComponent(entityId, new Alpha((i + _tick) % 256));
                _buffer.AddComponent(entityId, new Lifetime(2));
            }
            else
            {
                _buffer.AddComponent(entityId, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255));
                _buffer.AddComponent(entityId, new Scale(1 + (i % 10)));
                _buffer.AddComponent(entityId, new Lifetime(2));
            }

            createSlot[i] = entityId;
        }
    }
}

public sealed class ArchParticleStormWorld : ICommandBufferGameScenario
{
    private readonly ArchWorld _world = ArchWorld.Create();
    // Arch CB cannot safely destroy entities from a previous Playback cycle,
    // so structural ops (create/destroy) go directly to the world (immediate mode).
    // Buffer is used only for emitter Set (which CB handles correctly).
    private readonly ArchCommandBuffer _buffer = new(ParticleStormDefaults.BatchSize);
    private readonly ArchEntity[][] _stormSlots;
    private readonly ArchEntity[] _emitters;
    private readonly PhaseTicks _phases = new();
    private int _tick;
    private long _checksum;

    public ArchParticleStormWorld()
    {
        _stormSlots = new ArchEntity[ParticleStormDefaults.StormTickBuffer][];
        for (var i = 0; i < _stormSlots.Length; i++)
        {
            _stormSlots[i] = new ArchEntity[ParticleStormDefaults.BatchSize];
            for (var j = 0; j < ParticleStormDefaults.BatchSize; j++)
                _stormSlots[i][j] = CreateEntityDirect(j % 10, j, 0);
        }

        _emitters = new ArchEntity[ParticleStormDefaults.EmitterCount];
        for (var i = 0; i < _emitters.Length; i++)
            _emitters[i] = _world.Create<Position, EmitterTag>(new Position(i, i), new EmitterTag(i));
    }

    public string Engine => "Arch-Storm";
    public int LiveCount => ParticleStormDefaults.EmitterCount + Math.Min(_tick, ParticleStormDefaults.StormTickBuffer) * ParticleStormDefaults.BatchSize;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var tickStart = _checksum;
        var start = Stopwatch.GetTimestamp();

        for (var i = 0; i < _emitters.Length; i++)
            _checksum += _emitters[i].Id + i;
        var afterQuery = Stopwatch.GetTimestamp();

        RecordCommands();
        var afterRecord = Stopwatch.GetTimestamp();

        // Only emitter Set goes through buffer
        _buffer.Playback(_world, true);
        var afterApply = Stopwatch.GetTimestamp();

        _phases.Query += afterQuery - start;
        _phases.Record += afterRecord - afterQuery;
        _phases.Apply += afterApply - afterRecord;
        _tick++;
        return _checksum - tickStart;
    }

    public void ResetPhaseCounters() => _phases.Clear();

    public void Dispose()
    {
        _buffer.Dispose();
        _world.Dispose();
    }

    private void RecordCommands()
    {
        // Destroy old batch directly (immediate mode)
        if (_tick >= ParticleStormDefaults.StormTickBuffer)
        {
            var slot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
            for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
                _world.Destroy(slot[i]);
        }

        // Emitter Set through buffer (the only CB usage)
        for (var i = 0; i < _emitters.Length; i++)
        {
            _buffer.Set(_emitters[i], new Position(_tick + i, _tick * 2 + i));
            _buffer.Set(_emitters[i], new Alpha((_tick + i) % 256));
        }

        // Create new batch directly (immediate mode)
        var createSlot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
        for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
        {
            var entity = CreateEntityDirect(i % 10, i, _tick);
            _checksum += i + _tick;
            createSlot[i] = entity;
        }
    }

    private ArchEntity CreateEntityDirect(int type, int i, int tick)
    {
        var p = new Position(i, tick);
        var v = new Velocity((i & 3) - 1, (i & 7) - 3);
        if (type < 3)
            return _world.Create<Position, Velocity, Alpha>(p, v, new Alpha((i + tick) % 256));
        else if (type < 6)
            return _world.Create<Position, Velocity, Color, Scale>(p, v, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255), new Scale(1 + (i % 10)));
        else if (type < 8)
            return _world.Create<Position, Velocity, Alpha, Lifetime>(p, v, new Alpha((i + tick) % 256), new Lifetime(2));
        else
            return _world.Create<Position, Velocity, Color, Scale, Lifetime>(p, v, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255), new Scale(1 + (i % 10)), new Lifetime(2));
    }
}

public struct Position : IComponent
{
    public int X;
    public int Y;
    public Position(int x, int y) { X = x; Y = y; }
}

public struct Velocity : IComponent
{
    public int VX;
    public int VY;
    public Velocity(int vx, int vy) { VX = vx; VY = vy; }
}

public struct Health : IComponent
{
    public int Value;
    public Health(int value) { Value = value; }
}

public struct Team : IComponent
{
    public int Value;
    public Team(int value) { Value = value; }
}

public struct Damage : IComponent
{
    public int Value;
    public Damage(int value) { Value = value; }
}

public struct Lifetime : IComponent
{
    public int Ticks;
    public Lifetime(int ticks) { Ticks = ticks; }
}

public struct ProjectileTag : IComponent
{
    public int Value;
    public ProjectileTag(int value) { Value = value; }
}

public struct Burning : IComponent
{
    public int Ticks;
    public Burning(int ticks) { Ticks = ticks; }
}

public struct Frozen : IComponent
{
    public int Ticks;
    public Frozen(int ticks) { Ticks = ticks; }
}

public struct Alpha : IComponent
{
    public int Value;
    public Alpha(int value) { Value = value; }
}

public struct Color : IComponent
{
    public int R;
    public int G;
    public int B;
    public int A;
    public Color(int r, int g, int b, int a) { R = r; G = g; B = b; A = a; }
}

public struct Scale : IComponent
{
    public int Value;
    public Scale(int value) { Value = value; }
}

public struct EmitterTag : IComponent
{
    public int Value;
    public EmitterTag(int value) { Value = value; }
}

// --- HeroLight: lightweight per-tick mutations (like the Hero pipeline) ---

public static class HeroLightDefaults
{
    /// <summary>Persistent character entities.</summary>
    public const int CharacterCount = 1000;
    /// <summary>Request entities created per tick.</summary>
    public const int RequestsPerTick = 10;
}

public struct RequestTag : IComponent { }
public struct EffectTag : IComponent { }

public struct MiniArchRequestTarget { public MiniEntity Target; }
public struct FrifloRequestTarget : IComponent { public int TargetId; }

// Friflo doesn't support struct ref fields, so we use entity id via a lookup array.
public sealed class MiniArchCommandStreamHeroLightWorld : ICommandBufferGameScenario
{
    private readonly MiniWorld _world = new();
    private readonly MiniCommandStream _stream;
    private readonly MiniEntity[] _characters = new MiniEntity[HeroLightDefaults.CharacterCount];
    private readonly PhaseTicks _phases = new();
    private long _checksum;

    public MiniArchCommandStreamHeroLightWorld()
    {
        _stream = new MiniCommandStream(_world);
        for (var i = 0; i < _characters.Length; i++)
            _characters[i] = _world.Create(new Health(100), new Team(i & 3));
        // Seed initial pending requests
        for (var i = 0; i < HeroLightDefaults.RequestsPerTick; i++)
        {
            var req = _stream.Create();
            _stream.Add<RequestTag>(req, default);
            _stream.Add(req, new MiniArchRequestTarget { Target = _characters[i % _characters.Length] });
        }
        _stream.Submit();
    }

    public string Engine => "MiniHero-Stream";
    public int LiveCount => HeroLightDefaults.CharacterCount;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;
    public void ResetPhaseCounters() => _phases.Clear();
    public void Dispose() => _world.Dispose();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var t0 = Stopwatch.GetTimestamp();
        _stream.Submit();
        var tQ = Stopwatch.GetTimestamp();

        // Process existing requests â†?create effects + modify HP
        foreach (var chunk in _world.Query(new MiniQueryDescription().With<RequestTag>().With<MiniArchRequestTarget>()).GetChunks())
        {
            var targets = chunk.GetSpan<MiniArchRequestTarget>();
            for (var i = 0; i < chunk.Count; i++)
            {
                var effect = _stream.Create();
                _stream.Add<EffectTag>(effect, default);
                _stream.Add(effect, new MiniArchRequestTarget { Target = targets[i].Target });
                _stream.Set(targets[i].Target, new Health(101));
            }
        }

        // Destroy old request + effect entities
        foreach (var chunk in _world.Query(new MiniQueryDescription().With<RequestTag>()).GetChunks())
        {
            for (var i = 0; i < chunk.Count; i++)
                _stream.Destroy(chunk.GetEntities()[i]);
        }
        foreach (var chunk in _world.Query(new MiniQueryDescription().With<EffectTag>()).GetChunks())
        {
            for (var i = 0; i < chunk.Count; i++)
                _stream.Destroy(chunk.GetEntities()[i]);
        }

        // Create new request entities for the NEXT tick
        for (var i = 0; i < HeroLightDefaults.RequestsPerTick; i++)
        {
            var req = _stream.Create();
            _stream.Add<RequestTag>(req, default);
            _stream.Add(req, new MiniArchRequestTarget { Target = _characters[(i + _tickCounter) % _characters.Length] });
        }
        _tickCounter++;

        var tR = Stopwatch.GetTimestamp();
        _stream.Submit();
        var tA = Stopwatch.GetTimestamp();

        _phases.Query += tQ - t0;
        _phases.Record += tR - tQ;
        _phases.Apply += tA - tR;

        _checksum += HeroLightDefaults.RequestsPerTick;
        return _checksum;
    }
    private int _tickCounter;
}

public sealed class FrifloHeroLightWorld : ICommandBufferGameScenario
{
    private readonly EntityStore _store = new();
    private readonly CommandBuffer _buffer;
    private readonly int[] _characterIds = new int[HeroLightDefaults.CharacterCount];
    private readonly PhaseTicks _phases = new();
    private long _checksum;

    public FrifloHeroLightWorld()
    {
        _store.EnsureCapacity(HeroLightDefaults.CharacterCount + HeroLightDefaults.RequestsPerTick * 4);

        for (var i = 0; i < _characterIds.Length; i++)
        {
            var entity = _store.CreateEntity(new Health(100), new Team(i & 3));
            _characterIds[i] = entity.Id;
        }

        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;

        // Seed initial pending requests
        for (var i = 0; i < HeroLightDefaults.RequestsPerTick; i++)
        {
            var req = _buffer.CreateEntity();
            _buffer.AddComponent(req, new RequestTag());
            _buffer.AddComponent(req, new FrifloRequestTarget { TargetId = _characterIds[i % _characterIds.Length] });
        }
        _buffer.Playback();
    }

    public string Engine => "Friflo-Hero";
    public int LiveCount => HeroLightDefaults.CharacterCount;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;
    public void ResetPhaseCounters() => _phases.Clear();
    public void Dispose() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var t0 = Stopwatch.GetTimestamp();
        _buffer.Playback();
        var tQ = Stopwatch.GetTimestamp();

        // Process request entities â†?create effects + modify HP
        foreach (var (tags, targets, entities) in _store.Query<RequestTag, FrifloRequestTarget>().Chunks)
        {
            for (var i = 0; i < entities.Length; i++)
            {
                var effect = _buffer.CreateEntity();
                _buffer.AddComponent(effect, new EffectTag());
                _buffer.AddComponent(effect, new FrifloRequestTarget { TargetId = targets[i].TargetId });
                _buffer.AddComponent(targets[i].TargetId, new Health(101));
            }
        }

        // Destroy old request + effect entities
        foreach (var (_, entities) in _store.Query<RequestTag>().Chunks)
        {
            for (var i = 0; i < entities.Length; i++)
                _buffer.DeleteEntity(entities[i]);
        }
        foreach (var (_, entities) in _store.Query<EffectTag>().Chunks)
        {
            for (var i = 0; i < entities.Length; i++)
                _buffer.DeleteEntity(entities[i]);
        }

        // Create new request entities for the NEXT tick
        for (var i = 0; i < HeroLightDefaults.RequestsPerTick; i++)
        {
            var req = _buffer.CreateEntity();
            _buffer.AddComponent(req, new RequestTag());
            _buffer.AddComponent(req, new FrifloRequestTarget { TargetId = _characterIds[(i + _tickCounter) % _characterIds.Length] });
        }
        _tickCounter++;

        var tR = Stopwatch.GetTimestamp();
        _buffer.Playback();
        var tA = Stopwatch.GetTimestamp();

        _phases.Query += tQ - t0;
        _phases.Record += tR - tQ;
        _phases.Apply += tA - tR;

        _checksum += HeroLightDefaults.RequestsPerTick;
        return _checksum;
    }
    private int _tickCounter;
}
