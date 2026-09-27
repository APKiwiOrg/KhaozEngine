using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Eased boom recovery (<see cref="FollowCamera3D.BoomRecoveryRate"/>). A pull-in is always instant, and once
    /// the obstruction clears the boom eases back out as <see cref="FollowCamera3D.AdvanceBoom"/> decays the held
    /// shortfall. A teleport and a zoom in the open stay instant, and a zoom during recovery shifts the held
    /// shortfall so the eye never moves against the gesture.
    /// </summary>
    public class FollowCamera3DBoomRecoveryTests
    {
        /// <summary>The boom probe rows' framing: the boom runs straight down +Z from the origin for 10 m.</summary>
        static FollowCamera3D Camera(FixedReachProbe probe, float recoveryRate)
        {
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f, BoomProbe = probe,
                BoomRecoveryRate = recoveryRate,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            return cam;
        }

        static float Length(FollowCamera3D cam) => Vector3.Distance(cam.Eye, cam.Pivot);

        /// <summary>One rendered frame: advance the recovery, start the frame, read the boom.</summary>
        static float Frame(FollowCamera3D cam, float dt)
        {
            cam.AdvanceBoom(dt);
            cam.BeginFrame();
            return Length(cam);
        }

        [Fact]
        public void Rate_zero_follows_the_probe_both_ways_at_once()
        {
            var probe = new FixedReachProbe { ReachAt = 4f };
            FollowCamera3D cam = Camera(probe, 0f);

            Assert.Equal(3.95f, Length(cam), 4);

            probe.ReachAt = null;

            Assert.Equal(10f, Frame(cam, 1f / 60f), 4);
        }

        [Fact]
        public void A_pull_in_is_instant_with_recovery_on()
        {
            FollowCamera3D cam = Camera(new FixedReachProbe { ReachAt = 4f }, 4f);

            Assert.Equal(3.95f, Length(cam), 4);
        }

        [Fact]
        public void Recovery_eases_out_after_the_obstruction_clears()
        {
            var probe = new FixedReachProbe { ReachAt = 4f };
            FollowCamera3D cam = Camera(probe, 4f);
            Assert.Equal(3.95f, Length(cam), 4);

            probe.ReachAt = null;

            Assert.Equal(10f - 6.05f * MathF.Exp(-0.4f), Frame(cam, 0.1f), 4);
        }

        [Fact]
        public void Recovery_matches_across_frame_rates()
        {
            var probe60 = new FixedReachProbe { ReachAt = 4f };
            var probe30 = new FixedReachProbe { ReachAt = 4f };
            FollowCamera3D at60 = Camera(probe60, 4f);
            FollowCamera3D at30 = Camera(probe30, 4f);
            Assert.Equal(3.95f, Length(at60), 4);
            Assert.Equal(3.95f, Length(at30), 4);

            probe60.ReachAt = null;
            probe30.ReachAt = null;
            float a = 0f, b = 0f;
            for (int i = 0; i < 60; i++) a = Frame(at60, 1f / 60f);
            for (int i = 0; i < 30; i++) b = Frame(at30, 1f / 30f);

            Assert.True(MathF.Abs(a - b) < 1e-4f, $"60 Hz ended at {a}, 30 Hz at {b}");
            // Still easing after one second, so the two did not merely both reach an end stop.
            Assert.True(a > 3.95f && a < 10f, a.ToString());
        }

        [Fact]
        public void Zoom_stays_instant_while_unobstructed()
        {
            FollowCamera3D cam = Camera(new FixedReachProbe { ReachAt = null }, 4f);
            Assert.Equal(10f, Length(cam), 4);

            cam.Distance = 5f;
            Assert.Equal(5f, Length(cam), 4);

            cam.Distance = 15f;
            Assert.Equal(15f, Length(cam), 4);
        }

        /// <summary>Pulled in to 3.95 of 10 m, cleared, and eased out for 0.1 s. Returns the eased length.</summary>
        static float EasingAfterAPullIn(out FollowCamera3D cam)
        {
            var probe = new FixedReachProbe { ReachAt = 4f };
            cam = Camera(probe, 4f);
            Assert.Equal(3.95f, Length(cam), 4);

            probe.ReachAt = null;
            cam.AdvanceBoom(0.1f);
            float eased = Length(cam);
            Assert.True(eased > 3.95f && eased < 10f, eased.ToString());
            return eased;
        }

        [Fact]
        public void A_zoom_in_during_recovery_never_moves_the_eye_outward()
        {
            float eased = EasingAfterAPullIn(out FollowCamera3D cam);

            cam.Distance = 8f;

            Assert.Equal(MathF.Min(eased, 8f), Length(cam), 4);
        }

        [Fact]
        public void A_zoom_in_past_the_eased_length_lands_on_the_new_distance()
        {
            EasingAfterAPullIn(out FollowCamera3D cam);

            cam.Distance = 2f;

            Assert.Equal(2f, Length(cam), 4);
        }

        [Fact]
        public void A_deeper_obstruction_during_recovery_pulls_in_at_once()
        {
            var probe = new FixedReachProbe { ReachAt = 6f };
            FollowCamera3D cam = Camera(probe, 4f);
            Assert.Equal(5.95f, Length(cam), 4);

            probe.ReachAt = null;
            float eased = Frame(cam, 0.1f);
            Assert.True(eased > 5.95f && eased < 10f, eased.ToString());

            probe.ReachAt = 2f;
            cam.BeginFrame();

            Assert.Equal(1.95f, Length(cam), 4);
        }

        [Fact]
        public void Warp_clears_the_held_shortfall()
        {
            var probe = new FixedReachProbe { ReachAt = 4f };
            FollowCamera3D cam = Camera(probe, 4f);
            Assert.Equal(3.95f, Length(cam), 4);

            probe.ReachAt = null;
            cam.Warp(new Vector3(50f, 0f, 0f));

            Assert.Equal(10f, Length(cam), 4);
        }
    }
}
