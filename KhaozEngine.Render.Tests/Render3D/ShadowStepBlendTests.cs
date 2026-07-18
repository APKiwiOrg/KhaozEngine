using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless proof of the temporal shadow cross-fade bookkeeping: step detection on a quantized-direction change, the
    /// 0..1 weight ramp, retirement at the window end, and the mid-fade restart (issue #225), plus the adaptive fade
    /// duration (min(observed inter-step interval, clamp)), the first-step clamp fallback, and the hysteretic per-frame
    /// bypass (issue #227). No GPU. The GPU cross-fade (the two-atlas sample + lerp) is verified separately by
    /// <c>ShadowStepBlendGpuTests</c>; this pins the pure state machine.
    /// </summary>
    public class ShadowStepBlendTests
    {
        static readonly Vector3 DirA = new(0f, -1f, 0f);
        static readonly Vector3 DirB = new(0.3f, -0.95f, 0f);
        static readonly Vector3 DirC = new(-0.3f, -0.95f, 0f);

        [Fact]
        public void FirstAdvance_seeds_without_stepping()
        {
            var b = new ShadowStepBlend();
            bool stepped = b.Advance(DirA, dt: 0.016f, clampSeconds: 0.25f);
            Assert.False(stepped);
            Assert.False(b.Blending);
            Assert.Equal(1f, b.Weight);   // no cross-fade in flight => fully live
        }

        [Fact]
        public void SameDirection_never_steps()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0.016f, 0.25f);
            for (int i = 0; i < 10; i++)
                Assert.False(b.Advance(DirA, 0.016f, 0.25f));
            Assert.False(b.Blending);
            Assert.Equal(1f, b.Weight);
        }

        [Fact]
        public void DirectionChange_starts_a_blend_at_weight_zero()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0.016f, 0.5f);
            bool stepped = b.Advance(DirB, 0.016f, 0.5f);
            Assert.True(stepped);
            Assert.True(b.Blending);
            Assert.Equal(0f, b.Weight);   // the step frame shows fully the OUTGOING set (no jump)
        }

        [Fact]
        public void Weight_ramps_linearly_then_retires()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0f, 1f);
            b.Advance(DirB, 0f, 1f);          // step: first step => clamp window 1s, weight 0
            Assert.Equal(0f, b.Weight);

            b.Advance(DirB, 0.25f, 1f);       // +0.25s
            Assert.Equal(0.25f, b.Weight, 3);
            Assert.True(b.Blending);

            b.Advance(DirB, 0.5f, 1f);        // +0.75s total
            Assert.Equal(0.75f, b.Weight, 3);

            b.Advance(DirB, 0.25f, 1f);       // reaches the window end
            Assert.Equal(1f, b.Weight);
            Assert.False(b.Blending);         // retired

            // After retirement the same direction stays live at weight 1.
            Assert.False(b.Advance(DirB, 0.1f, 1f));
            Assert.Equal(1f, b.Weight);
        }

        [Fact]
        public void ClampZero_steps_without_blending()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0.016f, 0f);
            bool stepped = b.Advance(DirB, 0.016f, 0f);   // direction changed, but the clamp is 0 (blending disabled)
            Assert.False(stepped);                        // no freeze requested
            Assert.False(b.Blending);
            Assert.Equal(1f, b.Weight);
        }

        [Fact]
        public void MidFade_step_restarts_the_blend()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0f, 1f);
            b.Advance(DirB, 0f, 1f);          // step 1
            b.Advance(DirB, 0.6f, 1f);        // partway through (weight 0.6)
            Assert.Equal(0.6f, b.Weight, 3);

            bool stepped = b.Advance(DirC, 0.1f, 1f);   // a new step arrives mid-fade
            Assert.True(stepped);                       // caller must re-freeze (current live becomes the new frozen set)
            Assert.True(b.Blending);
            Assert.Equal(0f, b.Weight);                 // restarted from the outgoing end
        }

        [Fact]
        public void NegativeDt_does_not_run_the_fade_backwards()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0f, 1f);
            b.Advance(DirB, 0f, 1f);
            b.Advance(DirB, 0.5f, 1f);
            Assert.Equal(0.5f, b.Weight, 3);
            b.Advance(DirB, -1f, 1f);         // a bogus negative dt is clamped to 0, weight holds
            Assert.Equal(0.5f, b.Weight, 3);
        }

        [Fact]
        public void MidFade_clamp_change_does_not_rescale_the_current_fade()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0f, 1f);
            b.Advance(DirB, 0f, 1f);          // fade window captured at 1s (first step => clamp)
            b.Advance(DirB, 0.5f, 4f);        // caller now passes clamp 4s, but the in-flight fade keeps its 1s window
            Assert.Equal(0.5f, b.Weight, 3);  // 0.5 / 1.0, not 0.5 / 4.0
        }

        [Fact]
        public void Reset_cancels_the_fade_and_reseeds()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0f, 1f);
            b.Advance(DirB, 0f, 1f);
            Assert.True(b.Blending);
            b.Reset();
            Assert.False(b.Blending);
            Assert.Equal(1f, b.Weight);
            // Re-seeds without a step: the first advance after a reset commits the direction silently.
            Assert.False(b.Advance(DirC, 0.016f, 1f));
        }

        // ---- issue #227: adaptive fade duration -------------------------------------------------------------------

        [Fact]
        public void FirstStep_uses_the_clamp_even_when_the_commit_gap_is_short()
        {
            var b = new ShadowStepBlend();
            const float clamp = 0.5f;
            b.Advance(DirA, 0.01f, clamp);           // seed (commit)
            b.Advance(DirB, 0.01f, clamp);           // first step, ~0.01 after the commit: no interval observed yet
            Assert.True(b.Blending);
            Assert.Equal(0f, b.Weight);
            // The window is the CLAMP (0.5), not the tiny commit->step gap: halfway through 0.5s reads weight 0.5.
            b.Advance(DirB, 0.25f, clamp);
            Assert.True(b.Blending);
            Assert.Equal(0.5f, b.Weight, 3);         // 0.25 / 0.5; if it used the 0.01 gap the fade would have retired
        }

        [Fact]
        public void Window_adapts_to_the_observed_interval_when_below_the_clamp()
        {
            var b = new ShadowStepBlend();
            const float clamp = 1f;
            b.Advance(DirA, 0.05f, clamp);           // seed
            b.Advance(DirB, 0.05f, clamp);           // step 1 (first => clamp window)
            b.Advance(DirB, 0.05f, clamp);           // 3 held frames + the next step => a 0.20s inter-step interval
            b.Advance(DirB, 0.05f, clamp);
            b.Advance(DirB, 0.05f, clamp);
            bool stepped = b.Advance(DirC, 0.05f, clamp);   // step 2: interval 0.20 (4 frames), clamp 1 => window 0.20
            Assert.True(stepped);
            Assert.Equal(0f, b.Weight);
            b.Advance(DirC, 0.10f, clamp);           // half of the 0.20 window
            Assert.Equal(0.5f, b.Weight, 3);         // proves the window followed the 0.20 interval, not the 1.0 clamp
            b.Advance(DirC, 0.10f, clamp);           // completes the 0.20 window
            Assert.Equal(1f, b.Weight);
            Assert.False(b.Blending);
        }

        [Fact]
        public void Window_clamps_when_the_interval_exceeds_the_clamp()
        {
            var b = new ShadowStepBlend();
            const float clamp = 0.2f;
            b.Advance(DirA, 0.1f, clamp);            // seed
            b.Advance(DirB, 0.1f, clamp);            // step 1 (first => clamp window 0.2)
            for (int i = 0; i < 4; i++)
                b.Advance(DirB, 0.1f, clamp);        // 4 held frames + the next step => a 0.5s interval (> clamp)
            bool stepped = b.Advance(DirC, 0.1f, clamp);   // step 2: interval 0.5, clamp 0.2 => window MIN = 0.2
            Assert.True(stepped);
            b.Advance(DirC, 0.1f, clamp);            // half of the 0.2 window
            Assert.Equal(0.5f, b.Weight, 3);         // 0.1 / 0.2; if the window were the 0.5 interval this would be 0.2
            b.Advance(DirC, 0.1f, clamp);            // completes the 0.2 window (would still be blending if it were 0.5)
            Assert.Equal(1f, b.Weight);
            Assert.False(b.Blending);
        }

        [Fact]
        public void MidFade_step_chains_with_the_new_observed_interval()
        {
            var b = new ShadowStepBlend();
            const float clamp = 1f;
            b.Advance(DirA, 0.1f, clamp);            // seed
            b.Advance(DirB, 0.1f, clamp);            // step 1 (first => clamp window)
            b.Advance(DirB, 0.1f, clamp);
            b.Advance(DirB, 0.1f, clamp);
            b.Advance(DirC, 0.1f, clamp);            // step 2: interval 0.3 => window 0.3
            b.Advance(DirC, 0.15f, clamp);           // mid-fade of the 0.3 window (weight 0.5)
            Assert.Equal(0.5f, b.Weight, 3);

            // A new step arrives mid-fade, 0.15s after step 2. The in-flight fade lands instantly and the new fade
            // takes the fresh 0.15 interval as its window (min with the 1.0 clamp).
            bool stepped = b.Advance(DirA, 0f, clamp);
            Assert.True(stepped);
            Assert.Equal(0f, b.Weight);
            b.Advance(DirA, 0.075f, clamp);          // half of the new 0.15 window
            Assert.Equal(0.5f, b.Weight, 3);         // proves the chained fade uses 0.15, not the prior 0.3
        }

        // ---- issue #227: hysteretic per-frame bypass --------------------------------------------------------------

        [Fact]
        public void Bypass_engages_below_the_frame_floor_and_releases_above_with_hysteresis()
        {
            var b = new ShadowStepBlend();
            const float clamp = 1f, dt = 0.1f;
            b.Advance(DirA, dt, clamp);              // seed
            Assert.False(b.BypassQuantization);
            b.Advance(DirB, dt, clamp);              // first step: no interval yet, never bypasses
            Assert.False(b.BypassQuantization);

            // A step every single frame => interval == dt => 1 frame/step, below the ~2-frame engage floor.
            b.Advance(DirA, dt, clamp);              // step 2: engage bypass
            Assert.True(b.BypassQuantization);
            Assert.False(b.Blending);                // bypass suppresses the cross-fade (and drops any in-flight one)

            // A 3-frame/step cadence is inside the sticky band [2, 4]: it must NOT release.
            b.Advance(DirA, dt, clamp);
            b.Advance(DirA, dt, clamp);
            b.Advance(DirB, dt, clamp);              // step 3: interval 0.3 => 3 frames/step, still bypassed
            Assert.True(b.BypassQuantization);

            // Slow to 5 frames/step (above the 4-frame release threshold): release.
            for (int i = 0; i < 4; i++)
                b.Advance(DirB, dt, clamp);
            b.Advance(DirA, dt, clamp);              // step 4: interval 0.5 => 5 frames/step, release bypass
            Assert.False(b.BypassQuantization);
        }

        [Fact]
        public void Bypass_releases_when_the_sun_slows_to_a_stop_mid_bypass()
        {
            var b = new ShadowStepBlend();
            const float clamp = 1f, dt = 0.1f;
            b.Advance(DirA, dt, clamp);              // seed
            b.Advance(DirB, dt, clamp);              // first step
            b.Advance(DirA, dt, clamp);              // step 2: 1 frame/step => bypass on
            Assert.True(b.BypassQuantization);

            // The sun stops: no further steps. The bypass releases once the gap exceeds the 4-frame threshold, without
            // waiting for a step that never comes.
            for (int i = 0; i < 4; i++)
                b.Advance(DirA, dt, clamp);          // held at 4 frames: still bypassed (threshold is strict)
            Assert.True(b.BypassQuantization);
            b.Advance(DirA, dt, clamp);              // 5th held frame crosses the threshold
            Assert.False(b.BypassQuantization);
        }
    }
}
