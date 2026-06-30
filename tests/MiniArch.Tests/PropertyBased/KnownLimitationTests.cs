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
    /// KNOWN LIMITATION (surfaced by ReplayConvergencePropertyTests, pinned by
    /// brute-force search): when BOTH endpoints of a deferred AddChild are
    /// destroyed in the same frame, the cancelled creates emit no Reserve ops
    /// but the hierarchy intent is not cleaned up. The resulting FrameDelta
    /// carries an AddChild referencing unresolved placeholder seqs, and any
    /// peer replaying the delta throws "Unresolved placeholder entity".
    ///
    /// The producing host does not throw (it never replays its own cancelled
    /// creates), so this is a silent producer-side hazard for lockstep: every
    /// replica crashes on a delta the producer believed was valid.
    ///
    /// Minimal repro: 2 entities, link(0,1), destroy both, single frame.
    ///
    /// Two possible fixes (out of scope for the codebase-hardening pass, each
    /// needs a design call):
    ///   (a) In CommandStream hierarchy resolution, DROP an intent whose
    ///       resolved parent or child is the (-1,-1) sentinel.
    ///   (b) Throw at Snapshot() when an intent references a cancelled
    ///       placeholder, so the producer fails loud instead of the replica.
    /// The convergence property's generator excludes linked placeholders from
    /// destroy, which avoids the combination and lets the property pass for
    /// supported usage.
    /// </summary>
    [Fact]
    public void Deferred_link_then_destroy_BOTH_endpoints_yields_unresolved_delta()
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

        var replica = new World();
        try
        {
            Assert.Throws<InvalidOperationException>(() => replica.Replay(delta));
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
