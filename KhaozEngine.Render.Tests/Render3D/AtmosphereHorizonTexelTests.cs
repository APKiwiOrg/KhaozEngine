using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereHorizonTexelTests
{
    [Theory]
    [InlineData(53, -1)]
    [InlineData(53, 0)]
    [InlineData(53, 1)]
    [InlineData(54, -1)]
    [InlineData(54, 0)]
    [InlineData(54, 1)]
    public void BothHorizonEndpoints_AreUnblockedDespiteTexelCoordinateRoundoff(int row, int rounding)
    {
        float v = (row + .5f) / 108f;
        v = rounding < 0 ? MathF.BitDecrement(v) : rounding > 0 ? MathF.BitIncrement(v) : v;
        var uv = new Vector2(96.5f / 192f, v);
        var shaderDirection = AtmosphereShaderMath.SkyDirection(uv, 6360.002f, 6360f);

        // At 2 m the float32 radius is 6360.001953125 km and its tangent projection is -4.984350681 km.
        // The view-ground test's eight-epsilon margin is 0.000004754 km. Below -4.984355 it hits ground.
        Assert.InRange(6360.002f * shaderDirection.Y, -4.984355f, -4.984346f);
        Assert.Equal(shaderDirection, AtmosphereLutMapping.SkyDirection(uv, 6360.002f, 6360f));
    }

    [Theory]
    [InlineData(52, false)]
    [InlineData(55, true)]
    public void AdjacentTexels_RemainOnTheirOwnSideOfTheHorizon(int row, bool ground)
    {
        var direction = AtmosphereShaderMath.SkyDirection(new Vector2(96.5f / 192f, (row + .5f) / 108f),
            6360.002f, 6360f);
        Assert.Equal(ground, 6360.002f * direction.Y < -4.984355f);
    }
}
