using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// 512-bit bitmask backed by eight ulong fields.
/// Enables fast archetype matching for component ids 0..511.
/// </summary>
internal readonly struct ComponentMask : IEquatable<ComponentMask>
{
    /// <summary>Bits 0..63.</summary>
    public readonly ulong B0;
    /// <summary>Bits 64..127.</summary>
    public readonly ulong B1;
    /// <summary>Bits 128..191.</summary>
    public readonly ulong B2;
    /// <summary>Bits 192..255.</summary>
    public readonly ulong B3;
    /// <summary>Bits 256..319.</summary>
    public readonly ulong B4;
    /// <summary>Bits 320..383.</summary>
    public readonly ulong B5;
    /// <summary>Bits 384..447.</summary>
    public readonly ulong B6;
    /// <summary>Bits 448..511.</summary>
    public readonly ulong B7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentMask(ulong b0, ulong b1, ulong b2, ulong b3,
                         ulong b4 = 0, ulong b5 = 0, ulong b6 = 0, ulong b7 = 0)
    {
        B0 = b0; B1 = b1; B2 = b2; B3 = b3;
        B4 = b4; B5 = b5; B6 = b6; B7 = b7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsZero() =>
        B0 == 0 && B1 == 0 && B2 == 0 && B3 == 0 &&
        B4 == 0 && B5 == 0 && B6 == 0 && B7 == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ComponentMask other) =>
        B0 == other.B0 && B1 == other.B1 && B2 == other.B2 && B3 == other.B3 &&
        B4 == other.B4 && B5 == other.B5 && B6 == other.B6 && B7 == other.B7;

    public override bool Equals(object? obj) => obj is ComponentMask other && Equals(other);

    public override int GetHashCode() =>
        unchecked((int)(B0 ^ B1 ^ B2 ^ B3 ^ B4 ^ B5 ^ B6 ^ B7));

    /// <summary>
    /// Returns true when every bit set in <paramref name="other"/> is also set
    /// in this mask. Used for "required" archetype filtering.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSupersetOf(in ComponentMask other) =>
        (B0 & other.B0) == other.B0 &&
        (B1 & other.B1) == other.B1 &&
        (B2 & other.B2) == other.B2 &&
        (B3 & other.B3) == other.B3 &&
        (B4 & other.B4) == other.B4 &&
        (B5 & other.B5) == other.B5 &&
        (B6 & other.B6) == other.B6 &&
        (B7 & other.B7) == other.B7;

    /// <summary>
    /// Returns true when this mask and <paramref name="other"/> share at least
    /// one set bit. Used for "excluded" and "any" archetype filtering.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Intersects(in ComponentMask other) =>
        (B0 & other.B0) != 0 ||
        (B1 & other.B1) != 0 ||
        (B2 & other.B2) != 0 ||
        (B3 & other.B3) != 0 ||
        (B4 & other.B4) != 0 ||
        (B5 & other.B5) != 0 ||
        (B6 & other.B6) != 0 ||
        (B7 & other.B7) != 0;

    /// <summary>
    /// Tests whether bit <paramref name="id"/> is set.
    /// Returns <c>false</c> for ids outside 0..511 — the caller must fall back
    /// to a linear/binary search for those.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasBit(int id)
    {
        var cid = (uint)id;
        if (cid < 64)       return (B0 & (1UL << (int)cid)) != 0;
        else if (cid < 128) return (B1 & (1UL << (int)(cid - 64))) != 0;
        else if (cid < 192) return (B2 & (1UL << (int)(cid - 128))) != 0;
        else if (cid < 256) return (B3 & (1UL << (int)(cid - 192))) != 0;
        else if (cid < 320) return (B4 & (1UL << (int)(cid - 256))) != 0;
        else if (cid < 384) return (B5 & (1UL << (int)(cid - 320))) != 0;
        else if (cid < 448) return (B6 & (1UL << (int)(cid - 384))) != 0;
        else if (cid < 512) return (B7 & (1UL << (int)(cid - 448))) != 0;
        else return false;
    }
}

/// <summary>
/// Mutable counterpart to <see cref="ComponentMask"/> for incrementally
/// building a mask from component ids. <see cref="SetBit"/> is marked
/// <see cref="MethodImplOptions.AggressiveInlining"/> so the JIT inlines it,
/// producing the same branch sequence as hand-written bit-setting — there is
/// zero call overhead vs. inlining the if/else chain at each call site.
/// </summary>
/// <remarks>
/// <see cref="BitsSet"/> tracks how many ids fell within the 0..511 range.
/// If <c>BitsSet != compCount</c> after processing all components, at least
/// one id was &gt;= 512 and the mask is <b>not canonical</b> — the caller
/// must fall back to a Signature-keyed lookup to avoid silent collisions.
/// </remarks>
internal struct MaskBuilder
{
    public ulong B0, B1, B2, B3, B4, B5, B6, B7;
    public int BitsSet;

    /// <summary>
    /// Sets the bit for the given component id. No-op for ids &gt;= 512
    /// (the bit is silently dropped; <see cref="BitsSet"/> is not incremented).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBit(int id)
    {
        var cid = (uint)id;
        if (cid < 64)        { B0 |= 1UL << (int)cid;         BitsSet++; }
        else if (cid < 128)  { B1 |= 1UL << (int)(cid - 64);  BitsSet++; }
        else if (cid < 192)  { B2 |= 1UL << (int)(cid - 128); BitsSet++; }
        else if (cid < 256)  { B3 |= 1UL << (int)(cid - 192); BitsSet++; }
        else if (cid < 320)  { B4 |= 1UL << (int)(cid - 256); BitsSet++; }
        else if (cid < 384)  { B5 |= 1UL << (int)(cid - 320); BitsSet++; }
        else if (cid < 448)  { B6 |= 1UL << (int)(cid - 384); BitsSet++; }
        else if (cid < 512)  { B7 |= 1UL << (int)(cid - 448); BitsSet++; }
        // cid >= 512: bit dropped, BitsSet not incremented.
    }

    /// <summary>
    /// Produces the immutable <see cref="ComponentMask"/> snapshot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ComponentMask ToMask() => new(B0, B1, B2, B3, B4, B5, B6, B7);
}
