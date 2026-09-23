using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/381">#381</see> as a measurement: under
    /// <see cref="WaterGridMode.Clipmap"/> with the procedural swell, a coarse outer ring must not shade as flat
    /// triangle facets. The scene is the Ruinborne lake from the field report (a 120 m body, the scene-wide clipmap at
    /// its defaults, the sea's 42 m swell inherited), seen straight down from far above its south-west corner, so the
    /// clipmap centres on that corner and the 8 m and 16 m rings fill the frame.
    /// <para>
    /// <b>The signature.</b> A normal interpolated across a triangle is LINEAR inside it, so the shading it drives is
    /// smooth inside each triangle and kinks where two triangles meet: the luminance gradient jumps across every edge
    /// and is continuous across any line through a triangle's interior. A normal evaluated per pixel has no edges to
    /// jump at. So the measure is the mean jump in the horizontal luminance gradient across the triangles' LEFT edges,
    /// over the same jump across each quad's midline, both taken at the displaced vertex positions the CPU mirror
    /// (<see cref="GerstnerWaves"/>) says the ring was drawn with. About 1 means no facets.
    /// </para>
    /// <para>
    /// <b>Why straight down, and why it can be exact.</b> Under an orthographic camera looking straight down, the
    /// eye vector is constant, the pixel footprint is constant (a fragment's planar position IS its pixel), and a deep
    /// seabed makes the body colour constant, so the glint is the only term left and it reads the normal alone. The
    /// eye sits 5 km up so the reflected view direction barely moves across the frame.
    /// </para>
    /// <para>
    /// <b>The control proves the instrument.</b> A second render swaps the glint for a shallow seabed and a steep
    /// absorption ramp. The body colour then follows the rendered surface HEIGHT, which is interpolated per vertex by
    /// construction and so genuinely kinks at every edge. The same measure must read high there, which is what shows
    /// the edge mask lines up with the triangles the GPU actually drew and that the metric can see a facet at all.
    /// </para>
    /// <para>
    /// Measured on Metal: with the normal interpolated from the vertices the subject read 7.3 on the 8 m ring and
    /// 6.2 on the 16 m ring, and evaluated per pixel it reads 0.9 and 1.1. The control reads 3.7 and 3.4 either way.
    /// </para>
    /// </summary>
    [Collection("HdrGpu")]
    public sealed class WaterSwellFacetGpuTests
    {
        readonly ITestOutputHelper _out;

        public WaterSwellFacetGpuTests(ITestOutputHelper output) => _out = output;

        const int Size = 720;
        const float ViewMin = -54f, ViewMax = 126f;
        const float LakeHalfExtent = 120f;
        const float EyeX = -110f, EyeZ = -110f, EyeHeight = 5000f;
        const float FrozenTime = 3.7f;
        /// <summary>Pixels either side over which each one-sided gradient is taken.</summary>
        const int Probe = 4;

        /// <summary>The Ruinborne 0.16.2 lake: the scene-wide clipmap at its defaults and the sea's swell, which the
        /// lake inherited before the game muted it.</summary>
        static void ApplyLake(WaterSettings w)
        {
            w.WaveSource = WaterWaveSource.Procedural;
            w.GridMode = WaterGridMode.Clipmap;
            w.ClipmapCellSize = 0.5f;
            w.ClipmapRingCells = 32;
            w.ClipmapLevels = 0;
            w.ClipmapGeomorphBand = 0.5f;
            w.SwellAmplitude = 0.35f;
            w.SwellWavelength = 42f;
            w.SwellDirectionDegrees = 125f;
            w.SwellSpreadDegrees = 55f;
            w.SwellSteepness = 0.6f;
            w.SwellSpeed = 0.6f;
            w.SwellSeed = 0f;
            w.SwellComponents = 4;
            // Everything that is not the swell normal is held still: no ripple, no foam, no reflection.
            w.NormalStrength = 0f;
            w.FoamStrength = 0f;
            w.SurfStrength = 0f;
            w.SkyReflectionStrength = 0f;
            w.ShoreFadeDistance = 0f;
        }

        [GpuFact]
        public void ACoarseClipmapRingShadesTheSwellWithoutTriangleFacets()
        {
            var plane = new WaterPlane(0f, 0f, 0f, LakeHalfExtent);
            var camera = new TopDownCamera();
            var probe = new WaterSettings();
            ApplyLake(probe);
            List<Quad> quads = DrawnQuads(plane, probe, camera);

            byte[] control = Render(plane, camera, control: true);
            byte[] subject = Render(plane, camera, control: false);

            // The subject must carry a real swell glint, or a low ratio would only mean a flat image.
            float spread = LumaSpread(subject);
            _out.WriteLine($"subject luminance spread (5th to 95th percentile) {spread:F1}");
            Assert.True(spread >= 40f,
                $"the subject's luminance only spreads {spread:F1} levels, so the swell glint is not reaching the frame");

            var results = new List<(int Level, Signature Control, Signature Subject)>();
            foreach (int level in new[] { 4, 5 })
            {
                Signature c = Measure(control, quads, level);
                Signature s = Measure(subject, quads, level);
                results.Add((level, c, s));
                _out.WriteLine($"level {level}: control edge {c.Edge:F3} mid {c.Mid:F3} ratio {c.Ratio:F2} " +
                               $"({c.EdgeSamples}/{c.MidSamples} samples); subject edge {s.Edge:F3} mid {s.Mid:F3} " +
                               $"ratio {s.Ratio:F2} ({s.EdgeSamples}/{s.MidSamples} samples)");
            }

            foreach ((int level, Signature c, Signature s) in results)
            {
                Assert.True(c.EdgeSamples >= 500 && c.MidSamples >= 300,
                    $"level {level} has only {c.EdgeSamples}/{c.MidSamples} samples in frame; the framing drifted");
                Assert.True(c.Ratio >= 2.5f,
                    $"level {level}'s CONTROL reads an edge ratio of only {c.Ratio:F2}, so the instrument cannot see " +
                    "a real per-vertex kink and the subject's number below means nothing. Check the edge mask.");
                Assert.True(s.Ratio <= 1.5f,
                    $"level {level} shades the swell with an edge-to-interior gradient jump ratio of {s.Ratio:F2}: " +
                    "the swell normal is being interpolated across the coarse triangles again (#381)");
            }
        }

        static byte[] Render(WaterPlane plane, TopDownCamera camera, bool control)
        {
            MeshHandle seabed = default;
            return Render3DSnapshot.Capture(Size, Size,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(400f, 0.1f));
                    scene.CameraOverride = camera;
                    scene.EffectTimeSeconds = FrozenTime;
                    scene.Post.RenderScale = RenderScale.MatchViewport;
                    scene.Post.Hdr.Enabled = false;
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    // The sun a few degrees off the zenith, so the half vector sits on the lobe's flank and the glint
                    // moves with every degree of tilt the swell gives the normal.
                    scene.Post.LightDirection = -Vector3.Normalize(new Vector3(0.08f, 1f, 0.11f));
                    scene.Post.LightColor = new Color(1f, 1f, 1f, 1f);

                    WaterSettings w = scene.Post.Water;
                    ApplyLake(w);
                    if (control)
                    {
                        // Height alone: a steep absorption ramp between black and white over a shallow bed.
                        w.GlintStrength = 0f;
                        w.DeepColor = new Color(0f, 0f, 0f, 1f);
                        w.ShallowColor = new Color(1f, 1f, 1f, 1f);
                        w.AbsorptionPerMetre = new Color(3f, 3f, 3f, 0f);
                    }
                    else
                    {
                        // The normal alone: a broad lobe over a constant deep body.
                        w.GlintStrength = 0.6f;
                        w.GlintRoughness = 0.3f;
                        w.GlintDistantRoughness = 0.3f;
                        w.DeepColor = new Color(0.05f, 0.10f, 0.15f, 1f);
                        w.ShallowColor = new Color(0.05f, 0.10f, 0.15f, 1f);
                    }
                },
                drawFrame: scene =>
                {
                    float bedTop = control ? -0.6f : -40f;
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, bedTop - 0.1f, 0f), new Color(0.2f, 0.2f, 0.2f, 1f));
                    scene.DrawWater(plane);
                },
                frames: 2);
        }

        /// <summary>One drawn clipmap quad: its level and its four corners in PIXELS, displaced exactly as the vertex
        /// stage displaces them (the geomorph taps included), plus whether its left neighbour is drawn at the same
        /// level and cell size, which the edge measurement needs because its left samples land there.</summary>
        readonly record struct Quad(int Level, Vector2 P0, Vector2 P1, Vector2 P2, Vector2 P3, bool LeftNeighbourUsable);

        static List<Quad> DrawnQuads(WaterPlane plane, WaterSettings s, TopDownCamera camera)
        {
            float cell = s.ClipmapCellSize;
            int ring = WaterClipmap.ClampRingCells(s.ClipmapRingCells);
            int levels = WaterClipmap.LevelsFor(plane, cell, ring);
            Vector2 focus = WaterClipmap.ClampFocus(plane, camera.Eye.X, camera.Eye.Z);
            var verts = new WaterClipmapVertex[WaterClipmap.VertexCount(levels, ring)];
            var indices = new uint[WaterClipmap.IndexCount(levels, ring)];
            WaterClipmap.Build(plane, focus.X, focus.Y, cell, ring, levels, s.ClipmapGeomorphBand, verts, indices,
                out int indexCount);

            var comps = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(s.SwellAmplitude, s.SwellWavelength,
                GerstnerWaves.DegreesToRadians(s.SwellDirectionDegrees),
                GerstnerWaves.DegreesToRadians(s.SwellSpreadDegrees),
                s.SwellSteepness, s.SwellSpeed, s.SwellSeed, s.SwellComponents, comps);
            ReadOnlySpan<GerstnerWaves.Component> stack = comps.AsSpan(0, n);

            int stride = ring + 1, perLevel = stride * stride;
            var pixel = new Vector2[verts.Length];
            var usable = new bool[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                WaterClipmapVertex v = verts[i];
                // The vertex stage's tap blend: its own evaluation, and the two coarse neighbours by Morph.
                var at = new Vector2(v.Position.X, v.Position.Z);
                bool offLattice = v.Coarse.LengthSquared() > 0f;
                float w0 = offLattice ? 1f - v.Morph : 1f, w1 = offLattice ? 0.5f * v.Morph : 0f;
                Vector3 p = w0 * Displaced(at, stack, s.SwellSteepness, v.Position.Y);
                if (w1 > 0f)
                {
                    p += w1 * Displaced(at - v.Coarse, stack, s.SwellSteepness, v.Position.Y);
                    p += w1 * Displaced(at + v.Coarse, stack, s.SwellSteepness, v.Position.Y);
                }
                usable[i] = camera.WorldToScreen(p, Size, Size, out pixel[i]);
            }

            // Which (level, x, z) quads the index buffer actually draws, and with what cell width, so a quad whose
            // left neighbour is a hole, another level, or clamped at the plane edge is not measured across.
            var drawn = new Dictionary<(int, int, int), bool>();
            var found = new List<(int Level, int X, int Z, uint I0, uint I1, uint I2, uint I3)>();
            for (int k = 0; k + 5 < indexCount; k += 6)
            {
                uint i0 = indices[k], i2 = indices[k + 1], i1 = indices[k + 2], i3 = indices[k + 5];
                int level = (int)(i0 / perLevel), local = (int)(i0 % perLevel);
                int x = local % stride, z = local / stride;
                float c = WaterClipmap.CellSize(cell, level);
                bool full = MathF.Abs(verts[i1].Position.X - verts[i0].Position.X - c) < 1e-3f
                    && MathF.Abs(verts[i2].Position.Z - verts[i0].Position.Z - c) < 1e-3f;
                drawn[(level, x, z)] = full;
                found.Add((level, x, z, i0, i1, i2, i3));
            }

            var quads = new List<Quad>();
            foreach (var q in found)
            {
                if (!drawn[(q.Level, q.X, q.Z)]) continue;
                if (!usable[q.I0] || !usable[q.I1] || !usable[q.I2] || !usable[q.I3]) continue;
                bool left = drawn.TryGetValue((q.Level, q.X - 1, q.Z), out bool leftFull) && leftFull;
                quads.Add(new Quad(q.Level, pixel[q.I0], pixel[q.I1], pixel[q.I2], pixel[q.I3], left));
            }
            return quads;
        }

        static Vector3 Displaced(Vector2 xz, ReadOnlySpan<GerstnerWaves.Component> stack, float steepness, float y)
        {
            GerstnerWaves.Sample sample = GerstnerWaves.Evaluate(xz.X, xz.Y, FrozenTime, steepness, stack);
            return new Vector3(xz.X, y, xz.Y) + sample.Offset;
        }

        readonly record struct Signature(float Edge, float Mid, int EdgeSamples, int MidSamples)
        {
            public float Ratio => Edge / MathF.Max(Mid, 1e-6f);
        }

        /// <summary>The mean jump in the horizontal luminance gradient across each quad's left edge, and across its
        /// midline. Rows are kept away from the corners and from the quad's own diagonal (the triangulation runs it
        /// from the top-right corner to the bottom-left one) so every sample straddles exactly the line it is meant
        /// to.</summary>
        static Signature Measure(byte[] rgba, List<Quad> quads, int level)
        {
            double edge = 0, mid = 0;
            int ne = 0, nm = 0;
            foreach (Quad q in quads)
            {
                if (q.Level != level) continue;
                float top = q.P0.Y, bottom = q.P2.Y;
                for (int y = (int)MathF.Ceiling(top); y < bottom; y++)
                {
                    float t = (y + 0.5f - top) / (bottom - top);
                    if (q.LeftNeighbourUsable && t >= 0.22f && t <= 0.78f)
                    {
                        float x = q.P0.X + t * (q.P2.X - q.P0.X);
                        if (Jump(rgba, x, y, out float j)) { edge += j; ne++; }
                    }
                    if ((t >= 0.15f && t <= 0.30f) || (t >= 0.70f && t <= 0.85f))
                    {
                        Vector2 a = (q.P0 + q.P1) * 0.5f, b = (q.P2 + q.P3) * 0.5f;
                        float x = a.X + t * (b.X - a.X);
                        if (Jump(rgba, x, y, out float j)) { mid += j; nm++; }
                    }
                }
            }
            return new Signature(ne > 0 ? (float)(edge / ne) : 0f, nm > 0 ? (float)(mid / nm) : 0f, ne, nm);
        }

        /// <summary>|right gradient - left gradient| across the vertical line at pixel x-coordinate
        /// <paramref name="x"/> in row <paramref name="y"/>. Each side's gradient comes from pixels whose centres lie
        /// strictly on that side, which with no MSAA are shaded wholly by that side's triangle.</summary>
        static bool Jump(byte[] rgba, float x, int y, out float jump)
        {
            jump = 0f;
            int left = (int)MathF.Ceiling(x - 0.5f) - 1, right = left + 1;
            if (y < 0 || y >= Size || left - Probe < 0 || right + Probe >= Size) return false;
            float gl = (Luma(rgba, left, y) - Luma(rgba, left - Probe, y)) / Probe;
            float gr = (Luma(rgba, right + Probe, y) - Luma(rgba, right, y)) / Probe;
            jump = MathF.Abs(gr - gl);
            return true;
        }

        /// <summary>5th to 95th percentile luminance over the water, which the frame covers up to the lake's far
        /// edge (x and z at 120 m, column and row 696).</summary>
        static float LumaSpread(byte[] rgba)
        {
            const int Edge = 690;
            var values = new float[Edge * Edge];
            for (int y = 0; y < Edge; y++)
                for (int x = 0; x < Edge; x++)
                    values[y * Edge + x] = Luma(rgba, x, y);
            Array.Sort(values);
            return values[(int)(values.Length * 0.95f)] - values[(int)(values.Length * 0.05f)];
        }

        static float Luma(byte[] rgba, int x, int y)
        {
            int i = (y * Size + x) * 4;
            return 0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2];
        }

        /// <summary>Straight down from far above the lake's south-west corner, with an OFF-CENTRE orthographic
        /// window over the far side of the lake. The eye's own planar position is what the clipmap centres on, so
        /// this is what puts the coarse rings under the window. Screen right is world +X and screen down is +Z.</summary>
        sealed class TopDownCamera : IIsoCamera3D
        {
            public Vector3 Eye => new(EyeX, EyeHeight, EyeZ);
            public Vector3 Forward => -Vector3.UnitY;
            public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Eye - Vector3.UnitY, -Vector3.UnitZ);
            public Matrix4x4 Projection => Matrix4x4.CreateOrthographicOffCenter(
                ViewMin - EyeX, ViewMax - EyeX, EyeZ - ViewMax, EyeZ - ViewMin, EyeHeight - 200f, EyeHeight + 200f);
            public Matrix4x4 ViewProjection => View * Projection;
            public bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel) =>
                CameraProjection.WorldToScreen(ViewProjection, world, viewportWidth, viewportHeight, out screenPixel);
        }
    }
}
