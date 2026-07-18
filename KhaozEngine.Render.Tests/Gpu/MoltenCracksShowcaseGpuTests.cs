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
    /// Visual showcase of <see cref="DecalFillPattern.MoltenCracks"/> and <see cref="GroundDecal.EdgeErosion"/>
    /// on the real GPU: a 4x2 grid pairing the molten variants (plain, eroded, the Ruinborne sunder-beam recipe,
    /// wide cracks) with an erosion sweep over a solid circle, dumped to PNG at two effect times so the molten
    /// breathing is visible across the pair. This test does not lock pixels beyond its A/B guards - it is a
    /// human-reviewed showcase, the demo scene issue #228 asks for.
    /// <see cref="GoldenSnapshotTests.Golden3D_GroundDecals"/> stays the byte-exact net for non-opting decals
    /// (both features are zero-neutral by design). Gated on KE_GPU_TESTS. Dumps land in KE_PNG_DUMP_DIR
    /// (default: the temp dir).
    /// </summary>
    public sealed class MoltenCracksShowcaseGpuTests
    {
        const int W = 960, H = 540;
        const float Radius = 2.5f;
        const float Spacing = 6f;

        static readonly Color Scorch = new(0.05f, 0.03f, 0.03f, 0.95f);   // near-black, near-opaque dark field
        static readonly Color Lava = new(1f, 0.45f, 0.1f, 0.9f);          // the hot accent the cracks glow with

        static byte[] Read(IGpuDevice gd, Texture2D tex) => GpuReadback.ToRgba(gd, tex.Handle, W, H);

        static Vector3 Cell(int index)
        {
            int col = index % 4, row = index / 4;
            return new Vector3((col - 1.5f) * Spacing, 0f, (row - 0.5f) * Spacing);
        }

        static GroundDecal Molten(Vector3 center, float erosion = 0f, float patternParam = 0f, float flashAdd = 0f) => new()
        {
            Shape = DecalShape.Circle, Center = center, Size = new Vector4(Radius, 0f, 0f, 0f),
            FillColor = Scorch, AccentColor = Lava, OutlineColor = default,
            EdgeThickness = 0.08f, FillFraction = 1f, Blend = DecalBlend.Alpha,
            YTolerance = 0.3f, MaxStep = 0.4f, FeatherWidth = 0.15f,
            Pattern = DecalFillPattern.MoltenCracks, PatternSpeed = 0.25f, PatternScale = 1.2f,
            PatternParam = patternParam, EdgeErosion = erosion, FlashAdd = flashAdd,
        };

        static GroundDecal SolidEroded(Vector3 center, float erosion) => new()
        {
            Shape = DecalShape.Circle, Center = center, Size = new Vector4(Radius, 0f, 0f, 0f),
            FillColor = new Color(0.55f, 0.55f, 0.6f, 1f), OutlineColor = default,
            EdgeThickness = 0.08f, FillFraction = 1f, Blend = DecalBlend.Alpha,
            YTolerance = 0.3f, MaxStep = 0.4f, EdgeErosion = erosion,
        };

        static void DrawGrid(Scene3D s, MeshHandle floor)
        {
            // Dark neutral floor, the same "so the pattern reads" staging as TelegraphShowcaseGpuTests.
            s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.12f, 0.12f, 0.15f, 1f));

            // Top row: the molten variants. Plain, eroded, the Ruinborne sunder recipe (a beam, the issue's
            // consumer preview: PatternScale 1.2, EdgeErosion 0.6, FlashAdd riding a damage tick), wide cracks.
            s.DrawGroundDecal(Molten(Cell(0)));
            s.DrawGroundDecal(Molten(Cell(1), erosion: 0.6f));
            var beam = Molten(Cell(2), erosion: 0.6f, flashAdd: 0.15f);
            beam.Shape = DecalShape.Beam;
            beam.Center -= new Vector3(2.2f, 0f, 0f);                    // beam origin sits at one end: re-centre it
            beam.Size = new Vector4(2.2f, 1.1f, 0f, 0f);
            s.DrawGroundDecal(beam);
            s.DrawGroundDecal(Molten(Cell(3), patternParam: 0.45f));

            // Bottom row: the erosion sweep on a plain solid circle - the "all shapes and patterns" half of the
            // feature, isolated from the molten look. 0 is the analytic control.
            s.DrawGroundDecal(SolidEroded(Cell(4), 0f));
            s.DrawGroundDecal(SolidEroded(Cell(5), 0.3f));
            s.DrawGroundDecal(SolidEroded(Cell(6), 0.6f));
            s.DrawGroundDecal(SolidEroded(Cell(7), 0.9f));
        }

        static string Dump(string name, byte[] rgba)
        {
            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".png");
            PngWriter.Save(path, rgba, W, H);
            Assert.True(new FileInfo(path).Length > 0, $"expected a PNG dump at {path}");
            return path;
        }

        static long DiffPixels(byte[] a, byte[] b)
        {
            long n = 0;
            for (int i = 0; i + 3 < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) n++;
            return n;
        }

        [GpuFact]
        public void Showcase_molten_grid_dumps_at_two_effect_times_and_reduced_quality()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            // Grid spans x in [-11.5, 11.5], z in [-5.5, 5.5], the same generous framing as the telegraph showcase.
            preview.Scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(26f, 1f, 14f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(28f, 0.1f));

            void DrawScene(Scene3D s) => DrawGrid(s, floor);

            preview.Scene.EffectTimeSeconds = 0f;
            byte[] t0 = Read(gd, preview.Capture(DrawScene));
            Dump("decal_molten_t0", t0);

            preview.Scene.EffectTimeSeconds = 1.7f;
            byte[] t1 = Read(gd, preview.Capture(DrawScene));
            Dump("decal_molten_t1", t1);

            // The molten breathing (feature-point drift + per-cell pulse) must actually animate: the two effect
            // times paint different pixels, which only the decals can cause on this otherwise static scene.
            Assert.True(DiffPixels(t0, t1) > 0, "expected the t0 and t1 frames to differ (molten breathing)");

            // Reduced quality swaps the exact two-pass Voronoi border distance for the single-pass F2-F1
            // approximation. The dump is the eyeball. The diff proves the cheap path is actually selected.
            preview.Scene.EffectTimeSeconds = 0f;
            preview.Scene.DecalQuality = GroundDecalQuality.Reduced;
            byte[] reduced = Read(gd, preview.Capture(DrawScene));
            Dump("decal_molten_reduced", reduced);
            Assert.True(DiffPixels(t0, reduced) > 0, "Reduced quality must take the cheaper Voronoi neighbourhood");
        }

        [GpuFact]
        public void Erosion_bites_inward_and_is_stable_frame_to_frame()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            preview.Scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(8f, 1f, 8f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));

            byte[] RenderCircle(float erosion, float time)
            {
                preview.Scene.EffectTimeSeconds = time;
                return Read(gd, preview.Capture(s =>
                {
                    s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.12f, 0.12f, 0.15f, 1f));
                    s.DrawGroundDecal(SolidEroded(Vector3.Zero, erosion));
                }));
            }

            // Fill pixels: the solid decal's grey reads well above the dark floor in every channel.
            static long FillPixels(byte[] rgba)
            {
                long n = 0;
                for (int i = 0; i + 3 < rgba.Length; i += 4)
                    if (rgba[i] > 90 && rgba[i + 1] > 90 && rgba[i + 2] > 100) n++;
                return n;
            }

            byte[] control = RenderCircle(0f, 0f);
            byte[] eroded = RenderCircle(0.6f, 0f);
            long controlFill = FillPixels(control), erodedFill = FillPixels(eroded);
            Assert.True(controlFill > 0, "the control circle must render");
            // Inward-only is a hard contract: the CPU footprint quad is sized to the ANALYTIC bounds, so any
            // outward growth would clip at the quad edge. Fewer filled pixels proves the bite goes inward.
            Assert.True(erodedFill < controlFill,
                $"erosion must bite inward (control {controlFill}, eroded {erodedFill} fill pixels)");

            // Stability: the erosion field carries no time term, so a different effect time must not move the
            // silhouette. (The decal is Solid-patterned, so nothing else in it animates either.)
            byte[] erodedLater = RenderCircle(0.6f, 3.2f);
            Assert.Equal(0, DiffPixels(eroded, erodedLater));
        }
    }
}
