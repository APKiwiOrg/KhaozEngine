using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Eased boom recovery (<see cref="FollowCamera3D.BoomRecoveryRate"/>). A pull-in is always instant, and once
    /// the obstruction clears the boom eases back out as <see cref="FollowCamera3D.AdvanceBoom"/> decays the held
    /// shortfall. A zoom and a teleport stay instant.
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

        [Fact]
        public void A_zoom_drops_the_held_shortfall()
        {
            // Cleared: a zoom in to 2 m while 8.05 m is held would otherwise floor the boom at the minimum.
            var cleared = new FixedReachProbe { ReachAt = 2f };
            FollowCamera3D cam = Camera(cleared, 4f);
            Assert.Equal(1.95f, Length(cam), 4);

            cleared.ReachAt = null;
            cam.Distance = 2f;

            Assert.Equal(2f, Length(cam), 4);

            // Still obstructed: the real obstruction re-imposes itself on the same read.
            var blocked = new FixedReachProbe { ReachAt = 1f };
            FollowCamera3D held = Camera(blocked, 4f);
            Assert.Equal(0.95f, Length(held), 4);

            held.Distance = 2f;

            Assert.Equal(0.95f, Length(held), 4);
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
