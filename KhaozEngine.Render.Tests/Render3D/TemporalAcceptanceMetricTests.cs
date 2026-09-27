using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The acceptance metrics on synthetic input, device free.</summary>
    public sealed class TemporalAcceptanceMetricTests
    {
        static byte[] Grey(float v) { byte b = (byte)(v * 255f + 0.5f); return new byte[] { b, b, b, 255 }; }

        static long Flips(params float[] lumas)
        {
            var counter = new FlipCounter(1, 1, new PixelRect(0, 0, 1, 1));
            foreach (float l in lumas) counter.Add(Grey(l));
            return counter.Flips;
        }

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
    }
}
