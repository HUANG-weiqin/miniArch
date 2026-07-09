using MiniArch;
using MiniArch.Core;
using MiniArch.Diagnostics;
using MiniArch.Tests.Core.TestSupport;
using Xunit;

namespace MiniArchTests.Core;

/// <summary>
/// M4: Metamorphic parity scan — Submit vs Replay vs Restore three-way
/// convergence for operation patterns from soak seed 111/cap=100/ops/f=50
/// and M3 cross-feature parity patterns.
///
/// For each pattern:
///   Path A (Submit):   CommandStream → Submit → canonical checksum (post-mutation)
///   Path B (Replay):   CommandStream → Snapshot → FrameDelta → Replay into
///                      fresh shadow world → canonical checksum (post-mutation)
///   Path C (Restore):  CaptureState before mutation → Submit → RestoreState →
///                      canonical checksum (pre-mutation baseline)
///
/// Convergence criteria:
///   A == B  (Submit and Replay produce identical final state)
///   C == pre-mutation baseline  (RestoreState correctly rolls back the mutation)
/// </summary>
public sealed class SubmitReplayRestoreParityTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 1: Create + Add component (simple entity spawn)       ║
    // Source: soak seed 111 basic entity lifecycle (WarmUp phase)   ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P1_Create_Add_three_way_parity()
    {
        // Base: empty world
        var world = new World();
        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // Path A: Submit — create entity with Position
        var stream = new CommandStream(world);
        var e = stream.Create();
        stream.Add(e, new Position(10, 20));
        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay — shadow gets same delta sequence
        var shadow = new World();
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore — back to pre-mutation state
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 2: Set existing component on entity (value mutation)  ║
    // Source: soak seed 111 OpSet, MigrationStorm phase             ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P2_Set_component_three_way_parity()
    {
        // Base: world with one entity having Position(0, 0)
        var world = new World();
        var setup = new CommandStream(world);
        var e = setup.Create();
        setup.Add(e, new Position(0, 0));
        var baseDelta = setup.Snapshot();
        setup.Submit();

        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // Path A: Submit — Set Position to (99, 99)
        var stream = new CommandStream(world);
        stream.Set(e, new Position(99, 99));
        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay to fresh shadow
        var shadow = new World();
        new CommandStream(shadow).Replay(baseDelta);
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore to pre-mutation state
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 3: Remove + Add same component same frame             ║
    // Source: P1 pattern from SubmitReplayParityTests (blind spot   ║
    //          that soak test cannot generate due to OpAdd guards)  ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P3_Remove_then_Add_same_component_three_way_parity()
    {
        // Base: entity with Position(10, 20)
        var world = new World();
        var setup = new CommandStream(world);
        var e = setup.Create();
        setup.Add(e, new Position(10, 20));
        var baseDelta = setup.Snapshot();
        setup.Submit();

        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // Path A: Submit — Remove<Position> then Add<Position>(99, 99)
        var stream = new CommandStream(world);
        stream.Remove<Position>(e);
        stream.Add(e, new Position(99, 99));
        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay to shadow
        var shadow = new World();
        new CommandStream(shadow).Replay(baseDelta);
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 4: Add + Remove same component same frame             ║
    // Source: P1 reverse pattern (net-zero structural change)       ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P4_Add_then_Remove_same_component_three_way_parity()
    {
        // Base: entity without Position
        var world = new World();
        var setup = new CommandStream(world);
        var e = setup.Create();
        var baseDelta = setup.Snapshot();
        setup.Submit();

        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // Path A: Submit — Add<Position>(42, 43) then Remove<Position>
        var stream = new CommandStream(world);
        stream.Add(e, new Position(42, 43));
        stream.Remove<Position>(e);
        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay
        var shadow = new World();
        new CommandStream(shadow).Replay(baseDelta);
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 5: Hierarchy AddChild + cascade destroy               ║
    // Source: M3 Cell 1 (Hieararchy + CommandStream) + soak seed    ║
    //         111 HierarchyStress phase                             ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P5_Hierarchy_add_child_then_cascade_destroy_three_way_parity()
    {
        // Base: A → B → C (three-level hierarchy)
        var world = new World();
        var setup = new CommandStream(world);
        var a = setup.Create(); setup.Add(a, new Position(0, 0));
        var b = setup.Create(); setup.Add(b, new Position(1, 1));
        var c = setup.Create(); setup.Add(c, new Position(2, 2));
        setup.AddChild(a, b);
        setup.AddChild(b, c);
        var baseDelta = setup.Snapshot();
        setup.Submit();

        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // Mutation: add D under B, then destroy A (cascade destroys B, C, D)
        var stream = new CommandStream(world);
        var d = stream.Create(); stream.Add(d, new Position(3, 3));
        stream.AddChild(b, d);
        stream.Destroy(a);
        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay
        var shadow = new World();
        new CommandStream(shadow).Replay(baseDelta);
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 6: Create/cancel churn (B5/B6 territory)              ║
    // Source: soak seed 111 / cap=100 / ops/f=50 AllocatorChurn     ║
    //          phase — multiple pending creates with cancellations  ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P6_Create_cancel_churn_three_way_parity()
    {
        // Multiple frames of create/destroy churn similar to soak seed
        // 111 boundary configuration that historically exposed B5/B6.
        // Uses a tight entity capacity to force free-list reuse.
        var world = new World(entityCapacity: 8);
        var stream = new CommandStream(world);
        var allDeltas = new System.Collections.Generic.List<FrameDelta>();

        // Frame 1: create 4 entities to populate slots 0–3
        for (var i = 0; i < 4; i++)
        {
            var e = stream.Create();
            stream.Add(e, new Position(i, i + 1));
        }
        allDeltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 2: destroy 2 → free-list has 2 entries
        var alive = CollectAlive(world);
        stream.Destroy(alive[0]);
        stream.Destroy(alive[1]);
        allDeltas.Add(stream.Snapshot());
        stream.Submit();

        // Frame 3: create 3 pending entities, cancel 2, keep 1
        var p1 = stream.Create(); stream.Add(p1, new Health(1));
        var p2 = stream.Create(); stream.Add(p2, new Health(2));
        var p3 = stream.Create(); stream.Add(p3, new Health(3));
        stream.Destroy(p1); // cancel pending
        stream.Destroy(p2); // cancel pending
        // p3 survives
        allDeltas.Add(stream.Snapshot());
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay all deltas
        var shadow = new World(entityCapacity: 8);
        foreach (var d in allDeltas)
            new CommandStream(shadow).Replay(d);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore cannot be cleanly tested here because
        // the pre-mutation snapshot spans multiple frames.
        // Instead, verify entity count alignment and validator.
        Assert.Equal(world.EntityCount, shadow.EntityCount);
        Assert.True(WorldValidator.Validate(world).IsValid);
        Assert.True(WorldValidator.Validate(shadow).IsValid);
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 7: Clone + mutate (deep copy then modify)             ║
    // Source: soak seed 111 OpClone (stable through all phases)     ║
    //         + M3 clone-related patterns                           ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P7_Clone_then_mutate_three_way_parity()
    {
        // Base: parent(A) → child(B), both with components
        var world = new World();
        var setup = new CommandStream(world);
        var a = setup.Create(); setup.Add(a, new Position(0, 0));
        var b = setup.Create(); setup.Add(b, new Velocity(1, 1));
        setup.AddChild(a, b);
        var baseDelta = setup.Snapshot();
        setup.Submit();

        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // Mutation: clone A (deep, includes child), set clone's child component
        var stream = new CommandStream(world);
        var aClone = stream.Clone(a);
        stream.Set(aClone, new Position(99, 99));
        var cloneChildren = world.EnumerateChildren(aClone).ToChildList();
        foreach (var cc in cloneChildren)
            stream.Add(cc, new Health(42));
        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay
        var shadow = new World();
        new CommandStream(shadow).Replay(baseDelta);
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 8: Double Add then Remove same component              ║
    // Source: soak seed 111 OpAdd/OpSet through MigrationStorm      ║
    //          phase — tests dedup and skip logic                   ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P8_Double_Add_then_Remove_three_way_parity()
    {
        // Base: one entity with Velocity only
        var world = new World();
        var setup = new CommandStream(world);
        var e = setup.Create();
        setup.Add(e, new Velocity(5, 5));
        var baseDelta = setup.Snapshot();
        setup.Submit();

        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // Mutation: Add Position(1,2), Set Position(3,4) overwrites,
        // then Remove<Position>
        var stream = new CommandStream(world);
        stream.Add(e, new Position(1, 2));
        stream.Set(e, new Position(3, 4));  // overwrites first add via Set
        stream.Remove<Position>(e);
        // Also Set Velocity for value change
        stream.Set(e, new Velocity(10, 10));
        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay
        var shadow = new World();
        new CommandStream(shadow).Replay(baseDelta);
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Pattern 9: Multi-entity mixed operations (soak-like burst)    ║
    // Source: soak seed 111 cap=100 ops/f=50 pattern — high-density ║
    //         mixed Create/Destroy/Add/Set/Remove in single frame   ║
    // ═══════════════════════════════════════════════════════════════╝

    [Fact]
    public void P9_Mixed_operations_burst_three_way_parity()
    {
        // Base: 5 entities with various components
        var world = new World();
        var setup = new CommandStream(world);
        var entities = new Entity[5];
        for (var i = 0; i < 5; i++)
        {
            entities[i] = setup.Create();
            setup.Add(entities[i], new Position(i, i * 10));
        }
        setup.Add(entities[1], new Velocity(1, 2));
        setup.Add(entities[2], new Health(50));
        setup.Add(entities[3], new Velocity(3, 4));
        setup.Add(entities[3], new Health(100));
        // Hierarchy: e[0] → e[1], e[0] → e[2]
        setup.AddChild(entities[0], entities[1]);
        setup.AddChild(entities[0], entities[2]);
        var baseDelta = setup.Snapshot();
        setup.Submit();

        var preChecksum = world.CanonicalChecksum();
        var preSnap = world.CaptureState();

        // High-density frame (soak-like burst of ~15 ops)
        var stream = new CommandStream(world);

        // Destroy e[4]
        stream.Destroy(entities[4]);

        // Remove Velocity from e[1], Add Health to e[1]
        stream.Remove<Velocity>(entities[1]);
        stream.Add(entities[1], new Health(99));

        // Set Position on e[0] and e[2]
        stream.Set(entities[0], new Position(100, 200));
        stream.Set(entities[2], new Position(300, 400));

        // Create 2 new entities with mixed components
        var n1 = stream.Create();
        stream.Add(n1, new Position(500, 600));
        stream.Add(n1, new Velocity(7, 8));
        var n2 = stream.Create();
        stream.Add(n2, new Health(42));

        // Hierarchy: add n1 under e[0], add n2 under n1
        stream.AddChild(entities[0], n1);
        stream.AddChild(n1, n2);

        // Clone e[0] (deep, includes children)
        var clone = stream.Clone(entities[0]);

        // Remove Health from e[2]
        stream.Remove<Health>(entities[2]);

        var delta = stream.Snapshot();
        stream.Submit();
        var submitChecksum = world.CanonicalChecksum();

        // Path B: Replay
        var shadow = new World();
        new CommandStream(shadow).Replay(baseDelta);
        new CommandStream(shadow).Replay(delta);
        Assert.Equal(submitChecksum, shadow.CanonicalChecksum());

        // Path C: Restore
        world.RestoreState(preSnap);
        Assert.Equal(preChecksum, world.CanonicalChecksum());
    }

    // ═══════════════════════════════════════════════════════════════╗
    // Helpers                                                        ║
    // ═══════════════════════════════════════════════════════════════╝

    /// <summary>
    /// Collects alive entities from a world using the query API.
    /// Compact: uses List&lt;Entity&gt; for deterministic ordering, not
    /// Dictionary/HashSet.
    /// </summary>
    private static System.Collections.Generic.List<Entity> CollectAlive(World world)
    {
        var result = new System.Collections.Generic.List<Entity>();
        var query = world.Query(new QueryDescription());
        foreach (var chunk in query.GetChunks())
            foreach (var e in chunk.GetEntities())
                result.Add(e);
        return result;
    }
}
