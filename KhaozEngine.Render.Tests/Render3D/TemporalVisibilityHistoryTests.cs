using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityHistoryTests
{
    [Fact]
    public void ThePairFollowsOwnerGenerationAndDeviceAndRetiresOldTextures()
    {
        using var device = new FakeGpuDevice();
        using var otherDevice = new FakeGpuDevice();
        using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
        var owner = new TemporalHistory();
        var otherOwner = new TemporalHistory();
        var visibility = new TemporalVisibilityHistory();
        owner.EnsureTargets(device, 96, 54, 64, 36, retired);
        otherOwner.EnsureTargets(device, 96, 54, 64, 36, retired);
        Assert.True(visibility.Ensure(device, owner, retired));
        FakeTexture first = Assert.IsType<FakeTexture>(visibility.Texture(0));
        Assert.Equal((64u, 36u, GpuPixelFormat.R8UNorm), (first.Width, first.Height, first.Format));
        Assert.NotSame(first, visibility.Texture(1));
        Assert.False(visibility.Ensure(device, owner, retired));
        Assert.Same(first, visibility.Texture(0));

        owner.ReleaseTargets(retired);
        owner.EnsureTargets(device, 96, 54, 64, 36, retired);
        Assert.True(visibility.Ensure(device, owner, retired));
        Assert.False(first.Disposed);
        retired.BeginFrame();
        Assert.True(first.Disposed);
        Assert.False(visibility.Ready);

        FakeTexture second = Assert.IsType<FakeTexture>(visibility.Texture(0));
        Assert.True(visibility.Ensure(device, otherOwner, retired));
        Assert.False(second.Disposed);
        retired.BeginFrame();
        Assert.True(second.Disposed);
        FakeTexture third = Assert.IsType<FakeTexture>(visibility.Texture(0));
        otherOwner.EnsureTargets(otherDevice, 96, 54, 64, 36);
        int drains = device.WaitForIdleCalls;
        Assert.True(visibility.Ensure(otherDevice, otherOwner, null));
        Assert.True(third.Disposed);
        Assert.Equal(drains + 1, device.WaitForIdleCalls);
        visibility.Release(null);
        owner.ReleaseTargets(retired);
        otherOwner.ReleaseTargets();
    }

    [Fact]
    public void OnlyACompletedCopySeedsTheReadIndexAndARepeatedIndexNeverCopiesAgain()
    {
        using var device = new FakeGpuDevice();
        var owner = new TemporalHistory();
        owner.EnsureTargets(device, 8, 8, 8, 8);
        owner.MarkValidAfterFrame(); // valid colour must not admit a new visibility generation
        var visibility = new TemporalVisibilityHistory();
        visibility.Ensure(device, owner, null);
        using var commands = new RecordingGpuCommandList(device.Factory.CreateCommandList());
        using IGpuTexture signal = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(8, 8,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
        owner.BeginResolve(10);
        Assert.False(visibility.Ready);
        visibility.Store(commands, signal, owner.WriteIndex);
        RecordingGpuCommandList.TextureCopy copy = Assert.Single(commands.TextureCopies);
        Assert.Same(signal, copy.Source);
        Assert.Same(visibility.Texture(1), copy.Destination);
        Assert.True(copy.WholeResource);
        Assert.False(visibility.Ready);
        owner.BeginResolve(10);
        visibility.Store(commands, signal, owner.WriteIndex);
        Assert.Single(commands.TextureCopies);
        owner.BeginResolve(11);
        Assert.True(visibility.Ready);
        owner.Invalidate(TemporalResetReason.CameraCutRequested);
        Assert.False(visibility.Ready);
        visibility.Release(null);
        owner.ReleaseTargets();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ChangingOnlyVisibilityRebindsBothEntriesAndKeepsTheirReadHistory(int choice)
    {
        using var device = new FakeGpuDevice();
        using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
        var history = new TemporalHistory();
        history.EnsureTargets(device, 8, 8, 8, 8, retired);
        using var renderer = new TemporalResolveRenderer(device);
        var entry = (TemporalResolveEntry)choice;
        renderer.SelectEntry(entry, retired);
        using IGpuTexture signal = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(8, 8,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
        var inputs = new TemporalResolveInputs(history.Color(0), history.Color(0), history.PreviousDepth(0),
            history.Confidence(0), signal);
        history.BeginResolve(0);
        renderer.BindInputs(inputs, history, retired);
        using var commands = new RecordingGpuCommandList(device.Factory.CreateCommandList());
        renderer.Run(commands, history);
        FakeResourceSet before = Assert.IsType<FakeResourceSet>(renderer.CurrentSet);
        using IGpuTexture replacement = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(8, 8,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
        renderer.BindInputs(inputs with { KeyShadowVisibility = replacement }, history, retired);
        commands.Clear();
        renderer.Run(commands, history);
        FakeResourceSet after = Assert.IsType<FakeResourceSet>(renderer.CurrentSet);
        Assert.NotSame(before, after);
        Assert.Contains(replacement, after.Resources);
        Assert.Contains(renderer.VisibilityHistory.Texture(history.ReadIndex), after.Resources);
        FakeResourceSet[] signalSets = ((FakeGpuResourceFactory)device.Factory).ResourceSets
            .Where(s => s.Resources.Contains(replacement)).ToArray();
        Assert.Equal(entry == TemporalResolveEntry.Split ? 4 : 2, signalSets.Length);
        Assert.All(signalSets, s => Assert.Same(after.Resources[^1], s.Resources[^1]));
        Assert.Equal(16u, Assert.IsAssignableFrom<IGpuBuffer>(after.Resources[^1]).SizeInBytes);
        Assert.All(signalSets, s => Assert.Contains(s.Resources,
            r => ReferenceEquals(r, renderer.VisibilityHistory.Texture(0))
                || ReferenceEquals(r, renderer.VisibilityHistory.Texture(1))));
        Assert.False(before.Disposed);
        retired.BeginFrame();
        Assert.True(before.Disposed);
        renderer.ReleaseSets(retired);
        history.ReleaseTargets(retired);
    }
}
