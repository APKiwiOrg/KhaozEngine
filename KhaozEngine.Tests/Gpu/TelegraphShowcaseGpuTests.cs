using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Visual showcase of the modern ground-telegraph presets (feathered edges, noise fills, edge energy) on the
    /// real GPU: a 4x2 grid of ground circles, one per preset (Generic/Fire/Poison/Steel/Frost/Nature/Arcane) plus
    /// a fading residue mark in the last cell, dumped to PNG at two <see cref="Scene3D.EffectTimeSeconds"/> values
    /// so the noise/pattern animation is visible across the pair. Modeled on
    /// <see cref="GroundDecalBatchGpuTests"/> (scene setup / readback / PNG dump) and
    /// <see cref="GoldenSnapshotTests.Golden3D_GroundDecals"/> (ground-decal camera framing). This test does not
    /// itself lock pixels - it is a human-reviewed showcase. <see cref="GoldenSnapshotTests.Golden3D_GroundDecals"/>
    /// is the byte-exact net and is expected to stay green (the modern styling is zero-neutral by design). Gated
    /// on KE_GPU_TESTS.
    /// </summary>
    public sealed class TelegraphShowcaseGpuTests
    {
        const int W = 960, H = 540;
        const float Radius = 2.5f;
        const float Spacing = 6f;

        static byte[] Read(IGpuDevice gd, Texture2D tex) => GpuReadback.ToRgba(gd, tex.Handle, W, H);

        static Vector3 Cell(int index)
        {
            int col = index % 4, row = index / 4;
            return new Vector3((col - 1.5f) * Spacing, 0f, (row - 0.5f) * Spacing);
        }

        [GpuFact]
        public void Showcase_preset_grid_dumps_at_two_effect_times()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.Outline = true;
            // Grid spans x in [-11.5, 11.5], z in [-5.5, 5.5] (4 cols / 2 rows, spacing 6, radius 2.5). Frame a
            // generously padded bounds around it so all eight telegraphs fill the viewport.
            preview.Scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(26f, 1f, 14f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(28f, 0.1f));

            void DrawScene(Scene3D s)
            {
                // A dark neutral floor, not the default white: an additive-blend telegraph (Fire, Arcane) adds
                // colour on top of the background, and adding onto an already-saturated white floor clips right
                // back to white (invisible). Same "dark floor so additive reads" pattern as Golden3D_Bloom.
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.12f, 0.12f, 0.15f, 1f));

                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(Cell(0), Radius, 0.6f, TelegraphStyle.Generic));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(Cell(1), Radius, 0.6f, TelegraphStyle.Fire));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(Cell(2), Radius, 0.6f, TelegraphStyle.Poison));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(Cell(3), Radius, 0.6f, TelegraphStyle.Steel));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(Cell(4), Radius, 0.6f, TelegraphStyle.Frost));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(Cell(5), Radius, 0.6f, TelegraphStyle.Nature));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(Cell(6), Radius, 0.6f, TelegraphStyle.Arcane));
                // Eighth cell: a fading impact residue instead of a live telegraph (age 0.35, Fire style).
                s.DrawGroundDecal(GroundTelegraphs.BuildResidueCircle(Cell(7), Radius, 0.35f, TelegraphStyle.Fire));
            }

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            preview.Scene.EffectTimeSeconds = 0f;
            byte[] t0 = Read(gd, preview.Capture(DrawScene));
            AssertNonBackgroundPixels(t0, "t0");
            string png0 = Path.Combine(dir, "telegraph_showcase_t0.png");
            PngWriter.Save(png0, t0, W, H);
            Assert.True(new FileInfo(png0).Length > 0, $"expected a PNG dump at {png0}");

            preview.Scene.EffectTimeSeconds = 1.7f;
            byte[] t1 = Read(gd, preview.Capture(DrawScene));
            AssertNonBackgroundPixels(t1, "t1");
            string png1 = Path.Combine(dir, "telegraph_showcase_t1.png");
            PngWriter.Save(png1, t1, W, H);
            Assert.True(new FileInfo(png1).Length > 0, $"expected a PNG dump at {png1}");

            // The per-frame alpha smoke check passes on the opaque floor alone, so it cannot tell a decal no-op
            // from a working render. The time-animated noise fills guarantee the two effect times paint different
            // pixels, which only the decals can cause on this otherwise static scene.
            long changedPixels = 0;
            for (int i = 0; i + 3 < t0.Length; i += 4)
                if (t0[i] != t1[i] || t0[i + 1] != t1[i + 1] || t0[i + 2] != t1[i + 2] || t0[i + 3] != t1[i + 3])
                    changedPixels++;
            Assert.True(changedPixels > 0, "expected the t0 and t1 frames to differ (animated telegraph fills)");
        }

        /// <summary>
        /// Progress sweep: four presets (Generic, Fire, Frost, Arcane) at cast progress 0.15 / 0.45 / 0.75, one
        /// row per preset, dumped to telegraph_sweep.png. The early-progress column is the regression eyeball for
        /// the "bright ball at the shape center" failure mode: at low progress the swept region is tiny and any
        /// glow band wider than it floods the middle. Human-reviewed like the grid showcase above.
        /// </summary>
        [GpuFact]
        public void Showcase_progress_sweep_dumps()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.Outline = true;
            // 3 columns x 4 rows of radius-2.5 circles at spacing 6: x in [-6, 6], z in [-9, 9].
            preview.Scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(17f, 1f, 21f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(26f, 0.1f));

            // Borderless variants (FillMode.Fill): the no-outline look consumers like Ruinborne ship. The base
            // fill carries the extent, the sweep brightens across it, no outline band at all.
            static TelegraphStyle Borderless(TelegraphStyle s)
            {
                s.FillMode = FillMode.Fill;
                return s;
            }
            TelegraphStyle[] rows = { Borderless(TelegraphStyle.Generic), Borderless(TelegraphStyle.Fire),
                Borderless(TelegraphStyle.Frost), Borderless(TelegraphStyle.Arcane) };
            float[] cols = { 0.15f, 0.45f, 0.75f };

            void DrawScene(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.12f, 0.12f, 0.15f, 1f));
                for (int r = 0; r < rows.Length; r++)
                    for (int c = 0; c < cols.Length; c++)
                    {
                        var at = new Vector3((c - 1f) * Spacing, 0f, (r - 1.5f) * Spacing);
                        s.DrawGroundDecal(GroundTelegraphs.BuildCircle(at, Radius, cols[c], rows[r]));
                    }
            }

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            preview.Scene.EffectTimeSeconds = 1.3f;
            byte[] px = Read(gd, preview.Capture(DrawScene));
            AssertNonBackgroundPixels(px, "sweep");
            string png = Path.Combine(dir, "telegraph_sweep.png");
            PngWriter.Save(png, px, W, H);
            Assert.True(new FileInfo(png).Length > 0, $"expected a PNG dump at {png}");
        }

        /// <summary>
        /// Thin crisp static ring: the world-unit edge/feather overrides next to the derived default
        /// for contrast. Left column: derived auto-scaling edge at radius 5 (the fat soft band the
        /// overrides exist to fix). Right column: the range-ring recipe (FillMode.Outline, no
        /// animation, EdgeWidthWorld 0.04 / FeatherWidthWorld 0.02), which must read as a thin crisp
        /// line. Top row uses the Circle shape, bottom row the Ring shape (thin annulus band). Dumped
        /// to telegraph_thin_ring.png, human-reviewed like the showcases above. The pixel-count
        /// assertion is the behavioral net: the crisp right column must paint far fewer decal pixels
        /// than the fat left column.
        /// </summary>
        [GpuFact]
        public void Showcase_thin_static_ring_dumps()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Post.Outline = true;
            preview.Scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(25f, 1f, 22f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(30f, 0.1f));

            // The static range-ring recipe from docs/USING-KHAOZENGINE.md: stroke only, no animation,
            // solid pattern, feather and edge pinned in world units (0 = keep the derived behavior).
            static TelegraphStyle RangeRing(float edgeWorld, float featherWorld)
            {
                var s = TelegraphStyle.Generic;
                s.FillMode = FillMode.Outline;
                s.Animation = TelegraphAnim.None;
                s.Pattern = TelegraphFillPattern.Solid;
                if (edgeWorld > 0f) s.FeatherWidth = 0f;
                s.EdgeWidthWorld = edgeWorld;
                s.FeatherWidthWorld = featherWorld;
                return s;
            }

            const float R = 5f;
            void DrawScene(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.12f, 0.12f, 0.15f, 1f));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(new Vector3(-5.75f, 0f, -5.25f), R, 1f, RangeRing(0f, 0f)));
                s.DrawGroundDecal(GroundTelegraphs.BuildCircle(new Vector3(5.75f, 0f, -5.25f), R, 1f, RangeRing(0.04f, 0.02f)));
                s.DrawGroundDecal(GroundTelegraphs.BuildRing(new Vector3(-5.75f, 0f, 5.25f), R - 0.3f, R, 1f, RangeRing(0f, 0f)));
                s.DrawGroundDecal(GroundTelegraphs.BuildRing(new Vector3(5.75f, 0f, 5.25f), R - 0.3f, R, 1f, RangeRing(0.04f, 0.02f)));
            }

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            preview.Scene.EffectTimeSeconds = 0f;
            byte[] px = Read(gd, preview.Capture(DrawScene));
            AssertNonBackgroundPixels(px, "thin_ring");
            string png = Path.Combine(dir, "telegraph_thin_ring.png");
            PngWriter.Save(png, px, W, H);
            Assert.True(new FileInfo(png).Length > 0, $"expected a PNG dump at {png}");

            // Behavioral net: count pixels that differ from the flat floor color in each half. The
            // scene is left/right symmetric except for the stroke widths, so the crisp right column
            // must paint well under half the decal pixels of the fat left column.
            long left = 0, right = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    if (px[i + 3] == 0) continue;                       // background
                    // Floor material is (0.12, 0.12, 0.15) but the post stack (lighting, outline,
                    // tone mapping) renders it as (44, 44, 63): measured from an actual dump, not the
                    // raw material value. Treat anything clearly off that rendered color as decal.
                    bool decal = Math.Abs(px[i] - 44) > 25 || Math.Abs(px[i + 1] - 44) > 25 || Math.Abs(px[i + 2] - 63) > 25;
                    if (!decal) continue;
                    if (x < W / 2) left++; else right++;
                }
            Assert.True(right > 0, "expected the crisp override rings to paint some pixels");
            Assert.True(right < left / 2,
                $"expected the world-unit override rings (right, {right}px) to be far thinner than the derived default (left, {left}px)");
        }

        // Smoke assertion only: the preview composites with a transparent background, so any covered pixel
        // (the ground tile plus every decal) has nonzero alpha. This just proves something actually rendered.
        // The PNGs dumped above are the real, human-reviewed check.
        static void AssertNonBackgroundPixels(byte[] rgba, string label)
        {
            long nonBackground = 0;
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] > 0) nonBackground++;
            Assert.True(nonBackground > 0, $"expected non-background pixels in {label}");
        }
    }
}
