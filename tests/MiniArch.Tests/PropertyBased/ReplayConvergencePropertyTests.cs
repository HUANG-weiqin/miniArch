using FsCheck;
using FsCheck.Xunit;
using MiniArch;
using MiniArch.Core;

namespace MiniArchTests.PropertyBased;

// ───────────────────────────────────────────────────────────────────────────
// Property-based tests for replay convergence and snapshot round-trip under
// richer models than the narrow Position/Velocity/Health-only generator in
// SerializationRoundtripPropertyTests. These exercise:
//   * placeholder — local id mapping (lockstep core)
//   * varied component mixes replayed identically on independent worlds
//   * destroy-within-frame (free-list / recycling) preserved across snapshot
//   * same-frame cancellation of pending creates without malformed deltas
//
// Out of scope here: component ids crossing the 512-bit mask boundary. The
// global ComponentRegistry assigns ids sequentially from Type registration
// order and exposes no test hook to force a high id, so crossing 512 would
// require 512 source-defined struct types. That case stays covered by the
// existing hand-written archetype/edge tests instead of a property.
// ───────────────────────────────────────────────────────────────────────────

public sealed class ReplayConvergencePropertyTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value);

    /// <summary>
    /// Headline lockstep invariant: two independent empty worlds that replay
    /// the SAME sequence of placeholder deltas (DeferredEntities=true, the
    /// P2P lockstep mode) must converge to byte-identical canonical state
    /// after every frame — even when each frame creates entities with varied
    /// component mixes and cancels some pending creates. Each replica assigns
    /// its own local ids, but CanonicalChecksum is layout-independent so
    /// identical logical state hashes equal.
    /// </summary>
    /// <remarks>
    /// Same-frame placeholder <c>Destroy</c> cancels pending creates; repeated
    /// generated indices are deduplicated while component additions vary.
    /// </remarks>
    [Property(MaxTest = 150, QuietOnSuccess = true)]
    public bool Two_worlds_replaying_same_placeholder_deltas_converge_each_frame(
        FrameSpec[] frames)
    {
        // Scratch world hosts the CommandStream but is never submitted, so it
        // stays empty across frames (relay-only recording, exactly the demo's
        // P2P topology).
        using var scratch = new World();
        var recorder = new CommandStream(scratch) { DeferredEntities = true };

        var replica1 = new World();
        var replica2 = new World();

        try
        {
            foreach (var frame in frames)
            {
                var delta = RecordFrame(recorder, frame);

                // Snapshot without submitting — relay mode. The delta carries
                // placeholder ids; each replica resolves them to its own
                // local ids during Replay.
                recorder.Clear();

                new CommandStream(replica1).Replay(delta);
                new CommandStream(replica2).Replay(delta);

                // Cumulative check after every frame: any divergence must be
                // caught at the earliest frame it appears, not just at the end.
                if (!replica1.CanonicalChecksum().SequenceEqual(replica2.CanonicalChecksum()))
                    return false;
            }
            return true;
        }
        finally
        {
            replica1.Dispose();
            replica2.Dispose();
        }
    }

    /// <summary>
    /// Snapshot round-trip preserves canonical state for worlds with varied
    /// component mixes and mid-build destroys (which exercise free-list
    /// serialization).
    /// </summary>
    [Property(MaxTest = 200, QuietOnSuccess = true)]
    public bool Snapshot_roundtrip_preserves_flat_entities_and_recycling(RichWorldSpec spec)
    {
        var world = BuildWorld(spec);

        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, world);
        ms.Position = 0;
        var loaded = WorldSnapshot.Load(ms);

        var before = world.CanonicalChecksum();
        var after = loaded.CanonicalChecksum();
        return before.SequenceEqual(after);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static FrameDelta RecordFrame(CommandStream recorder, FrameSpec frame)
    {
        var created = new List<Entity>(frame.Creates.Length);
        foreach (var c in frame.Creates)
        {
            var p = recorder.Create();
            if ((c.CompMask & 1) != 0) recorder.Add(p, new Position(c.X, c.Y));
            if ((c.CompMask & 2) != 0) recorder.Add(p, new Velocity(c.Dx, c.Dy));
            if ((c.CompMask & 4) != 0) recorder.Add(p, new Health(c.Hp));
            created.Add(p);
        }

        if (created.Count > 0)
        {
            // Each generated index is destroyed at most once; this cancels
            // pending creates in the same frame without changing other entities.
            var destroyed = new HashSet<int>();
            foreach (var d in frame.DestroyIdx)
            {
                var di = NormalizeIndex(d, created.Count);
                if (!destroyed.Add(di))
                    continue;
                recorder.Destroy(created[di]);
            }
        }

        return recorder.Snapshot();
    }

    private static World BuildWorld(RichWorldSpec spec)
    {
        var world = new World();
        var created = new List<Entity>(spec.Entities.Length);
        foreach (var e in spec.Entities)
        {
            var entity = world.CreateEmpty();
            if ((e.CompMask & 1) != 0) world.Add(entity, new Position(e.X, e.Y));
            if ((e.CompMask & 2) != 0) world.Add(entity, new Velocity(e.Dx, e.Dy));
            if ((e.CompMask & 4) != 0) world.Add(entity, new Health(e.Hp));
            created.Add(entity);
        }

        if (created.Count > 0)
        {
            // Destroy generated handles while preserving other entities.
            foreach (var d in spec.DestroyIdx)
            {
                var di = NormalizeIndex(d, created.Count);
                if (world.IsAlive(created[di]))
                    world.Destroy(created[di]);
            }
        }

        return world;
    }

    // Euclidean-modulo an arbitrary FsCheck int into [0, count) so generated
    // indices always land on a valid entity. Negative inputs are common from
    // FsCheck and must not throw.
    private static int NormalizeIndex(int raw, int count)
    {
        var m = raw % count;
        return m < 0 ? m + count : m;
    }
}

// FsCheck auto-generates these records. Field names stay short to keep
// counterexamples readable when shrink fires.

public sealed record FrameSpec(CreateSpec[] Creates, int[] DestroyIdx);
public sealed record CreateSpec(int CompMask, int X, int Y, int Dx, int Dy, int Hp);
public sealed record RichWorldSpec(CreateSpec[] Entities, int[] DestroyIdx);
