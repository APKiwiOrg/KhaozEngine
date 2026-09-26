using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="Scene3D.CameraCut"/> and the automatic cut from <see cref="TemporalSettings"/>
/// (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2), and the proof that the frame after a cut is the
/// frame a scene with no history at all would render.
/// </summary>
public sealed class TemporalCameraCutTests
{
    static HeadlessSceneRig Warm()
    {
        var rig = new HeadlessSceneRig();
        rig.Scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        Assert.True(rig.Scene.TemporalHistory.IsValid);
        return rig;
    }

    /// <summary>A rig driven by a fly camera at an exact position, warmed with temporal rendering on, so a test can
    /// move or turn it by an exact amount.</summary>
    static HeadlessSceneRig WarmFly(out FlyCamera3D fly, float yaw = 0f, float pitch = 0f, bool pinOrigin = false)
    {
        var rig = new HeadlessSceneRig();
        fly = new FlyCamera3D
        {
            Position = new Vector3(0f, 2f, -10f),
            Yaw = yaw,
            Pitch = pitch,
            AspectRatio = (float)HeadlessSceneRig.Width / HeadlessSceneRig.Height,
        };
        rig.Scene.CameraOverride = fly;
        if (pinOrigin) rig.Scene.RenderOrigin = Vector3.Zero;   // a long move then never steps the origin
        rig.Scene.ForceTemporalForTests = true;
        rig.Frame();
        rig.Frame();
        Assert.True(rig.Scene.TemporalHistory.IsValid);
        return rig;
    }

    static void AssertCut(Scene3D scene, bool cut)
    {
        Assert.Equal(!cut, scene.TemporalHistory.IsValid);
        Assert.Equal(cut ? TemporalResetReason.CameraCutDetected : TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
    }

    [Fact]
    public void ACameraCutDropsHistoryForTheNextFrameOnly()
    {
        using HeadlessSceneRig rig = Warm();
        Scene3D scene = rig.Scene;
        scene.CameraCut();
        scene.CameraCut();   // twice before a frame is once
        rig.Frame();
        Assert.False(scene.TemporalHistory.IsValid);
        Assert.Equal(TemporalResetReason.CameraCutRequested, scene.TemporalHistory.LastReset);
        Assert.Null(scene.PreviousFrameView);
        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid);
    }

    [Fact]
    public void ACutRequestedBetweenBeginAndTheRenderLandsOnThatFrame()
    {
        using HeadlessSceneRig rig = Warm();
        rig.Frame(s => s.CameraCut());
        Assert.False(rig.Scene.TemporalHistory.IsValid);
        Assert.Equal(TemporalResetReason.CameraCutRequested, rig.Scene.TemporalHistory.LastReset);
    }

    [Fact]
    public void ACutRequestedAfterTheRenderWaitsThroughASecondRenderForTheNextFrame()
    {
        using HeadlessSceneRig rig = Warm();
        Scene3D scene = rig.Scene;
        scene.CameraCut();
        rig.Render();   // a second render inside the frame that already rendered
        Assert.True(scene.TemporalHistory.IsValid, "a second render consumed a cut meant for the next frame");
        rig.Frame();
        Assert.Equal(TemporalResetReason.CameraCutRequested, scene.TemporalHistory.LastReset);
    }

    [Theory]
    [InlineData(15.5f, false)]
    [InlineData(16.5f, true)]
    public void MovingFurtherThanTheCutDistanceInOneFrameIsADetectedCut(float metres, bool cut)
    {
        using HeadlessSceneRig rig = Warm();
        Scene3D scene = rig.Scene;
        scene.Camera.Target += new Vector3(metres, 0f, 0f);
        rig.Frame();
        AssertCut(scene, cut);
    }

    [Theory]
    [InlineData(59f, false)]
    [InlineData(61f, true)]
    public void TurningFurtherThanTheCutAngleInOneFrameIsADetectedCut(float degrees, bool cut)
    {
        using HeadlessSceneRig rig = WarmFly(out FlyCamera3D fly);
        fly.Yaw = degrees * MathF.PI / 180f;   // the eye stays put, only the view turns
        rig.Frame();
        AssertCut(rig.Scene, cut);
    }

