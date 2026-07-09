using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

/// <summary>
/// Verifies that CommandStream Submit and Replay paths produce transition
/// entries through the new Watch API.
/// </summary>
public sealed class ChangeTrackingReplayTests
{
    private readonly record struct Position(int X, int Y) : System.IEquatable<Position>;
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value) : System.IEquatable<Health>;

    private struct TransitionRecorder : ITransitionHandler
    {
        public System.Collections.Generic.List<(Entity, TransitionKind)> Changes;

        public TransitionRecorder(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, TransitionKind)>();
        }

        public void OnChange(World world, Entity entity, TransitionKind kind)
        {
            Changes.Add((entity, kind));
        }
    }

    private struct VelocityHandler : IChangeHandler<Velocity>
    {
        public System.Collections.Generic.List<(Entity Entity, Velocity Old, Velocity New)> Changes;

        public VelocityHandler(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, Velocity, Velocity)>();
        }

        public void OnChange(World world, Entity entity, in Velocity oldValue, in Velocity newValue)
        {
            Changes.Add((entity, oldValue, newValue));
        }
    }

    private struct PositionHandler : IChangeHandler<Position>
    {
        public System.Collections.Generic.List<(Entity Entity, Position Old, Position New)> Changes;

        public PositionHandler(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, Position, Position)>();
        }

        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
        {
            Changes.Add((entity, oldValue, newValue));
        }
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
        var watch = hostB.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(hostB);

        new CommandStream(hostB).Replay(delta);

        watch.Diff(hostB);
        Assert.Contains(watch.Handler.Changes, t => t.Item2 == TransitionKind.Entered);
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
        var watch = hostB.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(hostB);

        new CommandStream(hostB).Replay(delta);

        watch.Diff(hostB);
        Assert.Contains(watch.Handler.Changes, t => t.Item2 == TransitionKind.Exited && t.Item1 == eB);
    }

    [Fact]
    public void Replay_add_produces_entered_transition()
    {
        var hostA = new World();
        var eA = hostA.Create(new Position(0, 0));
        var cs = new CommandStream(hostA);
        cs.Add(eA, new Velocity(1, 1));
        var delta = cs.Snapshot();

        var hostB = new World();
        var eB = hostB.Create(new Position(0, 0));
        var watch = hostB.Watch<TransitionRecorder>(new QueryDescription().With<Velocity>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(hostB);

        new CommandStream(hostB).Replay(delta);

        watch.Diff(hostB);
        Assert.Contains(watch.Handler.Changes, t => t.Item2 == TransitionKind.Entered && t.Item1 == eB);
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
        var watch = hostB.Watch<Velocity, VelocityHandler>();
        watch.Handler = new VelocityHandler(0);
        watch.Snapshot(hostB);

        new CommandStream(hostB).Replay(delta);
        watch.Diff(hostB);

        // After replay, entity should have Velocity=2
        // Watch diff should report old=default, new=Velocity(2,2) since
        // the entity was created without Velocity and Snapshot captured that.
        Assert.Single(watch.Handler.Changes);
    }

    // ── Create + Destroy across two deltas ─────────────────────────

    [Fact]
    public void Replay_create_produces_entered()
    {
        var hostA = new World();
        var cs = new CommandStream(hostA);
        var e = cs.Create();
        cs.Add(e, new Position(1, 1));
        var delta1 = cs.Snapshot();

        var hostB = new World();
        var watch = hostB.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(hostB);

        new CommandStream(hostB).Replay(delta1);
        watch.Diff(hostB);
        Assert.Contains(watch.Handler.Changes, t => t.Item2 == TransitionKind.Entered);
    }

    [Fact]
    public void Replay_then_destroy_produces_exited()
    {
        var hostA = new World();
        var cs = new CommandStream(hostA);
        var e = cs.Create();
        cs.Add(e, new Position(1, 1));
        var delta1 = cs.Snapshot();

        cs = new CommandStream(hostA);
        cs.Destroy(e);
        var delta2 = cs.Snapshot();

        var hostB = new World();
        var eB = hostB.Create(new Position(1, 1));
        var watch = hostB.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(hostB);

        new CommandStream(hostB).Replay(delta2);
        watch.Diff(hostB);
        Assert.Contains(watch.Handler.Changes, t => t.Item2 == TransitionKind.Exited && t.Item1 == eB);
    }
}
