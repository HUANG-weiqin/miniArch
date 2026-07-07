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
}
