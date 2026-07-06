using MiniArch.Core;

namespace MiniArchTests.Core;

/// <summary>
/// Adversarial / robustness tests proving the ECS library handles malformed
/// input and illegal operations gracefully — no crashes, no state corruption.
///
/// Categories:
///   A) Wire fuzz — FrameDelta.Deserialize with hostile inputs
///   B) Illegal operations — World / CommandStream misuse
/// </summary>
public sealed class RobustnessTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // ════════════════════════════════════════════════════════════
    // Varint / entity-encoding helpers (match existing test pattern)
    // ════════════════════════════════════════════════════════════

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

    /// <summary>
    /// Produces a valid wire for a single Entity(0,1) created with
    /// Position and Health components.
    /// </summary>
    private static byte[] BuildValidWire()
    {
        var world = new World();
        var stream = new CommandStream(world);
        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Add(e, new Health(100));
        var delta = stream.Snapshot();
        stream.Submit();
        return delta.AsSpan().ToArray();
    }

    // ════════════════════════════════════════════════════════════
    // Category A: Wire Fuzz — FrameDelta.Deserialize robustness
    // ════════════════════════════════════════════════════════════

    // ── A1: Truncated wire ──────────────────────────────────────

    /// <summary>
    /// Truncates a valid wire at multiple offsets and verifies that
    /// Deserialize either succeeds (partial valid data) or throws a
    /// clean InvalidOperationException. After each failure the instance
    /// is in a clean (empty) state and can be reused.
    /// </summary>
    [Fact]
    public void Deserialize_truncated_wire_never_crashes_and_cleans_state()
    {
        var validWire = BuildValidWire();
        var delta = new FrameDelta();

        // Prime with valid data first so we can verify reuse later.
        delta.Deserialize(validWire);
        Assert.False(delta.IsEmpty);

        // Truncation points: 0 bytes, 1 byte, first few bytes, mid, near-end.
        var truncationLengths = new List<int> { 0, 1, 2, 3 };
        if (validWire.Length > 4)
            truncationLengths.Add(validWire.Length / 2);
        if (validWire.Length > 5)
            truncationLengths.Add(validWire.Length - 2);

        foreach (var len in truncationLengths)
        {
            if (len == 0)
            {
                // Empty input is a valid degenerate case — produces empty delta.
                delta.Deserialize(ReadOnlySpan<byte>.Empty);
                Assert.True(delta.IsEmpty);
                continue;
            }

            var truncated = validWire.AsSpan(0, Math.Min(len, validWire.Length)).ToArray();

            // Must either throw (truncated mid-structure) or produce a valid delta.
            var ex = Record.Exception(() => delta.Deserialize(truncated));
            if (ex is not null)
            {
                Assert.IsType<InvalidOperationException>(ex);
                // Verify catch block reset the state.
                Assert.True(delta.IsEmpty, $"Truncation length {len}: expected empty after failed Deserialize");
                Assert.Equal(0, delta.DeltaCount);
                Assert.Equal(0, delta.AsSpan().Length);
            }
        }

        // Verify the instance is still reusable with valid data.
        delta.Deserialize(validWire);
        Assert.False(delta.IsEmpty);
        Assert.True(delta.DeltaCount > 0);
    }

    // ── A2: Random garbage ──────────────────────────────────────

    /// <summary>
    /// Feeds 50 different random byte sequences (1-256 bytes) to
    /// Deserialize. Each call either produces an ArgumentException
    /// (oversized input) or an InvalidOperationException (malformed).
    /// No hangs, no crashes. Instance remains reusable.
    /// </summary>
    [Fact]
    public void Deserialize_random_garbage_never_crashes()
    {
        var rng = new Random(54321);
        var delta = new FrameDelta();

        for (var i = 0; i < 50; i++)
        {
            var len = rng.Next(1, 257);
            var garbage = new byte[len];
            rng.NextBytes(garbage);

            var ex = Record.Exception(() => delta.Deserialize(garbage));
            Assert.True(ex is ArgumentException or InvalidOperationException,
                $"Iteration {i} (len={len}): expected ArgumentException or InvalidOperationException, " +
                $"got {(ex?.GetType().Name ?? "null")}");

            // After an InvalidOperationException the catch block resets to empty.
            // After ArgumentException (oversize) the instance keeps prior state.
            if (ex is InvalidOperationException)
            {
                Assert.True(delta.IsEmpty, $"Iteration {i}: expected empty after InvalidOperationException");
            }
        }

        // Verify the instance can still receive valid data.
        var validWire = BuildValidWire();
        delta.Deserialize(validWire);
        Assert.False(delta.IsEmpty);
    }

    // ── A3: Corrupted size / length fields ─────────────────────

    /// <summary>
    /// Corrupts the data-size or component-count varints in a valid wire
    /// by modifying specific bytes that encode sizes. Verifies Deserialize
    /// does not crash and maintains instance integrity.
    /// </summary>
    [Fact]
    public void Deserialize_corrupted_size_fields_does_not_crash()
    {
        // Build a wire whose layout we control: manually encoded
        // Reserve(0,1) + Create(0,1, [Position]) with known byte positions.
        var posType = Component<Position>.ComponentType.Value;
        var buf = new List<byte>();

        // Reserve op (1 byte) + Entity(0,1): id=1 (1 byte) + ver=1 (1 byte)
        buf.Add((byte)DeltaOpKind.Reserve);
        buf.AddRange(new byte[] { 0x01, 0x01 });

        // Create op (1 byte) + Entity(0,1): id=1 (1 byte) + ver=1 (1 byte)
        buf.Add((byte)DeltaOpKind.Create);
        buf.AddRange(new byte[] { 0x01, 0x01 });

        // compCount = 1 (1 byte)
        buf.AddRange(V(1));

        // component type = posType (1 byte)
        buf.AddRange(V(posType));

        // data size = 8 (1 byte) ← corruption target A
        buf.AddRange(V(8));

        // actual position data (8 bytes)
        buf.AddRange(BitConverter.GetBytes(10));
        buf.AddRange(BitConverter.GetBytes(20));

        var originalWire = buf.ToArray();

        // ── Mutation 1: corrupt the data-size varint to huge value ──
        // The size field is at a known position: after Reserve(3), Create(1), 
        // compCount(1), typeId(n), so offset is 3 + 1 + 1 + V(posType).Length + 1 + 1
        // Actually let's just find it by scanning for 0x08 (value 8).
        for (var offset = 0; offset < originalWire.Length; offset++)
        {
            if (originalWire[offset] == 0x08 && offset >= 4)
            {
                var corrupted = (byte[])originalWire.Clone();
                // Make the size varint consume many bytes by setting continuation bit.
                // This causes SkipCreatePayload to read well past the buffer end.
                corrupted[offset] = 0xFF;
                corrupted[offset + 1] = 0xFF;

                var delta = new FrameDelta();
                var ex = Record.Exception(() => delta.Deserialize(corrupted));
                Assert.True(ex is null or InvalidOperationException,
                    $"Size corruption at offset {offset}: expected null or InvalidOperationException, " +
                    $"got {ex?.GetType().Name}");
                if (ex is InvalidOperationException)
                {
                    Assert.True(delta.IsEmpty);
                }
                break;
            }
        }

        // ── Mutation 2: corrupt compCount to huge value ──
        // The compCount byte comes after Entity(0,1) in Create.
        // Structure: Reserve(3) + Create(1) + id(1) + ver(1) = offset 6
        var compCountOffset = 6;
        if (compCountOffset < originalWire.Length)
        {
            var corrupted = (byte[])originalWire.Clone();
            corrupted[compCountOffset] = 0xFF;

            var delta = new FrameDelta();
            var ex = Record.Exception(() => delta.Deserialize(corrupted));
            Assert.True(ex is null or InvalidOperationException,
                $"compCount corruption: expected null or InvalidOperationException, " +
                $"got {ex?.GetType().Name}");
            if (ex is InvalidOperationException)
            {
                Assert.True(delta.IsEmpty);
            }
        }

        // ── Mutation 3: flip all bits in a data byte ──
        // This does not affect varint structure (data bytes are opaque to Deserialize).
        var dataOffset = originalWire.Length - 4;
        if (dataOffset >= 0 && dataOffset < originalWire.Length)
        {
            var corrupted = (byte[])originalWire.Clone();
            corrupted[dataOffset] ^= 0xFF;

            var delta = new FrameDelta();
            var ex = Record.Exception(() => delta.Deserialize(corrupted));
            Assert.Null(ex); // Data corruption alone should not cause Deserialize to fail.
        }
    }

    // ── A4: Corrupted type IDs ────────────────────────────────

    /// <summary>
    /// Mutates component-type-id bytes in a valid wire to values outside
    /// the known ComponentRegistry range. Deserialize does not validate
    /// type IDs — it just skips the data — so the call should succeed
    /// (or throw only if the mutation breaks varint continuity).
    /// No crash, no corrupt instance state.
    /// </summary>
    [Fact]
    public void Deserialize_corrupted_type_ids_does_not_crash()
    {
        var validWire = BuildValidWire();

        // Test 3 different mutation strategies.
        var mutations = new Action<byte[]>[]
        {
            bytes =>
            {
                // Find and flip a component-type-id varint byte.
                // Type IDs are small contiguous values (0, 1, 2...).
                // Look for a byte in the payload area that looks like a type ID.
                for (var i = 6; i < bytes.Length && i < 20; i++)
                {
                    if (bytes[i] >= 0x00 && bytes[i] <= 0x03 && bytes[i - 1] >= 0x01 && bytes[i - 1] <= 0x09)
                    {
                        bytes[i] = 0xFE; // Very large type ID
                        break;
                    }
                }
            },
            bytes =>
            {
                // Set a specific byte mid-wire to a large value.
                if (bytes.Length > 10)
                    bytes[bytes.Length / 2] = 0xAA;
            },
            bytes =>
            {
                // Zero out a byte right after the first op tag (likely type ID or size).
                if (bytes.Length > 5)
                    bytes[5] = 0x00;
            },
        };

        foreach (var mutate in mutations)
        {
            var corrupted = (byte[])validWire.Clone();
            mutate(corrupted);

            var delta = new FrameDelta();
            var ex = Record.Exception(() => delta.Deserialize(corrupted));
            // Deserialize may succeed (type IDs are not validated) or throw
            // if the mutation broke varint structure / exceeded buffer bounds.
            if (ex is not null)
            {
                Assert.True(ex is InvalidOperationException,
                    $"Type-ID corruption: expected InvalidOperationException, got {ex.GetType().Name}: {ex.Message}");
                Assert.True(delta.IsEmpty, "Instance must be empty after failed Deserialize");
            }
        }
    }

    // ── A5: Empty input ─────────────────────────────────────────

    [Fact]
    public void Deserialize_empty_wire_produces_empty_delta()
    {
        var delta = new FrameDelta();
        delta.Deserialize(ReadOnlySpan<byte>.Empty);

        Assert.True(delta.IsEmpty);
        Assert.Equal(0, delta.DeltaCount);
        Assert.Equal(0, delta.AsSpan().Length);
    }

    // ── A6: Reuse after failure ────────────────────────────────

    [Fact]
    public void Deserialize_reuse_after_malformed_failures()
    {
        var validWire = BuildValidWire();
        var delta = new FrameDelta();

        // Cycle through valid → malformed → valid → malformed → valid.
        var malformedInputs = new byte[][]
        {
            new byte[] { 0xFF, 0xFF, 0xFF },                     // unknown op kind
            new byte[] { 0x01, 0x80 },                            // truncated varint
            new byte[] { 0x06, 0x80, 0x80, 0x80, 0x80, 0x80 },  // malformed oversized varint
        };

        for (var cycle = 0; cycle < 3; cycle++)
        {
            // Should succeed.
            delta.Deserialize(validWire);
            Assert.False(delta.IsEmpty);
            var countBefore = delta.DeltaCount;

            // Should fail cleanly and reset state.
            foreach (var malformed in malformedInputs)
            {
                Assert.Throws<InvalidOperationException>(() => delta.Deserialize(malformed));
                Assert.True(delta.IsEmpty);
                Assert.Equal(0, delta.DeltaCount);
            }

            // Verify that the delta after all malformed attempts still has
            // correct valid data.
            delta.Deserialize(validWire);
            Assert.False(delta.IsEmpty);
            Assert.Equal(countBefore, delta.DeltaCount);
        }
    }

    // ════════════════════════════════════════════════════════════
    // Category B: Illegal operations on World / CommandStream
    // ════════════════════════════════════════════════════════════

    // ── B1: Destroy already-destroyed entity ──────────────────

    [Fact]
    public void Destroy_already_destroyed_entity_throws_and_entity_count_stable()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));

        var countBefore = world.EntityCount;

        world.Destroy(e);
        Assert.Equal(countBefore - 1, world.EntityCount);

        // Second destroy on same handle must throw (stale entity).
        var ex = Assert.Throws<InvalidOperationException>(() => world.Destroy(e));
        Assert.Contains("no longer alive", ex.Message);

        // EntityCount must be unchanged after the failed destroy.
        Assert.Equal(countBefore - 1, world.EntityCount);
    }

    // ── B2: Operations on dead entity ─────────────────────────

    [Fact]
    public void Add_on_dead_entity_throws()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        Assert.Throws<InvalidOperationException>(() => world.Add(e, new Velocity(3, 4)));
    }

    [Fact]
    public void Set_on_dead_entity_throws()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        Assert.Throws<InvalidOperationException>(() => world.Set(e, new Position(99, 99)));
    }

    [Fact]
    public void Remove_on_dead_entity_throws()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        Assert.Throws<InvalidOperationException>(() => world.Remove<Position>(e));
    }

    [Fact]
    public void AddChild_on_dead_entity_throws()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.Destroy(child);

        Assert.Throws<InvalidOperationException>(() => world.AddChild(parent, child));
        Assert.Throws<InvalidOperationException>(() => world.AddChild(child, parent));
    }

    [Fact]
    public void Has_on_dead_entity_returns_false()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        Assert.False(world.Has<Position>(e));
        Assert.False(world.Has<Velocity>(e));
    }

    [Fact]
    public void TryGet_on_dead_entity_returns_false()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        Assert.False(world.TryGet(e, out Position _));
        Assert.False(world.TryGet<Velocity>(e, out _));
    }

    // ── B3: Get<T> on entity without that component ──────────

    [Fact]
    public void Get_component_not_present_throws()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2)); // No Velocity or Health

        // Get<T> assumes the component exists; when it doesn't,
        // the column map returns -1 and the getter throws.
        Assert.Throws<IndexOutOfRangeException>(() => world.Get<Velocity>(e));
    }

    [Fact]
    public void TryGet_component_not_present_returns_false()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));

        Assert.False(world.TryGet<Velocity>(e, out _));
        Assert.False(world.TryGet<Health>(e, out _));
    }

    // ── B4: AddChild reparenting ──────────────────────────────

    [Fact]
    public void AddChild_reparents_entity_correctly()
    {
        var world = new World();
        var a = world.Create();
        var b = world.Create();
        var c = world.Create();

        // Attach B to A.
        world.AddChild(a, b);
        Assert.True(world.TryGetParent(b, out var parent1));
        Assert.Equal(a, parent1);

        // Re-parent B from A to C.
        world.AddChild(c, b);
        Assert.True(world.TryGetParent(b, out var parent2));
        Assert.Equal(c, parent2);

        // A should no longer have children.
        Assert.False(world.HasChildren(a));

        // No duplicate or cycle — all entities still alive.
        Assert.True(world.IsAlive(a));
        Assert.True(world.IsAlive(b));
        Assert.True(world.IsAlive(c));
    }

    // ── B5: Stale entity version detection ───────────────────

    [Fact]
    public void Stale_entity_handle_rejected_by_all_mutators()
    {
        var world = new World();

        // Create E (v1), destroy it, then create a new entity that
        // recycles the same slot (v2).
        var original = world.Create(new Position(1, 2));
        var originalVersion = original.Version;

        world.Destroy(original);
        var recycled = world.Create(new Position(3, 4));

        Assert.Equal(original.Id, recycled.Id);
        Assert.NotEqual(original.Version, recycled.Version);

        // The stale (v1) handle must be rejected by all mutation APIs.
        Assert.Throws<InvalidOperationException>(() => world.Set(original, new Position(99, 99)));
        Assert.Throws<InvalidOperationException>(() => world.Add(original, new Velocity(1, 2)));
        Assert.Throws<InvalidOperationException>(() => world.Remove<Position>(original));
        Assert.Throws<InvalidOperationException>(() => world.Destroy(original));

        // TryGet and Has must return false for the stale handle.
        Assert.False(world.TryGet(original, out Position _));
        Assert.False(world.Has<Position>(original));

        // The recycled entity's data must be intact.
        Assert.True(world.TryGet(recycled, out Position p));
        Assert.Equal(new Position(3, 4), p);
    }

    // ── B6: Submit on empty stream ───────────────────────────

    [Fact]
    public void Submit_empty_stream_is_noop()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var entityCountBefore = world.EntityCount;

        // No ops recorded — Submit should return false.
        var result = stream.Submit();

        Assert.False(result);
        Assert.Equal(entityCountBefore, world.EntityCount);
    }

    // ── B7: Replay empty FrameDelta ──────────────────────────

    [Fact]
    public void Replay_empty_delta_is_noop()
    {
        var world = new World();
        var original = world.Create(new Position(1, 2));

        var entityCountBefore = world.EntityCount;

        var emptyDelta = new FrameDelta();
        Assert.True(emptyDelta.IsEmpty);

        new CommandStream(world).Replay(emptyDelta);

        // World state must be unchanged.
        Assert.Equal(entityCountBefore, world.EntityCount);
        Assert.True(world.IsAlive(original));
        Assert.True(world.TryGet(original, out Position p));
        Assert.Equal(new Position(1, 2), p);
    }
}
