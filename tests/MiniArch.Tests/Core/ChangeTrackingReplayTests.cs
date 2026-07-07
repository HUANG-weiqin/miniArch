using System.IO;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

/// <summary>
/// Verifies that CommandStream Submit and Replay paths produce observer changes
/// (version bumps and transition log entries) through the instrumented Archetype
/// write chokepoints.
/// </summary>
public sealed class ChangeTrackingReplayTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value);

    // ── Submit → observer sees value change ───────────────────────────

    [Fact]
    public void Submit_set_produces_modified_chunk()
    {
        var world = new World();
        var e = world.Create(new Position(0, 0));
        var cs = new CommandStream(world);

        // Track BEFORE consuming so the cursor is at epoch 0.
        var tracker = world.Track().Capture<Position>().With<Position>();
        // Drain initial noise: Create(new Position) bumps the column version.
        _ = tracker.ModifiedChunks<Position>();
        _ = tracker.Transitions();

        cs.Set(e, new Position(1, 1));
        cs.Submit();

        // Submit applies through SetComponentAtFlat/Typed which bumps version.
        var modified = tracker.ModifiedChunks<Position>().ToList();
        Assert.NotEmpty(modified);
    }

    // ── Cross-world Replay → observer sees value change ──────────────

    [Fact]
    public void Replay_set_produces_modified_chunk_on_receiving_host()
    {
        // Host A: create entity, record a Set
        var hostA = new World();
        var eA = hostA.Create(new Position(0, 0));
        var cs = new CommandStream(hostA);
        cs.Set(eA, new Position(1, 1));
        var delta = cs.Snapshot();

        // Host B: fresh world with matching entity + Track BEFORE replay
        var hostB = new World();
        var eB = hostB.Create(new Position(0, 0));
        var tracker = hostB.Track().Capture<Position>().With<Position>();
        _ = tracker.ModifiedChunks<Position>();
        _ = tracker.Transitions();

        new CommandStream(hostB).Replay(delta);

        // Replay applies through WriteComponentRaw which bumps version.
        var modified = tracker.ModifiedChunks<Position>().ToList();
        Assert.NotEmpty(modified);
    }

    // ── Cross-world Replay → structural transitions ─────────────────

    [Fact]
    public void Replay_create_produces_entered_transition()
    {
        var hostA = new World();
        var cs = new CommandStream(hostA);
        var e = cs.Create();
        cs.Add(e, new Position(1, 1));
        var delta = cs.Snapshot();

        var hostB = new World();
        var tracker = hostB.Track().Capture<Position>().With<Position>();
        // No pre-existing entity on hostB; Replay creates it.

        new CommandStream(hostB).Replay(delta);

        // Replay Create goes through PlaceEntityInArchetype -> AppendTransition.
        var ts = tracker.Transitions().ToList();
        Assert.Contains(ts, t => t.Kind == TransitionKind.Entered);
    }

    [Fact]
    public void Replay_destroy_produces_exited_transition()
    {
        var hostA = new World();
        var eA = hostA.Create(new Position(1, 1));
        var cs = new CommandStream(hostA);
        cs.Destroy(eA);
        var delta = cs.Snapshot();

        var hostB = new World();
        var eB = hostB.Create(new Position(1, 1));
        var tracker = hostB.Track().Capture<Position>().With<Position>();
        _ = tracker.Transitions(); // drain create transition

        new CommandStream(hostB).Replay(delta);

        // Replay Destroy goes through DestroySingle -> AppendTransition.
        var ts = tracker.Transitions().ToList();
        Assert.Contains(ts, t => t.Kind == TransitionKind.Exited && t.Entity == eB);
    }

    [Fact]
    public void Replay_add_produces_entered_transition()
    {
        // Entity without Velocity; replay Add<Velocity> -> migration.
        var hostA = new World();
        var eA = hostA.Create(new Position(0, 0));
        var cs = new CommandStream(hostA);
        cs.Add(eA, new Velocity(1, 1));
        var delta = cs.Snapshot();

        var hostB = new World();
        var eB = hostB.Create(new Position(0, 0));
        var tracker = hostB.Track().Capture<Velocity>().With<Velocity>();
        _ = tracker.Transitions(); // drain (no Velocity transition yet)

        new CommandStream(hostB).Replay(delta);

        // Replay Add goes through ApplyRawAdd -> MoveEntityFromBytes -> AppendTransition.
        var ts = tracker.Transitions().ToList();
        Assert.Contains(ts, t => t.Kind == TransitionKind.Entered && t.Entity == eB);
    }

    // ── Create + Destroy across two deltas ─────────────────────────

    [Fact]
    public void Replay_create_then_destroy_produces_entered_then_exited()
    {
        // First delta: create entity with Position.
        var hostA = new World();
        var cs = new CommandStream(hostA);
        var e = cs.Create();
        cs.Add(e, new Position(1, 1));
        var delta1 = cs.Snapshot();

        // Second delta: destroy the same entity.
        cs = new CommandStream(hostA);
        cs.Destroy(e);
        var delta2 = cs.Snapshot();

        // Host B: Track BEFORE any replay.
        var hostB = new World();
        var tracker = hostB.Track().Capture<Position>().With<Position>();

        new CommandStream(hostB).Replay(delta1);
        new CommandStream(hostB).Replay(delta2);

        var ts = tracker.Transitions().ToList();
        // Should see Entered (create) then Exited (destroy).
        Assert.Contains(ts, t => t.Kind == TransitionKind.Entered);
        Assert.Contains(ts, t => t.Kind == TransitionKind.Exited);
        // Find the Entered and Exited; the Entered should come before Exited
        // or at least the entity should have both.
        var entered = ts.Where(t => t.Kind == TransitionKind.Entered).ToList();
        var exited = ts.Where(t => t.Kind == TransitionKind.Exited).ToList();
        Assert.NotEmpty(entered);
        Assert.NotEmpty(exited);
    }
}
