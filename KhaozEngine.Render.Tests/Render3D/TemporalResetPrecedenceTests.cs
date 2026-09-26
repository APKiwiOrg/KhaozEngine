using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// One reset reason per frame, the highest ranked when several triggers fire together
/// (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2). The ranking is not the enum's declaration order:
/// <see cref="TemporalResetReason.FirstFrame"/> is the first reason declared after
/// <see cref="TemporalResetReason.None"/> but <see cref="TemporalResetReason.DeviceReset"/>, declared last, ranks
/// second, and <see cref="TemporalResetReason.Resize"/> ranks above both camera cuts.
/// </summary>
public sealed class TemporalResetPrecedenceTests
{
    static readonly TemporalResetReason[] Documented =
    [
        TemporalResetReason.FirstFrame, TemporalResetReason.DeviceReset, TemporalResetReason.AntiAliasing,
        TemporalResetReason.RenderScale, TemporalResetReason.Resize, TemporalResetReason.CameraCutRequested,
        TemporalResetReason.CameraCutDetected,
    ];

    [Fact]
    public void EveryPairReportsTheHigherRankedReasonWhicheverFiredFirst()
    {
        var wrong = new List<string>();
        for (int high = 0; high < Documented.Length; high++)
            for (int low = high + 1; low < Documented.Length; low++)
                foreach (var (a, b) in new[] { (Documented[high], Documented[low]), (Documented[low], Documented[high]) })
                {
                    TemporalResetReason reported = TemporalResetPrecedence.Higher(a, b);
                    if (reported != Documented[high]) wrong.Add($"{a} with {b} reported {reported}");
                }
        Assert.True(wrong.Count == 0, "the ranking is not the documented precedence:\n" + string.Join("\n", wrong));
    }

