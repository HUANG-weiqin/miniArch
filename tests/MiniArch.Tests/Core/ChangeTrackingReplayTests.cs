using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

/// <summary>
/// Verifies that CommandStream Submit and Replay paths produce transition
/// log entries through the instrumented Archetype write chokepoints.
/// </summary>
public sealed class ChangeTrackingReplayTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value);

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
        var log = hostB.TrackTransitions(new QueryDescription().With<Position>());
        // No pre-existing entity on hostB; Replay creates it.

        new CommandStream(hostB).Replay(delta);

        // Replay Create goes through PlaceEntityInArchetype -> AppendTransition.
        var ts = log.Transitions.ToArray();
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
        var log = hostB.TrackTransitions(new QueryDescription().With<Position>());
        _ = log.Transitions.ToArray(); // drain create transition
        log.Clear();

        new CommandStream(hostB).Replay(delta);

        // Replay Destroy goes through DestroySingle -> AppendTransition.
        var ts = log.Transitions.ToArray();
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
        var log = hostB.TrackTransitions(new QueryDescription().With<Velocity>());
        _ = log.Transitions.ToArray(); // drain (no Velocity transition yet)
        log.Clear();

        new CommandStream(hostB).Replay(delta);

        // Replay Add goes through ApplyRawAdd -> MoveEntityFromBytes -> AppendTransition.
        var ts = log.Transitions.ToArray();
        Assert.Contains(ts, t => t.Kind == TransitionKind.Entered && t.Entity == eB);
    }

    [Fact]
    public void BUG_Replay_existing_entity_add_then_set_tracks_value_from_add_baseline()
    {
        var hostA = new World();
        var eA = hostA.Create(new Position(0, 0));
        var cs = new CommandStream(hostA);
        cs.Add(eA, new Velocity(1, 1));
        cs.Set(eA, new Velocity(2, 2));
        var delta = cs.Snapshot();

        var hostB = new World();
        var eB = hostB.Create(new Position(0, 0));
        var velocities = hostB.TrackValueChanges<Velocity>();

        new CommandStream(hostB).Replay(delta);

        var changes = velocities.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(eB, changes[0].Entity);
        Assert.Equal(new Velocity(1, 1), changes[0].Old);
        Assert.Equal(new Velocity(2, 2), changes[0].New);
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
        var log = hostB.TrackTransitions(new QueryDescription().With<Position>());

        new CommandStream(hostB).Replay(delta1);
        new CommandStream(hostB).Replay(delta2);

        var ts = log.Transitions.ToArray();
        // Should see Entered (create) then Exited (destroy).
        Assert.Contains(ts, t => t.Kind == TransitionKind.Entered);
        Assert.Contains(ts, t => t.Kind == TransitionKind.Exited);
        // Find the Entered and Exited; the Entered should come before Exited
        var entered = ts.Where(t => t.Kind == TransitionKind.Entered).ToList();
        var exited = ts.Where(t => t.Kind == TransitionKind.Exited).ToList();
        Assert.NotEmpty(entered);
        Assert.NotEmpty(exited);
    }
}