    /// <summary>Both limits are exclusive: a move of exactly the limit continues the frame, and an infinite limit
    /// turns the distance check off however far the eye goes. The origin is pinned, so only the distance can cut.</summary>
    [Theory]
    [InlineData(16f, 16f, false)]
    [InlineData(16f, 10000f, true)]
    [InlineData(float.PositiveInfinity, 10000f, false)]
    public void ADistanceCutNeedsAMoveStrictlyPastTheLimit(float limit, float metres, bool cut)
    {
        using HeadlessSceneRig rig = WarmFly(out FlyCamera3D fly, pinOrigin: true);
        Scene3D scene = rig.Scene;
        scene.Post.Temporal.CutDistanceMetres = limit;
        Vector3 start = fly.Position;
        fly.Position += new Vector3(metres, 0f, 0f);
        Assert.Equal(metres, Vector3.Distance(start, fly.Position));   // the move is exact, so the limit row is too
        rig.Frame();
        Assert.Equal(Vector3.Zero, scene.CurrentFrameView.RenderOrigin);
        AssertCut(scene, cut);
    }

    /// <summary>A turn is at most 180 degrees, so a limit of 180 or more never cuts, even on an exact about-turn. A limit
    /// compared as a cosine would fold 200 onto 160 and cut here.</summary>
    [Theory]
    [InlineData(60f, true)]
    [InlineData(179f, true)]
    [InlineData(180f, false)]
    [InlineData(200f, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void AnAngleLimitOf180OrMoreNeverCutsEvenOnAnExactAboutTurn(float limit, bool cut)
    {
        using HeadlessSceneRig rig = WarmFly(out FlyCamera3D fly);
        Scene3D scene = rig.Scene;
        scene.Post.Temporal.CutAngleDegrees = limit;
        Vector3 before = fly.Forward;
        fly.Yaw = MathF.PI;
        Assert.Equal(-1f, Vector3.Dot(before, fly.Forward));   // exactly opposite, or the 180 row proves nothing
        rig.Frame();
        AssertCut(scene, cut);
    }

    [Fact]
    public void AnAboutTurnWhoseRoundedDotFallsBelowMinusOneStillCuts()
    {
        (float yaw, float pitch) = AboutTurnBelowMinusOne();
        using HeadlessSceneRig rig = WarmFly(out FlyCamera3D fly, yaw, pitch);
        fly.Yaw = yaw + MathF.PI;
        fly.Pitch = -pitch;
        rig.Frame();
        AssertCut(rig.Scene, cut: true);
    }

    /// <summary>A fly camera orientation whose about-turn rounds the dot of the two normalized forward vectors below
    /// -1, where an unclamped acos is NaN and compares false against any limit. Searched on the running platform, since
    /// which orientations round that way depends on its float arithmetic.</summary>
    static (float Yaw, float Pitch) AboutTurnBelowMinusOne()
    {
        for (int i = 1; i < 4096; i++)
        {
            float yaw = i * 0.0137f, pitch = ((i * 7) % 100 - 50) * 0.02f;
            var from = new FlyCamera3D { Yaw = yaw, Pitch = pitch };
            var to = new FlyCamera3D { Yaw = yaw + MathF.PI, Pitch = -pitch };
            if (Vector3.Dot(Vector3.Normalize(from.Forward), Vector3.Normalize(to.Forward)) < -1f) return (yaw, pitch);
        }
        throw new XunitException("no about-turn rounded its dot below -1, so this test would prove nothing");
    }

    [Fact]
    public void ARenderOriginStepIsNotACut()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.ForceTemporalForTests = true;
        // The iso eye sits 31.62 m along x from its target, so a target at x 31.875 puts the eye just short of the 64 m
        // line where the nearest 128 m frame changes, and one metre more crosses it.
        scene.Camera.Target = new Vector3(31.875f, 0f, 0f);
        rig.Frame();
        rig.Frame();
        Assert.Equal(Vector3.Zero, scene.CurrentFrameView.RenderOrigin);

        scene.Camera.Target += new Vector3(1f, 0f, 0f);
        rig.Frame();
        Assert.Equal(new Vector3(128f, 0f, 0f), scene.CurrentFrameView.RenderOrigin);
        Assert.True(scene.TemporalHistory.IsValid, "a one metre move that stepped the render origin was treated as a cut");
        Assert.Equal(new Vector3(128f, 0f, 0f), TemporalAssert.Previous(scene).RenderOrigin);
    }

    /// <summary>An explicit render origin may jump anywhere while the eye stays still. A step of one 128 m cell or less
    /// per axis on the grid is rebased as usual. A step off the grid, or of more than one cell, is a detected cut.</summary>
    [Theory]
    [InlineData(0.5f, 0f, true)]
    [InlineData(0f, 64f, true)]
    [InlineData(256f, 0f, true)]
    [InlineData(0f, -384f, true)]
    [InlineData(128f, 0f, false)]
    [InlineData(0f, -128f, false)]
    [InlineData(128f, -128f, false)]
    public void AnOverrideJumpOffTheGridOrPastOneCellIsADetectedCut(float x, float z, bool cut)
    {
        using HeadlessSceneRig rig = Warm();
        Scene3D scene = rig.Scene;
        Assert.Equal(Vector3.Zero, scene.CurrentFrameView.RenderOrigin);
        var origin = new Vector3(x, 0f, z);
        scene.RenderOrigin = origin;
        rig.Frame();
        Assert.Equal(origin, scene.CurrentFrameView.RenderOrigin);
        AssertCut(scene, cut);
        if (!cut) Assert.Equal(origin, TemporalAssert.Previous(scene).RenderOrigin);

        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid, "an origin that stays put after a jump still resets history");
    }

