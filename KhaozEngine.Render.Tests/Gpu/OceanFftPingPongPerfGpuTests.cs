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
    /// The measurement harness behind the FFT ocean's cross-frame ping-pong
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/398">#398</see>): open water at the shipping sea
    /// state (three cascades at 128), rendered frame after frame with the wave clock advancing at 60 fps, timing
    /// blocks of frames for throughput and reading the ocean's own stall counters off
    /// <see cref="Scene3D.LastWaterStats"/>.
    /// <para>
    /// The A/B is in-process and interleaved, for the same reason
    /// <c>ShadowCascadeCullPerfGpuTests</c> is: a number measured against a different build is not comparable, and
    /// the machine drifts under a long run. The PROCEDURAL row is the isolation baseline - the same scene, the same
    /// draw, the same post chain, with the only difference being that no plane reads the cascades - so the
    /// difference between the rows is what the ocean actually costs.
    /// </para>
    /// <para>
    /// The assertion is on the STALL COUNT, not on a millisecond budget: the wall clock on a shared dev machine is
    /// not a stable gate, but "no frame in the steady state drains the device" is a structural claim that either
    /// holds or does not. The milliseconds are printed for the release note.
    /// </para>
    /// Gated on KE_GPU_TESTS.
    /// </summary>
    public sealed class OceanFftPingPongPerfGpuTests
    {
        const int W = 960, H = 600;
        const float FrameSeconds = 1f / 60f;
        // Interleaved round-robin blocks, reduced by median over the BLOCKS: the same shape (and the same reasons)
        // as the shadow cull harness, at a fraction of the frames, because the quantity here is one dispatch pair
        // rather than a whole depth pass over 16k casters.
        const int Rounds = 15, BlockWarmup = 2, BlockFrames = 12;

        readonly ITestOutputHelper _out;
        public OceanFftPingPongPerfGpuTests(ITestOutputHelper o) => _out = o;

        readonly record struct Row(string Label, double FrameMs, double StallMs, double WorstStallMs, int Stalls);

        static double Median(List<double> xs)
        {
            xs.Sort();
            return xs.Count == 0 ? 0 : xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) * 0.5;
        }

        [GpuFact]
        public void The_fft_ocean_costs_no_steady_state_gpu_drain()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            Assert.True(gd.Capabilities.SupportsCompute, $"{gd.Backend} reports no compute support");

            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;
            scene.EnableTiming = true;
            scene.Post.Starfield = false;
            scene.Post.TransparentBackground = false;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Sky.Enabled = true;
            scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
            // Open water at metre scale, seabed well below: the surface is the whole subject, exactly as the FFT
            // golden frames it, because a doll-house pond carries centimetre waves and measures nothing.
            MeshHandle seabed = scene.LoadMesh(MeshPrimitives.Tile(160f, 1f));
            scene.Camera.Frame(Vector3.Zero, new Vector3(46f, 30f, 46f));

            var sources = new[] { WaterWaveSource.FftOcean, WaterWaveSource.Procedural };
            var frameSamples = new List<double>[sources.Length];
            var stallSamples = new List<double>[sources.Length];
            var stalls = new int[sources.Length];
            for (int i = 0; i < sources.Length; i++) { frameSamples[i] = new List<double>(); stallSamples[i] = new List<double>(); }

            float time = 0f;
            void DrawFrame(Scene3D s)
            {
                s.EffectTimeSeconds = time;
                s.Draw(seabed, Matrix4x4.CreateTranslation(0f, -12f, 0f), new Color(0.18f, 0.20f, 0.18f, 1f));
                s.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 70f));
            }

            // Rotate the order every round, so no configuration is pinned to one position in the block (the shadow
            // harness found position itself measurable).
            for (int round = 0; round < Rounds; round++)
                for (int k = 0; k < sources.Length; k++)
                {
                    int ci = (k + round) % sources.Length;
                    scene.Post.Water.WaveSource = sources[ci];
                    // The warm-up frames are also what absorbs the priming drain that a wave source switch costs,
                    // so the measured block is the steady state and nothing else.
                    for (int i = 0; i < BlockWarmup; i++) { preview.Capture(DrawFrame); time += FrameSeconds; }
                    gd.WaitForIdle();
                    // The BLOCK is timed, not each frame, and that is the whole point of the measurement: a
                    // WaitForIdle per frame serializes the CPU against the GPU exactly as the mid-frame drain used
                    // to, so a per-frame stopwatch would measure the frame with the bubble reinstated by the
                    // harness. Submitting a run of frames back to back and draining once at the end is what a real
                    // frame loop does, and it is the only shape in which removing a pipeline bubble can show up.
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < BlockFrames; i++)
                    {
                        preview.Capture(DrawFrame);
                        WaterFrameStats water = scene.LastWaterStats;
                        stallSamples[ci].Add(water.OceanStallMs);
                        stalls[ci] += water.OceanStalls;
                        time += FrameSeconds;
                    }
                    gd.WaitForIdle();
                    frameSamples[ci].Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / BlockFrames);
                }

            var rows = new Row[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                double worst = 0;
                foreach (double ms in stallSamples[i]) worst = Math.Max(worst, ms);
                rows[i] = new Row(sources[i].ToString(), Median(frameSamples[i]), Median(stallSamples[i]), worst, stalls[i]);
            }

            _out.WriteLine($"open water {W}x{H}, 3 cascades at 128, {Rounds} x {BlockFrames} interleaved frames per " +
                           $"wave source (median), {(IsDebug ? "DEBUG" : "RELEASE")} build, {gd.Backend}");
            _out.WriteLine("");
            _out.WriteLine($"{"wave source",-12} {"frame ms",9} {"stall ms",9} {"worst stall",12} {"stalls",7}");
            foreach (Row r in rows)
                _out.WriteLine($"{r.Label,-12} {r.FrameMs,9:0.000} {r.StallMs,9:0.000} {r.WorstStallMs,12:0.000} {r.Stalls,7}");
            _out.WriteLine("");
            _out.WriteLine($"ocean over procedural: {rows[0].FrameMs - rows[1].FrameMs:0.000} ms/frame " +
                           $"over {Rounds * BlockFrames} measured frames each");

            Assert.Equal(0, rows[0].Stalls);
            Assert.Equal(0d, rows[0].WorstStallMs);
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
