using System.IO;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Persistence;

/// <summary>
/// Verifies that snapshot save/load correctly resets observer state:
/// transition logs are ephemeral snapshot state, but live in-memory handles
/// remain armed across RestoreState.
/// </summary>
public sealed class ChangeTrackingSnapshotTests
{
    private readonly record struct Position(int X, int Y);

    /// <summary>
    /// The per-log transition list is not part of the snapshot serialization.
    /// After save+load, a new Track returns a fresh log with an empty list even if
    /// the original world had entries.
    /// </summary>
    [Fact]
    public void Snapshot_does_not_carry_transition_log()
    {
        var world = new World(chunkCapacity: 2);
        var log = world.TrackTransitions(new QueryDescription().With<Position>());
        var e = world.Create(new Position(1, 1));
        Assert.NotEmpty(log.Transitions.ToArray());

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        var loadedLog = loaded.TrackTransitions(new QueryDescription().With<Position>());
        Assert.Empty(loadedLog.Transitions.ToArray());

        world.Dispose();
    }

    [Fact]
    public void RestoreState_preserves_transition_log_for_post_restore_mutations()
    {
        var world = new World(chunkCapacity: 2);
        var log = world.TrackTransitions(new QueryDescription().With<Position>());
        var snapshot = world.CaptureState();

        world.Create(new Position(1, 1));
        Assert.NotEmpty(log.Transitions.ToArray());

        world.RestoreState(snapshot);
        Assert.Empty(log.Transitions.ToArray());

        var entity = world.Create(new Position(2, 2));
        var transition = Assert.Single(log.Transitions.ToArray());
        Assert.Equal(entity, transition.Entity);
        Assert.Equal(TransitionCause.Created, transition.Cause);
        Assert.Equal(TransitionKind.Entered, transition.Kind);

        world.Dispose();
    }
}
