using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageRestorationGpuTests
{
    [GpuTheory]
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
    [InlineData(false, 9)]
    [InlineData(true, 9)]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    public void Actual_ordinary_routes_reject_restoration_locally_and_resolve_current(bool gpu, int kind)
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.UseGpuSkinning = gpu;
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.RenderScale = RenderScale.MatchViewport;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        h.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
        h.Scene.CameraOverride = new Camera();
        Vector4 weights = kind switch
        {
            0 => new(.5f, 0, 0, 0),
            1 => new(1, 0, 0, 0),
            2 => new(2, 0, 0, 0),
            3 => new(1.5f, -.5f, 0, 0),
            4 => Vector4.Zero,
            5 => new(1, -1, 0, 0),
            9 => new(1e-39f, 1, 0, 0),
            10 => new(.5f, 0, 0, 0),
            _ => new(.5f, .5f, 0, 0)
        };
        var mesh = Triangle(6, weights);
        var restored = h.Scene.LoadSkinnedMesh(mesh);
        var unrelated = h.Scene.LoadSkinnedMesh(Triangle(-10, Vector4.UnitX));
        Matrix4x4[] identity = [Matrix4x4.Identity, Matrix4x4.Identity];
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity];
        Matrix4x4 model = Matrix4x4.Identity;
        if (kind == 4 || kind == 5) palette[0].M44 = float.NaN;
        if (kind == 6) palette[0].M44 = 2;
        if (kind == 7) palette[0].M14 = .125f;
        if (kind == 8) { model = Matrix4x4.CreateTranslation(2, 0, 0); model.M14 = .01f; }
        if (kind == 9) palette[0].M11 = 1e38f;
        if (kind == 10) { model = Matrix4x4.CreateTranslation(2, 0, 0); h.Scene.RenderOrigin = new(128, 0, 0); }
        CoverageComputeHistoryGpuTests.Draw(h);
        Other(); h.Render(320, 160);
        if (kind == 10) h.Scene.RenderOrigin = new(256, 0, 0);
        h.Begin();
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(restored, model) { Motion = MotionKey.From(10) }, palette);
        Other(); h.Render(320, 160);
        float[] mask = h.Mask();
        var motion = h.Scene.ReadMotionTargetForTests().Motion;
        float[] confidence = TemporalTextureIo.Read(h.NativeDevice, h.Scene.TemporalHistory.Confidence(h.Scene.TemporalHistory.WriteIndex));
        int[] restoredPixels = Enumerable.Range(0, motion.Length).Where(i => i % 320 >= 160 && !MotionMath.IsBackground(motion[i])).ToArray();
        int[] unrelatedPixels = Enumerable.Range(0, motion.Length).Where(i => i % 320 < 100 && !MotionMath.IsBackground(motion[i])).ToArray();
        Assert.True(restoredPixels.Length > 20); Assert.True(unrelatedPixels.Length > 20);
        Assert.All(restoredPixels, i => Assert.Equal(1f, mask[i]));
        Assert.All(unrelatedPixels, i => Assert.Equal(0f, mask[i]));
        Assert.Equal(0f, mask[0]); Assert.Equal(0f, mask[^1]);
        Assert.True(unrelatedPixels.Count(i => confidence[i * 2] > 1f / 15f + .001f) > 20);
        var resolved = CoverageTemporalCurrentProbe.AssertRejectedPixelsMatchCurrent(h.NativeDevice, h.Scene, 320, 160,
            mask.Select(value => value == 0 ? (byte)0 : (byte)255).ToArray());
        CoverageHistoryEvidence.Write(h.NativeDevice, $"restoration-route{gpu}-kind{kind}", new
        {
            Width = 320,
            Height = 160,
            ActualGpuSkinning = gpu,
            InputCase = kind,
            ActualRestoredVisiblePixels = restoredPixels,
            ActualUnrelatedVisiblePixels = unrelatedPixels,
            ActualRestoredMissedPixelCount = restoredPixels.Count(i => mask[i] == 0),
            ActualUnrelatedRejectedPixelCount = unrelatedPixels.Count(i => mask[i] != 0),
            ActualUnrelatedAccumulatedPixelCount = unrelatedPixels.Count(i => confidence[i * 2] > 1f / 15f + .001f),
            ActualMaskBytes = mask.Select(value => value == 0 ? (byte)0 : (byte)255).ToArray(),
            ActualVisibleSamples = restoredPixels.Concat(unrelatedPixels).Select(i => new
            {
                Pixel = i,
                ActualMotionWords = new[] { Word(motion[i].X), Word(motion[i].Y) },
                ActualColourWords = Enumerable.Range(0, 4).Select(c => Word(resolved.ActualColour[i * 4 + c])).ToArray(),
                ActualConfidence = confidence[i * 2],
            }).ToArray(),
            InjectedReferenceControl = "Production resolve with history disabled, actual scene inputs retained",
            ReferenceCurrentVisibleSamples = restoredPixels.Select(i => new
            {
                Pixel = i,
                ReferenceColourWords = Enumerable.Range(0, 4).Select(c => Word(resolved.ReferenceColour[i * 4 + c])).ToArray(),
                ReferenceConfidence = resolved.ReferenceConfidence[i * 2],
            }).ToArray(),
            ResolvedComparedPixelCount = resolved.ComparedPixels,
            GlobalHistoryValid = h.Scene.TemporalHistory.IsValid,
        });
        Assert.True(h.Scene.TemporalHistory.IsValid);
        void Other() => h.Scene.DrawSkinned(new SkinnedInstanceDraw(unrelated, Matrix4x4.Identity) { Motion = MotionKey.From(20) }, identity);
    }

    static ushort Word(float value) => BitConverter.HalfToUInt16Bits((Half)value);

    static SkinnedGltfMesh Triangle(float x, Vector4 weights)
    {
        SkinnedVertex Vertex(float px, float y) => new()
        { Position = new(px, y, 0), Normal = Vector3.UnitZ, Color = Vector4.One, BoneIndices = new(0, 1, 0, 1), BoneWeights = weights };
        return new([Vertex(x - 1, -2), Vertex(x + 1, -2), Vertex(x, 2)], [0, 1, 2, 2, 1, 0],
            [Matrix4x4.Identity, Matrix4x4.Identity], [Matrix4x4.Identity, Matrix4x4.Identity]);
    }
}
