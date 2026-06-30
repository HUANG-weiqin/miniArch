using BulletLockstep.Demo;

namespace MiniArchTests.Core;

public sealed class IntMathTests
{
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(2L, 1L)]
    [InlineData(3L, 1L)]
    [InlineData(4L, 2L)]
    [InlineData(15L, 3L)]
    [InlineData(16L, 4L)]
    [InlineData(24L, 4L)]
    [InlineData(25L, 5L)]
    [InlineData(99L, 9L)]
    [InlineData(100L, 10L)]
    [InlineData((long)int.MaxValue, 46340L)]            // floor(sqrt(2^31 - 1)) = 46340
    public void Isqrt_returns_floor_sqrt(long input, long expected)
    {
        Assert.Equal(expected, IntMath.Isqrt(input));
    }

    [Fact]
    public void Isqrt_negative_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.Isqrt(-1));
    }

    [Fact]
    public void Isqrt_exact_squares_return_exact_root()
    {
        Assert.Equal(46341L, IntMath.Isqrt(46341L * 46341L));
        Assert.Equal(100000L, IntMath.Isqrt(100000L * 100000L));
    }

    // Regression: the previous int-domain implementation silently truncated
    // inputs above int.MaxValue (the (int) cast dropped high bits), so this
    // case returned garbage. The long-domain implementation must be correct
    // across the full long range. Result 3037000499 > int.MaxValue, so this
    // also pins the return type to long.
    [Fact]
    public void Isqrt_above_int_range_is_correct()
    {
        // Just past int.MaxValue: still 46340 (46341^2 = 2147488281 > 2147483648).
        Assert.Equal(46340L, IntMath.Isqrt((long)int.MaxValue + 1L));

        // Large perfect square whose root fits only in long.
        const long Root = 3_000_000_042L;                 // > int.MaxValue
        Assert.Equal(Root, IntMath.Isqrt(Root * Root));

        // floor(sqrt(long.MaxValue)) = 3037000499 (the largest representable root).
        Assert.Equal(3_037_000_499L, IntMath.Isqrt(long.MaxValue));
    }
}
