using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Arch.Core;
using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using ArchQueryDescription = Arch.Core.QueryDescription;
using MiniQuery = MiniArch.Core.Query;
using MiniComponentType = MiniArch.Core.ComponentType;
using MiniQueryDescription = MiniArch.QueryDescription;

public class QueryBenchmarks
{
    [Params(128, 256, 512, 1024, 2048, 10_000, 50_000, 100_000)]
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
        var description = new ArchQueryDescription()
            .WithAll<Position, Velocity, Health, Team>();

        return ExecuteArchQuery(description);
    }

    [Benchmark(Description = "MiniArch complex query WithAll execute")]
    public int MiniArch_WithAll_Execute()
    {
        var description = new MiniQueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>();
        var query = MiniQuery.Create(_miniState.World, in description);

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

    [Benchmark(Description = "MiniArch complex query WithAll components execute warmed span direct")]
    public int MiniArch_WithAll_Components_Execute_Warmed_Span_Direct()
    {
        return ExecuteMiniComponentQuerySpanDirect(_miniState.WithAllQuery, _miniState.PositionType, _miniState.VelocityType);
    }

    [Benchmark(Description = "Arch complex query WithAll components execute warmed span")]
    public int Arch_WithAll_Components_Execute_Warmed_Span()
    {
        return ExecuteArchComponentQuerySpan(_archState.WithAllDescription);
    }

    [Benchmark(Description = "Arch complex query WithAll+Without execute")]
    public int Arch_WithAll_Without_Execute()
    {
        var description = new ArchQueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithNone<ExcludedTag>();

        return ExecuteArchQuery(description);
    }

    [Benchmark(Description = "MiniArch complex query WithAll+Without execute")]
    public int MiniArch_WithAll_Without_Execute()
    {
        var description = new MiniQueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Without<ExcludedTag>();
        var query = MiniQuery.Create(_miniState.World, in description);

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
        var description = new ArchQueryDescription()
            .WithAll<Position, Velocity, Health, Team>()
            .WithAny<AnyTagA, AnyTagB>();

        return ExecuteArchQuery(description);
    }

    [Benchmark(Description = "MiniArch complex query WithAll+Any execute")]
    public int MiniArch_WithAll_Any_Execute()
    {
        var description = new MiniQueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .WithAny<AnyTagA>()
            .Or<AnyTagB>();
        var query = MiniQuery.Create(_miniState.World, in description);

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

    [Benchmark(Description = "MiniArch complex query WithAll execute warmed SIMD")]
    public int MiniArch_WithAll_Execute_Warmed_SIMD()
    {
        return ExecuteMiniQuerySimd(_miniState.WithAllQuery);
    }

    private static int ExecuteMiniQuerySimd(MiniQuery query)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        var vecSize = Vector<int>.Count;
        Span<int> gatherBuf = stackalloc int[vecSize];
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var entities = chunk.GetEntities();
            var ids = MemoryMarshal.Cast<MiniArch.Entity, int>(entities);
            var count = entities.Length;

            var acc = Vector<int>.Zero;
            var i = 0;
            var batchLimit = count - (count % vecSize);
            for (; i < batchLimit; i += vecSize)
            {
                for (var j = 0; j < vecSize; j++)
                {
                    gatherBuf[j] = ids[(i + j) * 2];
                }
                acc += new Vector<int>(gatherBuf);
            }
            checksum += Vector.Dot(acc, Vector<int>.One);

            for (; i < count; i++)
            {
                checksum += ids[i * 2];
            }
        }

        return checksum;
    }

    [Benchmark(Description = "MiniArch complex query WithAll components execute warmed span SIMD")]
    public int MiniArch_WithAll_Components_Execute_Warmed_Span_SIMD()
    {
        return ExecuteMiniComponentQuerySpanSimd(_miniState.WithAllQuery, _miniState.PositionType, _miniState.VelocityType);
    }

    private static int ExecuteMiniComponentQuerySpanSimd(MiniQuery query, MiniComponentType positionType, MiniComponentType velocityType)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        var vecSize = Vector<int>.Count;
        Span<int> gatherBuf = stackalloc int[vecSize];
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var positions = chunk.GetComponentSpan<Position>(positionType);
            var velocities = chunk.GetComponentSpan<Velocity>(velocityType);
            var posData = MemoryMarshal.Cast<Position, int>(positions);
            var velData = MemoryMarshal.Cast<Velocity, int>(velocities);
            var count = positions.Length;

            var accX = Vector<int>.Zero;
            var accY = Vector<int>.Zero;
            var i = 0;
            var batchLimit = count - (count % vecSize);
            for (; i < batchLimit; i += vecSize)
            {
                for (var j = 0; j < vecSize; j++)
                {
                    gatherBuf[j] = posData[(i + j) * 2];
                }
                accX += new Vector<int>(gatherBuf);
                for (var j = 0; j < vecSize; j++)
                {
                    gatherBuf[j] = velData[(i + j) * 2 + 1];
                }
                accY += new Vector<int>(gatherBuf);
            }
            checksum += Vector.Dot(accX, Vector<int>.One) + Vector.Dot(accY, Vector<int>.One);

            for (; i < count; i++)
            {
                checksum += posData[i * 2] + velData[i * 2 + 1];
            }
        }

        return checksum;
    }

    private static int ExecuteMiniComponentQueryRowWise(MiniQuery query, MiniComponentType positionType, MiniComponentType velocityType)
    {
        var checksum = 0;
        var chunks = query.GetChunkSpan();
        Span<MiniComponentType> componentTypes = stackalloc MiniComponentType[2] { positionType, velocityType };
        Span<int> columnIndices = stackalloc int[2];

        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            if (!chunk.TryGetColumnIndices(componentTypes, columnIndices))
            {
                continue;
            }

            var positionColumnIndex = columnIndices[0];
            var velocityColumnIndex = columnIndices[1];

            for (var row = 0; row < chunk.Count; row++)
            {
                var position = chunk.GetComponentAt<Position>(positionColumnIndex, row);
                var velocity = chunk.GetComponentAt<Velocity>(velocityColumnIndex, row);
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

    private static int ExecuteMiniComponentQuerySpanDirect(MiniQuery query, MiniComponentType positionType, MiniComponentType velocityType)
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

    private int ExecuteArchQuery(ArchQueryDescription description)
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

    private int ExecuteArchComponentQuerySpan(ArchQueryDescription description)
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
