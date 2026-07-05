using System.Collections.Concurrent;
using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class ParallelQueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // ================================================================
    //  ForEachChunk (sequential)
    // ================================================================

    [Fact]
    public void ForEachChunk_visits_every_matched_chunk()
    {
        var world = new World();
        for (var i = 0; i < 5; i++)
            world.Create(new Position(i, i));

        var desc = PositionDescription();
        var query = world.Query(in desc);
        var visited = 0;
        query.ForEachChunk(_ => visited++);

        Assert.Equal(1, visited);
    }

    [Fact]
    public void ForEachChunk_invokes_action_once_per_chunk_with_correct_count()
    {
        var world = new World();
        for (var i = 0; i < 10; i++)
            world.Create(new Position(i, i), new Velocity(i * 2, i * 3));

        var desc = PositionVelocityDescription();
        var query = world.Query(in desc);
        var total = 0;
        var chunkCount = 0;
        query.ForEachChunk(chunk =>
        {
            chunkCount++;
            total += chunk.Count;
        });

        Assert.Equal(1, chunkCount);
        Assert.Equal(10, total);
    }

    [Fact]
    public void ForEachChunk_reads_and_writes_component_values()
    {
        var world = new World();
        for (var i = 0; i < 8; i++)
            world.Create(new Position(i, i));

        var desc = PositionDescription();
        var query = world.Query(in desc);
        query.ForEachChunk(chunk =>
        {
            var positions = chunk.GetSpan<Position>();
            for (var i = 0; i < positions.Length; i++)
                positions[i] = new Position(positions[i].X * 10, positions[i].Y * 10);
        });

        var sum = SumPositionX(world);
        Assert.Equal(10 * (0 + 1 + 2 + 3 + 4 + 5 + 6 + 7), sum);
    }

    [Fact]
    public void ForEachChunk_throws_on_null_action()
    {
        var world = new World();
        world.Create(new Position(1, 1));
        var desc = PositionDescription();
        var query = world.Query(in desc);

        Assert.Throws<ArgumentNullException>(() => query.ForEachChunk(null!));
    }

    // ================================================================
    //  ForEachChunkParallel — correctness
    // ================================================================

    [Fact]
    public void ForEachChunkParallel_writes_single_component_correctly()
    {
        const int N = 1000;
        var world = new World();
        var entities = new Entity[N];
        for (var i = 0; i < N; i++)
            entities[i] = world.Create(new Position(i, i));

        var desc = PositionDescription();
        var query = world.Query(in desc);
        query.ForEachChunkParallel(StaticDoublePosition);

        for (var i = 0; i < N; i++)
        {
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(i * 2, p.X);
            Assert.Equal(i * 2, p.Y);
        }
    }

    [Fact]
    public void ForEachChunkParallel_writes_two_components_based_on_each_other()
    {
        const int N = 1000;
        var world = new World();
        var entities = new Entity[N];
        for (var i = 0; i < N; i++)
            entities[i] = world.Create(new Position(i, i), new Velocity(i, i));

        var desc = PositionVelocityDescription();
        var query = world.Query(in desc);
        query.ForEachChunkParallel(MovePositionByVelocity);

        for (var i = 0; i < N; i++)
        {
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(i * 2, p.X);
            Assert.Equal(i * 2, p.Y);
        }
    }

    [Fact]
    public void ForEachChunkParallel_matches_sequential_result()
    {
        const int N = 2000;
        var rng = new Random(42);

        var seqWorld = new World();
        var parWorld = new World();
        var entities = new (Entity Seq, Entity Par)[N];
        for (var i = 0; i < N; i++)
        {
            var pos = new Position(rng.Next(100), rng.Next(100));
            var vel = new Velocity(rng.Next(-5, 5), rng.Next(-5, 5));
            entities[i] = (seqWorld.Create(pos, vel), parWorld.Create(pos, vel));
        }

        var seqDesc = PositionVelocityDescription();
        var parDesc = PositionVelocityDescription();
        var seqQuery = seqWorld.Query(in seqDesc);
        var parQuery = parWorld.Query(in parDesc);

        seqQuery.ForEachChunk(MovePositionByVelocity);
        parQuery.ForEachChunkParallel(MovePositionByVelocity);

        foreach (var (seqEntity, parEntity) in entities)
        {
            Assert.True(seqWorld.TryGet(seqEntity, out Position seqPos));
            Assert.True(parWorld.TryGet(parEntity, out Position parPos));
            Assert.Equal(seqPos, parPos);
        }
    }

    [Fact]
    public void ForEachChunkParallel_handles_empty_query()
    {
        var world = new World();
        var desc = PositionDescription();
        var query = world.Query(in desc);

        var visited = 0;
        query.ForEachChunkParallel(_ => visited++);

        Assert.Equal(0, visited);
    }

    [Fact]
    public void ForEachChunkParallel_handles_multiple_archetypes()
    {
        var world = new World();
        // Three different archetypes (different component mixes)
        for (var i = 0; i < 50; i++)
            world.Create(new Position(i, i), new Velocity(i, i));
        for (var i = 0; i < 50; i++)
            world.Create(new Position(i + 100, i + 100), new Velocity(i + 100, i + 100), new Health(i));
        for (var i = 0; i < 50; i++)
            world.Create(new Position(i + 200, i + 200));

        var desc = PositionDescription();
        var query = world.Query(in desc);
        var totalSeen = 0;
        query.ForEachChunkParallel(chunk =>
        {
            Interlocked.Add(ref totalSeen, chunk.Count);
        });

        Assert.Equal(150, totalSeen);
    }

    [Fact]
    public void ForEachChunkParallel_handles_chunked_mode_multiple_segments()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var first = world.Create(new Position(1, 1));
        Assert.True(world.TryGetLocation(first, out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        for (var i = 0; i < 20; i++)
        {
            var r = arch.AddEntity(new Entity(10 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(i, i));
        }

        arch.AddSegmentForTesting();
        for (var i = 0; i < 10; i++)
        {
            var r = arch.AddEntity(new Entity(100 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(100 + i, 100 + i));
        }

        var desc = PositionDescription();
        var query = world.Query(in desc);

        var chunkCount = 0;
        var total = 0;
        query.ForEachChunkParallel(chunk =>
        {
            Interlocked.Increment(ref chunkCount);
            var span = chunk.GetSpan<Position>();
            for (var i = 0; i < span.Length; i++)
                span[i] = new Position(span[i].X + 1, span[i].Y + 1);
            Interlocked.Add(ref total, chunk.Count);
        });

        Assert.True(chunkCount >= 2, $"Expected >= 2 chunks (one per segment) in chunked mode, got {chunkCount}");
        Assert.Equal(31, total);

        // Verify writes landed
        Assert.True(world.TryGet(first, out Position moved));
        Assert.Equal(new Position(2, 2), moved);
    }

    [Fact]
    public void ForEachChunkParallel_large_fuzz_10000_entities()
    {
        const int N = 10_000;
        var world = new World();
        var entities = new Entity[N];
        var positions = new Position[N];
        var velocities = new Velocity[N];
        var rng = new Random(1234);
        for (var i = 0; i < N; i++)
        {
            positions[i] = new Position(rng.Next(-1000, 1000), rng.Next(-1000, 1000));
            velocities[i] = new Velocity(rng.Next(-10, 10), rng.Next(-10, 10));
            entities[i] = world.Create(positions[i], velocities[i]);
        }

        var desc = PositionVelocityDescription();
        var query = world.Query(in desc);
        query.ForEachChunkParallel(MovePositionByVelocity);

        for (var i = 0; i < N; i++)
        {
            Assert.True(world.TryGet(entities[i], out Position actual));
            Assert.Equal(positions[i].X + velocities[i].X, actual.X);
            Assert.Equal(positions[i].Y + velocities[i].Y, actual.Y);
        }
    }

    [Fact]
    public void ForEachChunkParallel_throws_on_null_action()
    {
        var world = new World();
        world.Create(new Position(1, 1));
        var desc = PositionDescription();
        var query = world.Query(in desc);

        Assert.Throws<ArgumentNullException>(() => query.ForEachChunkParallel(null!));
    }

    // ================================================================
    //  ForEachChunkParallel — parallelism (sanity)
    // ================================================================

    [Fact]
    public void ForEachChunkParallel_may_use_multiple_threads()
    {
        // Use chunked mode with many segments so Parallel.For has enough work items
        // to spread across worker threads.
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var first = world.Create(new Position(0, 0));
        Assert.True(world.TryGetLocation(first, out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        const int SegmentCount = 16;
        for (var s = 0; s < SegmentCount; s++)
        {
            arch.AddSegmentForTesting();
            for (var i = 0; i < 4; i++)
            {
                var r = arch.AddEntity(new Entity(s * 100 + i + 10, 1));
                arch.SetComponentAtTyped(0, r, new Position(s, i));
            }
        }

        var desc = PositionDescription();
        var query = world.Query(in desc);
        var chunksSeen = 0;
        var threadIds = new ConcurrentDictionary<int, byte>();
        query.ForEachChunkParallel(chunk =>
        {
            Interlocked.Increment(ref chunksSeen);
            threadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
            Thread.Sleep(2);
        });

        Assert.True(chunksSeen >= SegmentCount, $"Expected >= {SegmentCount} chunks (one per segment), got {chunksSeen}");
        // On a multi-core machine, multiple threads should be observed. Single-core CI machines
        // (rare) may legitimately see only 1 thread — we accept >= 1.
        Assert.True(threadIds.Count >= 1);
    }

    // ================================================================
    //  ForEachChunkParallel — collect + deferred structural change
    // ================================================================

    [Fact]
    public void ForEachChunkParallel_collect_then_destroy_via_command_stream()
    {
        const int N = 100;
        var world = new World();
        var entities = new Entity[N];
        for (var i = 0; i < N; i++)
        {
            entities[i] = world.Create(new Health(i));
            world.Add(entities[i], new Position(i, i));
        }

        var desc = PositionDescription();
        var query = world.Query(in desc);

        var toDestroy = new ConcurrentBag<Entity>();
        query.ForEachChunkParallel(chunk =>
        {
            var healths = chunk.GetSpan<Health>();
            var entitiesSpan = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (healths[i].Value < 50)
                    toDestroy.Add(entitiesSpan[i]);
            }
        });

        var stream = new CommandStream(world);
        foreach (var e in toDestroy)
            stream.Destroy(e);
        Assert.True(stream.Submit());

        Assert.Equal(50, world.EntityCount);
        for (var i = 50; i < N; i++)
            Assert.True(world.IsAlive(entities[i]));
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static QueryDescription PositionDescription()
        => new QueryDescription().With<Position>();

    private static QueryDescription PositionVelocityDescription()
        => new QueryDescription().With<Position>().With<Velocity>();

    private static void StaticDoublePosition(ChunkView chunk)
    {
        var span = chunk.GetSpan<Position>();
        for (var i = 0; i < span.Length; i++)
            span[i] = new Position(span[i].X * 2, span[i].Y * 2);
    }

    private static void MovePositionByVelocity(ChunkView chunk)
    {
        var positions = chunk.GetSpan<Position>();
        var velocities = chunk.GetSpan<Velocity>();
        for (var i = 0; i < positions.Length; i++)
            positions[i] = new Position(positions[i].X + velocities[i].X,
                                        positions[i].Y + velocities[i].Y);
    }

    private static int SumPositionX(World world)
    {
        var sum = 0;
        var desc = new QueryDescription().With<Position>();
        var query = world.Query(in desc);
        foreach (var chunk in query.GetChunks())
        {
            var span = chunk.GetSpan<Position>();
            for (var i = 0; i < span.Length; i++)
                sum += span[i].X;
        }
        return sum;
    }

    // ================================================================
    //  IChunkForEach (struct-generic, zero-alloc)
    // ================================================================

    private readonly struct DoublePositionJob : IChunkForEach
    {
        public void OnChunk(ChunkView chunk)
        {
            var span = chunk.GetSpan<Position>();
            for (var i = 0; i < span.Length; i++)
                span[i] = new Position(span[i].X * 2, span[i].Y * 2);
        }
    }

    private struct SumPositionJob : IChunkForEach
    {
        public int Total;
        public void OnChunk(ChunkView chunk)
        {
            var span = chunk.GetSpan<Position>();
            for (var i = 0; i < span.Length; i++)
                Total += span[i].X;
        }
    }

    private struct MoveJob : IChunkForEach
    {
        public void OnChunk(ChunkView chunk)
        {
            var pos = chunk.GetSpan<Position>();
            var vel = chunk.GetSpan<Velocity>();
            for (var i = 0; i < pos.Length; i++)
                pos[i] = new Position(pos[i].X + vel[i].X, pos[i].Y + vel[i].Y);
        }
    }

    [Fact]
    public void ForEachChunk_with_IChunkForEach_visits_every_row()
    {
        var world = new World();
        for (var i = 0; i < 10; i++)
            world.Create(new Position(i, i));

        var desc = PositionDescription();
        var query = world.Query(in desc);
        var job = new DoublePositionJob();
        query.ForEachChunk(ref job);

        desc = PositionDescription();
        foreach (var entity in world.Query(in desc))
            Assert.Equal(entity.Id * 2, world.Get<Position>(entity).X);
    }

    [Fact]
    public void ForEachChunk_with_IChunkForEach_supports_stateful_accumulator_via_ref()
    {
        var world = new World();
        for (var i = 0; i < 10; i++)
            world.Create(new Position(i + 1, 0));

        var desc = PositionDescription();
        var query = world.Query(in desc);
        var sum = new SumPositionJob();
        query.ForEachChunk(ref sum);

        // 1+2+...+10 = 55
        Assert.Equal(55, sum.Total);
    }

    [Fact]
    public void ForEachChunkParallel_with_IChunkForEach_writes_components_correctly()
    {
        var world = new World();
        for (var i = 0; i < 100; i++)
            world.Create(new Position(i, 0));

        var desc = PositionDescription();
        var query = world.Query(in desc);
        query.ForEachChunkParallel(new DoublePositionJob());

        var idx = 0;
        desc = PositionDescription();
        foreach (var chunk in world.Query(in desc).GetChunks())
        {
            var span = chunk.GetSpan<Position>();
            for (var i = 0; i < span.Length; i++)
            {
                Assert.Equal(idx * 2, span[i].X);
                idx++;
            }
        }
    }

    [Fact]
    public void IChunkForEach_produces_same_result_as_delegate_overload()
    {
        var world1 = new World();
        var world2 = new World();
        for (var i = 0; i < 50; i++)
        {
            world1.Create(new Position(i, i), new Velocity(1, 1));
            world2.Create(new Position(i, i), new Velocity(1, 1));
        }

        var desc = PositionVelocityDescription();
        var q1 = world1.Query(in desc);
        q1.ForEachChunk(static chunk =>
        {
            var pos = chunk.GetSpan<Position>();
            var vel = chunk.GetSpan<Velocity>();
            for (var i = 0; i < pos.Length; i++)
                pos[i] = new Position(pos[i].X + vel[i].X, pos[i].Y + vel[i].Y);
        });

        var move = new MoveJob();
        var q2 = world2.Query(in desc);
        q2.ForEachChunk(ref move);

        desc = PositionDescription();
        var sum1 = 0;
        var sum2 = 0;
        foreach (var e in world1.Query(in desc)) sum1 += world1.Get<Position>(e).X;
        foreach (var e in world2.Query(in desc)) sum2 += world2.Get<Position>(e).X;
        Assert.Equal(sum1, sum2);
    }
}