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
}
