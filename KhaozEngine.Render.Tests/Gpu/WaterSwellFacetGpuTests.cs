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
    /// <para>
    /// <b>The whitecap fold is the same measurement</b>
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1100">#1100</see>). A fold interpolated from the
    /// vertices is linear inside each triangle too, so the foam it thresholds bends at every edge and a whitecap on a
    /// coarse ring comes out as a triangle. The foam subject renders the crest term alone, white on black, with the
    /// coverage raised so most of a crest's flank sits inside the whitecap ramp rather than clipped at 0 or 1, and
    /// with the break-up pattern stretched flat so it multiplies every pixel by the same value. Only samples whose
    /// probe pixels are all inside the ramp are counted, since a clipped pixel has no gradient to jump, and the
    /// samples are pooled over several frozen times. Measured on Metal: with the fold interpolated from the vertices
    /// the foam read 10.2 on the 8 m ring and 11.4 on the 16 m ring, and evaluated per pixel it reads 0.9 and 1.0.
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
        /// <summary>The same for the foam subject, whose whitecap ramp is only about ten pixels wide, so a sample
        /// that has to sit wholly inside it cannot reach as far.</summary>
        const int FoamProbe = 2;
        /// <summary>The frozen times the foam subject pools.</summary>
        static readonly float[] FoamTimes = { FrozenTime, 5.2f, 6.9f, 8.3f };

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

            byte[] control = Render(plane, camera, Subject.Control);
            byte[] subject = Render(plane, camera, Subject.Glint);

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

        [GpuFact]
        public void ACoarseClipmapRingDrawsWhitecapsWithoutTriangleEdges()
        {
            var plane = new WaterPlane(0f, 0f, 0f, LakeHalfExtent);
            var camera = new TopDownCamera();
            var probe = new WaterSettings();
            ApplyLake(probe);
            byte[] control = Render(plane, camera, Subject.Control);
            List<Quad> controlQuads = DrawnQuads(plane, probe, camera);

            // A whitecap covers a few percent of the frame and only its flanks are inside the ramp, so the samples
            // are pooled over several frozen times, each measured against the triangles drawn at that time.
            var frames = new List<(byte[] Foam, List<Quad> Quads, float Top)>();
            foreach (float time in FoamTimes)
            {
                byte[] foam = Render(plane, camera, Subject.Foam, time);
                // The crest term's ceiling: the flat break-up pattern scales every pixel by one value, whatever it is.
                float top = 0f;
                for (int i = 0; i < foam.Length; i += 4) top = MathF.Max(top, Luma(foam, (i / 4) % Size, (i / 4) / Size));
                _out.WriteLine($"t={time}: foam ceiling {top:F1}");
                Assert.True(top >= 120f, $"the foam subject peaks at only {top:F1} at t={time}, so the whitecaps are not reaching the frame");
                frames.Add((foam, DrawnQuads(plane, probe, camera, time), top));
            }

            var results = new List<(int Level, Signature Control, Signature Foam)>();
            foreach (int level in new[] { 4, 5 })
            {
                Signature c = Measure(control, controlQuads, level);
                Signature f = default;
                foreach ((byte[] foam, List<Quad> quads, float top) in frames)
                    f = f.Merge(Measure(foam, quads, level, luma => luma > 3f && luma < top - 3f, FoamProbe));
                results.Add((level, c, f));
                _out.WriteLine($"level {level}: control ratio {c.Ratio:F2}; foam edge {f.Edge:F3} mid {f.Mid:F3} " +
                               $"ratio {f.Ratio:F2} ({f.EdgeSamples}/{f.MidSamples} samples)");
            }

            foreach ((int level, Signature c, Signature f) in results)
            {
                Assert.True(c.Ratio >= 2.5f,
                    $"level {level}'s CONTROL reads an edge ratio of only {c.Ratio:F2}, so the instrument cannot see " +
                    "a real per-vertex kink and the foam number below means nothing. Check the edge mask.");
                Assert.True(f.EdgeSamples >= 100 && f.MidSamples >= 60,
                    $"level {level} has only {f.EdgeSamples}/{f.MidSamples} in-ramp foam samples; the crests left the frame");
                Assert.True(f.Ratio <= 1.5f,
                    $"level {level} draws whitecaps with an edge-to-interior gradient jump ratio of {f.Ratio:F2}: the " +
                    "whitecap fold is being interpolated across the coarse triangles again (#1100)");
            }
        }

        enum Subject { Control, Glint, Foam }

        static byte[] Render(WaterPlane plane, TopDownCamera camera, Subject subject, float time = FrozenTime)
        {
            MeshHandle seabed = default;
            return Render3DSnapshot.Capture(Size, Size,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(400f, 0.1f));
                    scene.CameraOverride = camera;
                    scene.EffectTimeSeconds = time;
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
                    if (subject == Subject.Foam)
                    {
                        // The crest term alone: black body, no glint, no reflection, white foam. Coverage 0.9 puts
                        // the whitecap threshold at a fold of 0.1, so the ramp spans a crest's flank, and a pattern
                        // scale this large leaves every pixel at the same break-up value.
                        w.GlintStrength = 0f;
                        w.DeepColor = new Color(0f, 0f, 0f, 1f);
                        w.ShallowColor = new Color(0f, 0f, 0f, 1f);
                        w.HorizonColor = new Color(0f, 0f, 0f, 1f);
                        w.FoamColor = new Color(1f, 1f, 1f, 1f);
                        w.FoamStrength = 1f;
                        w.FoamCrestCoverage = 1f;
                        w.FoamPatternScale = 1e6f;
                        w.FoamShoreWidth = 0f;
                    }
                    else if (subject == Subject.Control)
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
                    float bedTop = subject == Subject.Control ? -0.6f : -40f;
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, bedTop - 0.1f, 0f), new Color(0.2f, 0.2f, 0.2f, 1f));
                    scene.DrawWater(plane);
                },
                frames: 2);
        }

        /// <summary>One drawn clipmap quad: its level and its four corners in PIXELS, displaced exactly as the vertex
        /// stage displaces them (the geomorph taps included), plus whether its left neighbour is drawn at the same
        /// level and cell size, which the edge measurement needs because its left samples land there.</summary>
        readonly record struct Quad(int Level, Vector2 P0, Vector2 P1, Vector2 P2, Vector2 P3, bool LeftNeighbourUsable);

        static List<Quad> DrawnQuads(WaterPlane plane, WaterSettings s, TopDownCamera camera, float time = FrozenTime)
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
                Vector3 p = w0 * Displaced(at, stack, s.SwellSteepness, v.Position.Y, time);
                if (w1 > 0f)
                {
                    p += w1 * Displaced(at - v.Coarse, stack, s.SwellSteepness, v.Position.Y, time);
                    p += w1 * Displaced(at + v.Coarse, stack, s.SwellSteepness, v.Position.Y, time);
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

        static Vector3 Displaced(Vector2 xz, ReadOnlySpan<GerstnerWaves.Component> stack, float steepness, float y,
            float time)
        {
            GerstnerWaves.Sample sample = GerstnerWaves.Evaluate(xz.X, xz.Y, time, steepness, stack);
            return new Vector3(xz.X, y, xz.Y) + sample.Offset;
        }

        readonly record struct Signature(float Edge, float Mid, int EdgeSamples, int MidSamples)
        {
            public float Ratio => Edge / MathF.Max(Mid, 1e-6f);

            /// <summary>Pool two measurements, each mean weighted by its own sample count.</summary>
            public Signature Merge(Signature o) => new(
                (Edge * EdgeSamples + o.Edge * o.EdgeSamples) / Math.Max(EdgeSamples + o.EdgeSamples, 1),
                (Mid * MidSamples + o.Mid * o.MidSamples) / Math.Max(MidSamples + o.MidSamples, 1),
                EdgeSamples + o.EdgeSamples, MidSamples + o.MidSamples);
        }

        /// <summary>The mean jump in the horizontal luminance gradient across each quad's left edge, and across its
        /// midline. Rows are kept away from the corners and from the quad's own diagonal (the triangulation runs it
        /// from the top-right corner to the bottom-left one) so every sample straddles exactly the line it is meant
        /// to.</summary>
        static Signature Measure(byte[] rgba, List<Quad> quads, int level, Func<float, bool>? inRamp = null,
            int probe = Probe)
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
                        if (Jump(rgba, x, y, inRamp, probe, out float j)) { edge += j; ne++; }
                    }
                    if ((t >= 0.15f && t <= 0.30f) || (t >= 0.70f && t <= 0.85f))
                    {
                        Vector2 a = (q.P0 + q.P1) * 0.5f, b = (q.P2 + q.P3) * 0.5f;
                        float x = a.X + t * (b.X - a.X);
                        if (Jump(rgba, x, y, inRamp, probe, out float j)) { mid += j; nm++; }
                    }
                }
            }
            return new Signature(ne > 0 ? (float)(edge / ne) : 0f, nm > 0 ? (float)(mid / nm) : 0f, ne, nm);
        }

        /// <summary>|right gradient - left gradient| across the vertical line at pixel x-coordinate
        /// <paramref name="x"/> in row <paramref name="y"/>. Each side's gradient comes from pixels whose centres lie
        /// strictly on that side, which with no MSAA are shaded wholly by that side's triangle. With
        /// <paramref name="inRamp"/> the sample only counts when every pixel it reads passes it.</summary>
        static bool Jump(byte[] rgba, float x, int y, Func<float, bool>? inRamp, int probe, out float jump)
        {
            jump = 0f;
            int left = (int)MathF.Ceiling(x - 0.5f) - 1, right = left + 1;
            if (y < 0 || y >= Size || left - probe < 0 || right + probe >= Size) return false;
            if (inRamp != null)
                for (int px = left - probe; px <= right + probe; px++)
                    if (!inRamp(Luma(rgba, px, y))) return false;
            float gl = (Luma(rgba, left, y) - Luma(rgba, left - probe, y)) / probe;
            float gr = (Luma(rgba, right + probe, y) - Luma(rgba, right, y)) / probe;
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
