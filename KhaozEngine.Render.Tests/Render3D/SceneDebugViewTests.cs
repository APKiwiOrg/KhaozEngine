using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary><c>Scene3D.DebugView</c>: <see cref="SceneDebugView.None"/> by default, and any other value turns temporal
/// rendering on while it is set (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, sections 5 and 6). A frame fixes
/// its temporal state at its first render, so a requester changed after that render takes effect on the next frame.</summary>
public sealed class SceneDebugViewTests
{
    /// <summary>The two round 1 requesters of temporal rendering.</summary>
    public enum Requester
    {
        DebugView,
        TestSeam,
    }

    [Fact]
    public void TheDefaultIsNoneAndLeavesTemporalOff()
    {
        using var rig = new HeadlessSceneRig();
        Assert.Equal(SceneDebugView.None, rig.Scene.DebugView);
        Assert.False(rig.Scene.TemporalActive);
        rig.Frame();
        Assert.Equal(Vector2.Zero, rig.Scene.CurrentFrameView.JitterPixels);
    }

    [Theory]
    [InlineData(SceneDebugView.MotionVectors)]
    [InlineData(SceneDebugView.History)]
    [InlineData(SceneDebugView.Disocclusion)]
    [InlineData(SceneDebugView.Reactive)]
    public void AnyViewOtherThanNoneTurnsTemporalRenderingOnWhileItIsSet(SceneDebugView debugView)
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        rig.Frame();

        scene.DebugView = debugView;
        rig.Frame();
        Assert.True(scene.TemporalActive);
        Assert.NotEqual(Vector2.Zero, scene.CurrentFrameView.JitterPixels);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid);

        scene.DebugView = SceneDebugView.None;
        rig.Frame();
        Assert.False(scene.TemporalActive);
        Assert.Equal(Vector2.Zero, scene.CurrentFrameView.JitterPixels);
        Assert.False(scene.TemporalHistory.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void AnUndefinedViewIsRefusedAndTheSettingIsKept(int value)
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.DebugView = SceneDebugView.MotionVectors;
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Scene.DebugView = (SceneDebugView)value);
        Assert.Equal(SceneDebugView.MotionVectors, rig.Scene.DebugView);
    }

    [Theory]
    [InlineData(Requester.DebugView)]
    [InlineData(Requester.TestSeam)]
    public void ARequestBetweenBeginAndTheFirstRenderTakesEffectThatFrame(Requester requester)
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        rig.Frame();

        bool activeWhileDrawing = false;
        rig.Frame(s =>
        {
            Request(s, requester, true);
            activeWhileDrawing = s.TemporalActive;   // draw-time code before the render reads the live requesters
        });
        Assert.True(activeWhileDrawing);
        Assert.True(scene.TemporalActive);
        FrameView view = scene.CurrentFrameView;
        Assert.Equal(TemporalJitter.Offset(view.FrameIndex, TemporalJitter.NativePhaseCount), view.JitterPixels);
        Assert.NotEqual(Vector2.Zero, view.JitterPixels);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
    }

    [Theory]
    [InlineData(Requester.DebugView)]
    [InlineData(Requester.TestSeam)]
    public void TurningARequesterOnBetweenTwoRendersOfOneFrameWaitsForTheNextFrame(Requester requester)
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        rig.Frame();
        long index = scene.CurrentFrameView.FrameIndex;

        Request(scene, requester, true);
        Assert.False(scene.TemporalActive);   // the frame fixed its state at its first render
        rig.Render();   // a second render of the same frame
        FrameView second = scene.CurrentFrameView;
        Assert.False(scene.TemporalActive);
        Assert.Equal(index, second.FrameIndex);
        Assert.Equal(Vector2.Zero, second.JitterPixels);
        TemporalAssert.BitIdentical(second.ViewProjection, second.JitteredViewProjection, "the second render's raster matrix");
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Null(scene.PreviousFrameView);

        rig.Frame();
        FrameView next = scene.CurrentFrameView;
        Assert.True(scene.TemporalActive);
        Assert.Equal(TemporalJitter.Offset(next.FrameIndex, TemporalJitter.NativePhaseCount), next.JitterPixels);
        Assert.NotEqual(Vector2.Zero, next.JitterPixels);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
    }

    [Theory]
    [InlineData(Requester.DebugView)]
    [InlineData(Requester.TestSeam)]
    public void TurningARequesterOffBetweenTwoRendersOfOneFrameWaitsForTheNextFrame(Requester requester)
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        Request(scene, requester, true);
        rig.Frame();
        rig.Frame();
        FrameView first = scene.CurrentFrameView;
        long previousIndex = TemporalAssert.Previous(scene).FrameIndex;
        Assert.NotEqual(Vector2.Zero, first.JitterPixels);

        Request(scene, requester, false);
        Assert.True(scene.TemporalActive);   // the frame fixed its state at its first render
        rig.Render();   // a second render of the same frame
        FrameView second = scene.CurrentFrameView;
        Assert.True(scene.TemporalActive);
        Assert.Equal(first.FrameIndex, second.FrameIndex);
        Assert.Equal(first.JitterPixels, second.JitterPixels);
        Assert.True(scene.TemporalHistory.IsValid);
        Assert.Equal(previousIndex, TemporalAssert.Previous(scene).FrameIndex);

        rig.Frame();
        Assert.False(scene.TemporalActive);
        Assert.Equal(Vector2.Zero, scene.CurrentFrameView.JitterPixels);
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Null(scene.PreviousFrameView);
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
    }

    static void Request(Scene3D scene, Requester requester, bool on)
    {
        if (requester == Requester.DebugView) scene.DebugView = on ? SceneDebugView.MotionVectors : SceneDebugView.None;
        else scene.ForceTemporalForTests = on;
    }
}
