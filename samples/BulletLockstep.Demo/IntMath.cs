namespace BulletLockstep.Demo;

public static class IntMath
{
    public static int Isqrt(long x)
    {
        if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if (x == 0) return 0;
        var b = (int)Math.Min(x, long.MaxValue);
        int result = 0;
        int bit = 1 << 30;
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