    [Fact]
    public void ARequestedCutOutranksADetectedOneAndASizeChangeOutranksARequestedCut()
    {
        using HeadlessSceneRig rig = Warm();
        Scene3D scene = rig.Scene;
        scene.CameraCut();
        scene.Camera.Target += new Vector3(40f, 0f, 0f);
        rig.Frame();
        Assert.Equal(TemporalResetReason.CameraCutRequested, scene.TemporalHistory.LastReset);

        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid);
        scene.CameraCut();
        rig.Frame(80, HeadlessSceneRig.Height);
        Assert.Equal(TemporalResetReason.Resize, scene.TemporalHistory.LastReset);
    }

    [Fact]
    public void ACutWhileTemporalIsOffDoesNotLeakIntoTheFirstTemporalFrame()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        rig.Frame();
        scene.CameraCut();
        rig.Frame();
        scene.ForceTemporalForTests = true;
        rig.Frame();
        Assert.Equal(TemporalResetReason.FirstFrame, scene.TemporalHistory.LastReset);
        rig.Frame();
        Assert.True(scene.TemporalHistory.IsValid);
    }

    [Fact]
    public void TheFrameAfterACutIsTheFrameAFreshSceneWouldRender()
    {
        using HeadlessSceneRig cut = Warm();
        using var fresh = new HeadlessSceneRig();
        fresh.Scene.ForceTemporalForTests = true;
        var destination = new Vector3(500.5f, 0f, -250.25f);

        cut.Scene.Camera.Target = destination;
        cut.Scene.CameraCut();
        cut.Frame();
        fresh.Scene.Camera.Target = destination;
        fresh.Frame();

        foreach (Scene3D scene in new[] { cut.Scene, fresh.Scene })
        {
            Assert.Null(scene.PreviousFrameView);
            Assert.False(scene.TemporalHistory.IsValid);
        }
        FrameView a = cut.Scene.CurrentFrameView, b = fresh.Scene.CurrentFrameView;
        Assert.Equal(b.RenderOrigin, a.RenderOrigin);

        // The next frame builds its history from the cut frame, never from what came before the cut.
        cut.Frame();
        fresh.Frame();
        TemporalAssert.BitIdentical(TemporalAssert.Previous(fresh.Scene).ViewProjection,
            TemporalAssert.Previous(cut.Scene).ViewProjection, "the previous view of the frame after the cut");
    }
}
