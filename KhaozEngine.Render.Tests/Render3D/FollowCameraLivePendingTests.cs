using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FollowCameraLivePendingTests
{
    const float Dt = 1f / 60;
    Vector2 _cursor = new(100, 50);

    InputState Frame(Vector2 delta = default, params MouseButton[] down)
    {
        _cursor += delta;
        var keys = new HashSet<Key>();
        return new InputState(delta, new(2, 3), keys, keys, keys,
            new HashSet<MouseButton>(down), new HashSet<MouseButton>(),
            _cursor, delta * new Vector2(2, 3), 0, 800, 600);
    }

    static PointerGesture Gesture(MouseButton button, bool live = true, float distance = 6) =>
        new(button, new PointerTapTolerance(distance, 0.2f, distance)) { LivePendingTravel = live };

    static FollowCameraController Controller() => new(new FollowCamera3D { Yaw = 0, Pitch = 0.5f })
    {
        LookGesture = Gesture(MouseButton.Right),
        OrbitGesture = Gesture(MouseButton.Left),
    };

    [Theory]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Left)]
    public void First_live_motion_turns_the_camera_without_capture_or_body_turn(MouseButton button)
    {
        var controller = Controller();
        controller.UpdateInput(Frame(new(20, 10), button), Dt);
        Assert.Equal(0, controller.Camera.Yaw);
        controller.UpdateInput(Frame(new(3, 2), button), Dt);
        Assert.Equal(-0.03f, controller.Camera.Yaw, 5);
        Assert.Equal(0.52f, controller.Camera.Pitch, 5);
        Assert.False(controller.WantsPointerCapture);
        Assert.False(controller.TurnBodyActive);
        controller.UpdateInput(Frame(new(4, 0), button), Dt);
        Assert.Equal(-0.07f, controller.Camera.Yaw, 5);
        Assert.True(controller.WantsPointerCapture);
        Assert.Equal(button == MouseButton.Right, controller.TurnBodyActive);
    }

    [Theory]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Left)]
    public void Own_live_wobble_still_taps_at_the_press_origin(MouseButton button)
    {
        var controller = Controller();
        controller.UpdateInput(Frame(default, button), Dt);
        controller.UpdateInput(Frame(new(3, 0), button), Dt);
        Assert.Equal(-0.03f, controller.Camera.Yaw, 5);
        controller.UpdateInput(Frame(new(-2, 0), button), Dt);
        controller.UpdateInput(Frame(new(30, 0)), Dt);
        Assert.Equal(button == MouseButton.Right, controller.LookTap);
        Assert.Equal(button == MouseButton.Left, controller.OrbitTap);
        Assert.Equal(new Vector2(100, 50), button == MouseButton.Right
            ? controller.LookGesture!.TapPosition : controller.OrbitGesture!.TapPosition);
        Assert.Equal(-0.01f, controller.Camera.Yaw, 5);
        controller.UpdateInput(Frame(), Dt);
        Assert.False(controller.LookTap);
        Assert.False(controller.OrbitTap);
    }

    [Theory]
    [InlineData(MouseButton.Right, false)]
    [InlineData(MouseButton.Left, false)]
    [InlineData(MouseButton.Right, true)]
    [InlineData(MouseButton.Left, true)]
    public void The_other_gestures_turn_voids_a_pending_tap(MouseButton tapping, bool otherLive)
    {
        var controller = Controller();
        MouseButton other = tapping == MouseButton.Right ? MouseButton.Left : MouseButton.Right;
        var turning = Gesture(other, otherLive, distance: 1);
        if (other == MouseButton.Right) controller.LookGesture = turning;
        else controller.OrbitGesture = turning;
        controller.UpdateInput(Frame(default, other), Dt);
        controller.UpdateInput(Frame(new(2, 0), other), Dt);
        controller.UpdateInput(Frame(default, other, tapping), Dt);
        controller.UpdateInput(Frame(new(1, 0), other, tapping), Dt);
        controller.UpdateInput(Frame(default, other), Dt);
        Assert.True((tapping == MouseButton.Right ? controller.LookGesture : controller.OrbitGesture)!.TapThisFrame);
        Assert.False(controller.LookTap);
        Assert.False(controller.OrbitTap);
        controller.UpdateInput(Frame(), Dt);
        controller.UpdateInput(Frame(default, tapping), Dt);
        controller.UpdateInput(Frame(), Dt);
        Assert.Equal(tapping == MouseButton.Right, controller.LookTap);
        Assert.Equal(tapping == MouseButton.Left, controller.OrbitTap);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    public void The_other_live_pending_gesture_voids_a_tap_on_its_release_frame(MouseButton tapping)
    {
        var controller = Controller();
        MouseButton other = tapping == MouseButton.Right ? MouseButton.Left : MouseButton.Right;
        controller.UpdateInput(Frame(default, tapping), Dt);
        controller.UpdateInput(Frame(default, tapping, other), Dt);
        controller.UpdateInput(Frame(new(2, 0), other), Dt);
        Assert.Equal(-0.02f, controller.Camera.Yaw, 5);
        Assert.False(controller.LookTap);
        Assert.False(controller.OrbitTap);
        Assert.Equal(PointerGesturePhase.Pending, (other == MouseButton.Right
            ? controller.LookGesture : controller.OrbitGesture)!.Phase);
    }

    [Fact]
    public void Both_live_buttons_turn_once_per_frame_and_neither_taps()
    {
        var controller = Controller();
        controller.UpdateInput(Frame(default, MouseButton.Left, MouseButton.Right), Dt);
        controller.UpdateInput(Frame(new(3, 2), MouseButton.Left, MouseButton.Right), Dt);
        Assert.Equal(-0.03f, controller.Camera.Yaw, 5);
        Assert.Equal(0.52f, controller.Camera.Pitch, 5);
        controller.UpdateInput(Frame(), Dt);
        Assert.True(controller.LookGesture!.TapThisFrame);
        Assert.True(controller.OrbitGesture!.TapThisFrame);
        Assert.False(controller.LookTap);
        Assert.False(controller.OrbitTap);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Stationary_two_button_release_suppresses_taps_when_either_gesture_is_live(
        bool orbitLive, bool lookLive, bool expectedTap)
    {
        var controller = Controller();
        controller.OrbitGesture = Gesture(MouseButton.Left, orbitLive);
        controller.LookGesture = Gesture(MouseButton.Right, lookLive);
        controller.UpdateInput(Frame(default, MouseButton.Left, MouseButton.Right), Dt);
        controller.UpdateInput(Frame(), Dt);
        Assert.True(controller.OrbitGesture.TapThisFrame);
        Assert.True(controller.LookGesture.TapThisFrame);
        Assert.Equal(expectedTap, controller.OrbitTap);
        Assert.Equal(expectedTap, controller.LookTap);
        Assert.Equal(0, controller.Camera.Yaw);
    }

    [Fact]
    public void Both_live_crossings_and_continuing_drags_turn_once_per_frame()
    {
        var controller = Controller();
        controller.UpdateInput(Frame(default, MouseButton.Left, MouseButton.Right), Dt);
        controller.UpdateInput(Frame(new(3, 0), MouseButton.Left, MouseButton.Right), Dt);
        controller.UpdateInput(Frame(new(4, 0), MouseButton.Left, MouseButton.Right), Dt);
        Assert.Equal(-0.07f, controller.Camera.Yaw, 5);
        Assert.True(controller.TurnBodyActive);
        controller.UpdateInput(Frame(new(2, 0), MouseButton.Left, MouseButton.Right), Dt);
        Assert.Equal(-0.09f, controller.Camera.Yaw, 5);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    public void A_joining_live_press_does_not_swallow_the_other_pending_motion(MouseButton first)
    {
        var controller = Controller();
        controller.UpdateInput(Frame(default, first), Dt);
        controller.UpdateInput(Frame(new(2, 0), MouseButton.Left, MouseButton.Right), Dt);
        Assert.Equal(-0.02f, controller.Camera.Yaw, 5);
        Assert.False(controller.WantsPointerCapture);
        controller.UpdateInput(Frame(), Dt);
        Assert.False(first == MouseButton.Left ? controller.LookTap : controller.OrbitTap);
    }

    [Fact]
    public void A_default_crossing_does_not_replay_motion_already_applied_live()
    {
        var controller = Controller();
        controller.OrbitGesture = Gesture(MouseButton.Left, live: false, distance: 4);
        controller.UpdateInput(Frame(default, MouseButton.Left, MouseButton.Right), Dt);
        controller.UpdateInput(Frame(new(3, 0), MouseButton.Left, MouseButton.Right), Dt);
        controller.UpdateInput(Frame(new(2, 0), MouseButton.Left, MouseButton.Right), Dt);
        Assert.Equal(-0.05f, controller.Camera.Yaw, 5);
        Assert.Equal(PointerGesturePhase.Dragging, controller.OrbitGesture.Phase);
        Assert.Equal(PointerGesturePhase.Pending, controller.LookGesture!.Phase);
        controller.UpdateInput(Frame(), Dt);
        Assert.False(controller.LookTap);
        Assert.False(controller.OrbitTap);
    }

    [Theory]
    [InlineData(MouseButton.Right, 0f)]
    [InlineData(MouseButton.Right, 100f)]
    [InlineData(MouseButton.Left, 0f)]
    [InlineData(MouseButton.Left, 100f)]
    public void Live_motion_takes_precedence_over_the_other_gestures_first_replay(MouseButton liveButton, float catchUpLimit)
    {
        var controller = Controller();
        MouseButton other = liveButton == MouseButton.Right ? MouseButton.Left : MouseButton.Right;
        var replaying = new PointerGesture(other,
            new PointerTapTolerance(6, 0.25f, 10) { CatchUpLimitPoints = catchUpLimit });
        if (other == MouseButton.Left) controller.OrbitGesture = replaying;
        else controller.LookGesture = replaying;
        controller.UpdateInput(Frame(default, other), 0);
        controller.UpdateInput(Frame(new(7, 0), other), 0.125f);
        controller.UpdateInput(Frame(default, MouseButton.Left, MouseButton.Right), 0);
        controller.UpdateInput(Frame(new(1, 0), MouseButton.Left, MouseButton.Right), 0.125f);
        Assert.Equal(PointerGesturePhase.Dragging, replaying.Phase);
        Assert.Equal(PointerGesturePhase.Pending, (liveButton == MouseButton.Right
            ? controller.LookGesture : controller.OrbitGesture)!.Phase);
        Assert.Equal(-0.01f, controller.Camera.Yaw, 5);
    }
}
