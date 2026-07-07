using System.IO;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Attack-surface tests for the unified ChangeQuery change-tracking system.
/// Covers: OnBeforeTransition/OnTransition rollback, multi-type Capture byte
/// layout, CommandStream replay + Previous(), mixed write/structural ops,
/// multi-query isolation, snapshot round-trips, and the pending-entity
/// final-state contract (ops on Create'd entities are folded to net result).
///
/// Tests assert CORRECT expected behavior per the documented contract.
/// </summary>
public sealed class ChangeTrackingAttackSurfaceTests
{
    private readonly record struct HP(int Value);
    private readonly record struct Dead;
    private readonly record struct Enemy;
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Mana(int Value);

    // ═══════════════════════════════════════════════════════════════
    // SECTION A1-A2: OnBeforeTransition rollback paths
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A1: When a structural change fires OnBeforeTransition but the transition
    /// does NOT match the filter (oldMatch=false AND newMatch=false), the
    /// entity list must be rolled back so Changes() indices stay correct.
    ///
    /// Scenario: filter requires HP, entity has only Position (no HP).
    /// Adding Velocity → entity still has no HP → no transition.
    /// OnBeforeTransition captured Old → OnTransition must rollback.
    ///
    /// BUG: OnBeforeTransition calls GetComponentIndexFast for HP on the
    /// {Position} archetype (which doesn't have HP). This either crashes
    /// (IndexOutOfRange) or silently reads corrupted data. The rollback
    /// never gets a chance to run cleanly.
    ///
    /// Expected: Changes() empty after Add<Velocity> on non-matching entity.
    /// Actual:   crashes or corrupted snapshot.
    /// </summary>
    [Fact]
    public void A1_OnBeforeTransition_rollback_when_filter_rejects_both()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();

        // Entity has Position but no HP → does NOT match filter
        var e = world.Create(new Position(1, 1));

        // Add Velocity → structural change, entity still has no HP
        // → oldMatch=false, newMatch=false → must rollback
        // BUG: this crashes or reads garbage because OnBeforeTransition
        // tries to read HP from {Position} which doesn't contain HP.
        world.Add(e, new Velocity(2, 2));

        // If we reach here: no crash (component IDs happened to align
        // to avoid bounds error), but data may still be corrupted.
        // Rollback must have cleared the dangling entity.
        Assert.Empty(q.Changes());

