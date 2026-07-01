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

    [Theory]
    [InlineData(-1f, 0f)]     // below the edge clamps to 0
    [InlineData(0f, 0f)]
    [InlineData(5f, 0.5f)]    // midpoint
    [InlineData(10f, 1f)]
    [InlineData(11f, 1f)]     // above the edge clamps to 1
    public void SmoothStep_ClampedHermite(float x, float expected)
        => Assert.Equal(expected, MathUtil.SmoothStep(0f, 10f, x), 1e-6f);

    [Fact]
    public void SmoothStep_DegenerateIsStepFunction()
    {
        Assert.Equal(0f, MathUtil.SmoothStep(5f, 5f, 4.9f));
        Assert.Equal(1f, MathUtil.SmoothStep(5f, 5f, 5f));
    }

    [Fact]
    public void SmoothStep_MatchesTerrainNoiseForward()
        => Assert.Equal(MathUtil.SmoothStep(2f, 8f, 3.7f), KhaozEngine.Terrain.TerrainNoise.SmoothStep(2f, 8f, 3.7f));
}
