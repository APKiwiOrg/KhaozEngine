using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The cross-backend image regression for <see cref="WaterWaveSource.FftOcean"/>: open water at metre scale,
    /// at a frozen time and a fixed seed, with the shading knobs left at their shipped defaults.
    /// <para>
    /// It is a separate GOLDEN rather than a behavioural <c>[GpuFact]</c> because of what the CI matrix does with
    /// the name. The hosted Direct3D11 and Vulkan legs run only <c>FullyQualifiedName~Golden</c> on a push; the
    /// full suite reaches them on the weekly cron and on a manual dispatch. Without a golden, an FFT surface that
    /// broke on WARP or lavapipe would sit undetected until the following Sunday, which for a feature whose entire
    /// risk surface is "the compute seam behaves differently per backend" is the wrong way round.
    /// </para>
    /// <para>
    /// Determinism is what makes this affordable, and it is proved rather than assumed: the same seed at the same
    /// time produces bitwise-identical maps (<c>OceanFftGpuTests</c>), and the scene freezes
    /// <see cref="Scene3D.EffectTimeSeconds"/> the same way every other golden does. The sea state is deliberately
    /// SMALL - two cascades at 64 - because this runs on two software rasterizers, and the point here is that the
    /// pipeline renders the same picture on all three backends, not that it renders a big one.
    /// </para>
    /// <para>
    /// It is OPEN WATER rather than the doll-house lake <c>scene3d_water</c> renders, for two reasons. The
    /// spectrum ties wave height to wind and fetch, so a ten-metre pond can only physically carry centimetre waves
    /// and would bake a flat sheet no regression could move (the first bake of this test did exactly that). And
    /// the far field is where every previous water defect actually lived, per
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/310">#310</see>, and no golden looked at it.
    /// </para>
    /// <para>
    /// On a device without compute the producer degrades to <see cref="WaterWaveSource.Procedural"/>, which would
    /// silently render a DIFFERENT (and perfectly plausible) picture, so the test asserts the capability up front
    /// rather than comparing against a golden baked from the wrong mode.
    /// </para>
    /// </summary>
    public sealed class OceanFftGoldenTests
    {
        const int W = 480, H = 320;

        [GpuFact]
        public void Golden3D_FftOcean()
        {
            using (GpuDeviceContext probe = GpuDeviceContext.CreateHeadless())
            {
                Assert.True(probe.GpuDevice.Capabilities.SupportsCompute,
                    $"{probe.GpuDevice.Backend} reports no compute support, so this scene would silently fall back " +
                    "to the procedural surface and be compared against an FFT-baked golden");
            }

            MeshHandle seabed = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(160f, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    scene.Post.Sky.HorizonColor = new Color(0.66f, 0.72f, 0.80f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.20f, 0.40f, 0.72f, 1f);
                    scene.Post.Sky.SunRadius = 0.09f;
                    scene.Post.Sky.HaloStrength = 0.6f;
                    scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);

                    // OPEN WATER at metre scale, deliberately, not the doll-house lake the procedural golden
                    // renders. Two reasons. The spectrum ties wave height to wind and fetch, so a 10 unit pond can
                    // only physically carry centimetre waves and would bake a flat sheet that no regression could
                    // move. And the far field is where every previous water defect actually lived (#310), and no
                    // golden looked at it.
                    //
                    // The shading knobs stay at their SHIPPED defaults here for the same reason the procedural
                    // golden overrides them: those defaults are tuned for an ocean, and this is one.
                    scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
                    WaterSeaState sea = scene.Post.Water.SeaState;
                    sea.Seed = 20260726;
                    // Two cascades at 64 rather than the shipping three at 128: this runs on two software
                    // rasterizers, and what is being pinned is that the pipeline renders the same picture on every
                    // backend, not that it renders a big one.
                    sea.CascadeCount = 2;
                    sea.CascadeResolution = 64;

                    scene.Camera.Frame(Vector3.Zero, new Vector3(46f, 30f, 46f));
                    scene.EffectTimeSeconds = 0f;
                },
                drawFrame: scene =>
                {
                    // Seabed well below the surface: open water everywhere, past both the shore feather and the
                    // absorption depth, so the body colour is fully deep and the alpha is fully opaque. The surface
                    // itself is the whole subject.
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, -12f, 0f), new Color(0.18f, 0.20f, 0.18f, 1f));
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 70f));
                },
                frames: 2);

            // Anti-degeneracy guard, same shape as the procedural water golden's: the surface must vary cell to
            // cell. A blank map (the failure this program actually hit twice, from a swapped Metal binding and from
            // a one-layer texture array) renders as a flat sheet, which a committed grid would happily accept as
            // "correct" on the day it was baked from the same bug.
            float[] grid = GoldenCompare.Downsample(rgba, W, H);
            int waterCells = 0;
            float minBrightness = float.MaxValue, maxBrightness = float.MinValue;
            for (int cell = 0; cell < grid.Length / 3; cell++)
            {
                float r = grid[cell * 3], g = grid[cell * 3 + 1], b = grid[cell * 3 + 2];
                if (b < r - 0.02f || MathF.Max(r, MathF.Max(g, b)) <= 0.05f) continue;
                waterCells++;
                float brightness = (r + g + b) / 3f;
                minBrightness = MathF.Min(minBrightness, brightness);
                maxBrightness = MathF.Max(maxBrightness, brightness);
            }
            Assert.True(waterCells >= 40,
                $"scene3d_fftocean has only {waterCells} blue-dominant (water-ish) cells (of {grid.Length / 3}); " +
                "expected a sizeable visible water region. Check the DrawWater plane/camera framing.");
            Assert.True(maxBrightness - minBrightness >= 0.08f,
                $"scene3d_fftocean's water cells only span brightness {minBrightness:F3}..{maxBrightness:F3} " +
                "(range < 0.08). An FFT surface that produced no displacement or no slope reads as a flat sheet, " +
                "which is exactly what this range catches.");

            GoldenCompare.AssertOrUpdate("scene3d_fftocean", rgba, W, H);
        }
    }
}
