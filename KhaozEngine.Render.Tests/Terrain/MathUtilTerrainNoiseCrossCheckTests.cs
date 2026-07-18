using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

/// <summary>
/// Cross-checks <see cref="MathUtil.SmoothStep(float,float,float)"/> against
/// <c>KhaozEngine.Terrain.TerrainNoise.SmoothStep</c>. Split out of Foundation.Tests' MathUtilTests so the
/// Foundation cluster does not carry a KhaozEngine.Terrain reference for this single Render-owned cross-check.
/// </summary>
public class MathUtilTerrainNoiseCrossCheckTests
{
    [Fact]
    public void SmoothStep_MatchesTerrainNoiseForward()
        => Assert.Equal(MathUtil.SmoothStep(2f, 8f, 3.7f), KhaozEngine.Terrain.TerrainNoise.SmoothStep(2f, 8f, 3.7f));
}
