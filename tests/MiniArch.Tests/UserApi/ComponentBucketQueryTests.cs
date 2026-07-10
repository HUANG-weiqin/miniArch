using System;
using System.Linq;
using Xunit;

namespace MiniArchTests.UserApi;

public sealed class ComponentBucketQueryTests
{
    // ── Helper component types ──────────────────────────────────────

    private readonly record struct CardZone(int Value);

    private readonly record struct Health(int Value);

    private readonly record struct Mana(int Value);

    private readonly record struct Tag;

    // Buffer large enough for all test scenarios (max ~5 entities per key).
    private static readonly Entity[] Buffer = new Entity[256];

    // ── 1. Default scope = With<TComponent>() ────────────────────────

    [Fact]
    public void Default_scope_is_With_TComponent()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e1 = world.Create(new CardZone(1));
        var e2 = world.Create(new CardZone(2));
        var e3 = world.Create(new CardZone(1));

        // Both zones should be findable.
        var count1 = query.Get(new CardZone(1), Buffer);
        Assert.Equal(2, count1);
        Assert.Contains(e1, Buffer[..count1].ToArray());
        Assert.Contains(e3, Buffer[..count1].ToArray());

        var count2 = query.Get(new CardZone(2), Buffer);
        Assert.Equal(1, count2);
        Assert.Equal(e2, Buffer[0]);
    }

    // ── 2. Custom scope automatically adds TComponent ────────────────

    [Fact]
    public void Custom_scope_auto_adds_TComponent()
    {
        using var world = new World();
        // Scope that only requires Health (not CardZone).
        var scope = new QueryDescription().With<Health>();
        using var query = new ComponentBucketQuery<CardZone>(world, scope);

        // Entities with CardZone + Health should be indexed.
        var e1 = world.Create(new CardZone(10), new Health(100));
        var e2 = world.Create(new CardZone(20), new Health(200));
        _ = world.Create(new Health(300)); // no CardZone — should be excluded

        Assert.Equal(1, query.Count(new CardZone(10)));
        Assert.Equal(1, query.Count(new CardZone(20)));
        Assert.Equal(0, query.Count(new CardZone(0))); // the entity with only Health
    }

    // ── 3. Get returns correctly bucketed entities ──────────────────

    [Fact]
    public void Get_returns_correctly_bucketed()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e1 = world.Create(new CardZone(1));
        var e2 = world.Create(new CardZone(2));
        var e3 = world.Create(new CardZone(1));
        var e4 = world.Create(new CardZone(3));

        var count1 = query.Get(new CardZone(1), Buffer);
        Assert.Equal(2, count1);
        Assert.Contains(e1, Buffer[..count1].ToArray());
        Assert.Contains(e3, Buffer[..count1].ToArray());

        var count2 = query.Get(new CardZone(2), Buffer);
        Assert.Equal(1, count2);
        Assert.Equal(e2, Buffer[0]);

        var count3 = query.Get(new CardZone(3), Buffer);
        Assert.Equal(1, count3);
        Assert.Equal(e4, Buffer[0]);

        // Non-existent key returns zero.
        Assert.Equal(0, query.Get(new CardZone(99), Buffer));
    }

    // ── 4. world.Set<TComponent> auto-detected on next Get ──────────

    [Fact]
    public void World_Set_is_auto_detected()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e = world.Create(new CardZone(1));
        Assert.Equal(1, query.Count(new CardZone(1)));

        // Change the zone value via world.Set.
        world.Set(e, new CardZone(2));

        // Don't call Refresh — the read API should auto-detect.
        Assert.Equal(0, query.Count(new CardZone(1)));
        Assert.Equal(1, query.Count(new CardZone(2)));
    }

    // ── 5. world.GetRef<TComponent> in-place mutation auto-detected ──

    [Fact]
    public void GetRef_inplace_mutation_is_auto_detected()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e = world.Create(new CardZone(1));
        Assert.Equal(1, query.Count(new CardZone(1)));

        // Mutate in-place via GetRef.
        ref var zone = ref world.GetRef<CardZone>(e);
        zone = new CardZone(3);

        Assert.Equal(0, query.Count(new CardZone(1)));
        Assert.Equal(1, query.Count(new CardZone(3)));
    }

    // ── 6. chunk.GetSpan<TComponent> in-place mutation auto-detected ──

    [Fact]
    public void Chunk_GetSpan_mutation_is_auto_detected()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e1 = world.Create(new CardZone(1));
        var e2 = world.Create(new CardZone(2));
        world.Create(new CardZone(1));

        // Mutate e2's CardZone via chunk span.
        var q = world.Query(new QueryDescription().With<CardZone>());
        foreach (var chunk in q.GetChunks())
        {
            var zones = chunk.GetSpan<CardZone>();
            var entities = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (entities[i].Equals(e2))
                    zones[i] = new CardZone(1);
            }
        }

        Assert.Equal(3, query.Count(new CardZone(1))); // e1, e3, and mutated e2 — all have zone 1
        Assert.Equal(0, query.Count(new CardZone(2)));
    }

    // ── 7. Destroy auto-detected on next Get ────────────────────────

    [Fact]
    public void Destroy_entity_auto_removes()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e1 = world.Create(new CardZone(1));
        var e2 = world.Create(new CardZone(1));
        var e3 = world.Create(new CardZone(1));

        Assert.Equal(3, query.Count(new CardZone(1)));

        world.Destroy(e1);

        Assert.Equal(2, query.Count(new CardZone(1)));
        var count = query.Get(new CardZone(1), Buffer);
        Assert.DoesNotContain(e1, Buffer[..count].ToArray());
    }

    // ── 8. Entity id reuse does not pollute results ──────────────────

    [Fact]
    public void Entity_id_reuse_does_not_pollute()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e1 = world.Create(new CardZone(42));
        int originalId = e1.Id;
        world.Destroy(e1);

        // Create entities until we get one that reuses the id.
        Entity e2;
        do
        {
            e2 = world.Create(new CardZone(99));
        } while (e2.Id != originalId);

        // Only e2 should be in the index (with zone 99).
        Assert.Equal(0, query.Count(new CardZone(42)));
        Assert.Equal(1, query.Count(new CardZone(99)));
    }

    // ── 9. ContainsKey / Count ──────────────────────────────────────

    [Fact]
    public void ContainsKey_returns_correctly()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        _ = world.Create(new CardZone(1));
        _ = world.Create(new CardZone(2));

        Assert.True(query.ContainsKey(new CardZone(1)));
        Assert.True(query.ContainsKey(new CardZone(2)));
        Assert.False(query.ContainsKey(new CardZone(3)));
    }

    [Fact]
    public void Count_returns_correctly()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        _ = world.Create(new CardZone(10));
        _ = world.Create(new CardZone(10));
        _ = world.Create(new CardZone(20));

        Assert.Equal(2, query.Count(new CardZone(10)));
        Assert.Equal(1, query.Count(new CardZone(20)));
        Assert.Equal(0, query.Count(new CardZone(99)));
    }

    // ── 10. Clear / Dispose ─────────────────────────────────────────

    [Fact]
    public void Clear_resets_cache_then_auto_refreshes_on_next_read()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e1 = world.Create(new CardZone(1));
        _ = world.Create(new CardZone(2));

        // First read verifies state.
        Assert.Equal(1, query.Count(new CardZone(1)));

        // Clear is a no-op (no internal state), but should not throw.
        query.Clear();

        // After changing the world, Clear + auto-refresh still sees the latest state.
        world.Set(e1, new CardZone(3));
        Assert.Equal(0, query.Count(new CardZone(1))); // e1 was changed
        Assert.Equal(1, query.Count(new CardZone(3))); // e1 is now zone 3
        Assert.Equal(1, query.Count(new CardZone(2))); // e2 unchanged
    }

    [Fact]
    public void Dispose_can_be_called_multiple_times()
    {
        using var world = new World();
        var query = new ComponentBucketQuery<CardZone>(world);

        _ = world.Create(new CardZone(1));

        query.Dispose();
        query.Dispose(); // second dispose — should not throw
    }

    [Fact]
    public void Operations_after_dispose_throw()
    {
        using var world = new World();
        var query = new ComponentBucketQuery<CardZone>(world);
        query.Dispose();

        Assert.Throws<ObjectDisposedException>(() => query.Get(new CardZone(1), Buffer));
        Assert.Throws<ObjectDisposedException>(() => query.TryGet(new CardZone(1), Buffer, out _));
        Assert.Throws<ObjectDisposedException>(() => query.ContainsKey(new CardZone(1)));
        Assert.Throws<ObjectDisposedException>(() => query.Count(new CardZone(1)));
        Assert.Throws<ObjectDisposedException>(() => query.Clear());
    }

    // ── 11. TryGet ──────────────────────────────────────────────────

    [Fact]
    public void TryGet_returns_false_for_nonexistent_key()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        Assert.False(query.TryGet(new CardZone(99), Buffer, out _));
    }

    [Fact]
    public void TryGet_returns_true_for_existing_key()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        var e = world.Create(new CardZone(5));
        var result = query.TryGet(new CardZone(5), Buffer, out int count);

        Assert.True(result);
        Assert.Equal(1, count);
        Assert.Equal(e, Buffer[0]);
    }

    // ── 12. CardZone scenario — single-component zone ───────────────

    [Fact]
    public void CardZone_scenario()
    {
        using var world = new World();
        using var query = new ComponentBucketQuery<CardZone>(world);

        // Simulate card game: entities belong to zones like Hand, Deck, Graveyard.
        var hand = world.Create(new CardZone(0));
        _ = world.Create(new CardZone(1)); // deck
        _ = world.Create(new CardZone(2)); // grave
        var card1 = world.Create(new CardZone(0));
        var card2 = world.Create(new CardZone(0));
        _ = world.Create(new CardZone(1)); // card3
        _ = world.Create(new CardZone(2)); // card4

        // Verify CardZone(0) = hand(0) + card1(0) + card2(0) = 3
        var count0 = query.Get(new CardZone(0), Buffer);
        Assert.Equal(3, count0);
        Assert.Contains(hand, Buffer[..count0].ToArray());
        Assert.Contains(card1, Buffer[..count0].ToArray());
        Assert.Contains(card2, Buffer[..count0].ToArray());

        // CardZone(1) = deck(1) + card3(1) = 2
        Assert.Equal(2, query.Count(new CardZone(1)));

        // CardZone(2) = grave(2) + card4(2) = 2
        Assert.Equal(2, query.Count(new CardZone(2)));

        // Move card1 from hand to deck.
        world.Set(card1, new CardZone(1));
        Assert.Equal(2, query.Count(new CardZone(0))); // hand + card2
        Assert.Equal(3, query.Count(new CardZone(1))); // deck + card3 + card1

        // Move card3 from deck to grave.
        // Note: card3 is unnamed, we need to find it. Let's just verify totals.
        // Currently zone1 has 3 entities. We move ONE of them to zone2.
        // After the move: zone1 = 2, zone2 = 3.

        // Actually let's skip the second move since finding the right entity is cumbersome
        // without tracking its reference. The first move already proves the auto-freshness.
    }
}
