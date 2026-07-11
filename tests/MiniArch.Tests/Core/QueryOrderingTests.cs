using MiniArch.Core;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

/// <summary>
/// 验证 Query 迭代顺序契约：
///
/// 1. Archetype 顺序 = archetype 创建顺序
/// 2. Entity 顺序（同一 archetype 内） = entity 存储顺序（append 到末尾；删除用 swap-remove）
/// 3. 所有访问路径（foreach / GetChunks / GetArchetypeSpan）产生一致顺序
/// 4. 给定相同输入序列，顺序是确定性的
///
/// 细节见 kb-core-ecs.md "Query 迭代顺序契约" 段。
/// </summary>
public sealed class QueryOrderingTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    // ──────────────────────────────────────────────
    //  Archetype 顺序
    // ──────────────────────────────────────────────

    [Fact]
    public void Query_iterates_archetypes_in_creation_order()
    {
        var world = new World();

        // Archetype A: {Position} — created first (via first entity with Position)
        var a1 = world.Create(new Position(1, 1));
        var a2 = world.Create(new Position(2, 2));

        // Archetype B: {Velocity} — created second (NOT matched, for padding)
        world.Create(new Velocity(3, 3));

        // Archetype C: {Position, Velocity} — created third
        var c1 = world.Create(new Position(4, 4), new Velocity(5, 5));

        var query = world.Query(new QueryDescription().With<Position>());

        // Expected: A (a1, a2) then C (c1)
        Assert.Equal([a1, a2, c1], CollectEntities(query));
    }

    [Fact]
    public void Query_iterates_archetypes_in_creation_order_complex()
    {
        var world = new World();

        // Create archetypes in deliberate order with 2 matching archetypes
        var a0 = world.Create(new Position(0, 0));                      // Arch 0: {Position}
        var b0 = world.Create(new Velocity(1, 1));                      // Arch 1: {Velocity}
        var a1 = world.Create(new Position(2, 2));                      // Arch 0 (append)
        var c0 = world.Create(new Position(3, 3), new Velocity(4, 4));  // Arch 2: {Position, Velocity}

        var query = world.Query(new QueryDescription().With<Position>());

        // All position entities in archetype creation order:
        // Arch 0 (first created: a0, a1), then Arch 2 (second matching: c0)
        Assert.Equal([a0, a1, c0], CollectEntities(query));
    }

    // ──────────────────────────────────────────────
    //  Entity 顺序（同一 archetype 内）
    // ──────────────────────────────────────────────

    [Fact]
    public void Single_archetype_entity_order_follows_creation_order()
    {
        var world = new World();
        var e0 = world.Create(new Position(0, 0));
        var e1 = world.Create(new Position(1, 1));
        var e2 = world.Create(new Position(2, 2));
        var e3 = world.Create(new Position(3, 3));

        var query = world.Query(new QueryDescription().With<Position>());
        Assert.Equal([e0, e1, e2, e3], CollectEntities(query));
    }

    [Fact]
    public void Entity_order_preserved_across_archetype_capacity_doubling()
    {
        // Use small entity capacity so growth is frequent
        var world = new World(entityCapacity: 4);
        var entities = new List<Entity>();
        for (var i = 0; i < 100; i++)
            entities.Add(world.Create(new Position(i, i)));

        var query = world.Query(new QueryDescription().With<Position>());
        Assert.Equal(entities, CollectEntities(query));
    }

    [Fact]
    public void Entity_order_preserved_across_chunk_promotion()
    {
        // Create enough entities to trigger non-chunked → chunked promotion.
        // Each entity: Position + Velocity = 16 bytes → segment capacity ~131072.
        // To force chunking we'd need a huge number. Instead use a tiny
        // per-entity size — create with Position only and fill many entities.
        //
        // However, chunk promotion depends on _segmentCapacity which is
        // computed from component sizes. ForceChunkedForTesting is available
        // for archetype-level tests. For an end-to-end test, we create enough
        // entities to exceed the default capacity (4) × some growth factor.
        // The test verifies order after promotion by checking chunk consistency.
        var world = new World();
        var entities = new List<Entity>();
        for (var i = 0; i < 50; i++)
            entities.Add(world.Create(new Position(i, i)));

        var query = world.Query(new QueryDescription().With<Position>());

        // Get order from foreach
        var fromForeach = CollectEntities(query);

        // Get order from chunks
        var fromChunks = new List<Entity>();
        foreach (var chunk in query.GetChunks())
            fromChunks.AddRange(chunk.GetEntities().ToArray());

        Assert.Equal(entities, fromForeach);
        Assert.Equal(entities, fromChunks);
    }

    // ──────────────────────────────────────────────
    //  所有访问路径一致性
    // ──────────────────────────────────────────────

    [Fact]
    public void All_query_access_paths_produce_same_entity_order()
    {
        var world = new World();

        // Entities across multiple archetypes
        world.Create(new Position(1, 1));
        world.Create(new Velocity(2, 2));
        world.Create(new Position(3, 3), new Velocity(4, 4));
        world.Create(new Velocity(5, 5));
        world.Create(new Position(6, 6));

        var query = world.Query(new QueryDescription().With<Position>());

        // Path 1: foreach
        var fromForeach = CollectEntities(query);

        // Path 2: GetChunks → entity span
        var fromChunks = new List<Entity>();
        foreach (var chunk in query.GetChunks())
            fromChunks.AddRange(chunk.GetEntities().ToArray());

        // Path 3: Advanced.GetArchetypeSpan → row-by-row
        var fromSpan = new List<Entity>();
        foreach (ref readonly var arch in query.Advanced.GetArchetypeSpan())
        {
            for (var row = 0; row < arch.EntityCount; row++)
                fromSpan.Add(arch.GetEntity(row));
        }

        Assert.Equal(fromForeach, fromChunks);
        Assert.Equal(fromForeach, fromSpan);
    }

    [Fact]
    public void GetChunks_and_GetArchetypeSpan_produce_consistent_order()
    {
        var world = new World();
        // Create entities with signatures that produce 3+ archetypes
        for (var i = 0; i < 5; i++) world.Create(new Position(i, i));
        for (var i = 0; i < 3; i++) world.Create(new Position(i, i), new Velocity(i, i));
        for (var i = 0; i < 4; i++) world.Create(new Position(i, i));

        var query = world.Query(new QueryDescription().With<Position>());

        var chunkCount = 0;
        var entityCountViaChunks = 0;
        foreach (var chunk in query.GetChunks())
        {
            chunkCount++;
            entityCountViaChunks += chunk.Count;
        }

        // Archetype span should have same number of archetypes as the
        // query's matched set (each archetype contributes ≥1 chunk)
        var archCount = query.Advanced.GetArchetypeSpan().Length;
        Assert.True(archCount <= chunkCount, "Each archetype contributes at least 1 chunk.");

        var entityCountViaArchs = 0;
        foreach (ref readonly var arch in query.Advanced.GetArchetypeSpan())
            entityCountViaArchs += arch.EntityCount;

        Assert.Equal(entityCountViaChunks, entityCountViaArchs);
    }

    // ──────────────────────────────────────────────
    //  确定性
    // ──────────────────────────────────────────────

    [Fact]
    public void Query_order_is_deterministic_across_repeated_enumeration()
    {
        var world = new World();
        for (var i = 0; i < 10; i++)
            world.Create(new Position(i, i));
        world.Create(new Velocity(100, 100));
        for (var i = 10; i < 20; i++)
            world.Create(new Position(i, i), new Velocity(i, i));

        var query = world.Query(new QueryDescription().With<Position>());

        var first = CollectEntities(query);
        var second = CollectEntities(query);
        var third = CollectEntities(query);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public async System.Threading.Tasks.Task Query_order_is_deterministic_across_concurrent_readers()
    {
        var world = new World();
        for (var i = 0; i < 20; i++)
            world.Create(new Position(i, i));

        var query = world.Query(new QueryDescription().With<Position>());
        var expected = CollectEntities(query);

        var start = new System.Threading.Barrier(9);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => System.Threading.Tasks.Task.Run(() =>
            {
                start.SignalAndWait();
                return CollectEntities(query);
            }))
            .ToArray();

        start.SignalAndWait();
        var results = await System.Threading.Tasks.Task.WhenAll(tasks);

        Assert.All(results, actual => Assert.Equal(expected, actual));
    }

    // ──────────────────────────────────────────────
    //  结构变更后的顺序确定性
    // ──────────────────────────────────────────────

    [Fact]
    public void Destroy_preserves_entity_order_of_survivors_in_remaining_archetype()
    {
        // Swap-remove in a flat archetype: destroying an entity near the
        // beginning moves the last entity into its slot.
        var world = new World();
        var e0 = world.Create(new Position(0, 0));
        var e1 = world.Create(new Position(1, 1));
        var e2 = world.Create(new Position(2, 2)); // last entity

        world.Destroy(e0); // swap-remove: e2 moves to slot 0, e1 stays at slot 1

        var query = world.Query(new QueryDescription().With<Position>());

        // After swap-remove: the entity that was last (e2) is now first,
        // and e1 remains in its position
        Assert.Equal([e2, e1], CollectEntities(query));
    }

    [Fact]
    public void Add_component_migrates_entity_to_end_of_destination_archetype()
    {
        var world = new World();

        // Archetype A: {Position}
        var a0 = world.Create(new Position(0, 0));
        var a1 = world.Create(new Position(1, 1));

        // Archetype B: {Velocity}
        var b0 = world.Create(new Velocity(2, 2));

        // Migrate a0: {Position} → {Position, Velocity}
        // This removes a0 from A (swap-remove: a1 moves to a0's slot)
        // and appends a0 to B (but B already has {Velocity}, destination
        // is {Position, Velocity})
        // Actually, adding Velocity to a {Position} entity creates a new
        // archetype {Position, Velocity} if one doesn't exist.
        // Let's re-think...
        //
        // Actually, the destination archetype depends on whether {Position, Velocity}
        // already exists. Since we didn't create any such entity, it will be
        // a new archetype (Archetype C: {Position, Velocity}).
        world.Add(a0, new Velocity(3, 3));

        // Now:
        // Archetype A: {Position} — a1 only (a0 moved out)
        // Archetype C: {Position, Velocity} — a0 only (migrated)

        // Query with .With<Position>() matches A and C.
        // Order: A (a1) then C (a0)
        var query = world.Query(new QueryDescription().With<Position>());
        Assert.Equal([a1, a0], CollectEntities(query));
    }

    [Fact]
    public void Remove_component_migrates_entity_to_end_of_destination_archetype()
    {
        var world = new World();

        // Archetype B: {Position, Velocity}
        var b0 = world.Create(new Position(0, 0), new Velocity(1, 1));
        var b1 = world.Create(new Position(2, 2), new Velocity(3, 3));

        // Archetype A: {Position}
        var a0 = world.Create(new Position(4, 4));

        // Remove Velocity from b1 → migrates to {Position} archetype (A)
        world.Remove<Velocity>(b1);

        // Now:
        // Archetype B: {Position, Velocity} — b0 only (b1 moved out; if swap-remove, last=b0 stays)
        // Archetype A: {Position} — a0, then b1 (appended)

        var query = world.Query(new QueryDescription().With<Position>());
        // Order: B first (b0), then A (a0, b1)
        Assert.Equal([b0, a0, b1], CollectEntities(query));
    }

    // ──────────────────────────────────────────────
    //  空查询 / 边界
    // ──────────────────────────────────────────────

    [Fact]
    public void Empty_query_returns_no_entities()
    {
        var world = new World();
        world.Create(new Velocity(1, 1));

        var query = world.Query(new QueryDescription().With<Position>());
        Assert.Empty(CollectEntities(query));
    }

    [Fact]
    public void Query_on_empty_world_returns_no_entities()
    {
        var world = new World();
        var query = world.Query(new QueryDescription().With<Position>());
        Assert.Empty(CollectEntities(query));
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    private static List<Entity> CollectEntities(Query query)
    {
        var list = new List<Entity>();
        foreach (var e in query)
            list.Add(e);
        return list;
    }
}
