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
    // Cell 1: CommandStream + Hierarchy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_CommandStream_Hierarchy_AddChildSubmitThenReplay()
    {
        // Build hierarchy via CommandStream, Submit, then Replay into shadow
        // and verify both worlds have identical hierarchy and checksum.
        var source = new World();
        var stream = new CommandStream(source);

        var parent = stream.Create();
        stream.Add(parent, new Position(1, 2));
        var child = stream.Create();
        stream.Add(child, new Velocity(3, 4));
        stream.AddChild(parent, child);
        var delta1 = stream.Snapshot();
        stream.Submit();

        // Source post-Submit assertions
        Assert.True(source.TryGetParent(child, out var sourceParent));
        Assert.Equal(parent, sourceParent);
        var sourceChildren = source.EnumerateChildren(parent).ToChildList();
        Assert.Single(sourceChildren);
        Assert.Equal(child, sourceChildren[0]);
        Assert.True(source.TryGet(parent, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.True(source.TryGet(child, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);

        // Replay into shadow world
        var shadow = new World();
        new CommandStream(shadow).Replay(delta1);

        Assert.True(shadow.TryGetParent(child, out var shadowParent));
        Assert.Equal(parent, shadowParent);
        var shadowChildren = shadow.EnumerateChildren(parent).ToChildList();
        Assert.Single(shadowChildren);
        Assert.Equal(child, shadowChildren[0]);
        Assert.True(shadow.TryGet(child, out Velocity sv));
        Assert.Equal(new Velocity(3, 4), sv);

        // Canonical checksums must match
        Assert.Equal(source.CanonicalChecksum(), shadow.CanonicalChecksum());
    }

    [Fact]
    public void X_CommandStream_Hierarchy_CascadeDestroyAcrossBatchBoundaries()
    {
        // Frame 1: create parent with two children via CS, Submit.
        // Frame 2: destroy parent via CS (cascade destroys children), Submit.
        // Verify cascade and Replay convergence.
        var source = new World();
        var stream1 = new CommandStream(source);

        var parent = stream1.Create();
        stream1.Add(parent, new Position(0, 0));
        var child1 = stream1.Create();
        stream1.Add(child1, new Position(10, 10));
        var child2 = stream1.Create();
        stream1.Add(child2, new Position(20, 20));
        stream1.AddChild(parent, child1);
        stream1.AddChild(parent, child2);
        var delta1 = stream1.Snapshot();
        stream1.Submit();

        Assert.True(source.IsAlive(parent));
        Assert.True(source.IsAlive(child1));
        Assert.True(source.IsAlive(child2));
        Assert.Equal(2, source.EnumerateChildren(parent).ToChildList().Count);

        // Frame 2: destroy parent via a new stream
        var stream2 = new CommandStream(source);
        stream2.Destroy(parent);
        var delta2 = stream2.Snapshot();
        stream2.Submit();

        Assert.False(source.IsAlive(parent));
        Assert.False(source.IsAlive(child1));
        Assert.False(source.IsAlive(child2));

        // Verify Replay of the same deltas produces identical state
        var shadow = new World();
        new CommandStream(shadow).Replay(delta1);
        new CommandStream(shadow).Replay(delta2);

        Assert.False(shadow.IsAlive(parent));
        Assert.False(shadow.IsAlive(child1));
        Assert.False(shadow.IsAlive(child2));
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

        // Capture state after replay
        var snapshot = hostB.CaptureState();
        var afterReplayChecksum = hostB.CanonicalChecksum();

        // Mutate
        hostB.Set(e, new Position(99, 99));
        hostB.Create(new Health(5));

        // Restore
        hostB.RestoreState(snapshot);

        Assert.Equal(afterReplayChecksum, hostB.CanonicalChecksum());
        Assert.True(hostB.TryGet(e, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.False(hostB.TryGet<Health>(e, out _));
    }

    [Fact]
    public void X_Replay_Snapshot_RestoreStateWithHierarchy()
    {
        // Replay a delta containing hierarchy, then CaptureState/RestoreState.
        var source = new World();
        var stream = new CommandStream(source);
        var parent = stream.Create();
        stream.Add(parent, new Position(0, 0));
        var child = stream.Create();
        stream.Add(child, new Velocity(1, 1));
        stream.AddChild(parent, child);
        var delta = stream.Snapshot();
        stream.Submit();

        var target = new World();
        new CommandStream(target).Replay(delta);

        var snap = target.CaptureState();
        var checksum = target.CanonicalChecksum();

        // Mutate hierarchy
        var newChild = target.Create(new Health(10));
        target.AddChild(parent, newChild);

        // Restore
        target.RestoreState(snap);

        Assert.Equal(checksum, target.CanonicalChecksum());
        Assert.True(target.TryGetParent(child, out var p));
        Assert.Equal(parent, p);
        Assert.Single(target.EnumerateChildren(parent).ToChildList());
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 5: Hierarchy + ChangeTracking (Watch API)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_Hierarchy_Watch_AddChildDoesNotTriggerTransition()
    {
        // Hierarchy changes do not affect filter membership (components unchanged).
        // AddChild must NOT produce a transition for the child.
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.Create(new Position(1, 2));

        var watch = world.Watch<TransitionRecorder>(
            new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);

        world.AddChild(parent, child);

        watch.Diff(world);
        Assert.Empty(watch.Handler.Changes);
    }

    [Fact]
    public void X_Hierarchy_Watch_RemoveChildDoesNotTriggerTransition()
    {
        // Removing a child should NOT change filter membership for the child
        // (child still has Position). No transition expected.
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.Create(new Position(1, 2));
        world.AddChild(parent, child);

        var watch = world.Watch<TransitionRecorder>(
            new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);

        world.RemoveChild(child);

        watch.Diff(world);
        Assert.Empty(watch.Handler.Changes);
    }

    [Fact]
    public void X_Hierarchy_Watch_CascadeDestroyProducesExitedForChildren()
    {
        // Destroy a parent with children; cascade should produce Exited
        // for all children that match the filter (children are destroyed).
        var world = new World();
        var parent = world.CreateEmpty();
        var child1 = world.Create(new Position(1, 2));
        var child2 = world.Create(new Position(3, 4));
        var unrelated = world.Create(new Position(5, 6));
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);

        var watch = world.Watch<TransitionRecorder>(
            new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);

        world.Destroy(parent);

        watch.Diff(world);
        // Both children should have Exited; unrelated survives.
        Assert.Equal(2, watch.Handler.Changes.Count);
        foreach (var (_, kind) in watch.Handler.Changes)
            Assert.Equal(TransitionKind.Exited, kind);
        var exitedEntities = watch.Handler.Changes
            .Select(c => c.Item1).OrderBy(e => e.Id).ToArray();
        Assert.Equal(child1, exitedEntities[0]);
        Assert.Equal(child2, exitedEntities[1]);
        Assert.True(world.IsAlive(unrelated));
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 6: Snapshot + Hierarchy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_Snapshot_Hierarchy_SaveLoadPreservesRelationships()
    {
        var world = new World();
        var root = world.Create(new Position(1, 2));
        var mid = world.Create(new Position(3, 4));
        var leaf = world.Create(new Position(5, 6));
        world.AddChild(root, mid);
        world.AddChild(mid, leaf);

        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, world);
        ms.Position = 0;

        var loaded = WorldSnapshot.Load(ms);

        // Check parent chain
        Assert.True(loaded.TryGetParent(leaf, out var leafParent));
        Assert.Equal(mid, leafParent);
        Assert.True(loaded.TryGetParent(mid, out var midParent));
        Assert.Equal(root, midParent);
        Assert.False(loaded.TryGetParent(root, out _));

        // Check children
        var rootChildren = loaded.EnumerateChildren(root).ToChildList();
        Assert.Single(rootChildren);
        Assert.Equal(mid, rootChildren[0]);
        var midChildren = loaded.EnumerateChildren(mid).ToChildList();
        Assert.Single(midChildren);
        Assert.Equal(leaf, midChildren[0]);

        // Canonical checksums must match
        Assert.Equal(world.CanonicalChecksum(), loaded.CanonicalChecksum());
    }

    [Fact]
    public void X_Snapshot_Hierarchy_CascadeDestroyAfterLoadWorks()
    {
        // Load a world with hierarchy, then destroy root; cascade must work.
        var world = new World();
        var root = world.CreateEmpty();
        var child = world.Create(new Position(1, 2));
        world.AddChild(root, child);

        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, world);
        ms.Position = 0;

        var loaded = WorldSnapshot.Load(ms);
        loaded.Destroy(root);

        Assert.False(loaded.IsAlive(root));
        Assert.False(loaded.IsAlive(child));
    }

    // ═══════════════════════════════════════════════════════════════
    // Cell 7: DeferredEntities + Hierarchy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void X_Deferred_Hierarchy_TreeViaTrackThenReplay()
    {
        // Use DeferredEntities=true with Track() for auto-updating handles.
        // Build hierarchy, Snapshot, Replay, verify checksums match.
        var source = new World();
        var stream = new CommandStream(source) { DeferredEntities = true };

        var slotParent = stream.Track(stream.Create());
        stream.Add(slotParent.Value, new Position(100, 200));
        var slotChild = stream.Track(stream.Create());
        stream.Add(slotChild.Value, new Position(300, 400));
        stream.AddChild(slotParent.Value, slotChild.Value);

        Assert.True(slotParent.Value.IsPlaceholder);
        Assert.True(slotChild.Value.IsPlaceholder);

        var delta = stream.Snapshot();
        stream.Submit();

        // Source post-Submit: slots resolved to real IDs, hierarchy intact
        Assert.False(slotParent.Value.IsPlaceholder);
        Assert.False(slotChild.Value.IsPlaceholder);
        Assert.True(source.TryGetParent(slotChild.Value, out _));
        var srcChildren = source.EnumerateChildren(slotParent.Value).ToChildList();
        Assert.Single(srcChildren);
        Assert.Equal(slotChild.Value, srcChildren[0]);

        // Shadow: Replay the placeholder delta
        var shadow = new World();
        new CommandStream(shadow).Replay(delta);

        // Use TryResolvePlaceholder to find real entities in shadow
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 0), out var shadowParent));
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 1), out var shadowChild));

        Assert.True(shadow.TryGetParent(shadowChild, out var actualParent));
        Assert.Equal(shadowParent, actualParent);
        var shadowChildren = shadow.EnumerateChildren(shadowParent).ToChildList();
        Assert.Single(shadowChildren);
        Assert.Equal(shadowChild, shadowChildren[0]);

        Assert.Equal(source.CanonicalChecksum(), shadow.CanonicalChecksum());
    }

    [Fact]
    public void X_Deferred_Hierarchy_DeepTreeViaTrackThenReplay()
    {
        // Build a 3-level deep tree with DeferredEntities=true + Track.
        var source = new World();
        var stream = new CommandStream(source) { DeferredEntities = true };

        var slotRoot = stream.Track(stream.Create());
        var slotMid = stream.Track(stream.Create());
        var slotLeaf = stream.Track(stream.Create());
        // Only add component to leaf to differentiate
        stream.Add(slotLeaf.Value, new Health(99));
        stream.AddChild(slotRoot.Value, slotMid.Value);
        stream.AddChild(slotMid.Value, slotLeaf.Value);

        var delta = stream.Snapshot();
        stream.Submit();

        // Source
        Assert.True(source.TryGetParent(slotLeaf.Value, out var leafP));
        Assert.Equal(slotMid.Value, leafP);
        Assert.True(source.TryGetParent(slotMid.Value, out var midP));
        Assert.Equal(slotRoot.Value, midP);

        // Shadow: Replay and resolve placeholders by seq
        var shadow = new World();
        new CommandStream(shadow).Replay(delta);

        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 0), out var sRoot));
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 1), out var sMid));
        Assert.True(shadow.TryResolvePlaceholder(new Entity(-1, 2), out var sLeaf));

        Assert.True(shadow.TryGetParent(sLeaf, out var sLeafP));
        Assert.Equal(sMid, sLeafP);
        Assert.True(shadow.TryGetParent(sMid, out var sMidP));
        Assert.Equal(sRoot, sMidP);

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
        stream1.AddChild(parent, child);
        stream1.Submit();

        // Capture this state
        var snap = world.CaptureState();

        // Phase 2: Mutate world with new entities
        world.Create(new Health(100));
        var stream2 = new CommandStream(world);
        stream2.Set(child, new Velocity(99, 99));
        stream2.Destroy(parent); // cascade destroys child
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

        // Final state — verify parent/child destroyed, extra2 alive
        Assert.False(world.IsAlive(parent));
        Assert.False(world.IsAlive(child));
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
            s1.AddChild(p, c);
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
    public void X_ThreeWay_HierarchySnapshotRestoreReplayConvergence()
    {
        // Build hierarchy, CaptureState, mutate hierarchy, RestoreState,
        // then apply same mutations via Replay. Verify checksums converge.
        var world = new World();

        // Initial tree: A → B → C
        var a = world.Create(new Position(1, 1));
        var b = world.Create(new Position(2, 2));
        var c = world.Create(new Position(3, 3));
        world.AddChild(a, b);
        world.AddChild(b, c);

        var snap = world.CaptureState();
        var initialChecksum = world.CanonicalChecksum();

        // Restructure: D added under A, B removed from A
        var d = world.Create(new Position(4, 4));
        world.AddChild(a, d);
        world.RemoveChild(b); // B becomes root

        world.RestoreState(snap);
        // Re-capture for second round
        var snap2 = world.CaptureState();

        // Verify restoration
        Assert.Equal(initialChecksum, world.CanonicalChecksum());
        Assert.Equal(3, world.EntityCount);
        Assert.True(world.TryGetParent(b, out var bp));
        Assert.Equal(a, bp);
        Assert.True(world.TryGetParent(c, out var cp));
        Assert.Equal(b, cp);
        Assert.False(world.TryGetParent(a, out _));

        // Apply same restructure via Submit
        var stream = new CommandStream(world);
        var d2 = stream.Create();
        stream.Add(d2, new Position(4, 4));
        stream.AddChild(a, d2);
        stream.RemoveChild(b);
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
