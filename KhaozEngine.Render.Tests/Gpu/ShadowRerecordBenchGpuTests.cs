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
    /// The local reproduction bench for issue #410's shadow re-record stall: one trace-shaped scene, rendered in ONE
    /// process under the four shadow-decision regimes the field trace can be in, with the cost of each printed
    /// beside the reason bits that produced it.
    /// <para>
    /// The scene is built to the Windows/D3D11 field trace's shape rather than to a round number: 1,525 rigid
    /// shadow-caster instances inside 4,593 drawn instances (the 1,600 and 4,800 quotas below, less the placements
    /// the disc clip rejects), chunk-major over a 500 m disc, four cascades at 2048, one skinned caster. The regimes
    /// are the ones the trace's diagnostics named: a stationary scene whose only dirty reason is that a skinned
    /// caster exists, the same scene with the skinned caster gone and the sun frozen (the cheap frame, the pass
    /// skips), the same scene under Ruinborne's real daylight rate (the sun alone re-fits every cascade), and the
    /// same scene under a sun delta a third of that, which is far below anything a viewer could see and still
    /// re-records the whole atlas.
    /// </para>
    /// <para>
    /// <b>The printed table is only meaningful from a serialized or an isolated run.</b> This class sits in the
    /// <c>AllocSensitive</c> collection, whose <c>DisableParallelization</c> keeps it off the parallel pool, because
    /// the rest of the assembly running beside it inflates this bench's encode numbers four to six times over
    /// (0.300 ms measured alone against 1.392 ms measured in an unserialized suite). Quote the table from a run
    /// where nothing else on this machine is competing for the device, and treat a number from any other run as
    /// contaminated.
    /// </para>
    /// <para>
    /// <b>What is asserted and what is only reported.</b> The assertions are the dirty-reason facts, which are
    /// deterministic: which regime renders, which skips, and which single reason bit each one names. The
    /// milliseconds are written to test output and asserted against nothing at all, because a wall-clock threshold
    /// on a shared machine is a coin flip and this bench must be runnable on CI without becoming the flakiest row in
    /// the suite. Read the numbers, do not gate on them.
    /// </para>
    /// <para>
    /// <b>The CPU-versus-queue question this bench can answer.</b> <see cref="Scene3DPassTimingsMs.ShadowDepthMs"/>
    /// is wall clock measured INSIDE command recording, so a large value is ambiguous on its face: it could be CPU
    /// work or it could be a graphics call blocking behind GPU work. The <c>pipelined</c> configuration is the
    /// discriminator. It records the identical frame with the queue deliberately loaded (no drain between frames)
    /// while its <c>drained</c> twin records with the device idled first. If the encode number is the same in both,
    /// recording is not waiting on the GPU on this backend, and the pass timing means what it says.
    /// </para>
    /// Gated on KE_GPU_TESTS. Run it in Release: a Debug build inflates the CPU-side recording several times over
    /// and would read as a re-record cost this machine does not actually pay.
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class ShadowRerecordBenchGpuTests
    {
        const int W = 960, H = 600;
        const int Resolution = 2048, Cascades = 4;
        const float DiscRadius = 250f;              // a 500 m disc, as the cascade-cull bench uses
        const float ChunkSize = 64f;                // submission is chunk-major, like a streamed world
        // The trace's shape. 1,600 casters inside 4,800 instances: the remaining 3,200 are drawn with the
        // castsShadows opt-out, so they load the main pass exactly as the field scene's non-casting props do while
        // staying out of the depth pass, which is the split the trace reports (1,601 candidates, ~4,800 instances).
        const int CasterInstances = 1600;
        const int NonCasterInstances = 3200;
        // Casters and non-casters are emitted in small alternating groups WITHIN a chunk, which is what makes the
        // caster list fragment into spans the way a real streamed chunk's mixed contents do. The depth pass iterates
        // SPANS, not instances, so this is the knob that decides what the bench actually measures: emitting each
        // chunk's casters as one block yields about 80 spans over four cascades, an order of magnitude below the
        // field trace's 707 to 789, and would measure a pass shape the reporting machine never records.
        const int CasterGroup = 4, NonCasterGroup = 8;
        const float SunElevationDegrees = 35f;
        // Ruinborne's real daylight rate: a 30 minute day is 0.2 deg/s, so 0.00333 deg per frame at 60 fps.
        const float DaylightDegreesPerFrame = 0.2f / 60f;
        // A thousandth of a degree per frame: three times slower than daylight and still far below any visual
        // epsilon. How far a sun step moves a shadow depends on the sun's ELEVATION as well as on the rotation: the
        // foot of a caster h tall sits at h*cot(e), so an azimuth step sweeps it by h*cot(e)*dTheta and an elevation
        // step lifts it by h*dTheta/sin^2(e). At this bench's 35 deg sun those are 1.43x and 3.04x the naive
        // h*dTheta, so 0.001 deg moves the shadow of a 10 m caster by at most 0.53 mm (0.17 mm naive). At cascade 3
        // (radius ~= 120 m at 2048) one texel is about 12 cm on the ground, so that is still ~220x below one texel.
        const float SubEpsilonDegreesPerFrame = 0.001f;
        // Interleaved round-robin blocks on one device, reduced by median, for the same reason the cascade-cull
        // bench does it: a sequential run charges the first configuration with the JIT and the device warm-up for
        // all of them, and machine drift over the run lands entirely on whichever configuration ran last.
        const int Rounds = 20, BlockWarmup = 4, BlockFrames = 10;

        readonly ITestOutputHelper _out;
        public ShadowRerecordBenchGpuTests(ITestOutputHelper o) => _out = o;

        readonly record struct Placement(int Kind, Vector3 Position, float Scale, float Yaw, bool CastsShadows);

        /// <summary>One measured regime. <paramref name="Skinned"/> queues the skinned caster,
        /// <paramref name="SunDegreesPerFrame"/> advances the sun, <paramref name="Drain"/> idles the device before
        /// each measured frame (off makes it the pipelined probe).</summary>
        readonly record struct Config(string Label, ShadowMode Mode, bool Skinned, float SunDegreesPerFrame, bool Drain);

        /// <summary>One regime's reduced numbers. <paramref name="Spans"/> and <paramref name="DrawCalls"/> are
        /// means over the regime's RENDERED frames, not a snapshot of the last one, so dividing the mean
        /// milliseconds by them compares two quantities reduced the same way (the sun-moving regimes sit at a
        /// slightly different angle each frame, so the cull splits the caster list into a slightly different span
        /// set every time). <paramref name="PerCascadeSpans"/> is the exception and IS the last rendered frame's
        /// snapshot, because a per-cascade split only means something as one frame's four numbers.</summary>
        readonly record struct Measurement(string Label, double ShadowMeanMs, double ShadowMedianMs,
            double FrameMedianMs, int Spans, int[] PerCascadeSpans, int DrawCalls, int SkinnedDraws, int Candidates,
            int RenderedFrames, int SkippedFrames)
        {
            public double MicrosecondsPerSpan => Spans > 0 ? ShadowMeanMs * 1000.0 / Spans : 0;
            public double MicrosecondsPerDraw => DrawCalls > 0 ? ShadowMeanMs * 1000.0 / DrawCalls : 0;
        }

        // Deterministic chunk-major layout over the disc: a streamed world submits a chunk's contents together, and
        // that spatial coherence is what decides how the per-cascade cull fragments the instanced runs. A random
        // layout would measure a span count no real scene produces.
        static Placement[] BuildPlacements()
        {
            var list = new List<Placement>(CasterInstances + NonCasterInstances);
            uint seed = 0x5EED_0410;
            float Next() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (float)(1 << 24); }

            int chunksPerSide = (int)MathF.Ceiling(2f * DiscRadius / ChunkSize);
            double discFraction = Math.PI / 4.0;            // the square grid over-covers the disc by 4/pi
            int chunkCount = chunksPerSide * chunksPerSide;
            int castersPerChunk = (int)Math.Ceiling(CasterInstances / discFraction / chunkCount);
            int othersPerChunk = (int)Math.Ceiling(NonCasterInstances / discFraction / chunkCount);
            int casters = 0, others = 0;
            for (int cz = 0; cz < chunksPerSide; cz++)
                for (int cx = 0; cx < chunksPerSide; cx++)
                {
                    float ox = -DiscRadius + cx * ChunkSize, oz = -DiscRadius + cz * ChunkSize;
                    bool Emit(int kind, bool castsShadows)
                    {
                        float x = ox + Next() * ChunkSize, z = oz + Next() * ChunkSize;
                        if (x * x + z * z > DiscRadius * DiscRadius) return false;   // clip the grid to the disc
                        list.Add(new Placement(kind, new Vector3(x, 0f, z), 0.6f + Next() * 0.8f, Next() * 6.28f, castsShadows));
                        return true;
                    }
                    // Alternate small caster and non-caster groups until the chunk's quota is spent. Both counters
                    // advance whether or not the disc clip accepted the placement, so the loop always terminates.
                    int c = 0, o = 0;
                    while (c < castersPerChunk || o < othersPerChunk)
                    {
                        for (int i = 0; i < CasterGroup && c < castersPerChunk; i++, c++)
                            if (casters < CasterInstances && Emit(c % 2, true)) casters++;
                        for (int i = 0; i < NonCasterGroup && o < othersPerChunk; i++, o++)
                            if (others < NonCasterInstances && Emit(o % 2, false)) others++;
                    }
                }
            return list.ToArray();
        }

        static Vector3 SunAt(float extraDegrees)
        {
            float e = SunElevationDegrees * MathF.PI / 180f;
            float a = extraDegrees * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Cos(a) * MathF.Cos(e), -MathF.Sin(e), MathF.Sin(a) * MathF.Cos(e)));
        }

        static double Median(List<double> xs)
        {
            xs.Sort();
            return xs.Count == 0 ? 0 : xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) * 0.5;
        }

        static double Mean(List<double> xs)
        {
            if (xs.Count == 0) return 0;
            double t = 0;
            foreach (double x in xs) t += x;
            return t / xs.Count;
        }

        [GpuFact]
        public void Trace_shaped_scene_re_records_the_whole_atlas_for_a_skinned_caster_and_for_any_sun_movement()
        {
            Placement[] placements = BuildPlacements();
            var configs = new[]
            {
                new Config("shadows off", ShadowMode.Off, false, 0f, true),
                new Config("skinned, sun frozen", ShadowMode.ShadowMap, true, 0f, true),
                new Config("rigid only, sun frozen", ShadowMode.ShadowMap, false, 0f, true),
                new Config("rigid only, daylight sun", ShadowMode.ShadowMap, false, DaylightDegreesPerFrame, true),
                new Config("rigid only, 0.001 deg sun", ShadowMode.ShadowMap, false, SubEpsilonDegreesPerFrame, true),
                new Config("skinned, sun frozen, pipelined", ShadowMode.ShadowMap, true, 0f, false),
            };

            Measurement[] results = MeasureAll(configs, placements);
            Report(placements, results);

            Measurement rerecord = results[1], skip = results[2], daylight = results[3], subEpsilon = results[4];

            // The stationary skinned regime re-records EVERY measured frame, and the only reason it can name is the
            // skinned caster: nothing moved, the sun is frozen, the rigid signature is identical.
            Assert.Equal(Rounds * BlockFrames, rerecord.RenderedFrames);
            Assert.Equal(0, rerecord.SkippedFrames);
            Assert.True(rerecord.SkinnedDraws > 0, "the skinned caster must be drawn into the atlas");

            // Drop the skinned caster and freeze the sun and the same scene stops recording entirely.
            Assert.Equal(Rounds * BlockFrames, skip.SkippedFrames);
            Assert.Equal(0, skip.RenderedFrames);
            Assert.Equal(0, skip.DrawCalls);

            // The sun alone is enough, at Ruinborne's real rate and at a third of that rate alike: the cascade
            // compare is exact matrix equality, so there is no movement small enough to be ignored.
            Assert.Equal(Rounds * BlockFrames, daylight.RenderedFrames);
            Assert.Equal(Rounds * BlockFrames, subEpsilon.RenderedFrames);
            Assert.True(daylight.DrawCalls > 0);
            // The same work for a movement 3.3x smaller. Not byte-equal: the two regimes sit at different sun
            // angles, so the per-cascade cull splits the caster list into a slightly different span set. Within a
            // few percent is the claim, and a real saving would show as a different order of magnitude.
            Assert.InRange(subEpsilon.DrawCalls, (int)(daylight.DrawCalls * 0.9), (int)(daylight.DrawCalls * 1.1));

            // Every caster instance is considered on every rendered pass, and the count is the trace's shape, so a
            // reader can tell at a glance whether this bench measured the field scene or some other one.
            int casters = 0;
            foreach (Placement p in placements) if (p.CastsShadows) casters++;
            Assert.Equal(casters, rerecord.Candidates);
            Assert.InRange(rerecord.Candidates, 1400, 1800);
        }

        Measurement[] MeasureAll(Config[] configs, Placement[] placements)
        {
            var shadows = new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = Resolution,
                ShadowCascadeCount = Cascades,
            };

            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H, shadows);
            Scene3D scene = preview.Scene;
            scene.EnableTiming = true;
            scene.UseGpuSkinning = true;            // the field build's skinning path, so the shadow slot packing counts
            scene.Post.TransparentBackground = false;
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Camera.Azimuth = 0.6f;
            scene.Camera.Elevation = 0.35f;
            scene.Camera.Frame(Vector3.Zero, new Vector3(60f, 20f, 60f));

            MeshHandle ground = scene.LoadMesh(MeshPrimitives.Tile(2f * DiscRadius, 0.2f));
            var kinds = new MeshHandle[2];
            kinds[0] = scene.LoadMesh(MeshPrimitives.Box(0.8f));                 // 12 triangles: the cheapest caster
            kinds[1] = scene.LoadMesh(MeshPrimitives.Cone(0.5f, 2.2f, 6));       // 18 triangles
            var tint = new Color(0.35f, 0.55f, 0.3f, 1f);
            using var limb = new SkinnedLimb(scene, radius: 0.4f, length: 2.5f, ringSegments: 8, radialSegments: 8,
                boneCount: 5, ChainConfig.Writhe, Axis.Z);
            // Solved ONCE, before any frame. The pose is then constant for the whole run, so a re-record can never
            // be blamed on the limb actually moving: the only thing the skinned caster contributes is its presence.
            limb.Update(new Vector3(0f, 1.2f, 0f), Vector3.UnitZ, Vector3.UnitY, 1.0f);

            bool drawSkinned = false;
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
                    s.Draw(kinds[p.Kind], world, tint, Material.None, p.CastsShadows);
                }
                if (drawSkinned) limb.Draw(s, Matrix4x4.CreateTranslation(0f, 1.2f, 0f), new Color(0.8f, 0.4f, 0.3f, 1f));
            }

            int n = configs.Length;
            var shadowSamples = new List<double>[n];
            var frameSamples = new List<double>[n];
            var sunAngles = new float[n];
            var spanSums = new long[n];
            var perCascadeSpans = new int[n][];
            var drawSums = new long[n];
            var skinnedDraws = new int[n];
            var candidates = new int[n];
            var rendered = new int[n];
            var skipped = new int[n];
            for (int i = 0; i < n; i++) { shadowSamples[i] = new List<double>(); frameSamples[i] = new List<double>(); }

            // Rotate the order every round, so no configuration is pinned to one position in the round: position
            // measurably matters on a warm device, and rotating spreads any drift across all of them equally.
            for (int round = 0; round < Rounds; round++)
                for (int k = 0; k < n; k++)
                {
                    int ci = (k + round) % n;
                    Config cfg = configs[ci];
                    scene.Post.Quality.Shadows.Mode = cfg.Mode;
                    drawSkinned = cfg.Skinned;
                    sunDegrees = sunAngles[ci];

                    // Warmup lands the scene in the regime's STEADY state: the first frame of a block always
                    // re-records (the caster set or the sun jumped when the block switched), and measuring that
                    // frame would report every configuration as dirty.
                    for (int i = 0; i < BlockWarmup; i++) { preview.Capture(DrawFrame); sunDegrees += cfg.SunDegreesPerFrame; }
                    gd.WaitForIdle();

                    for (int i = 0; i < BlockFrames; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        preview.Capture(DrawFrame);
                        if (cfg.Drain) gd.WaitForIdle();
                        frameSamples[ci].Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                        shadowSamples[ci].Add(scene.PassTimingsMs.ShadowDepthMs);
                        sunDegrees += cfg.SunDegreesPerFrame;

                        if (cfg.Mode != ShadowMode.ShadowMap) continue;
                        ShadowPassDiagnostics d = scene.LastShadowPassDiagnostics;
                        AssertReasonBits(cfg, d);
                        if (d.Rendered)
                        {
                            rendered[ci]++;
                            // Summed per rendered frame, reduced to a mean below, so the us/span and us/draw columns
                            // divide a 200-frame mean by a 200-frame mean rather than by one frame's count.
                            spanSums[ci] += d.TotalRigidSpanCount;
                            drawSums[ci] += d.TotalDrawCalls;
                            perCascadeSpans[ci] ??= new int[Cascades];
                            for (int c = 0; c < Cascades; c++) perCascadeSpans[ci][c] = d.RigidSpanCount(c);
                            skinnedDraws[ci] = d.SkinnedDrawCalls;
                            candidates[ci] = scene.ShadowCasterCandidateCount;
                        }
                        else skipped[ci]++;
                    }
                    if (!cfg.Drain) gd.WaitForIdle();   // never leave the queue loaded for the next block
                    sunAngles[ci] = sunDegrees;
                }

            var results = new Measurement[n];
            for (int i = 0; i < n; i++)
            {
                int meanSpans = rendered[i] > 0 ? (int)Math.Round((double)spanSums[i] / rendered[i]) : 0;
                int meanDraws = rendered[i] > 0 ? (int)Math.Round((double)drawSums[i] / rendered[i]) : 0;
                results[i] = new Measurement(configs[i].Label, Mean(shadowSamples[i]), Median(shadowSamples[i]),
                    Median(frameSamples[i]), meanSpans, perCascadeSpans[i] ?? new int[Cascades], meanDraws,
                    skinnedDraws[i], candidates[i], rendered[i], skipped[i]);
            }
            return results;
        }

        /// <summary>The stable half of this bench: each regime must name exactly the reason it was built to isolate,
        /// on every measured frame, or the numbers beside it describe a different experiment.</summary>
        static void AssertReasonBits(Config cfg, ShadowPassDiagnostics d)
        {
            Assert.True(d.Active);
            Assert.False(d.ResolutionChanged, "the atlas resolution is a construction-time knob and cannot move here");
            Assert.False(d.CasterDataChanged, "no rigid caster moves in any regime: the signature must compare equal");
            Assert.Equal(cfg.Skinned, d.AnySkinnedCaster);
            Assert.Equal(cfg.SunDegreesPerFrame != 0f, d.LightMatrixChanged);
            bool expectRender = cfg.Skinned || cfg.SunDegreesPerFrame != 0f;
            Assert.Equal(expectRender, d.Rendered);
            Assert.Equal(!expectRender, d.Skipped);
            if (!d.Rendered)
            {
                Assert.Equal(0, d.TotalDrawCalls);
                Assert.Equal(0, d.TotalRigidSpanCount);
            }
        }

        void Report(Placement[] placements, Measurement[] results)
        {
            int casters = 0;
            foreach (Placement p in placements) if (p.CastsShadows) casters++;
            _out.WriteLine($"scene: {placements.Length} instances ({casters} shadow casters) over a {2 * DiscRadius} m disc, " +
                           $"{Cascades} cascades at {Resolution}, sun {SunElevationDegrees} deg, GPU skinning on, " +
                           $"{Rounds} x {BlockFrames} interleaved frames per configuration, {(IsDebug ? "DEBUG" : "RELEASE")} build");
            _out.WriteLine($"daylight sun = {DaylightDegreesPerFrame:0.#####} deg/frame (Ruinborne's 30 minute day at 60 fps), " +
                           $"sub-epsilon sun = {SubEpsilonDegreesPerFrame:0.#####} deg/frame");
            _out.WriteLine("");
            _out.WriteLine("spans and draws are means over the regime's rendered frames, so us/span and us/draw " +
                           "divide one reduction by another");
            _out.WriteLine($"{"configuration",-32} {"shadow mean",11} {"shadow med",10} {"frame med",9} {"spans",6} {"draws",6} {"us/span",8} {"us/draw",8} {"rendered",8} {"skipped",7}");
            foreach (Measurement m in results)
                _out.WriteLine($"{m.Label,-32} {m.ShadowMeanMs,11:0.000} {m.ShadowMedianMs,10:0.000} {m.FrameMedianMs,9:0.000} " +
                               $"{m.Spans,6} {m.DrawCalls,6} {m.MicrosecondsPerSpan,8:0.00} {m.MicrosecondsPerDraw,8:0.00} " +
                               $"{m.RenderedFrames,8} {m.SkippedFrames,7}");

            Measurement noShadows = results[0], rerecord = results[1], skip = results[2];
            Measurement daylight = results[3], subEpsilon = results[4], pipelined = results[5];
            _out.WriteLine("");
            foreach (Measurement m in results)
                if (m.RenderedFrames > 0)
                    _out.WriteLine($"{m.Label}: spans per cascade, last rendered frame [{string.Join(", ", m.PerCascadeSpans)}] " +
                                   $"(the field trace's D3D11 stationary window: 45, 153, 221, 285)");
            _out.WriteLine("");
            _out.WriteLine($"candidates considered per rendered pass: {rerecord.Candidates}");
            _out.WriteLine($"whole-frame cost of the shadow tier: skipping frame {skip.FrameMedianMs - noShadows.FrameMedianMs:0.000} ms, " +
                           $"re-recording frame {rerecord.FrameMedianMs - noShadows.FrameMedianMs:0.000} ms (over shadows off)");
            _out.WriteLine($"the re-record itself: {rerecord.ShadowMeanMs - skip.ShadowMeanMs:0.000} ms/frame of encode a skip does not pay " +
                           $"({rerecord.ShadowMeanMs:0.000} vs {skip.ShadowMeanMs:0.000})");
            _out.WriteLine($"sun movement size does not change the price: daylight {daylight.ShadowMeanMs:0.000} ms, " +
                           $"a thousandth of a degree {subEpsilon.ShadowMeanMs:0.000} ms, same {subEpsilon.DrawCalls} draws");
            _out.WriteLine($"CPU or queue: drained {rerecord.ShadowMeanMs:0.000} ms vs pipelined {pipelined.ShadowMeanMs:0.000} ms encode " +
                           $"({(rerecord.ShadowMeanMs > 0 ? 100.0 * (pipelined.ShadowMeanMs - rerecord.ShadowMeanMs) / rerecord.ShadowMeanMs : 0):0.0} percent), " +
                           $"frame {rerecord.FrameMedianMs:0.000} vs {pipelined.FrameMedianMs:0.000} ms");
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
