using System;
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class WorldSurfaceTests
{
    // A 3x3 unit grid, cell 1, origin (-1,-1), flat top y=2 over a 2x2 area (corners empty).
    static PropSurface Flat()
    {
        float n = float.NaN;
        return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, 2f, n, 2f, 2f, 2f, n, 2f, n });
    }

    [Fact]
    public void SampleWorld_AppliesCentreAndScale()
    {
        var ws = new WorldSurface(Flat(), center: new Vector2(10f, 5f), scale: 2f, yaw: 0f, baseY: 1f);
        // Centre of the prop in world is (10,5); local (0,0) -> height 2*scale + baseY = 5.
        Assert.Equal(5f, ws.SampleWorld(10f, 5f)!.Value, 3);
    }

    [Fact]
    public void SampleWorld_OutsideFootprint_ReturnsNull()
    {
        var ws = new WorldSurface(Flat(), new Vector2(0f, 0f), 1f, 0f, 0f);
        Assert.Null(ws.SampleWorld(50f, 50f));
    }

    [Fact]
    public void SampleWorld_YawRotatesLookup()
    {
        // An asymmetric strip covered only along local +X at j=1, sampled through a 90deg yaw, is found by
        // querying along world +Z instead of +X.
        float n = float.NaN;
        var strip = new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, n, n, n, 3f, 3f, n, n, n }); // covered (1,1),(2,1)
        var ws = new WorldSurface(strip, Vector2.Zero, 1f, yaw: MathF.PI / 2f, baseY: 0f);
        Assert.NotNull(ws.SampleWorld(0f, 1f)); // local +X maps to world +Z under +90deg yaw
    }

    [Fact]
    public void TopWorld_IsBasePlusScaledMax()
    {
        var ws = new WorldSurface(Flat(), Vector2.Zero, scale: 2f, yaw: 0f, baseY: 1f);
        Assert.Equal(1f + 2f * 2f, ws.TopWorld, 3);
    }
}
