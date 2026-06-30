using BulletLockstep.Demo;

namespace MiniArchTests.Core;

public sealed class IntMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(15, 3)]
    [InlineData(16, 4)]
    [InlineData(24, 4)]
    [InlineData(25, 5)]
    [InlineData(99, 9)]
    [InlineData(100, 10)]
    [InlineData(int.MaxValue, 46340)]
    public void Isqrt_returns_floor_sqrt(long input, int expected)
    {
        Assert.Equal(expected, IntMath.Isqrt(input));
    }

    [Fact]
    public void Isqrt_negative_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.Isqrt(-1));
    }
}
