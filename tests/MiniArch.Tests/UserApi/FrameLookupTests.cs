using System;
using Xunit;

namespace MiniArchTests.UserApi;

public sealed class FrameLookupTests
{
    private readonly record struct GridCell(int X, int Y);
    private readonly record struct Health(int Value);

    private readonly struct CellKeySelector : IFrameKeySelector<(int X, int Y)>
    {
        public (int X, int Y) Select(Entity entity, ReadOnlySpan<ChunkView> chunks, int ci, int ri)
        {
            ref var cell = ref chunks[ci].GetSpan<GridCell>()[ri];
            return (cell.X, cell.Y);
        }
    }

    [Fact]
    public void Build_and_query_by_key()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        for (var i = 0; i < 5; i++)
            world.Create(new GridCell(i, 0), new Health(i * 10));

        var lookup = new FrameLookup<(int X, int Y)>();
        lookup.Build(world, query, new CellKeySelector());

        Assert.Equal(5, lookup.KeyCount);

        var rowSpan = lookup[(0, 0)];
        Assert.Equal(1, rowSpan.Length);
    }

    [Fact]
    public void Read_component_via_RowRef()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>().With<Health>();

        world.Create(new GridCell(0, 0), new Health(42));
        world.Create(new GridCell(1, 0), new Health(99));

        var lookup = new FrameLookup<(int X, int Y)>();
        lookup.Build(world, query, new CellKeySelector());
        var chunks = world.Query(query).GetChunks();

        foreach (ref readonly var rr in lookup[(0, 0)])
        {
            ref var hp = ref rr.Component<Health>(chunks);
            Assert.Equal(42, hp.Value);
        }
    }

    [Fact]
    public void Clear_resets_lookup()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        world.Create(new GridCell(0, 0), new Health(10));

        var lookup = new FrameLookup<(int X, int Y)>();
        lookup.Build(world, query, new CellKeySelector());
        Assert.Equal(1, lookup.KeyCount);

        lookup.Clear();
        Assert.Equal(0, lookup.KeyCount);
        Assert.Equal(0, lookup[(0, 0)].Length);
    }

    [Fact]
    public void Missing_key_returns_empty_span()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        world.Create(new GridCell(5, 5), new Health(10));

        var lookup = new FrameLookup<(int X, int Y)>();
        lookup.Build(world, query, new CellKeySelector());

        Assert.Equal(0, lookup[(0, 0)].Length);
    }

    [Fact]
    public void BUG_full_lookup_missing_key_returns_empty_span()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        for (var i = 0; i < 16; i++)
            world.Create(new GridCell(i, 0));

        var lookup = new FrameLookup<(int X, int Y)>();
        Assert.True(lookup.TryBuild(world, query, new CellKeySelector()));
        Assert.Equal(16, lookup.KeyCount);

        Assert.Equal(0, lookup[(16, 0)].Length);
    }

    [Fact]
    public void BUG_generation_wrap_does_not_make_empty_slots_look_occupied()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();
        world.Create(new GridCell(1, 0));

        var lookup = new FrameLookup<(int X, int Y)>();
        var generation = lookup.GetType().GetField(
            "_generation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        generation.SetValue(lookup, -1);

        Assert.True(lookup.TryBuild(world, query, new CellKeySelector()));
        Assert.Equal(1, lookup[(1, 0)].Length);
    }

    [Fact]
    public void Empty_world_returns_zero()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        var lookup = new FrameLookup<(int X, int Y)>();
        lookup.Build(world, query, new CellKeySelector());

        Assert.Equal(0, lookup.KeyCount);
        Assert.Equal(0, lookup.RowCount);
    }

    [Fact]
    public void TryBuild_fails_on_insufficient_capacity()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        for (var i = 0; i < 100; i++)
            world.Create(new GridCell(i, 0));

        var lookup = new FrameLookup<(int X, int Y)>();

        Assert.False(lookup.TryBuild(world, query, new CellKeySelector()));
        Assert.Equal(0, lookup.KeyCount);
        Assert.Equal(0, lookup.RowCount);
    }

    [Fact]
    public void Rebuild_replaces_previous_result()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        world.Create(new GridCell(0, 0), new Health(10));

        var lookup = new FrameLookup<(int X, int Y)>();
        lookup.Build(world, query, new CellKeySelector());
        Assert.Equal(1, lookup[(0, 0)].Length);

        lookup.Clear();
        Assert.Equal(0, lookup.KeyCount);
    }
}
