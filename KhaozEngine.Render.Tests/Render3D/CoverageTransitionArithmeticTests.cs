using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageTransitionArithmeticTests
{
    static FrameView View => new(Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
        Matrix4x4.Identity, Vector3.Zero, 100, 100, 1, Vector2.Zero);

    [Fact]
    public void Cancellation_bounds_include_both_float_associations_without_global_rejection()
    {
        var x = CoverageShaderPosition.FloatDot4(new(65536, 65536), CoverageIntervals.One,
            new(-65536, -65536), CoverageIntervals.One, new(.0025f, .0025f), CoverageIntervals.One,
            CoverageIntervals.Zero, CoverageIntervals.One);
        Assert.True(x.IsFinite);
        Assert.True(x.Min <= 0 && x.Max >= .0025f);
        Assert.True(x.Min > -.2 && x.Max < .2);
        var builder = new CoverageSurfaceBoundsBuilder(Vector3.Zero);
        builder.Add(new CoveragePositionBounds(x, CoverageIntervals.Zero, new(.5, .5), CoverageIntervals.One));
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(1); mask.EnsureSize(100, 100, null);
        mask.AddChangedBounds(new(builder.Build(), null, false));
        using var commands = new NullGpuCommandList();
        mask.Prepare(commands, View);
        Assert.True(mask.Rejects(50, 50));
        Assert.False(mask.Rejects(95, 50));
    }

    [Fact]
    public void Raw_gpu_weights_retain_homogeneous_w_through_model_and_projection()
    {
        var vertex = new SkinnedVertex { Position = new(.2f, 0, .5f), BoneWeights = new(2, 0, 0, 0) };
        var bounds = CoverageShaderPosition.Skinned(vertex, [Matrix4x4.Identity], Matrix4x4.CreateTranslation(.3f, 0, 0));
        Assert.True(bounds.IsFinite);
        Assert.True(bounds.X.Min <= 1 && bounds.X.Max >= 1);
        Assert.True(bounds.W.Min <= 2 && bounds.W.Max >= 2);
        var builder = new CoverageSurfaceBoundsBuilder(Vector3.Zero);
        builder.Add(bounds);
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(1); mask.EnsureSize(100, 100, null);
        mask.AddChangedBounds(new(builder.Build(), null, false));
        using var commands = new NullGpuCommandList();
        mask.Prepare(commands, View);
        Assert.True(mask.Rejects(75, 50));
        Assert.False(mask.Rejects(85, 50));
        Assert.False(mask.Rejects(95, 50));
    }

    [Fact]
    public void Zero_weight_branch_does_not_read_an_unused_nonfinite_palette()
    {
        var vertex = new SkinnedVertex { Position = new(.2f, 0, .5f) };
        var invalid = Matrix4x4.Identity; invalid.M11 = float.NaN;
        var bounds = CoverageShaderPosition.Skinned(vertex, [invalid], Matrix4x4.CreateTranslation(.3f, 0, 0));
        Assert.True(bounds.IsFinite);
        Assert.True(bounds.X.Min <= .5 && bounds.X.Max >= .5);
        Assert.True(bounds.W.Min <= 1 && bounds.W.Max >= 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uncertifiable_gpu_arithmetic_uses_explicit_uncertainty_rejection(bool overflow)
    {
        var vertex = new SkinnedVertex { Position = new(2, 0, .5f), BoneWeights = new(1, 0, 0, 0) };
        var bone = Matrix4x4.Identity;
        if (overflow) bone.M11 = float.MaxValue;
        else vertex.BoneWeights = new(float.NaN, 0, 0, 0);
        var bounds = CoverageShaderPosition.Skinned(vertex, [bone], Matrix4x4.Identity);
        Assert.False(bounds.IsFinite);
        var builder = new CoverageSurfaceBoundsBuilder(Vector3.Zero);
        builder.Add(bounds);
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(1); mask.EnsureSize(100, 100, null);
        mask.AddChangedBounds(new(builder.Build(), null, false));
        using var commands = new NullGpuCommandList();
        mask.Prepare(commands, View);
        Assert.True(mask.Rejects(0, 0));
        Assert.True(mask.Rejects(99, 99));
    }
}
