// Correctness matrix for FrameReadModel ValueLab layouts.
//
// Runs all cases from the plan (Task 6) against A/B/C/DenseInt layouts.
// Each test method receives the layout name and creates the correct concrete
// lookup type for that layout — no fake "Testing layout X" with mismatched
// internal lookup.
//
// Layout mappings:
//   EntityArray → EntityArrayLookup<TKey>   (entity path only for consistency)
//   LinkedRow   → LinkedRowLookup<TKey>     (row path)
//   CompactRow  → CompactRowLookup<TKey>    (row path)
//   DenseInt    → DenseIntCompactLookup     (row path, int keys only)
//
// Run with: dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
// Optionally: --n 1000000 for 1M smoke (default quick: smaller size).

using MiniArch;

namespace FrameReadModels.ValueLab;

internal static class FrameReadModelCorrectness
{
    private static int _n = 1000; // default test size (overridden via --n)

    /// <summary>
    /// Capped size for bulk test cases (multi-key, hot bucket, etc.).
    /// When _n is large (1M), use this cap to avoid excessive entity creation.
    /// The OneMillionSmoke test always uses full _n.
    /// </summary>
    private static int BulkN => Math.Min(_n, 10000);

    public static void SetSize(int n)
    {
        _n = n;
    }

    /// <summary>
    /// Runs all correctness cases against all layouts.
    /// Returns true if all pass, false otherwise.
    /// </summary>
    public static bool RunAll()
    {
        var pass = true;

        // Layout names — each test method dispatches on the name.
        var layouts = new[] { "EntityArray", "LinkedRow", "CompactRow", "DenseInt" };

        // Cases to run (excluding OneMillionSmoke — run separately).
        var cases = new (string Name, Func<string, bool> Run)[]
        {
            ("EmptyWorld",               TestEmptyWorld),
            ("SingleKey",                TestSingleKey),
            ("MultiKey",                 TestMultiKey),
            ("MissingKey",               TestMissingKey),
            ("DefaultKey",               TestDefaultKey),
            ("HotBucket",                TestHotBucket),
            ("HashCollision",            TestHashCollision),
            ("MultiArchetype",           TestMultiArchetype),
            ("ChunkedStorage",           TestChunkedStorage),
            ("Where0Percent",            TestWhere0Percent),
            ("WherePartial",             TestWherePartial),
            ("Where100Percent",          TestWhere100Percent),
            ("ScanOrder",                TestScanOrder),
            ("EntityVsRowConsistency",   TestEntityVsRowConsistency),
            ("DirectForEachConsistency", TestDirectForEachConsistency),
            ("RunForEachConsistency",    TestRunForEachConsistency),
            ("IndexerConsistency",       TestIndexerConsistency),
            ("AutoGrowMultiple",         TestAutoGrowMultiple),
            ("ClearRebuild",             TestClearRebuild),
            ("RebuildPublishesNewResult", TestRebuildPublishesNewResult),
            ("NoGrowEarlyFail",          TestNoGrowEarlyFail),
            ("NoGrowLateFail",           TestNoGrowLateFail),
        };

        foreach (var layout in layouts)
        {
            // Only print the layout line once, truthfully.
            Console.WriteLine($"  Testing layout: {layout}");
            foreach (var testCase in cases)
            {
                var ok = Run(testCase.Name, layout, testCase.Run);
                if (!ok) pass = false;
            }
        }

        // Run 1M smoke once (outside layout loop — creates 1M entities once).
        if (_n >= 1000000)
        {
            Console.WriteLine("  Testing 1M smoke...");
            var ok = Run("OneMillionSmoke", "AllLayouts", TestOneMillionSmoke);
            if (!ok) pass = false;
        }

        return pass;
    }

