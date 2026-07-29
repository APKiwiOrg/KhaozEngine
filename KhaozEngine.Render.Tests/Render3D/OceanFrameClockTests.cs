using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The FFT ocean's one-frame time compensation (#398), checked headless.
    /// <para>
    /// This is the fidelity claim of the cross-frame ping-pong, and it is a claim about a number rather than about
    /// a picture: the row pass dispatched during frame N must be handed the time frame N+1 will render at, because
    /// frame N+1's column pass is what consumes it. If that holds, the same math sees the same time input it always
    /// did and the surface is unmoved. So it is tested here, on the clock alone, rather than inferred from a golden
    /// that could only ever say the phase looks about right.
    /// </para>
    /// </summary>
    public sealed class OceanFrameClockTests
    {
        // The row time is a sum of two floats and the frame's own time is a product, so the compensation lands
        // within a few ULPs of the next frame's time rather than exactly on it (and a ULP of a wave clock GROWS
        // with the clock: at 16 s it is already 1e-6). The bound is therefore a THOUSANDTH OF A FRAME, which is
        // four orders under anything a half-float surface map can show and still catches a compensation that is
        // off by a whole frame or by a factor.
        const float PhaseToleranceFrames = 1e-3f;

        /// <summary>The whole compensation, at a steady frame rate: what frame N dispatched is what frame N+1
        /// renders. Run at three frame rates, because the row time is derived from the PREVIOUS delta and a
        /// compensation that only worked at 60 fps would pass a single-rate test.</summary>
        [Theory]
        [InlineData(1f / 60f)]
        [InlineData(1f / 30f)]
        [InlineData(1f / 144f)]
        public void TheRowTimeDispatchedThisFrameIsTheTimeTheNextFrameRenders(float frameSeconds)
        {
            var clock = new OceanFrameClock();
            float pending = 0f;

            for (int frame = 0; frame < 600; frame++)
            {
                float now = frame * frameSeconds;
                OceanFrameTick tick = clock.Advance(now);

                if (frame == 0)
                {
                    Assert.True(tick.Prime, "the first ocean frame has no pending rows and must prime");
                }
                else
                {
                    Assert.False(tick.Prime, $"frame {frame} re-primed under a steady {1f / frameSeconds:0} fps clock");
                    // Frame 1 is the ONE documented exception, and it costs one frame of a held surface at startup
                    // rather than a drain: frame 0 had no previous frame to measure, so it could not extrapolate
                    // and handed its own time forward. Everything from frame 2 is the steady state.
                    float expected = frame == 1 ? 0f : now;
                    Assert.True(MathF.Abs(pending - expected) <= frameSeconds * PhaseToleranceFrames,
                        $"frame {frame} consumes rows evolved to {pending} but renders at {now} " +
                        $"(expected {expected}), so the surface phase moved");
                }

                pending = tick.RowTime;
            }
        }

        /// <summary>The first frame has no previous frame to measure, so it carries no delta - the foam
        /// accumulator must start empty rather than take an injection sized by an invented step.</summary>
        [Fact]
        public void TheFirstFrameHasNoDeltaAndPrimes()
        {
            OceanFrameTick tick = new OceanFrameClock().Advance(4.25f);
            Assert.True(tick.Prime);
            Assert.Equal(0f, tick.Delta);
            // No delta means no extrapolation either: the rows this frame hands forward are for this frame's own
            // time, so the frame after a cold start repeats the phase once rather than jumping an invented step.
            Assert.Equal(4.25f, tick.RowTime);
        }

        /// <summary>A paused wave clock holds the phase and costs nothing: the extrapolation is zero, the pending
        /// rows keep describing the frame, and no frame drains the device.</summary>
        [Fact]
        public void APausedClockHoldsThePhaseAndPrimesOnlyOnce()
        {
            var clock = new OceanFrameClock();
            for (int frame = 0; frame < 10; frame++)
            {
                OceanFrameTick tick = clock.Advance(7f);
                Assert.Equal(frame == 0, tick.Prime);
                Assert.Equal(0f, tick.Delta);
                Assert.Equal(7f, tick.RowTime);
            }
        }

        /// <summary>A hitch is not a discontinuity. The frame after a delta change renders the change in the delta
        /// early (16 ms of frame becoming 50 ms leaves the surface 34 ms ahead of nothing anyone can see), and the
        /// clock must absorb that rather than pay a drain for it.</summary>
        [Fact]
        public void AFrameRateHitchDoesNotRePrime()
        {
            var clock = new OceanFrameClock();
            clock.Advance(0f);
            clock.Advance(1f / 60f);
            OceanFrameTick hitched = clock.Advance(1f / 60f + 0.05f);
            Assert.False(hitched.Prime);
            Assert.Equal(0.05f, hitched.Delta, 5);
        }

        /// <summary>The error does NOT accumulate: every frame re-derives its prediction from the wave clock it was
        /// handed, not from the last prediction, so a run of irregular frames stays within one delta change of the
        /// truth instead of drifting away from it.</summary>
        [Fact]
        public void AnIrregularClockStaysWithinOneFrameAndNeverDriftsAway()
        {
            var clock = new OceanFrameClock();
            var deltas = new[] { 0.016f, 0.021f, 0.013f, 0.038f, 0.016f, 0.009f, 0.028f, 0.016f };
            float now = 0f, pending = 0f;
            bool havePending = false;
            float worst = 0f;

            for (int frame = 0; frame < 400; frame++)
            {
                OceanFrameTick tick = clock.Advance(now);
                if (havePending)
                {
                    Assert.False(tick.Prime, $"frame {frame} re-primed on a merely irregular clock");
                    worst = MathF.Max(worst, MathF.Abs(pending - now));
                }
                pending = tick.RowTime;
                havePending = true;
                now += deltas[frame % deltas.Length];
            }

            // The bound is the largest step CHANGE in the sequence, and it is a bound on the error at any frame
            // rather than a total: a drifting compensation would blow past it inside a few dozen frames.
            Assert.True(worst <= 0.025f + 1e-5f, $"worst phase error {worst}s exceeded the largest delta change");
        }

        /// <summary>A gap wider than the delta clamp is not a frame. It means the ocean was not drawn for a while
        /// or the clock was scrubbed, and the pending rows are for a time nobody is rendering, so the frame primes
        /// rather than showing a stale sea for one frame.</summary>
        [Theory]
        [InlineData(0.5f)]
        [InlineData(5f)]
        [InlineData(-2f)]
        public void AJumpInTheWaveClockRePrimes(float jumpSeconds)
        {
            var clock = new OceanFrameClock();
            clock.Advance(10f);
            clock.Advance(10f + 1f / 60f);
            Assert.True(clock.Advance(10f + 1f / 60f + jumpSeconds).Prime,
                $"a {jumpSeconds}s jump left the pending rows describing a time nobody is rendering");
        }

        /// <summary>The delta stays clamped across a jump: the foam integrates elapsed time, and a scrubbed clock
        /// must not inject a spike or (going backwards) run the dissipation the wrong way.</summary>
        [Fact]
        public void TheDeltaIsClampedAcrossAJumpInEitherDirection()
        {
            var clock = new OceanFrameClock();
            clock.Advance(10f);
            Assert.Equal(OceanFrameClock.MaxFrameDelta, clock.Advance(40f).Delta);
            Assert.Equal(0f, clock.Advance(5f).Delta);
        }

        /// <summary>A re-bake replaces the spectrum the pending rows were evolved from, so they stop describing the
        /// sea even though the clock never moved. One prime, not a permanent one.</summary>
        [Fact]
        public void InvalidateForcesExactlyOnePrime()
        {
            var clock = new OceanFrameClock();
            clock.Advance(0f);
            Assert.False(clock.Advance(1f / 60f).Prime);

            clock.Invalidate();
            Assert.True(clock.Advance(2f / 60f).Prime);
            Assert.False(clock.Advance(3f / 60f).Prime);
        }

        /// <summary>A NaN wave clock primes rather than silently consuming rows for a time nobody asked for. The
        /// comparison is written negated for exactly this case, and a NaN compares false against everything.
        /// </summary>
        [Fact]
        public void ANaNWaveClockPrimes()
        {
            var clock = new OceanFrameClock();
            clock.Advance(1f);
            Assert.True(clock.Advance(float.NaN).Prime);
        }
    }
}
