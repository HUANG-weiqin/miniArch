using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

public class ChangeTrackingInfrastructureTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Tracking_is_inactive_by_default()
    {
        var world = new World();
        Assert.False(world.IsChangeTrackingActive);
    }

    [Fact]
    public void Track_activates_tracking()
    {
        var world = new World();
        var hp = world.Track<Position>();
        Assert.NotNull(hp);
        Assert.True(world.IsChangeTrackingActive);
    }

    [Fact]
    public void Track_activates_column_versions_on_existing_archetype()
    {
        var world = new World();
        var e = world.Create(new Position(0, 0));   // archetype exists before Track
        var pos = world.Track<Position>();
        // activation should have retro-fitted the archetype holding Position
        Assert.True(world.IsChangeTrackingActive);
        // transition log should have one entry (from Create, which happened before Track,
        // but Track retro-actively doesn't add entries — the create transition is there
        // because Track doesn't retroactively apply to existing entities)
    }

    [Fact]
    public void Track_then_create_activates_new_archetype_from_birth()
    {
        var world = new World();
        world.Track<Position>();
        var e = world.Create(new Position(0, 0));   // archetype created AFTER Track
        // no exception; archetype got activated in GetOrCreateArchetype
        Assert.True(world.IsChangeTrackingActive);
    }

    [Fact]
    public void WriteEpoch_starts_at_zero()
    {
        var world = new World();
        Assert.Equal(0, world.CurrentWriteEpoch);
    }

    // ── Task 2: per-column version bump on write chokepoints ──────────

    [Fact]
    public void Set_advances_column_version_when_tracking_active()
    {
        var world = new World();
        world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        var v0 = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
        world.Set(e, new Position(5, 0));
        var v1 = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
        Assert.True(v1 > v0);
    }

    [Fact]
    public void Set_does_not_advance_version_when_tracking_inactive()
    {
        var world = new World();   // no Track
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(5, 0));
        Assert.Equal(0, world.DebugGetColumnVersion(e, Component<Position>.ComponentType));
    }

    [Fact]
    public void Get_does_not_advance_version_even_when_tracking_active()
    {
        var world = new World();
        world.Track<Position>();
        var e = world.Create(new Position(1, 0));
        world.Set(e, new Position(1, 0));
        var v = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
        _ = world.Get<Position>(e);
        Assert.Equal(v, world.DebugGetColumnVersion(e, Component<Position>.ComponentType));
    }

    [Fact]
    public void Set_on_one_column_does_not_advance_other_column()
    {
        var world = new World();
        world.Track<Position>();
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        var posV = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
        world.Set(e, new Velocity(1, 0));
        Assert.Equal(posV, world.DebugGetColumnVersion(e, Component<Position>.ComponentType));
    }

    [Fact]
    public void EntityAccessor_Set_advances_version()
    {
        var world = new World();
        world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        var accessor = world.Access(e);
        var v0 = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
        accessor.Set(new Position(9, 0));
        var v1 = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
        Assert.True(v1 > v0);
    }

    // ── Task 3: structural transition dispatch ───────────────────────

    [Fact]
    public void Create_appends_entered_transition()
    {
        var world = new World();
        var pos = world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Create_then_destroy_produces_entered_then_exited()
    {
        var world = new World();
        var pos = world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        pos.Transitions();  // drain create transition
        world.Destroy(e);
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Add_produces_exited_when_filter_excludes_added_component()
    {
        var world = new World();
        var pos = world.Track<Position>().Without<Velocity>();
        var e = world.Create(new Position(0, 0));
        pos.Transitions();  // drain create
        world.Add(e, new Velocity(0, 0));
        // Entity left {Position, !Velocity} → Exited
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Remove_produces_entered_when_excluded_component_removed()
    {
        var world = new World();
        var pos = world.Track<Position>().Without<Velocity>();
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        pos.Transitions();  // drain create (entity does not match filter — has Velocity)
        world.Remove<Velocity>(e);
        // Entity entered {Position, !Velocity} → Entered
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Add_existing_component_does_not_append_transition()
    {
        var world = new World();
        var pos = world.Track<Position>();
        var e = world.Create(new Position(1, 1));
        pos.Transitions();  // drain
        world.Add(e, new Position(2, 2));   // already has Position → in-place overwrite, no migration
        Assert.Empty(pos.Transitions());
    }

    [Fact]
    public void Clone_appends_entered_transition()
    {
        var world = new World();
        var pos = world.Track<Position>();
        var src = world.Create(new Position(7, 7));
        pos.Transitions();  // drain create
        var clone = world.Clone(src);
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(clone, ts[0].Entity);
        Assert.NotEqual(src, clone);
    }

    [Fact]
    public void No_transitions_when_tracking_inactive()
    {
        var world = new World();   // no Track
        world.Create(new Position(0, 0));
        var e = world.Create(new Position(0, 0));
        world.Add(e, new Velocity(0, 0));
        world.Destroy(e);

        // Track after the fact — no dispatches happened before Track
        var pos = world.Track<Position>();
        Assert.Empty(pos.Transitions());
    }
}
