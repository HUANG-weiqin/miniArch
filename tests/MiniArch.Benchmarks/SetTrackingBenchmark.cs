using BenchmarkDotNet.Attributes;
using MiniArch;
using MiniArch.Core;

namespace MiniArchBenchmarks;

/// <summary>
/// Measures the overhead of change tracking on World.Set&lt;T&gt; and structural operations.
/// Tracking OFF = baseline (default). Tracking ON = after Watch setup.
/// </summary>
public class SetTrackingBenchmark
{
    private const int SetEntityCount = 10000;
    private const int CreateDestroyCount = 1000;

    private World _world = null!;
    private Entity[] _entities = null!;
    private ChangeWatch<Position, SetTrackingHandler>? _valueWatch;
    private TransitionWatch<SetTrackingTransitionHandler>? _transitionWatch;

    [Params(false, true)]
    public bool Tracked { get; set; }

    // === Set benchmark ===

    [IterationSetup(Target = nameof(SetPosition))]
    public void SetupSet()
    {
        _world = new World();
        _entities = new Entity[SetEntityCount];
        for (var i = 0; i < SetEntityCount; i++)
            _entities[i] = _world.Create(new Position(i, i));

        if (Tracked)
        {
            _valueWatch = _world.Watch<Position, SetTrackingHandler>();
            _valueWatch.Snapshot(_world);
        }
    }

    [Benchmark(Description = "Set Position (10k ops)")]
    public void SetPosition()
    {
        var world = _world;
        var entities = _entities;
        for (var i = 0; i < entities.Length; i++)
            world.Set(entities[i], new Position(i + 1, i + 1));

        if (_valueWatch is not null)
            _valueWatch.Diff(world);
    }

    // === CreateDestroy benchmark ===

    [IterationSetup(Target = nameof(CreateDestroy))]
    public void SetupCreateDestroy()
    {
        _world = new World();
        if (Tracked)
        {
            _valueWatch = _world.Watch<Position, SetTrackingHandler>();
            _valueWatch.Snapshot(_world);
            _transitionWatch = _world.Watch<SetTrackingTransitionHandler>(
                new QueryDescription().With<Position>());
            _transitionWatch.Snapshot(_world);
        }
    }

    [Benchmark(Description = "Create+Destroy entity (1k ops)")]
    public void CreateDestroy()
    {
        var world = _world;
        for (var i = 0; i < CreateDestroyCount; i++)
        {
            var e = world.Create(new Position(i, i));
            world.Destroy(e);
        }

        if (_valueWatch is not null)
            _valueWatch.Diff(world);
        if (_transitionWatch is not null)
            _transitionWatch.Diff(world);
    }
}

// Local handler structs to prevent JIT dead-code elimination
struct SetTrackingHandler : IChangeHandler<Position>
{
    public int TotalChanges;
    public int Checksum;

    public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
    {
        TotalChanges++;
        Checksum = HashCode.Combine(Checksum, entity.Id, oldValue.X, newValue.X);
    }
}

struct SetTrackingTransitionHandler : ITransitionHandler
{
    public int TotalChanges;
    public int Checksum;

    public void OnChange(World world, Entity entity, TransitionKind kind)
    {
        TotalChanges++;
        Checksum = HashCode.Combine(Checksum, entity.Id, (int)kind);
    }
}
