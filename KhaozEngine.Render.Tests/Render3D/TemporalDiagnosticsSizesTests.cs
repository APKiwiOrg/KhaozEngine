using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The sizes, preset and ratio in <c>Scene3D.LastTemporalDiagnostics</c>: they describe the frame's first
/// render and nothing after it, a frame with no display area reports no sizes rather than a stale display size, the
/// history state is the one the resolve used, and reading all of it allocates nothing. Headless, on
/// <see cref="HeadlessSceneRig"/>.</summary>
[Collection("AllocSensitive")]
public sealed class TemporalDiagnosticsSizesTests
{
    static HeadlessSceneRig Resolving(TemporalUpscale preset)
    {
        var rig = new HeadlessSceneRig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.Upscale = preset;
        return rig;
    }

    /// <summary>The history state the diagnostics report is the live history's and the one the resolve was
    /// told.</summary>
    static void AssertMatchesTheResolve(Scene3D scene, bool valid, TemporalResetReason reason, string at)
    {
        TemporalDiagnostics d = scene.LastTemporalDiagnostics;
        Assert.True((valid, reason) == (d.HistoryValid, d.LastReset),
            $"{at}: the diagnostics report history valid {d.HistoryValid}, last reset {d.LastReset}");
        Assert.Equal((scene.TemporalHistory.IsValid, scene.TemporalHistory.LastReset), (d.HistoryValid, d.LastReset));
        Assert.Equal(valid ? 1f : 0f, scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
    }

    [Fact]
    public void TheSizesPresetAndRatioDescribeTheFramesFirstRender()
    {
        using HeadlessSceneRig rig = Resolving(TemporalUpscale.Quality);
        rig.Frame(240, 160);
        TemporalDiagnostics d = rig.Scene.LastTemporalDiagnostics;
        Assert.Equal((160, 107), (d.InternalWidth, d.InternalHeight));
        Assert.Equal((240, 160), (d.DisplayWidth, d.DisplayHeight));
        Assert.Equal(TemporalUpscale.Quality, d.Preset);
        Assert.Equal(160f / 240f, d.UpscaleRatio);
        Assert.Equal((-1L, 0, 0, 0), (d.CountsFrameIndex, d.DisoccludedPixels, d.ReactivePixels, d.ClippedPixels));
    }

    /// <summary>A later render inside the frame at another size, after a preset change, renders at its own internal
    /// size, and the diagnostics still describe the frame's first render, the preset it read included. The next frame
    /// reports the new preset and sizes.</summary>
    [Fact]
    public void ALaterRenderAndASettingsChangeLeaveTheFramesDiagnosticsAlone()
    {
        using HeadlessSceneRig rig = Resolving(TemporalUpscale.Quality);
        Scene3D scene = rig.Scene;
        rig.Frame(240, 160);
        rig.Frame(240, 160);
        TemporalDiagnostics frame = scene.LastTemporalDiagnostics;

        scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
        Assert.Equal(frame, scene.LastTemporalDiagnostics);   // a settings change after the frame reports nothing
        rig.Render(96, 64);   // a capture inside the same frame
        Assert.Equal((48, 32), (scene.RenderTargetWidth, scene.RenderTargetHeight));
        Assert.Equal(frame, scene.LastTemporalDiagnostics);

        rig.Frame(240, 160);
        TemporalDiagnostics next = scene.LastTemporalDiagnostics;
        Assert.Equal(TemporalUpscale.Performance, next.Preset);
        Assert.Equal((120, 80, 240, 160),
            (next.InternalWidth, next.InternalHeight, next.DisplayWidth, next.DisplayHeight));
        Assert.Equal(0.5f, next.UpscaleRatio);
    }

    /// <summary>A frame with no display area, such as a minimised window, keeps the last display size for the jitter
    /// cycle and the history key, and renders a one pixel internal target. Its diagnostics report no sizes and a ratio
    /// of 1 instead of pairing the two, while the frame index and preset stay current. The next shown frame reports its
    /// sizes again.</summary>
    [Fact]
    public void AFrameWithNoDisplayAreaReportsNoSizesRatherThanTheLastDisplaySizeBesideItsOnePixelTarget()
    {
        using HeadlessSceneRig rig = Resolving(TemporalUpscale.Quality);
        Scene3D scene = rig.Scene;
        rig.Frame(240, 160);
        rig.Frame(240, 160);

        rig.Frame(0, 0);
        Assert.Equal((1, 1), (scene.RenderTargetWidth, scene.RenderTargetHeight));   // what such a render allocates
        TemporalDiagnostics d = scene.LastTemporalDiagnostics;
        Assert.Equal(scene.CurrentFrameView.FrameIndex, d.FrameIndex);
        Assert.Equal((0, 0, 0, 0), (d.InternalWidth, d.InternalHeight, d.DisplayWidth, d.DisplayHeight));
        Assert.Equal(1f, d.UpscaleRatio);
        Assert.Equal(TemporalUpscale.Quality, d.Preset);
        Assert.Equal(-1L, d.CountsFrameIndex);

        rig.Frame(240, 160);
        d = scene.LastTemporalDiagnostics;
        Assert.Equal((160, 107, 240, 160), (d.InternalWidth, d.InternalHeight, d.DisplayWidth, d.DisplayHeight));
        Assert.Equal(160f / 240f, d.UpscaleRatio);
    }

    /// <summary>The settings a later render reads may differ from the frame's first render and be put back before the
    /// next frame, so the history key never sees them. The later render never touches the history targets, so nothing
    /// replaces them, the next frame reads history, and its diagnostics say so, as the resolve was told.</summary>
    [Fact]
    public void SettingsChangedAndRestoredAroundALaterRenderKeepTheHistoryTheDiagnosticsReport()
    {
        using HeadlessSceneRig rig = Resolving(TemporalUpscale.Quality);
        Scene3D scene = rig.Scene;
        for (int i = 0; i < 3; i++) rig.Frame(240, 160);
        AssertMatchesTheResolve(scene, valid: true, TemporalResetReason.FirstFrame, "steady");
        int generation = scene.TemporalHistory.TargetGeneration;

        scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
        scene.Post.Hdr.Enabled = !scene.Post.Hdr.Enabled;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        rig.Render(240, 160);
        Assert.Equal((240, 160), (scene.RenderTargetWidth, scene.RenderTargetHeight));   // it read the changed settings
        scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
        scene.Post.Hdr.Enabled = !scene.Post.Hdr.Enabled;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Frame(240, 160);

        Assert.Equal(generation, scene.TemporalHistory.TargetGeneration);
        AssertMatchesTheResolve(scene, valid: true, TemporalResetReason.FirstFrame, "after the restore");
    }

    /// <summary>A 2 by 2 display at UltraPerformance renders a 1 by 1 internal target, and so does a frame with no
    /// display area, which keeps the last display size for the history key. Minimising it changes nothing the key
    /// holds, so the frame's advance keeps the history, and then the resolve replaces the 2 by 2 history targets with 1
    /// by 1 ones, which hold nothing, and drops the history. The diagnostics report the history the resolve used, not
    /// the one the advance published, on that frame and on the frame shown again.</summary>
    [Fact]
    public void HistoryTargetsReplacedWithoutAKeyChangeReportTheHistoryTheResolveDropped()
    {
        using HeadlessSceneRig rig = Resolving(TemporalUpscale.UltraPerformance);
        Scene3D scene = rig.Scene;
        for (int i = 0; i < 3; i++) rig.Frame(2, 2);
        Assert.Equal((1, 1), (scene.RenderTargetWidth, scene.RenderTargetHeight));
        AssertMatchesTheResolve(scene, valid: true, TemporalResetReason.FirstFrame, "steady");
        int generation = scene.TemporalHistory.TargetGeneration;

        rig.Frame(0, 0);
        Assert.Equal((1, 1), (scene.RenderTargetWidth, scene.RenderTargetHeight));
        Assert.Equal(generation + 1, scene.TemporalHistory.TargetGeneration);
        AssertMatchesTheResolve(scene, valid: false, TemporalResetReason.Resize, "minimised");
        rig.Frame(0, 0);
        AssertMatchesTheResolve(scene, valid: true, TemporalResetReason.Resize, "still minimised");

        rig.Frame(2, 2);
        Assert.Equal(generation + 2, scene.TemporalHistory.TargetGeneration);
        AssertMatchesTheResolve(scene, valid: false, TemporalResetReason.Resize, "shown again");
        rig.Frame(2, 2);
        AssertMatchesTheResolve(scene, valid: true, TemporalResetReason.Resize, "steady again");
    }

    /// <summary>Latching the sizes and preset, publishing the diagnostics and composing them on every read are value
    /// work: a steady resolving frame with bloom on and a later render, read after each render, allocates
    /// nothing.</summary>
    [Fact]
    public void ResolvingFramesAndReadsOfTheirDiagnosticsAllocateNothing()
    {
        using HeadlessSceneRig rig = Resolving(TemporalUpscale.Quality);
        Scene3D scene = rig.Scene;
        scene.Post.Bloom.Enabled = true;
        long seen = 0;
        void Frame()
        {
            rig.Frame(240, 160);
            TemporalDiagnostics d = scene.LastTemporalDiagnostics;
            rig.Render(240, 160);
            TemporalDiagnostics after = scene.LastTemporalDiagnostics;
            seen += d.InternalWidth + after.DisplayWidth + (long)d.Preset + d.CountsFrameIndex;
        }

        for (int i = 0; i < 8; i++) Frame();
        AllocAssert.NoPerCallAllocation("16 resolving frames with a later render and two diagnostics reads each", () =>
        {
            for (int i = 0; i < 16; i++) Frame();
        });
        Assert.True(seen > 0);
        Assert.True(scene.LastTemporalDiagnostics.HistoryValid, "the measured loop never ran the valid-history path");
    }
}
