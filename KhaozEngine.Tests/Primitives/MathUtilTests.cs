using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class MathUtilTests
{
    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(2f, 1f)]
    public void Clamp01(float input, float expected) => Assert.Equal(expected, MathUtil.Clamp01(input));

    [Fact]
    public void Lerp_Interpolates() => Assert.Equal(7.5f, MathUtil.Lerp(5f, 10f, 0.5f));

    [Fact]
    public void InverseLerp_Inverts() => Assert.Equal(0.5f, MathUtil.InverseLerp(5f, 10f, 7.5f));

    [Fact]
    public void InverseLerp_DegenerateReturnsZero() => Assert.Equal(0f, MathUtil.InverseLerp(5f, 5f, 7f));
}
