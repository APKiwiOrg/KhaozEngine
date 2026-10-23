using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalVisibilityCertificateGpuTests(LitVisibilityGpuScene stage, ITestOutputHelper output)
    : IClassFixture<LitVisibilityGpuScene>
{
    [GpuTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryOpaqueFamilyCertifiesOnlyItsActualFullyLitReceiver(int family)
    {
        ConfigureTemporal();
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }).Visibility, true, false);
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility, true, true);
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility, false, false);
        byte[] shadow = stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility;
        Check(shadow, false, false);
        if (family == 0) Assert.True(stage.Scene.ShadowPassSkippedLastFrame);
        Assert.Contains(shadow, b => b < 255);
        stage.Scene.Post.Quality.Shadows.ShadowStrength = 0f;
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility, true, false);
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility, true, true);
        stage.Scene.Post.Quality.Shadows.ShadowStrength = 1f;
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility, false, false);
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility, true, false);
        Check(stage.Capture(s => { s.Receiver(family); s.Caster(); }, cameraCut: false).Visibility, true, true);
    }

    [GpuTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void PreserversAndDiscardsKeepTheFullyLitCertificate(int writer)
    {
        ConfigureTemporal();
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        for (int frame = 0; frame < 2; frame++)
            Check(stage.Capture(s => { s.Receiver(0); s.Preserver(writer); }, cameraCut: false).Visibility, true, frame > 0);
    }

    [GpuFact]
    public void MsaaResolvesTheCertifiedReceiverToExactWhite()
    {
        foreach (int samples in new[] { 1, 2, 4, 8, 16, 32 }.Where(stage.Device.Capabilities.SupportsMsaaSampleCount))
        {
            stage.Configure(false, samples);
            stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            byte[] bytes = stage.Capture(s => { s.Receiver(0); s.Caster(); s.Preserver(0); }).Visibility;
            Assert.Equal(samples, stage.Scene.RenderTargetSampleCount);
            Assert.True(stage.Scene.ResolvedVisibilityFullyLit);
            Assert.All(bytes, b => Assert.Equal(255, b));
            output.WriteLine($"certified MSAA {samples}, {bytes.Length} exact white bytes");
        }
    }

    [GpuFact]
    public void RepeatedRenderAndEntryChangeKeepTheCertificateOfTheStoredReadIndex()
    {
        ConfigureTemporal();
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Check(stage.Capture(s => s.Receiver(0)).Visibility, true, false);
        Check(stage.Capture(s => s.Receiver(0), cameraCut: false).Visibility, true, true);
        var renderer = stage.Scene.TemporalResolveRendererForTests!;
        int write = stage.Scene.TemporalHistory.WriteIndex;
        byte[] stored = TemporalVisibilityHistoryGpuTests.Read(stage.Device, renderer.VisibilityHistory.Texture(write));
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        stage.Capture(s => s.Caster(), begin: false);
        Assert.False(stage.Scene.ResolvedLastRenderForTests);
        Assert.False(stage.Scene.ResolvedVisibilityFullyLit);
        Assert.Equal(stored, TemporalVisibilityHistoryGpuTests.Read(stage.Device, renderer.VisibilityHistory.Texture(write)));
        stage.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
        Check(stage.Capture(s => { s.Receiver(0); s.Caster(); }, cameraCut: false).Visibility, false, false);
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Check(stage.Capture(s => s.Receiver(0), cameraCut: false).Visibility, true, false);
        Check(stage.Capture(s => s.Receiver(0), cameraCut: false).Visibility, true, true);
        stage.Scene.CameraCut();
        Check(stage.Capture(s => s.Receiver(0), cameraCut: false).Visibility, true, false);
    }

    [GpuFact]
    public void SharedSceneCertificateMatchesAFreshSceneAfterLayoutChanges()
    {
        stage.Configure(false, 1);
        stage.Capture(s => { s.Receiver(5); s.Caster(); });
        stage.Configure(true);
        stage.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        var aged = stage.Capture(s => s.Receiver(0));
        using var fresh = new LitVisibilityGpuScene();
        fresh.Configure(true);
        fresh.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        var clean = fresh.Capture(s => s.Receiver(0));
        Assert.Equal(clean.Visibility, aged.Visibility);
        Assert.Equal(fresh.Scene.ResolvedVisibilityFullyLit, stage.Scene.ResolvedVisibilityFullyLit);
    }

    [GpuFact]
    public void ResetResizeOwnerAndDeviceChangesRequireAFreshCopiedCertificate()
    {
        using GpuDeviceContext other = GpuDeviceContext.CreateHeadless();
        var history = new TemporalHistory();
        var otherHistory = new TemporalHistory();
        var visibility = new TemporalVisibilityHistory();
        long frame = 1;
        void Seed(IGpuDevice device, TemporalHistory owner)
        {
            owner.BeginResolve(frame++);
            using IGpuCommandList commands = device.Factory.CreateCommandList();
            using (GpuRecording.Open(device, commands, nameof(TemporalVisibilityCertificateGpuTests)))
                visibility.Store(commands, visibility.FullyLit(), owner.WriteIndex, fullyLit: true);
            device.Submit(commands);
            device.WaitForIdle();
            owner.MarkValidAfterFrame();
            owner.BeginResolve(frame++);
            Assert.True(visibility.ReadFullyLit);
            Assert.All(TemporalVisibilityHistoryGpuTests.Read(device, visibility.Texture(owner.ReadIndex)),
                b => Assert.Equal(255, b));
        }
        try
        {
            history.EnsureTargets(stage.Device, 8, 8, 8, 8);
            visibility.Ensure(stage.Device, history, null);
            Assert.False(visibility.ReadFullyLit);
            Seed(stage.Device, history);
            history.Invalidate(TemporalResetReason.CameraCutRequested);
            Assert.False(visibility.ReadFullyLit);
            Seed(stage.Device, history);
            history.ReleaseTargets();
            history.EnsureTargets(stage.Device, 16, 16, 16, 16);
            visibility.Ensure(stage.Device, history, null);
            Assert.False(visibility.ReadFullyLit);
            Seed(stage.Device, history);
            otherHistory.EnsureTargets(stage.Device, 16, 16, 16, 16);
            visibility.Ensure(stage.Device, otherHistory, null);
            Assert.False(visibility.ReadFullyLit);
            Seed(stage.Device, otherHistory);
            otherHistory.EnsureTargets(other.GpuDevice, 16, 16, 16, 16);
            visibility.Ensure(other.GpuDevice, otherHistory, null);
            Assert.False(visibility.ReadFullyLit);
            Seed(other.GpuDevice, otherHistory);
        }
        finally
        {
            visibility.Release(null);
            history.ReleaseTargets();
            otherHistory.ReleaseTargets();
        }
    }

    void ConfigureTemporal()
    {
        stage.Configure(true);
        stage.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        stage.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
        output.WriteLine($"{stage.Device.Backend} {stage.Device.Capabilities.DeviceName}");
    }

    void Check(byte[] bytes, bool current, bool skip)
    {
        var renderer = stage.Scene.TemporalResolveRendererForTests!;
        Assert.Equal(current, stage.Scene.ResolvedVisibilityFullyLit);
        Assert.Equal(current, renderer.LastCurrentVisibilityFullyLit);
        Assert.Equal(skip, renderer.LastVisibilityDecisionSkipped);
        Assert.Equal(bytes, TemporalVisibilityHistoryGpuTests.Read(stage.Device,
            renderer.VisibilityHistory.Texture(stage.Scene.TemporalHistory.WriteIndex)));
        if (current) Assert.All(bytes, b => Assert.Equal(255, b));
    }
}
