namespace BulletLockstep.Demo;

// Deterministic input source: given (hostId, frame), produces a deterministic
// (dx, dy) movement intent. Same inputs on every host -> identical records.
//
// Slice 1 uses a pure PRNG seeded by (hostId, frame) — no real I/O — so every
// host computes the same per-host input vector independently.
public static class InputProvider
{
    // xorshift32-style mixing; returns int in [-3, 3] for each axis.
    public static (int Dx, int Dy) Get(int hostId, int frame)
    {
        var seed = (uint)((hostId * 2654435761u) ^ (frame * 40503u));
        var dx = (int)(Next(ref seed) % 7) - 3;
        var dy = (int)(Next(ref seed) % 7) - 3;
        return (dx, dy);
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
