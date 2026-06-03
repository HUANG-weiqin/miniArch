using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// 256-bit bitmask backed by four ulong fields.
/// Enables fast archetype matching for component ids 0..255.
/// Each additional ulong is only checked when the corresponding
/// <see cref="HasB1"/>/<see cref="HasB2"/>/<see cref="HasB3"/> is true,
/// so zero cost when fewer than 64/128/192 component types are registered.
/// </summary>
internal readonly struct ComponentMask
{
    /// <summary>Bits 0..63.</summary>
    public readonly ulong B0;

    /// <summary>Bits 64..127.</summary>
    public readonly ulong B1;

    /// <summary>Bits 128..191.</summary>
    public readonly ulong B2;

    /// <summary>Bits 192..255.</summary>
    public readonly ulong B3;

    /// <summary>
    /// Only used in HasHighBits and IsZero — maps B1 semantics for
    /// callers that still reference .High.
    /// </summary>
    internal ulong High => B1;

    /// <summary>
    /// Initializes a 256-bit mask from four 64-bit blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentMask(ulong b0, ulong b1, ulong b2, ulong b3)
    {
        B0 = b0;
        B1 = b1;
        B2 = b2;
        B3 = b3;
    }

    /// <summary>
    /// Returns true when all 256 bits are zero (no component ids registered).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsZero() => B0 == 0 && B1 == 0 && B2 == 0 && B3 == 0;

    /// <summary>
    /// Returns true when bits 64..127 are non-zero.
    /// </summary>
    public bool HasB1
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => B1 != 0;
    }

    /// <summary>
    /// Returns true when bits 128..191 are non-zero.
    /// </summary>
    public bool HasB2
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => B2 != 0;
    }

    /// <summary>
    /// Returns true when bits 192..255 are non-zero.
    /// </summary>
    public bool HasB3
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => B3 != 0;
    }

    /// <summary>
    /// Alias for <see cref="HasB1"/>, kept for external callers
    /// that previously referenced .HasHighBits.
    /// </summary>
    public bool HasHighBits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => B1 != 0;
    }

    /// <summary>Returns the hex representation of all 256 bits.</summary>
    public override string ToString() => $"0x{B3:X16}{B2:X16}{B1:X16}{B0:X16}";
}
