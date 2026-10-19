using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageMotionProjectionTests
{
    internal static Matrix4x4 OverflowProjection(int component)
    {
        var projection = Matrix4x4.Identity;
        projection[0, component] = 1e38f;
        projection[1, component] = -1e38f;
        return projection;
    }

    static CoverageMotionHistory.Surface Surface(CoverageSurfaceBounds bounds) =>
        new(MotionKey.From(7), 1, default, Matrix4x4.Identity, bounds, true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Finite_invertible_prior_projection_rejects_binary32_product_overflow(int component)
    {
        var projection = OverflowProjection(component);
        Assert.True(Matrix4x4.Invert(projection, out var inverse));
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                Assert.True(float.IsFinite(inverse[row, column]));
        Assert.True(float.IsPositiveInfinity(4f * projection[0, component]));
        Assert.True(double.IsFinite(4d * projection[0, component] + 4d * projection[1, component]));
        var surface = Surface(new(new(4), new(5)));
        Assert.False(surface.TryRebase(Vector3.Zero, projection, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16777216)]
    public void Ordinary_and_large_origin_bounds_preserve_finite_prior_projection(float anchor)
    {
        var origin = new Vector3(anchor, 0, 0);
        var bounds = CoverageSurfaceBounds.FromRender(new(new(-1), new(1)), origin);
        var surface = Surface(bounds) with { Model = Matrix4x4.CreateTranslation(origin) };
        var projection = Matrix4x4.CreateTranslation(128, 0, -4) * Matrix4x4.CreateOrthographic(8, 8, .1f, 100);
        Assert.True(surface.TryRebase(origin + new Vector3(128, 0, 0), projection, out var model));
        Assert.Equal(new Vector3(-128, 0, 0), model.Translation);
    }
}
