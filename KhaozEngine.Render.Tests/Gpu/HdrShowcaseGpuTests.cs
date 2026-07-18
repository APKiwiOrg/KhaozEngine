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
    [Collection("HdrGpu")]   // serialise with HdrPipelineGpuTests: two concurrent Metal contexts crash the driver
    public sealed class HdrShowcaseGpuTests
    {
        const int W = 640, H = 360;

        static string DumpDir()
        {
            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Dump(string name, byte[] rgba) => Dump(name, rgba, W, H);

        static void Dump(string name, byte[] rgba, int w, int h)
        {
            string png = Path.Combine(DumpDir(), name);
            PngWriter.Save(png, rgba, w, h);
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
        static byte[] RenderComposedScene(bool hdr, float chroma = 0f)
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
                    s.Post.Hdr.Operator = TonemapOperator.AcesFilmic;
                    s.Post.Hdr.ChromaPreservation = chroma;
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

        // ---- ChromaPreservation look-evidence ladder: 0 / 0.5 / 0.75 / 0.9 / 1.0 (ACES operator), so a human can
        // judge where the desaturate-vs-hold-tint blend should ship as the default. Filenames chroma_<scene>_<factor>.png,
        // factor as 000/050/075/090/100. Permanent dump-only showcases, smoke asserts only, same convention as the
        // rest of this file.

        static readonly float[] ChromaFactors = { 0f, 0.5f, 0.75f, 0.9f, 1f };

        static string FactorLabel(float chroma) => ((int)MathF.Round(chroma * 100f)).ToString("000");

        // Concatenate same-size RGBA frames left to right into one wide buffer, for a single side-by-side strip PNG.
        static byte[] HStack(byte[][] frames, int w, int h)
        {
            int outW = w * frames.Length;
            var outPx = new byte[outW * h * 4];
            for (int f = 0; f < frames.Length; f++)
                for (int y = 0; y < h; y++)
                    Array.Copy(frames[f], y * w * 4, outPx, (y * outW + f * w) * 4, w * 4);
            return outPx;
        }

        [GpuFact]
        public void Showcase_chroma_ladder_composed_scene()
        {
            var frames = new byte[ChromaFactors.Length][];
            for (int i = 0; i < ChromaFactors.Length; i++)
            {
                float chroma = ChromaFactors[i];
                byte[] rgba = RenderComposedScene(hdr: true, chroma: chroma);
                Assert.True(BrightPixels(rgba, 40f) > 200, $"expected the composed scene to render content at chroma {chroma}");
                Dump($"chroma_composed_{FactorLabel(chroma)}.png", rgba);
                frames[i] = rgba;
            }
            Dump("chroma_composed_strip.png", HStack(frames, W, H), W * ChromaFactors.Length, H);
        }

        // A saturated amber core scaled 1x/2x/4x/8x, unlike the grayscale RenderIntensityLadder above. That ladder
        // is achromatic (R == G == B), where ChromaPreservation is a mathematical no-op: the per-channel and
        // hue-preserving paths agree exactly when every channel is equal. At 1x/2x here the core stays under the
        // tonemap knee and the factor barely moves the pixels. At 4x/8x the per-channel path bleaches the hot core
        // toward white while the hue-preserving path holds the amber tint, which is the visible ladder this knob
        // exists for.
        static byte[] RenderChromaIntensityLadder(float chroma)
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
                    s.Post.Hdr.Operator = TonemapOperator.AcesFilmic;
                    s.Post.Hdr.ChromaPreservation = chroma;
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
                            Material.Glowing(new Color(0.9f * e, 0.35f * e, 0.08f * e, 1f)));
                    }
                },
                frames: 2);
        }

        [GpuFact]
        public void Showcase_chroma_ladder_intensity()
        {
            // Per-factor dumps only, no combined strip: the composed-scene and additive-decal strips below already
            // carry the side-by-side judgement, this ladder rounds out the evidence set across all four scenes.
            foreach (float chroma in ChromaFactors)
            {
                byte[] rgba = RenderChromaIntensityLadder(chroma);
                Assert.True(BrightPixels(rgba, 60f) > 100, $"expected the colored emissive ladder to render content at chroma {chroma}");
                Dump($"chroma_intensity_{FactorLabel(chroma)}.png", rgba);
            }
        }

        // An additive HDR decal (a saturated over-range green) composited onto a mid-tone floor. Additive glow
        // decals stack directly onto the lit ground, so a per-channel roll-off bleaching the green toward white as
        // it clips is exactly the case ChromaPreservation exists to fix.
        static byte[] RenderAdditiveGlowScene(float chroma)
        {
            MeshHandle floor = default;
            return Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    floor = s.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Starfield = false;
                    s.Post.Hdr.Enabled = true;
                    s.Post.Hdr.Operator = TonemapOperator.AcesFilmic;
                    s.Post.Hdr.ChromaPreservation = chroma;
                    s.Camera.Frame(new Vector3(0f, 0.2f, 0f), new Vector3(6f, 4.5f, 6f));
                },
                drawFrame: s =>
                {
                    s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.45f, 0.45f, 0.48f, 1f));
                    s.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Circle,
                        Center = Vector3.Zero,
                        Size = new Vector4(2.2f, 0f, 0f, 0f),
                        FillColor = new Color(0.15f, 3.5f, 0.4f, 0.9f),
                        OutlineColor = new Color(0.4f, 5f, 0.6f, 1f),
                        EdgeThickness = 0.1f,
                        FillFraction = 1f,
                        Blend = DecalBlend.Additive,
                        YTolerance = 0.3f,
                        MaxStep = 0.4f,
                    });
                },
                frames: 2);
        }

        [GpuFact]
        public void Showcase_chroma_ladder_additive_decal()
        {
            var frames = new byte[ChromaFactors.Length][];
            for (int i = 0; i < ChromaFactors.Length; i++)
            {
                float chroma = ChromaFactors[i];
                byte[] rgba = RenderAdditiveGlowScene(chroma);
                Assert.True(BrightPixels(rgba, 40f) > 50, $"expected the additive decal glow to render content at chroma {chroma}");
                Dump($"chroma_decal_{FactorLabel(chroma)}.png", rgba);
                frames[i] = rgba;
            }
            Dump("chroma_decal_strip.png", HStack(frames, W, H), W * ChromaFactors.Length, H);
        }
    }
}
