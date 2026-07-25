using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Human-facing PNG dumps of an OPEN OCEAN at the two viewpoints a player reported distance banding from
    /// (NOT goldens: no pixel lock, no "Golden" in the name). Everything about the surface is left at the shipped
    /// <see cref="WaterSettings"/> defaults on purpose - the artifact under investigation is what the defaults do,
    /// so a scene that tuned it away would prove nothing.
    /// <para>
    /// The reported look, from a Ruinborne playtest of 14.24.0: "there are 'lines' over the whole ocean which look
    /// very textured/tiled and it looks very basic from even a slight distance. Upclose its masked." Two shots, one
    /// at beach eye height showing parallel crest bands in the mid field, one from slight elevation where the whole
    /// surface reads as dense parallel wavy stripes with moire.
    /// </para>
    /// <para>
    /// Both views therefore point a perspective camera at the horizon over a plane big enough that the far field is
    /// most of the frame, which is the regime the 14.24.0 goldens never covered: both of those frame a 9-unit lake
    /// from a corner under an orthographic camera, where every ripple is comfortably resolved and the artifact
    /// cannot appear. Dumps land in KE_PNG_DUMP_DIR (temp when unset). Skipped unless KE_GPU_TESTS=1.
    /// </para>
    /// </summary>
    [Collection("HdrGpu")]   // serialise with the other Metal-context suites
    public sealed class WaterDistanceBandingProbe
    {
        const int W = 960, H = 540;

        /// <summary>Half-extent of the probe ocean, matching Ruinborne's Far render-distance tier.</summary>
        const float OceanHalfExtent = 600f;

        /// <summary>Still-water height. The seabed sits far enough below that every fragment is deep water, so the
        /// shore fade, the shoreline foam and the shallow end of the absorption ramp are all out of the picture and
        /// what remains is purely the ripple + swell shading this probe is about.</summary>
        const float WaterY = 0f;

        /// <summary>Seabed subdivision (see the note at the draw site: the depth reconstruction the water pass
        /// relies on degrades badly across large perspective triangles).</summary>
        const int SeabedTiles = 25;
        const float SeabedTileSize = 120f;

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

        /// <summary>
        /// Render the open ocean from <paramref name="eyeHeight"/>, looking just below the horizon along +Z. The sun
        /// is low and close to the view heading, which is the pose that maximizes both the specular ribbons and the
        /// crest-band contrast, i.e. the worst case for the artifact.
        /// </summary>
        static byte[] RenderOcean(float eyeHeight, float pitch, Action<WaterSettings>? tune = null)
        {
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, eyeHeight, -OceanHalfExtent * 0.5f),
                Yaw = 0f,
                Pitch = pitch,
                FieldOfView = MathF.PI / 3f,
                AspectRatio = (float)W / H,
                NearPlane = 0.1f,
                FarPlane = 900f,
            };

            MeshHandle seabed = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(SeabedTileSize, 1f));
                    scene.EffectTimeSeconds = 0f;   // deterministic frame
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.CameraOverride = fly;
                    scene.Post.BackgroundColor = new Color(0.55f, 0.66f, 0.80f, 1f);

                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.World;
                    scene.Post.Sky.HorizonColor = new Color(0.72f, 0.79f, 0.86f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.22f, 0.44f, 0.74f, 1f);
                    scene.Post.Sky.SunColor = new Color(1f, 0.96f, 0.86f, 1f);
                    scene.Post.Sky.SunRadius = 0.05f;
                    scene.Post.Sky.HaloStrength = 0.5f;
                    scene.Post.Sky.HaloFalloff = 0.2f;

                    // Sun low and almost dead ahead. A directional light TRAVELS along LightDirection and the sun
                    // sits at -normalize(that), so pointing the travel toward -Z puts the disc in FRONT of a
                    // camera looking down +Z, laying the specular path straight up the frame. That pose is the
                    // worst case for both artifacts under investigation.
                    scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.08f, -0.26f, -0.96f));

                    // WaterSettings otherwise untouched: this probe measures the defaults.
                    tune?.Invoke(scene.Post.Water);
                },
                drawFrame: scene =>
                {
                    // The seabed is TILED rather than one huge quad, and that is load-bearing. The depth the water
                    // pass reconstructs from is `gl_Position.z / gl_Position.w` written per VERTEX and interpolated
                    // (ShaderSources.Model.cs), which under a perspective projection is not the true per-fragment
                    // NDC z. Across one 2400-unit quad the error is large enough that the reconstructed seabed
                    // lands above the surface, the shore fade drives alpha to zero and the near water is discarded
                    // outright. Small triangles keep the error negligible. Tracked separately; not this change's
                    // job to fix, and Ruinborne does not hit it because its terrain is finely chunked.
                    for (int gz = 0; gz < SeabedTiles; gz++)
                        for (int gx = 0; gx < SeabedTiles; gx++)
                        {
                            float x = (gx - (SeabedTiles - 1) * 0.5f) * SeabedTileSize;
                            float z = (gz - (SeabedTiles - 1) * 0.5f) * SeabedTileSize + fly.Position.Z;
                            scene.Draw(seabed, Matrix4x4.CreateTranslation(x, WaterY - 40f, z),
                                new Color(0.16f, 0.18f, 0.17f, 1f));
                        }
                    scene.DrawWater(new WaterPlane(centerX: fly.Position.X, surfaceY: WaterY, centerZ: fly.Position.Z,
                        halfExtentX: OceanHalfExtent));
                },
                frames: 2);
        }

        /// <summary>Beach eye height, the first reported shot: the mid field is where the parallel crest bands
        /// showed.</summary>
        [GpuFact]
        public void Ocean_from_beach_eye_height()
        {
            byte[] rgba = RenderOcean(eyeHeight: 1.7f, pitch: -0.035f);
            Dump("water_bands_low.png", rgba);
            Assert.True(HorizontalStreakScore(rgba) >= 0f);   // smoke only; the measurement is reported, not gated
        }

        /// <summary>Slight elevation, the second reported shot: the whole surface filled with dense parallel
        /// stripes and moire.</summary>
        [GpuFact]
        public void Ocean_from_slight_elevation()
        {
            byte[] rgba = RenderOcean(eyeHeight: 26f, pitch: -0.14f);
            Dump("water_bands_high.png", rgba);
            Assert.True(HorizontalStreakScore(rgba) >= 0f);
        }

        /// <summary>
        /// The issue #299 case: a TIGHT specular lobe on the sun path. Against the old three-cosine field this
        /// rendered as smooth continuous ribbons at every roughness down to 0.04, because a tight lobe traces
        /// iso-slope contours and a three-cosine field's contours are continuous lines. A real spectrum should
        /// break the same lobe into scattered points, which is what sun glitter is.
        /// </summary>
        [GpuFact]
        public void Ocean_sun_glitter_with_a_tight_lobe()
        {
            byte[] rgba = RenderOcean(eyeHeight: 12f, pitch: -0.09f, tune: w =>
            {
                w.GlintRoughness = 0.05f;        // far past the 0.22 default, the setting #299 proved was unreachable
                w.GlintDistantRoughness = 0.12f; // so the far-field widening ramp cannot hide the result either
                w.GlintStrength = 1.1f;
                w.FoamStrength = 0f;             // foam specks would be mistaken for glitter
            });
            Dump("water_glitter_tight.png", rgba);
        }

        /// <summary>
        /// A crude, reportable number for "how banded is this frame": the mean absolute luminance step between
        /// vertically adjacent rows, averaged over the lower two thirds of the image (the water). Parallel bands
        /// running across the view produce a large row-to-row swing; a smooth fresnel gradient produces a small one.
        /// It is printed rather than asserted on, because the bar for this work is a human looking at the PNG - a
        /// scalar like this can be gamed by anything that merely flattens the image, which is not the fix.
        /// </summary>
        public static float HorizontalStreakScore(byte[] rgba)
        {
            double total = 0;
            int rows = 0;
            for (int y = H / 3; y < H - 1; y++)
            {
                double a = 0, b = 0;
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4, j = ((y + 1) * W + x) * 4;
                    a += 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];
                    b += 0.299 * rgba[j] + 0.587 * rgba[j + 1] + 0.114 * rgba[j + 2];
                }
                total += Math.Abs(a - b) / W;
                rows++;
            }
            return rows > 0 ? (float)(total / rows) : 0f;
        }
    }
}
