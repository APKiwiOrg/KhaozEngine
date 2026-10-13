using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageShiverTests
{
    static FoliageRenderSettings Still => new() { DrawRadius = 100f, DistantDensity = 1f };
    static ModelRenderer.FoliageInstanceData Upright(FoliageMotionProfile motion) => new()
    {
        Model = Matrix4x4.Identity,
        Parameters = new Vector4(.1f, 0f, 1f, 0f),
        Motion = motion.ToVector4(),
    };

    [Fact]
    public void ZeroAgitationHasNoShiver()
    {
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero, Still, [new FoliageInteractor(Vector3.Zero, 2f)], 0f);
        var instance = Upright(new FoliageMotionProfile(0f, 0f, 0f, .035f));
        foreach (Vector3 position in new[] { Vector3.Zero, Vector3.UnitY * .5f, Vector3.UnitY })
            Assert.Equal(position, FoliageWindMirror.VertexWorld(u, instance, position, Matrix4x4.Identity, Vector3.Zero, false, out _));
        Assert.Equal(Vector3.Zero, FoliageWindMirror.TopOffset(u, Vector3.Zero, 1f, new FoliageMotionProfile(0f, 0f, 0f, .035f)));
    }

    [Fact]
    public void ShiverLeavesTheRootFixedAndMovesUpperVertices()
    {
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero, Still, [new FoliageInteractor(Vector3.Zero, 2f, 1f, 1f)], 0f);
        var motion = new FoliageMotionProfile(0f, 0f, 0f, .035f);
        var instance = Upright(motion);
        Assert.Equal(Vector3.Zero, FoliageWindMirror.VertexWorld(u, instance, Vector3.Zero, Matrix4x4.Identity, Vector3.Zero, false, out _));
        Vector3 top = FoliageWindMirror.VertexWorld(u, instance, Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, false, out _);
        Assert.Equal(.035f, new Vector2(top.X, top.Z).Length(), 1e-6f);
        Assert.Equal(new Vector3(MathF.Sin(2.7f) * .035f, 1f, MathF.Cos(2.7f) * .035f), top);
        Assert.Equal(top - Vector3.UnitY, FoliageWindMirror.TopOffset(u, Vector3.Zero, 1f, motion));
        Vector3 middle = FoliageWindMirror.VertexWorld(u, instance, Vector3.UnitY * .5f, Matrix4x4.Identity, Vector3.Zero, false, out _);
        Assert.Equal(.035f * .25f, new Vector2(middle.X, middle.Z).Length(), 1e-6f);
    }

    [Fact]
    public void FullLeanStillShivers()
    {
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero, Still, [new FoliageInteractor(new Vector3(-.1f, 0f, 0f), 2f, 1f, 1f)], 0f);
        var motion = new FoliageMotionProfile(.3f, .15f, 0f, .035f);
        var rest = Upright(new FoliageMotionProfile(.3f, .15f, 0f, 0f));
        Vector3 leaned = FoliageWindMirror.VertexWorld(u, rest, Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, false, out _);
        Assert.Equal(.15f, leaned.X, 1e-6f);
        Vector3 shivering = FoliageWindMirror.VertexWorld(u, Upright(motion), Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, false, out _);
        // smoothstep(0, 2, .1) is .00725, so the strongest interactor's falloff is .99275.
        Assert.Equal(.035f * .99275f, Vector3.Distance(leaned, shivering), 1e-6f);
        Assert.Equal(leaned.Y, shivering.Y);
        Assert.True(shivering.X > .15f);
    }

    [Fact]
    public void FourInteractorShiverFitsTheApprovedBounds()
    {
        var actor = new FoliageInteractor(Vector3.Zero, 2f, 1f, 1f);
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero, Still, [actor, actor, actor, actor], 0f);
        var instance = Upright(new FoliageMotionProfile(0f, 0f, 0f, .25f));
        Vector3 top = FoliageWindMirror.VertexWorld(u, instance, Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, false, out _);
        Assert.Equal(.25f, new Vector2(top.X, top.Z).Length(), 1e-6f);
        Assert.InRange(new Vector2(top.X, top.Z).Length(), 0f, .25f);
    }

    [Fact]
    public void TheStrongestSingleInteractorsProductControlsShiver()
    {
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero, Still,
            [new FoliageInteractor(Vector3.Zero, 2f, .2f, 1f), new FoliageInteractor(Vector3.Zero, 2f, 1f, .4f)], 0f);
        var instance = Upright(new FoliageMotionProfile(0f, 0f, 0f, .035f));
        Vector3 top = FoliageWindMirror.VertexWorld(u, instance, Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, false, out _);
        Assert.Equal(.035f * .4f, new Vector2(top.X, top.Z).Length(), 1e-6f);
    }

    [Fact]
    public void PreviousAgitationAndAbsolutePhaseSurviveRebasing()
    {
        Vector3 root = new(12f, 0f, 8f);
        FoliageUniforms last = FoliageUniforms.Build(root, Still, [new FoliageInteractor(root, 2f, 1f, .2f)], 0f);
        FoliageUniforms now = FoliageUniforms.Build(root, Still, [new FoliageInteractor(root, 2f, 1f, 1f)], .1f).WithPrevious(last);
        var instance = Upright(new FoliageMotionProfile(0f, 0f, 0f, .035f));
        instance.Model = Matrix4x4.CreateTranslation(root);
        Vector3 before = FoliageWindMirror.VertexWorld(last, instance, Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, false, out _);
        Vector3 previous = FoliageWindMirror.VertexWorld(now, instance, Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, true, out _);
        Assert.Equal(before, previous);
        Vector3 origin = new(10f, 0f, 5f);
        Vector3 rebased = FoliageWindMirror.VertexWorld(now, instance, Vector3.UnitY, Matrix4x4.Identity, origin, false, out _);
        Vector3 absolute = FoliageWindMirror.VertexWorld(now, instance, Vector3.UnitY, Matrix4x4.Identity, Vector3.Zero, false, out _);
        Assert.Equal(absolute.X - origin.X, rebased.X, 1e-6f);
        Assert.Equal(absolute.Z - origin.Z, rebased.Z, 1e-6f);
    }
}