    private static bool Run(string caseName, string layoutName, Func<string, bool> test)
    {
        try
        {
            var result = test(layoutName);
            if (!result)
                Console.Error.WriteLine($"  FAIL: [{layoutName}] {caseName}");
            else
                Console.WriteLine($"  PASS: [{layoutName}] {caseName}");
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAIL: [{layoutName}] {caseName} — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ================================================================
    //  Test case implementations
    //  Each creates its own World, builds a lookup, and asserts.
    // ================================================================

    /// <summary>1. Empty world: query with no matching entities.</summary>
    private static bool TestEmptyWorld(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            default:
                return false;
        }
    }

    /// <summary>2. Single key, multi key, missing key, default key.</summary>
    private static bool TestSingleKey(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(5));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(5) == BulkN && lookup.GetRowCount(99) == 0;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(5) == BulkN && lookup.GetRowCount(99) == 0;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(5) == BulkN && lookup.GetRowCount(99) == 0;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(5) == BulkN && lookup.GetRowCount(99) == 0;
            }
            default:
                return false;
        }
    }

    private static bool TestMultiKey(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(i % 2));
        }

        var expected0 = BulkN / 2;
        var expected1 = BulkN - expected0;

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2
                    && lookup.GetRowCount(0) == expected0
                    && lookup.GetRowCount(1) == expected1;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2
                    && lookup.GetRowCount(0) == expected0
                    && lookup.GetRowCount(1) == expected1;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2
                    && lookup.GetRowCount(0) == expected0
                    && lookup.GetRowCount(1) == expected1;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2
                    && lookup.GetRowCount(0) == expected0
                    && lookup.GetRowCount(1) == expected1;
            }
            default:
                return false;
        }
    }

    private static bool TestMissingKey(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        var e = world.Create();
        world.Add(e, new Team(42));

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(0) == 0
                    && lookup.GetRowCount(42) == 1 && lookup.GetRowCount(9999) == 0;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(0) == 0
                    && lookup.GetRowCount(42) == 1 && lookup.GetRowCount(9999) == 0;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(0) == 0
                    && lookup.GetRowCount(42) == 1 && lookup.GetRowCount(9999) == 0;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(128, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(0) == 0
                    && lookup.GetRowCount(42) == 1 && lookup.GetRowCount(9999) == 0;
            }
            default:
                return false;
        }
    }

    private static bool TestDefaultKey(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        var e = world.Create();
        world.Add(e, new Team(0));

        switch (layout)
        {
            case "EntityArray":
            case "LinkedRow":
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(0) == 1;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, 8);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(0) == 1;
            }
            default:
                return false;
        }
    }

    /// <summary>3. All entities land in one hot bucket (same key).</summary>
    private static bool TestHotBucket(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(1));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(1) == BulkN;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(1) == BulkN;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(1) == BulkN;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 1 && lookup.GetRowCount(1) == BulkN;
            }
            default:
                return false;
        }
    }

    /// <summary>4. Hash collision — all keys have same hash code.</summary>
    private static bool TestHashCollision(string layout)
    {
        if (layout == "DenseInt")
        {
            // DenseIntCompactLookup only supports int keys; CollisionKey requires
            // custom hashing (all hash to 42). Hash collision testing via CollisionKey
            // is not applicable to DenseInt — skip.
            return true;
        }

        using var world = new World();
        var query = new QueryDescription().With<Cell>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Cell(i % 4));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<CollisionKey>.Create(8, BulkN + 1);
                Rows<Cell>.From(world, query)
                    .KeyBy<CollisionKey, CollisionCellKeySelector>()
                    .Into(ref lookup);
                var expectedPerKey = BulkN / 4;
                if (lookup.KeyCount != 4) return false;
                for (var k = 0; k < 4; k++)
                {
                    if (lookup.GetRowCount(new CollisionKey(k)) != expectedPerKey)
                        return false;
                }
                return true;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<CollisionKey>.Create(8, BulkN + 1);
                Rows<Cell>.From(world, query)
                    .KeyBy<CollisionKey, CollisionCellKeySelector>()
                    .Into(ref lookup);
                var expectedPerKey = BulkN / 4;
                if (lookup.KeyCount != 4) return false;
                for (var k = 0; k < 4; k++)
                {
                    if (lookup.GetRowCount(new CollisionKey(k)) != expectedPerKey)
                        return false;
                }
                return true;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<CollisionKey>.Create(8, BulkN + 1);
                Rows<Cell>.From(world, query)
                    .KeyBy<CollisionKey, CollisionCellKeySelector>()
                    .Into(ref lookup);
                var expectedPerKey = BulkN / 4;
                if (lookup.KeyCount != 4) return false;
                for (var k = 0; k < 4; k++)
                {
                    if (lookup.GetRowCount(new CollisionKey(k)) != expectedPerKey)
                        return false;
                }
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>5. Multi archetype: entities in different archetypes.</summary>
    private static bool TestMultiArchetype(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        var half = BulkN / 2;
        for (var i = 0; i < half; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(1));
        }
        for (var i = 0; i < half; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(2));
            world.Add(e, new Health(i));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2 && lookup.GetRowCount(1) == half && lookup.GetRowCount(2) == half;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2 && lookup.GetRowCount(1) == half && lookup.GetRowCount(2) == half;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2 && lookup.GetRowCount(1) == half && lookup.GetRowCount(2) == half;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == 2 && lookup.GetRowCount(1) == half && lookup.GetRowCount(2) == half;
            }
            default:
                return false;
        }
    }

    /// <summary>6. Chunked storage: enough entities to trigger chunking.</summary>
    private static bool TestChunkedStorage(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Position>();
        _ = world.Query(query).GetChunks();

        var count = Math.Max(_n, 1000);
        for (var i = 0; i < count; i++)
        {
            var e = world.Create();
            world.Add(e, new Position(i, i * 2));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(count + 1, count + 1);
                Rows<Position>.From(world, query)
                    .KeyBy<int, EntityIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == count;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(count + 1, count + 1);
                Rows<Position>.From(world, query)
                    .KeyBy<int, EntityIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == count;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(count + 1, count + 1);
                Rows<Position>.From(world, query)
                    .KeyBy<int, EntityIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == count;
            }
            case "DenseInt":
            {
                // Entity.Id may go up to ~count+1; ensure DenseInt can cover the range.
                var maxKey = count + count / 2 + 1; // generous upper bound
                var lookup = DenseIntCompactLookup.Create(maxKey, count + 1);
                Rows<Position>.From(world, query)
                    .KeyBy<int, EntityIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == count;
            }
            default:
                return false;
        }
    }

    /// <summary>7a. Where 0% — predicate matches nothing.</summary>
    private static bool TestWhere0Percent(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Health>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Health(i));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, 8);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(int.MaxValue))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, 8);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(int.MaxValue))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, 8);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(int.MaxValue))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, 8);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(int.MaxValue))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == 0 && lookup.TotalRows == 0;
            }
            default:
                return false;
        }
    }

    /// <summary>7b. Where partial — some match, some don't.</summary>
    private static bool TestWherePartial(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Health>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Health(i));
        }

        var threshold = BulkN / 2;
        var expected = BulkN - threshold;

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(threshold))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == expected;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(threshold))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == expected;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(threshold))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == expected;
            }
            case "DenseInt":
            {
                var maxKey = _n + _n / 2 + 1;
                var lookup = DenseIntCompactLookup.Create(maxKey, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(threshold))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == expected;
            }
            default:
                return false;
        }
    }

    /// <summary>7c. Where 100% — all match.</summary>
    private static bool TestWhere100Percent(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Health>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Health(i));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(0))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == BulkN;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(0))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == BulkN;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(0))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == BulkN;
            }
            case "DenseInt":
            {
                var maxKey = _n + _n / 2 + 1;
                var lookup = DenseIntCompactLookup.Create(maxKey, BulkN + 1);
                Rows<Health>.From(world, query)
                    .Where(new HealthAtLeast(0))
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                return lookup.KeyCount == BulkN;
            }
            default:
                return false;
        }
    }

    /// <summary>8. Same-key scan order: entities must appear in ECS scan order.</summary>
    private static bool TestScanOrder(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        var half = BulkN / 2;
        for (var i = 0; i < half; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(1));
        }
        for (var i = 0; i < half; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(1));
            world.Add(e, new Health(i));
        }

        // Build lookup and collect entities for key=1.
        var entities = new Entity[BulkN];
        var written = 0;

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                written = lookup.CopyEntities(1, entities, world.Query(query).GetChunks());
                break;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                written = lookup.CopyEntities(1, entities, world.Query(query).GetChunks());
                break;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                written = lookup.CopyEntities(1, entities, world.Query(query).GetChunks());
                break;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                written = lookup.CopyEntities(1, entities, world.Query(query).GetChunks());
                break;
            }
            default:
                return false;
        }

        if (written != BulkN) return false;

        // Verify order matches chunk scan order.
        var directChunks = world.Query(query).GetChunks();
        var directEntities = new Entity[BulkN];
        var di = 0;
        for (var ci = 0; ci < directChunks.Length; ci++)
        {
            var ents = directChunks[ci].GetEntities();
            for (var ri = 0; ri < directChunks[ci].Count; ri++)
                directEntities[di++] = ents[ri];
        }

        for (var i = 0; i < written; i++)
        {
            if (!entities[i].Equals(directEntities[i]))
                return false;
        }
        return true;
    }

    /// <summary>9. Entity-based vs chunk-row component reading consistency.
    /// EntityArray uses entity path only (CopyRowRefs returns 0);
    /// LinkedRow/CompactRow/DenseInt use row path.</summary>
    private static bool TestEntityVsRowConsistency(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Health>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Health(i * 10));
        }

        var chunks = world.Query(query).GetChunks();

        // Compute expected sum via direct entity scan.
        long expectedSum = 0;
        foreach (var chunk in chunks)
        {
            var ids = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
                expectedSum += world.Get<Health>(ids[i]).Value;
        }

        switch (layout)
        {
            case "EntityArray":
            {
                // EntityArray: entity path only (CopyEntities).
                var lookup = EntityArrayLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                long sum = 0;
                var buf = new Entity[16];
                for (var k = 0; k < lookup.KeyCount; k++)
                {
                    var cnt = lookup.CopyEntities(k, buf, chunks);
                    if (cnt == 0) continue;
                    if (cnt > buf.Length) buf = new Entity[cnt];
                    lookup.CopyEntities(k, buf, chunks);
                    for (var i = 0; i < cnt; i++)
                        sum += world.Get<Health>(buf[i]).Value;
                }
                return sum == expectedSum;
            }
            case "LinkedRow":
            {
                // LinkedRow: row path via CopyRowRefs.
                var lookup = LinkedRowLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                long sum = 0;
                var buf = new RowRef[16];
                for (var k = 0; k < lookup.KeyCount; k++)
                {
                    var cnt = lookup.CopyRowRefs(k, buf);
                    if (cnt == 0) continue;
                    if (cnt > buf.Length) buf = new RowRef[cnt];
                    lookup.CopyRowRefs(k, buf);
                    for (var i = 0; i < cnt; i++)
                    {
                        ref readonly var rr = ref buf[i];
                        sum += chunks[rr.ChunkIndex].GetSpan<Health>()[rr.RowIndex].Value;
                    }
                }
                return sum == expectedSum;
            }
            case "CompactRow":
            {
                // CompactRow: row path via CopyRowRefs.
                var lookup = CompactRowLookup<int>.Create(_n + 1, BulkN + 1);
                Rows<Health>.From(world, query)
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                long sum = 0;
                var buf = new RowRef[16];
                for (var k = 0; k < lookup.KeyCount; k++)
                {
                    var cnt = lookup.CopyRowRefs(k, buf);
                    if (cnt == 0) continue;
                    if (cnt > buf.Length) buf = new RowRef[cnt];
                    lookup.CopyRowRefs(k, buf);
                    for (var i = 0; i < cnt; i++)
                    {
                        ref readonly var rr = ref buf[i];
                        sum += chunks[rr.ChunkIndex].GetSpan<Health>()[rr.RowIndex].Value;
                    }
                }
                return sum == expectedSum;
            }
            case "DenseInt":
            {
                // DenseInt: row path via CopyRowRefs.
                var maxKey = _n + _n / 2 + 1;
                var lookup = DenseIntCompactLookup.Create(maxKey, BulkN + 1);
                Rows<Health>.From(world, query)
                    .KeyBy<int, HealthIdKeySelector>()
                    .Into(ref lookup);
                long sum = 0;
                var buf = new RowRef[16];
                for (var k = 0; k < lookup.KeyCount; k++)
                {
                    var cnt = lookup.CopyRowRefs(k, buf);
                    if (cnt == 0) continue;
                    if (cnt > buf.Length) buf = new RowRef[cnt];
                    lookup.CopyRowRefs(k, buf);
                    for (var i = 0; i < cnt; i++)
                    {
                        ref readonly var rr = ref buf[i];
                        sum += chunks[rr.ChunkIndex].GetSpan<Health>()[rr.RowIndex].Value;
                    }
                }
                return sum == expectedSum;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Public-shape probe: CompactRow direct ForEach must consume the same
    /// component values as CopyRowRefs + manual span read.
    /// Other layouts do not implement direct ForEach in this ValueLab.
    /// </summary>
    private static bool TestDirectForEachConsistency(string layout)
    {
        if (layout != "CompactRow")
            return true;

        using var world = new World();
        var query = new QueryDescription().With<Cell>().With<Position>().With<Health>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            world.Create(
                new Cell(i % 17),
                new Position(i, i * 2),
                new Health(i * 3));
        }

        var chunks = world.Query(query).GetChunks();
        var lookup = CompactRowLookup<int>.Create(32, BulkN + 1);
        Rows<Cell, Position, Health>.From(world, query)
            .KeyBy<int, CellKeySelector3>()
            .Into(ref lookup);

        var refs = new RowRef[BulkN];
        long copySum = 0;
        long directSum;

        var consumer = new HealthSumConsumer1();
        for (var key = 0; key < 17; key++)
        {
            var count = lookup.CopyRowRefs(key, refs);
            for (var i = 0; i < count; i++)
            {
                ref readonly var rr = ref refs[i];
                copySum += chunks[rr.ChunkIndex].GetSpan<Health>()[rr.RowIndex].Value;
            }

            lookup.ForEach<Health, HealthSumConsumer1>(key, chunks, ref consumer);
        }

        directSum = consumer.Sum;
        return directSum == copySum;
    }

    /// <summary>
    /// Public-shape probe: CompactRow chunk-run ForEach must consume the same
    /// component values as CopyRowRefs + manual span read, but with batched spans.
    /// Other layouts do not implement chunk-run in this ValueLab.
    /// </summary>
    private static bool TestRunForEachConsistency(string layout)
    {
        if (layout != "CompactRow")
            return true;

        using var world = new World();
        var query = new QueryDescription().With<Cell>().With<Position>().With<Health>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            world.Create(
                new Cell(i % 17),
                new Position(i, i * 2),
                new Health(i * 3));
        }

        var chunks = world.Query(query).GetChunks();
        var lookup = CompactRowLookup<int>.Create(32, BulkN + 1);
        Rows<Cell, Position, Health>.From(world, query)
            .KeyBy<int, CellKeySelector3>()
            .Into(ref lookup);

        var refs = new RowRef[BulkN];
        long copySum = 0;
        var consumer = new HealthRunSumConsumer();

        for (var key = 0; key < 17; key++)
        {
            var count = lookup.CopyRowRefs(key, refs);
            for (var i = 0; i < count; i++)
            {
                ref readonly var rr = ref refs[i];
                copySum += chunks[rr.ChunkIndex].GetSpan<Health>()[rr.RowIndex].Value;
            }

            lookup.ForEachRun<Health, HealthRunSumConsumer>(key, chunks, ref consumer);
        }

        return consumer.Sum == copySum && consumer.RunCount > 0;
    }

    private struct HealthRunSumConsumer : IFrameRunConsumer<Health>
    {
        public long Sum;
        public int RunCount;

        public void Accept(ReadOnlySpan<Entity> entities, ReadOnlySpan<Health> healths)
        {
            RunCount++;
            for (var i = 0; i < healths.Length; i++)
                Sum += healths[i].Value;
        }
    }

    /// <summary>
    /// Public-shape probe: CompactRow/DenseInt indexer must return the same
    /// RowRef values as CopyRowRefs.
    /// </summary>
    private static bool TestIndexerConsistency(string layout)
    {
        if (layout != "CompactRow" && layout != "DenseInt")
            return true;

        using var world = new World();
        var query = new QueryDescription().With<Cell>().With<Position>().With<Health>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            world.Create(
                new Cell(i % 17),
                new Position(i, i * 2),
                new Health(i * 3));
        }

        var chunks = world.Query(query).GetChunks();

        if (layout == "CompactRow")
        {
            var lookup = CompactRowLookup<int>.Create(32, BulkN + 1);
            Rows<Cell, Position, Health>.From(world, query)
                .KeyBy<int, CellKeySelector3>()
                .Into(ref lookup);

            var refs = new RowRef[BulkN];
            for (var key = 0; key < 17; key++)
            {
                var copyCount = lookup.CopyRowRefs(key, refs);
                var span = lookup[key];

                if (span.Length != copyCount) return false;
                for (var i = 0; i < span.Length; i++)
                {
                    if (span[i].ChunkIndex != refs[i].ChunkIndex ||
                        span[i].RowIndex != refs[i].RowIndex)
                        return false;
                }
            }
        }
        else // DenseInt
        {
            var lookup = DenseIntCompactLookup.Create(64, BulkN + 1);
            Rows<Cell, Position, Health>.From(world, query)
                .KeyBy<int, CellKeySelector3>()
                .Into(ref lookup);

            var refs = new RowRef[BulkN];
            for (var key = 0; key < 17; key++)
            {
                var copyCount = lookup.CopyRowRefs(key, refs);
                var span = lookup[key];

                if (span.Length != copyCount) return false;
                for (var i = 0; i < span.Length; i++)
                {
                    if (span[i].ChunkIndex != refs[i].ChunkIndex ||
                        span[i].RowIndex != refs[i].RowIndex)
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>10. AutoGrow: start small, let it resize multiple times.
    /// Verifies LastResult.Resized=true after a multi-grow build.</summary>
    private static bool TestAutoGrowMultiple(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        var count = Math.Max(BulkN, 32);
        for (var i = 0; i < count; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(i));
        }

        var initialCap = LayoutHelpers.CeilPow2(count / 2 + 1);

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(initialCap, count + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == count && lookup.LastResult.Resized;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(initialCap, count + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == count && lookup.LastResult.Resized;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(initialCap, count + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == count && lookup.LastResult.Resized;
            }
            case "DenseInt":
            {
                // DenseInt doesn't have key capacity separate from maxKeyValue.
                // We start with maxKeyValue=initialCap and rowCapacity=count/2+1 so
                // the row capacity is too small, forcing resize.
                var lookup = DenseIntCompactLookup.Create(initialCap + 10, count / 2 + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.KeyCount == count && lookup.LastResult.Resized;
            }
            default:
                return false;
        }
    }

    /// <summary>11. Clear + rebuild produces identical result.</summary>
    private static bool TestClearRebuild(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < BulkN; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(i % 3));
        }

        // Build once, capture state, rebuild, compare.
        int firstCount;
        int firstTotal;

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                firstCount = lookup.GetRowCount(0);
                firstTotal = lookup.TotalRows;

                // Clear and rebuild
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(0) == firstCount && lookup.TotalRows == firstTotal;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                firstCount = lookup.GetRowCount(0);
                firstTotal = lookup.TotalRows;

                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(0) == firstCount && lookup.TotalRows == firstTotal;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                firstCount = lookup.GetRowCount(0);
                firstTotal = lookup.TotalRows;

                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(0) == firstCount && lookup.TotalRows == firstTotal;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, BulkN + 1);
                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                firstCount = lookup.GetRowCount(0);
                firstTotal = lookup.TotalRows;

                Rows<Team>.From(world, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(0) == firstCount && lookup.TotalRows == firstTotal;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Publishing lifecycle probe: rebuilding the same lookup with a different
    /// snapshot must replace the old published result. v1 exposes no row view,
    /// span, or enumerator, so there is no user-holdable old view to invalidate.
    /// </summary>
    private static bool TestRebuildPublishesNewResult(string layout)
    {
        using var world1 = new World();
        using var world2 = new World();
        var query = new QueryDescription().With<Team>();
        _ = world1.Query(query).GetChunks();
        _ = world2.Query(query).GetChunks();

        for (var i = 0; i < 32; i++)
        {
            var e = world1.Create();
            world1.Add(e, new Team(1));
        }

        for (var i = 0; i < 48; i++)
        {
            var e = world2.Create();
            world2.Add(e, new Team(2));
        }

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, 64);
                Rows<Team>.From(world1, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                if (lookup.GetRowCount(1) != 32 || lookup.GetRowCount(2) != 0) return false;
                Rows<Team>.From(world2, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(1) == 0 && lookup.GetRowCount(2) == 48 && lookup.TotalRows == 48;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, 64);
                Rows<Team>.From(world1, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                if (lookup.GetRowCount(1) != 32 || lookup.GetRowCount(2) != 0) return false;
                Rows<Team>.From(world2, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(1) == 0 && lookup.GetRowCount(2) == 48 && lookup.TotalRows == 48;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, 64);
                Rows<Team>.From(world1, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                if (lookup.GetRowCount(1) != 32 || lookup.GetRowCount(2) != 0) return false;
                Rows<Team>.From(world2, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(1) == 0 && lookup.GetRowCount(2) == 48 && lookup.TotalRows == 48;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(64, 64);
                Rows<Team>.From(world1, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                if (lookup.GetRowCount(1) != 32 || lookup.GetRowCount(2) != 0) return false;
                Rows<Team>.From(world2, query).KeyBy<int, TeamKeySelector>().Into(ref lookup);
                return lookup.GetRowCount(1) == 0 && lookup.GetRowCount(2) == 48 && lookup.TotalRows == 48;
            }
            default:
                return false;
        }
    }

    /// <summary>12a. NoGrow early fail: capacity too small for keys.
    /// Uses TryBuild directly (not Rows DSL).</summary>
    private static bool TestNoGrowEarlyFail(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < 30; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(i));
        }

        var chunks = world.Query(query).GetChunks();
        var pred = new PassAll<Team>();
        var sel = new TeamKeySelector();

        bool ok;
        int totalRows;

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(4, 64);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(4, 64);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(4, 64);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            case "DenseInt":
            {
                // maxKeyValue=4 → only keys 0..4 fit; key 5+ causes out-of-range → fail.
                var lookup = DenseIntCompactLookup.Create(4, 64);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            default:
                return false;
        }

        return !ok && totalRows == 0;
    }

    /// <summary>12b. NoGrow late fail: capacity ok for keys but not for rows.</summary>
    private static bool TestNoGrowLateFail(string layout)
    {
        using var world = new World();
        var query = new QueryDescription().With<Team>();
        _ = world.Query(query).GetChunks();

        for (var i = 0; i < 100; i++)
        {
            var e = world.Create();
            world.Add(e, new Team(1));
        }

        var chunks = world.Query(query).GetChunks();
        var pred = new PassAll<Team>();
        var sel = new TeamKeySelector();

        bool ok;
        int totalRows;

        switch (layout)
        {
            case "EntityArray":
            {
                var lookup = EntityArrayLookup<int>.Create(8, 10);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            case "LinkedRow":
            {
                var lookup = LinkedRowLookup<int>.Create(8, 10);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            case "CompactRow":
            {
                var lookup = CompactRowLookup<int>.Create(8, 10);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            case "DenseInt":
            {
                var lookup = DenseIntCompactLookup.Create(8, 10);
                ok = lookup.TryBuild<Team, PassAll<Team>, TeamKeySelector>(chunks, ref pred, ref sel);
                totalRows = lookup.TotalRows;
                break;
            }
            default:
                return false;
        }

        return !ok && totalRows == 0;
    }

    /// <summary>13. Smoke with 1M entities — tests all layouts (single world).</summary>
    private static bool TestOneMillionSmoke(string layout)
    {
        if (_n < 1000000) return true; // skip when running quick tests

        using var world = new World();
        var query = new QueryDescription().With<Position>();
        _ = world.Query(query).GetChunks();

        const int count = 1000000;
        // Pre-compute max entity Id estimate for DenseInt.
        var maxIdEstimate = count + count / 10 + 1; // generous bound

        // Create all entities first (single world, reused across layouts).
        for (var i = 0; i < count; i++)
        {
            var e = world.Create();
            world.Add(e, new Position(i, i * 2));
        }

        // Test EntityArray
        {
            var lookup = EntityArrayLookup<int>.Create(count + 1, count + 1);
            Rows<Position>.From(world, query)
                .KeyBy<int, EntityIdKeySelector>()
                .Into(ref lookup);
            if (lookup.KeyCount != count)
            {
                Console.Error.WriteLine($"  1M smoke [EntityArray]: expected {count} keys, got {lookup.KeyCount}");
                return false;
            }
        }

        // Test LinkedRow
        {
            var lookup = LinkedRowLookup<int>.Create(count + 1, count + 1);
            Rows<Position>.From(world, query)
                .KeyBy<int, EntityIdKeySelector>()
                .Into(ref lookup);
            if (lookup.KeyCount != count)
            {
                Console.Error.WriteLine($"  1M smoke [LinkedRow]: expected {count} keys, got {lookup.KeyCount}");
                return false;
            }
        }

        // Test CompactRow
        {
            var lookup = CompactRowLookup<int>.Create(count + 1, count + 1);
            Rows<Position>.From(world, query)
                .KeyBy<int, EntityIdKeySelector>()
                .Into(ref lookup);
            if (lookup.KeyCount != count)
            {
                Console.Error.WriteLine($"  1M smoke [CompactRow]: expected {count} keys, got {lookup.KeyCount}");
                return false;
            }
        }

        // Test DenseIntCompactLookup
        {
            var lookup = DenseIntCompactLookup.Create(maxIdEstimate, count + 1);
            Rows<Position>.From(world, query)
                .KeyBy<int, EntityIdKeySelector>()
                .Into(ref lookup);
            if (lookup.KeyCount != count)
            {
                Console.Error.WriteLine($"  1M smoke [DenseInt]: expected {count} keys, got {lookup.KeyCount}");
                return false;
            }
        }

        return true;
    }
}
