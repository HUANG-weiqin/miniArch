using System.IO;
using System.Linq;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Persistence;

/// <summary>
/// Verifies that snapshot save/load correctly resets observer state:
/// per-query transition logs are ephemeral, and the user must
/// re-Track after restore.
/// </summary>
public sealed class ChangeTrackingSnapshotTests
{
    private readonly record struct Position(int X, int Y);

    /// <summary>
    /// The per-query transition log is not part of the snapshot serialization.
    /// After save+load, a new Track returns a fresh query with an empty log even if
    /// the original world had entries.
    /// </summary>
    [Fact]
    public void Snapshot_does_not_carry_transition_log()
    {
        var world = new World(chunkCapacity: 2);
        var pos = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(1, 1));
        Assert.NotEmpty(pos.Transitions());

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        var loadedPos = loaded.Track().Capture<Position>().With<Position>();
        Assert.Empty(loadedPos.Transitions());
    }

    [Fact]
    public void RestoreState_clears_tracked_changes()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(1, 2));

        var snap = world.CaptureState();

        world.Set(e, new Position(10, 20));
        Assert.Equal(1, q.ValueChanges<Position>().Length);

        world.RestoreState(snap);
        // After restore, the query should self-heal and see no changes
        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void BUG_RestoreState_preserves_value_query_for_mutations_after_restore()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(1, 2));

        var snap = world.CaptureState();

        world.Set(e, new Position(10, 20));
        Assert.Equal(1, q.ValueChanges<Position>().Length);

        world.RestoreState(snap);

        // Do not touch q before mutating. The restored observer must still see
        // the first post-restore Set, not require manual re-arming.
        world.Set(e, new Position(30, 40));

        var changes = q.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(1, 2), changes[0].Old);
        Assert.Equal(new Position(30, 40), changes[0].New);
    }

    [Fact]
    public void Snapshot_load_starts_with_empty_value_changes()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(1, 2));

        world.Set(e, new Position(10, 20));
        Assert.Equal(1, q.ValueChanges<Position>().Length);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        using var loaded = WorldSnapshot.Load(stream);
        var loadedQ = loaded.Track().Capture<Position>().Previous();

        Assert.Equal(0, loadedQ.ValueChanges<Position>().Length);

        loaded.Set(e, new Position(30, 40));
        var changes = loadedQ.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(10, 20), changes[0].Old);
        Assert.Equal(new Position(30, 40), changes[0].New);
    }

    [Fact]
    public void BUG_RestoreState_preserves_filtered_transition_query_for_mutations_after_restore()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().With<Position>();

        _ = world.Create(new Position(1, 1));
        Assert.Single(q.Transitions());

        var snap = world.CaptureState();

        _ = world.Create(new Position(2, 2));
        Assert.Single(q.Transitions());

        world.RestoreState(snap);

        var afterRestore = world.Create(new Position(3, 3));
        var transitions = q.Transitions().ToArray();

        Assert.Single(transitions);
        Assert.Equal(TransitionKind.Entered, transitions[0].Kind);
        Assert.Equal(TransitionCause.Created, transitions[0].Cause);
        Assert.Equal(afterRestore, transitions[0].Entity);
    }
}
