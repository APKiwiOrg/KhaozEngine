using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageSupportTransformTests
{
    [Fact]
    public void Supported_affine_inverse_keeps_an_exact_homogeneous_column()
    {
        var frame = Matrix4x4.CreateScale(.83f, 1.17f, .91f) * Matrix4x4.CreateRotationZ(.19f)
            * Matrix4x4.CreateTranslation(.03f, -.02f, .01f);
        var source = CoverageSupportTests.Source([new(0, 0, .2f), new(1, 0, .2f), new(0, 1, .2f)], [(0, 1, 2)]);
        var support = CoverageSupportSurface.Compile(source, [new(0, 0, 0, 1)], frame);
        var inverse = support.SourceToCoverage;
        Assert.Equal(0, inverse.M14);
        Assert.Equal(0, inverse.M24);
        Assert.Equal(0, inverse.M34);
        Assert.Equal(1, inverse.M44);
        foreach (Vector3 point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.True(Vector3.Distance(point, Vector3.Transform(Vector3.Transform(point, inverse), frame)) < 2e-7f);
        var query = CoverageAffinePoint.Project(new(.25f, .25f, .2f), inverse);
        Assert.True(support.TryGetHeightBounds(query, out var height));
        Assert.InRange(height.Min, .20879119, .20879123);
        Assert.InRange(height.Max, .20879119, .20879123);
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(50)]
    public void Finite_well_conditioned_affine_does_not_depend_on_float_determinant_range(int exponent)
    {
        float scale = MathF.ScaleB(1, exponent);
        var source = CoverageSupportTests.Source([Vector3.Zero, new(scale, 0, 0), new(0, scale, 0)], [(0, 1, 2)]);
        var support = CoverageSupportSurface.Compile(source, [new(0, 0, 0, 1)], Matrix4x4.CreateScale(scale));
        Assert.Equal(MathF.ScaleB(1, -exponent), support.SourceToCoverage.M11);
        Assert.Equal(new CoveragePoint3(1, 0, 0), support.PrecisePositions[1]);
        Assert.Equal(new CoveragePoint3(0, 1, 0), support.PrecisePositions[2]);
        Assert.True(support.TryGetHeight(new Vector2(.25f, .25f), out float height));
        Assert.Equal(0, height);
    }

    [Theory]
    [InlineData("homogeneous")]
    [InlineData("projective-x")]
    [InlineData("projective-y")]
    [InlineData("projective-z")]
    [InlineData("nonfinite-linear")]
    [InlineData("nonfinite-translation")]
    [InlineData("singular")]
    [InlineData("mirror")]
    [InlineData("ill-conditioned")]
    [InlineData("inverse-overflow")]
    public void Unsupported_authored_affine_inputs_still_refuse(string kind)
    {
        var frame = Matrix4x4.Identity;
        switch (kind)
        {
            case "homogeneous": frame.M44 = MathF.BitDecrement(1); break;
            case "projective-x": frame.M14 = float.Epsilon; break;
            case "projective-y": frame.M24 = float.Epsilon; break;
            case "projective-z": frame.M34 = float.Epsilon; break;
            case "nonfinite-linear": frame.M11 = float.NaN; break;
            case "nonfinite-translation": frame.M41 = float.PositiveInfinity; break;
            case "singular": frame.M33 = 0; break;
            case "mirror": frame.M11 = -1; break;
            case "ill-conditioned": frame.M11 = 1e-7f; break;
            case "inverse-overflow": frame = Matrix4x4.CreateScale(float.Epsilon); break;
        }
        var source = CoverageSupportTests.Source([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [(0, 1, 2)]);
        var error = Assert.Throws<InvalidDataException>(() => CoverageSupportSurface.Compile(source, [new(0, 0, 0, 1)], frame));
        Assert.Contains("synthetic", error.Message);
        Assert.Contains(kind == "ill-conditioned" ? "Ill-conditioned coverage transform" : "Unsupported coverage transform", error.Message);
    }
}
