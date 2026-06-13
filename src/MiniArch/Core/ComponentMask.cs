using System;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// 512-bit bitmask backed by eight ulong fields.
/// Enables fast archetype matching for component ids 0..511.
/// Each additional ulong is only checked when the corresponding
/// <see cref="HasB1"/>–<see cref="HasB7"/> is true,
/// so zero cost for the unused bit ranges.
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

    public bool HasB1 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => B1 != 0; }
    public bool HasB2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => B2 != 0; }
    public bool HasB3 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => B3 != 0; }
    public bool HasB4 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => B4 != 0; }
    public bool HasB5 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => B5 != 0; }
    public bool HasB6 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => B6 != 0; }
    public bool HasB7 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => B7 != 0; }
}
