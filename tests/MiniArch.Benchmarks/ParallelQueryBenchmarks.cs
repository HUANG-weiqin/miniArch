using System.Threading;
using Arch.Core;
using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using ArchQueryDescription = Arch.Core.QueryDescription;
using MiniQuery = MiniArch.Core.Query;
using MiniQueryDescription = MiniArch.QueryDescription;

/// <summary>
/// Compares sequential vs parallel chunk-level query iteration for MiniArch and Arch.
/// Workload: Position += Velocity (read + write per row).
/// </summary>
public class ParallelQueryBenchmarks
{
    [Params(10_000, 50_000, 100_000)]
    public int EntityCount { get; set; }

    private MiniWorldState _miniState = null!;
    private ArchWorldState _archState = null!;
    private MiniArch.Query _miniQuery;
    private ArchQueryDescription _archDescription;
    private Chunk[] _archChunks = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        _miniState = BenchmarkWorldFactory.CreateMiniWorldWithPositionAndVelocity(EntityCount);
        _archState = BenchmarkWorldFactory.CreateArchWorldWithPositionAndVelocity(EntityCount);

        var desc = new MiniQueryDescription().With<Position>().With<Velocity>();
        _miniQuery = _miniState.World.Query(in desc);
        _ = _miniQuery.GetChunks();

        _archDescription = new ArchQueryDescription().WithAll<Position, Velocity>();
        var archQuery = _archState.World.Query(in _archDescription);
        // Snapshot chunks for stable parallel indexing.
        var chunkList = new List<Chunk>();
        foreach (var c in archQuery)
            chunkList.Add(c);
        _archChunks = chunkList.ToArray();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _archState.Dispose();
    }

    [Benchmark(Description = "MiniArch ForEachChunk (sequential)", Baseline = true)]
    public int MiniArch_Sequential()
    {
        var sum = 0;
        _miniQuery.ForEachChunk(chunk =>
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = new Position(positions[i].X + velocities[i].X,
                                            positions[i].Y + velocities[i].Y);
                sum += positions[i].X;
            }
        });
        return sum;
    }

    [Benchmark(Description = "MiniArch ForEachChunkParallel")]
    public int MiniArch_Parallel()
    {
        var sum = 0;
        _miniQuery.ForEachChunkParallel(chunk =>
        {
            var local = 0;
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = new Position(positions[i].X + velocities[i].X,
                                            positions[i].Y + velocities[i].Y);
                local += positions[i].X;
            }
            Interlocked.Add(ref sum, local);
        });
        return sum;
    }

    [Benchmark(Description = "Arch foreach chunk (sequential)")]
    public int Arch_Sequential()
    {
        var sum = 0;
        foreach (var chunk in _archState.World.Query(in _archDescription))
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = new Position(positions[i].X + velocities[i].X,
                                            positions[i].Y + velocities[i].Y);
                sum += positions[i].X;
            }
        }
        return sum;
    }

    [Benchmark(Description = "Arch Parallel.For over chunks")]
    public int Arch_Parallel()
    {
        var sum = 0;
        var chunks = _archChunks;
        Parallel.For(0, chunks.Length, i =>
        {
            var chunk = chunks[i];
            var local = 0;
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var r = 0; r < positions.Length; r++)
            {
                positions[r] = new Position(positions[r].X + velocities[r].X,
                                            positions[r].Y + velocities[r].Y);
                local += positions[r].X;
            }
            Interlocked.Add(ref sum, local);
        });
        return sum;
    }
}
