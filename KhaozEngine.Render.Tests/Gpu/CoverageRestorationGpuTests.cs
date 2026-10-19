using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed partial class CoverageRestorationGpuTests
{
    [GpuFact]
    public void Raw_half_weight_restoration_rejects_actual_gpu_pixels_and_preserves_unrelated_history()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.RenderScale = RenderScale.MatchViewport;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        h.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
        h.Scene.CameraOverride = new Camera();
        var restored = h.Scene.LoadSkinnedMesh(Triangle(6, .5f));
        var unrelated = h.Scene.LoadSkinnedMesh(Triangle(-10, 1));
        Matrix4x4[] palette = [Matrix4x4.Identity];
        CoverageComputeHistoryGpuTests.Draw(h);
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(unrelated, Matrix4x4.Identity) { Motion = MotionKey.From(20) }, palette);
        h.Render(320, 160);
        h.Begin();
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(restored, Matrix4x4.Identity) { Motion = MotionKey.From(10) }, palette);
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(unrelated, Matrix4x4.Identity) { Motion = MotionKey.From(20) }, palette);
        h.Render(320, 160);
        float[] mask = h.Mask();
        var motion = h.Scene.ReadMotionTargetForTests().Motion;
        float[] confidence = TemporalTextureIo.Read(h.NativeDevice, h.Scene.TemporalHistory.Confidence(h.Scene.TemporalHistory.WriteIndex));
        int[] restoredPixels = Enumerable.Range(0, motion.Length).Where(i => i % 320 >= 160 && !MotionMath.IsBackground(motion[i])).ToArray();
        int[] unrelatedPixels = Enumerable.Range(0, motion.Length).Where(i => i % 320 < 100 && !MotionMath.IsBackground(motion[i])).ToArray();
        int missed = restoredPixels.Count(i => mask[i] == 0);
        CoverageHistoryEvidence.Write(h.NativeDevice, "restoration-half-weight", new
        {
            Width = 320,
            Height = 160,
            ActualRestoredVisiblePixels = restoredPixels,
            ActualUnrelatedVisiblePixels = unrelatedPixels,
            ActualRestoredMissedPixelCount = missed,
            ActualUnrelatedRejectedPixelCount = unrelatedPixels.Count(i => mask[i] != 0),
            ActualMask = mask,
            ActualMotion = motion.Select(v => new[] { v.X, v.Y }).ToArray(),
            ActualResolvedConfidence = confidence,
            ReferenceRestCenter = new[] { 6f, 0, 0, 1 },
            ReferenceGpuHomogeneousCenter = new[] { 3f, 0, 0, .5f },
        });
        Assert.True(restoredPixels.Length > 20);
        Assert.True(unrelatedPixels.Length > 20);
        Assert.Equal(0, unrelatedPixels.Count(i => mask[i] != 0));
        Assert.Equal(0f, mask[0]); Assert.Equal(0f, mask[^1]);
        Assert.True(unrelatedPixels.Count(i => confidence[i * 2] > 1f / 15f + .001f) > 20);
        Assert.Equal(0, missed);
        Assert.True(h.Scene.TemporalHistory.IsValid);
    }

    static SkinnedGltfMesh Triangle(float x, float weight)
    {
        SkinnedVertex Vertex(float px, float y) => new()
        { Position = new(px, y, 0), Normal = Vector3.UnitZ, Color = Vector4.One, BoneWeights = new(weight, 0, 0, 0) };
        return new([Vertex(x - 1, -2), Vertex(x + 1, -2), Vertex(x, 2)], [0, 1, 2, 2, 1, 0],
            [Matrix4x4.Identity], [Matrix4x4.Identity]);
    }

    sealed class Camera : IIsoCamera3D, IRenderOriginAware
    {
        public Vector3 RenderOrigin { get; set; }
        public Matrix4x4 View => Matrix4x4.CreateTranslation(RenderOrigin + new Vector3(0, 0, -4));
        public Matrix4x4 Projection => Matrix4x4.CreateOrthographic(32, 32, .1f, 100);
        public Matrix4x4 ViewProjection => View * Projection;
        public Matrix4x4 AbsoluteViewProjection => Matrix4x4.CreateTranslation(-RenderOrigin) * ViewProjection;
        public Vector3 Eye => new(0, 0, 4);
        public Vector3 Forward => -Vector3.UnitZ;
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 screenPixel)
        { screenPixel = default; return false; }
    }
}
