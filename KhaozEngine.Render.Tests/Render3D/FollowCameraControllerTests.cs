using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class FollowCameraControllerTests
    {
        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(
            Vector2 mouseDelta = default, float scroll = 0f,
            MouseButton? down = null)
        {
            var md = new HashSet<MouseButton>();
            if (down is MouseButton b) md.Add(b);
            var (edgePressed, edgeReleased) = _mouse.Advance(md);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                md, edgePressed,
                mousePosition: Vector2.Zero, mouseDelta: mouseDelta, scrollDelta: scroll,
                width: 800, height: 600, mouseReleased: edgeReleased);
        }

        [Fact]
        public void OrbitButton_defaults_to_right_mouse()
        {
            var cam = new FollowCamera3D();
            Assert.Equal(MouseButton.Right, new FollowCameraController(cam).OrbitButton);
        }

        [Fact]
        public void Drag_with_button_held_changes_yaw_and_pitch()
        {
            // Default mapping: drag right turns the view left (yaw -=), drag down looks up (pitch +=).
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(mouseDelta: new Vector2(10, 4), down: ctl.OrbitButton), 1f / 60f);
            Assert.Equal(-10f * ctl.OrbitYawSpeed, cam.Yaw, 5);
            Assert.Equal(0.5f + 4f * ctl.OrbitPitchSpeed, cam.Pitch, 5);
        }

        [Fact]
        public void Invert_flags_flip_each_axis()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam) { InvertX = true, InvertY = true };
            ctl.Update(Frame(mouseDelta: new Vector2(10, 4), down: ctl.OrbitButton), 1f / 60f);
            Assert.Equal(10f * ctl.OrbitYawSpeed, cam.Yaw, 5);
            Assert.Equal(0.5f - 4f * ctl.OrbitPitchSpeed, cam.Pitch, 5);
        }

        [Fact]
        public void Drag_without_button_does_nothing()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(mouseDelta: new Vector2(10, 4)), 1f / 60f);   // no button
            Assert.Equal(0f, cam.Yaw, 5);
            Assert.Equal(0.5f, cam.Pitch, 5);
        }

        [Fact]
        public void Scroll_up_zooms_in_scroll_down_zooms_out()
        {
            var cam = new FollowCamera3D();
            cam.Distance = 10f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(scroll: 1f), 1f / 60f);
            Assert.True(cam.Distance < 10f, $"scroll up should reduce distance: {cam.Distance}");
            float after = cam.Distance;
            ctl.Update(Frame(scroll: -1f), 1f / 60f);
            Assert.True(cam.Distance > after, $"scroll down should increase distance: {cam.Distance}");
        }

        [Fact]
        public void Pitch_and_distance_stay_clamped()
        {
            var cam = new FollowCamera3D();
            var ctl = new FollowCameraController(cam);
            // Drag far past the pitch limit.
            ctl.Update(Frame(mouseDelta: new Vector2(0, -100000), down: ctl.OrbitButton), 1f / 60f);
            Assert.True(cam.Pitch <= cam.MaxPitch + 1e-4f && cam.Pitch >= cam.MinPitch - 1e-4f);
            // Scroll in hard.
            for (int i = 0; i < 200; i++) ctl.Update(Frame(scroll: 1f), 1f / 60f);
            Assert.Equal(cam.MinDistance, cam.Distance, 4);
        }

        [Fact]
        public void No_input_leaves_camera_unchanged()
        {
            var cam = new FollowCamera3D { Yaw = 1.2f };
            cam.Pitch = 0.4f; cam.Distance = 7f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(), 1f / 60f);
            Assert.Equal(1.2f, cam.Yaw, 5);
            Assert.Equal(0.4f, cam.Pitch, 5);
            Assert.Equal(7f, cam.Distance, 5);
        }

        [Fact]
        public void Update_advances_target_damping_when_enabled()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, EnableTargetDamping = true, TargetDampingRate = 10f };
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(), 1f / 60f);                 // initialise at the origin target
            cam.Target = new Vector3(10, 0, 0);
            for (int i = 0; i < 5; i++) ctl.Update(Frame(), 1f / 60f);
            float x = cam.EffectiveTarget.X;
            Assert.True(x > 0f && x < 10f, $"controller should be easing the target, got {x}");
        }

        [Fact]
        public void Update_does_not_damp_when_disabled()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero };   // damping off (default)
            var ctl = new FollowCameraController(cam);
            cam.Target = new Vector3(10, 0, 0);
            ctl.Update(Frame(), 1f / 60f);
            Assert.Equal(cam.Target, cam.EffectiveTarget);           // immediate, unchanged behaviour
        }

        [Fact]
        public void Update_advances_boom_recovery()
        {
            var probe = new FixedReachProbe { ReachAt = 4f };
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, BoomProbe = probe,
                BoomRecoveryRate = 4f,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            var ctl = new FollowCameraController(cam);
            Assert.Equal(3.95f, Vector3.Distance(cam.Eye, cam.Pivot), 4);   // pulled in

            probe.ReachAt = null;
            ctl.Update(Frame(), 0.1f);
            float length = Vector3.Distance(cam.Eye, cam.Pivot);

            Assert.True(length > 3.95f && length < 10f, $"controller should be easing the boom out, got {length}");
        }

        [Fact]
        public void UpdateInputLeavesTheCameraClocksAlone()
        {
            var probe = new FixedReachProbe { ReachAt = 4f };
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, BoomProbe = probe,
                BoomRecoveryRate = 4f, EnableTargetDamping = true, TargetDampingRate = 10f,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            var ctl = new FollowCameraController(cam);
            ctl.Update(Frame(), 1f / 60f);                 // initialise the damping at the origin target
            Assert.Equal(3.95f, Vector3.Distance(cam.Eye, cam.Pivot), 4);   // pulled in

            cam.Target = new Vector3(10, 0, 0);
            probe.ReachAt = null;
            ctl.UpdateInput(Frame(), 0.5f);
            Assert.Equal(Vector3.Zero, cam.EffectiveTarget);
            Assert.Equal(3.95f, Vector3.Distance(cam.Eye, cam.Pivot), 4);

            ctl.Update(Frame(), 0.5f);
            Assert.True(cam.EffectiveTarget.X > 0f, $"Update should ease the target, got {cam.EffectiveTarget.X}");
            Assert.True(Vector3.Distance(cam.Eye, cam.Pivot) > 3.95f, "Update should ease the boom out");
        }

        // Frames of 1/32 s are exact in binary, so the 0.25 s grace ends on frame 8 (the press frame is frame 0).
        const float ToleranceDt = 1f / 32f;

        static PointerGesture ToleranceGesture() => new(MouseButton.Left, new PointerTapTolerance(4f, 0.25f, 8f));

        [Fact]
        public void Tolerance_gesture_short_move_released_inside_the_grace_is_a_tap()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam) { OrbitGesture = ToleranceGesture() };
            for (int i = 0; i < 3; i++)
                ctl.Update(Frame(mouseDelta: new Vector2(2f, 0f), down: MouseButton.Left), ToleranceDt);
            ctl.Update(Frame(), ToleranceDt);
            Assert.True(ctl.OrbitTap);
            Assert.Equal(0f, cam.Yaw);
            Assert.Equal(0.5f, cam.Pitch);
        }

        [Fact]
        public void Tolerance_gesture_slow_drag_orbits_from_the_frame_the_grace_ends()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam) { OrbitGesture = ToleranceGesture() };
            for (int frame = 0; frame < 12; frame++)
            {
                ctl.Update(Frame(mouseDelta: new Vector2(0.875f, 0f), down: MouseButton.Left), ToleranceDt);
                if (frame < 8)
                    Assert.Equal(0f, cam.Yaw);
                else    // the crossing replays every step after the press frame, then each frame adds its own
                    Assert.Equal(-0.875f * frame * ctl.OrbitYawSpeed, cam.Yaw, 5);
            }
            Assert.Equal(0.5f, cam.Pitch);
        }

        [Fact]
        public void UpdateInputTimesATapTolerance()
        {
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam) { OrbitGesture = ToleranceGesture() };
            for (int frame = 0; frame < 12; frame++)
            {
                ctl.UpdateInput(Frame(mouseDelta: new Vector2(0.875f, 0f), down: MouseButton.Left), ToleranceDt);
                if (frame < 8)
                    Assert.Equal(0f, cam.Yaw);
                else
                    Assert.Equal(-0.875f * frame * ctl.OrbitYawSpeed, cam.Yaw, 5);
            }
            ctl.UpdateInput(Frame(), ToleranceDt);
            Assert.False(ctl.OrbitTap);

            // A zero dt never ends the grace, so the same drag holds until it passes the 8 point grace limit.
            var frozen = new FollowCamera3D { Yaw = 0f };
            frozen.Pitch = 0.5f;
            var frozenCtl = new FollowCameraController(frozen) { OrbitGesture = ToleranceGesture() };
            for (int frame = 0; frame < 10; frame++)
            {
                frozenCtl.UpdateInput(Frame(mouseDelta: new Vector2(0.875f, 0f), down: MouseButton.Left), 0f);
                Assert.Equal(0f, frozen.Yaw);
            }
            frozenCtl.UpdateInput(Frame(mouseDelta: new Vector2(0.875f, 0f), down: MouseButton.Left), 0f);
            Assert.Equal(-0.875f * 10 * frozenCtl.OrbitYawSpeed, frozen.Yaw, 5);
        }

        [Fact]
        public void Tolerance_gesture_press_frame_motion_is_still_a_tap()
        {
            // The playtester's click: the hand is still settling on the press frame, then holds still past the grace.
            var cam = new FollowCamera3D { Yaw = 0f };
            cam.Pitch = 0.5f;
            var ctl = new FollowCameraController(cam) { OrbitGesture = ToleranceGesture() };
            ctl.Update(Frame(mouseDelta: new Vector2(5f, 0f), down: MouseButton.Left), ToleranceDt);
            Assert.Equal(0f, cam.Yaw);
            Assert.Equal(0.5f, cam.Pitch);
            for (int frame = 1; frame <= 12; frame++)
            {
                ctl.Update(Frame(down: MouseButton.Left), ToleranceDt);
                Assert.Equal(0f, cam.Yaw);
                Assert.Equal(0.5f, cam.Pitch);
            }
            ctl.Update(Frame(), ToleranceDt);
            Assert.True(ctl.OrbitTap);
            Assert.Equal(0f, cam.Yaw);
            Assert.Equal(0.5f, cam.Pitch);
        }
    }
}
