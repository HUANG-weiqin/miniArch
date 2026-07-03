using MiniArch;
using MiniArch.Core;

namespace MiniArchTests.Core;

/// <summary>
/// Hostile-wire / attack-surface tests for FrameDelta replay.
///
/// Design: <see cref="FrameDelta.Validate()"/> is the defense boundary —
/// it walks the delta once and rejects structural violations with clear errors.
/// After Validate passes, <see cref="World.Replay"/> is safe because the delta
/// conforms to the wire format contract.
///
/// These tests document:
///   A) <c>delta.Validate()</c> → throws (malformed delta rejected)
///   B) <c>world.Replay(delta)</c> without prior Validate → silently corrupts
///      (demonstrating WHY validation is needed for untrusted deltas)
///
/// The first block of each test shows the safe path (A).  The second block (B)
/// documents the current unfixed behavior —present so that the gap is visible
/// and any future fix that changes (B) is immediately caught.
/// </summary>
public sealed class FrameDeltaAttackSurfaceTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Health(int Value);

    // ── helpers ──────────────────────────────────────────────────────────

    private static byte[] V(int value)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        uint v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            bytes.Add(b);
        } while (v != 0);
        return bytes.ToArray();
    }

    private static byte[] Enc(Entity e) => [.. V(e.Id), .. V(e.Version)];

    private static int QueryCount(World w)
    {
        var count = 0;
        foreach (var chunk in w.Query(new QueryDescription()).GetChunks())
            count += chunk.Count;
        return count;
    }

    // ══════════════════════════════════════════════════════════
    //  1. Create-without-Reserve
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_rejects_create_without_reserve()
    {
        var delta = FrameDelta.Deserialize([0x03, 0x00, 0x01, 0x00]);

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    [Fact]
    public void Replay_without_validate_accepts_create_without_reserve_and_leaks_ghost()
    {
        var delta = FrameDelta.Deserialize([0x03, 0x00, 0x01, 0x00]);
        var world = new World();

        world.Replay(delta); // no Validate → ghost created

        Assert.Equal(1, QueryCount(world));
        Assert.Equal(0, world.EntityCount);
        Assert.False(world.IsAlive(new Entity(0, 1)));
    }

    // ══════════════════════════════════════════════════════════
    //  2. Duplicate Create
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_rejects_duplicate_create()
    {
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(0, 1))]);
        buf.AddRange([(byte)DeltaOpKind.Create, .. Enc(new Entity(0, 1)), 0x00]);
        buf.AddRange([(byte)DeltaOpKind.Create, .. Enc(new Entity(0, 1)), 0x00]);
        var delta = FrameDelta.Deserialize(buf.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    [Fact]
    public void Replay_without_validate_accepts_duplicate_create_and_leaks_row()
    {
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(0, 1))]);
        buf.AddRange([(byte)DeltaOpKind.Create, .. Enc(new Entity(0, 1)), 0x00]);
        buf.AddRange([(byte)DeltaOpKind.Create, .. Enc(new Entity(0, 1)), 0x00]);
        var delta = FrameDelta.Deserialize(buf.ToArray());
        var world = new World();

        world.Replay(delta); // no Validate → duplicate row leaked

        Assert.Equal(2, QueryCount(world));
        Assert.Equal(1, world.EntityCount);
    }

    // ══════════════════════════════════════════════════════════
    //  3. Reserve mismatch — allocator leak (Entity state, not delta validation)
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_does_not_check_allocator_state_so_reserve_mismatch_still_replay_rejects()
    {
        // Validate() only checks delta structure, not world allocator state.
        // The allocator-advance-before-throw is still a World.Replay issue.
        var delta = FrameDelta.Deserialize([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(5, 1))]);
        delta.Validate(); // passes —delta structure is fine

        var world = new World();
        world.Create();
        var ex = Assert.Throws<InvalidOperationException>(() => world.Replay(delta));
        Assert.Contains("out of sync", ex.Message);
        // Allocator leaked (slot 1 consumed) — documented in the test above.
    }

    // ══════════════════════════════════════════════════════════
    //  4. Component data size mismatch
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_rejects_component_data_size_mismatch()
    {
        var posType = Component<Position>.ComponentType.Value;
        // sizeof(Position) = 8, delta declares dataSize = 1
        var buf = new System.Collections.Generic.List<byte>();
        buf.Add((byte)DeltaOpKind.Set);
        buf.AddRange(Enc(new Entity(0, 1)));
        buf.AddRange(V(posType));
        buf.AddRange(V(1)); // wrong size
        buf.Add(0x42);

        var delta = new FrameDelta { _buffer = buf.ToArray(), _length = buf.Count, _opCount = 1 };

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("has size", ex.Message);
        Assert.Contains("but delta declares", ex.Message);
    }

    [Fact]
    public void Replay_without_validate_accepts_size_mismatch_and_corrupts_data()
    {
        // Buffer: [Set Entity(0,1) type=Position size=1] [0x42]
        //         [Destroy Entity(127,1)] [padding for 8-byte read]
        // WriteComponentRaw copies 8 bytes regardless of declared size=1,
        // reading the Destroy op's bytes as component data.
        var world = new World();
        var e = world.Create();
        world.Add(e, new Position(100, 200));

        var posType = Component<Position>.ComponentType.Value;
        var buf = new System.Collections.Generic.List<byte>();
        buf.Add((byte)DeltaOpKind.Set);
        buf.AddRange(Enc(new Entity(0, 1)));
        buf.AddRange(V(posType));
        buf.AddRange(V(1)); // dataSize on wire = 1 (wrong; sizeof = 8)
        buf.Add(0x42);
        buf.Add((byte)DeltaOpKind.Destroy);
        buf.AddRange(Enc(new Entity(127, 1)));
        buf.Add((byte)DeltaOpKind.Destroy);
        buf.AddRange(Enc(new Entity(255, 1)));

        var delta = new FrameDelta { _buffer = buf.ToArray(), _length = buf.Count, _opCount = 3 };

        world.Replay(delta); // no Validate → silent corruption

        Assert.True(world.TryGet(e, out Position p));
        Assert.NotEqual(100, p.X);
        Assert.NotEqual(200, p.Y);
    }

    // ══════════════════════════════════════════════════════════
    //  5. Negative component count in Create
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_rejects_negative_component_count()
    {
        // Reserve(0,1) + Create(0,1) compCount=-1
        var wire = new byte[] {
            (byte)DeltaOpKind.Reserve, 0x00, 0x01,
            (byte)DeltaOpKind.Create, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F
        };
        var delta = FrameDelta.Deserialize(wire);

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("Negative component count", ex.Message);
    }

    // ══════════════════════════════════════════════════════════
    //  6. Duplicate component types in Create payload
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_rejects_duplicate_component_types_in_create()
    {
        var healthType = Component<Health>.ComponentType.Value;
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(0, 1))]);
        buf.Add((byte)DeltaOpKind.Create);
        buf.AddRange(Enc(new Entity(0, 1)));
        buf.AddRange(V(2)); // compCount = 2
        buf.AddRange(V(healthType)); buf.AddRange(V(4)); buf.AddRange(System.BitConverter.GetBytes(1));
        buf.AddRange(V(healthType)); buf.AddRange(V(4)); buf.AddRange(System.BitConverter.GetBytes(100));

        var delta = FrameDelta.Deserialize(buf.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("Duplicate component type", ex.Message);
    }

    [Fact]
    public void Replay_without_validate_accepts_duplicate_types_last_write_wins()
    {
        var healthType = Component<Health>.ComponentType.Value;
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(0, 1))]);
        buf.Add((byte)DeltaOpKind.Create);
        buf.AddRange(Enc(new Entity(0, 1)));
        buf.AddRange(V(2));
        buf.AddRange(V(healthType)); buf.AddRange(V(4)); buf.AddRange(System.BitConverter.GetBytes(1));
        buf.AddRange(V(healthType)); buf.AddRange(V(4)); buf.AddRange(System.BitConverter.GetBytes(100));

        var delta = FrameDelta.Deserialize(buf.ToArray());
        var world = new World();

        world.Replay(delta); // no Validate → last write wins

        Assert.True(world.IsAlive(new Entity(0, 1)));
        Assert.True(world.TryGet(new Entity(0, 1), out Health h));
        Assert.Equal(100, h.Value);
    }

    // ══════════════════════════════════════════════════════════
    //  7. State machine: Reserve → Release → Create rejected
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_rejects_create_after_release()
    {
        // Reserve(0,1) → Release(0,1) → Create(0,1) should be rejected
        // because Create comes after the entity entered terminal state.
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(0, 1))]);
        buf.AddRange([(byte)DeltaOpKind.Release, .. Enc(new Entity(0, 1))]);
        buf.AddRange([(byte)DeltaOpKind.Create, .. Enc(new Entity(0, 1)), 0x00]);
        var delta = FrameDelta.Deserialize(buf.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    [Fact]
    public void Validate_rejects_release_after_create()
    {
        // Reserve(0,1) → Create(0,1) → Release(0,1) should be rejected
        // because Release comes after entity is already created.
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(0, 1))]);
        buf.AddRange([(byte)DeltaOpKind.Create, .. Enc(new Entity(0, 1)), 0x00]);
        buf.AddRange([(byte)DeltaOpKind.Release, .. Enc(new Entity(0, 1))]);
        var delta = FrameDelta.Deserialize(buf.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    [Fact]
    public void Validate_rejects_release_without_reserve()
    {
        // Release(0,1) without Reserve
        var delta = FrameDelta.Deserialize([(byte)DeltaOpKind.Release, 0x00, 0x01]);

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    // ══════════════════════════════════════════════════════════
    //  8. Placeholder entity shape + seq validation
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Validate_rejects_entity_id_less_than_negative_one()
    {
        // Entity(-2, 0): Id=-2 is neither a valid placeholder (must be -1)
        // nor a valid real entity (must be >= 0).
        // Wire: Reserve tag + id=-2 (unsigned LEB128: FE FF FF FF 0F) + ver=0
        var wire = new byte[] {
            (byte)DeltaOpKind.Reserve,
            0xFE, 0xFF, 0xFF, 0xFF, 0x0F,  // id = -2
            0x00                              // version = 0
        };
        var delta = FrameDelta.Deserialize(wire);

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("Invalid entity id", ex.Message);
    }

    [Fact]
    public void Validate_rejects_placeholder_seq_exceeding_max()
    {
        // Entity(-1, MaxPlaceholderSeq + 1) should be rejected.
        var hugeSeq = FrameDelta.MaxPlaceholderSeq + 1;
        var buf = new System.Collections.Generic.List<byte>();
        buf.Add((byte)DeltaOpKind.Reserve);
        buf.AddRange(V(-1));  // placeholder id
        buf.AddRange(V(hugeSeq));
        var delta = FrameDelta.Deserialize(buf.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("exceeds max", ex.Message);
    }

    // ══════════════════════════════════════════════════════════
    //  9. PreScan no longer pre-grows for non-alloc ops
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Destroy_with_huge_entity_id_does_not_oom_in_prescan()
    {
        // Previously, PreScanForCapacity would track ANY entity id and
        // pre-grow _records —so Destroy(Entity(100_000_000, 1)) would
        // allocate a 100M-slot array before the main pass.
        // Now only Reserve and Create ops trigger pre-growth.
        // Wire: Destroy(100_000_000, 1) — id = 100M, version = 1.
        var id = 100_000_000;
        var buf = new System.Collections.Generic.List<byte>();
        buf.Add((byte)DeltaOpKind.Destroy);
        buf.AddRange(V(id));
        buf.AddRange(V(1));
        var delta = FrameDelta.Deserialize(buf.ToArray());

        // Should not OOM —pre-scan skips non-alloc entity ids.
        var world = new World();
        world.Replay(delta);

        // Destroy on non-existent entity is a no-op.
        Assert.Equal(0, world.EntityCount);
    }
}
