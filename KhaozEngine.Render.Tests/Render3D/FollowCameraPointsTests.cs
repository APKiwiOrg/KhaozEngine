using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Windowing;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FollowCameraPointsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void Uncaptured_gesture_replay_and_subsequent_frames_turn_once_in_points(float x, float y)
    {
        var rig = new PointerPointsTestRig(new Vector2(x, y));
        var camera = new FollowCamera3D { Yaw = 0, Pitch = 0.5f };
        var controller = new FollowCameraController(camera)
        {
            LookGesture = new PointerGesture(MouseButton.Right),
            OrbitGesture = new PointerGesture(MouseButton.Left),
        };
        controller.Update(rig.Sample(true), 1f / 60);
        controller.Update(rig.Sample(true, new Vector2(3, 1)), 1f / 60);
        Assert.Equal(0, camera.Yaw);
        Assert.False(controller.WantsPointerCapture);
        controller.Update(rig.Sample(true, new Vector2(2, 1)), 1f / 60);
        Assert.Equal(-5 * controller.OrbitYawSpeed, camera.Yaw, 5);
        Assert.Equal(0.5f + 2 * controller.OrbitPitchSpeed, camera.Pitch, 5);

        rig.Accumulator.OnMouseDown(MouseButton.Left);
        controller.Update(rig.Sample(true, new Vector2(3, 0)), 1f / 60);
        controller.Update(rig.Sample(true, new Vector2(2, 0)), 1f / 60);
        Assert.Equal(-10 * controller.OrbitYawSpeed, camera.Yaw, 5);
        Assert.True(controller.TurnBodyActive);
        Assert.True(controller.WantsPointerCapture);
    }

    [Fact]
    public void No_gestures_keeps_legacy_framebuffer_motion()
    {
        var rig = new PointerPointsTestRig(new Vector2(2, 3));
        var camera = new FollowCamera3D { Yaw = 0, Pitch = 0.5f };
        var controller = new FollowCameraController(camera);
        controller.Update(rig.Sample(true, new Vector2(3, 2)), 1f / 60);
        Assert.Equal(-6 * controller.OrbitYawSpeed, camera.Yaw, 5);
        Assert.Equal(0.5f + 6 * controller.OrbitPitchSpeed, camera.Pitch, 5);
    }
}
