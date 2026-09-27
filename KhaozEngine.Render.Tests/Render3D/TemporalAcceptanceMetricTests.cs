using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The acceptance metrics on synthetic input, device free. A sequence of one-pixel grey frames stands for
    /// one pixel's luma over time.</summary>
    public sealed class TemporalAcceptanceMetricTests
    {
        static byte[] Grey(float v) { byte b = (byte)(v * 255f + 0.5f); return new byte[] { b, b, b, 255 }; }

        static long Flips(params float[] lumas) => Count(lumas).Flips;

        static (long Flips, long Fast) Count(params float[] lumas)
        {
            var counter = new FlipCounter(1, 1, Pixel);
            foreach (float l in lumas) counter.Add(Grey(l));
            return (counter.Flips, counter.FastFlips);
        }

        static readonly PixelRect Pixel = new(0, 0, 1, 1);

        static byte[][] Sequence(params float[] lumas) => lumas.Select(Grey).ToArray();

        static double Error(float[] tested, float[] reference) =>
            TemporalAcceptance.TemporalError(Sequence(tested), Sequence(reference), 1, 1, Pixel);

        [Fact] public void AToggleAboveTheThresholdFlipsEveryReversal() => Assert.Equal(2, Flips(0f, 0.1f, 0f, 0.1f));
        [Fact] public void StepsBelowTheThresholdNeverFlip() => Assert.Equal(0, Flips(0f, 0.02f, 0f, 0.02f));
        [Fact] public void ARampNeverFlips() => Assert.Equal(0, Flips(0f, 0.1f, 0.2f, 0.3f));
        [Fact] public void TheSignSurvivesASmallStep() => Assert.Equal(1, Flips(0f, 0.1f, 0.11f, 0f));

        [Fact]
        public void TheRateDividesTheFlipsByTheClippedRegionAndTheFrameSteps()
        {
            // A 2 by 2 image toggling every frame, measured through a region that hangs off its bottom right corner.
            // Five frames make four steps and three reversals a pixel, so 12 flips over 4 pixels and 4 steps.
            var counter = new FlipCounter(2, 2, new PixelRect(0, 0, 5, 5));
            for (int i = 0; i < 5; i++)
            {
                byte v = (byte)(i % 2 == 0 ? 40 : 200);
                counter.Add(new byte[] { v, v, v, 255, v, v, v, 255, v, v, v, 255, v, v, v, 255 });
            }
            Assert.Equal(12, counter.Flips);
            Assert.Equal(0.75, counter.FlipsPerPixelPerFrame, 12);
        }

        [Fact]
        public void ABilinearUpscaleOfAFlatImageStaysFlat()
        {
            var src = new byte[4 * 3 * 4];
            for (int i = 0; i < src.Length; i += 4)
            {
                src[i] = 100; src[i + 1] = 150; src[i + 2] = 200; src[i + 3] = 255;
            }
            byte[] up = TemporalAcceptance.BilinearUpscale(src, 4, 3, 10, 7);
            for (int i = 0; i < up.Length; i += 4) { Assert.Equal(100, up[i]); Assert.Equal(200, up[i + 2]); }
        }

        [Fact]
        public void TheTrailIgnoresPixelsLeftOneFrameAgoAndWithinOnePixelOfNow()
        {
            const int W = 20, H = 4;
            var frame = new byte[W * H * 4];
            var bg = new byte[W * H * 4];
            for (int i = 3; i < frame.Length; i += 4) { frame[i] = 255; bg[i] = 255; }
            for (int y = 0; y < H; y++) { frame[(y * W + 5) * 4] = 255; frame[(y * W + 9) * 4] = 255; }
            var footprints = new[]
            {
                new PixelRect(10, 0, 12, H), new PixelRect(8, 0, 10, H), new PixelRect(4, 0, 6, H),
            };
            var (over, check, _) = TemporalAcceptance.Trail(frame, bg, W, H, footprints, 0.05f);
            Assert.Equal(2 * H, check);           // columns 4 and 5, two frames ago
            Assert.Equal(H, over);                // column 5 still lit, column 9 excluded as one frame ago
        }

        [Fact]
        public void TheColourTrailSeesAHueShiftThatKeepsTheLuma()
        {
            // The background is grey 100. The trail pixel is 160, 85, 100: its luma is 102, under a 0.05 step, while
            // its red is 60 steps, 0.235, away.
            const int W = 8, H = 1;
            var frame = new byte[W * H * 4];
            var bg = new byte[W * H * 4];
            for (int i = 0; i < frame.Length; i += 4)
            {
                frame[i] = frame[i + 1] = frame[i + 2] = bg[i] = bg[i + 1] = bg[i + 2] = 100;
                frame[i + 3] = bg[i + 3] = 255;
            }
            frame[1 * 4] = 160; frame[1 * 4 + 1] = 85;
            var footprints = new[] { new PixelRect(6, 0, 8, H), new PixelRect(4, 0, 6, H), new PixelRect(0, 0, 3, H) };

            var luma = TemporalAcceptance.Trail(frame, bg, W, H, footprints, 0.05f);
            var colour = TemporalAcceptance.Trail(frame, bg, W, H, footprints, 0.05f, PixelDifference.MaxChannel);

            Assert.Equal((0, 3), (luma.Over, luma.Checked));
            Assert.Equal((1, 3), (colour.Over, colour.Checked));
            Assert.Equal(60f / 255f, colour.Worst, 5);
        }

        [Fact] public void AReversalOfTheLastStepIsFast() => Assert.Equal((1, 1), Count(0f, 0.1f, 0f));

        [Fact]
        public void AReversalTwoFramesAfterTheLastStepIsFast() => Assert.Equal((1, 1), Count(0f, 0.1f, 0.1f, 0f));

        [Fact]
        public void AReversalThreeFramesAfterTheLastStepIsNotFast() =>
            Assert.Equal((1, 0), Count(0f, 0.1f, 0.1f, 0.1f, 0f));

        [Fact]
        public void TheTemporalErrorOfASequenceAgainstItselfIsZero()
        {
            float[] path = { 0f, 0.2f, 1f, 0.4f, 0.4f, 0.9f };
            Assert.Equal(0.0, Error(path, path));
        }

        [Fact]
        public void AFlickerTheReferenceLacksCountsEveryStep()
        {
            // 0.5 and 0.6 store as 128 and 153, so each of the four steps is 25/255 against a still reference.
            float[] flicker = { 0.5f, 0.6f, 0.5f, 0.6f, 0.5f }, still = { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
            Assert.Equal(25.0 / 255.0, Error(flicker, still), 6);
        }

        [Fact]
        public void ALaggedCrossingCountsBothTheMissedChangeAndTheLateOne()
        {
            // A thin feature crosses the pixel on frame 2 of the reference and on frame 3 of the tested sequence. The
            // steps differ by 0, 1, 2, 1 and 0, so 4 over 5 steps.
            Assert.Equal(0.8, Error(new[] { 0f, 0f, 0f, 1f, 0f, 0f }, new[] { 0f, 0f, 1f, 0f, 0f, 0f }), 6);
        }

        [Fact]
        public void ATemporalLowPassOfTheCrossingIsPenalisedWhereRawFlipsRewardIt()
        {
            // An exponential blend of 0.1 toward each reference frame smears the crossing into a rise of 0.1 and a fall
            // of about 0.01 a frame. It never reverses by more than the flip threshold, so it scores no flip where the
            // reference scores one, yet its temporal error is about 0.38 a step.
            float[] reference = { 0f, 0f, 1f, 0f, 0f, 0f };
            var blurred = new float[reference.Length];
            for (int t = 1; t < reference.Length; t++)
                blurred[t] = blurred[t - 1] + 0.1f * (reference[t] - blurred[t - 1]);

            Assert.Equal(1, Flips(reference));
            Assert.Equal(0, Flips(blurred));
            Assert.InRange(Error(blurred, reference), 0.35, 0.42);
        }

        [Fact]
        public void AFrozenImageScoresTheReferencesOwnChange()
        {
            float[] reference = { 0f, 0f, 1f, 0f, 0.4f, 0.4f };
            float[] frozen = { 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f };
            double change = TemporalAcceptance.MeanChange(Sequence(reference), 1, 1, Pixel);

            Assert.Equal((1.0 + 1.0 + 102.0 / 255.0) / 5.0, change, 6);
            Assert.Equal(change, Error(frozen, reference), 6);
        }

        [Fact]
        public void TheTemporalErrorRefusesSequencesOfDifferentLengthsOrOneFrame()
        {
            Assert.Throws<ArgumentException>(() => Error(new[] { 0f, 1f }, new[] { 0f, 1f, 0f }));
            Assert.Throws<ArgumentException>(() => Error(new[] { 0f }, new[] { 0f }));
        }

        [Fact]
        public void SharpnessIsOneForTheReferenceItselfAndZeroForAFlatFrame()
        {
            var checker = new byte[4 * 4 * 4];
            var flat = new byte[4 * 4 * 4];
            for (int p = 0; p < 16; p++)
            {
                byte v = (byte)(((p % 4) + (p / 4)) % 2 == 0 ? 0 : 255);
                checker[p * 4] = checker[p * 4 + 1] = checker[p * 4 + 2] = v;
                flat[p * 4] = flat[p * 4 + 1] = flat[p * 4 + 2] = 128;
                checker[p * 4 + 3] = flat[p * 4 + 3] = 255;
            }
            var all = new PixelRect(0, 0, 4, 4);
            Assert.Equal(1.0, TemporalAcceptance.Sharpness(checker, checker, 4, 4, all));
            Assert.Equal(0.0, TemporalAcceptance.Sharpness(flat, checker, 4, 4, all));
            Assert.True(double.IsNaN(TemporalAcceptance.Sharpness(checker, flat, 4, 4, all)));
        }

        [Fact]
        public void TheTrailNeedsTwoRectanglesAndAnEmptyOneNowExcludesNothing()
        {
            const int W = 3, H = 1;
            var frame = new byte[W * H * 4];
            var bg = new byte[W * H * 4];
            Assert.Throws<ArgumentException>(() => TemporalAcceptance.Trail(frame, bg, W, H, new[] { Pixel }, 0.05f));

            var footprints = new[] { default(PixelRect), default(PixelRect), new PixelRect(0, 0, 1, 1) };
            Assert.Equal(1, TemporalAcceptance.Trail(frame, bg, W, H, footprints, 0.05f).Checked);
        }

        [Fact]
        public void AFootprintWhoseEdgesLieOnPixelBoundariesHoldsExactlyItsPixels()
        {
            // 0.025 world units a pixel. A 0.75 box at the origin spans columns 145 to 175 and rows 75 to 105 exactly,
            // and the same box half a pixel to the right touches a partly covered column at each side.
            var stage = new FrontStage(320, 180, 4.5f);
            var half = new Vector3(0.375f, 0.375f, 0.25f);
            var shift = new Vector3(0.5f * stage.PixelWorld, 0f, 0f);

            Assert.Equal(new PixelRect(145, 75, 175, 105),
                TemporalAcceptance.Footprint(stage.Camera(), -half, half, 320, 180));
            Assert.Equal(new PixelRect(145, 75, 176, 105),
                TemporalAcceptance.Footprint(stage.Camera(), shift - half, shift + half, 320, 180));
        }
    }
}
