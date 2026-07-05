using System.Collections.ObjectModel;

namespace MiniArch.Diagnostics;

/// <summary>
/// A breakdown of a <see cref="World"/> into per-domain SHA-256 hashes.
/// Use to rapidly narrow down which state domain diverged before running
/// the heavier <see cref="WorldDiff.Compare"/>.
/// </summary>
/// <remarks>
/// All hashes are deterministic (same logical state → same digest).
/// The <see cref="Total"/> hash is a combination of all domain hashes,
/// NOT equivalent to <see cref="World.Checksum"/> (which may include
/// additional internal layout details).
/// </remarks>
public readonly struct WorldDigestResult
{
    /// <summary>Combined hash of all domains.</summary>
    public byte[] Total { get; }

    /// <summary>Hash of alive entity IDs and versions (sorted by ID).</summary>
    public byte[] Occupancy { get; }

    /// <summary>Hash of free-list entries (ID, version).</summary>
    public byte[] FreeList { get; }

    /// <summary>Hash of parent-child relations.</summary>
    public byte[] Hierarchy { get; }

    /// <summary>One hash per component type covering all values of that type.</summary>
    public IReadOnlyDictionary<Type, byte[]> PerComponent { get; }

    /// <summary>One hash per non-empty archetype (signature + entity data).</summary>
    public IReadOnlyDictionary<int, byte[]> PerArchetype { get; }

    internal WorldDigestResult(
        byte[] total,
        byte[] occupancy,
        byte[] freeList,
        byte[] hierarchy,
        Dictionary<Type, byte[]> perComponent,
        Dictionary<int, byte[]> perArchetype)
    {
        Total = total;
        Occupancy = occupancy;
        FreeList = freeList;
        Hierarchy = hierarchy;
        PerComponent = new ReadOnlyDictionary<Type, byte[]>(perComponent);
        PerArchetype = new ReadOnlyDictionary<int, byte[]>(perArchetype);
    }
}

/// <summary>
/// Accumulates hash input data and computes SHA-256 at the end.
/// Replaces IncrementalHash (unavailable in System.IO.Hashing 8.x).
/// </summary>
internal sealed class HashBuilder : IDisposable
{
    private readonly MemoryStream _stream = new();

    internal void Append(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        _stream.Write(bytes, 0, bytes.Length);
    }

    internal void Append(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
    }

    internal byte[] GetHashAndReset()
    {
        _stream.Position = 0;
        var hash = System.Security.Cryptography.SHA256.HashData(_stream);
        _stream.SetLength(0);
        return hash;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Static helpers for WorldDigest.</summary>
internal static class DigestHelper
{
    internal static byte[] CombineHashes(params byte[][] hashes)
    {
        using var builder = new HashBuilder();
        foreach (var h in hashes)
        {
            builder.Append(BitConverter.GetBytes(h.Length));
            builder.Append(h.AsSpan());
        }
        return builder.GetHashAndReset();
    }
}
