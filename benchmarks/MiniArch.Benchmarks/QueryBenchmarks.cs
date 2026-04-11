using Arch.Core;
using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArch.Benchmarks;

using MiniQuery = MiniArch.Core.Query;
using MiniComponentType = MiniArch.Core.ComponentType;

public class QueryBenchmarks
{
    [Params(10_000, 50_000, 100_000)]
    public int EntityCount { get; set; }

    private MiniComplexQueryWorldState _miniState = null!;
    private ArchComplexQueryWorldState _archState = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _miniState = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(EntityCount);
        _archState = BenchmarkWorldFactory.CreateArchComplexQueryWorld(EntityCount);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _archState.Dispose();
    }

    [Benchmark(Description = "Arch complex query WithAll execute")]
    public int Arch_WithAll_Execute()
    {
        var description = new QueryDescription()
            .WithAll<Position, Velocity, Health, Team>();

        return ExecuteArchQuery(description);
    }

    [Benchmark(Description = "MiniArch complex query WithAll execute")]
    public int MiniArch_WithAll_Execute()
    {
        var query = _miniState.World.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Build();

        return ExecuteMiniQuery(query);
    }

    [Benchmark(Description = "Arch complex query WithAll execute warmed")]
    public int Arch_WithAll_Execute_Warmed()
    {
        return ExecuteArchQuery(_archState.WithAllDescription);
    }

    [Benchmark(Description = "MiniArch complex query WithAll execute warmed")]
    public int MiniArch_WithAll_Execute_Warmed()
    {
        return ExecuteMiniQuery(_miniState.WithAllQuery);
    }

    [Benchmark(Description = "MiniArch complex query WithAll components execute warmed row-wise")]
    public int MiniArch_WithAll_Components_Execute_Warmed_RowWise()
    {
        return ExecuteMiniComponentQueryRowWise(_miniState.WithAllQuery, _miniState.PositionType, _miniState.VelocityType);
    }

    [Benchmark(Description = "MiniArch complex query WithAll components execute warmed span")]
    public int MiniArch_WithAll_Components_Execute_Warmed_Span()
    {
        return ExecuteMiniComponentQuerySpan(_miniState.WithAllQuery, _miniState.PositionType, _miniState.VelocityType);
    }

    [Benchmark(Description = "Arch complex query WithAll components execute warmed span")]
    public int Arch_WithAll_Components_Execute_Warmed_Span()
    {
        return ExecuteArchComponentQuerySpan(_archState.WithAllDescription);
    }

    [Benchmark(Description = "Arch complex query WithAll+Without execute")]
    public int Arch_WithAll_Without_Execute()
    {
        var description = new QueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithNone<ExcludedTag>();

        return ExecuteArchQuery(description);
    }

    [Benchmark(Description = "MiniArch complex query WithAll+Without execute")]
    public int MiniArch_WithAll_Without_Execute()
    {
        var query = _miniState.World.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Without<ExcludedTag>()
            .Build();

        return ExecuteMiniQuery(query);
    }

    [Benchmark(Description = "Arch complex query WithAll+Without execute warmed")]
    public int Arch_WithAll_Without_Execute_Warmed()
    {
        return ExecuteArchQuery(_archState.WithAllWithoutDescription);
    }

    [Benchmark(Description = "MiniArch complex query WithAll+Without execute warmed")]
    public int MiniArch_WithAll_Without_Execute_Warmed()
    {
        return ExecuteMiniQuery(_miniState.WithAllWithoutQuery);
    }

    [Benchmark(Description = "Arch complex query WithAll+Any execute")]
    public int Arch_WithAll_Any_Execute()
    {
        var description = new QueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithAny<AnyTagA, AnyTagB>();

        return ExecuteArchQuery(description);
    }

    [Benchmark(Description = "MiniArch complex query WithAll+Any execute")]
    public int MiniArch_WithAll_Any_Execute()
    {
        var query = _miniState.World.Query()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Any<AnyTagA>()
            .Or<AnyTagB>()
            .Build();

        return ExecuteMiniQuery(query);
    }

    [Benchmark(Description = "Arch complex query WithAll+Any execute warmed")]
    public int Arch_WithAll_Any_Execute_Warmed()
    {
        return ExecuteArchQuery(_archState.WithAllAnyDescription);
    }

    [Benchmark(Description = "MiniArch complex query WithAll+Any execute warmed")]
    public int MiniArch_WithAll_Any_Execute_Warmed()
    {
        return ExecuteMiniQuery(_miniState.WithAllAnyQuery);
    }

    private int ExecuteMiniQuery(MiniQuery query)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var entities = chunk.GetEntities();
            for (var row = 0; row < entities.Length; row++)
            {
                checksum += entities[row].Id;
            }
        }

        return checksum;
    }

    private static int ExecuteMiniComponentQueryRowWise(MiniQuery query, MiniComponentType positionType, MiniComponentType velocityType)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            for (var row = 0; row < chunk.Count; row++)
            {
                var position = chunk.GetComponent<Position>(positionType, row);
                var velocity = chunk.GetComponent<Velocity>(velocityType, row);
                checksum += position.X + velocity.Y;
            }
        }

        return checksum;
    }

    private static int ExecuteMiniComponentQuerySpan(MiniQuery query, MiniComponentType positionType, MiniComponentType velocityType)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var positions = chunk.GetComponentSpan<Position>(positionType);
            var velocities = chunk.GetComponentSpan<Velocity>(velocityType);
            for (var row = 0; row < positions.Length; row++)
            {
                checksum += positions[row].X + velocities[row].Y;
            }
        }

        return checksum;
    }

    private int ExecuteArchQuery(QueryDescription description)
    {
        var checksum = 0;
        var query = _archState.World.Query(in description);
        foreach (var chunk in query)
        {
            for (var row = 0; row < chunk.Count; row++)
            {
                checksum += chunk.Entity(row).Id;
            }
        }

        return checksum;
    }

    private int ExecuteArchComponentQuerySpan(QueryDescription description)
    {
        var checksum = 0;
        var query = _archState.World.Query(in description);
        foreach (var chunk in query)
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var row = 0; row < positions.Length; row++)
            {
                checksum += positions[row].X + velocities[row].Y;
            }
        }

        return checksum;
    }
}