        // Now add HP → entity enters filter (Entered transition + snapshot)
        world.Add(e, new HP(100));
        _ = q.Changes();  // drain the transition snapshot
        var ts = q.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);

        // Set HP → should produce a clean Changes() entry
        world.Set(e, new HP(50));
        var cs = q.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(e, cs[0].Entity);
        Assert.Equal(100, cs[0].Old.Get<HP>().Value);
        Assert.Equal(50, cs[0].New.Get<HP>().Value);
    }

    /// <summary>
    /// A2: Add then Remove same component same frame on same entity.
    /// Entity enters filter then immediately exits.
    /// Both OnBeforeTransition calls and both OnTransition calls must
    /// produce correct, non-overlapping snapshot pairs.
    ///
    /// BUG: The Add<HP> step crashes in OnBeforeTransition because the
    /// entity's current archetype ({Position}) doesn't contain HP.
    /// Same root cause as A1.
    ///
    /// Expected: 2 snapshot entries (one from Add, one from Remove),
    ///           both with correct entities.
    /// Actual:   crashes at Add step.
    /// </summary>
    [Fact]
    public void A2_Add_then_Remove_same_frame_correct_snapshots()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();

        // Entity starts without HP → not in filter
        var e = world.Create(new Position(1, 1));

        // Add HP → enters filter (Entered)
        world.Add(e, new HP(100));

        // Remove HP → exits filter (Exited)
        world.Remove<HP>(e);

        // Two transitions: Entered then Exited
        var ts = q.Transitions().ToList();
        Assert.Equal(2, ts.Count);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(TransitionKind.Exited, ts[1].Kind);

        // Changes(): Add captured Old (HP absent) and New (HP=100),
        //            Remove captured Old (HP=100) and New (HP absent)
        var cs = q.Changes().ToList();
        Assert.Equal(2, cs.Count);
        Assert.Equal(e, cs[0].Entity);
        Assert.Equal(e, cs[1].Entity);
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A3-A4: Multi-type Capture interactions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A3: Multi-type Capture byte layout — Changes() must correctly
    /// read Position and Velocity at their proper offsets, without
    /// cross-contamination between types.
    /// </summary>
    [Fact]
    public void A3_Multi_type_Capture_byte_layout_no_cross_contamination()
    {
        var world = new World();
        var q = world.Track()
            .Capture<Position>()
            .Capture<Velocity>()
            .With<Position>()
            .Previous();

        var e = world.Create(new Position(0, 0), new Velocity(1, 1));

        // Set Position only → Velocity should be unchanged in snapshot
        world.Set(e, new Position(5, 5));
        var cs = q.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(e, cs[0].Entity);
        Assert.Equal(0, cs[0].Old.Get<Position>().X);
        Assert.Equal(0, cs[0].Old.Get<Position>().Y);
        Assert.Equal(5, cs[0].New.Get<Position>().X);
        Assert.Equal(5, cs[0].New.Get<Position>().Y);
        Assert.Equal(1, cs[0].Old.Get<Velocity>().Dx);
        Assert.Equal(1, cs[0].Old.Get<Velocity>().Dy);
        Assert.Equal(1, cs[0].New.Get<Velocity>().Dx);
        Assert.Equal(1, cs[0].New.Get<Velocity>().Dy);

        // Set Velocity only → Position should be unchanged
        world.Set(e, new Velocity(9, 9));
        cs = q.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(5, cs[0].Old.Get<Position>().X);
        Assert.Equal(5, cs[0].Old.Get<Position>().Y);
        Assert.Equal(5, cs[0].New.Get<Position>().X);
        Assert.Equal(5, cs[0].New.Get<Position>().Y);
        Assert.Equal(1, cs[0].Old.Get<Velocity>().Dx);
        Assert.Equal(1, cs[0].Old.Get<Velocity>().Dy);
        Assert.Equal(9, cs[0].New.Get<Velocity>().Dx);
        Assert.Equal(9, cs[0].New.Get<Velocity>().Dy);

        // Set both in sequence → per-entity: 1 entry with first Old and last New
        world.Set(e, new Position(10, 10));
        world.Set(e, new Velocity(20, 20));
        cs = q.Changes().ToList();
        Assert.Single(cs);

        Assert.Equal(e, cs[0].Entity);
        Assert.Equal(5, cs[0].Old.Get<Position>().X);
        Assert.Equal(10, cs[0].New.Get<Position>().X);
        Assert.Equal(9, cs[0].Old.Get<Velocity>().Dx);
        Assert.Equal(20, cs[0].New.Get<Velocity>().Dx);
    }

    /// <summary>
    /// A4: Multi-type Capture ModifiedChunks independence —
    /// cursors for Position and Velocity must advance independently.
    /// </summary>
    [Fact]
    public void A4_Multi_type_ModifiedChunks_independent_cursors()
    {
        var world = new World();
        var q = world.Track()
            .Capture<Position>()
            .Capture<Velocity>()
            .With<Position>()
            .With<Velocity>();

        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        _ = q.ModifiedChunks<Position>();
        _ = q.ModifiedChunks<Velocity>();

        // Write Position only
        world.Set(e, new Position(5, 5));
        Assert.NotEmpty(q.ModifiedChunks<Position>());
        Assert.Empty(q.ModifiedChunks<Velocity>());

        // Write Velocity only
        world.Set(e, new Velocity(3, 3));
        Assert.Empty(q.ModifiedChunks<Position>());
        Assert.NotEmpty(q.ModifiedChunks<Velocity>());

        Assert.Empty(q.ModifiedChunks<Position>());
        Assert.Empty(q.ModifiedChunks<Velocity>());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A5-A7: CommandStream replay + Previous()
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A5: Replay Set with Previous() — Old/New snapshots must be
    /// captured correctly through the CommandStream replay path.
    /// </summary>
    [Fact]
    public void A5_Replay_Set_with_Previous_captures_old_and_new()
    {
        var hostA = new World();
        var eA = hostA.Create(new HP(100));
        var cs = new CommandStream(hostA);
        cs.Set(eA, new HP(80));
        var delta = cs.Snapshot();

        var hostB = new World();
        var eB = hostB.Create(new HP(100));
        var q = hostB.Track().Capture<HP>().With<HP>().Previous();

        new CommandStream(hostB).Replay(delta);

        var changes = q.Changes().ToList();
        Assert.Single(changes);
        Assert.Equal(eB, changes[0].Entity);
        Assert.Equal(100, changes[0].Old.Get<HP>().Value);
        Assert.Equal(80, changes[0].New.Get<HP>().Value);
    }

    /// <summary>
    /// A6: Replay Create+Add+Set on same entity in one delta — pending entity
    /// operations are folded to final state. After Replay, the entity enters
    /// the filter (it ends with HP), but Changes() is empty because no
    /// individual write hooks fire during pending entity materialization.
    ///
    /// This is by design: the CommandStream batch buffer records only the
    /// final component state for created entities; intermediate Add/Set/Remove
    /// operations are not observable as individual Changes() entries.
    /// </summary>
    [Fact]
    public void A6_Replay_CreateAdd_then_Set_in_one_delta()
    {
        var hostA = new World();
        var cs = new CommandStream(hostA);
        var e = cs.Create();
        cs.Add(e, new HP(100));
        cs.Set(e, new HP(80));
        var delta = cs.Snapshot();

        var hostB = new World();
        var q = hostB.Track().Capture<HP>().With<HP>().Previous();

        new CommandStream(hostB).Replay(delta);

        // The entity enters the filter (final state has HP=80)
        var ts = q.Transitions().ToList();
        Assert.Contains(ts, t => t.Kind == TransitionKind.Entered);

        // Changes() is empty because pending ops are folded to final state.
        // No individual Set hook fires during batch materialization.
        Assert.Empty(q.Changes());
    }

    /// <summary>
    /// A7: Replay a mixed batch of Add, Set, Remove in one delta — pending
    /// entity operations are folded to final state.
    ///
    /// Entity A: Create + Add HP(10) + Set HP(20) + Remove HP → final: no HP.
    ///   → Never enters the filter (final state has no HP). No transitions.
    /// Entity B: Create + Add HP(30) → final: has HP.
    ///   → Enters the filter. One Entered.
    ///
    /// This is by design: the CommandStream batch buffer retains only the
    /// final component signature per pending entity; intermediate structural
    /// membership changes are not observable as individual transitions.
    /// </summary>
    [Fact]
    public void A7_Replay_mixed_Add_Set_Remove_batch()
    {
        var hostA = new World();
        var csA = new CommandStream(hostA);

        // Entity A: final state has no HP
        var eA = csA.Create();
        csA.Add(eA, new HP(10));
        csA.Set(eA, new HP(20));
        csA.Remove<HP>(eA);

        // Entity B: final state has HP=30
        var eB = csA.Create();
        csA.Add(eB, new HP(30));

        var delta = csA.Snapshot();

        var hostB = new World();
        var q = hostB.Track().Capture<HP>().With<HP>().Previous();

        new CommandStream(hostB).Replay(delta);

        var ts = q.Transitions().ToList();
        var entered = ts.Where(t => t.Kind == TransitionKind.Entered).ToList();
        var exited = ts.Where(t => t.Kind == TransitionKind.Exited).ToList();

        // Only B enters the filter (A ends with no HP)
        Assert.Single(entered);
        Assert.Empty(exited);

        // Changes() is empty: no individual write hooks fire during
        // pending entity batch materialization.
        Assert.Empty(q.Changes());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A8: Mixed Set + structural change on same entity
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A8: Set a captured type then immediately do a structural change
    /// (Add component that causes filter exit). Changes() and Transitions()
    /// must be independent and correct.
    /// </summary>
    [Fact]
    public void A8_Set_then_structural_exit_same_entity()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Without<Dead>().Previous();

        var e = world.Create(new HP(100));
        _ = q.Transitions();
        _ = q.Changes();

        // Set HP → captured in Changes
        world.Set(e, new HP(50));

        // Add Dead → entity exits filter (Exited transition + snapshot)
        world.Add(e, new Dead());

        var ts = q.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);

        // Changes should include both the Set entry and the transition snapshot
        var cs = q.Changes().ToList();
        Assert.NotEmpty(cs);
        Assert.Contains(cs, c => c.Entity == e);
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A9: Multiple Sets before drain — order preservation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A9: Multiple Sets on the same entity without draining.
    /// Per-entity: only first Old and last New recorded.
    /// </summary>
    [Fact]
    public void A9_Multiple_sets_before_drain_ordered_chain()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();

        var e = world.Create(new HP(100));

        world.Set(e, new HP(90));
        world.Set(e, new HP(80));
        world.Set(e, new HP(70));

        var cs = q.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(e, cs[0].Entity);

        Assert.Equal(100, cs[0].Old.Get<HP>().Value);
        Assert.Equal(70, cs[0].New.Get<HP>().Value);

        Assert.Empty(q.Changes());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A10-A11: Multi-query isolation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A10: Two queries with same Capture but different filters.
    /// Q1 has Without&lt;Dead&gt;, Q2 does not.
    /// Their Changes() and ModifiedChunks must be independent.
    /// </summary>
    [Fact]
    public void A10_Two_queries_same_capture_different_filters_no_cross_talk()
    {
        var world = new World();
        var q1 = world.Track().Capture<HP>().With<HP>().Without<Dead>().Previous();
        var q2 = world.Track().Capture<HP>().With<HP>().Previous();

        var e = world.Create(new HP(100));
        _ = q1.Transitions();
        _ = q2.Transitions();

        // Set HP → both capture
        world.Set(e, new HP(80));
        Assert.NotEmpty(q1.Changes());
        Assert.NotEmpty(q2.Changes());

        // Add Dead → q1 sees Exited + transition snapshot, q2 sees nothing
        world.Add(e, new Dead());
        _ = q1.Changes();  // drain transition snapshot
        var ts1 = q1.Transitions().ToList();
        var ts2 = q2.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Exited, ts1[0].Kind);
        Assert.Empty(ts2);

        // Set HP on {HP, Dead} → q1 filter excludes, q2 includes
        world.Set(e, new HP(60));
        Assert.Empty(q1.Changes());    // BUG? filter should exclude, but may capture anyway
        Assert.NotEmpty(q2.Changes());

        // Remove Dead → q1 sees Entered + snapshot, q2 nothing
        world.Remove<Dead>(e);
        _ = q1.Changes();  // drain transition snapshot
        ts1 = q1.Transitions().ToList();
        ts2 = q2.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
        Assert.Empty(ts2);
    }

    /// <summary>
    /// A11: Two queries capturing different types.
    /// Q1 captures Position, Q2 captures Velocity.
    /// Setting Position bumps Q1's column version; Q2's ModifiedChunks&lt;Velocity&gt;
    /// stays empty (no Velocity write). However, Q2's Changes() DOES capture
    /// Velocity snapshot because OnBeforeWrite/OnAfterWrite capture ALL
    /// captured types for matched archetypes.
    /// </summary>
    [Fact]
    public void A11_Two_queries_different_capture_types_independent()
    {
        var world = new World();
        var qPos = world.Track().Capture<Position>().With<Position>().Previous();
        var qVel = world.Track().Capture<Velocity>().With<Velocity>().Previous();

        var e = world.Create(new Position(0, 0), new Velocity(1, 1));
        _ = qPos.ModifiedChunks<Position>();
        _ = qVel.ModifiedChunks<Velocity>();

        // Set Position → only qPos sees ModifiedChunks bump
        world.Set(e, new Position(5, 5));
        Assert.NotEmpty(qPos.ModifiedChunks<Position>());
        Assert.NotEmpty(qPos.Changes());
        Assert.Empty(qVel.ModifiedChunks<Velocity>());
        // qVel captures Velocity snapshot (unchanged) because entity archetype matches filter
        Assert.NotEmpty(qVel.Changes());

        // Set Velocity → only qVel sees ModifiedChunks bump
        world.Set(e, new Velocity(9, 9));
        Assert.Empty(qPos.ModifiedChunks<Position>());
        Assert.NotEmpty(qVel.ModifiedChunks<Velocity>());
        Assert.NotEmpty(qVel.Changes());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A12: Snapshot round-trip with Previous()
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A12: After Save→Load, a new Track() with Previous() must start
    /// clean — no residual snapshots, no stale transitions.
    /// Previous() must work correctly on the loaded world.
    /// </summary>
    [Fact]
    public void A12_Previous_survives_snapshot_roundtrip_cleanly()
    {
        var world = new World(chunkCapacity: 2);
        var q = world.Track().Capture<HP>().With<HP>().Previous();
        var e = world.Create(new HP(100));

        world.Set(e, new HP(80));
        world.Set(e, new HP(60));
        // Per-entity: only first Old and last New
        Assert.Single(q.Changes());

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        var loadedQ = loaded.Track().Capture<HP>().With<HP>().Previous();
        Assert.Empty(loadedQ.Changes());
        Assert.Empty(loadedQ.Transitions());

        loaded.Set(e, new HP(40));
        var cs = loadedQ.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(e, cs[0].Entity);
        Assert.Equal(60, cs[0].Old.Get<HP>().Value);
        Assert.Equal(40, cs[0].New.Get<HP>().Value);

        Assert.Empty(loadedQ.Changes());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A13: OnBeforeTransition crash when captured type absent
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A13: When Previous() is enabled and a captured type is NOT present
    /// in the entity's archetype, OnBeforeTransition reads the component
    /// via GetComponentIndexFast → GetComponentBytes, which must handle
    /// missing columns gracefully.
    ///
    /// The entity has HP (so filter matches) but NOT Mana (also captured).
    /// A structural change (Add Velocity) fires OnBeforeTransition, which
    /// iterates all captured types — including Mana which is absent from
    /// {Position, HP}.
    ///
    /// BUG: GetComponentIndexFast(Mana) on {Position, HP} returns -1,
    /// then GetComponentBytes(-1, row) throws IndexOutOfRangeException.
    ///
    /// Expected: no crash, Mana column silently skipped.
    /// Actual:   IndexOutOfRangeException.
    /// </summary>
    [Fact]
    public void A13_OnBeforeTransition_does_not_crash_when_captured_type_absent()
    {
        var world = new World();

        var q = world.Track().Capture<HP>().Capture<Mana>().With<HP>().Previous();

        // Entity has Position and HP → matches filter, but no Mana
        var e = world.Create(new Position(1, 1), new HP(100));

        // BUG: Add<Velocity> fires OnBeforeTransition which tries to read
        // Mana from {Position, HP} — Mana not present → crash
        world.Add(e, new Velocity(2, 2));

        // Verify Changes() still works correctly afterward
        world.Set(e, new HP(50));
        var cs = q.Changes().ToList();
        Assert.NotEmpty(cs);
        Assert.Equal(e, cs[0].Entity);
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A14: EntityAccessor.Set skips Previous() (by design)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A14: EntityAccessor.Set() deliberately bypasses DispatchBeforeWrite/
    /// DispatchAfterWrite. It bumps the column version (so ModifiedChunks
    /// sees the write) but Previous() snapshot capture is NOT triggered.
    /// This is documented behavior — use World.Set or CommandStream.Set
    /// when Previous() capture is needed.
    /// </summary>
    [Fact]
    public void A14_EntityAccessor_Set_bumps_version_but_skips_Previous()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();
        var e = world.Create(new HP(100));
        _ = q.ModifiedChunks<HP>();

        var accessor = world.Access(e);
        accessor.Set(new HP(75));

        Assert.NotEmpty(q.ModifiedChunks<HP>());
        Assert.Empty(q.Changes());

        // Verify via World.Set — this DOES trigger Previous()
        world.Set(e, new HP(50));
        var cs = q.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(75, cs[0].Old.Get<HP>().Value);
        Assert.Equal(50, cs[0].New.Get<HP>().Value);
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION A15: ModifiedChunks + Transitions independence
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A15: Draining Transitions() must not affect ModifiedChunks cursor
    /// and vice versa. Both enumeration methods are independent.
    /// Create writes initial component values via SetComponentAtTyped
    /// which bumps column version — ModifiedChunks sees it and must
    /// be drained separately.
    /// </summary>
    [Fact]
    public void A15_ModifiedChunks_and_Transitions_independent()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>();

        var e = world.Create(new HP(100));
        _ = q.ModifiedChunks<HP>();
        _ = q.Transitions();

        world.Set(e, new HP(80));
        Assert.NotEmpty(q.ModifiedChunks<HP>());

        // Create writes initial HP via SetComponentAtTyped → bumps version
        world.Create(new HP(50));
        _ = q.ModifiedChunks<HP>();  // drain Create's write

        var ts = q.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);

        Assert.Empty(q.ModifiedChunks<HP>());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION B16: CommandStream Submit with Previous()
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// B16: Submit (same-world deferred execution) of Create+Add+Set with
    /// Previous(). Pending entity operations are folded to final state —
    /// the entity is materialized directly with HP=60. No individual
    /// Set hooks fire during batch materialization.
    ///
    /// Transitions: the entity enters the filter (final state has HP).
    /// Changes(): empty (no intermediate write hooks).
    /// </summary>
    [Fact]
    public void B16_Submit_CreateAddSet_with_Previous()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();

        // Defer everything via CommandStream
        var cs = new CommandStream(world);
        var e = cs.Create();
        cs.Add(e, new HP(100));
        cs.Set(e, new HP(80));
        cs.Set(e, new HP(60));
        cs.Submit();

        // Submit materializes the pending entity with final state HP=60.
        // The entity enters the filter With<HP>.
        var ts = q.Transitions().ToList();
        Assert.Contains(ts, t => t.Kind == TransitionKind.Entered);

        // Changes() is empty: pending batch materialization does not
        // fire individual write hooks. All ops are folded to final state.
        Assert.Empty(q.Changes());
    }

    /// <summary>
    /// B16b: Submit of mixed Add/Remove with Previous() — pending entity
    /// operations are folded to final state.
    ///
    /// Entity: Create + Add HP(100) + Remove HP → final: no HP.
    ///   → Never enters the filter. No transitions, no changes.
    ///
    /// This is by design: the CommandStream batch buffer retains only the
    /// final component signature per pending entity. A Create+Add+Remove
    /// sequence that nets to "no HP" is indistinguishable from never
    /// having had HP.
    /// </summary>
    [Fact]
    public void B16b_Submit_mixed_Add_Remove_with_Previous()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();

        var cs = new CommandStream(world);
        var e = cs.Create();
        cs.Add(e, new HP(100));
        cs.Remove<HP>(e);
        cs.Submit();

        // Entity ends with no HP → never entered the filter
        Assert.Empty(q.Transitions());
        Assert.Empty(q.Changes());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION B17: Interleaved Changes() calls
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// B17: Multiple Changes() calls interleaved with Set ops.
    /// Each Changes() call must return only entries accumulated since
    /// the previous call (auto-clear). No entries should be lost or doubled.
    /// </summary>
    [Fact]
    public void B17_Interleaved_Changes_calls_accumulate_correctly()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();
        var e = world.Create(new HP(100));

        // First Changes should be empty (no Set since Create, Create was before Track?)
        // Actually Create happens after Track(), so the initial HP write bumps version
        // but doesn't go through Set. Drain:
        _ = q.Changes();
        _ = q.Transitions();

        // Set 1
        world.Set(e, new HP(90));
        var c1 = q.Changes().ToList();
        Assert.Single(c1);
        Assert.Equal(100, c1[0].Old.Get<HP>().Value);
        Assert.Equal(90, c1[0].New.Get<HP>().Value);

        // Set 2
        world.Set(e, new HP(80));
        var c2 = q.Changes().ToList();
        Assert.Single(c2);
        Assert.Equal(90, c2[0].Old.Get<HP>().Value);
        Assert.Equal(80, c2[0].New.Get<HP>().Value);

        // Set 3 + Set 4 without draining → per-entity: 1 entry
        world.Set(e, new HP(70));
        world.Set(e, new HP(60));
        var c34 = q.Changes().ToList();
        Assert.Single(c34);
        Assert.Equal(80, c34[0].Old.Get<HP>().Value);
        Assert.Equal(60, c34[0].New.Get<HP>().Value);

        // Final drain: empty
        Assert.Empty(q.Changes());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION B18: Previous() + ModifiedChunks independent for Sets
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// B18: When Previous() is enabled, ModifiedChunks must still work
    /// independently. Calling Changes() must not affect the ModifiedChunks
    /// cursor, and vice versa.
    /// </summary>
    [Fact]
    public void B18_Previous_and_ModifiedChunks_independent()
    {
        var world = new World();
        var q = world.Track().Capture<HP>().With<HP>().Previous();
        var e = world.Create(new HP(100));
        _ = q.ModifiedChunks<HP>();
        _ = q.Changes();
        _ = q.Transitions();

        // Set → both ModifiedChunks and Changes should see it
        world.Set(e, new HP(80));

        // Drain Changes first
        Assert.Single(q.Changes());
        // ModifiedChunks should still have the chunk
        Assert.NotEmpty(q.ModifiedChunks<HP>());

        // Drain ModifiedChunks
        Assert.Empty(q.ModifiedChunks<HP>());

        // Second Set → verify both still work
        world.Set(e, new HP(60));
        Assert.Single(q.Changes());
        Assert.NotEmpty(q.ModifiedChunks<HP>());
    }

    // ═══════════════════════════════════════════════════════════════
    // SECTION B19: non-deterministic OnBeforeTransition crash
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// B19: Regression test for the old missing-guard crash.
    /// OnBeforeTransition must tolerate captured types absent from the
    /// entity's current archetype and leave their snapshot bytes as zeros.
    /// </summary>
    [Fact]
    public void B19_OnBeforeTransition_no_guard_for_missing_captured_type()
    {
        var world = new World();

        var q = world.Track().Capture<Mana>().With<Mana>().Previous();

        // Entity starts with Position and Velocity, no Mana
        var e = world.Create(new Position(1, 1), new Velocity(0, 0));

        // Adding HP triggers structural change. Old archetype {Position, Velocity}
        // does not contain Mana; the guard must skip it instead of crashing.
        world.Add(e, new HP(100));

        // Entity should still not match With<Mana>(), so no transitions or
        // changes are produced.
        Assert.Empty(q.Transitions());
        Assert.Empty(q.Changes());
    }
}
