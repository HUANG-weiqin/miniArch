using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>256-bit component mask (4 × 64-bit longs, inspired by Friflo.Engine.ECS BitSet).</summary>
internal struct ComponentMask256
{
    public long L0, L1, L2, L3;

    public static ComponentMask256 Empty => default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ComponentMask256 FromComponents(ReadOnlySpan<ComponentType> components)
    {
        var m = Empty;
        for (var i = 0; i < components.Length; i++)
            m.SetBit(components[i].Value);
        return m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBit(int id)
    {
        if (id < 64) L0 |= 1L << id;
        else if (id < 128) L1 |= 1L << (id - 64);
        else if (id < 192) L2 |= 1L << (id - 128);
        else if (id < 256) L3 |= 1L << (id - 192);
        // ids >= 256: not covered by mask, handled by slow path
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsBitSet(int id)
    {
        if (id < 64) return (L0 & (1L << id)) != 0;
        if (id < 128) return (L1 & (1L << (id - 64))) != 0;
        if (id < 192) return (L2 & (1L << (id - 128))) != 0;
        if (id < 256) return (L3 & (1L << (id - 192))) != 0;
        return false;
    }

    /// <summary>Returns true if this mask has ALL bits set that are set in required.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasAll(in ComponentMask256 required)
    {
        return (L0 & required.L0) == required.L0
            && (L1 & required.L1) == required.L1
            && (L2 & required.L2) == required.L2
            && (L3 & required.L3) == required.L3;
    }

    /// <summary>Returns true if this mask has NO bits in common with excluded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasNone(in ComponentMask256 excluded)
    {
        return (L0 & excluded.L0) == 0
            && (L1 & excluded.L1) == 0
            && (L2 & excluded.L2) == 0
            && (L3 & excluded.L3) == 0;
    }

    /// <summary>Returns true if this mask has ANY bit in common with any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasAny(in ComponentMask256 any)
    {
        return (L0 & any.L0) != 0
            || (L1 & any.L1) != 0
            || (L2 & any.L2) != 0
            || (L3 & any.L3) != 0;
    }

    public readonly bool IsZero => (L0 | L1 | L2 | L3) == 0;
}
