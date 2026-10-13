using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class FoliageShiverMotionGpuTests
{
    [GpuFact]
    public void ShiveringShrubMotionMatchesCurrentAndPreviousOracle()
    {
        const int Width = 320, Height = 180;
        using var fx = new TemporalFixture(Width, Height, s =>
        {
            s.ForceTemporalForTests = true;
            s.Camera.OrthoSize = 3f;
            s.Camera.Target = Vector3.UnitY * .5f;
        });
        Vector3[] positions = [new(-.6f, .25f, 0f), new(.6f, .55f, .1f), new(-.15f, 1f, -.1f)];
        ModelVertex[] vertices =
        [
            new(positions[0], Vector3.UnitZ, Vector4.One),
            new(positions[1], Vector3.UnitZ, Vector4.One),
            new(positions[2], Vector3.UnitZ, Vector4.One),
            new(Vector3.Zero, Vector3.UnitY, Vector4.One), // unused root fixes the authored height at one metre
        ];
        MeshHandle mesh = fx.Scene.LoadMesh(new GltfMesh(vertices, new ushort[] { 0, 1, 2 }));
        var profile = new FoliageMotionProfile(.3f, .15f, .4f, .035f);
        using FoliageBatch batch = fx.Scene.CreateFoliageBatch([new FoliageInstance(mesh, Matrix4x4.Identity, .1f, profile)]);
        var settings = new FoliageRenderSettings
        {
            DrawRadius = 100f, DistantDensity = 1f, WindStrength = .3f, WindDirection = Vector2.UnitX, WindFadeBladePixels = 30f,
        };
        void Draw(Scene3D scene, int n)
        {
            scene.EffectTimeSeconds = n == 0 ? 0f : .1f;
            scene.Camera.Zoom = n == 0 ? 1f : 1.2f;
            scene.DrawFoliage(batch, Vector3.Zero, settings, [new FoliageInteractor(Vector3.Zero, 2f, 1f, n == 0 ? .2f : 1f)]);
        }
        fx.Frame(Draw);
        FrameView then = fx.Scene.CurrentFrameView;
        var last = fx.Scene.FoliageUniformsForTests[0];
        fx.Frame(Draw);
        FrameView now = fx.Scene.CurrentFrameView;
        var current = fx.Scene.FoliageUniformsForTests[0];
        Assert.NotNull(fx.Scene.PreviousFrameView);
        Assert.Equal(.2f, current.PrevAgitation.X);
        Assert.NotEqual(last.WindFade.Y, current.WindFade.Y);
        var instance = new ModelRenderer.FoliageInstanceData
        {
            Model = Matrix4x4.Identity, Parameters = new Vector4(.1f, 0f, 1f, 0f), Motion = profile.ToVector4(),
        };
        var rasterClip = new Vector4[3];
        var currentClip = new Vector4[3];
        var previousClip = new Vector4[3];
        for (int i = 0; i < 3; i++)
        {
            Vector3 here = FoliageWindMirror.VertexWorld(current, instance, positions[i], now.ViewProjection, now.RenderOrigin, false, out bool rejected);
            Vector3 before = FoliageWindMirror.VertexWorld(last, instance, positions[i], then.ViewProjection, then.RenderOrigin, false, out bool lastRejected);
            Assert.False(rejected || lastRejected);
            rasterClip[i] = Vector4.Transform(new Vector4(here, 1f), now.JitteredViewProjection);
            currentClip[i] = Vector4.Transform(new Vector4(here, 1f), now.ViewProjection);
            previousClip[i] = Vector4.Transform(new Vector4(before, 1f), then.ViewProjection);
        }
        Vector2 Expected(int x, int y) => ExpectedMotion(x, y, rasterClip, currentClip, previousClip, Width, Height);
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), Expected, .05f);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");
    }

    // The raster barycentrics are perspective-corrected before interpolating the unjittered clip varyings.
    // A rigid-translation oracle would miss the local-position contribution to each leaf's phase.
    internal static Vector2 ExpectedMotion(int x, int y, Vector4[] rasterClip, Vector4[] currentClip,
        Vector4[] previousClip, int width, int height)
    {
        Vector2 Screen(Vector4 clip) => (new Vector2(clip.X, -clip.Y) / clip.W + Vector2.One) * .5f * new Vector2(width, height);
        Vector2 a = Screen(rasterClip[0]), b = Screen(rasterClip[1]), c = Screen(rasterClip[2]);
        Vector2 point = new(x + .5f, y + .5f);
        static float Cross(Vector2 left, Vector2 right) => left.X * right.Y - left.Y * right.X;
        float area = Cross(b - a, c - a);
        float wa = Cross(b - point, c - point) / area / rasterClip[0].W;
        float wb = Cross(c - point, a - point) / area / rasterClip[1].W;
        float wc = Cross(a - point, b - point) / area / rasterClip[2].W;
        float reciprocalSum = wa + wb + wc;
        Vector4 here = (currentClip[0] * wa + currentClip[1] * wb + currentClip[2] * wc) / reciprocalSum;
        Vector4 before = (previousClip[0] * wa + previousClip[1] * wb + previousClip[2] * wc) / reciprocalSum;
        return MotionMath.UvMotion(here, before) * new Vector2(width, height);
    }

    [Fact]
    public void ExpectedMotionPerspectiveCorrectsRasterWeights()
    {
        // Screen triangle (0,0), (2,0), (0,2). At (.5,.5), raster weights are (.5,.25,.25),
        // corrected by w=(1,2,4) to (8/11,2/11,1/11). Only the last vertex moved one clip unit in X.
        Vector4[] raster = [new(-1f, 1f, 0f, 1f), new(2f, 2f, 0f, 2f), new(-4f, -4f, 0f, 4f)];
        Vector4[] current = [new(0f, 0f, 0f, 1f), new(0f, 0f, 0f, 1f), new(1f, 0f, 0f, 1f)];
        Vector4[] previous = [new(0f, 0f, 0f, 1f), new(0f, 0f, 0f, 1f), new(0f, 0f, 0f, 1f)];
        Assert.Equal(1f / 11f, ExpectedMotion(0, 0, raster, current, previous, 2, 2).X, 1e-6f);
        Assert.Equal(0f, ExpectedMotion(0, 0, raster, current, previous, 2, 2).Y);
    }
}
