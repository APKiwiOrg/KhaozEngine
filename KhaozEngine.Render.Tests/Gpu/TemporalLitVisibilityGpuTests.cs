using System;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalLitVisibilityGpuTests(LitVisibilityGpuScene stage, ITestOutputHelper output)
    : IClassFixture<LitVisibilityGpuScene>
{
    void Identity() => output.WriteLine($"{stage.Device.Backend} {stage.Device.Capabilities.DeviceName}");

    static void Nontrivial(byte[] visibility)
    {
        Assert.True(visibility.Count(v => v < 100) > 30, "the opaque receiver must carry a shadow");
        Assert.True(visibility.Count(v => v == 255) > 30, "the receiver and background must also carry full light");
        Assert.True(visibility.Any(v => v > 0 && v < 255), "the filtered shadow edge must have fractional visibility");
    }

    [GpuTheory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(5, false)] [InlineData(5, true)]
    public void AllLitFamiliesExportTheSampledShadow(int family, bool motion)
    {
        Identity();
        stage.Configure(motion);
        var image = stage.Capture(s => { s.Receiver(family); s.Caster(); });
        Nontrivial(image.Visibility);
        Assert.Equal(motion, stage.Scene.MotionResourcesForTests is not null);
    }

    [GpuTheory]
    [InlineData(false)] [InlineData(true)]
    public void BackgroundDisabledShadowsAndStrengthHaveTheSpecifiedVisibility(bool motion)
    {
        Identity();
        stage.Configure(motion);
        Assert.All(stage.Capture(_ => { }).Visibility, v => Assert.Equal(255, v));
        var full = stage.Capture(s => { s.Receiver(0); s.Caster(); }).Visibility;
        Nontrivial(full);
        stage.Scene.Post.Quality.Shadows.ShadowStrength = .4f;
        var weak = stage.Capture(_ => { }, begin: false).Visibility;
        for (int p = 0; p < full.Length; p++)
            Assert.InRange(Math.Abs(weak[p] - (255 - .4f * (255 - full[p]))), 0, 1.5f);
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Assert.All(stage.Capture(s => { s.Receiver(0); s.Caster(); }).Visibility, v => Assert.Equal(255, v));
    }

    [GpuTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void AllSupportedMsaaCountsExplicitlyResolveNontrivialR8(int family)
    {
        Identity();
        foreach (int samples in new[] { 1, 2, 4, 8, 16, 32 }.Where(stage.Device.Capabilities.SupportsMsaaSampleCount))
        {
            stage.Configure(false, samples);
            var image = stage.Capture(s => { s.Receiver(family); s.Caster(); });
            Assert.Equal(samples, stage.Scene.RenderTargetSampleCount);
            Nontrivial(image.Visibility);
            output.WriteLine($"R8 resolved at {samples} samples");
        }
    }

    [GpuTheory]
    [InlineData(false)] [InlineData(true)]
    public void TransparentAndDiscardedWritersPreserveOpaqueVisibility(bool motion)
    {
        Identity();
        int[] sampleCounts = motion ? [1] : new[] { 1, 2, 4, 8, 16, 32 }
            .Where(stage.Device.Capabilities.SupportsMsaaSampleCount).ToArray();
        foreach (int samples in sampleCounts)
        {
            stage.Configure(motion, samples);
            for (int writer = 0; writer < 11; writer++)
            {
                var bare = stage.Capture(s => { s.Receiver(0); s.Caster(); });
                Nontrivial(bare.Visibility);
                var jitter = stage.Scene.CurrentFrameView.JitterPixels;
                var over = stage.Capture(s => s.Preserver(writer), begin: false);
                Assert.Equal(jitter, stage.Scene.CurrentFrameView.JitterPixels);
                Assert.Equal(bare.Visibility, over.Visibility);
                if (writer < 6 || writer >= 9)
                    Assert.False(bare.Color.SequenceEqual(over.Color), $"writer {writer} must draw visible pixels");
                output.WriteLine($"writer {writer} preserved opaque R8 at {samples} samples");
            }
        }
    }

    [GpuFact]
    public void SharedSceneMatchesFreshSceneAfterLayoutAndSampleChanges()
    {
        stage.Configure(true);
        stage.Capture(s => { s.Receiver(1); s.Caster(); });
        stage.Configure(false);
        var aged = stage.Capture(s => { s.Receiver(0); s.Caster(); });
        using var fresh = new LitVisibilityGpuScene();
        fresh.Configure(false);
        var clean = fresh.Capture(s => { s.Receiver(0); s.Caster(); });
        Assert.Equal(clean.Visibility, aged.Visibility);
        Assert.Equal(clean.Color, aged.Color);
    }

    [GpuFact]
    public void FourAttachmentDeviceUsesApprovedVisibilityPolicy()
    {
        using var limited = new LitVisibilityGpuScene(4);
        using var reference = new LitVisibilityGpuScene();
        limited.Configure(true);
        reference.Configure(true);
        limited.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        reference.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        limited.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
        reference.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
        var degraded = limited.Capture(s => { s.Receiver(0); s.Caster(); });
        var filterOnly = reference.Capture(s => { s.Receiver(0); s.Caster(); });
        degraded = limited.Capture(s => { s.Receiver(0); s.Caster(); }, cameraCut: false);
        filterOnly = reference.Capture(s => { s.Receiver(0); s.Caster(); }, cameraCut: false);
        Assert.True(limited.Scene.TemporalHistory.IsValid);
        Assert.Null(limited.Scene.ResolvedVisibilityTarget);
        Assert.Empty(degraded.Visibility);
        Nontrivial(filterOnly.Visibility);
        Assert.Equal(filterOnly.Color, degraded.Color);
        output.WriteLine($"four-attachment policy on {limited.Device.Backend} {limited.Device.Capabilities.DeviceName}");
    }

    [GpuTheory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void DissolveAndItsComplementLeaveRevealedVisibilityUntouched(int family, bool motion)
    {
        stage.Configure(motion);
        var empty = stage.Capture(_ => { }).Visibility;
        var gone = stage.Capture(s => { s.Receiver(family, 1f); s.Caster(); }).Visibility;
        Assert.Equal(empty, gone);
        using var normalScene = new LitVisibilityGpuScene();
        using var inverseScene = new LitVisibilityGpuScene();
        normalScene.Configure(motion);
        inverseScene.Configure(motion);
        var ordinary = normalScene.Capture(s => { s.Receiver(family, .5f); s.Caster(); }).Visibility;
        var complement = inverseScene.Capture(s => { s.Receiver(family, .5f, 1f); s.Caster(); }).Visibility;
        Assert.Equal(normalScene.Scene.CurrentFrameView.JitterPixels, inverseScene.Scene.CurrentFrameView.JitterPixels);
        Assert.True(ordinary.Count(v => v < 100) > 10);
        Assert.True(complement.Count(v => v < 100) > 10);
        for (int p = 0; p < ordinary.Length; p++)
            Assert.True(ordinary[p] == 255 || complement[p] == 255, "complementary fragments must leave disjoint visibility");
    }

    [GpuFact]
    public void CascadesAndOutOfCoverageExportTheirFinalStrength()
    {
        stage.Configure(false);
        var camera = CascadeHandoffScene.Camera();
        stage.Scene.CameraOverride = camera;
        stage.Scene.Post.LightDirection = CascadeHandoffScene.Light;
        var shadows = stage.Scene.Post.Quality.Shadows;
        shadows.ShadowNearDistance = CascadeHandoffScene.NearDist;
        shadows.ShadowMaxDistance = CascadeHandoffScene.MaxDist;
        shadows.ShadowCascadeBlend = 0f;
        var hard = stage.Capture(s => s.Handoff()).Visibility;
        shadows.ShadowCascadeBlend = CascadeHandoffScene.DefaultBlend;
        var soft = stage.Capture(_ => { }, begin: false).Visibility;
        Nontrivial(soft);
        bool[] band = CascadeHandoffScene.BlendBandMask(camera, CascadeHandoffScene.DefaultBlend);
        int movedInBand = 0;
        for (int y = 0; y < LitVisibilityGpuScene.H; y++)
            for (int x = 0; x < LitVisibilityGpuScene.W; x++)
                if (band[y * CascadeHandoffScene.H / LitVisibilityGpuScene.H * CascadeHandoffScene.W
                    + x * CascadeHandoffScene.W / LitVisibilityGpuScene.W]
                    && Math.Abs(hard[y * LitVisibilityGpuScene.W + x] - soft[y * LitVisibilityGpuScene.W + x]) > 1)
                    movedInBand++;
        Assert.True(movedInBand > 0, "visibility must carry the actual cascade handoff blend");
        output.WriteLine($"{movedInBand} visibility pixels changed inside the cascade handoff band");
        stage.Configure(false);
        stage.Scene.Camera.Distance = 100f;
        Assert.All(stage.Capture(s => { s.Receiver(0); s.Caster(); }).Visibility, v => Assert.Equal(255, v));
    }

    [GpuTheory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    public void AlphaCutoutWritersLeaveTheRevealedOpaqueSignal(int family, bool motion)
    {
        stage.Configure(motion);
        var bare = stage.Capture(s => { s.Receiver(0); s.Caster(); });
        Nontrivial(bare.Visibility);
        var jitter = stage.Scene.CurrentFrameView.JitterPixels;
        var cut = stage.Capture(s => s.Receiver(family, cutout: true, height: .5f), begin: false);
        Assert.Equal(jitter, stage.Scene.CurrentFrameView.JitterPixels);
        Assert.Equal(bare.Visibility, cut.Visibility);
    }
}
