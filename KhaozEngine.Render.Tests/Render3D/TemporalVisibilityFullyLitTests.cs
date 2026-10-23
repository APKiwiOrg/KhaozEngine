using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityFullyLitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReceiverEvidenceRequiresBothCurrentAndReadIndexToBeFullyLit(int choice)
    {
        using var device = new FakeGpuDevice();
        using IGpuTexture image = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            160, 90, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer target = device.Factory.CreateFramebuffer(null, image);
        using var scene = new Scene3D(device, target.Outputs);
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
        scene.TemporalResolveEntryForTests = (TemporalResolveEntry)choice;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        (ShadowMode Mode, float Strength, bool Skip)[] frames =
        [
            (ShadowMode.Off, 1f, false), (ShadowMode.Off, 1f, true),
            (ShadowMode.ShadowMap, 1f, false), (ShadowMode.ShadowMap, 1f, false),
            (ShadowMode.Off, 1f, false), (ShadowMode.Off, 1f, true),
            (ShadowMode.ShadowMap, 0f, true), (ShadowMode.ShadowMap, 1f, false),
        ];
        foreach (var frame in frames)
        {
            scene.Post.Quality.Shadows.Mode = frame.Mode;
            scene.Post.Quality.Shadows.ShadowStrength = frame.Strength;
            scene.Begin();
            scene.Draw(box, Matrix4x4.Identity);
            scene.PrepareFrame();
            using (GpuRecording.Open(device, commands, nameof(TemporalVisibilityFullyLitTests)))
                scene.RenderInternal(commands, 160, 90, target);
            Assert.Equal(frame.Skip, DecisionIsAbsent(scene.TemporalResolveRendererForTests!));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticWhiteInputIsUnknownAndOnlyTheOwnedFallbackCertifies(bool fallback)
    {
        using var device = new FakeGpuDevice();
        using var renderer = new TemporalResolveRenderer(device);
        var history = new TemporalHistory();
        history.EnsureTargets(device, 8, 8, 8, 8);
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        using IGpuTexture signal = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            8, 8, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
        var inputs = new TemporalResolveInputs(history.Color(0), history.Color(0), history.PreviousDepth(0),
            history.Confidence(0), fallback ? null : signal);
        for (int frame = 0; frame < 3; frame++)
        {
            history.BeginResolve(frame);
            renderer.BindInputs(inputs, history);
            renderer.PrepareUniforms(commands, new TemporalResolveUniforms { Jitter = new Vector4(0, 0, 1, frame > 0 ? 1 : 0) }, default);
            renderer.Run(commands, history);
            Assert.Equal(fallback && frame > 0, renderer.LastVisibilityDecisionSkipped);
            Assert.Equal(fallback, renderer.LastCurrentVisibilityFullyLit);
            history.MarkValidAfterFrame();
        }
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 1);
        renderer.ReleaseSets(retired);
        history.ReleaseTargets();
    }

    [Fact]
    public void CertificatesPublishWithTheCopyAndCannotLeakAcrossFramesResetsOwnersOrDevices()
    {
        using var device = new FakeGpuDevice();
        using var otherDevice = new FakeGpuDevice();
        var history = new TemporalHistory();
        var otherHistory = new TemporalHistory();
        history.EnsureTargets(device, 8, 8, 8, 8);
        otherHistory.EnsureTargets(device, 8, 8, 8, 8);
        var visibility = new TemporalVisibilityHistory();
        visibility.Ensure(device, history, null);
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        history.BeginResolve(10);
        Assert.False(visibility.ReadFullyLit);
        visibility.Store(commands, visibility.FullyLit(), history.WriteIndex, fullyLit: true);
        visibility.Store(commands, visibility.FullyLit(), history.WriteIndex, fullyLit: false);
        history.MarkValidAfterFrame();
        history.BeginResolve(11);
        Assert.True(visibility.ReadFullyLit);
        visibility.Store(commands, visibility.FullyLit(), history.WriteIndex);
        history.BeginResolve(12);
        Assert.False(visibility.ReadFullyLit);
        visibility.Store(commands, visibility.FullyLit(), history.WriteIndex, fullyLit: true);
        history.BeginResolve(13);
        Assert.True(visibility.ReadFullyLit);
        history.Invalidate(TemporalResetReason.CameraCutRequested);
        Assert.False(visibility.ReadFullyLit);
        history.MarkValidAfterFrame();
        history.ReleaseTargets();
        history.EnsureTargets(device, 16, 16, 16, 16);
        visibility.Ensure(device, history, null);
        Assert.False(visibility.ReadFullyLit);
        visibility.Ensure(device, otherHistory, null);
        Assert.False(visibility.ReadFullyLit);
        otherHistory.EnsureTargets(otherDevice, 8, 8, 8, 8);
        visibility.Ensure(otherDevice, otherHistory, null);
        Assert.False(visibility.ReadFullyLit);
        visibility.Release(null);
        history.ReleaseTargets();
        otherHistory.ReleaseTargets();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ResolveDebugAndCountUseCachedProgramsForTheSameCertificate(int choice)
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
        rig.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        rig.Scene.DebugView = SceneDebugView.History;
        rig.Scene.TemporalResolveEntryForTests = (TemporalResolveEntry)choice;
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
        { CommandTrace = new List<RecordingGpuCommandList.Command>() };
        rig.Frame();
        rig.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        rig.Frame();
        int shaders = 0, sets = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            rig.Scene.Post.Quality.Shadows.Mode = frame == 1 ? ShadowMode.ShadowMap : ShadowMode.Off;
            rig.Scene.TemporalResolveRendererForTests!.ArmCounts();
            commands.Clear();
            commands.CommandTrace!.Clear();
            rig.Frame(commands: commands);
            bool skip = frame is 0 or 3;
            var renderer = rig.Scene.TemporalResolveRendererForTests!;
            Assert.Equal(skip, renderer.LastVisibilityDecisionSkipped);
            string[] actual = commands.CommandTrace!.Where(c => c.Kind == "draw")
                .Select(c => (c.Pipeline as FakePipeline)?.Request?.FragmentGlsl)
                .Where(s => s is not null && s.Contains("bool visibilityReleased = ", StringComparison.Ordinal))
                .Select(s => s!).ToArray();
            Assert.Equal(3, actual.Length); // Resolve, sampled count and debug re-evaluation.
            Assert.All(actual, source => Assert.Equal(skip,
                source.Contains("bool visibilityReleased = false;", StringComparison.Ordinal)));
            if (frame == 0) { shaders = rig.Factory.ShaderRequests.Count; sets = rig.Factory.ResourceSets.Count; }
            if (frame > 0)
            {
                Assert.Equal(shaders, rig.Factory.ShaderRequests.Count);
                Assert.Equal(sets, rig.Factory.ResourceSets.Count);
            }
        }
    }

    [Fact]
    public void AFailedVariantConstructionReleasesTheShadersItAlreadyCreated()
    {
        using var device = new FakeGpuDevice(maxColorAttachments: 4);
        var factory = (FakeGpuResourceFactory)device.Factory;
        Assert.Throws<InvalidOperationException>(() => new TemporalVisibilityPipelinePair(device,
            ShaderSources.TemporalResolveFrag, [], [], new GpuOutputDescription(null,
                GpuPixelFormat.R8UNorm, GpuPixelFormat.R8UNorm, GpuPixelFormat.R8UNorm,
                GpuPixelFormat.R8UNorm, GpuPixelFormat.R8UNorm)));
        Assert.Equal(2, factory.ShaderSets.Count);
        Assert.All(factory.ShaderSets, shader => Assert.True(shader.Disposed));
    }

    internal static bool DecisionIsAbsent(TemporalResolveRenderer renderer)
        => Array.Exists(renderer.LastEntryFragmentsForTests(), source =>
            source.Contains("bool visibilityReleased = false;", StringComparison.Ordinal));
}
