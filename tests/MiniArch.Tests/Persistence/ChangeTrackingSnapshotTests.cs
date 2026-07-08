using System.IO;
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
}
