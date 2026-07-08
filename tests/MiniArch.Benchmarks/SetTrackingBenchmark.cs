using BenchmarkDotNet.Attributes;
using MiniArch;
using MiniArch.Core;

namespace MiniArchBenchmarks;

/// <summary>
/// Measures the overhead of change tracking on World.Set&lt;T&gt; and structural operations.
/// Tracking OFF = baseline (default). Tracking ON = after TrackValueChanges/Transitions.
/// </summary>
public class SetTrackingBenchmark
{
    private const int SetEntityCount = 10000;
    private const int CreateDestroyCount = 1000;

    private World _world = null!;
    private Entity[] _entities = null!;
    private SharedValueChanges<Position>? _valueTracker;
    private TransitionLog? _transitionLog;

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
            _valueTracker = _world.TrackValueChanges<Position>();
    }

    [Benchmark(Description = "Set Position (10k ops)")]
    public void SetPosition()
    {
        var world = _world;
        var entities = _entities;
        for (var i = 0; i < entities.Length; i++)
            world.Set(entities[i], new Position(i + 1, i + 1));
    }

    // === CreateDestroy benchmark ===

    [IterationSetup(Target = nameof(CreateDestroy))]
    public void SetupCreateDestroy()
    {
        _world = new World();
        if (Tracked)
        {
            _valueTracker = _world.TrackValueChanges<Position>();
            _transitionLog = _world.TrackTransitions(new QueryDescription().With<Position>());
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
    }
}
