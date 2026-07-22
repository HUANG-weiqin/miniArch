using System.Collections.ObjectModel;

namespace MiniArch.Diagnostics;

/// <summary>
/// A breakdown of a <see cref="World"/> into per-domain SHA-256 hashes.
/// Use to rapidly narrow down which state domain diverged before running
/// the heavier <see cref="WorldDiff.Compare"/>.
/// </summary>
/// <remarks>
/// All hashes are deterministic for the same internal world layout.
/// The <see cref="Total"/> hash is a combination of all domain hashes,
/// including physical per-archetype row-order data. It is NOT equivalent to
/// layout-independent <see cref="World.CanonicalChecksum"/>.
/// </remarks>
public readonly struct WorldDigestResult
{
    private readonly byte[]? _total;
    private readonly byte[]? _occupancy;
    private readonly byte[]? _freeList;
    private readonly byte[]? _hierarchy;
    private readonly Dictionary<Type, byte[]>? _perComponent;
    private readonly Dictionary<int, byte[]>? _perArchetype;

    /// <summary>Gets a defensive copy of the combined hash of all domains.</summary>
    public byte[] Total => CopyHash(_total);

    /// <summary>Gets a defensive copy of the hash of alive entity IDs and versions (sorted by ID).</summary>
    public byte[] Occupancy => CopyHash(_occupancy);

    /// <summary>Gets a defensive copy of the hash of free-list entries (ID, version).</summary>
    public byte[] FreeList => CopyHash(_freeList);

    /// <summary>Gets a defensive copy of the hash of parent-child relations.</summary>
    public byte[] Hierarchy => CopyHash(_hierarchy);

    /// <summary>Gets a defensive snapshot of component-type hashes.</summary>
    public IReadOnlyDictionary<Type, byte[]> PerComponent => CopyHashes(_perComponent);

    /// <summary>Gets a defensive snapshot of non-empty archetype hashes.</summary>
    public IReadOnlyDictionary<int, byte[]> PerArchetype => CopyHashes(_perArchetype);

    internal WorldDigestResult(
        byte[] total,
        byte[] occupancy,
        byte[] freeList,
        byte[] hierarchy,
        Dictionary<Type, byte[]> perComponent,
        Dictionary<int, byte[]> perArchetype)
    {
        _total = total;
        _occupancy = occupancy;
        _freeList = freeList;
        _hierarchy = hierarchy;
        _perComponent = perComponent;
        _perArchetype = perArchetype;
    }

    private static byte[] CopyHash(byte[]? hash)
        => hash is null ? null! : (byte[])hash.Clone();

    private static IReadOnlyDictionary<TKey, byte[]> CopyHashes<TKey>(Dictionary<TKey, byte[]>? hashes)
        where TKey : notnull
    {
        if (hashes is null)
            return null!;

        var copy = new Dictionary<TKey, byte[]>(hashes.Count, hashes.Comparer);
        foreach (var (key, hash) in hashes)
            copy.Add(key, (byte[])hash.Clone());
        return new ReadOnlyDictionary<TKey, byte[]>(copy);
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
