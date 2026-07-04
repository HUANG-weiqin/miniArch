using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;

namespace MiniArchTests.Persistence;

public sealed class WorldSnapshotTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Unmanaged_world_can_round_trip_preserving_entity_metadata_values_and_archetype_membership()
    {
        var world = new World(chunkCapacity: 2);

        var first = world.Create();
        var second = world.Create();
        var third = world.Create();

        world.Add(first, new Position(1, 2));
        world.Add(second, new Position(3, 4));
        world.Add(second, new Velocity(5, 6));
        world.Add(third, new Position(7, 8));
        world.Add(third, new Velocity(9, 10));
        world.Set(third, new Position(11, 12));

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);

        Assert.True(loaded.TryGetLocation(first, out var firstLocation));
        Assert.Equal(first.Version, firstLocation.Version);
        Assert.Equal(1, firstLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(1, 2), GetComponent<Position>(loaded, first));

        Assert.True(loaded.TryGetLocation(second, out var secondLocation));
        Assert.Equal(second.Version, secondLocation.Version);
        Assert.Equal(2, secondLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(3, 4), GetComponent<Position>(loaded, second));
        Assert.Equal(new Velocity(5, 6), GetComponent<Velocity>(loaded, second));

        Assert.True(loaded.TryGetLocation(third, out var thirdLocation));
        Assert.Equal(third.Version, thirdLocation.Version);
        Assert.Equal(2, thirdLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(11, 12), GetComponent<Position>(loaded, third));
        Assert.Equal(new Velocity(9, 10), GetComponent<Velocity>(loaded, third));
    }

    [Fact]
    public void Snapshot_round_trip_preserves_multiple_archetypes_and_multiple_chunks()
    {
        var world = new World(chunkCapacity: 2);

        var positionOnly = new Entity[3];
        var moving = new Entity[3];
        var living = new Entity[3];

        for (var i = 0; i < positionOnly.Length; i++)
        {
            positionOnly[i] = world.Create();
            world.Add(positionOnly[i], new Position(i, i + 10));
        }

        for (var i = 0; i < moving.Length; i++)
        {
            moving[i] = world.Create();
            world.Add(moving[i], new Position(i + 100, i + 110));
            world.Add(moving[i], new Velocity(i + 120, i + 130));
        }

        for (var i = 0; i < living.Length; i++)
        {
            living[i] = world.Create();
            world.Add(living[i], new Health(i + 200));
        }

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);

        foreach (var entity in positionOnly)
        {
            Assert.True(loaded.TryGetLocation(entity, out var location));
            Assert.Equal(1, location.Archetype.Signature.Count);
            Assert.Equal(new Position(entity.Id, entity.Id + 10), GetComponent<Position>(loaded, entity));
        }

        for (var i = 0; i < moving.Length; i++)
        {
            var entity = moving[i];
            Assert.True(loaded.TryGetLocation(entity, out var location));
            Assert.Equal(2, location.Archetype.Signature.Count);
            Assert.Equal(new Position(i + 100, i + 110), GetComponent<Position>(loaded, entity));
            Assert.Equal(new Velocity(i + 120, i + 130), GetComponent<Velocity>(loaded, entity));
        }

        for (var i = 0; i < living.Length; i++)
        {
            var entity = living[i];
            Assert.True(loaded.TryGetLocation(entity, out var location));
            Assert.Equal(1, location.Archetype.Signature.Count);
            Assert.Equal(new Health(i + 200), GetComponent<Health>(loaded, entity));
        }

        Assert.True(loaded.TryGetLocation(positionOnly[2], out var thirdPositionOnlyLocation));
        Assert.NotNull(thirdPositionOnlyLocation.Archetype);

        Assert.True(loaded.TryGetLocation(moving[2], out var thirdMovingLocation));
        Assert.NotNull(thirdMovingLocation.Archetype);

        Assert.True(loaded.TryGetLocation(living[2], out var thirdLivingLocation));
        Assert.NotNull(thirdLivingLocation.Archetype);
    }


    [Fact]
    public void Snapshot_preserves_free_slot_versions_for_reused_entity_ids()
    {
        var world = new World();
        var original = world.Create();

        world.Destroy(original);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        var recreated = loaded.Create();

        Assert.Equal(original.Id, recreated.Id);
        Assert.Equal(original.Version + 1, recreated.Version);
        Assert.False(loaded.TryGetLocation(original, out _));
        Assert.True(loaded.TryGetLocation(recreated, out _));
    }

    [Fact]
    public void Snapshot_preserves_parent_and_children_relationships()
    {
        var world = new World();
        var parent = world.Create();
        var firstChild = world.Create();
        var secondChild = world.Create();

        world.AddChild(parent, firstChild);
        world.AddChild(parent, secondChild);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);

        Assert.True(loaded.TryGetParent(firstChild, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);
        Assert.Equal(
            [firstChild, secondChild],
            loaded.EnumerateChildren(parent).ToChildList().OrderBy(entity => entity.Id).ToArray());
    }

    [Fact]
    public void Snapshot_restores_hierarchy_so_cascade_destroy_still_works()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();

        world.AddChild(root, child);
        world.AddChild(child, grandChild);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        loaded.Destroy(root);

        Assert.False(loaded.IsAlive(root));
        Assert.False(loaded.IsAlive(child));
        Assert.False(loaded.IsAlive(grandChild));
    }

    [Fact]
    public void Save_canonicalizes_entity_row_order_within_archetype_so_load_yields_id_ascending_layout()
    {
        // Build a world where archetype internal rows are NOT in id order
        // (swap-remove on row 0 moves the last entity into the first slot).
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(10, 20));
        var e1 = world.Create(); world.Add(e1, new Position(30, 40));
        var e2 = world.Create(); world.Add(e2, new Position(50, 60));
        world.Destroy(e0); // swap-remove: e2 moves to row 0 -> internal [e2, e1]

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        // After canonical Save+Load, archetype rows should follow ascending entity id.
        Assert.True(loaded.TryGetLocation(e1, out var e1Location));
        Assert.True(loaded.TryGetLocation(e2, out var e2Location));
        Assert.Equal(0, e1Location.RowIndex);
        Assert.Equal(1, e2Location.RowIndex);
        Assert.Equal(new Position(30, 40), GetComponent<Position>(loaded, e1));
        Assert.Equal(new Position(50, 60), GetComponent<Position>(loaded, e2));
    }

    [Fact]
    public void Save_is_idempotent_after_round_trip_with_non_canonical_internal_layout()
    {
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(10, 20));
        var e1 = world.Create(); world.Add(e1, new Position(30, 40));
        var e2 = world.Create(); world.Add(e2, new Position(50, 60));
        world.Destroy(e0); // internal archetype rows: [e2, e1]

        using var stream1 = new MemoryStream();
        WorldSnapshot.Save(stream1, world);

        stream1.Position = 0;
        var loaded = WorldSnapshot.Load(stream1);

        using var stream2 = new MemoryStream();
        WorldSnapshot.Save(stream2, loaded);

        Assert.Equal(stream1.ToArray(), stream2.ToArray());
    }

    [Fact]
    public void Checksum_is_stable_for_identical_worlds_and_differs_on_mutation()
    {
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(1, 2));
        var e1 = world.Create(); world.Add(e1, new Position(3, 4));

        var hash1 = world.Checksum();
        var hash2 = world.Checksum();

        Assert.Equal(32, hash1.Length);
        Assert.Equal(hash1, hash2);

        world.Set(e0, new Position(99, 99));
        var hash3 = world.Checksum();
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void Checksum_matches_across_save_load_round_trip()
    {
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(1, 2));
        var e1 = world.Create(); world.Add(e1, new Velocity(3, 4));

        var hashOriginal = world.Checksum();

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        var hashLoaded = loaded.Checksum();
        Assert.Equal(hashOriginal, hashLoaded);
    }

    // ──────────────────────────────────────────────
    //  CanonicalChecksum
    // ──────────────────────────────────────────────

    [Fact]
    public void CanonicalChecksum_returns_32_bytes()
    {
        var world = new World();
        world.Create(new Position(1, 2));

        var hash = world.CanonicalChecksum();

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void CanonicalChecksum_matches_across_save_load_round_trip()
    {
        // The canonical hash is the right tool for comparing worlds that arrived at
        // the same logical state via different construction paths (live vs loaded).
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(1, 2));
        var e1 = world.Create(); world.Add(e1, new Velocity(3, 4));
        world.Destroy(e1);
        world.AddChild(e0, world.Create(new Health(9)));

        var hashOriginal = world.CanonicalChecksum();

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        Assert.Equal(hashOriginal, loaded.CanonicalChecksum());
    }

    [Fact]
    public void CanonicalChecksum_detects_free_list_divergence_ignored_by_checksum()
    {
        // Hardening target (commit 8f1d517): two worlds with identical live
        // entities/components/hierarchy but different free lists must hash
        // differently under CanonicalChecksum. Plain Checksum() only sees
        // slot-count + per-slot version, so two slot-compatible layouts with
        // divergent free lists produce the same Checksum but must diverge
        // under CanonicalChecksum (which appends every (id,version) free slot).
        var a = new World();
        var b = new World();

        // Both worlds arrive at the same live state: one Position entity at id 0.
        a.Create(new Position(1, 2));
        b.Create(new Position(1, 2));

        // Now diverge their free lists while keeping live state identical.
        // World A: create id 1 then destroy it  —free list [1(v2)], slot count 2
        // World B: create ids 1,2 then destroy both —free list [2(v2),1(v2)], slot count 3
        // In both worlds the only alive entity is id 0 with Position(1,2), so
        // live-state hashes must agree —but canonical (with free list) must not.
        var a1 = a.Create(new Velocity(0, 0));
        a.Destroy(a1);

        var b1 = b.Create(new Velocity(0, 0));
        var b2 = b.Create(new Health(0));
        b.Destroy(b1);
        b.Destroy(b2);

        // Sanity: identical live state.
        Assert.Equal(1, a.EntityCount);
        Assert.Equal(1, b.EntityCount);

        var canonicalA = a.CanonicalChecksum();
        var canonicalB = b.CanonicalChecksum();
        Assert.NotEqual(canonicalA, canonicalB);
    }

    [Fact]
    public void CanonicalChecksum_stable_for_identical_worlds_and_differs_on_mutation()
    {
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(1, 2));
        var e1 = world.Create(); world.Add(e1, new Position(3, 4));

        var hash1 = world.CanonicalChecksum();
        var hash2 = world.CanonicalChecksum();
        Assert.Equal(hash1, hash2);

        world.Set(e0, new Position(99, 99));
        var hash3 = world.CanonicalChecksum();
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void Save_load_preserves_free_id_allocation_order()
    {
        var world = new World();
        var e0 = world.Create(new Position(0, 0));
        var e1 = world.Create(new Position(1, 1));
        var e2 = world.Create(new Position(2, 2));
        var e3 = world.Create(new Position(3, 3));
        var e4 = world.Create(new Position(4, 4));

        // Destroy in non-descending order to create a specific LIFO free list.
        // Free list after these destroys (push order): [1, 3, 4]
        // Pop order on next Create: 4, 3, 1
        world.Destroy(e1);
        world.Destroy(e3);
        world.Destroy(e4);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        // Both worlds should allocate the same recycled ids in the same order.
        var a = world.Create();
        var b = world.Create();
        var c = world.Create();

        var la = loaded.Create();
        var lb = loaded.Create();
        var lc = loaded.Create();

        Assert.Equal(a.Id, la.Id);
        Assert.Equal(b.Id, lb.Id);
        Assert.Equal(c.Id, lc.Id);
    }

    // ──────────────────────────────────────────────
    //  Tier 1 in-memory rollback snapshot
    // ──────────────────────────────────────────────

    [Fact]
    public void Capture_and_restore_preserves_full_state()
    {
        var world = new World();
        var e0 = world.Create(new Position(1, 2));
        var e1 = world.Create(new Velocity(3, 4));
        var e2 = world.Create(new Position(5, 6));
        world.Add(e2, new Velocity(7, 8));
        world.Destroy(e1);

        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);

        var checksumPre = world.Checksum();
        var snapshot = world.CaptureState();

        // Mutate heavily
        var fresh = world.Create(new Health(100));
        world.Destroy(e0);
        world.Add(fresh, new Position(9, 10));
        world.Remove<Velocity>(fresh);
        world.RemoveChild(child);
        world.Destroy(parent);

        world.RestoreState(snapshot);
        var checksumPost = world.Checksum();

        Assert.Equal(checksumPre, checksumPost);
        Assert.True(world.IsAlive(e0));
        Assert.True(world.IsAlive(e2));
        Assert.False(world.IsAlive(e1));
        Assert.True(world.TryGetParent(child, out var restoredParent));
        Assert.Equal(parent, restoredParent);
    }

    [Fact]
    public void Rollback_and_replay_produces_deterministic_ids()
    {
        var world = new World();
        world.Create(new Position(0, 0));
        world.Create(new Position(1, 1));
        world.Create(new Position(2, 2));

        // Create a specific free-list state
        world.Destroy(world.Create(new Position(3, 3)));
        world.Destroy(world.Create(new Position(4, 4)));

        var snapshot = world.CaptureState();

        // Simulate a predicted frame
        var a1 = world.Create(new Position(10, 10));
        var a2 = world.Create(new Position(20, 20));

        world.RestoreState(snapshot);

        // Re-simulate same frame
        var b1 = world.Create(new Position(10, 10));
        var b2 = world.Create(new Position(20, 20));

        Assert.Equal(a1.Id, b1.Id);
        Assert.Equal(a2.Id, b2.Id);
        Assert.Equal(a1.Version, b1.Version);
        Assert.Equal(a2.Version, b2.Version);
    }

    [Fact]
    public void Capture_restore_twice_is_idempotent()
    {
        var world = new World();
        var e0 = world.Create(new Position(1, 2));
        var e1 = world.Create(new Velocity(3, 4));
        world.AddChild(e0, e1);

        var s1 = world.CaptureState();
        world.RestoreState(s1);
        var hash1 = world.Checksum();

        var s2 = world.CaptureState();
        world.RestoreState(s2);
        var hash2 = world.Checksum();

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Rollback_with_invalidated_caches_still_correct()
    {
        var world = new World();
        world.Create(new Position(1, 2));

        var snapshot = world.CaptureState();

        // Mutate
        world.Create(new Velocity(3, 4));
        world.Create(new Health(5));

        world.RestoreState(snapshot);

        // After rollback, only the Position entity should exist.
        // Verify by performing structural changes (which use destination caches).
        var fresh = world.Create(new Velocity(6, 7));
        Assert.True(world.IsAlive(fresh));
        Assert.Equal(2, world.EntityCount);
    }

    // ──────────────────────────────────────────────
    //  Chunked archetype coverage for CaptureState/RestoreState
    // ──────────────────────────────────────────────

    [Fact]
    public void Capture_restore_preserves_chunked_archetype_across_multiple_segments()
    {
        // Build a world whose Position archetype is chunked across multiple segments.
        // Position is 8 bytes; the byte-based segment capacity (2MB/8 = 262144) means
        // auto-promotion would need a huge entity count, so we force chunked explicitly
        // and then grow segments. All entities are created via world.Create so that
        // _records stays consistent —direct arch.AddEntity would bypass the registry.
        var world = new World();
        const int EntityCount = 40;
        var entities = new Entity[EntityCount];
        for (var i = 0; i < EntityCount; i++)
            entities[i] = world.Create(new Position(i, i * 2));

        // Promote the Position archetype to chunked and add a second empty segment
        // so subsequent world.Create calls land in a non-first segment.
        Assert.True(world.TryGetLocation(entities[0], out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        arch.AddSegmentForTesting();
        Assert.True(arch.IsChunked);
        Assert.True(arch.SegmentCount >= 2, $"expected >=2 segments, got {arch.SegmentCount}");

        var checksumPre = world.Checksum();
        var snapshot = world.CaptureState();

        // Mutate heavily: destroy several entities (triggers cross-segment swap-remove
        // that rewrites segment data), add a brand-new archetype (prediction frame),
        // and AddChild a parent/child to perturb the hierarchy table.
        for (var i = 0; i < EntityCount; i += 5)
            world.Destroy(entities[i]);
        var parent = world.Create(new Velocity(1, 1));
        var child = world.Create(new Velocity(2, 2));
        world.AddChild(parent, child);
        world.Create(new Health(7));

        world.RestoreState(snapshot);

        // Whole-world checksum must match the pre-mutation state. This simultaneously
        // validates entity records, free list, and chunked archetype bytes.
        Assert.Equal(checksumPre, world.Checksum());

        // Spot-check every entity we created before the snapshot: liveness and the
        // exact component value, regardless of which segment it landed in.
        for (var i = 0; i < EntityCount; i++)
        {
            Assert.True(world.IsAlive(entities[i]));
            Assert.Equal(new Position(i, i * 2), GetComponent<Position>(world, entities[i]));
        }
        Assert.Equal(EntityCount, world.EntityCount);

        // The archetype is still chunked with the same segment layout after restore.
        Assert.True(arch.IsChunked);
        Assert.True(arch.SegmentCount >= 2);
    }

    [Fact]
    public void Capture_restore_round_trip_is_chunked_aware_after_segment_growth_during_prediction()
    {
        // Regression: prediction frame may GrowChunked on the live archetype, adding
        // new trailing segments. After RestoreState, the archetype must revert to
        // exactly the segment layout captured, with no stale trailing-segment data.
        //
        // Use a large component so segment capacity (2MB / sizeof(Big)) is small —
        // a modest create burst then forces real GrowChunked during the prediction frame.
        var world = new World();
        var seed = world.Create(new BigPayload(1));
        Assert.True(world.TryGetLocation(seed, out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        var segmentsAtCapture = arch.SegmentCount;

        var snapshot = world.CaptureState();

        // Prediction frame: create enough entities to force at least one new segment
        // on the live archetype via the chunked write path.
        const int PredictedCount = 5000;
        var predicted = new Entity[PredictedCount];
        for (var i = 0; i < predicted.Length; i++)
            predicted[i] = world.Create(new BigPayload(2));        Assert.True(arch.SegmentCount > segmentsAtCapture,
            $"prediction should have grown segments from {segmentsAtCapture}, got {arch.SegmentCount}");

        world.RestoreState(snapshot);

        // Only the seed entity remains; archetype count and segment contents must match
        // the pre-prediction state exactly. Stale data in grown-then-unused trailing
        // segments must not affect observable state.
        Assert.Equal(1, world.EntityCount);
        Assert.True(world.IsAlive(seed));
        Assert.Equal(1, GetComponent<BigPayload>(world, seed).Tag);
        Assert.Equal(1, arch.EntityCount);

        // Re-creating after rollback must produce a deterministic id (free list intact)
        // and be observable through the query layer (cache was invalidated by restore).
        var next = world.Create(new BigPayload(3));
        Assert.True(world.IsAlive(next));
        Assert.Equal(2, world.EntityCount);

        var desc = new QueryDescription().With<BigPayload>();
        var query = world.Query(in desc);
        var seen = 0;
        foreach (var chunk in query.GetChunks())
        {
            var span = chunk.GetSpan<BigPayload>();
            for (var i = 0; i < chunk.Count; i++)
                seen++;
        }
        Assert.Equal(2, seen);
    }

    // ~512 bytes per entity —segment capacity —4096 (2MB / 512). A 5000-create
    // burst then reliably crosses a segment boundary during the prediction frame.
#pragma warning disable CS0649 // padding fields intentionally never assigned
    private struct BigPayload
    {
        public int Tag;
        public long P00, P01, P02, P03, P04, P05, P06, P07;
        public long P08, P09, P10, P11, P12, P13, P14, P15;
        public long P16, P17, P18, P19, P20, P21, P22, P23;
        public long P24, P25, P26, P27, P28, P29, P30, P31;
        public long P32, P33, P34, P35, P36, P37, P38, P39;
        public long P40, P41, P42, P43, P44, P45, P46, P47;
        public long P48, P49, P50, P51, P52, P53, P54, P55;
        public long P56, P57, P58, P59, P60, P61, P62, P63;

        public BigPayload(int tag) => Tag = tag;
    }
#pragma warning restore CS0649

    private static T GetComponent<T>(World world, Entity entity) where T : unmanaged
    {
        Assert.True(world.TryGetLocation(entity, out var location));

        var componentType = ComponentRegistry.Shared.GetOrCreate<T>();
        return location.Archetype.GetComponentAt<T>(location.Archetype.GetComponentIndex(componentType), location.RowIndex);
    }

    // ──────────────────────────────────────────────
    //  Lifecycle / rollback-pool coverage
    // ──────────────────────────────────────────────

    [Fact]
    public void Restored_snapshot_is_marked_recycled()
    {
        var world = new World();
        world.Create(new Position(1, 2));

        var snap = world.CaptureState();
        Assert.False(snap.IsRecycled);

        world.RestoreState(snap);
        Assert.True(snap.IsRecycled);
    }

    [Fact]
    public void Restoring_same_snapshot_twice_throws()
    {
        var world = new World();
        world.Create(new Position(1, 2));

        var snap = world.CaptureState();
        world.RestoreState(snap);

        Assert.Throws<InvalidOperationException>(() => world.RestoreState(snap));
    }

    [Fact]
    public void Multi_frame_rollback_window_round_trips_out_of_order()
    {
        // GGPO-style: capture N frames forward, then restore an earlier
        // handle on misprediction. The pool must support multiple live
        // snapshots simultaneously.
        var world = new World();
        var e = world.Create(new Position(0, 0));

        var ring = new WorldStateSnapshot[4];
        for (var i = 0; i < ring.Length; i++)
        {
            ring[i] = world.CaptureState();
            world.Set(e, new Position(i + 1, 0));
        }

        // World is now at Position(4, 0). Roll back to frame 1's snapshot,
        // which captured Position(1, 0). The other snapshots remain live.
        world.RestoreState(ring[1]);
        Assert.Equal(1, world.Get<Position>(e).X);

        // Restoring another still-live handle (frame 3) must work even though
        // ring[1] has been recycled into the pool.
        world.RestoreState(ring[3]);
        Assert.Equal(3, world.Get<Position>(e).X);
    }

    [Fact]
    public void Multi_frame_window_is_zero_alloc_in_steady_state()
    {
        // Warm the pool by running one full capture/restore cycle of depth N,
        // then assert that a second identical cycle allocates no new
        // WorldStateSnapshot instances. We detect this by counting how many
        // times the constructor would run: each pooled CaptureState reuses
        // an instance, so after warmup the pool depth covers the window.
        var world = new World();
        world.Create(new Position(7, 7));

        const int Depth = 6;
        var ring = new WorldStateSnapshot[Depth];

        // Warm-up: prime the pool.
        for (var i = 0; i < Depth; i++) ring[i] = world.CaptureState();
        for (var i = 0; i < Depth; i++) world.RestoreState(ring[i]);

        // Steady state: every CaptureState must pop from the pool. We verify
        // by checking reference identity against the warm-up handles, which
        // were all returned to the pool.
        for (var i = 0; i < Depth; i++)
        {
            var s = world.CaptureState();
            Assert.True(Array.IndexOf(ring, s) >= 0,
                "CaptureState should reuse a pooled instance in steady state.");
            world.RestoreState(s);
        }
    }

    // ══════════════════════════════════════════════════════════?
    // CRC32 integrity (v4 format)
    // ══════════════════════════════════════════════════════════?

    [Fact]
    public void V4_snapshot_round_trip_preserves_checksum()
    {
        var world = new World();
        world.Create(new Position(10, 20));
        world.Create(new Velocity(1, 2));

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        Assert.Equal(world.CanonicalChecksum(), loaded.CanonicalChecksum());
    }

    [Fact]
    public void V4_corrupted_snapshot_throws_InvalidDataException()
    {
        var world = new World();
        world.Create(new Position(10, 20));

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        var bytes = stream.ToArray();

        // Flip a byte in the body (after header, before CRC)
        var bodyOffset = 8; // magic(4) + version(4)
        bytes[bodyOffset + 4] ^= 0xFF;

        using var corrupted = new MemoryStream(bytes);
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(corrupted));
        Assert.Contains("CRC mismatch", ex.Message);
    }

    [Fact]
    public void V3_snapshot_without_CRC_loads_successfully()
    {
        // Build a minimal v3 snapshot: [magic:4][version=3:4][body...]
        // body contains: chunkCapacity=16, entitySlotCount=4, schemaCount=0,
        // archetypeCount=0, hierarchyLinkCount=0, slotVersions(4x0)
        // + empty free list (0 length).
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3); // v3 (no CRC)
        writer.Write(16); // chunkCapacity
        writer.Write(4);  // entitySlotCount
        writer.Write(0);  // schemaCount
        writer.Write(0);  // archetypeCount
        writer.Write(0);  // hierarchyLinkCount
        for (var i = 0; i < 4; i++) writer.Write(0); // slot versions
        writer.Write(0);  // free list length
        writer.Flush();

        ms.Position = 0;
        var world = WorldSnapshot.Load(ms);
        Assert.NotNull(world);
        Assert.Equal(4, world.EntitySlotCount);
    }

    // ───────────────────────────────────────────────────────────────────
    // Zero-allocation: Task 7 (SpanFeeder delegate -> ISpanFeeder struct)
    // ───────────────────────────────────────────────────────────────────
    //
    // Task 7 replaced `internal delegate void SpanFeeder(ReadOnlySpan<byte>)`
    // with a struct interface `ISpanFeeder` + generic `where TFeeder:struct`
    // methods taking `ref TFeeder`. Before Task 7, every FeedColumnData /
    // FeedRowData caller in ComputeChecksum / ComputeCanonicalChecksum passed
    // `span => hash.AppendData(span)`, a closure that captured the
    // IncrementalHash on the heap each call — i.e. one allocation per
    // component column per archetype per checksum. The struct rewrite removes
    // that allocation from the inner feeding loop.
    //
    // NOTE on scope: this makes the *per-column/per-row feeding path* zero
    // alloc. The full ComputeChecksum/ComputeCanonicalChecksum methods still
    // carry fixed per-call overhead (entity-id sort buffer, relations list)
    // that does NOT scale with component data volume. The two tests below
    // verify the inner-loop fix directly and prove allocation is independent
    // of column count.

    /// <summary>
    /// FeedColumnData with a struct ISpanFeeder allocates zero bytes,
    /// regardless of row count. This is the exact path Task 7 rewrote.
    /// Asserts the literal inner-loop invariant.
    /// </summary>
    [Fact]
    public void FeedColumnData_with_struct_feeder_is_zero_alloc()
    {
        RunOnDedicatedThread(() =>
        {
            var world = new World();
            for (var i = 0; i < 256; i++)
                world.Create(new Position(i, i + 1));

            var arch = world.Archetypes[0];
            var feeder = new NoOpFeeder();

            // Warmup (resolve JIT / method-table for the generic specialization).
            arch.FeedColumnData(0, 256, ref feeder);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            arch.FeedColumnData(0, 256, ref feeder);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        });
    }

    /// <summary>
    /// End-to-end proof that Task 7 removed per-COLUMN allocation: two worlds
    /// with identical entity/relation counts but different component-column
    /// counts (1 vs 3) must allocate the same number of bytes in
    /// ComputeCanonicalChecksum. If the closure had returned, the 3-column
    /// world would allocate ~2 extra delegate captures.
    /// </summary>
    [Fact]
    public void ComputeCanonicalChecksum_allocation_does_not_scale_with_column_count()
    {
        RunOnDedicatedThread(() =>
        {
            var worldA = new World();
            for (var i = 0; i < 100; i++)
                worldA.Create(new Position(i, i));              // 1 column

            var worldB = new World();
            for (var i = 0; i < 100; i++)
                worldB.Create(new Position(i, i),               // 3 columns
                              new Velocity(i, i),
                              new Health(i));

            // Warmup both (resolve column codecs, JIT the generic feeders).
            worldA.CanonicalChecksum();
            worldB.CanonicalChecksum();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var beforeA = GC.GetAllocatedBytesForCurrentThread();
            worldA.CanonicalChecksum();
            var allocA = GC.GetAllocatedBytesForCurrentThread() - beforeA;

            var beforeB = GC.GetAllocatedBytesForCurrentThread();
            worldB.CanonicalChecksum();
            var allocB = GC.GetAllocatedBytesForCurrentThread() - beforeB;

            // Fixed per-call overhead is identical (same entity count, same
            // relation count). The only column-dependent allocation source
            // was the pre-Task-7 closure. After Task 7 the difference is 0.
            // Tolerance of 8 bytes absorbs any measurement granularity; a
            // regression would add ~64 bytes (closure display class) per
            // extra column, well above the tolerance.
            var delta = allocB - allocA;
            Assert.True(delta <= 8,
                $"Per-column allocation regressed: 3-column checksum allocated {delta} bytes " +
                $"more than 1-column (allocA={allocA}, allocB={allocB}). Expected ~0.");
        });
    }

    // BUG REPROOF: when two archetypes with different segment capacities
    // (and thus different segment array sizes) are captured, the backup pool
    // reuses arrays sized for the larger archetype when backing up the
    // smaller one. RestoreTo then copies SegmentEntities[i].Length /
    // SegmentData[i].Length (the oversized length) into the smaller
    // destination arrays, causing ArgumentException.
    //
    // Empty archetype → _segmentCapacity = 65536.
    // BigPayload (~516 bytes) → _segmentCapacity = 4096.
    [Fact]
    public void BUG_chunked_restore_pooled_larger_backup_arrays_overflow_smaller_destination()
    {
        var world = new World();

        // Phase 1: create empty entity + BigPayload entity, force-chunk both
        // archetypes so they expose standard-capacity segments.
        var empty = world.Create();
        Assert.True(world.TryGetLocation(empty, out var emptyLoc));
        var archEmpty = emptyLoc.Archetype;
        archEmpty.ForceChunkedForTesting();    // segment arrays sized to 65536

        var bp = world.Create(new BigPayload(1));
        Assert.True(world.TryGetLocation(bp, out var bpLoc));
        var archBP = bpLoc.Archetype;
        archBP.ForceChunkedForTesting();       // segment arrays sized to 4096

        // Capture: both archetypes non-empty → two backup slots.
        // Slot 0 = Empty archetype (inserted first), segment arrays of size 65536.
        var snap = world.CaptureState();

        // Restore returns the snapshot to the pool.
        world.RestoreState(snap);

        // Phase 2: destroy the empty entity so its archetype becomes empty
        // and is skipped during the second capture. Only BigPayload remains
        // non-empty, reusing backup slot 0 whose arrays are oversized.
        world.Destroy(empty);

        // Capture again — the pool pops the recycled snapshot whose
        // ArchetypeBackups[0] still holds Empty's 65536-length arrays.
        // CopyFromChunked(BigPayload) reuses them (65536 >= 4096).
        snap = world.CaptureState();

        // Old code: RestoreTo copies SegmentEntities[0].Length (65536)
        // into archBP's segment[0].Entities (4096) → ArgumentException.
        // Fix: copies seg.Entities.Length (4096) instead.
        var ex = Record.Exception(() => world.RestoreState(snap));
        Assert.Null(ex);

        Assert.True(world.IsAlive(bp));
        Assert.Equal(new BigPayload(1), GetComponent<BigPayload>(world, bp));
    }

    private readonly struct NoOpFeeder : Archetype.ISpanFeeder
    {
        public void Feed(ReadOnlySpan<byte> span) { }
    }

    private static void RunOnDedicatedThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.Start();
        thread.Join();
        if (captured is not null) ExceptionDispatchInfo.Capture(captured).Throw();
    }
}

