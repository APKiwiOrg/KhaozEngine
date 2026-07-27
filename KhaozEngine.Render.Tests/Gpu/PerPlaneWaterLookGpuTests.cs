using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The visual acceptance for the per-plane <see cref="WaterLook"/> (KhaozEngine#370): two water planes in ONE
    /// frame, one on the scene's FFT ocean and one overridden to a still procedural body, have to look different.
    /// The headless packing tests prove the override reaches the uniform slot. They cannot prove it reaches the
    /// picture, which is the only claim a consumer cares about.
    /// <para>
    /// <b>Relative, not a golden, and deliberately so.</b> Every assertion compares two REGIONS of the same render
    /// (or the same region across two renders), so nothing depends on a baked reference, on a backend's
    /// rasterization, or on the day it was baked. That is what lets this run on Metal, D3D11/WARP and
    /// Vulkan/lavapipe with no bake cycle at all. There is no "Golden" in the name on purpose: that substring is
    /// what makes CI fan a test across backends expecting a committed reference file.
    /// </para>
    /// <para>
    /// <b>The footprints come out of the render, not out of the projection.</b> Projecting the plane corners by
    /// hand would re-derive the engine's clip conventions in test code, where a mistake is a silently wrong
    /// measurement, and a screen-half split would additionally assume which way round the view basis puts world X.
    /// Instead each plane is rendered ALONE against a no-water render of the same scene, and the pixels it moves
    /// are its footprint - no projection, no handedness, no assumption about where in frame anything landed. The
    /// mask is then eroded so nothing on a silhouette is measured, and the same masks are reused across every
    /// condition, so a difference between conditions can never come from the sampling moving.
    /// </para>
    /// </summary>
    public sealed class PerPlaneWaterLookGpuTests
    {
        readonly ITestOutputHelper _out;

        public PerPlaneWaterLookGpuTests(ITestOutputHelper output) => _out = output;

        internal const int W = 640, H = 400;

        // Two disjoint XZ footprints either side of the camera's own axis, at different surface heights. Mirror
        // symmetric about x = 0 so the two regions sit at identical distances and identical sun geometry, which is
        // what makes the no-override control meaningful.
        const float SeaCenterX = -92f, LakeCenterX = 92f;
        const float PlaneCenterZ = 32f;
        const float HalfExtentX = 90f, HalfExtentZ = 88f;
        const float SeaSurfaceY = 0f, LakeSurfaceY = 1.5f;

        // A flat seabed well under both, in many small tiles. One big quad will not do: the depth the water pass
        // reconstructs is written per vertex and interpolated, so across a large perspective triangle the
        // reconstructed bottom drifts (see WaterDistanceBandingProbe's note on the same hazard).
        const float SeabedY = -14f;
        const float TileSize = 10f;
        const int TilesX = 52, TilesZ = 32;
        const float SeabedCenterZ = 60f;

        const float WaveTime = 8f;

        /// <summary>The look under test: a still inland body beside the sea. Four fields carry the intent, and the
        /// fifth (<see cref="WaterLook.NormalStrength"/>) is what makes it read as GLASSY rather than merely
        /// unswelled - the ripple normal field is live under <see cref="WaterWaveSource.Procedural"/> and would
        /// otherwise keep the surface busy at the fragment level.</summary>
        internal static WaterLook StillLake() => new()
        {
            WaveSource = WaterWaveSource.Procedural,
            SwellAmplitude = 0.02f,
            FoamStrength = 0f,
            SurfStrength = 0f,
            NormalStrength = 0.05f,
        };

        static WaterSeaState Sea() => new()
        {
            WindSpeed = 13f,
            WindDirectionDegrees = 90f,
            FetchKilometres = 140f,
            DepthMetres = 60f,
            DirectionalSpread = 0.75f,
            SwellAmount = 0.45f,
            SwellDirectionDegrees = 90f,
            Choppiness = 1.2f,
            SmallWaveCutoffMetres = 0.02f,
            Seed = 20260727,
            // Two cascades at 64 for the same reason the shore suite uses them: the software CI legs are slow
            // (KhaozEngine#332), and what needs proving per backend is that two differently-packed slots draw
            // differently, not that the ocean is big.
            CascadeCount = 2,
            CascadeResolution = 64,
            CascadeTileMetres = 220f,
            CascadeTileRatio = 4.2f,
            FoamGain = 1.8f,
            FoamJacobianBias = 0.5f,
            FoamDissipationPerSecond = 0.4f,
        };

        /// <summary>
        /// Render the two-plane scene. <paramref name="lakeLook"/> is the look on the overridable plane
        /// (<c>null</c> leaves it on the scene's sea, which is the control). <paramref name="water"/> false draws
        /// the seabed alone and <paramref name="only"/> queues a single plane (0 = sea, 1 = lake). Together those
        /// two are how each plane's screen footprint is derived, with no projection maths in test code.
        /// </summary>
        internal static byte[] Render(int w, int h, WaterLook? lakeLook, bool water = true, int only = -1,
            int frames = 3)
        {
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, 30f, -70f),
                Yaw = 0f,                     // straight along +Z, with the eye on the two planes' axis of symmetry
                Pitch = -0.26f,
                FieldOfView = MathF.PI / 3f,
                AspectRatio = (float)w / h,
                NearPlane = 0.5f,
                FarPlane = 900f,
            };

            MeshHandle tile = default;
            return Render3DSnapshot.Capture(w, h,
                setup: scene =>
                {
                    tile = scene.LoadMesh(MeshPrimitives.Tile(TileSize, 1f));
                    scene.EffectTimeSeconds = WaveTime;
                    scene.CameraOverride = fly;
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.World;
                    scene.Post.Sky.HorizonColor = new Color(0.74f, 0.80f, 0.86f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.20f, 0.42f, 0.72f, 1f);
                    // The sun sits dead ahead so the glint path runs down the frame's centre line and lands on both
                    // planes equally. Any x component would tilt the lighting toward one region and the control
                    // would be measuring that instead of the override.
                    scene.Post.LightDirection = Vector3.Normalize(new Vector3(0f, -0.50f, -0.87f));
                    scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
                    scene.Post.Water.SeaState = Sea();
                    // Uniform vertex density: both planes are on screen at once, and a camera-focused grid would
                    // hand each of them a different distribution depending on where its own near edge clamps.
                    scene.Post.Water.GridFocusBias = 1f;
                },
                drawFrame: scene =>
                {
                    for (int gz = 0; gz < TilesZ; gz++)
                    {
                        for (int gx = 0; gx < TilesX; gx++)
                        {
                            float x = (gx - (TilesX - 1) * 0.5f) * TileSize;
                            float z = SeabedCenterZ + (gz - (TilesZ - 1) * 0.5f) * TileSize;
                            scene.Draw(tile, Matrix4x4.CreateTranslation(x, SeabedY, z),
                                new Color(0.38f, 0.33f, 0.26f, 1f));
                        }
                    }
                    if (!water) return;
                    if (only != 1)
                        scene.DrawWater(new WaterPlane(SeaCenterX, SeaSurfaceY, PlaneCenterZ, HalfExtentX,
                            HalfExtentZ));
                    if (only != 0)
                        scene.DrawWater(new WaterPlane(LakeCenterX, LakeSurfaceY, PlaneCenterZ, HalfExtentX,
                            HalfExtentZ, lakeLook));
                },
                frames: frames);
        }

        // ---- Footprints and statistics ---------------------------------------------------------------------

        /// <summary>Pixels the water pass touched, eroded by <see cref="ErodeRadius"/> so nothing on a footprint's
        /// silhouette (where the displaced edge moves between conditions) is ever measured.</summary>
        const int ErodeRadius = 4;

        internal static bool[] Footprint(byte[] withWater, byte[] withoutWater, int w, int h)
        {
            var raw = new bool[w * h];
            for (int i = 0; i < raw.Length; i++)
            {
                int b = i * 4;
                int d = Math.Abs(withWater[b] - withoutWater[b])
                    + Math.Abs(withWater[b + 1] - withoutWater[b + 1])
                    + Math.Abs(withWater[b + 2] - withoutWater[b + 2]);
                raw[i] = d > 8;
            }

            var eroded = new bool[w * h];
            for (int y = ErodeRadius; y < h - ErodeRadius; y++)
            {
                for (int x = ErodeRadius; x < w - ErodeRadius; x++)
                {
                    bool all = true;
                    for (int dy = -ErodeRadius; dy <= ErodeRadius && all; dy++)
                        for (int dx = -ErodeRadius; dx <= ErodeRadius && all; dx++)
                            all = raw[(y + dy) * w + x + dx];
                    eroded[y * w + x] = all;
                }
            }
            return eroded;
        }

        /// <summary>What one region's surface looks like as numbers.</summary>
        readonly record struct Look(int Pixels, double Detail, double Bright, double Variance)
        {
            public override string ToString()
                => $"{Pixels,6} px  detail {Detail:F5}  bright {Bright:F5}  variance {Variance:F5}";
        }

        static double Luma(byte[] rgba, int i)
            => (0.2126 * rgba[i * 4] + 0.7152 * rgba[i * 4 + 1] + 0.0722 * rgba[i * 4 + 2]) / 255.0;

        /// <summary>How far above the frame's own median water luminance a pixel has to sit to count as foam. Read
        /// off the measurements rather than guessed: on this scene the two surfaces' medians are about 0.38 and
        /// their whitecaps run past 0.60, so a fifth of the range up is comfortably clear of the body colour and of
        /// the smooth sky sheen, and comfortably below the whitest foam.</summary>
        const double BrightMargin = 0.20;

        /// <summary>The bright-tail threshold for one frame: the median luminance over BOTH footprints, plus
        /// <see cref="BrightMargin"/>. Frame-relative on purpose. An absolute cut would make a backend that tone
        /// maps a few per cent darker look like it had lost its foam, which is exactly the cross-backend tuning
        /// this whole test shape exists to avoid, and the median is taken over the union so the threshold cannot
        /// drift with whichever region is under test.</summary>
        static double BrightThreshold(byte[] rgba, bool[] a, bool[] b, int w, int h)
        {
            var lums = new double[w * h];
            int n = 0;
            for (int i = 0; i < w * h; i++)
                if (a[i] || b[i]) lums[n++] = Luma(rgba, i);
            Array.Sort(lums, 0, n);
            return lums[n / 2] + BrightMargin;
        }

        /// <summary>
        /// Three numbers over one masked region.
        /// <list type="bullet">
        /// <item><b>Detail</b> is the mean 4-neighbour Laplacian of luminance: a LOCAL high pass, so a smooth
        /// distance gradient across the region contributes nothing and only surface structure (crests, the broken
        /// glint, foam speckle) does. This is the headline number, because "still" is a statement about the small
        /// scale rather than about the average.</item>
        /// <item><b>Bright</b> is the fraction of the region past <paramref name="brightThreshold"/>: the foam
        /// tail. The mean luminance will not do, because a glassy body reflecting a bright sky is just as bright
        /// on average as a rough one and only differs in the tail.</item>
        /// <item><b>Variance</b> is the plain luminance variance over the region. Reported, never asserted on, and
        /// the reason is the single most useful thing this test measured: on the override render the two regions
        /// come out at 0.0065 and 0.0066, indistinguishable, while the local detail separates by 7.6x. A whole-
        /// region variance is dominated by the distance and fresnel gradient across the footprint, which both
        /// surfaces share, so a test built on it would have reported no difference at all in a frame where the
        /// difference is obvious to the eye.</item>
        /// </list>
        /// </summary>
        static Look Measure(byte[] rgba, bool[] mask, int w, int h, double brightThreshold)
        {
            double sum = 0, sumSq = 0, detail = 0;
            int n = 0, dn = 0, bright = 0;
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (!mask[i]) continue;
                    double l = Luma(rgba, i);
                    sum += l;
                    sumSq += l * l;
                    if (l > brightThreshold) bright++;
                    n++;
                    if (!mask[i - 1] || !mask[i + 1] || !mask[i - w] || !mask[i + w]) continue;
                    detail += Math.Abs(4.0 * l - Luma(rgba, i - 1) - Luma(rgba, i + 1)
                        - Luma(rgba, i - w) - Luma(rgba, i + w));
                    dn++;
                }
            }
            if (n == 0 || dn == 0) return new Look(n, 0, 0, 0);
            double mean = sum / n;
            return new Look(n, detail / dn, bright / (double)n, sumSq / n - mean * mean);
        }

        // ---- The tests -------------------------------------------------------------------------------------

        /// <summary>
        /// The shippable claim: one frame, two planes, and the overridden one is materially calmer and materially
        /// less white than the sea beside it. Both margins are wide (multiples, not percentages) so no backend has
        /// to be tuned for, and both are one-sided so the test still fails if the override merely darkens the plane
        /// instead of stilling it.
        /// </summary>
        [GpuFact]
        public void AnOverriddenPlaneRendersStillBesideTheSceneSeaInTheSameFrame()
        {
            byte[] dry = Render(W, H, null, water: false);
            bool[] seaMask = Footprint(Render(W, H, null, only: 0), dry, W, H);
            bool[] lakeMask = Footprint(Render(W, H, null, only: 1), dry, W, H);
            byte[] mixed = Render(W, H, StillLake());

            double t = BrightThreshold(mixed, seaMask, lakeMask, W, H);
            Look sea = Measure(mixed, seaMask, W, H, t);
            Look lake = Measure(mixed, lakeMask, W, H, t);
            _out.WriteLine($"override  sea  {sea}   (bright cut {t:F4})");
            _out.WriteLine($"override  lake {lake}");

            Assert.True(sea.Pixels > 4000 && lake.Pixels > 4000,
                $"only {sea.Pixels} / {lake.Pixels} pixels of surface were found for the two planes, which is too " +
                "little to measure anything on. The camera or the footprints have moved.");

            Assert.True(sea.Detail > lake.Detail * 3.0,
                $"the overridden plane's surface detail is {lake.Detail:F5} against the sea's {sea.Detail:F5}, " +
                "less than the 3x gap a still body beside an FFT ocean has to show. Either the per-plane look is " +
                "not reaching the shader, or both planes are drawing from the same packed slot.");

            // The floor is the half of this that a ratio alone cannot state: if the sea itself had no foam tail,
            // "the lake has less" would be true and would mean nothing.
            Assert.True(sea.Bright > 0.004,
                $"only {sea.Bright:P2} of the SEA is past the foam cut, so there is no whitecap tail to lose and " +
                "the comparison below would pass vacuously.");
            Assert.True(sea.Bright > lake.Bright * 5.0,
                $"{lake.Bright:P3} of the overridden plane is past the foam cut against {sea.Bright:P3} of the " +
                "sea. FoamStrength = 0 on a Procedural source should take every whitecap off that body while the " +
                "sea beside it keeps them.");
        }

        /// <summary>
        /// The negative: with the look removed the two footprints measure the same. This is what rules out the
        /// geometry, the split and the two surface heights as the source of the gap above - without it, a test that
        /// happened to sample a calm corner of the ocean on the right would pass for the wrong reason.
        /// </summary>
        [GpuFact]
        public void WithNoLookOnEitherPlaneTheTwoFootprintsMeasureTheSame()
        {
            byte[] dry = Render(W, H, null, water: false);
            bool[] seaMask = Footprint(Render(W, H, null, only: 0), dry, W, H);
            bool[] lakeMask = Footprint(Render(W, H, null, only: 1), dry, W, H);
            byte[] control = Render(W, H, null);

            double t = BrightThreshold(control, seaMask, lakeMask, W, H);
            Look sea = Measure(control, seaMask, W, H, t);
            Look lake = Measure(control, lakeMask, W, H, t);
            _out.WriteLine($"control   sea  {sea}   (bright cut {t:F4})");
            _out.WriteLine($"control   lake {lake}");

            Assert.True(sea.Pixels > 4000 && lake.Pixels > 4000,
                $"only {sea.Pixels} / {lake.Pixels} pixels of surface were found for the two planes.");

            double detail = Ratio(sea.Detail, lake.Detail);
            Assert.True(detail < 1.8,
                $"with no look on either plane the two footprints' surface detail differ by {detail:F2}x " +
                $"(sea {sea.Detail:F5}, other {lake.Detail:F5}). They are the same sea, so a gap this size means " +
                "the footprints, the two surface heights or the sun geometry are doing the separating rather than " +
                "the override, and the acceptance test above proves nothing.");

            // Looser than the detail cap because it deserves to be: the whitecap tail is the stochastic part of the
            // ocean, and two footprints are two different patches of it. Measured at about 1.4x here against the
            // override's 50x, so the claim survives the slack easily.
            double foam = Ratio(sea.Bright, lake.Bright);
            Assert.True(foam < 2.5,
                $"with no look on either plane the two footprints' foam tails differ by {foam:F2}x " +
                $"(sea {sea.Bright:P3}, other {lake.Bright:P3}), which is half the gap the override is supposed " +
                "to open on its own.");
        }

        static double Ratio(double a, double b)
            => a > b ? a / Math.Max(b, 1e-9) : b / Math.Max(a, 1e-9);
    }
}
