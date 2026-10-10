using System.IO;
using System.Security.Cryptography;
using MiniArch;
using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;
using Xunit;

namespace MiniArchTests.Core;

/// <summary>
/// Cross-feature parity test matrix (M3). Each test covers one combination
/// of two or more subsystems to verify they interact correctly.
/// All tests use fixed seeds / deterministic inputs.
/// </summary>
public sealed class CrossFeatureParityTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value);

    // ── Handler types for Watch API tests ──────────────────────────────

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

    private struct PositionHandler : IChangeHandler<Position>
    {
        public System.Collections.Generic.List<(Entity, Position, Position)> Changes;

        public PositionHandler(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, Position, Position)>();
        }

        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
        {
            Changes.Add((entity, oldValue, newValue));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 1: CommandStream + flat lifecycle
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_CommandStream_FlatCreateSubmitThenReplay()
    {
        // Create independent entities via CommandStream, then replay into a shadow world.
        var source = new World();
        var stream = new CommandStream(source);

        var parent = stream.Create();
        stream.Add(parent, new Position(1, 2));
        var child = stream.Create();
        stream.Add(child, new Velocity(3, 4));
        var delta1 = stream.Snapshot();
        stream.Submit();

        // Source post-Submit assertions
        Assert.True(source.TryGet(parent, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(source.TryGet(child, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);

        // Replay into shadow world
        var shadow = new World();
        new CommandStream(shadow).Replay(delta1);

        Assert.True(shadow.TryGet(parent, out Position sp));
        Assert.Equal(new Position(1, 2), sp);
        Assert.True(shadow.TryGet(child, out Velocity sv));
        Assert.Equal(new Velocity(3, 4), sv);

        // Canonical checksums must match
        Assert.Equal(source.CanonicalChecksum(), shadow.CanonicalChecksum());
    }

    [Fact]
    public void X_CommandStream_DestroyOneAcrossBatchBoundaries()
    {
        // Create three independent entities, then destroy only the specified one.
        var source = new World();
        var stream1 = new CommandStream(source);

        var parent = stream1.Create();
        stream1.Add(parent, new Position(0, 0));
        var child1 = stream1.Create();
        stream1.Add(child1, new Position(10, 10));
        var child2 = stream1.Create();
        stream1.Add(child2, new Position(20, 20));
        var delta1 = stream1.Snapshot();
        stream1.Submit();

        Assert.True(source.IsAlive(parent));
        Assert.True(source.IsAlive(child1));
        Assert.True(source.IsAlive(child2));

        // Frame 2: destroy parent via a new stream
        var stream2 = new CommandStream(source);
        stream2.Destroy(parent);
        var delta2 = stream2.Snapshot();
        stream2.Submit();

        Assert.False(source.IsAlive(parent));
        Assert.True(source.IsAlive(child1));
        Assert.True(source.IsAlive(child2));

        // Verify Replay of the same deltas produces identical state
        var shadow = new World();
        new CommandStream(shadow).Replay(delta1);
        new CommandStream(shadow).Replay(delta2);

        Assert.False(shadow.IsAlive(parent));
        Assert.True(shadow.IsAlive(child1));
        Assert.True(shadow.IsAlive(child2));
        Assert.Equal(source.CanonicalChecksum(), shadow.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 2: CommandStream + DeferredEntities
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_CommandStream_Deferred_TrackThenSubmitResolvesPlaceholders()
    {
        // Use Track() to get auto-updating entity slots.
        var world = new World();
        var stream = new CommandStream(world) { DeferredEntities = true };

        var slotA = stream.Track(stream.Create());
        stream.Add(slotA.Value, new Position(1, 2));
        var slotB = stream.Track(stream.Create());
        stream.Add(slotB.Value, new Velocity(3, 4));
        stream.Add(slotB.Value, new Health(100));

        Assert.True(slotA.Value.IsPlaceholder);
        Assert.True(slotB.Value.IsPlaceholder);

        stream.Submit();

        // After Submit, slots resolve to real entities
        Assert.False(slotA.Value.IsPlaceholder);
        Assert.False(slotB.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slotA.Value));
        Assert.True(world.IsAlive(slotB.Value));
        Assert.True(world.TryGet(slotA.Value, out Position sp));
        Assert.Equal(new Position(1, 2), sp);
        Assert.True(world.TryGet(slotB.Value, out Velocity sv));
        Assert.Equal(new Velocity(3, 4), sv);
        Assert.True(world.TryGet(slotB.Value, out Health sh));
        Assert.Equal(new Health(100), sh);
    }

    [Fact]
    public void X_CommandStream_Deferred_SubmitSnapshotReplayRoundtrip()
    {
        // Deferred mode: Track + Submit creates real entities.
        // Snapshot creates placeholder delta for cross-world Replay.
        // Verify Replay into shadow works.
        var source = new World();
        var stream = new CommandStream(source) { DeferredEntities = true };

        var slotA = stream.Track(stream.Create());
        stream.Add(slotA.Value, new Health(42));
        var slotB = stream.Track(stream.Create());
        stream.Add(slotB.Value, new Position(7, 8));
        stream.Add(slotB.Value, new Velocity(9, 10));

        var delta = stream.Snapshot();
        stream.Submit();

        // Source: real entities exist
        Assert.False(slotA.Value.IsPlaceholder);
        Assert.True(source.IsAlive(slotA.Value));
        Assert.True(source.TryGet(slotA.Value, out Health h));
        Assert.Equal(new Health(42), h);

        // Shadow: Replay the placeholder delta
        var shadow = new World();
        new CommandStream(shadow).Replay(delta);

        // Shadow must have the same canonical checksum
        Assert.Equal(source.CanonicalChecksum(), shadow.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 3: CommandStream + ChangeTracking (Watch API)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_CommandStream_Watch_ValueChangesPicksUpCommandStreamSet()
    {
        // B12 regression hardening: CommandStream.Set<T> → Submit must
        // produce visible value changes via Watch API Diff.
        var world = new World();
        var entity = world.Create(new Position(10, 20));

        // Set up Watch before CommandStream operations
        var handler = new PositionHandler(0);
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = handler;
        watch.Snapshot(world);

        // Use CommandStream to Set
        var stream = new CommandStream(world);
        stream.Set(entity, new Position(99, 199));
        stream.Submit();

        // Diff must find the change
        watch.Diff(world);
        var change = Assert.Single(handler.Changes);
        Assert.Equal(entity, change.Item1);
        Assert.Equal(new Position(10, 20), change.Item2); // old
        Assert.Equal(new Position(99, 199), change.Item3); // new
    }

    [Fact]
    public void X_CommandStream_Watch_ValueChangesPicksUpCommandStreamAdd()
    {
        // Add a new component via CommandStream; Watch should detect the
        // transition from default to the new value.
        var world = new World();
        var entity = world.CreateEmpty(); // no Position initially

        var handler = new PositionHandler(0);
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = handler;
        watch.Snapshot(world);

        var stream = new CommandStream(world);
        stream.Add(entity, new Position(50, 60));
        stream.Submit();

        watch.Diff(world);
        var change = Assert.Single(handler.Changes);
        Assert.Equal(entity, change.Item1);
        Assert.Equal(default(Position), change.Item2); // old was default
        Assert.Equal(new Position(50, 60), change.Item3);
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 4: Replay + Snapshot (same-frame RestoreState)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_Replay_Snapshot_RestoreStateSameFrameCorrectness()
    {
        // Replay delta into world, CaptureState, mutate, RestoreState.
        // Verify world returns to the post-Replay state.
        var hostA = new World();
        var cs = new CommandStream(hostA);
        var e = cs.Create();
        cs.Add(e, new Position(1, 2));
        var delta = cs.Snapshot();
        cs.Submit();

        var hostB = new World();
        new CommandStream(hostB).Replay(delta);

        // Capture the physical-row rollback projection after replay. RestoreState
        // retains newly discovered archetypes, but empty ones do not affect either checksum.
        var snapshot = hostB.CaptureState();
        var afterReplayChecksum = hostB.Checksum();

        // Mutate
        hostB.Set(e, new Position(99, 99));
        hostB.Create(new Health(5));

        // Restore
        hostB.RestoreState(snapshot);

        Assert.Equal(afterReplayChecksum, hostB.Checksum());
        Assert.True(hostB.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.False(hostB.TryGet<Health>(e, out _));
    }

    [Fact]
    public void X_Replay_Snapshot_RestoreStateWithFlatEntities()
    {
        // Replay a flat delta, then capture and restore state.
        var source = new World();
        var stream = new CommandStream(source);
        var parent = stream.Create();
        stream.Add(parent, new Position(0, 0));
        var child = stream.Create();
        stream.Add(child, new Velocity(1, 1));
        var delta = stream.Snapshot();
        stream.Submit();

        var target = new World();
        new CommandStream(target).Replay(delta);

        var snap = target.CaptureState();
        var checksum = target.Checksum();

        // Mutate the world after capture.
        var newChild = target.Create(new Health(10));
        target.Destroy(parent);

        // Restore
        target.RestoreState(snap);

        Assert.Equal(checksum, target.Checksum());
        Assert.True(target.IsAlive(parent));
        Assert.True(target.IsAlive(child));
        Assert.False(target.IsAlive(newChild));
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 5: Flat lifecycle + ChangeTracking (Watch API)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_FlatDestroy_Watch_ProducesExitedOnlyForDestroyedEntity()
    {
        // Only the destroyed matching entity exits the watch.
        var world = new World();
        var parent = world.CreateEmpty();
        var child1 = world.Create(new Position(1, 2));
        var child2 = world.Create(new Position(3, 4));
        var unrelated = world.Create(new Position(5, 6));

        var watch = world.Watch<TransitionRecorder>(
            new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);

        world.Destroy(child1);
        world.Destroy(parent);

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal((child1, TransitionKind.Exited), change);
        Assert.True(world.IsAlive(child2));
        Assert.True(world.IsAlive(unrelated));
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 6: Snapshot + flat lifecycle
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_Snapshot_Flat_SaveLoadPreservesComponents()
    {
        var world = new World();
        var root = world.Create(new Position(1, 2));
        var mid = world.Create(new Position(3, 4));
        var leaf = world.Create(new Position(5, 6));

        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, world);
        ms.Position = 0;

        var loaded = WorldSnapshot.Load(ms);

        Assert.Equal(new Position(1, 2), loaded.Get<Position>(root));
        Assert.Equal(new Position(3, 4), loaded.Get<Position>(mid));
        Assert.Equal(new Position(5, 6), loaded.Get<Position>(leaf));

        // Canonical checksums must match
        Assert.Equal(world.CanonicalChecksum(), loaded.CanonicalChecksum());
    }

    [Fact]
    public void X_Snapshot_Flat_DestroyAfterLoadPreservesOtherEntities()
    {
        // Destroying one loaded entity leaves the other loaded entity intact.
        var world = new World();
        var root = world.CreateEmpty();
        var child = world.Create(new Position(1, 2));

        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, world);
        ms.Position = 0;

        var loaded = WorldSnapshot.Load(ms);
        loaded.Destroy(root);

        Assert.False(loaded.IsAlive(root));
        Assert.True(loaded.IsAlive(child));
        Assert.Equal(new Position(1, 2), loaded.Get<Position>(child));
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 7: DeferredEntities + flat replay
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_Deferred_Flat_TrackThenReplay()
    {
        // Use DeferredEntities=true with Track() for auto-updating handles.
        var source = new World();
        var stream = new CommandStream(source) { DeferredEntities = true };

        var slotParent = stream.Track(stream.Create());
        stream.Add(slotParent.Value, new Position(100, 200));
        var slotChild = stream.Track(stream.Create());
        stream.Add(slotChild.Value, new Position(300, 400));

        Assert.True(slotParent.Value.IsPlaceholder);
        Assert.True(slotChild.Value.IsPlaceholder);

        var delta = stream.Snapshot();
        stream.Submit();

        // Source post-Submit: slots resolve to independent real entities.
        Assert.False(slotParent.Value.IsPlaceholder);
        Assert.False(slotChild.Value.IsPlaceholder);
        Assert.Equal(new Position(100, 200), source.Get<Position>(slotParent.Value));
        Assert.Equal(new Position(300, 400), source.Get<Position>(slotChild.Value));

        // Shadow: Replay the placeholder delta
        var shadow = new World();
        new CommandStream(shadow).Replay(delta);

        // Use TryResolvePlaceholder to find real entities in shadow
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 0), out var shadowParent));
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 1), out var shadowChild));

        Assert.Equal(new Position(100, 200), shadow.Get<Position>(shadowParent));
        Assert.Equal(new Position(300, 400), shadow.Get<Position>(shadowChild));

        Assert.Equal(source.CanonicalChecksum(), shadow.CanonicalChecksum());
    }

    [Fact]
    public void X_Deferred_Flat_ThreeSlotsViaTrackThenReplay()
    {
        // Track three independent pending entities.
        var source = new World();
        var stream = new CommandStream(source) { DeferredEntities = true };

        var slotRoot = stream.Track(stream.Create());
        var slotMid = stream.Track(stream.Create());
        var slotLeaf = stream.Track(stream.Create());
        // Only the last entity has a component.
        stream.Add(slotLeaf.Value, new Health(99));

        var delta = stream.Snapshot();
        stream.Submit();

        // Source
        Assert.True(source.IsAlive(slotRoot.Value));
        Assert.True(source.IsAlive(slotMid.Value));
        Assert.Equal(new Health(99), source.Get<Health>(slotLeaf.Value));

        // Shadow: Replay and resolve placeholders by seq
        var shadow = new World();
        new CommandStream(shadow).Replay(delta);

        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 0), out var sRoot));
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 1), out var sMid));
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 2), out var sLeaf));

        Assert.True(shadow.IsAlive(sRoot));
        Assert.True(shadow.IsAlive(sMid));
        Assert.Equal(new Health(99), shadow.Get<Health>(sLeaf));

        Assert.Equal(source.CanonicalChecksum(), shadow.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 8: ChangeTracking (Watch) + RestoreState
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_Watch_RestoreState_ValueTrackingSurvivesRestore()
    {
        // B8/B15 hardening: Watch value tracking must work correctly
        // after RestoreState, without stale data or lost tracking.
        var world = new World();
        var e = world.Create(new Position(10, 20));

        var handler = new PositionHandler(0);
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = handler;

        // Capture state, snapshot watch baseline, mutate, verify diff
        var snap = world.CaptureState();
        watch.Snapshot(world);

        world.Set(e, new Position(99, 99));
        watch.Diff(world);
        Assert.Single(handler.Changes);

        // Restore and get a fresh snapshot for the next restore step
        world.RestoreState(snap);
        // Re-capture for second round
        var snap2 = world.CaptureState();

        handler = new PositionHandler(0);
        watch.Handler = handler;

        // After restore, watch should detect new mutations
        watch.Snapshot(world);
        world.Set(e, new Position(200, 300));
        watch.Diff(world);
        var change = Assert.Single(handler.Changes);
        Assert.Equal(e, change.Item1);
        Assert.Equal(new Position(10, 20), change.Item2); // old = post-restore value
        Assert.Equal(new Position(200, 300), change.Item3);

        // Clean up second snapshot
        world.RestoreState(snap2);
    }

    [Fact]
    public void X_Watch_RestoreState_TransitionTrackingSurvivesRestore()
    {
        // B8/B15 hardening: TransitionWatch must work after RestoreState.
        var world = new World();
        var e = world.Create(new Position(0, 0));

        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(
            new QueryDescription().With<Position>());
        watch.Handler = handler;

        var snap = world.CaptureState();
        watch.Snapshot(world);
        watch.Diff(world); // drain initial
        Assert.Empty(watch.Handler.Changes);

        world.RestoreState(snap);
        var snap2 = world.CaptureState(); // re-capture for cleanup

        handler = new TransitionRecorder(0);
        watch.Handler = handler;
        watch.Snapshot(world);

        // Create a new entity after restore
        var e2 = world.Create(new Position(5, 5));
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e2, change.Item1);
        Assert.Equal(TransitionKind.Entered, change.Item2);

        world.RestoreState(snap2);
    }

    [Fact]
    public void X_Watch_RestoreState_NoStaleTransitionsAfterRestore()
    {
        // After RestoreState, old transitions must not remain in watch buffer.
        var world = new World();
        var e1 = world.Create(new Position(0, 0));

        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(
            new QueryDescription().With<Position>());
        watch.Handler = handler;

        var snap = world.CaptureState();
        watch.Snapshot(world);

        // Destroy e1 (this produces Exited)
        world.Destroy(e1);
        watch.Diff(world);
        Assert.Single(watch.Handler.Changes);

        // Restore to before the destroy
        world.RestoreState(snap);
        var snap2 = world.CaptureState(); // re-capture

        handler = new TransitionRecorder(0);
        watch.Handler = handler;
        watch.Snapshot(world);

        // After restore, e1 is alive again. Diff must show no transitions.
        watch.Diff(world);
        Assert.Empty(watch.Handler.Changes);

        world.RestoreState(snap2);
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 9: Three-way — Submit → Snapshot → RestoreState → Replay
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_SubmitSnapshotRestoreReplay_CanonicalChecksumPipeline()
    {
        // Full pipeline: Submit commands, Snapshot delta, RestoreState
        // to roll back, then Replay the delta. Verify final state matches
        // the original post-Submit state via canonical checksum.
        var world = new World();

        // Phase 1: Build initial state via Submit
        var stream1 = new CommandStream(world);
        var parent = stream1.Create();
        stream1.Add(parent, new Position(1, 2));
        var child = stream1.Create();
        stream1.Add(child, new Velocity(3, 4));
        stream1.Submit();

        // Capture this state
        var snap = world.CaptureState();

        // Phase 2: Mutate world with new entities
        world.Create(new Health(100));
        var stream2 = new CommandStream(world);
        stream2.Set(child, new Velocity(99, 99));
        stream2.Destroy(parent);
        stream2.Submit();

        // Phase 3: RestoreState back to captured state
        world.RestoreState(snap);
        // Re-capture for the Replay comparison
        var snap2 = world.CaptureState();

        // Re-create the same mutations via Replay of a Snapshot
        var stream3 = new CommandStream(world);
        var extra2 = stream3.Create();
        stream3.Add(extra2, new Health(100));
        stream3.Set(child, new Velocity(99, 99));
        stream3.Destroy(parent);
        var delta = stream3.Snapshot();
        stream3.Submit();

        // Now restore once more and Replay the delta instead
        world.RestoreState(snap2);
        new CommandStream(world).Replay(delta);

        // Final state — only the specified entity was destroyed.
        Assert.False(world.IsAlive(parent));
        Assert.True(world.IsAlive(child));
        Assert.Equal(new Velocity(99, 99), world.Get<Velocity>(child));
        Assert.True(world.TryGet(extra2, out Health h));
        Assert.Equal(new Health(100), h);

        var postReplayChecksum = world.CanonicalChecksum();
        var expectedChecksum = ComputeExpectedChecksum();

        Assert.Equal(expectedChecksum, postReplayChecksum);

        // Local helper: builds the expected world from scratch via Submit
        byte[] ComputeExpectedChecksum()
        {
            var expected = new World();
            var s1 = new CommandStream(expected);
            var p = s1.Create();
            s1.Add(p, new Position(1, 2));
            var c = s1.Create();
            s1.Add(c, new Velocity(3, 4));
            s1.Submit();

            var s2 = new CommandStream(expected);
            var ex = s2.Create();
            s2.Add(ex, new Health(100));
            s2.Set(c, new Velocity(99, 99));
            s2.Destroy(p);
            s2.Submit();

            return expected.CanonicalChecksum();
        }
    }

    [Fact]
    public void X_ThreeWay_FlatSnapshotRestoreReplayConvergence()
    {
        // CaptureState, mutate, restore, then replay equivalent commands.
        var world = new World();

        // Three independent entities.
        var a = world.Create(new Position(1, 1));
        var b = world.Create(new Position(2, 2));
        var c = world.Create(new Position(3, 3));

        var snap = world.CaptureState();
        var initialChecksum = world.CanonicalChecksum();

        // A direct world mutation is rolled back.
        world.Create(new Position(4, 4));
        world.Set(b, new Position(20, 20));

        world.RestoreState(snap);
        // Re-capture for second round
        var snap2 = world.CaptureState();

        // Verify restoration
        Assert.Equal(initialChecksum, world.CanonicalChecksum());
        Assert.Equal(3, world.EntityCount);
        Assert.Equal(new Position(1, 1), world.Get<Position>(a));
        Assert.Equal(new Position(2, 2), world.Get<Position>(b));
        Assert.Equal(new Position(3, 3), world.Get<Position>(c));

        // Apply the same changes via Submit.
        var stream = new CommandStream(world);
        var d2 = stream.Create();
        stream.Add(d2, new Position(4, 4));
        stream.Set(b, new Position(20, 20));
        var delta = stream.Snapshot();
        stream.Submit();

        // Capture expected post-Submit state
        var expectedChecksum = world.CanonicalChecksum();

        // Restore and Replay
        world.RestoreState(snap2);
        new CommandStream(world).Replay(delta);

        Assert.Equal(expectedChecksum, world.CanonicalChecksum());
        Assert.Equal(4, world.EntityCount);
    }
}
