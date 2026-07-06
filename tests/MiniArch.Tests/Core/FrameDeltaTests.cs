using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;

namespace MiniArchTests.Core;

public sealed class FrameDeltaTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // ══════════════════════════════════════════════════════════—
    // Zero-allocation reusable Deserialize
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Deserialize_reuses_existing_buffer_when_capacity_is_sufficient()
    {
        // Build a small valid delta via CommandStream.
        var world = new World();
        var stream = new CommandStream(world);
        var e = stream.Create();
        stream.Add(e, new Position(100, 200));
        stream.Add(e, new Velocity(1, 2));
        var delta = stream.Snapshot();
        stream.Submit();

        var wire = delta.AsSpan().ToArray();

        // Create reusable instance and warm up (first call allocates buffer).
        var reusable = new FrameDelta();
        reusable.Deserialize(wire.AsSpan());

        // Verify warm-up worked correctly.
        Assert.Equal(delta.DeltaCount, reusable.DeltaCount);

        // Measure allocation for second Deserialize.
        var before = GC.GetAllocatedBytesForCurrentThread();
        reusable.Deserialize(wire.AsSpan());
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);

        // Verify replay behavior matches FromWire.
        var fromWire = FrameDelta.FromWire(wire.AsSpan());
        Assert.Equal(fromWire.DeltaCount, reusable.DeltaCount);

        // Both should replay identically.
        var targetA = new World();
        var targetB = new World();
        new CommandStream(targetA).Replay(fromWire);
        new CommandStream(targetB).Replay(reusable);
        var ha = HashWorld(targetA);
        var hb = HashWorld(targetB);
        Assert.Equal(ha, hb);
    }

    // ══════════════════════════════════════════════════════════—
    // FromWire returns independent owning delta
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void FromWire_returns_independent_delta()
    {
        // Build a valid delta.
        var world = new World();
        var stream = new CommandStream(world);
        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Add(e, new Velocity(3, 4));
        stream.Add(e, new Health(5));
        var original = stream.Snapshot();
        stream.Submit();

        // Copy wire to a mutable byte array.
        var wire = original.AsSpan().ToArray();
        var wireCopy = wire.AsSpan().ToArray(); // preserve expected content

        // Create delta via FromWire.
        var delta = FrameDelta.FromWire(wire.AsSpan());

        // Mutate the source byte array.
        for (var i = 0; i < wire.Length; i++)
            wire[i] = 0xFF;

        // Verify delta still has original content.
        var actual = delta.AsSpan().ToArray();
        Assert.Equal(wireCopy, actual);
    }

    // ══════════════════════════════════════════════════════════—
    // Snapshot create batch emission allocation threshold
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Snapshot_create_batch_emission_is_bounded_alloc()
    {
        // Use a persistent World and CommandStream.
        var world = new World();
        var stream = new CommandStream(world);

        // Warm up: create-commit cycles to size pools and FrameDelta capacity.
        for (var warmIdx = 0; warmIdx < 5; warmIdx++)
        {
            var warmE = stream.Create();
            stream.Add(warmE, new Position(1, 2));
            stream.Add(warmE, new Velocity(3, 4));
            stream.Add(warmE, new Health(100));
            stream.Snapshot();
            stream.Submit();
            // Destroy to keep world small but warm up paths.
            stream.Destroy(warmE);
            stream.Snapshot();
            stream.Submit();
        }

        // Create a batch of entities with varied archetypes (triggers EmitCreateFromBatch).
        const int createCount = 64;
        var created = new Entity[createCount];
        for (var i = 0; i < createCount; i++)
        {
            created[i] = stream.Create();
            stream.Add(created[i], new Position(i, i + 1));
            switch (i & 3)
            {
                case 1: stream.Add(created[i], new Velocity(i, i)); break;
                case 2: stream.Add(created[i], new Health(i)); break;
                case 3:
                    stream.Add(created[i], new Velocity(i, i));
                    stream.Add(created[i], new Health(i));
                    break;
            }
        }

        // Measure Snapshot allocation on create-heavy frame.
        // After warmup, pool rentals and FrameDelta buffer reuse should
        // keep allocation well below the naive-per-create cost.
        // A tight threshold of a few KB accounts for any unavoidable
        // framework allocations (e.g., xUnit test infrastructure).
        var before = GC.GetAllocatedBytesForCurrentThread();
        var snap = stream.Snapshot();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The snapshot delta itself contains data -- we expect minimal
        // per-frame GC allocations. Threshold: 8 KB.
        // Each RawComponentValue being rented from pool (not individually
        // allocated) means only the delta's own internal buffer growth
        // should show up here.
        Assert.True(allocated < 8192,
            $"Snapshot create batch emission allocated {allocated} bytes (expected < 8192). " +
            "If this is a false positive due to test scaffolding, adjust threshold.");
        _ = snap; // keep reference alive
    }

    // ══════════════════════════════════════════════════════════—
    // Deserialize exception safety — malformed wire leaves instance clean
    // ══════════════════════════════════════════════════════════—

    [Fact]
    public void Deserialize_malformed_wire_leaves_instance_in_clean_state()
    {
        // Build a reusable instance with valid data first.
        var world = new World();
        var stream = new CommandStream(world);
        var e = stream.Create();
        stream.Add(e, new Position(100, 200));
        var validDelta = stream.Snapshot();
        stream.Submit();
        var validWire = validDelta.AsSpan().ToArray();

        var reusable = new FrameDelta();
        reusable.Deserialize(validWire); // prime the buffer

        // Now feed malformed wire — truncated varint.
        Assert.Throws<InvalidOperationException>(() =>
            reusable.Deserialize(new byte[] { 0x01, 0x80 }));

        Assert.True(reusable.IsEmpty);
        Assert.Equal(0, reusable.DeltaCount);
        Assert.Equal(0, reusable.AsSpan().Length);

        // Also verify the instance can be reused after the failure.
        Assert.Throws<InvalidOperationException>(() =>
            reusable.Deserialize(new byte[] { 0xFF, 0x01, 0x01 }));
        Assert.True(reusable.IsEmpty);
        Assert.Equal(0, reusable.DeltaCount);
        Assert.Equal(0, reusable.AsSpan().Length);

        // Reuse with valid data should still work.
        reusable.Deserialize(validWire);
        Assert.False(reusable.IsEmpty);
        Assert.Equal(validDelta.DeltaCount, reusable.DeltaCount);
    }

    [Fact]
    public void Deserialize_produces_owning_copy()
    {
        // Build a valid delta.
        var world = new World();
        var stream = new CommandStream(world);
        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Add(e, new Velocity(3, 4));
        stream.Add(e, new Health(5));
        var original = stream.Snapshot();
        stream.Submit();

        // Copy wire to a mutable byte array.
        var wire = original.AsSpan().ToArray();
        var wireCopy = wire.AsSpan().ToArray();

        // Deserialize using the instance method.
        var delta = new FrameDelta();
        delta.Deserialize(wire.AsSpan());

        // Mutate the source byte array.
        for (var i = 0; i < wire.Length; i++)
            wire[i] = 0xFF;

        // Verify delta still has original content.
        var actual = delta.AsSpan().ToArray();
        Assert.Equal(wireCopy, actual);
    }

    // ══════════════════════════════════════════════════════════—
    // Helpers
    // ══════════════════════════════════════════════════════════—

    private static string HashWorld(World w)
    {
        using var ms = new System.IO.MemoryStream();
        WorldSnapshot.Save(ms, w);
        var span = new System.ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        return System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(span));
    }
}
