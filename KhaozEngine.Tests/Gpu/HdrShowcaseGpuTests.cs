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
    /// Human-facing PNG dumps of the HDR pipeline (NOT goldens: no pixel lock, no "Golden" in the name). Each dumps a
    /// PNG into KE_PNG_DUMP_DIR (temp when unset) for eyeballing the filmic curve, the intensity roll-off, and the HDR
    /// vs legacy look, with a smoke assert only (content present, file written). Deterministic (EffectTimeSeconds 0,
    /// fixed seeds). Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    /// </summary>
    public sealed class HdrShowcaseGpuTests
    {
        const int W = 640, H = 360;

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

        static int BrightPixels(byte[] rgba, float lumaCut)
        {
            int n = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
                if (0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2] > lumaCut) n++;
            return n;
        }

        // Four emissive spheres at 1x / 2x / 4x / 8x, left to right, HDR + bloom. The filmic curve pulls the higher
        // intensities together at the top while bloom widens the hotter ones.
        static byte[] RenderIntensityLadder(TonemapOperator op)
        {
            MeshHandle sphere = default;
            return Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    sphere = s.LoadMesh(MeshPrimitives.Sphere(0.5f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Starfield = false;
                    s.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    s.Post.Hdr.Enabled = true;
                    s.Post.Hdr.Operator = op;
                    s.Post.Bloom.Enabled = true;
                    s.Post.Bloom.Threshold = 1.05f;
                    s.Post.Bloom.Intensity = 0.8f;
                    s.Camera.Frame(Vector3.Zero, new Vector3(9f, 2.6f, 3f));
                },
                drawFrame: s =>
                {
                    float[] levels = { 1f, 2f, 4f, 8f };
                    for (int i = 0; i < levels.Length; i++)
                    {
                        float e = levels[i];
                        s.Draw(sphere, Matrix4x4.CreateTranslation(-3f + 2f * i, 0f, 0f), Color.Black,
                            Material.Glowing(new Color(e, e, e, 1f)));
                    }
                },
                frames: 2);
        }

        [GpuFact]
        public void Showcase_hdr_intensity_ladder()
        {
            byte[] rgba = RenderIntensityLadder(TonemapOperator.AcesFilmic);
            Assert.True(BrightPixels(rgba, 60f) > 100, "expected the emissive ladder to produce bright content");
            Dump("hdr_intensity_ladder.png", rgba);
        }

        [GpuFact]
        public void Showcase_hdr_operators()
        {
            foreach (var (op, name) in new[]
            {
                (TonemapOperator.AcesFilmic, "hdr_operator_aces.png"),
                (TonemapOperator.Reinhard, "hdr_operator_reinhard.png"),
                (TonemapOperator.Clamp, "hdr_operator_clamp.png"),
            })
            {
                byte[] rgba = RenderIntensityLadder(op);
                Assert.True(BrightPixels(rgba, 60f) > 100, $"expected bright content under {op}");
                Dump(name, rgba);
            }
        }

        // A composed scene (sky + sun + water + an over-range emissive sphere + an over-range beam + an additive
        // particle burst), rendered once through the HDR chain and once through the legacy chain, so the two PNGs sit
        // side by side for a look comparison.
        static byte[] RenderComposedScene(bool hdr)
        {
            MeshHandle floor = default, sphere = default;
            return Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    floor = s.LoadMesh(MeshPrimitives.Tile(12f, 0.1f));
                    sphere = s.LoadMesh(MeshPrimitives.Sphere(0.7f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Starfield = false;
                    s.Post.Hdr.Enabled = hdr;
                    s.Post.Bloom.Enabled = true;
                    s.Post.Bloom.Threshold = 1.05f;
                    s.Post.Bloom.Intensity = 0.7f;
                    s.Post.Sky.Enabled = true;
                    s.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    s.Post.Sky.HorizonColor = new Color(0.66f, 0.72f, 0.80f, 1f);
                    s.Post.Sky.ZenithColor = new Color(0.20f, 0.40f, 0.72f, 1f);
                    s.Post.Sky.SunRadius = 0.09f;
                    s.Post.Sky.HaloStrength = 0.6f;
                    s.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
                    s.Post.Water.DeepColor = new Color(0.04f, 0.16f, 0.26f, 0.92f);
                    s.Post.Water.HorizonColor = new Color(0.60f, 0.70f, 0.80f, 0.75f);
                    s.Post.Water.GlintStrength = 0.9f;
                    s.Post.Water.GlintExponent = 100f;
                    s.Camera.Frame(new Vector3(0.1f, 0.6f, 0.2f), new Vector3(7f, 5f, 7f));
                },
                drawFrame: s =>
                {
                    s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.20f, 0.24f, 0.20f, 1f));
                    // Over-range emissive sphere above the water.
                    s.Draw(sphere, Matrix4x4.CreateTranslation(-1.5f, 1.5f, 0.5f), Color.Black,
                        Material.Glowing(new Color(4f, 2f, 0.8f, 1f)));
                    // Over-range beam skimming the surface.
                    s.DrawBeam(new Vector3(1.8f, 1.3f, -1.8f), new Vector3(1.8f, 1.3f, 1.8f), 0.3f,
                        new Color(1f, 0.5f, 0.2f, 1f),
                        BeamStyle.Default with { CoreColor = new Color(3.5f, 1f, 0.5f, 1f), Taper = 0.2f });
                    Span<ParticleSprite> burst = stackalloc ParticleSprite[5];
                    for (int i = 0; i < burst.Length; i++)
                        burst[i] = new ParticleSprite
                        {
                            Position = new Vector3(-0.5f + 0.3f * i, 1.4f + 0.1f * i, 1.0f - 0.15f * i),
                            Size = 0.28f,
                            Color = new Color(3f, 2.2f, 0.9f, 1f),
                            Shape = ParticleShape.Ember,
                            Seed = 0.17f * (i + 1),
                            Blend = BillboardBlend.Additive,
                        };
                    s.DrawParticles(burst);
                    s.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 1.0f, centerZ: 0f, halfExtentX: 5f));
                },
                frames: 2);
        }

        [GpuFact]
        public void Showcase_hdr_vs_ldr_scene()
        {
            byte[] hdr = RenderComposedScene(hdr: true);
            Assert.True(BrightPixels(hdr, 40f) > 200, "expected the HDR composed scene to render content");
            Dump("hdr_vs_ldr_hdr.png", hdr);

            byte[] ldr = RenderComposedScene(hdr: false);
            Assert.True(BrightPixels(ldr, 40f) > 200, "expected the legacy composed scene to render content");
            Dump("hdr_vs_ldr_legacy.png", ldr);
        }
    }
}
