using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary><c>Scene3D.LastTemporalDiagnostics</c>: one value per frame, written on the frame's first render, and a
/// latch plus history advance that allocates nothing (acceptance 4).</summary>
[Collection("AllocSensitive")]
public sealed class TemporalDiagnosticsTests
{
    [Fact]
    public void BeforeTheFirstRenderTheDiagnosticsAreDefault()
    {
        using var rig = new HeadlessSceneRig();
        Assert.Equal(default(TemporalDiagnostics), rig.Scene.LastTemporalDiagnostics);
    }

    [Fact]
    public void TheDiagnosticsDescribeEachFramesIndexJitterAndHistory()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;

        rig.Frame();
        TemporalDiagnostics first = scene.LastTemporalDiagnostics;
        Assert.Equal(scene.CurrentFrameView.FrameIndex, first.FrameIndex);
        Assert.Equal(TemporalJitter.Phase(first.FrameIndex, TemporalJitter.NativePhaseCount), first.JitterPhase);
        Assert.Equal(scene.CurrentFrameView.JitterPixels, first.JitterPixels);
        Assert.NotEqual(Vector2.Zero, first.JitterPixels);
        Assert.Equal((0, 0, 0), (first.KeyedRigid, first.KeyedSkinned, first.KeyCollisions));
        Assert.False(first.HistoryValid);
        Assert.Equal(TemporalResetReason.FirstFrame, first.LastReset);

        rig.Frame();
        TemporalDiagnostics second = scene.LastTemporalDiagnostics;
        Assert.Equal(first.FrameIndex + 1, second.FrameIndex);
        Assert.True(second.HistoryValid);
        Assert.Equal(TemporalResetReason.FirstFrame, second.LastReset);

        scene.CameraCut();
        rig.Frame();
        Assert.False(scene.LastTemporalDiagnostics.HistoryValid);
        Assert.Equal(TemporalResetReason.CameraCutRequested, scene.LastTemporalDiagnostics.LastReset);
    }

    [Fact]
    public void ASecondRenderInTheSameFrameLeavesTheDiagnosticsAlone()
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        TemporalDiagnostics frame = rig.Scene.LastTemporalDiagnostics;
        rig.Render(96, 48);
        Assert.Equal(frame, rig.Scene.LastTemporalDiagnostics);
    }

    [Fact]
    public void WithTemporalOffTheJitterIsZeroAndTheHistoryInvalid()
    {
        using var rig = new HeadlessSceneRig();
        rig.Frame();
        rig.Frame();
        TemporalDiagnostics d = rig.Scene.LastTemporalDiagnostics;
        Assert.Equal(Vector2.Zero, d.JitterPixels);
        Assert.False(d.HistoryValid);
        Assert.Equal(TemporalResetReason.FirstFrame, d.LastReset);
    }

    [Fact]
    public void LatchingAndAdvancingHistoryAllocatesNothing()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.DebugView = SceneDebugView.MotionVectors;
        // Warm every path once, a reset included. The first reset in a process initialises the reset precedence table,
        // a one-time allocation rather than a per-frame one, so the cut before the third frame keeps it out of the
        // measurement. After that the camera stays still and temporal rendering stays on, so every measured frame
        // runs the valid-history path with the full reset detection and the automatic cut checks, and fires nothing.
        for (int i = 0; i < 4; i++)
        {
            if (i == 2) scene.CameraCut();
            rig.Frame();
        }

        AllocAssert.NoPerCallAllocation("latching the frame view and advancing temporal history", () =>
        {
            for (int i = 0; i < 64; i++)
            {
                scene.BeginFrameView();
                scene.LatchFrameView();
            }
        });
        Assert.True(scene.LastTemporalDiagnostics.HistoryValid, "the measured loop never ran the valid-history path");
        Assert.Equal(TemporalResetReason.CameraCutRequested, scene.LastTemporalDiagnostics.LastReset);
    }
}
