using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Tests for <see cref="TransitionWatch{THandler}"/> API — filter semantic coverage.
/// </summary>
public class ChangeQueryFilterTests
{
    // ── Test structs ────────────────────────────────────────────────
    private readonly record struct HP(int Value);
    private readonly record struct Dead;
    private readonly record struct Enemy;
    private readonly record struct Position(int X, int Y);

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

    private static TransitionWatch<TransitionRecorder> Track(World world)
    {
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>());
        watch.Handler = new TransitionRecorder(0);
        return watch;
    }

    [Fact]
    public void Watch_rejects_empty_filter()
    {
        using var world = new World();

        var ex = Assert.Throws<ArgumentException>((Action)(() => world.Watch<TransitionRecorder>(new QueryDescription())));
        Assert.Contains("With<T>()", ex.Message);
    }

    [Fact]
    public void Watch_accepts_without_only_filter()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().Without<Dead>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);
        var e = world.Create(new HP(100));
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Watch_accepts_withany_only_filter()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().WithAny<HP>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);
        var e = world.Create(new HP(100));
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Create_with_matching_filter_reported_as_entered()
    {
        using var world = new World();
        var watch = Track(world);
        watch.Snapshot(world);
        var e = world.Create(new HP(100));
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Destroy_of_matching_entity_reported_as_exited()
    {
        using var world = new World();
        var watch = Track(world);
        var e = world.Create(new HP(100));
        watch.Snapshot(world);
        world.Destroy(e);
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Exited, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Add_of_missing_component_enters()
    {
        using var world = new World();
        var watch = Track(world);
        var e = world.CreateEmpty();
        watch.Snapshot(world);
        world.Add(e, new HP(50));
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
    }

    [Fact]
    public void Remove_of_component_exits()
    {
        using var world = new World();
        var watch = Track(world);
        var e = world.Create(new HP(50));
        world.Add(e, new Dead()); // entity has HP+Dead
        watch.Snapshot(world);
        world.Remove<Dead>(e); // still has HP — still matches filter (With<HP> only)
        watch.Diff(world);
        // With<HP> only: entity still has HP, so no transition
        Assert.Empty(watch.Handler.Changes);
    }

    [Fact]
    public void Create_with_both_components_reported_as_entered()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>().With<Enemy>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);

        var e = world.Create(new HP(100), new Enemy());
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Create_without_both_components_not_reported()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>().With<Enemy>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);

        world.Create(new HP(100)); // missing Enemy
        watch.Diff(world);

        Assert.Empty(watch.Handler.Changes);
    }

    [Fact]
    public void Add_second_required_component_enters()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>().With<Enemy>());
        watch.Handler = new TransitionRecorder(0);
        var e = world.Create(new HP(100));
        watch.Snapshot(world);

        world.Add(e, new Enemy()); // now has HP+Enemy → matches
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Remove_required_component_exits()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>());
        watch.Handler = new TransitionRecorder(0);
        var e = world.Create(new HP(100));
        watch.Snapshot(world);

        world.Remove<HP>(e);
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Exited, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Multiple_enters_and_exits_reported()
    {
        using var world = new World();
        var watch = Track(world);
        watch.Snapshot(world);

        var e1 = world.Create(new HP(10));
        var e2 = world.Create(new HP(20));
        var e3 = world.CreateEmpty(); // no HP
        watch.Diff(world);

        Assert.Equal(2, watch.Handler.Changes.Count);
        Assert.Contains(watch.Handler.Changes, c => c.Item1 == e1);
        Assert.Contains(watch.Handler.Changes, c => c.Item1 == e2);
        Assert.DoesNotContain(watch.Handler.Changes, c => c.Item1 == e3);
    }

    [Fact]
    public void Remove_and_readd_produces_exit_then_enter_across_snapshots()
    {
        using var world = new World();
        var watch = Track(world);
        var e = world.Create(new HP(100));

        watch.Snapshot(world);
        world.Remove<HP>(e);
        watch.Diff(world);

        var exited = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Exited, exited.Item2);
        Assert.Equal(e, exited.Item1);

        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);
        world.Add(e, new HP(200));
        watch.Diff(world);

        var entered = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, entered.Item2);
        Assert.Equal(e, entered.Item1);
    }

    [Fact]
    public void Without_filter_enters_when_excluded_component_removed()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>().Without<Dead>());
        watch.Handler = new TransitionRecorder(0);
        var e = world.Create(new HP(100), new Dead());
        watch.Snapshot(world);

        world.Remove<Dead>(e); // now matches {HP, !Dead}
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Without_filter_exits_when_excluded_component_added()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>().Without<Dead>());
        watch.Handler = new TransitionRecorder(0);
        var e = world.Create(new HP(100));
        watch.Snapshot(world);

        world.Add(e, new Dead()); // no longer matches {HP, !Dead}
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Exited, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Multiple_Watch_instances_are_independent()
    {
        using var world = new World();
        var watch1 = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>());
        watch1.Handler = new TransitionRecorder(0);
        var watch2 = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>());
        watch2.Handler = new TransitionRecorder(0);

        watch1.Snapshot(world);
        watch2.Snapshot(world);
        var e = world.Create(new HP(100));

        watch1.Diff(world);
        Assert.NotEmpty(watch1.Handler.Changes);

        // watch2 hasn't Diff'd yet
        Assert.Empty(watch2.Handler.Changes);

        watch2.Diff(world);
        Assert.NotEmpty(watch2.Handler.Changes);
    }

    [Fact]
    public void Transitions_with_WithAny_filter()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().WithAny<HP>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);

        var e = world.Create(new HP(100));
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Stale_watch_after_Dispose_throws()
    {
        var world = new World();
        // Dispose before creating watch — no world to create from
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<HP>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => watch.Diff(world));
    }

    [Fact]
    public void Watch_empty_filter_throws()
    {
        using var world = new World();
        var ex = Assert.Throws<ArgumentException>(() =>
            world.Watch<TransitionRecorder>(new QueryDescription()));
        Assert.Contains("With<T>()", ex.Message);
    }
}
