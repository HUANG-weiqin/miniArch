using MiniArch;
using Xunit;

namespace MiniArchTests.UserApi;

public class ChangeQueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);

    // ── ModifiedChunks ─────────────────────────────────────────────

    [Fact]
    public void ModifiedChunks_yields_chunk_after_Set()
    {
        var world = new World();
        var hp = world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        _ = hp.ModifiedChunks();                 // drain any setup noise
        world.Set(e, new Position(1, 1));
        var modified = hp.ModifiedChunks().ToList();
        Assert.NotEmpty(modified);
    }

    [Fact]
    public void ModifiedChunks_empty_when_no_write_since_last_call()
    {
        var world = new World();
        var hp = world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(1, 1));
        Assert.NotEmpty(hp.ModifiedChunks());    // first call sees the write
        Assert.Empty(hp.ModifiedChunks());        // second call: cursor advanced, nothing new
    }

    [Fact]
    public void ModifiedChunks_does_not_yield_for_unrelated_component_write()
    {
        var world = new World();
        var pos = world.Track<Position>();
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        _ = pos.ModifiedChunks();
        world.Set(e, new Velocity(9, 9));        // Velocity written, not Position
        Assert.Empty(pos.ModifiedChunks());
    }
}
