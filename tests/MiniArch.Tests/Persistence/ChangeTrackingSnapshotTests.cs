using System.IO;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Persistence;

/// <summary>
/// Verifies that snapshot save/load behavior with the new Watch API.
/// </summary>
public sealed class ChangeTrackingSnapshotTests
{
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

    /// <summary>
    /// TransitionWatch state is not persisted across save/load.
    /// After save+load, a fresh watch starts empty.
    /// </summary>
    [Fact]
    public void Snapshot_does_not_carry_transition_watch()
    {
        var world = new World(chunkCapacity: 2);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);
        var e = world.Create(new Position(1, 1));
        watch.Diff(world);
        Assert.NotEmpty(watch.Handler.Changes);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        var loadedWatch = loaded.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        loadedWatch.Handler = new TransitionRecorder(0);
        loadedWatch.Snapshot(loaded);
        loadedWatch.Diff(loaded);
        Assert.Empty(loadedWatch.Handler.Changes);

        world.Dispose();
    }

    [Fact]
    public void RestoreState_preserves_watch_for_post_restore_mutations()
    {
        var world = new World(chunkCapacity: 2);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        var snapshot = world.CaptureState();

        watch.Snapshot(world);
        world.Create(new Position(1, 1));
        watch.Diff(world);
        Assert.NotEmpty(watch.Handler.Changes);

        world.RestoreState(snapshot);
        watch.Handler = new TransitionRecorder(0);
        watch.Snapshot(world);
        var entity = world.Create(new Position(2, 2));
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(entity, change.Item1);
        Assert.Equal(TransitionKind.Entered, change.Item2);

        world.Dispose();
    }
}
