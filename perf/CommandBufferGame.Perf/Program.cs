using System.Diagnostics;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using MiniCommandBuffer = MiniArch.Core.CommandBuffer;
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
        Console.WriteLine("=== CommandBuffer Game Steady-State: MiniArch vs Friflo vs Arch ===");
        Console.WriteLine($"Actors: {ScenarioDefaults.ActorCount:N0}, Projectiles: {ScenarioDefaults.ProjectileCount:N0}, Spawn/Destroy: {ScenarioDefaults.ProjectileChurnPerTick:N0}/tick");
        Console.WriteLine($"Mutations: {ScenarioDefaults.ActorMutationCount:N0}/tick, Status toggles: {ScenarioDefaults.StatusToggleCount:N0}/tick");
        Console.WriteLine($"Warmup: {options.WarmupSeconds}s, Measure: {options.MeasureSeconds}s");
        Console.WriteLine();
        Console.WriteLine($"{"Engine",-10} | {"Ticks/s",10} | {"ms/tick",9} | {"Checksum",14} | {"Live",8} | {"Heap Δ",12} | {"GC",8} | {"Query",8} | {"Record",8} | {"Apply",8}");
        Console.WriteLine(new string('-', 116));

        RunAndPrint(new MiniArchSteadyCombatWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        RunAndPrint(new MiniArchCommandStreamSteadyCombatWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        RunAndPrint(CreateFrifloScenarioQuietly(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        RunAndPrint(new ArchSteadyCombatWorld(), options);
        Console.WriteLine();
        Console.WriteLine("=== ParticleStorm: high structural churn, minimal Set ===");
        Console.WriteLine($"Batch: {ParticleStormDefaults.BatchSize:N0}/tick, Lifespan: {ParticleStormDefaults.StormTickBuffer} ticks, Steady pool: {ParticleStormDefaults.InitialLiveCount - ParticleStormDefaults.EmitterCount:N0}");
        Console.WriteLine($"Emitters: {ParticleStormDefaults.EmitterCount}, Set: {ParticleStormDefaults.EmitterCount}/tick");
        Console.WriteLine();
        Console.WriteLine($"{"Engine",-18} | {"Ticks/s",10} | {"ms/tick",9} | {"Checksum",14} | {"Live",8} | {"Heap Δ",12} | {"GC",8} | {"Query",8} | {"Record",8} | {"Apply",8}");
        Console.WriteLine(new string('-', 122));
        RunAndPrint(new MiniArchParticleStormWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        RunAndPrint(new MiniArchCommandStreamParticleStormWorld(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        RunAndPrint(CreateFrifloParticleStormQuietly(), options);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        RunAndPrint(new ArchParticleStormWorld(), options);
        Console.WriteLine();
        Console.WriteLine("Command: dotnet run -c Release --project perf/CommandBufferGame.Perf -- --warmup 3 --measure 10");
    }

    private static void RunAndPrint(ICommandBufferGameScenario scenario, BenchmarkOptions options)
    {
        using (scenario)
        {
            var result = BenchmarkRunner.Run(scenario, options);
            Console.WriteLine($"{result.Engine,-10} | {result.TicksPerSecond,10:F1} | {result.MillisecondsPerTick,9:F3} | {result.Checksum,14} | {result.LiveCount,8:N0} | {FormatBytes(result.HeapDelta),12} | {result.GcCollections,8} | {result.QueryPercent,7:F1}% | {result.RecordPercent,7:F1}% | {result.ApplyPercent,7:F1}%");
        }
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

public readonly record struct BenchmarkOptions(int WarmupSeconds, int MeasureSeconds)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var warmup = 3;
        var measure = 10;

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
            }
        }

        return new BenchmarkOptions(warmup, measure);
    }
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
        sw.Restart();
        while (sw.Elapsed.TotalSeconds < options.MeasureSeconds)
        {
            checksum += scenario.RunTick();
            ticks++;
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

public sealed class MiniArchSteadyCombatWorld : ICommandBufferGameScenario
{
    private readonly MiniWorld _world = new(128, ScenarioDefaults.InitialLiveCount + ScenarioDefaults.ProjectileChurnPerTick * 4);
    private readonly MiniCommandBuffer _buffer;
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

    public MiniArchSteadyCombatWorld()
    {
        _buffer = new MiniCommandBuffer(_world);

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

    public string Engine => "MiniArch";
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
        _buffer.Submit();
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
            var entity = _buffer.Create();
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
// ParticleStorm scenarios — high structural churn, minimal Set
// ============================================================

public sealed class MiniArchParticleStormWorld : ICommandBufferGameScenario
{
    private readonly MiniWorld _world = new(128, ParticleStormDefaults.InitialLiveCount + ParticleStormDefaults.BatchSize);
    private readonly MiniCommandBuffer _buffer;
    private readonly MiniEntity[][] _stormSlots;
    private readonly MiniEntity[] _emitters;
    private readonly PhaseTicks _phases = new();
    private int _tick;
    private long _checksum;

    public MiniArchParticleStormWorld()
    {
        _buffer = new MiniCommandBuffer(_world);

        _stormSlots = new MiniEntity[ParticleStormDefaults.StormTickBuffer][];
        for (var i = 0; i < _stormSlots.Length; i++)
            _stormSlots[i] = new MiniEntity[ParticleStormDefaults.BatchSize];

        _emitters = new MiniEntity[ParticleStormDefaults.EmitterCount];
        for (var i = 0; i < _emitters.Length; i++)
            _emitters[i] = _world.Create(new Position(i, i), new EmitterTag(i));
    }

    public string Engine => "MiniArch-Storm";
    public int LiveCount => ParticleStormDefaults.EmitterCount + Math.Min(_tick, ParticleStormDefaults.StormTickBuffer) * ParticleStormDefaults.BatchSize;
    public long Checksum => _checksum;
    public PhaseTicks Phases => _phases;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunTick()
    {
        var tickStart = _checksum;
        var start = Stopwatch.GetTimestamp();

        // Query emitters (minimal — just for checksum & phase timing)
        for (var i = 0; i < _emitters.Length; i++)
            _checksum += _emitters[i].Id + i;
        var afterQuery = Stopwatch.GetTimestamp();

        RecordCommands();
        var afterRecord = Stopwatch.GetTimestamp();

        _buffer.Submit();
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
        // Destroy batch from (tick) % StormTickBuffer — entities created StormTickBuffer ticks ago
        if (_tick >= ParticleStormDefaults.StormTickBuffer)
        {
            var slot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
            for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
                _buffer.Destroy(slot[i]);
        }

        // Set emitter values (the only Set operation in this scenario)
        for (var i = 0; i < _emitters.Length; i++)
        {
            _buffer.Set(_emitters[i], new Position(_tick + i, _tick * 2 + i));
            _buffer.Set(_emitters[i], new Alpha((_tick + i) % 256));
        }

        // Create new batch with diversified archetype signatures
        var createSlot = _stormSlots[_tick % ParticleStormDefaults.StormTickBuffer];
        for (var i = 0; i < ParticleStormDefaults.BatchSize; i++)
        {
            var entity = _buffer.Create();
            _buffer.Add(entity, new Position(i, _tick));
            _buffer.Add(entity, new Velocity((i & 3) - 1, (i & 7) - 3));
            _checksum += i + _tick;

            var type = i % 10;
            if (type < 3)       // 30%: Position + Velocity + Alpha
                _buffer.Add(entity, new Alpha((i + _tick) % 256));
            else if (type < 6)  // 30%: Position + Velocity + Color + Scale
            {
                _buffer.Add(entity, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255));
                _buffer.Add(entity, new Scale(1 + (i % 10)));
            }
            else if (type < 8)  // 20%: Position + Velocity + Alpha + Lifetime
            {
                _buffer.Add(entity, new Alpha((i + _tick) % 256));
                _buffer.Add(entity, new Lifetime(2));
            }
            else                // 20%: Position + Velocity + Color + Scale + Lifetime
            {
                _buffer.Add(entity, new Color(i % 256, (i + 64) % 256, (i + 128) % 256, 255));
                _buffer.Add(entity, new Scale(1 + (i % 10)));
                _buffer.Add(entity, new Lifetime(2));
            }

            createSlot[i] = entity;
        }
    }
}

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
