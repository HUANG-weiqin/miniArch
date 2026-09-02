using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MiniArch;
using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;

namespace MiniArchTests.Persistence;

public sealed class WorldSnapshotTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);
    private readonly record struct ManagedReferenceComponent(string Name);
    private readonly record struct PartialMutationComponent(int Value);
    private readonly record struct NestedPayload(short Count, long Total);
    private readonly record struct NestedComponent(byte Kind, NestedPayload Payload);
    private readonly record struct InlineArrayComponent(IntInlineArray Values);
    private readonly record struct RegistrationOrderA(int Value);
    private readonly record struct RegistrationOrderB(long Value);
    private readonly record struct FloatingPointComponent(float Single, double Double);

    private enum SnapshotMode : short
    {
        Disabled = 0,
        Active = 7,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaddedComponent
    {
        public byte Tag;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBoolComponent
    {
        public bool Enabled;
        public byte Marker;
    }

    [InlineArray(3)]
    private struct IntInlineArray
    {
        public int _element0;
    }

    private unsafe struct FixedBufferComponent
    {
        public int Count;
        public fixed short Values[3];
    }

    private unsafe struct PointerComponent
    {
        public int* Value;
    }

    private struct NativeIntegerComponent
    {
        public IntPtr Signed;
        public UIntPtr Unsigned;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct OverlappingUnionComponent
    {
        [FieldOffset(0)] public int Integer;
        [FieldOffset(0)] public float Single;
    }

    [StructLayout(LayoutKind.Auto)]
    private struct AutoLayoutComponent
    {
        public int Value;
    }

    [StructLayout(LayoutKind.Auto)]
    private struct AutoLayoutLoadComponent
    {
        public int Value;
    }

    [Fact]
    public void Unmanaged_world_can_round_trip_preserving_entity_metadata_values_and_archetype_membership()
    {
        var world = new World(chunkCapacity: 2);

        var first = world.CreateEmpty();
        var second = world.CreateEmpty();
        var third = world.CreateEmpty();

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
            positionOnly[i] = world.CreateEmpty();
            world.Add(positionOnly[i], new Position(i, i + 10));
        }

        for (var i = 0; i < moving.Length; i++)
        {
            moving[i] = world.CreateEmpty();
            world.Add(moving[i], new Position(i + 100, i + 110));
            world.Add(moving[i], new Velocity(i + 120, i + 130));
        }

        for (var i = 0; i < living.Length; i++)
        {
            living[i] = world.CreateEmpty();
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
    public void BUG_snapshot_round_trip_preserves_reserved_entity_count()
    {
        using var world = new World();
        var stream = new CommandStream(world);
        var reserved = stream.Create();

        try
        {
            Assert.Equal(0, world.EntityCount);

            using var snapshot = new MemoryStream();
            WorldSnapshot.Save(snapshot, world);
            snapshot.Position = 0;
            using var loaded = WorldSnapshot.Load(snapshot);

            Assert.Equal(world.EntityCount, loaded.EntityCount);
            Assert.False(loaded.IsAlive(reserved));
        }
        finally
        {
            stream.Clear();
        }
    }

    [Fact]
    public void Snapshot_preserves_free_slot_versions_for_reused_entity_ids()
    {
        var world = new World();
        var original = world.CreateEmpty();

        world.Destroy(original);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        var recreated = loaded.CreateEmpty();

        Assert.Equal(original.Id, recreated.Id);
        Assert.Equal(original.Version + 1, recreated.Version);
        Assert.False(loaded.TryGetLocation(original, out _));
        Assert.True(loaded.TryGetLocation(recreated, out _));
    }

    [Fact]
    public void Snapshot_preserves_parent_and_children_relationships()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var firstChild = world.CreateEmpty();
        var secondChild = world.CreateEmpty();

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
        var root = world.CreateEmpty();
        var child = world.CreateEmpty();
        var grandChild = world.CreateEmpty();

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
        var e0 = world.CreateEmpty(); world.Add(e0, new Position(10, 20));
        var e1 = world.CreateEmpty(); world.Add(e1, new Position(30, 40));
        var e2 = world.CreateEmpty(); world.Add(e2, new Position(50, 60));
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
        var e0 = world.CreateEmpty(); world.Add(e0, new Position(10, 20));
        var e1 = world.CreateEmpty(); world.Add(e1, new Position(30, 40));
        var e2 = world.CreateEmpty(); world.Add(e2, new Position(50, 60));
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
    public void Save_writes_schema_identities_and_archetype_indices_in_ordinal_order()
    {
        using var world = new World();
        world.Create(new Position(10, 20), new Health(100));
        world.Create(new Position(30, 40), new Velocity(1, 2));

        var layout = ParseV5Snapshot(SaveToBytes(world));
        var identities = layout.Schemas.Select(schema => schema.Identity).ToArray();

        Assert.Equal(identities.OrderBy(identity => identity, StringComparer.Ordinal), identities);
        Assert.All(layout.Archetypes, archetype =>
        {
            var schemaIndices = archetype.SchemaIndexOffsets
                .Select(offset => ReadInt32(layout.Bytes, offset))
                .ToArray();
            Assert.Equal(schemaIndices.Order(), schemaIndices);
        });
    }

    [Fact]
    public void Save_load_preserves_empty_archetypes()
    {
        var world = new World();

        // Create three distinct signatures. Destroy all entities of one to
        // create an empty archetype. Empty archetypes must survive Save→Load
        // so that future creation of that signature doesn't change query order.
        world.Create(new Position(1, 2));     // archetype {Position}
        world.Create(new Health(3));           // archetype {Health}
        var temp = world.Create(new Velocity(4, 5)); // archetype {Velocity}
        world.Destroy(temp);                   // {Velocity} becomes empty

        // Archetypes are now sorted by signature. Capture original state.
        var origArchs = world.Archetypes;
        Assert.Equal(3, origArchs.Length);

        // Verify the {Velocity} archetype is empty.
        var emptySig = ComponentRegistry.Shared.GetOrCreate<Velocity>();
        var origEmptyArch = origArchs.FirstOrDefault(a =>
            a.Signature.AsSpan().Length == 1 && a.Signature.AsSpan()[0] == emptySig);
        Assert.NotNull(origEmptyArch);
        Assert.Equal(0, origEmptyArch.EntityCount);

        // Count non-empty archetypes.
        var nonEmpty = 0;
        foreach (var a in origArchs)
            if (a.EntityCount > 0) nonEmpty++;
        Assert.Equal(2, nonEmpty);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        // All three archetypes still exist, in the same sorted order.
        var loadedArchs = loaded.Archetypes;
        Assert.Equal(3, loadedArchs.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(origArchs[i].Signature.AsSpan().SequenceEqual(loadedArchs[i].Signature.AsSpan()),
                $"Archetype at index {i} has different signature after Save→Load.");
            Assert.Equal(origArchs[i].EntityCount, loadedArchs[i].EntityCount);
        }

        // Creating a new entity with the empty archetype's signature should
        // restore its entity count to 1.
        var newVy = loaded.Create(new Velocity(6, 7));
        var query = loaded.Query(new QueryDescription().With<Velocity>());
        var chunks = query.GetChunks();
        Assert.Equal(1, chunks.Length);
        Assert.Equal(new Velocity(6, 7), GetComponent<Velocity>(loaded, newVy));
    }

    [Fact]
    public void Save_is_idempotent_after_round_trip_with_empty_archetypes()
    {
        var world = new World();
        var e0 = world.Create(new Position(10, 20));
        var e1 = world.Create(new Velocity(30, 40));
        world.Destroy(e1); // {Velocity} becomes empty

        using var stream1 = new MemoryStream();
        WorldSnapshot.Save(stream1, world);

        stream1.Position = 0;
        var loaded = WorldSnapshot.Load(stream1);

        using var stream2 = new MemoryStream();
        WorldSnapshot.Save(stream2, loaded);

        // Second save must be byte-identical to the first despite empty archetype.
        Assert.Equal(stream1.ToArray(), stream2.ToArray());
    }

    [Fact]
    public void Snapshot_with_only_empty_archetypes_round_trips()
    {
        var world = new World();

        // Create then destroy each entity so every archetype is empty.
        var a = world.Create(new Position(1, 2));
        var b = world.Create(new Velocity(3, 4));
        var c = world.Create(new Health(5));
        world.Destroy(a);
        world.Destroy(b);
        world.Destroy(c);

        Assert.Equal(3, world.Archetypes.Length);
        Assert.All(world.Archetypes, a => Assert.Equal(0, a.EntityCount));

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        Assert.Equal(3, loaded.Archetypes.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0, loaded.Archetypes[i].EntityCount);
            Assert.True(
                world.Archetypes[i].Signature.AsSpan().SequenceEqual(
                    loaded.Archetypes[i].Signature.AsSpan()),
                $"Archetype {i} signature differs after round-trip.");
        }
    }

    [Fact]
    public void Checksum_is_stable_for_identical_worlds_and_differs_on_mutation()
    {
        var world = new World();
        var e0 = world.CreateEmpty(); world.Add(e0, new Position(1, 2));
        var e1 = world.CreateEmpty(); world.Add(e1, new Position(3, 4));

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
        var e0 = world.CreateEmpty(); world.Add(e0, new Position(1, 2));
        var e1 = world.CreateEmpty(); world.Add(e1, new Velocity(3, 4));

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
        // Load reconstructs the exact v5 persistence state, so the live and loaded
        // worlds must have the same canonical payload hash.
        var world = new World();
        var e0 = world.CreateEmpty(); world.Add(e0, new Position(1, 2));
        var e1 = world.CreateEmpty(); world.Add(e1, new Velocity(3, 4));
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
    public void CanonicalChecksum_detects_free_list_order_ignored_by_checksum()
    {
        using var a = new World();
        using var b = new World();

        a.Create(new Position(1, 2));
        b.Create(new Position(1, 2));
        var a1 = a.Create(new Velocity(0, 0));
        var a2 = a.Create(new Health(0));
        var b1 = b.Create(new Velocity(0, 0));
        var b2 = b.Create(new Health(0));

        // Slot versions, schemas, archetypes, and live state remain identical;
        // only the order of otherwise identical free-list entries differs.
        a.Destroy(a1);
        a.Destroy(a2);
        b.Destroy(b2);
        b.Destroy(b1);

        Assert.Equal(a.Checksum(), b.Checksum());
        Assert.NotEqual(a.CanonicalChecksum(), b.CanonicalChecksum());
    }

    [Fact]
    public void CanonicalChecksum_stable_for_identical_worlds_and_differs_on_mutation()
    {
        var world = new World();
        var e0 = world.CreateEmpty(); world.Add(e0, new Position(1, 2));
        var e1 = world.CreateEmpty(); world.Add(e1, new Position(3, 4));

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
        var a = world.CreateEmpty();
        var b = world.CreateEmpty();
        var c = world.CreateEmpty();

        var la = loaded.CreateEmpty();
        var lb = loaded.CreateEmpty();
        var lc = loaded.CreateEmpty();

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

        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
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
    public void RestoreState_clears_replay_placeholder_map()
    {
        // Scenario: replay populates the placeholder→real-entity map,
        // then a rollback via RestoreState must discard stale mappings
        // so TryResolvePlaceholder doesn't return entities from the
        // rolled-back frame.

        var world = new World(chunkCapacity: 4);
        // Create a baseline entity so the world isn't empty.
        world.Create(new Position(0, 0));

        var snapshot = world.CaptureState();

        // Produce a FrameDelta with a placeholder entity.
        var stream = new CommandStream(world) { DeferredEntities = true };
        var placeholder = stream.Create();
        stream.Add(placeholder, new Position(99, 99));
        var delta = stream.Snapshot();

        // Replay populates _replayPlaceholderMap.
        new CommandStream(world).Replay(delta);

        // The placeholder should resolve to a real entity now.
        Assert.True(world.TryResolvePlaceholder(new Entity(-1, 0), out var beforeRollback));
        Assert.NotEqual(default, beforeRollback);

        // Rollback: the replay-allocated entity must disappear,
        // and the placeholder map must be cleared.
        world.RestoreState(snapshot);

        // After rollback, the placeholder must NOT resolve.
        Assert.False(world.TryResolvePlaceholder(new Entity(-1, 0), out _));
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
        Assert.True(arch.IsChunked);
        arch.AddSegmentForTesting();
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
        Assert.True(arch.IsChunked);
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

    // WorldSnapshot Save/Load round-trip with explicitly forced-chunked
    // archetype (not just natural promotion). Verifies the serialization
    // path (WriteColumnOrderedTo / ReadColumnFrom) handles segment data.
    [Fact]
    public void Save_load_round_trip_with_explicitly_chunked_archetype()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);

        var originalEntities = new Entity[50];
        for (var i = 0; i < 50; i++)
            originalEntities[i] = world.Create(new Position(i, i * 10));

        // Force the archetype to be chunked
        Assert.True(world.TryGetLocation(new Entity(1, 1), out var info));
        info.Archetype.ForceChunkedForTesting();
        Assert.True(info.Archetype.IsChunked);

        // Save
        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        // Load
        var loaded = WorldSnapshot.Load(stream);

        // Verify data
        var desc = new QueryDescription().With<Position>();
        var count = 0;
        foreach (var chunk in loaded.Query(in desc).GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            for (var ci = 0; ci < chunk.Count; ci++)
            {
                Assert.Equal(new Position(count, count * 10), positions[ci]);
                count++;
            }
        }
        Assert.Equal(50, count);

        // Also verify via entity lookup using the original entity handle
        for (var i = 0; i < 50; i++)
        {
            Assert.True(loaded.IsAlive(originalEntities[i]));
            Assert.Equal(new Position(i, i * 10), GetComponent<Position>(loaded, originalEntities[i]));
        }
    }

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
    public void BUG_recycled_snapshot_reference_is_not_reactivated_after_pool_reuse()
    {
        using var world = new World();
        var entity = world.Create(new Position(0, 0));

        var oldLease = world.CaptureState();
        world.RestoreState(oldLease);

        world.Set(entity, new Position(5, 0));
        var freshLease = world.CaptureState();

        Assert.Same(oldLease.Payload, freshLease.Payload);
        Assert.True(oldLease.IsRecycled);
        Assert.False(freshLease.IsRecycled);

        oldLease.Dispose();
        Assert.False(freshLease.IsRecycled);

        world.Set(entity, new Position(9, 0));
        Assert.Throws<InvalidOperationException>(() => world.RestoreState(oldLease));
        Assert.Equal(new Position(9, 0), world.Get<Position>(entity));
        Assert.False(freshLease.IsRecycled);

        world.RestoreState(freshLease);
        Assert.Equal(new Position(5, 0), world.Get<Position>(entity));
    }

    [Fact]
    public void Default_snapshot_is_recycled_and_cannot_be_restored()
    {
        using var world = new World();
        var snapshot = default(WorldStateSnapshot);

        Assert.True(snapshot.IsRecycled);
        Assert.Throws<InvalidOperationException>(() => world.RestoreState(snapshot));
    }

    [Fact]
    public void Disposing_snapshot_recycles_payload_without_restoring_world()
    {
        using var world = new World();
        var entity = world.Create(new Position(1, 0));
        var snapshot = world.CaptureState();
        var copy = snapshot;
        var payload = snapshot.Payload;

        world.Set(entity, new Position(2, 0));
        snapshot.Dispose();

        Assert.True(snapshot.IsRecycled);
        Assert.True(copy.IsRecycled);
        copy.Dispose();
        Assert.Equal(new Position(2, 0), world.Get<Position>(entity));
        Assert.Throws<InvalidOperationException>(() => world.RestoreState(snapshot));

        var next = world.CaptureState();
        Assert.Same(payload, next.Payload);
        next.Dispose();
    }

    [Fact]
    public void Cross_world_restore_failure_does_not_consume_source_snapshot()
    {
        using var source = new World();
        using var target = new World();
        source.Create(new Position(1, 0));
        var snapshot = source.CaptureState();

        Assert.Throws<InvalidOperationException>(() => target.RestoreState(snapshot));
        Assert.False(snapshot.IsRecycled);

        source.RestoreState(snapshot);
        Assert.True(snapshot.IsRecycled);
    }

    [Fact]
    public void Disposing_snapshot_after_world_disposal_releases_backing_storage()
    {
        var world = new World();
        world.Create(new Position(1, 0));
        var snapshot = world.CaptureState();
        var payload = snapshot.Payload;
        Assert.NotEmpty(payload.Records);

        world.Dispose();
        Assert.True(snapshot.IsRecycled);

        snapshot.Dispose();
        snapshot.Dispose();

        Assert.Empty(payload.Records);
        Assert.Empty(payload.ArchetypeBackups);
        Assert.Empty(payload.HierarchyParentByChild);
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
    public void BUG_reused_larger_rollback_snapshot_does_not_restore_stale_hierarchy_tail()
    {
        using var world = new World();

        // Keep two live handles so the larger pooled instance can be reused
        // while the current world has a zero-length hierarchy table.
        var empty1 = world.CaptureState();
        var oldParent = world.CreateEmpty();
        var oldChild = world.CreateEmpty();
        world.AddChild(oldParent, oldChild);
        var withHierarchy = world.CaptureState();

        world.RestoreState(empty1);
        var empty2 = world.CaptureState();
        world.RestoreState(withHierarchy);
        world.RestoreState(empty2);

        var keepTopPoolEntryLive = world.CaptureState();
        var reusedLargerEntry = world.CaptureState();
        world.RestoreState(reusedLargerEntry);

        var newParent = world.CreateEmpty();
        var newChild = world.CreateEmpty();

        Assert.False(world.TryGetParent(newChild, out _));

        world.RestoreState(keepTopPoolEntryLive);
    }

    [Fact]
    public void Multi_frame_window_is_zero_alloc_in_steady_state()
    {
        // Warm the pool by running one full capture/restore cycle of depth N,
        // then assert that a second identical cycle reuses those payloads.
        // WorldStateSnapshot itself is a value lease; the large payload is pooled.
        var world = new World();
        world.Create(new Position(7, 7));

        const int Depth = 6;
        var ring = new WorldStateSnapshot[Depth];
        var payloads = new object?[Depth];

        // Warm-up: prime the pool.
        for (var i = 0; i < Depth; i++)
        {
            ring[i] = world.CaptureState();
            payloads[i] = ring[i].Payload;
        }
        for (var i = 0; i < Depth; i++) world.RestoreState(ring[i]);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Depth; i++) ring[i] = world.CaptureState();
        for (var i = 0; i < Depth; i++) world.RestoreState(ring[i]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        for (var i = 0; i < Depth; i++)
        {
            Assert.True(Array.IndexOf(payloads, ring[i].Payload) >= 0,
                "CaptureState should reuse a pooled payload in steady state.");
        }
    }

    [Fact]
    public void BUG_rolling_snapshot_window_discards_old_payloads_without_allocation()
    {
        using var world = new World();
        world.Create(new Position(7, 7));

        const int Depth = 4;
        var ring = new WorldStateSnapshot[Depth];

        // Warm the payload pool and its Stack backing storage.
        for (var i = 0; i < Depth; i++) ring[i] = world.CaptureState();
        for (var i = 0; i < Depth; i++) ring[i].Dispose();
        for (var i = 0; i < Depth; i++) ring[i] = world.CaptureState();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 256; frame++)
        {
            var slot = frame % Depth;
            ring[slot].Dispose();
            ring[slot] = world.CaptureState();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        for (var i = 0; i < Depth; i++) ring[i].Dispose();

        Assert.Equal(0, allocated);
    }

    // ══════════════════════════════════════════════════════════
    // Canonical field wire format (v5)
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void V5_snapshot_round_trip_preserves_checksum_and_header_integrity()
    {
        using var world = new World();
        world.Create(new Position(10, 20));
        world.Create(new Velocity(1, 2));

        var bytes = SaveToBytes(world);
        Assert.Equal(0x4D415243, ReadInt32(bytes, 0));
        Assert.Equal(5, ReadInt32(bytes, 4));
        Assert.Equal(
            Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(uint))));
        Assert.Equal(
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - sizeof(uint))),
            world.CanonicalChecksum());

        using var loaded = WorldSnapshot.Load(new MemoryStream(bytes, writable: false));
        Assert.Equal(world.CanonicalChecksum(), loaded.CanonicalChecksum());
        Assert.Equal(bytes, SaveToBytes(loaded));
    }

    [Fact]
    public void V5_primitive_payload_is_little_endian_and_preserves_floating_point_bits()
    {
        const int x = 0x01020304;
        const int y = 0x11121314;
        const int singleBits = unchecked((int)0x7FC12345u);
        const long doubleBits = unchecked((long)0xFFF8000000001234UL);

        using var world = new World();
        var entity = world.Create(
            new Position(x, y),
            new FloatingPointComponent(
                BitConverter.Int32BitsToSingle(singleBits),
                BitConverter.Int64BitsToDouble(doubleBits)));
        var bytes = SaveToBytes(world);
        var layout = ParseV5Snapshot(bytes);
        var columns = layout.Archetypes.SelectMany(archetype => archetype.Columns).ToArray();
        var position = Assert.Single(columns, column => column.ComponentType == typeof(Position));
        var floatingPoint = Assert.Single(columns, column => column.ComponentType == typeof(FloatingPointComponent));

        Assert.Equal(
            new byte[] { 0x04, 0x03, 0x02, 0x01, 0x14, 0x13, 0x12, 0x11 },
            bytes.AsSpan(position.PayloadOffset, position.PayloadLength).ToArray());
        Assert.Equal(
            new byte[] { 0x34, 0x12, 0x00, 0x00, 0x00, 0x00, 0xF8, 0xFF, 0x45, 0x23, 0xC1, 0x7F },
            bytes.AsSpan(floatingPoint.PayloadOffset, floatingPoint.PayloadLength).ToArray());

        using var loaded = WorldSnapshot.Load(new MemoryStream(bytes, writable: false));
        var restored = GetComponent<FloatingPointComponent>(loaded, entity);
        Assert.Equal(singleBits, BitConverter.SingleToInt32Bits(restored.Single));
        Assert.Equal(doubleBits, BitConverter.DoubleToInt64Bits(restored.Double));
    }

    [Fact]
    public void V5_corrupted_snapshot_throws_InvalidDataException()
    {
        using var world = new World();
        world.Create(new Position(10, 20));
        var bytes = SaveToBytes(world);
        bytes[12] ^= 0x01;

        var exception = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(bytes, writable: false)));
        Assert.Contains("CRC", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Legacy_snapshot_versions_are_explicitly_rejected(int version)
    {
        using var world = new World();
        var bytes = SaveToBytes(world);
        WriteInt32(bytes, 4, version);
        RewriteCrc(bytes);

        var exception = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(bytes, writable: false)));
        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V5_save_ignores_poisoned_clr_padding_and_round_trip_is_byte_idempotent()
    {
        Assert.True(
            Unsafe.SizeOf<PaddedComponent>() > sizeof(byte) + sizeof(int),
            "The regression component must contain CLR padding.");

        using var first = new World();
        using var second = new World();
        var firstEntity = first.Create(new PaddedComponent { Tag = 7, Value = 123456 });
        var secondEntity = second.Create(new PaddedComponent { Tag = 7, Value = 123456 });
        PoisonPaddedComponent(first, firstEntity, 0xA5);
        PoisonPaddedComponent(second, secondEntity, 0x5A);

        Assert.Equal(GetComponent<PaddedComponent>(first, firstEntity).Value,
            GetComponent<PaddedComponent>(second, secondEntity).Value);
        Assert.NotEqual(GetRawComponentBytes<PaddedComponent>(first, firstEntity),
            GetRawComponentBytes<PaddedComponent>(second, secondEntity));

        var firstBytes = SaveToBytes(first);
        var secondBytes = SaveToBytes(second);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(first.CanonicalChecksum(), second.CanonicalChecksum());

        using var loaded = WorldSnapshot.Load(new MemoryStream(firstBytes, writable: false));
        var component = GetComponent<PaddedComponent>(loaded, firstEntity);
        Assert.Equal((byte)7, component.Tag);
        Assert.Equal(123456, component.Value);
        Assert.Equal(firstBytes, SaveToBytes(loaded));
    }

    [Fact]
    public unsafe void V5_round_trip_preserves_enum_nested_fixed_buffer_and_inline_array_fields()
    {
        var inline = new IntInlineArray();
        inline[0] = 11;
        inline[1] = 22;
        inline[2] = 33;
        var fixedBuffer = new FixedBufferComponent { Count = 3 };
        fixedBuffer.Values[0] = -4;
        fixedBuffer.Values[1] = 5;
        fixedBuffer.Values[2] = 6;

        using var world = new World();
        var entity = world.Create(
            SnapshotMode.Active,
            new NestedComponent(9, new NestedPayload(12, 3456789)),
            fixedBuffer,
            new InlineArrayComponent(inline));
        var bytes = SaveToBytes(world);

        using var loaded = WorldSnapshot.Load(new MemoryStream(bytes, writable: false));
        Assert.Equal(SnapshotMode.Active, GetComponent<SnapshotMode>(loaded, entity));
        Assert.Equal(
            new NestedComponent(9, new NestedPayload(12, 3456789)),
            GetComponent<NestedComponent>(loaded, entity));
        var loadedFixed = GetComponent<FixedBufferComponent>(loaded, entity);
        Assert.Equal(3, loadedFixed.Count);
        Assert.Equal((short)-4, loadedFixed.Values[0]);
        Assert.Equal((short)5, loadedFixed.Values[1]);
        Assert.Equal((short)6, loadedFixed.Values[2]);
        var loadedInline = GetComponent<InlineArrayComponent>(loaded, entity).Values;
        Assert.Equal(11, loadedInline[0]);
        Assert.Equal(22, loadedInline[1]);
        Assert.Equal(33, loadedInline[2]);
        Assert.Equal(bytes, SaveToBytes(loaded));
    }

    [Fact]
    public void V5_save_normalizes_native_bool_storage_and_load_rejects_noncanonical_bool()
    {
        using var world = new World();
        var entity = world.Create(new NativeBoolComponent { Enabled = true, Marker = 42 });
        SetRawComponentByte<NativeBoolComponent>(world, entity, 0, 0x02);

        var bytes = SaveToBytes(world);
        var layout = ParseV5Snapshot(bytes);
        var boolColumn = layout.Archetypes.SelectMany(archetype => archetype.Columns)
            .Single(column => column.ComponentType == typeof(NativeBoolComponent));
        Assert.Equal(1, bytes[boolColumn.PayloadOffset]);

        using var loaded = WorldSnapshot.Load(new MemoryStream(bytes, writable: false));
        var component = GetComponent<NativeBoolComponent>(loaded, entity);
        Assert.True(component.Enabled);
        Assert.Equal((byte)42, component.Marker);

        bytes[boolColumn.PayloadOffset] = 2;
        RewriteCrc(bytes);
        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(bytes, writable: false)));
    }

    [Fact]
    public void Unsupported_component_shapes_fail_before_snapshot_bytes_are_published()
    {
        if (Environment.GetEnvironmentVariable(UnsupportedProbeEnvironmentVariable) is not null)
            return;

        foreach (var mode in new[] { "Pointer", "NativeInteger", "OverlappingUnion", "AutoLayout" })
            RunUnsupportedComponentProbe(mode);
    }

    [Fact]
    public unsafe void Unsupported_component_shape_probe()
    {
        var mode = Environment.GetEnvironmentVariable(UnsupportedProbeEnvironmentVariable);
        if (mode is null)
            return;

        switch (mode)
        {
            case "Pointer":
                AssertSaveNotSupported(new PointerComponent { Value = null });
                break;
            case "NativeInteger":
                AssertSaveNotSupported(new NativeIntegerComponent
                {
                    Signed = new IntPtr(1),
                    Unsigned = new UIntPtr(2),
                });
                break;
            case "OverlappingUnion":
                AssertSaveNotSupported(new OverlappingUnionComponent { Integer = 1 });
                break;
            case "AutoLayout":
                using (var world = new World())
                {
                    _ = Assert.Throws<NotSupportedException>(
                        () => world.Create(new AutoLayoutComponent { Value = 1 }));
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown unsupported-component probe mode '{mode}'.");
        }
    }

    [Fact]
    public void Fresh_process_registration_order_produces_identical_v5_bytes_and_checksum()
    {
        if (Environment.GetEnvironmentVariable(ProbeModeEnvironmentVariable) is not null)
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"MiniArchSnapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var ab = Path.Combine(directory, "ab.bin");
            var ba = Path.Combine(directory, "ba.bin");
            RunRegistrationOrderProbe("AB", ab);
            RunRegistrationOrderProbe("BA", ba);

            Assert.Equal(File.ReadAllBytes(ab), File.ReadAllBytes(ba));
            Assert.Equal(File.ReadAllBytes(ab + ".sha256"), File.ReadAllBytes(ba + ".sha256"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Fresh_process_registration_order_probe()
    {
        var mode = Environment.GetEnvironmentVariable(ProbeModeEnvironmentVariable);
        if (mode is null)
            return;

        var output = Environment.GetEnvironmentVariable(ProbeOutputEnvironmentVariable)
            ?? throw new InvalidOperationException("Snapshot probe output path is missing.");
        using (var registrationWorld = new World())
        {
            if (mode == "AB")
            {
                registrationWorld.Create(new RegistrationOrderA(1));
                registrationWorld.Create(new RegistrationOrderB(2));
            }
            else if (mode == "BA")
            {
                registrationWorld.Create(new RegistrationOrderB(2));
                registrationWorld.Create(new RegistrationOrderA(1));
            }
            else
            {
                throw new InvalidOperationException($"Unknown snapshot probe mode '{mode}'.");
            }
        }

        using var world = new World(chunkCapacity: 2);
        var parent = world.Create(new RegistrationOrderA(10), new RegistrationOrderB(20));
        var child = world.Create(new RegistrationOrderA(30));
        world.AddChild(parent, child);
        var recycled = world.Create(new RegistrationOrderB(40));
        world.Destroy(recycled);

        File.WriteAllBytes(output, SaveToBytes(world));
        File.WriteAllBytes(output + ".sha256", world.CanonicalChecksum());
    }

    // BUG REPROOF: CaptureState stores a non-chunked backup; if the archetype
    // is promoted to chunked storage between CaptureState and RestoreState
    // (e.g. a GGPO prediction frame creates enough entities to trigger
    // chunked promotion), RestoreState crashes inside RestoreTo because the
    // non-chunked restore branch assumes arch._data / arch entity storage are
    // the flat arrays it captured, but they have been replaced by segment
    // arrays (or _data is null).
    [Fact]
    public void BUG_capture_nonchunked_then_promote_then_restore_crashes()
    {
        var world = new World();
        var seed = world.Create(new Position(1, 2));
        Assert.True(world.TryGetLocation(seed, out var info));
        var arch = info.Archetype;
        Assert.False(arch.IsChunked);

        var snapshot = world.CaptureState();

        // Simulate prediction-time promotion to chunked storage.
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        // This throws ArgumentException ("Destination array was not long
        // enough") from RestoreTo's Array.Copy(Entities, arch.GetEntityStorageUnsafe(), Count).
        world.RestoreState(snapshot);

        Assert.True(world.IsAlive(seed));
        Assert.Equal(new Position(1, 2), GetComponent<Position>(world, seed));
    }

    // Regression: multi-column cross-mode restore. Backup taken non-chunked
    // has column data laid out at old-capacity offsets; after promotion the
    // archetype uses segment-capacity offsets. RestoreFlatBackup must rebase
    // each column independently.
    [Fact]
    public void Restore_flat_backup_to_chunked_preserves_multi_column_data()
    {
        var world = new World();
        var e1 = world.Create(new Position(10, 20), new Velocity(1, 2));
        var e2 = world.Create(new Position(30, 40), new Velocity(3, 4));
        Assert.True(world.TryGetLocation(e1, out var info));
        var arch = info.Archetype;
        Assert.False(arch.IsChunked);

        var snapshot = world.CaptureState();
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        world.RestoreState(snapshot);

        Assert.True(world.TryGet(e1, out Position p1));
        Assert.Equal(new Position(10, 20), p1);
        Assert.True(world.TryGet(e1, out Velocity v1));
        Assert.Equal(new Velocity(1, 2), v1);

        Assert.True(world.TryGet(e2, out Position p2));
        Assert.Equal(new Position(30, 40), p2);
        Assert.True(world.TryGet(e2, out Velocity v2));
        Assert.Equal(new Velocity(3, 4), v2);
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
        var empty = world.CreateEmpty();
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

    // BUG REPROOF: Non-chunked archetype with small chunkCapacity undergoes
    // EnsureCapacity doubling between Capture and Restore, changing column
    // byte offsets. CopyDataFrom copies old-layout backup data into the
    // new-layout _data buffer at the wrong offsets. The real scenario:
    // prediction MODIFIES a component at the new offset, and CopyDataFrom
    // does NOT overwrite it, so the entity keeps the predicted value.
    [Fact]
    public void BUG_nonchunked_restore_after_capacity_doubling_does_not_restore_modified_column()
    {
        // Use small chunkCapacity so EntityCount quickly triggers doubling.
        var world = new World(chunkCapacity: 4);

        var e1 = world.Create(new Position(10, 20), new Velocity(1, 2));
        var snap = world.CaptureState();

        // Trigger EnsureCapacity doubling (capacity 4 → 8).
        // With Position(8B)+Velocity(8B):
        //   old layout (cap=4): pos@0(32B), vel@32(32B)  → _data=64
        //   new layout (cap=8): pos@0(64B), vel@64(64B)  → _data=128
        for (var i = 0; i < 5; i++)
            world.Create(new Position(i, i), new Velocity(i, i));

        // Modify e1's Velocity during prediction (written at new offset 64).
        world.Set(e1, new Velocity(99, 100));

        // Restore — e1 must revert to the captured Velocity(1, 2).
        // CopyDataFrom copies 64-byte backup into _data[0..63].
        // But _data[64] (new vel offset) is NOT overwritten → still (99,100).
        world.RestoreState(snap);

        Assert.True(world.IsAlive(e1));
        Assert.True(world.TryGet(e1, out Position p1));
        Assert.Equal(new Position(10, 20), p1);

        Assert.True(world.TryGet(e1, out Velocity v1),
            "Velocity should be restored to the captured value. If this " +
            "fails then CopyDataFrom did not rebase column offsets.");
        Assert.Equal(new Velocity(1, 2), v1);
    }

    // 1024-byte component → segCap = 2048. Used to trigger chunked promotion
    // without allocating hundreds of thousands of entities.
    private unsafe struct Component1024
    {
        public int Value;
        public fixed byte Pad[1020];
    }

    // Capture flat → predict (promote to chunked) → restore → verify count.
    // Exercises RestoreFlatBackup loading a flat backup into a now-chunked archetype.
    [Fact]
    public void Capture_nonchunked_promoted_during_prediction_restores_correctly()
    {
        var world = new World();
        for (var i = 0; i < 100; i++)
        {
            var e = world.CreateEmpty();
            world.Add(e, new Component1024 { Value = i });
        }

        var snap = world.CaptureState();

        // Prediction: add enough to promote Component1024 archetype (segCap=2048).
        for (var i = 0; i < 4000; i++)
        {
            var e = world.CreateEmpty();
            world.Add(e, new Component1024 { Value = i + 1000 });
        }

        world.RestoreState(snap);
        Assert.Equal(100, world.EntityCount);
    }

    // Capture → restore cycle twice with a chunked archetype.
    // Verifies pool reuse of backup arrays stays stable across cycles.
    [Fact]
    public void Capture_restore_cycle_twice_with_chunked_archetype_is_stable()
    {
        var world = new World();
        // Create enough entities with Component1024 to trigger chunked mode.
        for (var i = 0; i < 3000; i++)
        {
            var e = world.CreateEmpty();
            world.Add(e, new Component1024 { Value = i });
        }

        var snap1 = world.CaptureState();

        // Modify world.
        for (var i = 0; i < 1000; i++)
        {
            var e = world.CreateEmpty();
            world.Add(e, new Component1024 { Value = i + 9000 });
        }

        world.RestoreState(snap1);
        Assert.Equal(3000, world.EntityCount);

        var snap2 = world.CaptureState(); // pool reuse — backup arrays from snap1

        world.RestoreState(snap2); // restore from pool-recycled arrays
        Assert.Equal(3000, world.EntityCount);
    }

    [Fact]
    public void Export_and_Import_round_trips_schema_independent_of_world()
    {
        var world = new World();
        var entity = world.CreateEmpty();
        world.Add<Position>(entity, new Position(0, 0));
        world.Add<Velocity>(entity, new Velocity(0, 0));
        world.Add<Health>(entity, new Health(0));

        // Export: pure schema — no entity data, no world state
        var schemaBytes = ComponentSchema.Export();
        Assert.NotNull(schemaBytes);
        Assert.True(schemaBytes.Length > 0, "Schema should not be empty");

        // Import: resolve type names back to Type objects
        var schemaTypes = ComponentSchema.Import(schemaBytes);

        // Every registered type is present exactly once
        Assert.Contains(typeof(Position), schemaTypes);
        Assert.Contains(typeof(Velocity), schemaTypes);
        Assert.Contains(typeof(Health), schemaTypes);
        Assert.Equal(1, schemaTypes.Count(t => t == typeof(Position)));

        world.Dispose();
    }

    [Fact]
    public void Importing_same_schema_twice_is_idempotent()
    {
        var world = new World();
        var entity = world.CreateEmpty();
        world.Add<Position>(entity, new Position(0, 0));

        var schemaBytes = ComponentSchema.Export();

        // First import
        var first = ComponentSchema.Import(schemaBytes);
        // Second import — types already registered
        var second = ComponentSchema.Import(schemaBytes);

        // Same Type[] content
        Assert.Equal(first, second);

        world.Dispose();
    }

    [Fact]
    public void Schema_import_rejects_corrupt_data()
    {
        // Bad magic: first 4 bytes should be 0x4D435343
        var badMagic = new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 };
        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(badMagic));

        // Unsupported format version
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.UTF8))
        {
            writer.Write(0x4D435343); // magic
            writer.Write(999);        // bogus version
            writer.Write(0);          // count
            writer.Flush();
            _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(ms.ToArray()));
        }

        // Truncated (count says 10 but no data follows)
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.UTF8))
        {
            writer.Write(0x4D435343); // magic
            writer.Write(1);          // version
            writer.Write(10);         // count — but no strings follow
            writer.Flush();
            _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(ms.ToArray()));
        }

        // Negative count
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.UTF8))
        {
            writer.Write(0x4D435343);
            writer.Write(1);
            writer.Write(-1);
            writer.Flush();
            _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(ms.ToArray()));
        }

        // Empty byte array (EndOfStreamException → InvalidDataException)
        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(Array.Empty<byte>()));

        // Trailing bytes
        var goodBytes = ComponentSchema.Export();
        var padded = goodBytes.Concat(new byte[] { 1, 2, 3 }).ToArray();
        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(padded));
    }

    [Fact]
    public void Schema_import_rejects_unresolvable_type()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write(0x4D435343); // magic
        writer.Write(1);          // version
        writer.Write(1);          // count
        writer.Write("Some.NonExistent.Type, FakeAssembly");
        writer.Flush();
        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(ms.ToArray()));
    }

    [Fact]
    public void Schema_import_rejects_duplicate_type()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write(0x4D435343); // magic
        writer.Write(1);          // version
        writer.Write(2);          // count — two entries, same name
        var typeName = typeof(Position).AssemblyQualifiedName!;
        writer.Write(typeName);
        writer.Write(typeName);
        writer.Flush();
        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(ms.ToArray()));
    }

    [Fact]
    public void Schema_import_rejects_generic_type()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write(0x4D435343);
        writer.Write(1);
        writer.Write(1);
        // An open generic type (not a concrete component)
        writer.Write(typeof(List<>).AssemblyQualifiedName!);
        writer.Flush();
        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(ms.ToArray()));
    }

    [Fact]
    public void Schema_import_handles_empty_schema()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        writer.Write(0x4D435343); // magic
        writer.Write(1);          // version
        writer.Write(0);          // count = 0 → empty schema
        writer.Flush();

        var types = ComponentSchema.Import(ms.ToArray());
        Assert.NotNull(types);
        Assert.Empty(types);
    }

    [Fact]
    public void Schema_import_null_throws_argument_null()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ComponentSchema.Import(null!));
    }

    [Fact]
    public void Schema_import_rejects_class_type()
    {
        var data = BuildComponentSchemaBlob(typeof(string).AssemblyQualifiedName!);

        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(data));
    }

    [Fact]
    public void Schema_import_rejects_managed_field_struct()
    {
        var data = BuildComponentSchemaBlob(typeof(ManagedReferenceComponent).AssemblyQualifiedName!);

        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(data));
    }

    [Fact]
    public void Schema_import_rejects_duplicate_resolved_type()
    {
        var data = BuildComponentSchemaBlob(
            typeof(int).FullName!,
            typeof(int).AssemblyQualifiedName!);

        _ = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(data));
    }

    [Fact]
    public void Schema_import_rejects_overlong_type_name_without_large_allocation()
    {
        var data = BuildComponentSchemaBlobWithRawStringByteLength(1024 * 1024);

        var ex = Assert.Throws<InvalidDataException>(() => ComponentSchema.Import(data));
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_load_rejects_unsupported_schema_types_as_invalid_data()
    {
        var shape = GetSchemaShape<int>();
        foreach (var type in new[]
                 {
                     typeof(string),
                     typeof(ManagedReferenceComponent),
                     typeof(AutoLayoutLoadComponent),
                 })
        {
            var data = BuildV5SnapshotWithSchemas([(type.AssemblyQualifiedName!, shape)]);
            _ = Assert.Throws<InvalidDataException>(
                () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
        }
    }

    [Fact]
    public void Snapshot_load_rejects_duplicate_resolved_schema_type()
    {
        var shape = GetSchemaShape<int>();
        var data = BuildV5SnapshotWithSchemas(
        [
            (typeof(int).FullName!, shape),
            (typeof(int).AssemblyQualifiedName!, shape),
        ]);

        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
    }

    [Fact]
    public void Snapshot_load_rejects_overlong_schema_name_without_large_allocation()
    {
        var data = BuildV5SnapshotWithRawSchemaNameLength(1024 * 1024);

        var exception = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
        Assert.Contains("length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void Snapshot_load_rejects_invalid_chunk_capacity(int chunkCapacity)
    {
        using var world = new World();
        var data = SaveToBytes(world);
        WriteInt32(data, 8, chunkCapacity);
        RewriteCrc(data);

        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
    }

    [Fact]
    public void Snapshot_load_rejects_non_positive_slot_version()
    {
        using var world = new World();
        _ = world.CreateEmpty();
        var data = SaveToBytes(world);
        var layout = ParseV5Snapshot(data);
        WriteInt32(data, layout.SlotVersionOffsets[0], 0);
        RewriteCrc(data);

        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Snapshot_load_rejects_invalid_free_list_count(int freeCount)
    {
        using var world = new World();
        var data = SaveToBytes(world);
        var layout = ParseV5Snapshot(data);
        WriteInt32(data, layout.FreeCountOffset, freeCount);
        RewriteCrc(data);

        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
    }

    [Fact]
    public void Snapshot_load_rejects_free_id_out_of_range()
    {
        using var world = new World();
        var entity = world.CreateEmpty();
        world.Destroy(entity);
        var data = SaveToBytes(world);
        var layout = ParseV5Snapshot(data);
        WriteInt32(data, layout.FreeIdOffsets.Single(), layout.SlotVersionOffsets.Length);
        RewriteCrc(data);

        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
    }

    [Fact]
    public void Snapshot_load_rejects_invalid_archetype_component_metadata()
    {
        using var world = new World();
        world.Create(new Position(1, 2), new Velocity(3, 4));
        var original = SaveToBytes(world);
        var layout = ParseV5Snapshot(original);
        var archetype = layout.Archetypes.Single();

        var negativeCount = (byte[])original.Clone();
        WriteInt32(negativeCount, archetype.ComponentCountOffset, -1);
        RewriteCrc(negativeCount);
        AssertInvalidSnapshot(negativeCount);

        var outOfRangeIndex = (byte[])original.Clone();
        WriteInt32(outOfRangeIndex, archetype.SchemaIndexOffsets[0], layout.Schemas.Count);
        RewriteCrc(outOfRangeIndex);
        AssertInvalidSnapshot(outOfRangeIndex);

        var duplicateIndex = (byte[])original.Clone();
        WriteInt32(
            duplicateIndex,
            archetype.SchemaIndexOffsets[1],
            ReadInt32(duplicateIndex, archetype.SchemaIndexOffsets[0]));
        RewriteCrc(duplicateIndex);
        AssertInvalidSnapshot(duplicateIndex);
    }

    [Fact]
    public void Snapshot_load_rejects_duplicate_archetype_signature()
    {
        using var world = new World();
        var position = world.Create(new Position(1, 2));
        var velocity = world.Create(new Velocity(3, 4));
        world.Destroy(position);
        world.Destroy(velocity);
        var data = SaveToBytes(world);
        var layout = ParseV5Snapshot(data);
        Assert.Equal(2, layout.Archetypes.Count);

        WriteInt32(
            data,
            layout.Archetypes[1].SchemaIndexOffsets.Single(),
            ReadInt32(data, layout.Archetypes[0].SchemaIndexOffsets.Single()));
        RewriteCrc(data);

        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
    }

    [Fact]
    public void Snapshot_load_rejects_invalid_entity_rows()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        world.Create(new Position(3, 4));
        var original = SaveToBytes(world);
        var layout = ParseV5Snapshot(original);
        var archetype = layout.Archetypes.Single();

        var excessiveCount = (byte[])original.Clone();
        WriteInt32(excessiveCount, archetype.RowCountOffset, layout.SlotVersionOffsets.Length + 1);
        RewriteCrc(excessiveCount);
        AssertInvalidSnapshot(excessiveCount);

        var duplicateId = (byte[])original.Clone();
        WriteInt32(
            duplicateId,
            archetype.EntityIdOffsets[1],
            ReadInt32(duplicateId, archetype.EntityIdOffsets[0]));
        RewriteCrc(duplicateId);
        AssertInvalidSnapshot(duplicateId);
    }

    [Fact]
    public void Snapshot_load_rejects_hierarchy_out_of_range_duplicate_child_and_cycle()
    {
        using var world = new World();
        var root = world.CreateEmpty();
        var child = world.CreateEmpty();
        var grandChild = world.CreateEmpty();
        world.AddChild(root, child);
        world.AddChild(child, grandChild);
        var original = SaveToBytes(world);
        var layout = ParseV5Snapshot(original);

        var outOfRange = (byte[])original.Clone();
        WriteInt32(outOfRange, layout.HierarchyLinks[0].ParentOffset, layout.SlotVersionOffsets.Length);
        RewriteCrc(outOfRange);
        AssertInvalidSnapshot(outOfRange);

        var duplicateChild = (byte[])original.Clone();
        WriteInt32(
            duplicateChild,
            layout.HierarchyLinks[1].ChildOffset,
            ReadInt32(duplicateChild, layout.HierarchyLinks[0].ChildOffset));
        RewriteCrc(duplicateChild);
        AssertInvalidSnapshot(duplicateChild);

        var cycle = (byte[])original.Clone();
        var childLink = layout.HierarchyLinks.Single(link => ReadInt32(cycle, link.ChildOffset) == child.Id);
        WriteInt32(cycle, childLink.ParentOffset, grandChild.Id);
        RewriteCrc(cycle);
        AssertInvalidSnapshot(cycle);
    }

    [Fact]
    public void Snapshot_load_rejects_corrupt_schema_shape_with_valid_crc()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        var data = SaveToBytes(world);
        var schema = ParseV5Snapshot(data).Schemas.Single();
        Assert.True(schema.ShapeLength > 0);
        data[schema.ShapeOffset] ^= 0xFF;
        RewriteCrc(data);

        AssertInvalidSnapshot(data);
    }

    [Fact]
    public void Snapshot_load_rejects_payload_truncation_and_trailing_bytes_with_valid_crc()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        var original = SaveToBytes(world);
        var column = ParseV5Snapshot(original).Archetypes.Single().Columns.Single();

        var truncated = RemovePayloadByte(original, column.PayloadOffset + column.PayloadLength - 1);
        AssertInvalidSnapshot(truncated);

        var trailing = InsertPayloadByte(original, original.Length - sizeof(uint), 0xCC);
        AssertInvalidSnapshot(trailing);
    }

    [Fact]
    public void Snapshot_load_rejects_truncated_large_slot_table_before_allocation()
    {
        var data = BuildV5HeaderOnlySnapshot(slotCount: 5_000_000);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var exception = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Contains("slot version table", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(allocated < 1_000_000,
            $"Truncated header allocated {allocated:N0} bytes before rejection.");
    }

    [Fact]
    public void Snapshot_load_does_not_register_schema_type_when_later_payload_is_invalid()
    {
        var schemaType = typeof(PartialMutationComponent);
        Assert.DoesNotContain(schemaType, ComponentRegistry.Shared.GetRegisteredTypes());
        var data = BuildV5SnapshotWithSchemas(
            [(schemaType.AssemblyQualifiedName!, GetSchemaShape<Health>())],
            archetypeCount: 1,
            writeArchetypes: writer =>
            {
                writer.Write(0);
                writer.Write(5);
            });

        AssertInvalidSnapshot(data);

        Assert.DoesNotContain(schemaType, ComponentRegistry.Shared.GetRegisteredTypes());
    }

    private static byte[] BuildComponentSchemaBlob(params string[] schemaNames)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D435343);
        writer.Write(1);
        writer.Write(schemaNames.Length);
        foreach (var name in schemaNames)
            ComponentSchemaCodec.WriteSchemaName(writer, name);
        writer.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildComponentSchemaBlobWithRawStringByteLength(int byteLength)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D435343);
        writer.Write(1);
        writer.Write(1);
        writer.Flush();
        Write7BitEncodedInt(ms, byteLength);
        return ms.ToArray();
    }

    private const string ProbeModeEnvironmentVariable = "MINIARCH_SNAPSHOT_PROBE_MODE";
    private const string ProbeOutputEnvironmentVariable = "MINIARCH_SNAPSHOT_PROBE_OUTPUT";
    private const string UnsupportedProbeEnvironmentVariable = "MINIARCH_SNAPSHOT_UNSUPPORTED_PROBE";

    private static byte[] SaveToBytes(World world)
    {
        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        return stream.ToArray();
    }

    private static void AssertInvalidSnapshot(byte[] data)
    {
        _ = Assert.Throws<InvalidDataException>(
            () => WorldSnapshot.Load(new MemoryStream(data, writable: false)));
    }

    private static void AssertSaveNotSupported<T>(T component) where T : unmanaged
    {
        using var world = new World();
        world.Create(component);
        using var stream = new MemoryStream();

        _ = Assert.Throws<NotSupportedException>(() => WorldSnapshot.Save(stream, world));
        Assert.Equal(0, stream.Length);
    }

    private static byte[] GetSchemaShape<T>() where T : unmanaged
    {
        using var world = new World();
        world.Create(default(T));
        var layout = ParseV5Snapshot(SaveToBytes(world));
        var schema = layout.Schemas.Single(entry => entry.ComponentType == typeof(T));
        return layout.Bytes.AsSpan(schema.ShapeOffset, schema.ShapeLength).ToArray();
    }

    private static byte[] BuildV5SnapshotWithSchemas(
        (string Identity, byte[] Shape)[] schemas,
        int archetypeCount = 0,
        Action<BinaryWriter>? writeArchetypes = null)
    {
        var orderedSchemas = schemas.OrderBy(schema => schema.Identity, StringComparer.Ordinal).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243);
        writer.Write(5);
        writer.Write(16);
        writer.Write(4);
        writer.Write(orderedSchemas.Length);
        writer.Write(archetypeCount);
        writer.Write(0);
        for (var index = 0; index < 4; index++)
            writer.Write(1);
        foreach (var (identity, shape) in orderedSchemas)
        {
            var identityBytes = Encoding.UTF8.GetBytes(identity);
            writer.Write(identityBytes.Length);
            writer.Write(identityBytes);
            writer.Write(shape.Length);
            writer.Write(shape);
        }
        writeArchetypes?.Invoke(writer);
        writer.Write(0);
        writer.Flush();
        return AppendCrc(stream.ToArray());
    }

    private static byte[] BuildV5SnapshotWithRawSchemaNameLength(int byteLength)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243);
        writer.Write(5);
        writer.Write(16);
        writer.Write(4);
        writer.Write(1);
        writer.Write(0);
        writer.Write(0);
        for (var index = 0; index < 4; index++)
            writer.Write(1);
        writer.Write(byteLength);
        writer.Write(0);
        writer.Flush();
        return AppendCrc(stream.ToArray());
    }

    private static byte[] BuildV5HeaderOnlySnapshot(int slotCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243);
        writer.Write(5);
        writer.Write(16);
        writer.Write(slotCount);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Flush();
        return AppendCrc(stream.ToArray());
    }

    private static byte[] AppendCrc(byte[] headerAndPayload)
    {
        var result = new byte[headerAndPayload.Length + sizeof(uint)];
        headerAndPayload.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(headerAndPayload.Length),
            Crc32.HashToUInt32(headerAndPayload));
        return result;
    }

    private static void RewriteCrc(byte[] snapshot)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            snapshot.AsSpan(snapshot.Length - sizeof(uint)),
            Crc32.HashToUInt32(snapshot.AsSpan(0, snapshot.Length - sizeof(uint))));
    }

    private static byte[] RemovePayloadByte(byte[] snapshot, int offset)
    {
        var payloadLength = snapshot.Length - sizeof(uint);
        Assert.InRange(offset, 0, payloadLength - 1);
        var content = new byte[payloadLength - 1];
        snapshot.AsSpan(0, offset).CopyTo(content);
        snapshot.AsSpan(offset + 1, payloadLength - offset - 1).CopyTo(content.AsSpan(offset));
        return AppendCrc(content);
    }

    private static byte[] InsertPayloadByte(byte[] snapshot, int offset, byte value)
    {
        var payloadLength = snapshot.Length - sizeof(uint);
        Assert.InRange(offset, 0, payloadLength);
        var content = new byte[payloadLength + 1];
        snapshot.AsSpan(0, offset).CopyTo(content);
        content[offset] = value;
        snapshot.AsSpan(offset, payloadLength - offset).CopyTo(content.AsSpan(offset + 1));
        return AppendCrc(content);
    }

    private static int ReadInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));

    private static void WriteInt32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)), value);

    private static SnapshotLayout ParseV5Snapshot(byte[] bytes)
    {
        Assert.Equal(0x4D415243, ReadInt32(bytes, 0));
        Assert.Equal(5, ReadInt32(bytes, 4));
        var end = bytes.Length - sizeof(uint);
        var offset = 8;
        _ = ReadAndAdvance(bytes, ref offset);
        var slotCount = ReadAndAdvance(bytes, ref offset);
        var schemaCount = ReadAndAdvance(bytes, ref offset);
        var archetypeCount = ReadAndAdvance(bytes, ref offset);
        var hierarchyCount = ReadAndAdvance(bytes, ref offset);

        var slotVersionOffsets = new int[slotCount];
        for (var index = 0; index < slotVersionOffsets.Length; index++)
        {
            slotVersionOffsets[index] = offset;
            offset += sizeof(int);
        }

        var schemas = new List<SnapshotSchemaLayout>(schemaCount);
        for (var index = 0; index < schemaCount; index++)
        {
            var identityLength = ReadAndAdvance(bytes, ref offset);
            var identity = Encoding.UTF8.GetString(bytes, offset, identityLength);
            offset += identityLength;
            var shapeLength = ReadAndAdvance(bytes, ref offset);
            var shapeOffset = offset;
            offset += shapeLength;
            var componentType = Type.GetType(identity, throwOnError: true)!;
            schemas.Add(new SnapshotSchemaLayout(
                componentType,
                identity,
                shapeOffset,
                shapeLength));
        }

        var archetypes = new List<SnapshotArchetypeLayout>(archetypeCount);
        for (var index = 0; index < archetypeCount; index++)
        {
            var componentCountOffset = offset;
            var componentCount = ReadAndAdvance(bytes, ref offset);
            var schemaIndexOffsets = new int[componentCount];
            var schemaIndices = new int[componentCount];
            for (var componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                schemaIndexOffsets[componentIndex] = offset;
                schemaIndices[componentIndex] = ReadAndAdvance(bytes, ref offset);
            }

            var rowCountOffset = offset;
            var rowCount = ReadAndAdvance(bytes, ref offset);
            var entityIdOffsets = new int[rowCount];
            for (var row = 0; row < rowCount; row++)
            {
                entityIdOffsets[row] = offset;
                offset += sizeof(int);
            }

            var columns = new List<SnapshotColumnLayout>(componentCount);
            foreach (var schemaIndex in schemaIndices)
            {
                var componentType = schemas[schemaIndex].ComponentType;
                var payloadLength = checked(GetCanonicalWireWidth(componentType) * rowCount);
                columns.Add(new SnapshotColumnLayout(componentType, offset, payloadLength));
                offset += payloadLength;
            }

            archetypes.Add(new SnapshotArchetypeLayout(
                componentCountOffset,
                schemaIndexOffsets,
                rowCountOffset,
                entityIdOffsets,
                columns));
        }

        var hierarchyLinks = new List<SnapshotHierarchyLayout>(hierarchyCount);
        for (var index = 0; index < hierarchyCount; index++)
        {
            hierarchyLinks.Add(new SnapshotHierarchyLayout(offset, offset + sizeof(int)));
            offset += 2 * sizeof(int);
        }

        var freeCountOffset = offset;
        var freeCount = ReadAndAdvance(bytes, ref offset);
        var freeIdOffsets = new int[freeCount];
        for (var index = 0; index < freeCount; index++)
        {
            freeIdOffsets[index] = offset;
            offset += sizeof(int);
        }

        Assert.Equal(end, offset);
        return new SnapshotLayout(
            bytes,
            slotVersionOffsets,
            schemas,
            archetypes,
            hierarchyLinks,
            freeCountOffset,
            freeIdOffsets);
    }

    private static int ReadAndAdvance(byte[] bytes, ref int offset)
    {
        var value = ReadInt32(bytes, offset);
        offset += sizeof(int);
        return value;
    }

    private static int GetCanonicalWireWidth(Type type)
    {
        if (type.IsEnum)
            return GetCanonicalWireWidth(Enum.GetUnderlyingType(type));
        if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte))
            return 1;
        if (type == typeof(char) || type == typeof(short) || type == typeof(ushort))
            return 2;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(float))
            return 4;
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(double))
            return 8;

        var inlineArray = type.GetCustomAttribute<InlineArrayAttribute>();
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (inlineArray is not null)
        {
            Assert.Single(fields);
            return checked(inlineArray.Length * GetCanonicalWireWidth(fields[0].FieldType));
        }

        var width = 0;
        foreach (var field in fields)
        {
            var fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>();
            width = checked(width + (fixedBuffer is null
                ? GetCanonicalWireWidth(field.FieldType)
                : fixedBuffer.Length * GetCanonicalWireWidth(fixedBuffer.ElementType)));
        }
        return width;
    }

    private static byte[] GetRawComponentBytes<T>(World world, Entity entity) where T : unmanaged
    {
        Assert.True(world.TryGetLocation(entity, out var location));
        var column = location.Archetype.GetComponentIndex(ComponentRegistry.Shared.GetOrCreate<T>());
        return location.Archetype.GetComponentBytes(column, location.RowIndex).ToArray();
    }

    private static void SetRawComponentByte<T>(World world, Entity entity, int byteOffset, byte value)
        where T : unmanaged
    {
        Assert.True(world.TryGetLocation(entity, out var location));
        var column = location.Archetype.GetComponentIndex(ComponentRegistry.Shared.GetOrCreate<T>());
        var bytes = location.Archetype.GetComponentBytes(column, location.RowIndex);
        Assert.InRange(byteOffset, 0, bytes.Length - 1);
        ref var first = ref MemoryMarshal.GetReference(bytes);
        Unsafe.Add(ref first, byteOffset) = value;
    }

    private static void PoisonPaddedComponent(World world, Entity entity, byte value)
    {
        var valueOffset = Marshal.OffsetOf<PaddedComponent>(nameof(PaddedComponent.Value)).ToInt32();
        for (var offset = sizeof(byte); offset < valueOffset; offset++)
            SetRawComponentByte<PaddedComponent>(world, entity, offset, value);
    }

    private static void RunRegistrationOrderProbe(string mode, string output)
    {
        RunChildProbe(
            nameof(Fresh_process_registration_order_probe),
            mode,
            (ProbeModeEnvironmentVariable, mode),
            (ProbeOutputEnvironmentVariable, output));
        Assert.True(File.Exists(output) && File.Exists(output + ".sha256"),
            $"Snapshot probe {mode} did not publish both output files.");
    }

    private static void RunUnsupportedComponentProbe(string mode)
    {
        RunChildProbe(
            nameof(Unsupported_component_shape_probe),
            mode,
            (UnsupportedProbeEnvironmentVariable, mode));
    }

    private static void RunChildProbe(
        string methodName,
        string displayName,
        params (string Name, string Value)[] environment)
    {
        var project = FindTestProject();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var filter = $"FullyQualifiedName={typeof(WorldSnapshotTests).FullName}.{methodName}";
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add(filter);
        foreach (var (name, value) in environment)
            startInfo.Environment[name] = value;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start snapshot probe {displayName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail(
                $"Snapshot probe {displayName} timed out.\nstdout:\n{stdout.GetAwaiter().GetResult()}\nstderr:\n{stderr.GetAwaiter().GetResult()}");
        }

        var standardOutput = stdout.GetAwaiter().GetResult();
        var standardError = stderr.GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"Snapshot probe {displayName} failed with exit code {process.ExitCode}.\n" +
            $"stdout:\n{standardOutput}\nstderr:\n{standardError}");
    }

    private static string FindTestProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "MiniArch.Tests", "MiniArch.Tests.csproj");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Could not locate tests/MiniArch.Tests/MiniArch.Tests.csproj.");
    }

    private sealed record SnapshotLayout(
        byte[] Bytes,
        int[] SlotVersionOffsets,
        IReadOnlyList<SnapshotSchemaLayout> Schemas,
        IReadOnlyList<SnapshotArchetypeLayout> Archetypes,
        IReadOnlyList<SnapshotHierarchyLayout> HierarchyLinks,
        int FreeCountOffset,
        int[] FreeIdOffsets);

    private sealed record SnapshotSchemaLayout(
        Type ComponentType,
        string Identity,
        int ShapeOffset,
        int ShapeLength);

    private sealed record SnapshotArchetypeLayout(
        int ComponentCountOffset,
        int[] SchemaIndexOffsets,
        int RowCountOffset,
        int[] EntityIdOffsets,
        IReadOnlyList<SnapshotColumnLayout> Columns);

    private sealed record SnapshotColumnLayout(Type ComponentType, int PayloadOffset, int PayloadLength);
    private sealed record SnapshotHierarchyLayout(int ChildOffset, int ParentOffset);

    private static void Write7BitEncodedInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            stream.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        stream.WriteByte((byte)remaining);
    }
}
