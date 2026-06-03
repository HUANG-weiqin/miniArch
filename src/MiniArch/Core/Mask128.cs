using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// 128-bit bitmask backed by two ulong fields.
/// Enables fast archetype matching for component ids 0..127.
/// </summary>
public readonly struct Mask128
{
    public readonly ulong Low;  // bits 0..63
    public readonly ulong High; // bits 64..127

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Mask128(ulong low, ulong high)
    {
        Low = low;
        High = high;
    }

    public bool IsZero => Low == 0 && High == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Mask128 operator &(Mask128 left, Mask128 right) =>
        new(left.Low & right.Low, left.High & right.High);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Mask128 operator |(Mask128 left, Mask128 right) =>
        new(left.Low | right.Low, left.High | right.High);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Mask128 operator ~(Mask128 value) =>
        new(~value.Low, ~value.High);

    public static Mask128 Zero => default;

    public override string ToString() => $"0x{High:X16}{Low:X16}";
}
