using Arch.Core;
using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

public class QueryBenchmarks
{
    [Params(1000)]
    public int EntityCount { get; set; }

    private MiniWorldState _miniState = null!;
    private ArchWorldState _archState = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _miniState = BenchmarkWorldFactory.CreateMiniWorldWithPositionAndVelocity(EntityCount);
        _archState = BenchmarkWorldFactory.CreateArchWorldWithPositionAndVelocity(EntityCount);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _archState.Dispose();
    }

    [Benchmark(Description = "Arch.QueryDescription.WithAll<Position,Velocity>")]
    public QueryDescription Arch_Query_Create()
    {
        return new QueryDescription().WithAll<Position, Velocity>();
    }

    [Benchmark(Description = "MiniArch.World.Query().With<Position>().With<Velocity>()")]
    public QueryBuilder MiniArch_Query_Create()
    {
        return _miniState.World.Query().With<Position>().With<Velocity>();
    }
}
