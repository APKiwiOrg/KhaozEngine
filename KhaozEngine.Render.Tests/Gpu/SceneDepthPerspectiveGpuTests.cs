using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The scene-depth MRT under a PERSPECTIVE camera, asserted through the consumer that reconstructs a world
    /// position out of it: the water pass (issue #301).
    /// <para>
    /// <b>The claim is that triangle size does not change the depth.</b> One seabed plane is drawn twice, once as a
    /// single 2400-unit quad and once as a 24x24 grid of 100-unit tiles covering the same ground, and the two
    /// frames have to agree. They did not. The depth was <c>gl_Position.z / gl_Position.w</c> computed per VERTEX
    /// and carried in a varying, and NDC z is not an affine function of world position, so a varying (which
    /// interpolates perspective-correct, i.e. reproduces exactly the functions that ARE affine in world space)
    /// does not reproduce it. The error is zero at the vertices and grows with the w-range the triangle spans,
    /// which across a 2400-unit ground quad is most of the frame. Small triangles pinned the value often enough to
    /// hide it, which is why every other water scene in this suite tiles its ground, and no golden ever caught it
    /// because they are all orthographic, where w is constant and the interpolation is exact.
    /// </para>
    /// <para>
    /// <b>Why the water is tuned before it is measured.</b> At the shipped defaults this scene renders the same
    /// either way, and that is not the fix working, it is the instrument being blind: absorption is saturated by
    /// about 12 metres and the seabed is 40 down, so every depth from ~15 to infinity paints the identical deep
    /// tint and the reconstruction can be wildly wrong without moving a pixel. The legacy two-stop blend over an
    /// 80-unit <see cref="WaterSettings.ShallowDepth"/> straddles the true 40 instead, so the body colour is a live
    /// readout of the reconstructed depth, and foam, glint and sky reflection are off so nothing else can move.
    /// </para>
    /// <para>
    /// NOT a golden: no pixel is locked and nothing is baked. Both frames come from the same device in the same
    /// run, so the whole comparison is backend-relative and carries no committed reference to rot. Skipped unless
    /// KE_GPU_TESTS=1.
    /// </para>
    /// </summary>
    [Collection("HdrGpu")]   // serialise with the other Metal-context suites
    public sealed class SceneDepthPerspectiveGpuTests
    {
        const int W = 640, H = 360;

        /// <summary>Half-extent of the probe ocean, matching Ruinborne's Far render-distance tier.</summary>
        const float OceanHalfExtent = 600f;

        /// <summary>Still-water height, with the seabed this far below it.</summary>
        const float WaterY = 0f;
        const float SeabedDrop = 40f;

        /// <summary>The two seabeds: one 2400-unit quad, or a 24x24 grid of 100-unit tiles. Both cover exactly
        /// x,z in [-1200, 1200] about the camera, so the plane under the water is the same plane.</summary>
        const float OneQuadSize = 2400f;
        const int GridTiles = 24;
        const float GridTileSize = 100f;

        /// <summary>
        /// The issue's repro scene: open ocean from slight elevation, sun low and nearly dead ahead. The only
        /// thing that changes between the two renders is how many triangles the seabed is made of.
        /// </summary>
        static byte[] RenderOcean(bool oneQuad)
        {
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, 26f, -OceanHalfExtent * 0.5f),
                Yaw = 0f,
                Pitch = -0.14f,
                FieldOfView = MathF.PI / 3f,
                AspectRatio = (float)W / H,
                NearPlane = 0.1f,
                FarPlane = 900f,
            };

            MeshHandle seabed = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(oneQuad ? OneQuadSize : GridTileSize, 1f));
                    scene.EffectTimeSeconds = 0f;   // deterministic frame
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.CameraOverride = fly;
                    scene.Post.BackgroundColor = new Color(0.55f, 0.66f, 0.80f, 1f);

                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.World;
                    scene.Post.Sky.HorizonColor = new Color(0.72f, 0.79f, 0.86f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.22f, 0.44f, 0.74f, 1f);
                    scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.08f, -0.26f, -0.96f));

                    // Make the surface a readout of the reconstructed depth, and only of that.
                    WaterSettings w = scene.Post.Water;
                    w.AbsorptionPerMetre = new Color(0f, 0f, 0f, 0f);   // all-zero selects the two-stop blend
                    w.ShallowDepth = 80f;                               // ramps across the true 40-unit depth
                    w.FoamStrength = 0f;
                    w.GlintStrength = 0f;
                    w.SkyReflectionStrength = 0f;
                },
                drawFrame: scene =>
                {
                    var tint = new Color(0.16f, 0.18f, 0.17f, 1f);
                    if (oneQuad)
                    {
                        scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, WaterY - SeabedDrop, fly.Position.Z),
                            tint);
                    }
                    else
                    {
                        for (int gz = 0; gz < GridTiles; gz++)
                            for (int gx = 0; gx < GridTiles; gx++)
                            {
                                float x = (gx - (GridTiles - 1) * 0.5f) * GridTileSize;
                                float z = (gz - (GridTiles - 1) * 0.5f) * GridTileSize + fly.Position.Z;
                                scene.Draw(seabed, Matrix4x4.CreateTranslation(x, WaterY - SeabedDrop, z), tint);
                            }
                    }
                    scene.DrawWater(new WaterPlane(centerX: fly.Position.X, surfaceY: WaterY,
                        centerZ: fly.Position.Z, halfExtentX: OceanHalfExtent));
                },
                frames: 2);
        }

        /// <summary>The near-field band: the rows well below the horizon, inset from the frame edges so neither the
        /// very bottom of the projection nor a corner can carry a result on its own.</summary>
        static (int X0, int X1, int Y0, int Y1) Band()
            => ((int)(W * 0.10f), (int)(W * 0.90f), (int)(H * 0.55f), (int)(H * 0.95f));

        static float Luminance(byte[] rgba, int x, int y)
        {
            int i = (y * W + x) * 4;
            return 0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2];
        }

        /// <summary>Mean luminance and standard deviation over the near-field band.</summary>
        static (float Mean, float StdDev) BandStats(byte[] rgba)
        {
            (int x0, int x1, int y0, int y1) = Band();
            double sum = 0, sumSq = 0;
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    float lum = Luminance(rgba, x, y);
                    sum += lum; sumSq += (double)lum * lum; n++;
                }
            double mean = sum / n;
            return ((float)mean, (float)Math.Sqrt(Math.Max(0.0, sumSq / n - mean * mean)));
        }

        /// <summary>Mean and worst per-pixel luminance difference over the near-field band.</summary>
        static (float Mean, float Worst) BandDifference(byte[] a, byte[] b)
        {
            (int x0, int x1, int y0, int y1) = Band();
            double sum = 0;
            float worst = 0f;
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    float d = MathF.Abs(Luminance(a, x, y) - Luminance(b, x, y));
                    sum += d; n++;
                    if (d > worst) worst = d;
                }
            return ((float)(sum / n), worst);
        }

        /// <summary>
        /// The same seabed plane reconstructs the same depth whether it is one quad or a grid of tiles. Measured
        /// on Metal, before the per-fragment depth fix: one-quad mean 78.43 against a tiled mean of 125.67, with
        /// a per-pixel difference of mean 47.24 and worst 82.11. The reconstructed ground was tens of metres out,
        /// so the two-stop blend painted deep water where the tiled reference painted mid. After: both means
        /// 120.23, difference mean 0.01 and worst 1.00. The thresholds sit between the two with room for a
        /// software rasterizer to disagree with itself at a seam.
        /// </summary>
        [GpuFact]
        public void One_seabed_quad_reconstructs_the_same_depth_as_a_tiled_seabed()
        {
            byte[] oneQuad = RenderOcean(oneQuad: true);
            byte[] tiled = RenderOcean(oneQuad: false);

            (float mean, float worst) = BandDifference(oneQuad, tiled);
            (float meanOne, float sdOne) = BandStats(oneQuad);
            (float meanTiled, _) = BandStats(tiled);
            string seen = $"one-quad mean {meanOne:F2}, tiled mean {meanTiled:F2}, " +
                          $"per-pixel difference mean {mean:F2} worst {worst:F2}";

            Assert.True(sdOne > 2f,
                $"the near field over the single quad is flat, i.e. the water was discarded outright ({seen})");
            Assert.True(mean < 3f,
                $"triangle size moved the reconstructed scene depth under perspective ({seen}, issue #301)");
            Assert.True(worst < 20f, $"a near-field region reconstructs a different depth over one quad ({seen})");
        }
    }
}
