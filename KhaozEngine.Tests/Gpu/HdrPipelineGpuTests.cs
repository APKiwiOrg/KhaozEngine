using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Behavioural GPU proofs of the HDR pipeline (float16 colour chain + pre-tonemap bloom + ACES tonemap) that a
    /// coarse RGB golden grid cannot express: over-range emissive stays separable through the filmic curve, bloom
    /// extracts only over-range highlights, the HDR toggle/rebuild path leaks no state, the float16 MSAA resolve
    /// works, and the background alpha marker survives the tonemap pass. All scenes are deterministic
    /// (EffectTimeSeconds 0, fixed camera and transforms). Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    /// </summary>
    public sealed class HdrPipelineGpuTests
    {
        const int W = 128, H = 128;

        static float Luma(byte r, byte g, byte b) => 0.299f * r + 0.587f * g + 0.114f * b;

        // Average luma of a small block at the image centre. A centred emissive sphere (framed to fill) covers it, so
        // this reads the tonemapped core brightness without depending on the exact silhouette.
        static float CenterLuma(byte[] rgba)
        {
            double sum = 0;
            int n = 0;
            for (int y = H / 2 - 3; y <= H / 2 + 3; y++)
                for (int x = W / 2 - 3; x <= W / 2 + 3; x++)
                {
                    int i = (y * W + x) * 4;
                    sum += Luma(rgba[i], rgba[i + 1], rgba[i + 2]);
                    n++;
                }
            return (float)(sum / n);
        }

        // Render a single centred, pure-emissive sphere (tint black zeroes albedo, so the pixel value IS the emissive
        // driven through the tonemap) at the given emissive level and operator, HDR on and bloom off.
        static byte[] RenderEmissiveSphere(float emissive, TonemapOperator op)
        {
            MeshHandle sphere = default;
            return Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    sphere = s.LoadMesh(MeshPrimitives.Sphere(0.5f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Starfield = false;
                    s.Post.TransparentBackground = false;
                    s.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    s.Post.Bloom.Enabled = false;
                    s.Post.Hdr.Enabled = true;
                    s.Post.Hdr.Operator = op;
                    s.Camera.Frame(Vector3.Zero, new Vector3(1.4f, 1.4f, 1.4f));
                },
                drawFrame: s => s.Draw(sphere, Matrix4x4.Identity, Color.Black,
                    Material.Glowing(new Color(emissive, emissive, emissive, 1f))),
                frames: 2);
        }

        [GpuFact]
        public void Hdr_emissive_above_one_brightens_through_tonemap()
        {
            // ACES: a 6x emissive core stays strictly brighter than a 1x core (roughly 0.8 vs 0.99 mapped). If the
            // emissive authoring surface clamped to 1.0, or the tonemap were absent, these would read equal.
            float aces1 = CenterLuma(RenderEmissiveSphere(1f, TonemapOperator.AcesFilmic));
            float aces6 = CenterLuma(RenderEmissiveSphere(6f, TonemapOperator.AcesFilmic));
            Assert.True(aces6 > aces1 + 10f,
                $"ACES should keep the 6x core brighter than the 1x core (1x={aces1}, 6x={aces6})");

            // Clamp: both saturate to white, proving the separation above is the filmic curve at work, not raw value.
            float clamp1 = CenterLuma(RenderEmissiveSphere(1f, TonemapOperator.Clamp));
            float clamp6 = CenterLuma(RenderEmissiveSphere(6f, TonemapOperator.Clamp));
            Assert.True(clamp1 > 250f && clamp6 > 250f,
                $"Clamp should saturate both cores to white (1x={clamp1}, 6x={clamp6})");
            Assert.True(MathF.Abs(clamp6 - clamp1) <= 3f,
                $"Clamp should not separate the two cores (1x={clamp1}, 6x={clamp6})");
        }

        // Count background pixels (the a=0 marker still set, so TransparentBackground preserves it) that carry a
        // non-trivial colour: a bloom halo landing on the background around an over-range core lights these up, while a
        // sub-threshold core leaves the background dark.
        static int BackgroundHaloPixels(float emissive)
        {
            MeshHandle sphere = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    sphere = s.LoadMesh(MeshPrimitives.Sphere(0.5f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Starfield = false;
                    s.Post.TransparentBackground = true;   // keep the a=0 marker so a halo on background is detectable
                    s.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    s.Post.Hdr.Enabled = true;
                    s.Post.Bloom.Enabled = true;
                    s.Post.Bloom.Threshold = 1.05f;
                    s.Post.Bloom.Knee = 0f;                // hard cutoff at 1.05 so 0.9 is cleanly excluded
                    s.Post.Bloom.Intensity = 1f;
                    s.Post.Bloom.Radius = 6;
                    s.Camera.Frame(Vector3.Zero, new Vector3(3f, 3f, 3f));   // modest sphere, background all around
                },
                drawFrame: s => s.Draw(sphere, Matrix4x4.Identity, Color.Black,
                    Material.Glowing(new Color(emissive, emissive, emissive, 1f))),
                frames: 2);
            int count = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
                if (rgba[i + 3] < 128 && Luma(rgba[i], rgba[i + 1], rgba[i + 2]) > 20f) count++;
            return count;
        }

        [GpuFact]
        public void Hdr_bloom_extracts_over_range_only()
        {
            int dim = BackgroundHaloPixels(0.9f);   // luma 0.9 < threshold 1.05: no bloom
            int hot = BackgroundHaloPixels(5.0f);   // luma 5.0 > threshold: over-range core blooms
            Assert.True(hot > 30, $"over-range core should bloom onto the background (halo px={hot})");
            Assert.True(dim < 10, $"sub-threshold core should not bloom (halo px={dim})");
            Assert.True(hot > dim * 3, $"the hot core should halo far more than the dim one (hot={hot}, dim={dim})");
        }

        [GpuFact]
        public void Hdr_off_toggle_roundtrip_is_stable()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.TransparentBackground = false;
            preview.Scene.Post.Starfield = false;
            preview.Scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.06f, 1f);
            preview.Scene.EffectTimeSeconds = 0f;
            preview.Scene.Camera.Frame(Vector3.Zero, new Vector3(2.4f, 2.4f, 2.4f));
            MeshHandle cube = preview.Scene.LoadMesh(MeshPrimitives.Box(0.8f));
            MeshHandle sphere = preview.Scene.LoadMesh(MeshPrimitives.Sphere(0.4f));

            void DrawScene(Scene3D s)
            {
                s.Draw(cube, Matrix4x4.CreateTranslation(-0.6f, 0f, 0f));
                s.Draw(sphere, Matrix4x4.CreateTranslation(0.7f, 0f, 0f), Color.White,
                    Material.Glowing(new Color(2f, 1.5f, 0.5f, 1f)));
            }

            // Frame A: legacy chain (HDR off).
            preview.Scene.Post.Hdr.Enabled = false;
            preview.Capture(DrawScene);
            byte[] a = preview.ReadbackRgba();

            // Toggle HDR on and render (drives the target + pipeline rebuild), then back to legacy.
            preview.Scene.Post.Hdr.Enabled = true;
            preview.Capture(DrawScene);
            _ = preview.ReadbackRgba();

            // Frame B: legacy chain again. Byte-identical to A proves the toggle/rebuild path leaks no state.
            preview.Scene.Post.Hdr.Enabled = false;
            preview.Capture(DrawScene);
            byte[] b = preview.ReadbackRgba();

            Assert.Equal(a.Length, b.Length);
            long diff = 0;
            for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
            Assert.Equal(0, diff);
        }

        [GpuFact]
        public void Hdr_msaa4_float16_resolves()
        {
            MeshHandle cube = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    cube = s.LoadMesh(MeshPrimitives.Box(0.8f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Starfield = false;
                    s.Post.TransparentBackground = false;
                    s.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.06f, 1f);
                    s.Post.Hdr.Enabled = true;
                    s.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
                    s.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));
                },
                drawFrame: s => s.Draw(cube, Matrix4x4.CreateRotationY(0.6f)),
                frames: 2);

            // A lit cube on a dark background: the float16 MSAA target must resolve to foreground pixels (and not throw).
            int lit = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
                if (Luma(rgba[i], rgba[i + 1], rgba[i + 2]) > 40f) lit++;
            Assert.True(lit > 100, $"HDR + MSAA4 lit cube should produce foreground pixels (lit px={lit})");
        }

        [GpuFact]
        public void Hdr_alpha_marker_survives_tonemap()
        {
            MeshHandle cube = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    cube = s.LoadMesh(MeshPrimitives.Box(0.3f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Hdr.Enabled = true;
                    s.Post.Starfield = true;
                    s.Post.TransparentBackground = false;
                    s.Post.BackgroundColor = new Color(0.01f, 0.01f, 0.02f, 1f);
                    s.Post.Bloom.Enabled = false;
                    s.Camera.Frame(Vector3.Zero, new Vector3(3.5f, 3.5f, 3.5f));   // small cube centre, starfield around
                },
                drawFrame: s => s.Draw(cube, Matrix4x4.Identity),
                frames: 2);

            // The blit injects starfield only where the background alpha marker is set (a < 0.5). Those bright points
            // showing up means the marker survived the tonemap pass. Count bright pixels OUTSIDE a central box bounding
            // the small cube, so only stars can be there.
            int bx0 = W * 35 / 100, bx1 = W * 65 / 100, by0 = H * 35 / 100, by1 = H * 65 / 100;
            int stars = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (x >= bx0 && x < bx1 && y >= by0 && y < by1) continue;
                    int i = (y * W + x) * 4;
                    if (Luma(rgba[i], rgba[i + 1], rgba[i + 2]) > 120f) stars++;
                }
            Assert.True(stars > 5, $"starfield should inject bright background pixels through the tonemap (stars={stars})");
        }
    }
}
