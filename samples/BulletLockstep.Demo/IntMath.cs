namespace BulletLockstep.Demo;

public static class IntMath
{
    /// <summary>
    /// Integer square root: returns floor(sqrt(<paramref name="x"/>)) for x &gt;= 0.
    /// Pure integer bit-construction (no floating point), so the result is
    /// identical across CPUs/hardware. Suitable for deterministic lockstep.
    /// </summary>
    /// <returns>floor(sqrt(x)) as a <see cref="long"/>. For x near <see cref="long.MaxValue"/>
    /// the result (~3.04e9) exceeds <see cref="int.MaxValue"/>, hence the return type.</returns>
    public static long Isqrt(long x)
    {
        if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if (x == 0) return 0;

        // Classic bit-construction isqrt run entirely in long arithmetic.
        // The previous int-domain variant silently truncated inputs above
        // int.MaxValue (the (int) cast dropped high bits), contradicting the
        // long signature. Working in long keeps the full [0, long.MaxValue]
        // domain correct; the result itself may exceed int.MaxValue near the
        // top of the long range, which is why we return long.
        long b = x;
        long result = 0;
        // Largest power of four representable as a non-negative long is 4^31 = 2^62.
        long bit = 1L << 62;
        while (bit > b) bit >>= 2;
        while (bit != 0)
        {
            if (b >= result + bit)
            {
                b -= result + bit;
                result = (result >> 1) + bit;
            }
            else
            {
                result >>= 1;
            }
            bit >>= 2;
        }
        return result;
    }
}
