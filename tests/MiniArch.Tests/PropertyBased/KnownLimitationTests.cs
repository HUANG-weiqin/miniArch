using MiniArch;
using MiniArch.Core;

namespace MiniArchTests.PropertyBased;

/// <summary>
/// Pinned regression cases surfaced by the property-based convergence tests.
/// Each documents a real input pattern and the library's current behavior, so
/// the behaviour is locked in whether correct or a known limitation.
/// </summary>
public sealed class KnownLimitationTests
{
    private readonly record struct Position(int X, int Y);

    /// <summary>
    /// Pins the deferred recorder's existing tolerance for an invalid repeated
    /// cancellation; this is not a general guarantee for cancelled-handle use.
    /// Before the fallthrough guard, the second Destroy emitted an unresolved
    /// placeholder op and crashed replay with "Unresolved placeholder seq=0".
    /// </summary>
    [Fact]
    public void Deferred_destroy_twice_on_same_placeholder_cancels_cleanly()
    {
        using var scratch = new World();
        var recorder = new CommandStream(scratch) { DeferredEntities = true };

        var p = recorder.Create();
        recorder.Add(p, new Position(1, 1));
        recorder.Destroy(p);
        recorder.Destroy(p);

        var delta = recorder.Snapshot();
        Assert.True(delta.IsEmpty, "Cancelled create: the delta must be empty.");

        var replica = new World();
        try
        {
            new CommandStream(replica).Replay(delta);
            Assert.Equal(0, replica.EntityCount);
        }
        finally
        {
            replica.Dispose();
        }
    }

    /// <summary>
    /// Component commands are cleaned up on cancelled create: create,
    /// add-component, then destroy emits an empty delta.
    /// </summary>
    [Fact]
    public void Deferred_add_then_destroy_same_placeholder_cancels_cleanly()
    {
        using var scratch = new World();
        var recorder = new CommandStream(scratch) { DeferredEntities = true };

        var p = recorder.Create();
        recorder.Add(p, new Position(1, 1));
        recorder.Destroy(p);

        var delta = recorder.Snapshot();
        Assert.True(delta.IsEmpty, "Cancelled create with no surviving ops should emit an empty delta.");

        var replica = new World();
        try
        {
            new CommandStream(replica).Replay(delta);
            Assert.Equal(0, replica.EntityCount);
        }
        finally
        {
            replica.Dispose();
        }
    }
}
