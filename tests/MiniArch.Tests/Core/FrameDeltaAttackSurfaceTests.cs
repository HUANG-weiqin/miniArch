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
///   B) <c>new CommandStream(world).Replay(delta)</c> without prior Validate → silently corrupts
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

    private static byte[] Enc(Entity e) => [.. V(e.Id == -1 ? 0 : e.Id + 1), .. V(e.Version)];

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
        var delta = FrameDelta.FromWire([0x03, 0x01, 0x01, 0x00]);

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    [Fact]
    public void Replay_without_validate_accepts_create_without_reserve_and_leaks_ghost()
    {
        var delta = FrameDelta.FromWire([0x03, 0x01, 0x01, 0x00]);
        var world = new World();

        new CommandStream(world).Replay(delta); // no Validate → ghost created

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
        var delta = FrameDelta.FromWire(buf.ToArray());

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
        var delta = FrameDelta.FromWire(buf.ToArray());
        var world = new World();

        new CommandStream(world).Replay(delta); // no Validate → duplicate row leaked

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
        var delta = FrameDelta.FromWire([(byte)DeltaOpKind.Reserve, .. Enc(new Entity(5, 1))]);
        delta.Validate(); // passes —delta structure is fine

        var world = new World();
        world.CreateEmpty();
        var ex = Assert.Throws<InvalidOperationException>(() => new CommandStream(world).Replay(delta));
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
        var e = world.CreateEmpty();
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

        new CommandStream(world).Replay(delta); // no Validate → silent corruption

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
        // Build delta manually so Deserialize does not eagerly reject it;
        // the purpose of this test is to verify Validate catches it.
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange([(byte)DeltaOpKind.Reserve, 0x00, 0x01]);
        buf.AddRange([(byte)DeltaOpKind.Create, 0x00, 0x01]);
        buf.AddRange(V(-1)); // compCount = -1
        var delta = new FrameDelta { _buffer = buf.ToArray(), _length = buf.Count, _opCount = 2 };

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

        var delta = FrameDelta.FromWire(buf.ToArray());

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

        var delta = FrameDelta.FromWire(buf.ToArray());
        var world = new World();

        new CommandStream(world).Replay(delta); // no Validate → last write wins

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
        var delta = FrameDelta.FromWire(buf.ToArray());

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
        var delta = FrameDelta.FromWire(buf.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    [Fact]
    public void Validate_rejects_release_without_reserve()
    {
        // Release(0,1) without Reserve
        var delta = FrameDelta.FromWire([(byte)DeltaOpKind.Release, 0x00, 0x01]);

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
        var delta = FrameDelta.FromWire(wire);

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
        buf.AddRange(V(0));  // placeholder id: bias -1 → 0
        buf.AddRange(V(hugeSeq));
        var delta = FrameDelta.FromWire(buf.ToArray());

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
        var delta = FrameDelta.FromWire(buf.ToArray());

        // Should not OOM —pre-scan skips non-alloc entity ids.
        var world = new World();
        new CommandStream(world).Replay(delta);

        // Destroy on non-existent entity is a no-op.
        Assert.Equal(0, world.EntityCount);
    }

    // ══════════════════════════════════════════════════════════
    //  10. Decoder negative length/size/count rejection
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Decoder_ReadBytes_negative_length_throws()
    {
        var buf = new byte[64];
        var decoder = new FrameDelta.OpDecoder(buf, buf.Length, 0);
        var ex = Assert.Throws<InvalidOperationException>(() => decoder.ReadBytes(-1));
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void Decoder_SkipData_negative_size_throws()
    {
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange(V(1234)); // component type id
        buf.AddRange(V(-1));   // size = -1
        var decoder = new FrameDelta.OpDecoder(buf.ToArray(), buf.Count, 0);
        var ex = Assert.Throws<InvalidOperationException>(() => decoder.SkipData());
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void Decoder_SkipCreatePayload_negative_count_throws()
    {
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange(V(-1)); // compCount = -1
        var decoder = new FrameDelta.OpDecoder(buf.ToArray(), buf.Count, 0);
        var ex = Assert.Throws<InvalidOperationException>(() => decoder.SkipCreatePayload());
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void Decoder_SkipCreatePayload_negative_component_size_throws()
    {
        var buf = new System.Collections.Generic.List<byte>();
        buf.AddRange(V(1));   // compCount = 1
        buf.AddRange(V(42));  // component type
        buf.AddRange(V(-1));  // size = -1
        var decoder = new FrameDelta.OpDecoder(buf.ToArray(), buf.Count, 0);
        var ex = Assert.Throws<InvalidOperationException>(() => decoder.SkipCreatePayload());
        Assert.Contains("negative", ex.Message);
    }

    // ══════════════════════════════════════════════════════════
    //  11. Int-adder overflow — large positive size/length bypasses bounds check
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// When <c>_pos &gt; 0</c> and <c>size = int.MaxValue</c>, the check
    /// <c>_pos + size &gt; _end</c> overflows to negative and passes.
    /// The decoder must reject with <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void Decoder_ReadBytes_overflow_length_throws()
    {
        // _pos = 5, _end = 10 → only 5 bytes remaining
        // length = int.MaxValue → 5 + int.MaxValue overflows in old code
        var buf = new byte[10];
        var decoder = new FrameDelta.OpDecoder(buf, buf.Length, 5);
        Assert.Throws<InvalidOperationException>(() => decoder.ReadBytes(int.MaxValue));
    }

    [Fact]
    public void Decoder_SkipData_overflow_size_throws()
    {
        // Wire: small component type (0x00) + size = int.MaxValue (FF FF FF FF 07)
        // After reading type (+1) and size (+5), _pos = 6, _end = 6.
        // _pos + int.MaxValue overflows → old code bypasses bounds.
        var buf = new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
        var decoder = new FrameDelta.OpDecoder(buf, buf.Length, 0);

        Assert.Throws<InvalidOperationException>(() =>
        {
            decoder.SkipData();
            // If SkipData didn't throw (old code: overflow bypass),
            // attempting the next read will crash with IndexOutOfRangeException.
            decoder.MoveNext();
        });
    }

    [Fact]
    public void Decoder_SkipCreatePayload_overflow_component_size_throws()
    {
        // Wire: compCount=1, type=42, size=int.MaxValue (FF FF FF FF 07)
        // After reading count (+1), type (+1), size (+5): _pos=7, _end=7.
        var buf = new byte[] { 0x01, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
        var decoder = new FrameDelta.OpDecoder(buf, buf.Length, 0);

        Assert.Throws<InvalidOperationException>(() =>
        {
            decoder.SkipCreatePayload();
            decoder.MoveNext();
        });
    }

    /// <summary>
    /// Reproduces the reviewer's exact malformed wire bytes:
    /// Set op (0x06) + Entity(0,0) + component size = int.MaxValue.
    /// Before fix: _pos + size overflows, bypasses bounds check, then
    /// next MoveNext throws IndexOutOfRangeException (uncontrolled).
    /// After fix: must throw <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void Deserialize_rejects_overflow_size_with_invalid_operation()
    {
        // 0x06 = Set, 0x00 = entity id, 0x00 = entity ver,
        // 0x00 = component type, 0xFF,0xFF,0xFF,0xFF,0x07 = size = int.MaxValue
        var wire = new byte[] { 0x06, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };

        var ex = Record.Exception(() => FrameDelta.FromWire(wire));
        Assert.IsType<InvalidOperationException>(ex);
    }
}
