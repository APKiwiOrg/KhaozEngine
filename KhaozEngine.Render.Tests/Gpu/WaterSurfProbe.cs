using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Human-facing PNG dumps of the 16.13.0 shore work (NOT goldens: no pixel lock, no "Golden" in the name).
    /// Two subjects, both of which are judgements a number cannot make: whether the shallows read as CALM rather
    /// than merely smaller, and whether the surf band reads as a wave crashing rather than a lit strip.
    /// <para>
    /// The beach is the same ramp <c>WaterShoreGpuTests</c> measures on, at a player's eye height instead of a
    /// measurement camera, because that is the viewpoint the feedback came from. Dumps land in KE_PNG_DUMP_DIR
    /// (temp when unset). Skipped unless KE_GPU_TESTS=1.
    /// </para>
    /// </summary>
    [Collection("HdrGpu")]   // serialise with the other Metal-context suites
    public sealed class WaterSurfProbe
    {
        const int W = 960, H = 540;
        const float GroundAtOrigin = -6f;
        const float Slope = 0.10f;
        const float PlaneHalfExtent = 400f;
        // Many SMALL tiles: the depth the water pass reconstructs is written per vertex and interpolated,
        // so a large perspective triangle drifts far enough to discard water that should be drawn (see
        // WaterDistanceBandingProbe's note on the same hazard).
        const int BeachTiles = 100;
        const float BeachTileSize = 8f;

        /// <summary>The ramp runs along +Z, so a camera at yaw 0 looks straight up the beach.</summary>
        static float GroundY(float z) => GroundAtOrigin + Slope * z;

        static string DumpDir()
        {
            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Dump(string name, byte[] rgba)
        {
            string png = Path.Combine(DumpDir(), name);
            PngWriter.Save(png, rgba, W, H);
            Assert.True(new FileInfo(png).Length > 0, $"expected a PNG dump at {png}");
        }

        static WaterBathymetry Field()
        {
            var field = new WaterBathymetry(256, centerX: 0f, centerZ: 0f, halfExtentX: PlaneHalfExtent);
            field.FillFromGround((_, z) => GroundY(z), surfaceY: 0f);
            return field;
        }

        /// <summary>Render the beach from just off it, looking up the ramp: deep water in the near field, the surf
        /// zone across the middle, dry sand beyond it.</summary>
        static byte[] RenderBeach(float time, Action<WaterSettings> tune)
        {
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, 22f, -10f),
                Yaw = 0f,                  // looking along +Z, from just outside the break up the beach
                Pitch = -0.34f,
                FieldOfView = MathF.PI / 3f,
                AspectRatio = (float)W / H,
                NearPlane = 0.1f,
                FarPlane = 900f,
            };

            MeshHandle tile = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    tile = scene.LoadMesh(MeshPrimitives.Tile(BeachTileSize, 1f));
                    scene.EffectTimeSeconds = time;
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.CameraOverride = fly;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.World;
                    scene.Post.Sky.HorizonColor = new Color(0.72f, 0.79f, 0.86f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.22f, 0.44f, 0.74f, 1f);
                    scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.35f, -0.55f, -0.75f));
                    scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
                    tune(scene.Post.Water);
                },
                drawFrame: scene =>
                {
                    float angle = MathF.Atan(Slope);
                    for (int gz = 0; gz < BeachTiles; gz++)
                    {
                        for (int gx = 0; gx < BeachTiles; gx++)
                        {
                            float x = (gx - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                            float z = (gz - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                            scene.Draw(tile,
                                Matrix4x4.CreateRotationX(-angle) * Matrix4x4.CreateTranslation(x, GroundY(z), z),
                                new Color(0.44f, 0.40f, 0.31f, 1f));
                        }
                    }
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f,
                        halfExtentX: PlaneHalfExtent));
                },
                frames: 2);
        }

        /// <summary>The open-water surface running straight onto a beach with no depth field: the 16.12.0
        /// behaviour, and the thing the playtest called out. Full-height swell right up to the waterline and
        /// nothing breaking on it.</summary>
        [GpuFact]
        public void Beach_without_bathymetry()
            => Dump("shore_none.png", RenderBeach(6f, _ => { }));

        /// <summary>The same beach with a depth field bound, at the shipped defaults.</summary>
        [GpuFact]
        public void Beach_with_shoaling_and_surf()
            => Dump("shore_surf.png", RenderBeach(6f, w => w.Bathymetry = Field()));

        /// <summary>Shoaling alone, so the surf band's contribution is the difference between this and the one
        /// above rather than something to be taken on trust.</summary>
        [GpuFact]
        public void Beach_with_shoaling_only()
            => Dump("shore_shoal.png", RenderBeach(6f, w =>
            {
                w.Bathymetry = Field();
                w.SurfStrength = 0f;
            }));

        /// <summary>The band on its own, with the taper turned off: an unambiguous read of WHERE the surf is and
        /// how wide it is, without the calmed shallows underneath it confusing the eye.</summary>
        [GpuFact]
        public void Beach_with_surf_only()
            => Dump("shore_surf_only.png", RenderBeach(6f, w =>
            {
                w.Bathymetry = Field();
                w.ShoalingStrength = 0f;
            }));

        // ---- The clipmap geomorph (#348) -----------------------------------------------------------------

        /// <summary>
        /// The #348 residual, made visible - as a HEIGHT field, not as pixels.
        /// <para>
        /// A rendered difference cannot show this, and it is worth saying why because pixels are the obvious thing
        /// to reach for. Moving the camera legitimately changes the fresnel term, the glint lobe and the footprint
        /// band-limit at every fragment on the surface, and those swamp a residual measured in millimetres of
        /// height: amplified enough to see, the whole sea saturates. So this dumps exactly what the acceptance test
        /// measures - the surface HEIGHT over a world-fixed lattice, at frozen wave time, differenced across a
        /// camera step - where the only thing that can be non-zero is the grid resampling itself.
        /// </para>
        /// <para>
        /// Two steps, because they show different halves of the same claim. At the metric's own 0.5 m only the
        /// innermost ring snaps, and the hard swap draws its boundary as a hard square outline while the geomorph
        /// draws a dimmer, wider smear of the same total. At 8 m five rings snap at once and the whole concentric
        /// structure is in frame. Both pairs are normalized TOGETHER, so a dimmer image is genuinely a smaller
        /// change rather than a rescaled one - which is the entire point, since the geomorph trades a sharp step in
        /// a thin band for a gentle one over a wide one, and only the first of those is visible in motion.
        /// </para>
        /// </summary>
        [GpuFact]
        public void Clipmap_boundary_step_height_maps()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            var settings = new WaterSettings
            {
                WaveSource = WaterWaveSource.FftOcean,
                GridMode = WaterGridMode.Clipmap,
                ClipmapCellSize = 0.5f,
                ClipmapRingCells = 32,
            };
            WaterSeaState sea = settings.SeaState;
            sea.CascadeCount = WaterMirror.Cascades;
            sea.CascadeResolution = WaterMirror.N;
            sea.Seed = 20260727;
            settings.SeaState = sea;
            // Read the shipped default BEFORE anything writes the band onto this shared object.
            float shipped = settings.ClipmapGeomorphBand;

            using var producer = new OceanFftProducer(dev);
            WaterMirror.Ocean maps = WaterMirror.Capture(dev, producer, settings, 7.5f);

            foreach (float step in new[] { 0.5f, 8f })
            {
                float[] hard = HeightDifference(maps, settings, 0f, step);
                float[] morphed = HeightDifference(maps, settings, shipped, step);
                float scale = 0f;
                foreach (float v in hard) scale = MathF.Max(scale, v);
                foreach (float v in morphed) scale = MathF.Max(scale, v);
                scale = scale > 1e-9f ? 255f / scale : 0f;
                string tag = step < 1f ? "near" : "far";
                DumpGrey($"clipmap_hard_swap_{tag}.png", hard, scale);
                DumpGrey($"clipmap_geomorphed_{tag}.png", morphed, scale);
            }
        }

        /// <summary>Resolution of the height-difference maps, and the world square they cover.</summary>
        const int MapSize = 400;
        const float MapHalfExtent = 120f;

        static float[] HeightDifference(in WaterMirror.Ocean maps, WaterSettings settings, float band, float step)
        {
            settings.ClipmapGeomorphBand = band;
            var plane = new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 600f);
            WaterMirror.Surface before = WaterMirror.Surface.Clip(plane, maps, settings, 0f);
            WaterMirror.Surface after = WaterMirror.Surface.Clip(plane, maps, settings, step);

            var diff = new float[MapSize * MapSize];
            for (int j = 0; j < MapSize; j++)
            {
                float pz = -MapHalfExtent + (j + 0.317f) * (2f * MapHalfExtent / MapSize);
                for (int i = 0; i < MapSize; i++)
                {
                    float px = -MapHalfExtent + (i + 0.712f) * (2f * MapHalfExtent / MapSize);
                    diff[j * MapSize + i] = MathF.Abs(after.HeightAt(px, pz) - before.HeightAt(px, pz));
                }
            }
            return diff;
        }

        static void DumpGrey(string name, float[] values, float scale)
        {
            var rgba = new byte[MapSize * MapSize * 4];
            for (int i = 0; i < values.Length; i++)
            {
                byte v = (byte)Math.Clamp(values[i] * scale, 0f, 255f);
                rgba[i * 4] = v; rgba[i * 4 + 1] = v; rgba[i * 4 + 2] = v; rgba[i * 4 + 3] = 255;
            }
            string png = Path.Combine(DumpDir(), name);
            PngWriter.Save(png, rgba, MapSize, MapSize);
            Assert.True(new FileInfo(png).Length > 0, $"expected a PNG dump at {png}");
        }

        /// <summary>Two seconds later. Held side by side with <see cref="Beach_with_shoaling_and_surf"/> this is
        /// the only way to see the thing the band is actually for: the white has to have MOVED up the beach with
        /// the wave, not brightened in place.</summary>
        [GpuFact]
        public void Beach_with_surf_two_seconds_later()
            => Dump("shore_surf_t2.png", RenderBeach(8f, w => w.Bathymetry = Field()));
    }
}
