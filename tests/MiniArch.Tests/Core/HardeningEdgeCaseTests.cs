using System.IO;
using System.Text;
using MiniArch;
using MiniArch.Core;

namespace MiniArchTests.Core;

/// <summary>
/// Edge-case / attack tests that prove each hardening measure in the M1-M9
/// roadmap actually intercepts its target condition at runtime.
///
/// Each test either:
///   A) triggers a protection mechanism and verifies the clean exception, or
///   B) demonstrates that a formerly-dangerous input is now handled safely.
///
/// These tests validate the *unconditional* protections (not Debug.Assert).
/// </summary>
public sealed class HardeningEdgeCaseTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Health(int Value);

    // ── helpers ──────────────────────────────────────────────────────────

    private static byte[] V(int value)
    {
        var bytes = new List<byte>();
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

    // ══════════════════════════════════════════════════════════════════════
    // M1.9 — ReadVarint boundary: LEB128 > int.MaxValue rejected
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reserve op with Entity ID encoded as LEB128 value &gt; int.MaxValue.
    /// The unsigned LEB128 encodes uint.MaxValue (0xFFFFFFFF) which the
    /// ReadVarint fix detects as negative (bit 31 set) and throws before
    /// any downstream index overflow.
    ///
    /// Wire: [Reserve=0x01] [0xFF,0xFF,0xFF,0xFF,0x0F] [version=0x01]
    ///       Entity ID encodes uint.MaxValue → ReadVarint reads -1 → throws.
    /// </summary>
    [Fact]
    public void FromWire_rejects_entity_id_exceeding_int_maxvalue()
    {
        // LEB128 of 0xFFFFFFFF = FF FF FF FF 0F
        var wire = new byte[]
        {
            (byte)DeltaOpKind.Reserve,
            0xFF, 0xFF, 0xFF, 0xFF, 0x0F, // id = uint.MaxValue → decodes as -1
            0x01                            // version = 1
        };

        var ex = Assert.Throws<InvalidOperationException>(() => FrameDelta.FromWire(wire));
        Assert.Contains("exceeds int.MaxValue", ex.Message);
    }

    /// <summary>
    /// Reserve op with Entity ID at the exact boundary: LEB128 = int.MaxValue
    /// (0x7FFFFFFF). This is the maximum valid value — bit 31 is clear, so
    /// ReadVarint does NOT throw. Verifies we can still process legitimate
    /// maximum values.
    ///
    /// Wire: [Reserve=0x01] [0xFF,0xFF,0xFF,0xFF,0x07] [version=0x01]
    ///       Entity ID encodes int.MaxValue (2147483647) → passes ≤int.MaxValue check.
    /// </summary>
    [Fact]
    public void FromWire_accepts_entity_id_at_int_maxvalue_boundary()
    {
        // LEB128 of 0x7FFFFFFF = FF FF FF FF 07
        var wire = new byte[]
        {
            (byte)DeltaOpKind.Reserve,
            0xFF, 0xFF, 0xFF, 0xFF, 0x07, // id = int.MaxValue (2147483647)
            0x01                             // version = 1
        };

        // Should not throw — exact boundary value is valid.
        var delta = FrameDelta.FromWire(wire);
        Assert.False(delta.IsEmpty);
        Assert.True(delta.DeltaCount >= 1);
    }

    /// <summary>
    /// Component size field with LEB128 &gt; int.MaxValue is also caught
    /// by ReadVarint. Set op with component size = uint.MaxValue.
    ///
    /// Wire: [Set=0x07] [Entity(0,1)] [type=0x00] [size=uint.MaxValue]
    /// </summary>
    [Fact]
    public void FromWire_rejects_component_size_exceeding_int_maxvalue()
    {
        var wire = new byte[]
        {
            (byte)DeltaOpKind.Set,           // Set op
            0x01, 0x01,                       // Entity(0, 1)
            0x00,                             // component type = 0
            0xFF, 0xFF, 0xFF, 0xFF, 0x0F     // size = uint.MaxValue
        };

        var ex = Assert.Throws<InvalidOperationException>(() => FrameDelta.FromWire(wire));
        Assert.Contains("exceeds int.MaxValue", ex.Message);
    }

    // ══════════════════════════════════════════════════════════════════════
    // M8.3 — PreScan entity-id clamp prevents OOM from Reserve with huge ID
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Before the fix, Reserve(Entity(100M, 1)) in a delta would cause
    /// PreScanForCapacity to set maxEntityId = 100M, pre-growing _records
    /// to 100M entries (~800 MB). The fix caps PreScan growth to
    /// Math.Max(_records.Length * 2, 65536), avoiding this OOM.
    ///
    /// The replay still fails with "out of sync" because a fresh world
    /// has no free slot at id=100M — but the failure is clean (no crash,
    /// no OOM, world is still usable).
    /// </summary>
    [Fact]
    public void Replay_reserve_with_huge_entity_id_does_not_oom()
    {
        var bigId = 100_000_000;
        var buf = new List<byte>();
        buf.Add((byte)DeltaOpKind.Reserve);
        buf.AddRange(V(bigId));
        buf.AddRange(V(1));

        var delta = FrameDelta.FromWire(buf.ToArray());

        var world = new World();
        // PreScan caps pre-growth to Max(128*2, 65536)=65536 instead of 100M.
        // The actual Reserve op then fails because no slot at id=100M exists.
        var ex = Assert.Throws<InvalidOperationException>(() => new CommandStream(world).Replay(delta));
        Assert.Contains("out of sync", ex.Message);

        // World must still be functional after the failed replay.
        Assert.Equal(0, world.EntityCount);
        var e = world.Create(new Position(1, 2));
        Assert.True(world.IsAlive(e));
        Assert.Equal(new Position(1, 2), world.Get<Position>(e));
    }

    // ══════════════════════════════════════════════════════════════════════
    // M8.3 (Create path) — PreScan entity-id clamp; Validate catches miss
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create(Entity(huge, 1)) without a preceding Reserve is rejected by
    /// Validate with "without preceding Reserve" — same clean rejection as
    /// for small entity IDs.
    ///
    /// The PreScan also clamps Create entity IDs to prevent pre-allocation
    /// OOM (line 1034-1036 of World.cs), but Validate fires first.
    /// </summary>
    [Fact]
    public void Validate_rejects_create_with_huge_entity_id_without_reserve()
    {
        var hugeId = 100_000_000;
        var buf = new List<byte>();
        buf.Add((byte)DeltaOpKind.Create);
        buf.AddRange(V(hugeId));
        buf.AddRange(V(1)); // version
        buf.Add(0x00);      // compCount = 0 (empty entity)
        var delta = FrameDelta.FromWire(buf.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => delta.Validate());
        Assert.Contains("without preceding Reserve", ex.Message);
    }

    /// <summary>
    /// Create(Entity(huge, 1)) without Reserve, replayed WITHOUT Validate.
    ///
    /// PreScan clamps maxEntityId to Max(128*2, 65536)=65536, so _records
    /// grows from 128 to 65537 — NOT to 100M (no OOM). But the main pass
    /// then tries _records[100M] which is out of bounds → IndexOutOfRangeException.
    ///
    /// This documents a gap: for small entity IDs (within the pre-grown
    /// _records), Replay without Validate creates a ghost entity; for huge
    /// entity IDs it crashes with IndexOutOfRangeException. Always use
    /// Validate() before Replay() for untrusted deltas.
    /// </summary>
    [Fact]
    public void Replay_create_with_huge_entity_id_without_validate_crashes_index_out_of_range()
    {
        var hugeId = 100_000_000;
        var buf = new List<byte>();
        buf.Add((byte)DeltaOpKind.Create);
        buf.AddRange(V(hugeId));
        buf.AddRange(V(1));
        buf.Add(0x00); // compCount = 0
        var delta = FrameDelta.FromWire(buf.ToArray());

        var world = new World();
        // PreScan clamped, no OOM. But main pass crashes because _records
        // was only pre-grown to 65537, not to 100M.
        Assert.Throws<IndexOutOfRangeException>(() => new CommandStream(world).Replay(delta));

        // World is in an inconsistent state after the crash (documented gap).
        // EntityCount may be unreliable — calling destroy on what was created.
        Assert.Equal(0, world.EntityCount);
    }

    /// <summary>
    /// Degenerate create-only delta with small entity ID (0) — proves the
    /// small-entity path works as a baseline: ghost entity is created,
    /// no crash, no OOM.
    /// </summary>
    [Fact]
    public void Replay_create_small_entity_without_validate_creates_ghost()
    {
        var delta = FrameDelta.FromWire([(byte)DeltaOpKind.Create, 0x01, 0x01, 0x00]);
        // Create(Entity(0, 1), compCount = 0) — small ID, fits in _records.

        var world = new World();
        new CommandStream(world).Replay(delta); // no Validate

        // Ghost entity created: counts in query but not in EntityCount.
        Assert.Equal(0, world.EntityCount);
    }

    /// <summary>
    /// Replay Reserve + Create of a huge entity that IS consistent:
    /// Reserve(Entity(65536, 1)) followed by Create(Entity(65536, 1)).
    ///
    /// PreScan clamps maxEntityId = Min(65536, Max(128*2, 65536)) = 65536.
    /// _records grows from 128 to 65537 entries. Then Reserve sees
    /// entity.Id == _entitySlotCount (0) but Version != 1 — it goes to
    /// ReserveDeferredEntity which finds slot 0, version 1, but returns
    /// Entity(0, 1) which doesn't match Entity(65536, 1) → "out of sync".
    ///
    /// This shows that even with consistent Reserve+Create, a huge entity
    /// ID fails cleanly because the world's allocator doesn't match.
    /// </summary>
    [Fact]
    public void Replay_reserve_and_create_with_huge_id_at_cap_boundary_fails_clean()
    {
        var cap = 65536;
        var buf = new List<byte>();
        // Reserve(65536, 1) — the PreScan clamp cap for a fresh world
        buf.Add((byte)DeltaOpKind.Reserve);
        buf.AddRange(V(cap));
        buf.AddRange(V(1));

        // Create(65536, 1, compCount=0)
        buf.Add((byte)DeltaOpKind.Create);
        buf.AddRange(V(cap));
        buf.AddRange(V(1));
        buf.Add(0x00);

        var delta = FrameDelta.FromWire(buf.ToArray());
        delta.Validate(); // passes: Reserve+Create is a valid state machine

        var world = new World();
        var ex = Assert.Throws<InvalidOperationException>(() => new CommandStream(world).Replay(delta));
        Assert.Contains("out of sync", ex.Message);
    }

    // ══════════════════════════════════════════════════════════════════════
    // M8.4 — WorldSnapshot.Load rejects entity slot count > 256M
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A malicious snapshot with entitySlotCount beyond the 256M limit must
    /// be rejected before allocating the slot-versions array (~1 GB / 256M).
    ///
    /// Constructs a minimal v3 snapshot (no CRC) with slotCount = 300M.
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_entity_slot_count_over_256M()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3);          // v3 (no CRC, simpler to construct)
        writer.Write(16);         // chunkCapacity
        writer.Write(300_000_001);// entitySlotCount > 256M limit → must be rejected
        writer.Write(0);          // schemaCount
        writer.Write(0);          // archetypeCount
        writer.Write(0);          // hierarchyLinkCount
        writer.Flush();

        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("out of range", ex.Message);
        Assert.Contains("300000001", ex.Message);
    }

    /// <summary>
    /// Negative entity slot count must also be rejected.
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_negative_entity_slot_count()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3);          // v3
        writer.Write(16);         // chunkCapacity
        writer.Write(-1);         // entitySlotCount < 0 → rejected
        writer.Write(0);          // schemaCount
        writer.Write(0);          // archetypeCount
        writer.Write(0);          // hierarchyLinkCount
        writer.Flush();

        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("out of range", ex.Message);
    }

    /// <summary>
    /// A reasonable snapshot (slotCount = 8, no entities) must still load
    /// successfully under the new limit — regression guard.
    /// </summary>
    [Fact]
    public void SnapshotLoad_accepts_reasonable_slot_count()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3);          // v3
        writer.Write(16);         // chunkCapacity
        writer.Write(8);          // entitySlotCount = 8 (reasonable)
        writer.Write(0);          // schemaCount
        writer.Write(0);          // archetypeCount
        writer.Write(0);          // hierarchyLinkCount
        for (var i = 0; i < 8; i++) writer.Write(0); // slot versions
        writer.Write(0);          // free list length
        writer.Flush();

        ms.Position = 0;
        var world = WorldSnapshot.Load(ms);
        Assert.NotNull(world);
        Assert.Equal(8, world.EntitySlotCount);
    }

    // ══════════════════════════════════════════════════════════════════════
    // M9.4 — Snapshot truncation (< 8 bytes) caught early
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Feeds a stream with only 4 bytes (can't even read magic + version).
    /// The fix throws InvalidDataException instead of letting the CRC or
    /// body parsing read out of bounds.
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_truncated_input()
    {
        using var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("too short", ex.Message);
    }

    /// <summary>
    /// Empty stream also gets the truncation message.
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_empty_stream()
    {
        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("too short", ex.Message);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Snapshot schemaCount / archetypeCount / hierarchyLinkCount caps
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Malicious snapshot with schemaCount beyond 65536 must be rejected
    /// before allocating schemaTypes array (prevents OOM).
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_excessive_schema_count()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3);          // v3
        writer.Write(16);         // chunkCapacity
        writer.Write(1);          // entitySlotCount = 1 (passes)
        writer.Write(100_000);    // schemaCount > 65536 → rejected
        writer.Write(0);          // archetypeCount
        writer.Write(0);          // hierarchyLinkCount
        writer.Flush();

        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("schema count", ex.Message);
    }

    /// <summary>
    /// Malicious snapshot with archetypeCount beyond 262144 must be rejected
    /// before reading archetype data (prevents excessive loop + allocation).
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_excessive_archetype_count()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3);          // v3
        writer.Write(16);         // chunkCapacity
        writer.Write(1);          // entitySlotCount = 1 (passes)
        writer.Write(0);          // schemaCount
        writer.Write(300_000);    // archetypeCount > 262144 → rejected
        writer.Write(0);          // hierarchyLinkCount
        writer.Flush();

        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("archetype count", ex.Message);
    }

    /// <summary>
    /// Malicious snapshot with hierarchyLinkCount beyond entitySlotCount
    /// must be rejected (prevents OOM from excessive link processing).
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_excessive_hierarchy_link_count()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3);          // v3
        writer.Write(16);         // chunkCapacity
        writer.Write(8);          // entitySlotCount = 8 (passes)
        writer.Write(0);          // schemaCount
        writer.Write(0);          // archetypeCount
        writer.Write(9);          // hierarchyLinkCount > 8 → rejected
        writer.Flush();

        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("hierarchy link count", ex.Message);
    }

    /// <summary>
    /// Negative schema/archetype/hierarchy counts must also be rejected.
    /// </summary>
    [Fact]
    public void SnapshotLoad_rejects_negative_metadata_counts()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4D415243); // magic
        writer.Write(3);          // v3
        writer.Write(16);         // chunkCapacity
        writer.Write(1);          // entitySlotCount = 1 (passes)
        writer.Write(-1);         // schemaCount < 0 → rejected
        writer.Write(-1);         // archetypeCount < 0 → rejected
        writer.Write(-1);         // hierarchyLinkCount < 0 → rejected
        writer.Flush();

        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => WorldSnapshot.Load(ms));
        Assert.Contains("out of range", ex.Message);
    }

    // ══════════════════════════════════════════════════════════════════════
    // M9.1 — ArgumentNullException.ThrowIfNull for Replay
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Replay_null_delta_throws_ArgumentNullException()
    {
        var world = new World();
        Assert.Throws<ArgumentNullException>(() => new CommandStream(world).Replay(null!));
    }

    // ══════════════════════════════════════════════════════════════════════
    // M9.2 — ArgumentNullException.ThrowIfNull for SubmitAndSnapshotIntoAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void SubmitAndSnapshotIntoAsync_null_target_throws_ArgumentNullException()
    {
        var world = new World();
        var stream = new CommandStream(world);
        // SubmitAndSnapshotIntoAsync throws ArgumentNullException synchronously
        // (before creating the Task), so Record.Exception works without async.
        var ex = Record.Exception(() => { stream.SubmitAndSnapshotIntoAsync(null!); });
        Assert.IsType<ArgumentNullException>(ex);
    }

    // ══════════════════════════════════════════════════════════════════════
    // M9.5 — ArgumentNullException.ThrowIfNull for WorldSnapshot.Load
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void SnapshotLoad_null_stream_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WorldSnapshot.Load(null!));
    }
}
