using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless proof of the temporal shadow cross-fade bookkeeping (issue #225): step detection on a quantized-direction
    /// change, the 0..1 weight ramp, retirement at the window end, and the mid-fade restart. No GPU. The GPU cross-fade
    /// (the two-atlas sample + lerp) is verified separately by the goldens; this pins the pure state machine.
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
            bool stepped = b.Advance(DirA, dt: 0.016f, blendSeconds: 0.25f);
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
            b.Advance(DirB, 0f, 1f);          // step: window 1s, weight 0
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
        public void BlendSecondsZero_steps_without_blending()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0.016f, 0f);
            bool stepped = b.Advance(DirB, 0.016f, 0f);   // direction changed, but blending disabled
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
        public void MidFade_duration_change_does_not_rescale_the_current_fade()
        {
            var b = new ShadowStepBlend();
            b.Advance(DirA, 0f, 1f);
            b.Advance(DirB, 0f, 1f);          // fade window captured at 1s
            b.Advance(DirB, 0.5f, 4f);        // caller now passes 4s, but the in-flight fade keeps its 1s window
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
    }
}
