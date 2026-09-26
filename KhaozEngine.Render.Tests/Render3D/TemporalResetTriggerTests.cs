using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Every non-camera reset trigger (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2), each reported by
/// its root cause and each dropping history for exactly one frame, plus the rebuilds that must keep history.
/// </summary>
public sealed class TemporalResetTriggerTests
{
    static void AssertResetsForOneFrame(TemporalResetReason expected, Action<Scene3D> change,
        int width = HeadlessSceneRig.Width, int height = HeadlessSceneRig.Height)
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid, "the history must be valid before the change, or the reset proves nothing");

        change(scene);
        rig.Frame(width, height);
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Equal(expected, scene.TemporalHistory.LastReset);
        Assert.Null(scene.PreviousFrameView);

        rig.Frame(width, height);
        Assert.True(scene.TemporalHistory.IsValid, "a reset drops history for one frame, not for good");
        Assert.NotNull(scene.PreviousFrameView);
    }

    [Fact]
    public void TogglingTheHdrChainIsADeviceReset()
        => AssertResetsForOneFrame(TemporalResetReason.DeviceReset, s => s.Post.Hdr.Enabled = !s.Post.Hdr.Enabled);

    [Fact]
    public void ChangingTheAntiAliasingModeResets()
        => AssertResetsForOneFrame(TemporalResetReason.AntiAliasing, s => s.Post.Quality.AntiAliasing = AntiAliasing.Fxaa);

    [Fact]
    public void SupersamplingReportsTheAntiAliasingChangeRatherThanTheSizeItCauses()
        => AssertResetsForOneFrame(TemporalResetReason.AntiAliasing, s => s.Post.Quality.AntiAliasing = AntiAliasing.Ssaa(2f));

    [Fact]
    public void ChangingTheSupersampleFactorIsARenderScaleResetRatherThanAResize()
        => AssertResetsForOneFrame(TemporalResetReason.RenderScale, s => s.Post.Supersample = 2f);

    [Fact]
    public void ChangingTheRenderScaleModeResetsEvenAtTheSameSize()
        => AssertResetsForOneFrame(TemporalResetReason.RenderScale, s =>
        {
            s.Post.RenderScale = RenderScale.FixedInternal;
            s.Post.RenderWidth = HeadlessSceneRig.Width;
            s.Post.RenderHeight = HeadlessSceneRig.Height;
        });

    [Fact]
    public void AChangeOfInternalSizeIsAResize()
        => AssertResetsForOneFrame(TemporalResetReason.Resize, _ => { }, width: 80);

    [Fact]
    public void ABloomToggleOrADistortionFieldKeepsHistory()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();

        scene.Post.Bloom.Enabled = true;   // recreates the internal targets with the half-resolution chain
        rig.Frame();
        Assert.True(scene.BloomAllocated, "the bloom toggle did not rebuild the targets, so this proves nothing");
        Assert.True(scene.TemporalHistory.IsValid, "a bloom toggle reset temporal history");

        rig.Frame(s => s.DrawDistortion(new DistortionSprite { Position = new Vector3(0f, 1f, 0f), Size = 1f, Strength = 0.5f }));
        Assert.True(scene.TemporalHistory.IsValid, "the distortion field coming into being reset temporal history");
        Assert.NotNull(scene.PreviousFrameView);
    }

    [Fact]
    public void ASecondRenderAtAnotherSizeIsNotAResize()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        rig.Render(96, 48);   // an offscreen capture at another size inside the same frame
        Assert.True(scene.TemporalHistory.IsValid);
        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid, "an offscreen capture at another size reset the main view's history");
        Assert.Equal(HeadlessSceneRig.Width, TemporalAssert.Previous(scene).Width);
    }
}
