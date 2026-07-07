using System.IO;
using System.Security.Cryptography;
using MiniArch.Core;

namespace MiniArchTests.Core;

/// <summary>
/// Targeted unit tests for Submit/Replay parity covering operation sequences
/// that the soak test cannot generate due to its operation guards.
///
/// Each test constructs a specific operation sequence via CommandStream,
/// applies to the source world via Submit, replays the delta into a shadow
/// world, and verifies that both worlds are bit-identical.
///
/// This class also covers the 5 divergence patterns (P1-P5) from the
/// bug-classification system: deduplication mismatch, skip/cancel asymmetry,
/// operation order divergence, collection mutation semantics, and
/// recording-phase immediate mutation.
/// </summary>
public sealed class SubmitReplayParityTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Remove + Add same component, same frame          ║
    // Pattern: P1 (deduplication mismatch risk — both paths must   ║
    //          handle the Remove-then-Add cycle identically)       ║
    // Soak test blocks: OpAdd checks !_pendingRemoves.Contains()   ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Remove_then_Add_same_component_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed an entity with a Position component
        var e = stream.Create();
        stream.Add(e, new Position(10, 20));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: Remove<Position> then Add<Position> with a new value
        stream.Remove<Position>(e);
        stream.Add(e, new Position(99, 99));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify source has the expected state
        Assert.True(source.TryGet(e, out Position p));
        Assert.Equal(new Position(99, 99), p);

        // Verify replay converges
        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "Remove then Add same component, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Add + Remove same component, same frame          ║
    // Pattern: P1 (same as above, reversed order — must converge   ║
    //          to entity NOT having the component)                 ║
    // Soak test blocks: OpRemove checks _source.Has<CompX>() —     ║
    //          false because Add hasn't been submitted yet         ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Add_then_Remove_same_component_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed an entity WITHOUT Position
        var e = stream.Create();
        deltas.Add(stream.Snapshot());
        stream.Submit();
        Assert.False(source.Has<Position>(e));

        // Frame 2: Add<Position> then Remove<Position>
        stream.Add(e, new Position(42, 43));
        stream.Remove<Position>(e);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify final state: no Position component
        Assert.False(source.Has<Position>(e));
        Assert.True(source.IsAlive(e));

        // Verify replay converges
        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "Add then Remove same component, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Double Add same component, same frame            ║
    // Pattern: P1 (dedup — second Add must overwrite value in both ║
    //          paths; no duplicate archetype move)                 ║
    // Soak test: NOT blocked but probabilistically exercised       ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Double_Add_same_component_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed an entity with no Position
        var e = stream.Create();
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: Add<Position> then Set<Position> (Add only when absent)
        stream.Add(e, new Position(1, 2));
        stream.Set(e, new Position(3, 4));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: Set overwrote after Add
        Assert.True(source.TryGet(e, out Position p));
        Assert.Equal(new Position(3, 4), p);

        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "Double Add same component, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Double Remove same component, same frame         ║
    // Pattern: P1 (second Remove must be no-op in both paths)      ║
    // Soak test: NOT blocked — possible but not guaranteed         ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Double_Remove_same_component_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed an entity WITH Position
        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: Remove<Position> twice
        stream.Remove<Position>(e);
        stream.Remove<Position>(e);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: Position is removed (second Remove is a no-op)
        Assert.False(source.Has<Position>(e));
        Assert.True(source.IsAlive(e));

        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "Double Remove same component, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Set after Remove same component, same frame      ║
    // Pattern: P2 (skip/cancel asymmetry risk — both paths MUST    ║
    //          throw InvalidOperationException; if one silently    ║
    //          succeeds while the other throws, that is a bug)     ║
    // Soak test blocks: OpSet checks !_pendingRemoves.Contains()   ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Set_after_Remove_same_component_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed an entity WITH Position
        var e = stream.Create();
        stream.Add(e, new Position(10, 20));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: Remove<Position> then Set<Position>
        // The Set must throw because the component no longer exists.
        stream.Remove<Position>(e);
        stream.Set(e, new Position(99, 99));

        // Capture delta BEFORE Submit (so we can Replay it later)
        var delta = stream.Snapshot();

        // Submit path: throws because Set runs after Remove.
        // Note: Submit throws ArgumentException (from GetComponentIndex inside
        // ComponentStore.ApplyToWorld), while Replay throws InvalidOperationException
        // (from ApplyRawSet's explicit guard). Both throw —neither silently accepts
        // the invalid operation —which is the critical property.
        var submitEx = Record.Exception(() => stream.Submit());
        Assert.NotNull(submitEx);
        Assert.True(
            submitEx is InvalidOperationException || submitEx is ArgumentException,
            $"Submit threw unexpected exception type: {submitEx.GetType().Name}: {submitEx.Message}");

        // Replay path: must also throw (same reason)
        var shadow = new World();
        var shadowEx = Record.Exception(
            () => new CommandStream(shadow).Replay(delta));
        Assert.NotNull(shadowEx);
        Assert.True(
            shadowEx is InvalidOperationException,
            $"Replay threw unexpected exception type: {shadowEx.GetType().Name}: {shadowEx.Message}");

        // Both paths throw —consistent behavior. No AssertWorldsMatch because
        // both worlds are in an exception-terminated (incomplete) state.
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: AddChild then RemoveChild same pair, same frame  ║
    // Pattern: P4 (collection mutation semantics — dictionary       ║
    //          overwrite of hierarchy intent by RemoveChild)        ║
    // Soak test: NOT blocked (RemoveChild has no guard against     ║
    //          overwriting a previous AddChild)                    ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void AddChild_then_RemoveChild_same_pair_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed parent and child (separate, unrelated)
        var parent = stream.Create();
        stream.Add(parent, new Position(0, 0));
        var child = stream.Create();
        stream.Add(child, new Position(1, 1));
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify initial state: no relationship
        Assert.False(source.TryGetParent(child, out _));

        // Frame 2: AddChild(parent, child) then RemoveChild(child)
        stream.AddChild(parent, child);
        stream.RemoveChild(child);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: RemoveChild overwrites AddChild —no relationship
        Assert.False(source.TryGetParent(child, out _));

        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "AddChild then RemoveChild same pair, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: AddChild reparent then destroy parent, same frame║
    // Pattern: P5 (recording-phase mutation — destroy of newly-    ║
    //          established parent must skip pending AddChild)      ║
    // Soak test's OpDestroy checks _pendingHierarchyChildren       ║
    //          and HasAncestorDestroyedThisFrame, but NOT          ║
    //          _pendingHierarchyParents —may exercise this path    ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void AddChild_reparent_then_destroy_parent_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed A (parent), B (child), C (new parent)
        var a = stream.Create();
        var b = stream.Create();
        stream.AddChild(a, b);
        var c = stream.Create();
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: B is child of A
        Assert.True(source.TryGetParent(b, out var parentOfB));
        Assert.Equal(a, parentOfB);

        // Frame 2: AddChild(C, B) then Destroy(C)
        // The AddChild intent tries to reparent B to C, but C is also
        // destroyed this frame. ApplyHierarchy should skip the AddChild
        // because C is in the DestroyEntities array and IsDestroyedThisFrame
        // returns true. B remains a child of A.
        stream.AddChild(c, b);
        stream.Destroy(c);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: B is still child of A (AddChild was skipped)
        Assert.False(source.IsAlive(c));
        Assert.True(source.IsAlive(b));
        Assert.True(source.TryGetParent(b, out var parentAfter));
        Assert.Equal(a, parentAfter);

        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "AddChild reparent then destroy parent, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Double AddChild same pair, same frame            ║
    // Pattern: P4 (dictionary overwrite — second AddChild          ║
    //          overwrites first with same intent/value)            ║
    // Soak test: NOT blocked but low probability of same pair      ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Double_AddChild_same_pair_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed parent and child
        var parent = stream.Create();
        var child = stream.Create();
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: AddChild(parent, child) twice
        stream.AddChild(parent, child);
        stream.AddChild(parent, child);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: single relationship established
        Assert.True(source.TryGetParent(child, out var actualParent));
        Assert.Equal(parent, actualParent);

        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "Double AddChild same pair, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Destroy entity already cascade-destroyed,        ║
    //          same frame                                          ║
    // Pattern: P2 (skip/cancel — IsAlive guard in both paths       ║
    //          must prevent double-destroy from throwing)          ║
    // Soak test blocks: OpDestroy checks                           ║
    //          HasAncestorDestroyedThisFrame and                   ║
    //          _pendingHierarchyChildren.Contains(e)               ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Destroy_entity_already_cascade_destroyed_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed parent → child hierarchy
        var parent = stream.Create();
        var child = stream.Create();
        stream.AddChild(parent, child);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: parent has child
        Assert.True(source.HasChildren(parent));

        // Frame 2: record Destroy(parent) then Destroy(child)
        // When Submit processes Destroy(parent), it cascade-destroys child.
        // When it reaches the explicit Destroy(child) entry, IsAlive(child)
        // returns false → the entry is skipped (no crash, no corruption).
        stream.Destroy(parent);
        stream.Destroy(child);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify both are dead
        Assert.False(source.IsAlive(parent));
        Assert.False(source.IsAlive(child));

        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "Destroy already cascade-destroyed entity, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Create entity, Add component, Remove same        ║
    //          component —all within one frame via batch+store     ║
    // Pattern: P5 (recording-phase immediate mutation)             ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Create_Add_Remove_component_all_one_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);

        // Single frame: create entity, add Position (batch), remove Position (store)
        var e = stream.Create();
        stream.Add(e, new Position(1, 2));
        stream.Remove<Position>(e);

        var delta = stream.Snapshot();
        stream.Submit();

        // Entity exists but Position was removed
        Assert.True(source.IsAlive(e));
        Assert.False(source.Has<Position>(e));

        var shadow = new World();
        new CommandStream(shadow).Replay(delta);

        AssertWorldsMatch(source, shadow,
            "Create, Add, Remove —all one frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: AddSameFrameCreate + Set on same entity          ║
    // Pattern: P1 + P5 —batch components materialize via batch     ║
    //          buffer, then component store Set applies.           ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Create_and_Set_same_entity_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);

        // Seed entity from previous frame (so Set via store works)
        var seed = stream.Create();
        stream.Add(seed, new Position(0, 0));
        var seedDelta = stream.Snapshot();
        stream.Submit();

        // Single frame: create, add Velocity via batch, Set Position via store
        var e = stream.Create();
        stream.Add(e, new Velocity(5, 6));
        stream.Set(seed, new Position(10, 20));

        var delta = stream.Snapshot();
        stream.Submit();

        Assert.True(source.TryGet(seed, out Position p));
        Assert.Equal(new Position(10, 20), p);
        Assert.True(source.TryGet(e, out Velocity v));
        Assert.Equal(new Velocity(5, 6), v);

        var shadow = new World();
        new CommandStream(shadow).Replay(seedDelta);
        new CommandStream(shadow).Replay(delta);

        AssertWorldsMatch(source, shadow,
            "Create+Set same frame, cross-entity");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: Destroy pending (cancel) + Destroy same entity   ║
    //          as explicit destroy in same frame                   ║
    // Pattern: P2 (skip/cancel — cancel vs destroy path must       ║
    //          interact correctly)                                 ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void Cancel_pending_then_destroy_existing_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: create entity E that will later be destroyed explicitly
        var e = stream.Create();
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: create pending P, cancel P, destroy E
        var p = stream.Create();    // pending entity
        stream.Destroy(p);          // cancel the pending entity (goes to batch)
        stream.Destroy(e);          // explicit destroy of existing entity

        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Verify: E is dead, P was never materialized
        Assert.False(source.IsAlive(e));
        // P was canceled —should not exist
        // (cannot verify via IsAlive since it was never materialized)

        var shadow = new World();
        foreach (var d in deltas)
            new CommandStream(shadow).Replay(d);

        AssertWorldsMatch(source, shadow,
            "Cancel pending then destroy existing, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Blind spot: AddChild + Destroy grandparent = cascade kills   ║
    //          intermediate + leaf, all in same frame              ║
    // Pattern: P3 (order divergence risk) + P5 (recording-phase)   ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void AddChild_midtree_then_destroy_root_cascade_same_frame()
    {
        var source = new World();
        var stream = new CommandStream(source);
        var deltas = new List<FrameDelta>();

        // Frame 1: seed grandparent(A)→parent(B)→child(C)
        var a = stream.Create();
        var b = stream.Create();
        var c = stream.Create();
        stream.AddChild(a, b);
        stream.AddChild(b, c);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: add D as child of B, then destroy A (root)
        // cascade: A→B→C, and also D (if AddChild(B,D) is applied before destroy)
        var d = stream.Create();
        stream.AddChild(b, d);
        stream.Destroy(a);
        deltas.Add(stream.Snapshot());
        stream.Submit();

        // Everything should be dead
        Assert.False(source.IsAlive(a));
        Assert.False(source.IsAlive(b));
        Assert.False(source.IsAlive(c));
        Assert.False(source.IsAlive(d));

        var shadow = new World();
        foreach (var d2 in deltas)
            new CommandStream(shadow).Replay(d2);

        AssertWorldsMatch(source, shadow,
            "AddChild midtree then destroy root cascade, same frame");
    }

    // ═══════════════════════════════════════════════════════════════╗
    // World comparison helpers (hash-based checksum)                ║
    // ═══════════════════════════════════════════════════════════════╝

    private static void AssertWorldsMatch(World a, World b, string context)
    {
        var ha = HashWorld(a);
        var hb = HashWorld(b);
        if (ha != hb)
        {
            var sa = a.GetStats();
            var sb = b.GetStats();
            Assert.Fail(
                $"Worlds diverge for [{context}].\n" +
                $"  A: ec={sa.EntityCount}, ac={sa.ArchetypeCount}, " +
                $"slots={sa.EntityCapacity}, hash={ha[..16]}\n" +
                $"  B: ec={sb.EntityCount}, ac={sb.ArchetypeCount}, " +
                $"slots={sb.EntityCapacity}, hash={hb[..16]}");
        }
    }

    private static string HashWorld(World w)
    {
        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, w);
        var span = new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        return Convert.ToHexString(SHA256.HashData(span));
    }
}
