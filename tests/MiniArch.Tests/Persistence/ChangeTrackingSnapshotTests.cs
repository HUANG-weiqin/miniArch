using System.IO;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Persistence;

/// <summary>
/// Verifies that snapshot save/load correctly resets observer state:
/// per-query transition logs are ephemeral, write epoch resets, and the user must
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

    /// <summary>
    /// After snapshot load, write epoch is 0, tracking is inactive,
    /// and a freshly Track'd query starts with an empty log.
    /// The user must call Track{T} again.
    /// </summary>
    [Fact]
    public void Restored_world_starts_with_fresh_observer_state()
    {
        var world = new World(chunkCapacity: 2);
        world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(1, 1));
        world.Set(e, new Position(2, 2));
        Assert.True(world.IsChangeTrackingActive);
        Assert.True(world.CurrentWriteEpoch > 0);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        Assert.Equal(0, loaded.CurrentWriteEpoch);
        Assert.False(loaded.IsChangeTrackingActive);

        var loadedPos = loaded.Track().Capture<Position>().With<Position>();
        Assert.Empty(loadedPos.Transitions());
    }

    /// <summary>
    /// After restore, calling Track{T} again and performing a write
    /// correctly produces a modified chunk — verify the observer
    /// machinery works from scratch after load.
    /// </summary>
    [Fact]
    public void Track_after_restore_resumes_tracking_cleanly()
    {
        var world = new World(chunkCapacity: 2);
        var e = world.Create(new Position(1, 1));

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);

        // re-obtain entity on loaded world (same id/version)
        var tracker = loaded.Track().Capture<Position>().With<Position>();
        Assert.True(loaded.IsChangeTrackingActive);
        Assert.Empty(tracker.Transitions());

        loaded.Set(e, new Position(2, 2));
        var modified = tracker.ModifiedChunks<Position>().ToList();
        Assert.NotEmpty(modified);
    }
}
