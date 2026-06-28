namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Computes a SHA-256 checksum of the entire world state.
    /// Stable across peers driven by the same delta sequence — use to detect
    /// lockstep divergence. Returns 32 raw bytes; use
    /// <c>Convert.ToHexString(world.Checksum())</c> for a hex string.
    /// </summary>
    public byte[] Checksum() => Core.WorldSnapshot.ComputeChecksum(this);

    /// <summary>
    /// Computes a canonical SHA-256 checksum that is identical for any two
    /// worlds with the same logical state, regardless of how they were built
    /// (replay, snapshot-load, or manual construction). Slower than
    /// <see cref="Checksum"/>; use only for comparing worlds from different
    /// construction paths (e.g. client snapshot vs server live state).
    /// </summary>
    public byte[] CanonicalChecksum() => Core.WorldSnapshot.ComputeCanonicalChecksum(this);
}
