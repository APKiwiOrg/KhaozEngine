using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The background under a turning camera. The starfield places its stars by pixel, so under temporal
    /// anti-aliasing it takes zero motion and its stars hold still while the camera yaws: no smear, and the flip metric
    /// under threshold. The sky's sun sits in a world direction, so the sky keeps the rotation reprojection and its
    /// disc lands where a still frame at the same pose draws it. No geometry, so every pixel is background. HDR and the
    /// sharpen are off.
    /// </summary>
    public sealed class TemporalStarfieldGpuTests
    {
        const int W = 320, H = 180, Warm = 24, Measured = 16;
        const float YawPerFrame = 0.01f;   // radians, 0.57 degrees a frame, far under the 60 degree automatic cut

        // Acceptance thresholds, first estimates retuned in one place.
        const int MinStarPixels = 100;           // the reference frame has to draw at least this many star pixels
        const double MinStarsKept = 0.8;         // share of star pixels still lit after the yaw
        const double MaxYawFlipRate = 0.002;     // flips per pixel per frame under the yaw
        const double MaxFrameDrift = 0.004;      // mean abs luma between consecutive frames
        const double MaxSunMissPixels = 1.0;     // sun centroid distance from the still frame's
        const int MinSunPixels = 10;             // the sun disc's core has to cover at least this many pixels

        readonly ITestOutputHelper _out;
        public TemporalStarfieldGpuTests(ITestOutputHelper output) => _out = output;

        static void Stage(Scene3D s, AntiAliasing aa, bool sky)
        {
            s.Post.Quality.AntiAliasing = aa;
            s.Post.Temporal.Upscale = TemporalUpscale.Native;
            s.Post.Temporal.Sharpness = 0f;       // the sharpen would add its own ringing around a one-pixel star
            s.Post.Hdr.Enabled = false;           // LDR, so a star and the sun disc read on the 8-bit scale as authored
            s.Post.TransparentBackground = false;
            s.Post.Background = sky ? BackgroundMode.Sky : BackgroundMode.Starfield;   // after the fixture's preset
            // Ahead and a little up.
            s.Post.Sky.SunDirectionOverride = Vector3.Normalize(new Vector3(0.1f, 0.12f, 1f));
            s.CameraOverride = new FlyCamera3D
            {
                Position = new Vector3(0f, 1.5f, 0f),
                Yaw = 0f,
                Pitch = 0.05f,
                FieldOfView = MathF.PI / 3f,
                AspectRatio = (float)W / H,
                NearPlane = 0.1f,
                FarPlane = 400f,
            };
        }

        // Frame n looks YawPerFrame * n radians round from +Z. Draws nothing.
        static void Turn(Scene3D s, int n) => ((FlyCamera3D)s.CameraOverride!).Yaw = YawPerFrame * n;

        [GpuFact]
        public void AStarfieldHoldsStillWhileTheCameraYaws()
        {
            // The stars do not depend on the camera, so any still frame is the reference for every frame.
            byte[] reference;
            using (var still = new TemporalFixture(W, H, s => Stage(s, AntiAliasing.Off, sky: false)))
                reference = still.Frame(Turn);

            var region = new PixelRect(2, 2, W - 2, H - 2);
            var flips = new FlipCounter(W, H, region);
            byte[] last = Array.Empty<byte>(), before = Array.Empty<byte>();
            using (var fx = new TemporalFixture(W, H, s => Stage(s, AntiAliasing.Temporal, sky: false)))
            {
                fx.Frames(Warm, Turn);
                for (int i = 0; i < Measured; i++)
                {
                    before = last;
                    last = fx.Frame(Turn);
                    flips.Add(last);
                }
                Assert.Equal(1f, fx.Scene.TemporalResolveRendererForTests!.LastUniforms.Params.Y);
            }

            int stars = 0, kept = 0;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                {
                    if (TemporalAcceptance.Luma(reference, W, x, y) < 0.3f) continue;
                    stars++;
                    if (TemporalAcceptance.Luma(last, W, x, y) > 0.15f) kept++;
                }
            double rate = flips.FlipsPerPixelPerFrame;
            double drift = TemporalAcceptance.MeanAbsLuma(last, before, W, region);
            double off = TemporalAcceptance.MeanAbsLuma(last, reference, W, region);
            _out.WriteLine($"starfield under a {YawPerFrame} rad a frame yaw: {rate:0.00000} flips per pixel per frame "
                + $"({flips.FastFlipsPerPixelPerFrame:0.00000} fast), {kept} of {stars} star pixels kept, frame to "
                + $"frame mean abs luma {drift:0.00000}, against the still frame {off:0.00000}");
            Assert.True(stars > MinStarPixels, $"the reference drew only {stars} star pixels");
            Assert.True(kept >= MinStarsKept * stars, $"the stars must stay on their pixels: {kept} of {stars} kept");
            Assert.True(rate <= MaxYawFlipRate,
                $"the starfield must not flicker under a yaw: {rate:0.00000} flips per pixel per frame");
            Assert.True(drift <= MaxFrameDrift, $"consecutive frames must match: mean abs luma {drift:0.00000}");
        }

        [GpuFact]
        public void TheSkyKeepsTheRotationReprojectionAndItsSunLandsWhereAStillFrameDrawsIt()
        {
            const int last = Warm + Measured;
            byte[] reference;
            using (var still = new TemporalFixture(W, H, s => Stage(s, AntiAliasing.Off, sky: true)))
            {
                still.SkipFrames(last);
                reference = still.Frame(Turn);
            }
            byte[] taa;
            using (var fx = new TemporalFixture(W, H, s => Stage(s, AntiAliasing.Temporal, sky: true)))
            {
                fx.Frames(last, Turn);
                taa = fx.Frame(Turn);
                TemporalResolveUniforms u = fx.Scene.TemporalResolveRendererForTests!.LastUniforms;
                Assert.Equal(0f, u.Params.Y);
                Assert.Equal(1f, u.Jitter.W);
                Assert.NotEqual(Matrix4x4.Identity, u.BackgroundToPrevious);
            }
            (double X, double Y) want = SunCentroid(reference), got = SunCentroid(taa);
            double miss = Math.Sqrt((want.X - got.X) * (want.X - got.X) + (want.Y - got.Y) * (want.Y - got.Y));
            _out.WriteLine($"sky under a yaw: sun centroid {got.X:0.00},{got.Y:0.00} against a still frame's "
                + $"{want.X:0.00},{want.Y:0.00}, {miss:0.00} px apart");
            Assert.True(miss <= MaxSunMissPixels,
                $"the sun must follow the rotation reprojection: {miss:0.00} px from the still frame's");
        }

        // The centre of the pixels bright enough to be the sun disc's core (the sun colour's luma is 0.96, the
        // horizon's 0.69).
        static (double X, double Y) SunCentroid(byte[] rgba)
        {
            double sx = 0, sy = 0;
            int count = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (TemporalAcceptance.Luma(rgba, W, x, y) > 0.9f) { sx += x; sy += y; count++; }
            Assert.True(count > MinSunPixels, $"the sun disc covered only {count} pixels");
            return (sx / count, sy / count);
        }
    }
}
