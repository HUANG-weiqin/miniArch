using BenchmarkDotNet.Attributes;
using MiniArch.Core;

namespace MiniArchBenchmarks;

using MiniWorld = MiniArch.World;

/// <summary>
/// Compares per-entity Create+Add loops against CreateMany v2 bulk materialize,
/// in both submit (materialize) and async (materialize + snapshot) modes, for
/// 8-component entities. Designed to isolate record+submit cost from world setup.
/// </summary>
public class CreateManyBenchmarks
{
    [Params(1000, 10000, 30000)]
    public int EntityCount { get; set; }

    private MiniWorld _world = null!;
    private Entity[] _entities = null!;

    // Each IterationSetup creates a fresh world so Submit always starts from a
    // clean slate. The entities[] buffer is pre-allocated so only record+submit
    // is measured.
    [IterationSetup]
    public void IterationSetup()
    {
        _world = new MiniWorld(entityCapacity: EntityCount);
        _entities = new Entity[EntityCount];
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _world = null!;
        _entities = null!;
    }

    // ── Per-entity baseline: Create() + 8 Add() calls per entity, then Submit ──

    [Benchmark(Description = "Per-entity Create+Add x8 Submit")]
    public void PerEntity_Submit()
    {
        var stream = new CommandStream(_world);
        for (var i = 0; i < EntityCount; i++)
        {
            _entities[i] = stream.Create();
            stream.Add(_entities[i], new BmPos(i, i + 1));
            stream.Add(_entities[i], new BmVel(i + 2, i + 3));
            stream.Add(_entities[i], new BmHealth(i + 4));
            stream.Add(_entities[i], new BmSig(i, i + 1, i + 2));
            stream.Add(_entities[i], new BmC4(i + 5));
            stream.Add(_entities[i], new BmC5(i + 6));
            stream.Add(_entities[i], new BmC6(i + 7));
            stream.Add(_entities[i], new BmC7(i + 8));
        }
        stream.Submit();
    }

    [Benchmark(Description = "Per-entity Create+Add x8 Async")]
    public async Task PerEntity_Async()
    {
        var stream = new CommandStream(_world);
        for (var i = 0; i < EntityCount; i++)
        {
            _entities[i] = stream.Create();
            stream.Add(_entities[i], new BmPos(i, i + 1));
            stream.Add(_entities[i], new BmVel(i + 2, i + 3));
            stream.Add(_entities[i], new BmHealth(i + 4));
            stream.Add(_entities[i], new BmSig(i, i + 1, i + 2));
            stream.Add(_entities[i], new BmC4(i + 5));
            stream.Add(_entities[i], new BmC5(i + 6));
            stream.Add(_entities[i], new BmC6(i + 7));
            stream.Add(_entities[i], new BmC7(i + 8));
        }
        await stream.SubmitAndSnapshotAsync();
    }

    // ── CreateMany v2: single CreateMany call, then Submit ──

    [Benchmark(Description = "CreateMany x8 Submit")]
    public void CreateMany_Submit()
    {
        var stream = new CommandStream(_world);
        stream.CreateMany<BmPos, BmVel, BmHealth, BmSig, BmC4, BmC5, BmC6, BmC7,
            EightComponentBenchWriter>(_entities, new EightComponentBenchWriter());
        stream.Submit();
    }

    [Benchmark(Description = "CreateMany x8 Async")]
    public async Task CreateMany_Async()
    {
        var stream = new CommandStream(_world);
        stream.CreateMany<BmPos, BmVel, BmHealth, BmSig, BmC4, BmC5, BmC6, BmC7,
            EightComponentBenchWriter>(_entities, new EightComponentBenchWriter());
        await stream.SubmitAndSnapshotAsync();
    }

    // ── Component types (must be distinct from other benchmark types to get
    //    their own component ids, so archetype lookup isn't accidentally cached) ──

    private readonly record struct BmPos(int X, int Y);
    private readonly record struct BmVel(int X, int Y);
    private readonly record struct BmHealth(int Value);
    private readonly record struct BmSig(long A, long B, long C);
    private readonly record struct BmC4(int Value);
    private readonly record struct BmC5(int Value);
    private readonly record struct BmC6(int Value);
    private readonly record struct BmC7(int Value);

    private readonly struct EightComponentBenchWriter
        : ICreateManyWriter<BmPos, BmVel, BmHealth, BmSig, BmC4, BmC5, BmC6, BmC7>
    {
        public void Write(int index, Entity entity,
            out BmPos c1, out BmVel c2, out BmHealth c3, out BmSig c4,
            out BmC4 c5, out BmC5 c6, out BmC6 c7, out BmC7 c8)
        {
            c1 = new BmPos(index, index + 1);
            c2 = new BmVel(index + 2, index + 3);
            c3 = new BmHealth(index + 4);
            c4 = new BmSig(index, index + 1, index + 2);
            c5 = new BmC4(index + 5);
            c6 = new BmC5(index + 6);
            c7 = new BmC6(index + 7);
            c8 = new BmC7(index + 8);
        }
    }
}
