using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class FollowCamera3DTests
    {
        [Fact]
        public void Eye_is_behind_target_along_yaw_pitch_distance()
        {
            // Yaw 0, Pitch 0, no height offset: eye sits +Z of the target by Distance, looking -Z.
            // MinPitch lowered so the pure pitch-0 geometry is exercised (the camera clamps pitch > 0 by default).
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 0, 10)) < 1e-4f, cam.Eye.ToString());
            Assert.True(Vector3.Distance(cam.Forward, new Vector3(0, 0, -1)) < 1e-4f, cam.Forward.ToString());
        }

        [Fact]
        public void Height_offset_raises_the_eye()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 2f, MinPitch = 0f };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 2, 10)) < 1e-4f, cam.Eye.ToString());
        }

        [Fact]
        public void Camera_always_looks_at_the_target()
        {
            foreach (var (yaw, pitch, dist) in new[] { (0f, 0.3f, 6f), (1.2f, 0.8f, 12f), (-2f, 0.1f, 4f) })
            {
                var cam = new FollowCamera3D { Target = new Vector3(3, 1, -2), Yaw = yaw, HeightOffset = 1f };
                cam.Pitch = pitch; cam.Distance = dist;
                Vector3 inView = Vector3.Transform(cam.Target, cam.View);   // target in view space
                Assert.True(MathF.Abs(inView.X) < 1e-3f && MathF.Abs(inView.Y) < 1e-3f, inView.ToString());
                Assert.True(inView.Z < 0f, $"target should be in front (-Z): {inView.Z}");
            }
        }

        [Fact]
        public void Pitch_clamps_to_its_range()
        {
            var cam = new FollowCamera3D();
            cam.Pitch = 100f;                       // absurdly high
            Assert.Equal(cam.MaxPitch, cam.Pitch, 5);
            cam.Pitch = -100f;                      // absurdly low
            Assert.Equal(cam.MinPitch, cam.Pitch, 5);
        }

        [Fact]
        public void Distance_clamps_to_min_max()
        {
            var cam = new FollowCamera3D();
            cam.Distance = 1e6f;
            Assert.Equal(cam.MaxDistance, cam.Distance, 5);
            cam.Distance = -50f;
            Assert.Equal(cam.MinDistance, cam.Distance, 5);
        }

        [Fact]
        public void Target_projects_to_screen_center()
        {
            var cam = new FollowCamera3D { Target = new Vector3(2, 0.5f, 1), AspectRatio = 1.6f };
            cam.Pitch = 0.4f; cam.Distance = 8f;
            Vector4 clip = Vector4.Transform(new Vector4(cam.Target, 1f), cam.ViewProjection);
            Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);
            Assert.True(MathF.Abs(ndc.X) < 1e-3f && MathF.Abs(ndc.Y) < 1e-3f, ndc.ToString());
        }

        [Fact]
        public void Eye_is_lifted_above_high_ground_at_its_xz()
        {
            // Ground higher than the geometric eye (a dip: terrain rises behind the character) lifts the eye
            // so it never sinks below the surface.
            var cam = new FollowCamera3D { Target = Vector3.Zero, GroundClearance = 0.5f };
            cam.Pitch = 0.3f; cam.Distance = 9f;
            float geomEyeY = cam.Eye.Y;       // before a ground delegate is attached
            cam.GroundHeight = (x, z) => 50f; // ground far above the geometric eye
            Assert.True(cam.Eye.Y >= 50f + 0.5f - 1e-4f, $"eye Y {cam.Eye.Y} not lifted above ground+clearance");
            Assert.True(cam.Eye.Y > geomEyeY, "eye should have been lifted");
        }

        [Fact]
        public void Eye_is_unchanged_when_ground_is_below()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero };
            cam.Pitch = 0.3f; cam.Distance = 9f;
            float geomEyeY = cam.Eye.Y;
            cam.GroundHeight = (x, z) => -1000f;   // ground far below the eye: no clamp
            Assert.Equal(geomEyeY, cam.Eye.Y, 4);
        }

        [Fact]
        public void Eye_is_geometric_when_no_ground_delegate()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, HeightOffset = 0f, MinPitch = 0f };
            cam.Pitch = 0f; cam.Distance = 10f;
            Assert.Null(cam.GroundHeight);
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 0, 10)) < 1e-4f, cam.Eye.ToString());
        }

        [Fact]
        public void Target_damping_is_off_by_default_so_the_effective_target_tracks_the_target()
        {
            var cam = new FollowCamera3D { Target = new Vector3(5, 1, -3) };
            Assert.False(cam.EnableTargetDamping);
            Assert.Equal(cam.Target, cam.EffectiveTarget);
            // Moving the target is reflected immediately, with no AdvanceTarget call - existing consumers unchanged.
            cam.Target = new Vector3(9, 2, 4);
            Assert.Equal(cam.Target, cam.EffectiveTarget);
            cam.AdvanceTarget(1f / 60f);              // a no-op while disabled
            Assert.Equal(cam.Target, cam.EffectiveTarget);
        }

        [Fact]
        public void Enabled_target_damping_eases_the_effective_target_toward_the_target_and_converges()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, EnableTargetDamping = true, TargetDampingRate = 10f };
            cam.AdvanceTarget(1f / 60f);             // first enabled step locks onto the current target (no lurch)
            Assert.Equal(Vector3.Zero, cam.EffectiveTarget);

            cam.Target = new Vector3(10, 0, 0);
            cam.AdvanceTarget(1f / 60f);             // one step: eased partway, not all the way
            float x1 = cam.EffectiveTarget.X;
            Assert.True(x1 > 0f && x1 < 10f, $"should ease partway, got {x1}");

            for (int i = 0; i < 600; i++) cam.AdvanceTarget(1f / 60f);
            Assert.True(Vector3.Distance(cam.EffectiveTarget, cam.Target) < 1e-3f, cam.EffectiveTarget.ToString());
        }

        [Fact]
        public void Target_damping_is_frame_rate_independent()
        {
            var coarse = new FollowCamera3D { Target = Vector3.Zero, EnableTargetDamping = true, TargetDampingRate = 8f };
            var fine = new FollowCamera3D { Target = Vector3.Zero, EnableTargetDamping = true, TargetDampingRate = 8f };
            coarse.AdvanceTarget(0f); fine.AdvanceTarget(0f);   // initialise both at the origin target
            coarse.Target = new Vector3(10, 0, 0);
            fine.Target = new Vector3(10, 0, 0);

            // 0.2s of damping as one coarse step vs twenty fine steps -> the same effective target (exp smoothing).
            coarse.AdvanceTarget(0.2f);
            for (int i = 0; i < 20; i++) fine.AdvanceTarget(0.01f);

            Assert.Equal(fine.EffectiveTarget.X, coarse.EffectiveTarget.X, 2);
        }

        [Fact]
        public void Enabled_damping_drives_the_eye_and_view_through_the_effective_target()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f,
                EnableTargetDamping = true, TargetDampingRate = 10f };
            cam.Pitch = 0f; cam.Distance = 10f;
            cam.AdvanceTarget(1f / 60f);             // lock onto origin
            cam.Target = new Vector3(20, 0, 0);      // jump the target far away
            // The eye has NOT teleported: it is still built around the (un-advanced) effective target near the origin.
            Assert.True(cam.Eye.X < 1f, $"eye should still be near the origin target, got {cam.Eye}");
            for (int i = 0; i < 600; i++) cam.AdvanceTarget(1f / 60f);
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(20, 0, 10)) < 1e-2f, cam.Eye.ToString());
        }

        [Fact]
        public void Warp_snaps_the_effective_target_onto_the_destination_with_no_trailing_under_damping()
        {
            // Damping on, eased only partway toward a far target (the "fly"), then Warp cuts to a new destination.
            var cam = new FollowCamera3D { Target = Vector3.Zero, EnableTargetDamping = true, TargetDampingRate = 10f };
            cam.AdvanceTarget(1f / 60f);             // init the damped target at origin
            cam.Target = new Vector3(100, 0, 0);
            cam.AdvanceTarget(1f / 60f);             // one step: still trailing far behind the target
            Assert.True(cam.EffectiveTarget.X < 50f, $"precondition: damping should still be trailing, got {cam.EffectiveTarget.X}");

            cam.Warp(new Vector3(-7, 3, 12));
            Assert.Equal(new Vector3(-7, 3, 12), cam.EffectiveTarget);   // cut this frame, zero trailing
            Assert.Equal(new Vector3(-7, 3, 12), cam.Target);           // Warp also moves the follow point
        }

        [Fact]
        public void Warp_leaves_no_ease_on_the_next_advance()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, EnableTargetDamping = true, TargetDampingRate = 10f };
            cam.AdvanceTarget(1f / 60f);
            cam.Warp(new Vector3(40, 0, 0));
            cam.AdvanceTarget(1f / 60f);             // damping resumes, but Target == damped target so it does not move
            Assert.Equal(new Vector3(40, 0, 0), cam.EffectiveTarget);
        }

        [Fact]
        public void Warp_forces_the_effective_target_even_before_the_first_advance()
        {
            // A brand-new camera with damping on that has never been advanced: Warp still cuts immediately.
            var cam = new FollowCamera3D { EnableTargetDamping = true, TargetDampingRate = 20f };
            cam.Warp(new Vector3(2, 0, -5));
            Assert.Equal(new Vector3(2, 0, -5), cam.EffectiveTarget);
        }

        [Fact]
        public void SnapToTarget_collapses_in_flight_damping_onto_the_current_target_without_moving_it()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, EnableTargetDamping = true, TargetDampingRate = 10f };
            cam.AdvanceTarget(1f / 60f);
            cam.Target = new Vector3(100, 0, 0);
            cam.AdvanceTarget(1f / 60f);             // trailing
            Assert.True(cam.EffectiveTarget.X < 50f);

            cam.SnapToTarget();
            Assert.Equal(cam.Target, cam.EffectiveTarget);      // collapsed onto the current Target
            Assert.Equal(new Vector3(100, 0, 0), cam.Target);   // Target itself unchanged
        }
    }
}
