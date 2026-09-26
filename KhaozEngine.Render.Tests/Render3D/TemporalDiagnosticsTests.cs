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

    /// <summary>The first frame's index is 1, which every sequence of two or more phases reports as phase 1. Index 8 is
    /// the first where the native sequence of 8 wraps to phase 0 and a longer one reports 8, so the frames run past it.
    /// Each frame's jitter is checked against the phase it reports.</summary>
    [Fact]
    public void TheJitterPhaseWrapsWithTheNativeSequenceAndPicksTheFramesJitter()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;

        for (int i = 0; i < 2 * TemporalJitter.NativePhaseCount + 1; i++)
        {
            rig.Frame();
            TemporalDiagnostics d = scene.LastTemporalDiagnostics;
            Assert.Equal(scene.CurrentFrameView.FrameIndex, d.FrameIndex);
            Assert.Equal(TemporalJitter.Phase(d.FrameIndex, TemporalJitter.NativePhaseCount), d.JitterPhase);
            long haltonIndex = d.JitterPhase + 1L;
            float x = TemporalJitter.Halton(haltonIndex, 2) - 0.5f, y = TemporalJitter.Halton(haltonIndex, 3) - 0.5f;
            Assert.Equal(new Vector2(x, y), d.JitterPixels);
        }
        Assert.True(scene.LastTemporalDiagnostics.FrameIndex > TemporalJitter.NativePhaseCount,
            "the frames never passed the index where the native sequence wraps");
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
        Assert.Equal(rig.Scene.CurrentFrameView.FrameIndex, d.FrameIndex);
        Assert.Equal(TemporalJitter.Phase(d.FrameIndex, TemporalJitter.NativePhaseCount), d.JitterPhase);
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
        // Warm every path once, a reset included. The first trigger folded through TemporalResetPrecedence.Higher
        // initialises its static rank table once per process, so the cut before the third frame keeps that out of the
        // measurement. The cut also leaves the CameraCutRequested marker the final assertion checks. After that the
        // camera stays still and temporal rendering stays on, so every measured frame runs the valid-history path with
        // the full reset detection and the automatic cut checks, and fires nothing.
        for (int i = 0; i < 4; i++)
        {
            if (i == 2) scene.CameraCut();
            rig.Frame();
        }

        MeterLatchAndAdvance(scene, "latching the frame view and advancing temporal history");
        Assert.True(scene.LastTemporalDiagnostics.HistoryValid, "the measured loop never ran the valid-history path");
        Assert.Equal(TemporalResetReason.CameraCutRequested, scene.LastTemporalDiagnostics.LastReset);
    }

    /// <summary>The default path every game runs: nothing requests temporal rendering, so each frame drops the history
    /// without comparing it and still publishes the diagnostics.</summary>
    [Fact]
    public void LatchingAndAdvancingHistoryWithTemporalOffAllocatesNothing()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.DebugView = SceneDebugView.None;
        scene.ForceTemporalForTests = false;
        for (int i = 0; i < 4; i++) rig.Frame();   // warm every path once

        MeterLatchAndAdvance(scene, "latching the frame view and advancing temporal history with temporal off");
        Assert.False(scene.LastTemporalDiagnostics.HistoryValid, "the measured loop ran with temporal rendering on");
        Assert.Equal(TemporalResetReason.FirstFrame, scene.LastTemporalDiagnostics.LastReset);
    }

    static void MeterLatchAndAdvance(Scene3D scene, string description)
        => AllocAssert.NoPerCallAllocation(description, () =>
        {
            for (int i = 0; i < 64; i++)
            {
                scene.BeginFrameView();
                scene.LatchFrameView();
            }
        });
}
