using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageDisplacementTests
{
    [Fact]
    public void ProgramsIncludeOneIdenticalDisplacementFunction()
    {
        foreach (string source in new[] { ShaderSources.FoliageVert, ShaderSources.FoliageMotionVert })
        {
            Assert.Contains(ShaderSources.FoliageDisplacementGlsl, source);
            Assert.Equal(2, source.Split(ShaderSources.FoliageDisplacementGlsl, StringSplitOptions.None).Length);
            Assert.Contains(ShaderSources.FoliageUniformBlockGlsl, source);
        }
    }

    [Fact]
    public void LeanAndWindRespectTheProfile()
    {
        var settings = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f };
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero, settings,
            [new FoliageInteractor(new Vector3(-.1f, 0f, 0f), 2f, 1f)], 0f);

        Vector3 capped = FoliageWindMirror.TopOffset(u, Vector3.Zero, 1f,
            new FoliageMotionProfile(.3f, .15f, .4f, 0f));
        Assert.Equal(.15f, capped.X, 1e-6f);
        Assert.Equal(-(1f - MathF.Sqrt(1f - .15f * .15f)), capped.Y, 1e-6f);
        Assert.Equal(0f, capped.Z);
        Assert.Equal(Vector3.Zero, FoliageWindMirror.TopOffset(u, Vector3.Zero, 1f,
            new FoliageMotionProfile(0f, 0f, 0f, 0f)));

        Vector3 fullLean = FoliageWindMirror.TopOffset(u, Vector3.Zero, 1f,
            new FoliageMotionProfile(4f, 1f, 0f, 0f));
        Assert.Equal(1f, fullLean.X, 1e-6f);
        Assert.Equal(-1f, fullLean.Y, 1e-6f);
        Assert.True(float.IsFinite(fullLean.Y));

        FoliageUniforms wind = FoliageUniforms.Build(Vector3.Zero,
            settings with { WindStrength = .1f, WindDirection = Vector2.UnitX }, [], 1f);
        Vector3 blade = FoliageWindMirror.TopOffset(wind, Vector3.Zero, 1f);
        Vector3 shrub = FoliageWindMirror.TopOffset(wind, Vector3.Zero, 1f,
            new FoliageMotionProfile(1f, 1f, .4f, 0f));
        Assert.NotEqual(0f, blade.X);
        Assert.Equal(blade.X * .4f, shrub.X, 1e-6f);
        Assert.Equal(blade.Z * .4f, shrub.Z, 1e-6f);
    }

    [Fact]
    public void VertexWorldKeepsTheRootAndUsesSquaredLocalHeightWeight()
    {
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero,
            new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f },
            [new FoliageInteractor(new Vector3(-.1f, 0f, 0f), 2f)], 0f);
        var instance = Upright(new FoliageMotionProfile(.3f, .15f, 0f, 0f));

        Assert.Equal(Vector3.Zero, FoliageWindMirror.VertexWorld(u, instance, Vector3.Zero,
            Matrix4x4.Identity, Vector3.Zero, false, out bool rootRejected));
        Assert.False(rootRejected);
        Vector3 middle = FoliageWindMirror.VertexWorld(u, instance, Vector3.UnitY * .5f,
            Matrix4x4.Identity, Vector3.Zero, false, out bool rejected);
        Assert.False(rejected);
        Assert.Equal(.0375f, middle.X, 1e-6f);
        Assert.Equal(.5f - (1f - MathF.Sqrt(1f - .15f * .15f)) * .25f, middle.Y, 1e-6f);
    }

    [Fact]
    public void VertexWorldFadesHeightAndRebasesWithoutMovingTheAuthoredRoot()
    {
        FoliageUniforms u = FoliageUniforms.Build(Vector3.Zero,
            new FoliageRenderSettings { DrawRadius = 10f, FadeBandWidth = 4f, DistantDensity = 1f }, [], 0f);
        var instance = Upright(new FoliageMotionProfile(0f, 0f, 0f, 0f));
        instance.Model = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(8f, 0f, 0f);
        instance.Parameters = new Vector4(.1f, -1f, .5f, 0f);
        Vector3 origin = new(7f, 0f, 0f);

        Assert.Equal(new Vector3(1f, -2f, 0f), FoliageWindMirror.VertexWorld(u, instance,
            -Vector3.UnitY, Matrix4x4.Identity, origin, false, out bool rootRejected));
        Assert.False(rootRejected);
        Assert.Equal(new Vector3(1f, 0f, 0f), FoliageWindMirror.VertexWorld(u, instance,
            Vector3.UnitY, Matrix4x4.Identity, origin, false, out bool rejected));
        Assert.False(rejected);
    }

    [Fact]
    public void PreviousEvaluationUsesHistoryFocusClockInteractorsAndPixelScale()
    {
        var settings = new FoliageRenderSettings { DrawRadius = 100f, DistantDensity = 1f };
        FoliageUniforms previous = FoliageUniforms.Build(Vector3.Zero, settings,
            [new FoliageInteractor(new Vector3(-.1f, 0f, 0f), 2f)], 0f);
        FoliageUniforms current = FoliageUniforms.Build(new Vector3(200f, 0f, 0f), settings, [], 1f)
            .WithPrevious(previous);
        var instance = Upright(new FoliageMotionProfile(.3f, .15f, 0f, 0f));

        Vector3 now = FoliageWindMirror.VertexWorld(current, instance, Vector3.UnitY,
            Matrix4x4.Identity, Vector3.Zero, false, out bool rejected);
        Vector3 last = FoliageWindMirror.VertexWorld(current, instance, Vector3.UnitY,
            Matrix4x4.Identity, Vector3.Zero, true, out bool lastRejected);
        Assert.True(rejected);
        Assert.False(lastRejected);
        Assert.Equal(0f, now.X);
        Assert.Equal(.15f, last.X, 1e-6f);

        settings = settings with { WindStrength = .1f, WindDirection = Vector2.UnitX, WindFadeBladePixels = 1f };
        previous = FoliageUniforms.Build(Vector3.Zero, settings, [], 2f);
        current = FoliageUniforms.Build(Vector3.Zero, settings, [], 1f).WithPrevious(previous);
        current.WindFade.Y = 1f;
        current.WindFade.Z = 0f;
        instance = Upright();
        Assert.Equal(Vector3.UnitY, FoliageWindMirror.VertexWorld(current, instance, Vector3.UnitY,
            Matrix4x4.Identity, Vector3.Zero, false, out _));
        last = FoliageWindMirror.VertexWorld(current, instance, Vector3.UnitY,
            Matrix4x4.Identity, Vector3.Zero, true, out _);
        previous.WindFade.X = 0f;
        Vector3 expected = Vector3.UnitY + FoliageWindMirror.TopOffset(previous, Vector3.Zero, 1f);
        Assert.Equal(expected, last);
    }

    static ModelRenderer.FoliageInstanceData Upright(FoliageMotionProfile motion = default) => new()
    {
        Model = Matrix4x4.Identity,
        Parameters = new Vector4(.1f, 0f, 1f, 0f),
        Motion = motion.ToVector4(),
    };
}
