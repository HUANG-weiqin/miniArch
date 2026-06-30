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
    /// Regression guard: destroying BOTH endpoints of a deferred AddChild in the
    /// same frame must replay cleanly. Previously the second Destroy fell through
    /// to AppendDestroy (the cascade from the first Destroy had already cancelled
    /// the other endpoint's deferred batch, so TryGetPendingBatch returned false),
    /// emitting a placeholder Destroy op with no matching Reserve and crashing
    /// every replaying peer with "Unresolved placeholder entity". The producing
    /// host never replayed its own cancelled creates, so this was a silent
    /// producer-side lockstep hazard. Fixed by guarding the Destroy fallthrough
    /// against placeholders: both endpoints cancel, the link intent is purged by
    /// the cascade, and the delta is empty.
    /// </summary>
    [Fact]
    public void Deferred_link_then_destroy_BOTH_endpoints_replays_cleanly()
    {
        using var scratch = new World();
        var recorder = new CommandStream(scratch) { DeferredEntities = true };

        var parent = recorder.Create();
        recorder.Add(parent, new Position(1, 1));
        var child = recorder.Create();
        recorder.Add(child, new Position(2, 2));
        recorder.AddChild(parent, child);
        // Destroy BOTH endpoints of the link in the same frame.
        recorder.Destroy(parent);
        recorder.Destroy(child);

        var delta = recorder.Snapshot();
        Assert.True(delta.IsEmpty, "Both endpoints cancelled: the delta must be empty.");

        var replica = new World();
        try
        {
            replica.Replay(delta);
            Assert.Equal(0, replica.EntityCount);
        }
        finally
        {
            replica.Dispose();
        }
    }

    /// <summary>
    /// Asymmetry guard: destroying only ONE endpoint of a deferred link is
    /// handled correctly (no malformed delta). Only the both-endpoints case
    /// above is broken. If a future fix changes this, the test will flag it.
    /// </summary>
    [Fact]
    public void Deferred_link_then_destroy_single_endpoint_replays_cleanly()
    {
        using var scratch = new World();
        var recorder = new CommandStream(scratch) { DeferredEntities = true };

        var parent = recorder.Create();
        recorder.Add(parent, new Position(1, 1));
        var child = recorder.Create();
        recorder.Add(child, new Position(2, 2));
        recorder.AddChild(parent, child);
        recorder.Destroy(child); // only the child endpoint

        var delta = recorder.Snapshot();

        var replica = new World();
        try
        {
            replica.Replay(delta); // no throw
            // Parent survives; child was cancelled before materialization.
            Assert.Equal(1, replica.EntityCount);
        }
        finally
        {
            replica.Dispose();
        }
    }

    /// <summary>
    /// Minimal root-cause repro for the bug above, independent of hierarchy:
    /// destroying a deferred placeholder whose batch was already cancelled (here,
    /// simply by a prior Destroy on the same placeholder) must be a no-op. Before
    /// the Destroy fallthrough guard, the second Destroy emitted a placeholder
    /// Destroy op and crashed replay with "Unresolved placeholder seq=0".
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
            replica.Replay(delta);
            Assert.Equal(0, replica.EntityCount);
        }
        finally
        {
            replica.Dispose();
        }
    }

    /// <summary>
    /// Component commands ARE cleaned up on cancelled create (unlike hierarchy
    /// intents). create + add-component + destroy (no link) cancels cleanly
    /// and emits an empty delta. This pins the asymmetry: the malformed-delta
    /// hazard is specific to link-then-destroy-both-endpoints.
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
            replica.Replay(delta);
            Assert.Equal(0, replica.EntityCount);
        }
        finally
        {
            replica.Dispose();
        }
    }
}
