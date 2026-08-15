using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The measurement harness behind the per-cascade caster cull, on an MMO-shaped caster load: roughly 15k small
    /// instanced casters spread over a 500 m disc (Ruinborne's foliage plus prop count), 50 large merged meshes
    /// (HLOD clusters), four cascades at 2048, a 35 degree sun. It renders the SAME scene several times in ONE
    /// process - shadows off, culling off, then culling on at a sweep of merge gaps - and prints every number.
    /// <para>
    /// The A/B is in-process on purpose. A number measured against a different build is not comparable (JIT, device
    /// and thermal state all move), and the interesting quantity is not the absolute millisecond but the ratio
    /// between drawing every caster into every cascade and drawing only the ones that reach it.
    /// </para>
    /// <para>
    /// The shadows-off row is the isolation baseline: the whole-frame time minus that row is what the shadow pass
    /// actually costs, which is the only number worth quoting (the main pass over 15k instances and the post chain
    /// dominate the raw frame time and would drown a real saving).
    /// </para>
    /// <para>
    /// The sun advances by Ruinborne's real rate (a 30 minute day, about 0.2 degrees per second, so 0.0033 degrees
    /// per frame at 60 fps) so the depth pass is DIRTY every frame, which is the case the fix exists for. See
    /// <c>ShadowCascadeStabilityTests</c> for why that is the normal case and not a contrived one.
    /// </para>
    /// Gated on KE_GPU_TESTS. Run it in Release: a Debug build inflates the CPU-side split several times over while
    /// leaving the GPU work alone, which reads as the cull costing more than it saves.
    /// </summary>
    public sealed class ShadowCascadeCullPerfGpuTests
    {
        const int W = 960, H = 600;
        const int Resolution = 2048, Cascades = 4;
        const float DiscRadius = 250f;          // a 500 m disc
        // Ruinborne's actual caster mix, and the TRIANGLE weighting matters as much as the counts: an instance
        // outside a cascade still pays vertex processing plus clipping even though it rasterizes nothing, so what
        // the cull recovers scales with the vertices of the instances it drops, not with their number. A harness
        // built entirely from 12-triangle boxes measures the cull as free and worthless in equal measure.
        const int FoliageCasters = 13600;       // ground cover: very light
        const int TreeRockCasters = 3600;       // trees and rocks: a few hundred triangles each
        const int MergedMeshes = 50;            // HLOD clusters: large bounds, heavy geometry
        const float ChunkSize = 64f;            // submission is chunk-major, like a streamed world
        const float SunElevationDegrees = 35f;
        const float SunDegreesPerFrame = 0.2f / 60f;   // Ruinborne: 30 minute day at 60 fps
        // Every configuration is measured in INTERLEAVED blocks on ONE device, round-robin, and reduced by median.
        // Measuring them as separate sequential runs does not work: the first configuration pays the JIT and the
        // device warm-up for all of them, and the machine drifts under the run, which produced a first draft where
        // "shadows off" timed SLOWER than "shadows on". Round-robin blocks spread any drift across every
        // configuration equally, and the median throws away the scheduler outliers a mean would swallow.
        const int Rounds = 25, BlockWarmup = 3, BlockFrames = 10;

        static readonly int[] MergeGaps = { 0, 8, 16, 32, 64 };

        readonly ITestOutputHelper _out;
        public ShadowCascadeCullPerfGpuTests(ITestOutputHelper o) => _out = o;

        readonly record struct Placement(int Kind, Vector3 Position, float Scale, float Yaw);

        readonly record struct Measurement(string Label, double ShadowEncodeMs, double FrameMs,
            int Candidates, int[] PerCascade, int[] Spans)
        {
            public int TotalInstanceDraws { get { int n = 0; foreach (int v in PerCascade) n += v; return n; } }
            public int TotalDrawCalls { get { int n = 0; foreach (int v in Spans) n += v; return n; } }
        }

        // Deterministic chunk-major layout over the disc, so submission order is spatially coherent in chunk-sized
        // blocks exactly as a streamed world's is. That coherence is what decides how badly a per-cascade split
        // fragments the instanced runs, so a random layout would measure the wrong thing.
        static Placement[] BuildPlacements()
        {
            var list = new List<Placement>(FoliageCasters + TreeRockCasters + MergedMeshes);
            uint seed = 0x5EED_1234;
            float Next() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (float)(1 << 24); }

            int chunksPerSide = (int)MathF.Ceiling(2f * DiscRadius / ChunkSize);
            // The square grid over-covers the disc by 4/pi, so scale the per-chunk quotas up to land on the target
            // counts after the disc clip.
            double discFraction = Math.PI / 4.0;
            int chunkCount = chunksPerSide * chunksPerSide;
            int foliagePerChunk = (int)Math.Ceiling(FoliageCasters / discFraction / chunkCount);
            int treesPerChunk = (int)Math.Ceiling(TreeRockCasters / discFraction / chunkCount);
            int foliage = 0, trees = 0;
            for (int cz = 0; cz < chunksPerSide; cz++)
                for (int cx = 0; cx < chunksPerSide; cx++)
                {
                    float ox = -DiscRadius + cx * ChunkSize, oz = -DiscRadius + cz * ChunkSize;
                    // Within a chunk the kinds are laid out in BLOCKS, not interleaved: a streamed world emits a
                    // chunk's ground cover together, then its trees, then its rocks. Interleaving them would stride
                    // every mesh run by the kind count and make the fragmentation measurement meaningless.
                    bool Emit(int kind, float scale)
                    {
                        float x = ox + Next() * ChunkSize, z = oz + Next() * ChunkSize;
                        if (x * x + z * z > DiscRadius * DiscRadius) return false;   // clip the square grid to the disc
                        list.Add(new Placement(kind, new Vector3(x, 0f, z), scale, Next() * 6.28f));
                        return true;
                    }
                    for (int i = 0; i < foliagePerChunk && foliage < FoliageCasters; i++)
                        if (Emit(i % 2, 0.6f + Next() * 0.8f)) foliage++;
                    for (int i = 0; i < treesPerChunk && trees < TreeRockCasters; i++)
                        if (Emit(2 + i % 2, 0.7f + Next() * 0.9f)) trees++;
                }
            // The merged HLOD meshes: few, large, spread over the same disc.
            for (int i = 0; i < MergedMeshes; i++)
            {
                float a = Next() * 6.28f, rr = MathF.Sqrt(Next()) * DiscRadius;
                list.Add(new Placement(4, new Vector3(MathF.Cos(a) * rr, 0f, MathF.Sin(a) * rr), 1f, Next() * 6.28f));
            }
            return list.ToArray();
        }

        static Vector3 SunAt(float extraDegrees)
        {
            float e = SunElevationDegrees * MathF.PI / 180f;
            float a = extraDegrees * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Cos(a) * MathF.Cos(e), -MathF.Sin(e), MathF.Sin(a) * MathF.Cos(e)));
        }

        readonly record struct Config(string Label, ShadowMode Mode, bool Culling, int MergeGap);

        static double Median(List<double> xs)
        {
            xs.Sort();
            return xs.Count == 0 ? 0 : xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) * 0.5;
        }

        Measurement[] MeasureAll(Config[] configs, Placement[] placements)
        {
            var shadows = new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = Resolution,
                ShadowCascadeCount = Cascades,
                // The light hold is OFF here, and that is the whole point of the moving sun below: this bench
                // measures the per-cascade CULL, which only runs on a frame that records, so it needs the depth pass
                // dirty on every measured frame. With the hold at its shipped default the sun would be held for most
                // frames and the bench would time the skip floor instead of the cull. What the hold itself costs is
                // ShadowRerecordBenchGpuTests.
                ShadowLightHoldTexels = 0f,
            };

            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H, shadows);
            Scene3D scene = preview.Scene;
            scene.EnableTiming = true;
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Camera.Azimuth = 0.6f;
            scene.Camera.Elevation = 0.35f;
            scene.Camera.Frame(Vector3.Zero, new Vector3(60f, 20f, 60f));

            MeshHandle ground = scene.LoadMesh(MeshPrimitives.Tile(2f * DiscRadius, 0.2f));
            var kinds = new MeshHandle[5];
            kinds[0] = scene.LoadMesh(MeshPrimitives.Box(0.8f));                  // ground cover: 12 triangles
            kinds[1] = scene.LoadMesh(MeshPrimitives.Cone(0.5f, 2.2f, 6));        // ground cover: 18 triangles
            kinds[2] = scene.LoadMesh(MeshPrimitives.Sphere(2.5f, 14, 20));       // tree canopy: ~540 triangles
            kinds[3] = scene.LoadMesh(MeshPrimitives.RoundedBox(2.2f, 0.4f, 5));  // rock: a few hundred triangles
            kinds[4] = scene.LoadMesh(MeshPrimitives.Sphere(18f, 24, 32));        // merged HLOD cluster: large bounds, ~1500 triangles
            var tint = new Color(0.35f, 0.55f, 0.3f, 1f);

            float sunDegrees = 0f;
            void DrawFrame(Scene3D s)
            {
                s.Post.LightDirection = SunAt(sunDegrees);
                s.Draw(ground, Matrix4x4.Identity, new Color(0.55f, 0.56f, 0.5f, 1f), Material.None, false);
                foreach (Placement p in placements)
                {
                    Matrix4x4 world = Matrix4x4.CreateScale(p.Scale)
                        * Matrix4x4.CreateRotationY(p.Yaw)
                        * Matrix4x4.CreateTranslation(p.Position);
                    s.Draw(kinds[p.Kind], world, tint);
                }
            }

            var frameSamples = new List<double>[configs.Length];
            var encodeSamples = new List<double>[configs.Length];
            var perCascade = new int[configs.Length][];
            var spanCounts = new int[configs.Length][];
            int candidates = 0;
            for (int i = 0; i < configs.Length; i++) { frameSamples[i] = new List<double>(); encodeSamples[i] = new List<double>(); }

            // ROTATE the order each round. Measuring the configurations in a fixed order leaves each one pinned to
            // one position in the round, and position turns out to matter: a fixed-order run reproducibly split the
            // gaps into a good set and a bad set by their INDEX (3 and 5 fast, 2, 4 and 6 slow) in a pattern the
            // deterministic instance and draw counts cannot explain. Rotating breaks the correlation, and the
            // orderings then agree with each other.
            for (int round = 0; round < Rounds; round++)
                for (int k = 0; k < configs.Length; k++)
                {
                    int ci = (k + round) % configs.Length;
                    Config cfg = configs[ci];
                    scene.Post.Quality.Shadows.Mode = cfg.Mode;
                    scene.ShadowCascadeCulling = cfg.Culling;
                    scene.ShadowCullMergeGap = cfg.MergeGap;
                    for (int i = 0; i < BlockWarmup; i++) { preview.Capture(DrawFrame); sunDegrees += SunDegreesPerFrame; }
                    gd.WaitForIdle();
                    for (int i = 0; i < BlockFrames; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        preview.Capture(DrawFrame);
                        gd.WaitForIdle();
                        frameSamples[ci].Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                        encodeSamples[ci].Add(scene.PassTimingsMs.ShadowDepthMs);
                        sunDegrees += SunDegreesPerFrame;
                        if (cfg.Mode == ShadowMode.ShadowMap)
                            Assert.False(scene.ShadowPassSkippedLastFrame,
                                "the moving sun must keep the depth pass dirty; a skipped frame would measure nothing");
                    }
                    if (cfg.Mode != ShadowMode.ShadowMap) continue;
                    perCascade[ci] = new int[Cascades];
                    spanCounts[ci] = new int[Cascades];
                    for (int c = 0; c < Cascades; c++)
                    {
                        perCascade[ci][c] = scene.ShadowCascadeCasterCount(c);
                        spanCounts[ci][c] = scene.ShadowCascadeSpanCount(c);
                    }
                    candidates = scene.ShadowCasterCandidateCount;
                }

            var results = new Measurement[configs.Length];
            for (int i = 0; i < configs.Length; i++)
                results[i] = new Measurement(configs[i].Label, Median(encodeSamples[i]), Median(frameSamples[i]),
                    candidates, perCascade[i] ?? Array.Empty<int>(), spanCounts[i] ?? Array.Empty<int>());
            return results;
        }

        [GpuFact]
        public void Ruinborne_shaped_caster_load_costs_less_per_cascade_after_culling()
        {
            Placement[] placements = BuildPlacements();
            _out.WriteLine($"scene: {placements.Length} caster instances over a {2 * DiscRadius} m disc " +
                           $"({MergedMeshes} merged HLOD meshes), {Cascades} cascades at {Resolution}, sun {SunElevationDegrees} deg " +
                           $"advancing {SunDegreesPerFrame:0.#####} deg/frame, {Rounds} x {BlockFrames} interleaved frames " +
                           $"per configuration (median), {(IsDebug ? "DEBUG" : "RELEASE")} build");

            var configs = new List<Config>
            {
                new("shadows off", ShadowMode.Off, true, Scene3D.DefaultShadowCullMergeGap),
                new("cull off (baseline)", ShadowMode.ShadowMap, false, 0),
            };
            foreach (int gap in MergeGaps) configs.Add(new Config($"cull on, gap {gap}", ShadowMode.ShadowMap, true, gap));

            Measurement[] results = MeasureAll(configs.ToArray(), placements);
            Measurement noShadows = results[0], before = results[1];

            _out.WriteLine("");
            _out.WriteLine($"{"configuration",-22} {"shadow ms",10} {"frame ms",9} {"pass ms",8} {"inst draws",11} {"draw calls",11}");
            _out.WriteLine($"{noShadows.Label,-22} {"-",10} {noShadows.FrameMs,9:0.000} {"-",8} {"-",11} {"-",11}");
            for (int i = 1; i < results.Length; i++)
            {
                Measurement m = results[i];
                _out.WriteLine($"{m.Label,-22} {m.ShadowEncodeMs,10:0.000} {m.FrameMs,9:0.000} {m.FrameMs - noShadows.FrameMs,8:0.000} " +
                               $"{m.TotalInstanceDraws,11} {m.TotalDrawCalls,11}");
            }

            _out.WriteLine("");
            _out.WriteLine($"candidates per cascade: {before.Candidates}");
            for (int i = 1; i < results.Length; i++)
                _out.WriteLine($"{results[i].Label}: drawn [{string.Join(", ", results[i].PerCascade)}] " +
                               $"in [{string.Join(", ", results[i].Spans)}] draws");

            Measurement shipped = results[2 + Array.IndexOf(MergeGaps, Scene3D.DefaultShadowCullMergeGap)];
            double beforePass = before.FrameMs - noShadows.FrameMs;
            double afterPass = shipped.FrameMs - noShadows.FrameMs;
            _out.WriteLine("");
            _out.WriteLine($"SHIPPED gap {Scene3D.DefaultShadowCullMergeGap}: shadow pass {beforePass:0.000} -> {afterPass:0.000} ms/frame " +
                           $"({(beforePass > 0 ? 100.0 * (beforePass - afterPass) / beforePass : 0):0.0} percent), " +
                           $"caster instance-draws {before.TotalInstanceDraws} -> {shipped.TotalInstanceDraws}");

            // The claim under test is the rasterized caster count, which is deterministic. The millisecond numbers are
            // reported, not asserted: a shared dev machine's timings are not a stable gate.
            Assert.Equal(before.Candidates, shipped.Candidates);
            Assert.True(shipped.TotalInstanceDraws < before.TotalInstanceDraws / 4,
                $"expected the per-cascade cull to cut the caster instance-draws by at least 4x, " +
                $"got {shipped.TotalInstanceDraws} of {before.TotalInstanceDraws}");
        }

        static bool IsDebug
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }
    }
}
