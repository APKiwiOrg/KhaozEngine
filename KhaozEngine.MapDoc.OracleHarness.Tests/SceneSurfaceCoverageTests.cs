using System.Runtime.Versioning;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class SceneSurfaceCoverageTests
{
    [Fact]
    public void Coverage_AllowsReorderedRefinement()
    {
        var a = new SceneSurfacePoint(0, 0, 0); var b = new SceneSurfacePoint(1, 0, 0);
        var c = new SceneSurfacePoint(0, 0, 1); var mid = new SceneSurfacePoint(.5, 0, 0);
        Assert.True(SceneSurfaceCoverage.Covers([new(a, b, c)], [new(mid, b, c), new(a, mid, c)]));
    }

    [Fact]
    public void Coverage_RejectsEqualAreaOverlapAndHole()
    {
        var a = new SceneSurfacePoint(0, 0, 0); var b = new SceneSurfacePoint(1, 0, 0);
        var c = new SceneSurfacePoint(0, 0, 1); var mid = new SceneSurfacePoint(.5, 0, 0);
        Assert.False(SceneSurfaceCoverage.Covers([new(a, b, c)], [new(a, mid, c), new(a, mid, c)]));
    }
}
