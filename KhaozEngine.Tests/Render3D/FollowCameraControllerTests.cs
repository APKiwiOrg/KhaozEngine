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
        static InputState Frame(
            Vector2 mouseDelta = default, float scroll = 0f,
            MouseButton? down = null)
        {
            var md = new HashSet<MouseButton>();
            if (down is MouseButton b) md.Add(b);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                md, new HashSet<MouseButton>(),
                mousePosition: Vector2.Zero, mouseDelta: mouseDelta, scrollDelta: scroll,
                width: 800, height: 600);
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
    }
}