    /// <summary>A reason appended to the public enum and left out of the ranking fails here, not on the first frame
    /// two triggers collide.</summary>
    [Fact]
    public void EveryDefinedReasonIsRankedOnceAndOutranksNone()
    {
        TemporalResetReason[] defined = Enum.GetValues<TemporalResetReason>();
        Assert.Equal(defined.Length - 1, TemporalResetPrecedence.HighestFirst.Length);
        foreach (TemporalResetReason reason in defined)
        {
            Assert.Equal(reason, TemporalResetPrecedence.Higher(reason, TemporalResetReason.None));
            Assert.Equal(reason, TemporalResetPrecedence.Higher(TemporalResetReason.None, reason));
        }
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TemporalResetPrecedence.Higher((TemporalResetReason)99, TemporalResetReason.FirstFrame));
    }

    /// <summary>The triggers a scene can produce in one frame.</summary>
    [Flags]
    public enum Trigger
    {
        TemporalTurnedOn = 1,
        HdrToggle = 2,
        AntiAliasingMode = 4,
        SupersampleFactor = 8,
        ViewportSize = 16,
        ExplicitCut = 32,
        CameraJump = 64,
    }

    [Theory]
    // Each trigger alone, so every collision below is between triggers proven to fire.
    [InlineData(Trigger.TemporalTurnedOn, TemporalResetReason.FirstFrame)]
    [InlineData(Trigger.HdrToggle, TemporalResetReason.DeviceReset)]
    [InlineData(Trigger.AntiAliasingMode, TemporalResetReason.AntiAliasing)]
    [InlineData(Trigger.SupersampleFactor, TemporalResetReason.RenderScale)]
    [InlineData(Trigger.ViewportSize, TemporalResetReason.Resize)]
    [InlineData(Trigger.ExplicitCut, TemporalResetReason.CameraCutRequested)]
    [InlineData(Trigger.CameraJump, TemporalResetReason.CameraCutDetected)]
    // FirstFrame against every other reason.
    [InlineData(Trigger.TemporalTurnedOn | Trigger.HdrToggle, TemporalResetReason.FirstFrame)]
    [InlineData(Trigger.TemporalTurnedOn | Trigger.AntiAliasingMode, TemporalResetReason.FirstFrame)]
    [InlineData(Trigger.TemporalTurnedOn | Trigger.SupersampleFactor, TemporalResetReason.FirstFrame)]
    [InlineData(Trigger.TemporalTurnedOn | Trigger.ViewportSize, TemporalResetReason.FirstFrame)]
    [InlineData(Trigger.TemporalTurnedOn | Trigger.ExplicitCut, TemporalResetReason.FirstFrame)]
    [InlineData(Trigger.TemporalTurnedOn | Trigger.CameraJump, TemporalResetReason.FirstFrame)]
    // Each settings change over the ones below it and over the size change.
    [InlineData(Trigger.HdrToggle | Trigger.AntiAliasingMode, TemporalResetReason.DeviceReset)]
    [InlineData(Trigger.HdrToggle | Trigger.SupersampleFactor, TemporalResetReason.DeviceReset)]
    [InlineData(Trigger.HdrToggle | Trigger.ViewportSize, TemporalResetReason.DeviceReset)]
    [InlineData(Trigger.AntiAliasingMode | Trigger.SupersampleFactor, TemporalResetReason.AntiAliasing)]
    [InlineData(Trigger.AntiAliasingMode | Trigger.ViewportSize, TemporalResetReason.AntiAliasing)]
    [InlineData(Trigger.SupersampleFactor | Trigger.ViewportSize, TemporalResetReason.RenderScale)]
    // The size change over both cuts, and an explicit cut over a detected one.
    [InlineData(Trigger.ViewportSize | Trigger.ExplicitCut, TemporalResetReason.Resize)]
    [InlineData(Trigger.ViewportSize | Trigger.CameraJump, TemporalResetReason.Resize)]
    [InlineData(Trigger.ExplicitCut | Trigger.CameraJump, TemporalResetReason.CameraCutRequested)]
    // Everything at once.
    [InlineData(Trigger.HdrToggle | Trigger.AntiAliasingMode | Trigger.SupersampleFactor | Trigger.ViewportSize,
        TemporalResetReason.DeviceReset)]
    [InlineData(Trigger.TemporalTurnedOn | Trigger.HdrToggle | Trigger.AntiAliasingMode | Trigger.SupersampleFactor
        | Trigger.ViewportSize, TemporalResetReason.FirstFrame)]
    public void SeveralTriggersInOneFrameReportOnlyTheHighestRanked(Trigger triggers, TemporalResetReason expected)
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = !triggers.HasFlag(Trigger.TemporalTurnedOn);
        rig.Frame();
        rig.Frame();
        Assert.Equal(scene.ForceTemporalForTests, scene.TemporalHistory.IsValid);

        scene.ForceTemporalForTests = true;
        if (triggers.HasFlag(Trigger.HdrToggle)) scene.Post.Hdr.Enabled = !scene.Post.Hdr.Enabled;
        if (triggers.HasFlag(Trigger.AntiAliasingMode)) scene.Post.Quality.AntiAliasing = AntiAliasing.Fxaa;
        if (triggers.HasFlag(Trigger.SupersampleFactor)) scene.Post.Supersample = 2f;
        if (triggers.HasFlag(Trigger.ExplicitCut)) scene.CameraCut();
        if (triggers.HasFlag(Trigger.CameraJump)) scene.Camera.Target += new Vector3(40f, 0f, 0f);
        int width = triggers.HasFlag(Trigger.ViewportSize) ? 80 : HeadlessSceneRig.Width;
        rig.Frame(width, HeadlessSceneRig.Height);
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Equal(expected, scene.TemporalHistory.LastReset);
        Assert.Null(scene.PreviousFrameView);

        rig.Frame(width, HeadlessSceneRig.Height);
        Assert.True(scene.TemporalHistory.IsValid, "the collision dropped history for more than one frame");
    }
}
