using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeHistoryGpuTests
{
    [GpuFact]
    public void Unsafe_prior_clip_products_seed_current_gpu_data_and_reject_only_the_local_transition()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        h.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
        var unsafeProjection = CoverageMotionProjectionTests.OverflowProjection(0);
        unsafeProjection.M11 = 1e37f; unsafeProjection.M21 = -1e37f;
        var camera = new HistoryCamera { ProjectionOverride = unsafeProjection };
        h.Scene.CameraOverride = camera;
        var first = Draw(h, model: Matrix4x4.CreateTranslation(40, 40, 0)); h.Render(96, 72);
        Assert.True(first.Preparation!.MotionPresented);
        Assert.True(first.Preparation.WorldEnvelope.Relative.Min.X > 34);
        Assert.True(Matrix4x4.Invert(h.Scene.CurrentFrameView.ViewProjection, out var inverse));
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++) Assert.True(float.IsFinite(inverse[row, column]));
        var firstDraw = Assert.Single(h.NativeDraws);
        uint[] prior = CoverageSelectedReadback.Read(h.NativeDevice, firstDraw.Surface.Current);
        camera.ProjectionOverride = null;
        var current = Draw(h, .4f); h.Render(96, 72);
        Assert.NotNull(h.Scene.PreviousFrameView);
        Assert.True(h.Scene.TemporalHistory.IsValid);
        Assert.False(current.Preparation!.MotionReusable);
        var draw = Assert.Single(h.NativeDraws);
        uint[] rawCurrent = CoverageSelectedReadback.Read(h.NativeDevice, draw.Surface.Current);
        Assert.Equal(prior, CoverageSelectedReadback.Read(h.NativeDevice, draw.Surface.Previous));
        Assert.Equal(1u, rawCurrent[draw.Surface.CurrentBase + 15]);
        Assert.Equal(Matrix4x4.Identity, draw.PreviousModel);
        uint[] selected = VerifySelection(h, draw, out int mismatches);
        float[] mask = h.Mask();
        Assert.Contains(1f, mask); Assert.Contains(0f, mask);
        Assert.Equal(0f, mask[0]); Assert.Equal(0f, mask[^1]);
        var motion = h.Scene.ReadMotionTargetForTests().Motion.Where(value => !MotionMath.IsBackground(value)).ToArray();
        Assert.NotEmpty(motion);
        Assert.All(motion, value => Assert.True(float.IsFinite(value.X) && float.IsFinite(value.Y)));
        var resolved = CoverageTemporalCurrentProbe.AssertRejectedPixelsMatchCurrent(h.NativeDevice, h.Scene, 96, 72,
            mask.Select(value => value == 0 ? (byte)0 : (byte)255).ToArray());
        CoverageHistoryEvidence.Write(h.NativeDevice, "unsafe-prior-projection", new
        {
            Width = 96,
            Height = 72,
            Actor = current.Preparation.Actor.Value,
            ActualCurrentBufferId = CoverageHistoryEvidence.BufferId(draw.Surface.Current),
            ActualPreviousBufferId = CoverageHistoryEvidence.BufferId(draw.Surface.Previous),
            draw.Surface.CurrentBase,
            draw.Surface.PreviousBase,
            ActualCurrentHeader = rawCurrent.Take(16).ToArray(),
            ActualCommittedPriorHeader = prior.Take(16).ToArray(),
            ActualEffectivePreviousCornerWords = selected,
            SelectionMismatchCount = mismatches,
            ActualMask = mask,
            RejectedPixelCount = mask.Count(value => value != 0),
            PreservedPixelCount = mask.Count(value => value == 0),
            ActualNonBackgroundMotion = motion.Select(value => new[] { value.X, value.Y }).ToArray(),
            GlobalHistoryValid = h.Scene.TemporalHistory.IsValid,
            ActualResolvedColour = resolved.ActualColour,
            ActualResolvedConfidence = resolved.ActualConfidence,
            InjectedReferenceControl = "Same production resolve with history disabled, actual scene inputs retained",
            ReferenceCurrentColour = resolved.ReferenceColour,
            ReferenceCurrentConfidence = resolved.ReferenceConfidence,
            ResolvedComparedPixelCount = resolved.ComparedPixels,
        });
    }
}
