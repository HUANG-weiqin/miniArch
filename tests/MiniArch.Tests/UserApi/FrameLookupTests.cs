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

    private readonly struct XKeySelector : IFrameKeySelector<int>
    {
        public int Select(Entity entity, ReadOnlySpan<ChunkView> chunks, int ci, int ri)
            => chunks[ci].GetSpan<GridCell>()[ri].X;
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

    [Fact]
    public void BUG_full_lookup_accepts_additional_rows_for_existing_key()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        // Fill all 16 key slots with distinct keys.
        for (var i = 0; i < 16; i++)
            world.Create(new GridCell(i, 0));

        var lookup = new FrameLookup<(int X, int Y)>();
        Assert.True(lookup.TryBuild(world, query, new CellKeySelector()));
        Assert.Equal(16, lookup.KeyCount);
        Assert.Equal(16, lookup.RowCount);

        // Add 5 more entities with EXISTING keys 0-4.
        for (var i = 0; i < 5; i++)
            world.Create(new GridCell(i, 0));

        // Old code rejected at distinctKeys >= _capacity BEFORE probing the
        // existing key. Fixed code probes first and returns the existing slot.
        Assert.True(lookup.TryBuild(world, query, new CellKeySelector()));
        Assert.Equal(16, lookup.KeyCount);       // same 16 distinct keys
        Assert.Equal(21, lookup.RowCount);        // 16 + 5 = 21 rows

        for (var i = 0; i < 5; i++)
            Assert.Equal(2, lookup[(i, 0)].Length);

        for (var i = 5; i < 16; i++)
            Assert.Equal(1, lookup[(i, 0)].Length);
    }

    [Fact]
    public void BUG_ensure_capacity_preserves_built_lookup()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();
        world.Create(new GridCell(16, 0));

        var lookup = new FrameLookup<int>();
        lookup.Build(world, query, new XKeySelector());
        Assert.Equal(1, lookup[16].Length);

        // Hash 16 moves from slot 0 (mask 15) to slot 16 (mask 31).
        lookup.EnsureCapacity(32, 0);

        Assert.Equal(1, lookup.KeyCount);
        Assert.Equal(1, lookup.RowCount);
        Assert.Equal(1, lookup[16].Length);
    }

    private struct CountingSelector : IFrameKeySelector<int>
    {
        public int Counter;

        public int Select(Entity entity, ReadOnlySpan<ChunkView> chunks, int ci, int ri)
        {
            return Counter++;
        }
    }

    [Fact]
    public void BUG_stateful_struct_selector_uses_same_initial_state_for_both_passes()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        world.Create(new GridCell(10, 0));
        world.Create(new GridCell(20, 0));
        world.Create(new GridCell(30, 0));

        var lookup = new FrameLookup<int>();
        Assert.True(lookup.TryBuild(world, query, new CountingSelector()));

        Assert.Equal(3, lookup.KeyCount);
        Assert.Equal(3, lookup.RowCount);
        Assert.Equal(1, lookup[0].Length);  // first entity → key 0
        Assert.Equal(1, lookup[1].Length);  // second     → key 1
        Assert.Equal(1, lookup[2].Length);  // third      → key 2
    }

    private struct ThrowingSelector : IFrameKeySelector<(int X, int Y)>
    {
        public int ThrowAfter;
        public int CallCount;

        public (int X, int Y) Select(Entity entity, ReadOnlySpan<ChunkView> chunks, int ci, int ri)
        {
            CallCount++;
            if (CallCount > ThrowAfter)
                throw new InvalidOperationException("Intentional failure from ThrowingSelector.");
            ref var cell = ref chunks[ci].GetSpan<GridCell>()[ri];
            return (cell.X, cell.Y);
        }
    }

    [Fact]
    public void BUG_failed_build_does_not_expose_partial_lookup()
    {
        using var world = new World();
        var query = new QueryDescription().With<GridCell>();

        // Build a valid lookup first.
        for (var i = 0; i < 5; i++)
            world.Create(new GridCell(i, 0));

        var lookup = new FrameLookup<(int X, int Y)>();
        lookup.Build(world, query, new CellKeySelector());
        Assert.Equal(5, lookup.KeyCount);
        Assert.Equal(5, lookup.RowCount);
        Assert.Equal(1, lookup[(0, 0)].Length);

        // Trigger a build that throws during pass 1 (after 2 calls).
        var throwing = new ThrowingSelector { ThrowAfter = 2 };
        Assert.Throws<InvalidOperationException>(() => lookup.TryBuild(world, query, throwing));

        // After exception, lookup must be clean — no leftover or stale state.
        Assert.Equal(0, lookup.KeyCount);
        Assert.Equal(0, lookup.RowCount);
        Assert.Equal(0, lookup[(0, 0)].Length);  // previous key must be gone
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData((1 << 30) + 1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, (1 << 30) + 1)]
    public void BUG_constructor_rejects_invalid_capacity(int keyCapacity, int rowCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FrameLookup<(int, int)>(keyCapacity, rowCapacity));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData((1 << 30) + 1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, (1 << 30) + 1)]
    public void BUG_EnsureCapacity_rejects_invalid_capacity(int keyCapacity, int rowCapacity)
    {
        var lookup = new FrameLookup<(int, int)>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => lookup.EnsureCapacity(keyCapacity, rowCapacity));
    }
}
