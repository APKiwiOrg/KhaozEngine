using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageOrdinaryBoundsTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    public void Ordinary_envelope_preserves_the_actual_route_position(bool gpu, int kind)
    {
        Vector4 weights = kind switch
        {
            0 => new(.5f, 0, 0, 0),
            1 => new(1, 0, 0, 0),
            2 => new(2, 0, 0, 0),
            3 => new(1.5f, -.5f, 0, 0),
            4 => Vector4.Zero,
            5 => new(1, -1, 0, 0),
            _ => new(.5f, .5f, 0, 0)
        };
        SkinnedVertex vertex = new() { Position = new(6, 2, 1), Normal = Vector3.UnitZ, BoneIndices = new(0, 1, 0, 1), BoneWeights = weights };
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(.25f, 0, 0)];
        Matrix4x4 model = Matrix4x4.CreateTranslation(-126, 1, 0);
        if (kind == 6) palette[0].M44 = 2;
        if (kind == 7) palette[0].M14 = .125f;
        if (kind == 8) model.M14 = .01f;
        var bounds = new CoverageActorBounds([vertex]).BoundOrdinary(palette, model, gpu);
        Vector4 actual = gpu
            ? Vector4.Transform(Vector4.Transform(new Vector4(vertex.Position, 1), SkinningMath.BlendSkinMatrix(palette, vertex.BoneIndices, weights)), model)
            : Vector4.Transform(new Vector4(SkinningMath.SkinVertex(vertex, palette).Position, 1), model);
        Inside(bounds, actual);
        if (kind == 0) Assert.InRange(bounds.W.Midpoint, gpu ? .499f : .999f, gpu ? .501f : 1.001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Raw_subnormal_weight_amplification_stays_in_a_tight_envelope(bool gpu)
    {
        var vertex = new SkinnedVertex { Position = Vector3.One, BoneIndices = new(0, 1, 0, 1), BoneWeights = new(1e-39f, 1, 0, 0) };
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity]; palette[0].M11 = 1e38f;
        var bounds = new CoverageActorBounds([vertex]).BoundOrdinary(palette, Matrix4x4.Identity, gpu);
        Inside(bounds, new(1 + 1e-39f * 1e38f, 1, 1, 1));
        Assert.InRange(bounds.X.Max, 1.09, 1.11);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Projective_palette_amplifies_raw_subnormal_position_without_assuming_w_one(bool gpu)
    {
        var vertex = new SkinnedVertex { Position = new(1e-39f, 0, 0), BoneWeights = new(1, 0, 0, 0) };
        Matrix4x4[] palette = [Matrix4x4.Identity]; palette[0].M14 = 1e38f;
        var bounds = new CoverageActorBounds([vertex]).BoundOrdinary(palette, Matrix4x4.Identity, gpu);
        Inside(bounds, new(1e-39f, 0, 0, gpu ? 1 + 1e-39f * 1e38f : 1));
        Assert.True(bounds.W.Min <= 1);
        Assert.InRange(bounds.W.Max, gpu ? 1.09 : 1, gpu ? 1.11 : 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Identity_and_zero_sum_branches_do_not_read_a_bad_palette(bool gpu, bool cancelling)
    {
        var vertex = new SkinnedVertex { Position = new(6, 2, 1), BoneWeights = cancelling ? new(1, -1, 0, 0) : Vector4.Zero };
        Matrix4x4[] palette = [new(float.NaN, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, float.NaN)];
        Inside(new CoverageActorBounds([vertex]).BoundOrdinary(palette, Matrix4x4.Identity, gpu), new(6, 2, 1, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nonfinite_palette_w_is_ignored_only_by_the_cpu_skin_route(bool gpu)
    {
        var vertex = new SkinnedVertex { Position = new(6, 2, 1), BoneWeights = new(1, 0, 0, 0) };
        Matrix4x4[] palette = [Matrix4x4.Identity]; palette[0].M44 = float.NaN;
        Assert.Equal(!gpu, new CoverageActorBounds([vertex]).BoundOrdinary(palette, Matrix4x4.Identity, gpu).IsFinite);
    }

    [Fact]
    public void Signed_affine_weights_keep_the_joint_total_correlation()
    {
        SkinnedVertex[] vertices = [new() { Position = Vector3.One, BoneWeights = new(2, -1, 0, 0) },
            new() { Position = Vector3.One, BoneWeights = new(3, -2, 0, 0) }];
        var bound = new CoverageActorBounds(vertices).BoundOrdinary([Matrix4x4.Identity], Matrix4x4.Identity, true);
        Assert.InRange(bound.W.Min, .999, 1); Assert.InRange(bound.W.Max, 1, 1.001);
    }

    [Fact]
    public void Warm_ordinary_bound_does_not_allocate()
    {
        var vertex = new SkinnedVertex { Position = Vector3.One, BoneWeights = new(.5f, 0, 0, 0) };
        var bounds = new CoverageActorBounds([vertex]); Matrix4x4[] palette = [Matrix4x4.Identity];
        _ = bounds.BoundOrdinary(palette, Matrix4x4.Identity, true);
        _ = bounds.BoundOrdinary(palette, Matrix4x4.Identity, true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = bounds.BoundOrdinary(palette, Matrix4x4.Identity, true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(result.IsFinite); Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reassociated_threshold_is_uncertain_only_on_the_gpu_route(bool gpu)
    {
        Vector4 weights = new(1, .75e-8f, -1, .5e-8f);
        Assert.True(weights.X + weights.Y + weights.Z + weights.W < 1e-8f);
        Assert.True((weights.X + weights.Z) + (weights.Y + weights.W) >= 1e-8f);
        var vertex = new SkinnedVertex { Position = new(6, 2, 1), BoneWeights = weights };
        Matrix4x4[] palette = [Matrix4x4.Identity]; palette[0].M44 = float.NaN;
        Assert.Equal(!gpu, new CoverageActorBounds([vertex]).BoundOrdinary(palette, Matrix4x4.Identity, gpu).IsFinite);
    }

    static void Inside(CoveragePositionBounds bounds, Vector4 point)
    {
        Assert.True(bounds.IsFinite);
        Assert.InRange(point.X, bounds.X.Min, bounds.X.Max); Assert.InRange(point.Y, bounds.Y.Min, bounds.Y.Max);
        Assert.InRange(point.Z, bounds.Z.Min, bounds.Z.Max); Assert.InRange(point.W, bounds.W.Min, bounds.W.Max);
    }
}
