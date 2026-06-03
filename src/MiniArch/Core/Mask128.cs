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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsZero() => Low == 0 && High == 0;

    public override string ToString() => $"0x{High:X16}{Low:X16}";
}
