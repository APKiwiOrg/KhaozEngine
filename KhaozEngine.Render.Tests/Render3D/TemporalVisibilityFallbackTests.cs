using System;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityFallbackTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheOutsideCostSeamKeepsTemporalRenderingWithoutVisibilityInfrastructure(int entry)
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.DisableTemporalVisibilityForTests = true;
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
        rig.Scene.TemporalResolveEntryForTests = (TemporalResolveEntry)entry;
        var renderer = new TemporalResolveRenderer(rig.Device, TemporalVisibilityCostPrograms.WithoutVisibility,
            disableVisibilityForTests: true);
        typeof(Scene3D).GetField("_temporalResolve", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(rig.Scene, renderer);
        using var commands = new RecordingGpuCommandList(rig.Device.Factory.CreateCommandList());
        rig.Frame(commands: commands);
        rig.Frame(commands: commands);
        Assert.True(rig.Scene.ResolvedLastRenderForTests);
        Assert.Equal((TemporalResolveEntry)entry, renderer.LastEntry);
        Assert.True(rig.Scene.TemporalHistory.TargetsAllocated);
        Assert.Null(rig.Scene.ResolvedVisibilityTarget);
        Assert.False(renderer.VisibilityHistory.Allocated);
        Assert.False(renderer.VisibilityHistory.StoredThisFrame);
        Assert.DoesNotContain(commands.TextureCopies, copy => copy.Source.Format == GpuPixelFormat.R8UNorm
            || copy.Destination.Format == GpuPixelFormat.R8UNorm);
        Assert.Equal(default, renderer.LastVisibilityUniforms);
        foreach (FakeResourceSet set in rig.Factory.ResourceSets)
            foreach (IGpuBindableResource resource in set.Resources)
                if (resource is IGpuTexture texture) Assert.NotEqual(GpuPixelFormat.R8UNorm, texture.Format);
        rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        rig.Frame();
        Assert.False(renderer.VisibilityHistory.Allocated);
    }
}
