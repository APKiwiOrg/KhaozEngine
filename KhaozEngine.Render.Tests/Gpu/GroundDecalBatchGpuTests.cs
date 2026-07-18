using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof of the batched, footprint-bounded ground-decal path. Renders many overlapping decals (mixed shapes
    /// and blends) through the batched instanced pass, then re-renders the exact same decals with the renderer forced
    /// to full-viewport quads (ForceFullscreenQuads, the pre-bounding coverage). The two must be pixel-identical -
    /// bounding the rasterized area only skips pixels the fullscreen path discarded - which proves the footprint
    /// bounding is presentation-neutral. Also proves the per-instance attributes carry each decal's own params (the
    /// composite shows every distinct colour). A PNG of the batched render is dumped for the orchestrator. The
    /// pixel-exact match against the PRE-change baseline is additionally locked by the untouched telegraph_ground and
    /// scene3d_shadow_blob goldens. Gated on KE_GPU_TESTS.
    /// </summary>
    public sealed class GroundDecalBatchGpuTests
    {
        const int W = 480, H = 320;

        static void QueueOverlappingDecals(Scene3D s)
        {
            // A cluster of overlapping decals, mixed shapes and blends interleaved in submission order, so the batch
            // splits at blend boundaries and overlapping alpha decals composite in the queued order.
            s.DrawGroundDecal(new GroundDecal
            {
                Shape = DecalShape.Circle, Center = new Vector3(-0.6f, 0f, 0.3f), Size = new Vector4(1.7f, 0, 0, 0),
                FillColor = new Color(0.95f, 0.12f, 0.06f, 0.7f), OutlineColor = new Color(1f, 0.8f, 0.2f, 0.9f),
                EdgeThickness = 0.08f, FillFraction = 1f, Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
            });
            s.DrawGroundDecal(new GroundDecal
            {
                Shape = DecalShape.Ring, Center = new Vector3(0.5f, 0f, -0.2f), Size = new Vector4(0.6f, 1.5f, 0, 0),
                FillColor = new Color(0.1f, 0.8f, 0.95f, 0.6f), OutlineColor = new Color(0.7f, 1f, 1f, 0.9f),
                EdgeThickness = 0.08f, FillFraction = 1f, Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
            });
            // An additive glow between two alpha decals: forces a blend-run split in the middle of the submission order.
            s.DrawGroundDecal(new GroundDecal
            {
                Shape = DecalShape.Circle, Center = new Vector3(0.1f, 0f, 0.1f), Size = new Vector4(1.1f, 0, 0, 0),
                FillColor = new Color(0.2f, 0.9f, 0.3f, 0.5f), OutlineColor = new Color(0.4f, 1f, 0.5f, 0.7f),
                EdgeThickness = 0.06f, FillFraction = 1f, Blend = DecalBlend.Additive, YTolerance = 0.3f, MaxStep = 0.4f,
            });
            s.DrawGroundDecal(new GroundDecal
            {
                Shape = DecalShape.Cone, Center = new Vector3(-0.2f, 0f, -0.9f), Rotation = 0.4f,
                Size = new Vector4(2.4f, 0.6f, 0, 0),
                FillColor = new Color(0.9f, 0.5f, 0.1f, 0.6f), OutlineColor = new Color(1f, 0.85f, 0.3f, 0.9f),
                EdgeThickness = 0.08f, FillFraction = 1f, Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
            });
        }

        static byte[] Read(IGpuDevice gd, Texture2D tex) => GpuReadback.ToRgba(gd, tex.Handle, W, H);

        static long Diff(byte[] a, byte[] b)
        {
            long d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
            return d;
        }

        [GpuFact]
        public void Batched_footprint_render_matches_fullscreen_coverage_and_carries_per_instance_params()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.Outline = true;
            // Pin the legacy (non-HDR) post chain. This test proves the decal BATCH path: footprint bounding is
            // pixel-neutral and the per-instance attributes carry each decal's own colour (the additive green run,
            // split mid-order between two alpha runs, is the canary). Both are independent of the post tonemap. The
            // ACES tonemap (HDR is on by default since 906c1df3, which landed a day after this test) desaturates this
            // near-white lit-floor scene wholesale, dropping the additive green's colour-dominance below the check
            // below - the batch itself is correct (LDR renders all three decals distinctly). Render LDR so the
            // assertions measure the decal path, not the tonemap curve (which has its own goldens).
            preview.Scene.Post.Hdr.Enabled = false;
            preview.Scene.Camera.Frame(new Vector3(0f, 0.2f, 0f), new Vector3(6f, 4.5f, 6f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(8f, 0.1f));

            void DrawScene(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                QueueOverlappingDecals(s);
            }

            // Batched + footprint-bounded (production path).
            preview.Scene.DecalRenderer.ForceFullscreenQuads = false;
            byte[] bounded = Read(gd, preview.Capture(DrawScene));

            // Same decals, forced to full-viewport quads (the pre-bounding coverage). Must be pixel-identical.
            preview.Scene.DecalRenderer.ForceFullscreenQuads = true;
            byte[] fullscreen = Read(gd, preview.Capture(DrawScene));

            Assert.Equal(0, Diff(bounded, fullscreen));   // footprint bounding must not change a single pixel

            // The composite must carry each decal's own colour (the batched per-instance attributes work): count red,
            // cyan, and orange-ish presence. A single-colour smear (the old shared-slot failure mode) fails this.
            bool red = false, cyan = false, green = false;
            for (int i = 0; i + 3 < bounded.Length; i += 4)
            {
                int r = bounded[i], g = bounded[i + 1], b = bounded[i + 2];
                if (r > 130 && r - g > 55 && r - b > 55) red = true;
                if (g > 120 && b > 120 && g - r > 45 && b - r > 45) cyan = true;
                if (g > 130 && g - r > 40 && g - b > 40) green = true;
            }
            Assert.True(red, "red circle decal missing from the batched composite");
            Assert.True(cyan, "cyan ring decal missing from the batched composite");
            Assert.True(green, "additive green glow missing from the batched composite");

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, "decal_batch_bounded.png");
            PngWriter.Save(png, bounded, W, H);
            Assert.True(new FileInfo(png).Length > 0, $"expected a PNG dump at {png}");
        }
    }
}
