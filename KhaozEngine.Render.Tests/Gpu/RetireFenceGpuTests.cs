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
    /// The fence-polled retirement path on a real device. Two things are under test and they are different
    /// questions.
    /// <para><b>Safety.</b> The mesh churn below is the shape that crashed Mesa lavapipe before any of this existed
    /// (8c2a6c6b): a mesh is drawn into a frame, that frame is submitted, and the mesh is unloaded while its
    /// command buffer may still be executing. Hundreds of frames of it must not fault, and the pool must come back
    /// to zero once the churn stops. A leak here is the fence never signaling, which would be as bad as freeing
    /// early in a different direction.</para>
    /// <para><b>Cost.</b> The same churn is then measured twice on ONE device in ONE process, once with the fence
    /// path and once with the capability suppressed so the frame-count-plus-drain fallback runs. In-process A/B for
    /// the reason ShadowCascadeCullPerfGpuTests gives: a number measured against a different build or a different
    /// process is not comparable. The asserted claim is the drain COUNT. The milliseconds are printed, not gated,
    /// because a shared dev machine's timings are not a stable gate.</para>
    /// <para><b>The drain count is no longer asserted as zero</b>, because since
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/425">#425</see> it is not device-independent. The
    /// queue's safety valve pays one drain past its sealed-batch cap, which is what stops a CPU running away from
    /// its GPU growing the holding without bound. What is asserted instead is that every drain came from the valve,
    /// that the cap held at every frame boundary, and that the valve fired no more often than its own period
    /// allows. See the gate at the bottom of this file.</para>
    /// <para><b>And the valve fires HERE, on an M2 Max, which is worth knowing before reading the number.</b> The
    /// churn below runs through <c>Render3DPreview.Capture</c>, which submits a frame and returns: there is no
    /// swapchain and no present, so nothing throttles the CPU at all and it runs eight or nine frames ahead of the
    /// GPU on hardware that is in no way struggling. The measured run is 16 valve drains over 400 frames against
    /// the fallback's 396, and a peak holding that sits exactly on the cap. A windowed game does not look like
    /// this: its present blocks at the backend's frames-in-flight depth (default 3), which is half the cap, so the
    /// valve stays shut and the drain count really is zero. So a non-zero reading here is the instrument working,
    /// and the shape to be alarmed by is a peak holding ABOVE the cap, which is what the gate checks.</para>
    /// <para>Gated on KE_GPU_TESTS. This leg runs on Metal locally. The Direct3D11/WARP and Vulkan/lavapipe legs
    /// run in CI on tag, and lavapipe is the one that matters for the crash: until it runs there, the Vulkan fence
    /// path is unproven and only the argument plus the Metal evidence stands behind it. WHICH DIRECT3D 11 BACKEND
    /// it is decides what happens on that WARP leg, and the two differ. The incumbent
    /// <c>GpuBackendKind.Direct3D11</c> never takes the fence path at all (its Veldrid fence is a CPU submit
    /// receipt), so it stays on the fallback the suppressed-capability leg here exercises and the fenced test
    /// skips by capability. The engine's own <c>Direct3D11Native</c> backend reports completion fences on both
    /// its timeline mechanisms (decision C5), so on a process that came up on it the fenced test RUNS, which is
    /// part of what https://github.com/APKiwiOrg/KhaozEngine/issues/460 turns on.</para>
    /// </summary>
    public sealed class RetireFenceGpuTests
    {
        const int W = 320, H = 200;
        const int Frames = 400;          // hundreds of frames of load-draw-unload
        const int MeshesPerFrame = 6;
        // How long the post-churn settle may take before a still-populated pool is called a leak. Generous on
        // purpose: it bounds a hang, it does not gate performance. A software rasterizer draining ~400 sealed
        // batches one completed frame at a time is the slowest legitimate case, and it is minutes below this.
        const int SettleTimeoutMs = 60_000;

        readonly ITestOutputHelper _out;
        public RetireFenceGpuTests(ITestOutputHelper o) => _out = o;

        static GltfMesh Blade(int seed)
        {
            // A handful of distinct little meshes so no two frames retire byte-identical buffers, and so the
            // allocator is actually churned rather than handing the same block back every time.
            int rings = 6 + seed % 5;
            return MeshPrimitives.Sphere(0.5f + seed % 3 * 0.25f, rings, rings + 4);
        }

        // One churn run: every frame loads a fresh set of meshes, draws them, then unloads the PREVIOUS frame's set
        // (whose buffers the just-submitted command list referenced). Returns the drains it cost and how long it
        // took. Everything is driven through Render3DPreview so the frame boundary and the submit are the real ones.
        (int Drains, int FencedSubmits, double Ms, int PeakPending, int PeakBatches, int ValveDrains) Churn(
            IGpuDevice inner, bool suppressFences)
        {
            var spy = new SpyGpuDevice(inner, suppressFences);
            using var preview = new Render3DPreview(spy, W, H);
            Scene3D scene = preview.Scene;
            scene.Post.Starfield = false;
            scene.Camera.Frame(Vector3.Zero, new Vector3(6f, 6f, 6f));

            var previous = new List<MeshHandle>();
            var current = new List<MeshHandle>();
            int peak = 0, peakBatches = 0;

            // Warm up outside the measurement: the first frames pay pipeline creation and JIT.
            for (int i = 0; i < 10; i++) preview.Capture(_ => { });
            inner.WaitForIdle();

            int drainsBefore = spy.WaitForIdleCalls;
            int fencedBefore = spy.FencedSubmitCalls;
            long t0 = Stopwatch.GetTimestamp();

            for (int frame = 0; frame < Frames; frame++)
            {
                current.Clear();
                for (int i = 0; i < MeshesPerFrame; i++) current.Add(scene.LoadMesh(Blade(frame * MeshesPerFrame + i)));

                List<MeshHandle> drawn = current;
                preview.Capture(s =>
                {
                    for (int i = 0; i < drawn.Count; i++)
                        s.Draw(drawn[i], Matrix4x4.CreateTranslation(new Vector3(i * 1.5f - 4f, 0f, 0f)), Color.White);
                });

                // Retire what the frame just submitted was drawing. This is the hazard, not a contrived one: it is
                // exactly what the terrain streamer does when a chunk leaves the ring.
                foreach (MeshHandle h in previous) scene.UnloadMesh(h);
                previous.Clear();
                previous.AddRange(current);

                if (scene.RetiredResourceCount > peak) peak = scene.RetiredResourceCount;
                // Sampled at the frame boundary, which is where the valve's bound is stated: after a BeginFrame has
                // returned, the queue holds at most MaxSealedBatches batches (#425). Sampling anywhere else would
                // be sampling the middle of the sweep.
                if (scene.RetiredBatchCount > peakBatches) peakBatches = scene.RetiredBatchCount;
            }

            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            int drains = spy.WaitForIdleCalls - drainsBefore;
            int fenced = spy.FencedSubmitCalls - fencedBefore;
            int valve = scene.RetireValveDrains;

            // Settle: unload the tail and run the pool until it is empty, so "drains to zero at idle" is a real
            // assertion and not a snapshot taken mid-burst. Every poll WAITS for the frame it just submitted.
            // Without that the loop submits a full scene render per iteration and blocks on nothing, so on a device
            // whose frame costs more than the iteration does (lavapipe, roughly 10x) the queue depth grows
            // monotonically and no fixed iteration count ever catches up. That is what read as a dead fence on the
            // Vulkan leg in #423 while the fence path was in fact working - it had freed exactly the batches whose
            // frames had completed. The bound is wall clock for the same reason: an iteration is not a unit of time
            // on an unknown device. A genuine leak (a fence that never signals) frees nothing however long it runs,
            // so it still fails here, just at the deadline instead of on iteration 300.
            foreach (MeshHandle h in previous) scene.UnloadMesh(h);
            long settleStart = Stopwatch.GetTimestamp();
            double SettleMs() => (Stopwatch.GetTimestamp() - settleStart) * 1000.0 / Stopwatch.Frequency;
            while (scene.RetiredResourceCount > 0 && SettleMs() < SettleTimeoutMs)
            {
                preview.Capture(_ => { });
                inner.WaitForIdle();   // the raw device, so this drain is not one the spy counts (already sampled)
            }
            Assert.True(scene.RetiredResourceCount == 0,
                $"the retired pool still holds {scene.RetiredResourceCount} resources after {SettleMs():F0} ms of "
                + "settling with a device drain per poll, so the retirement fence never signaled");

            return (drains, fenced, ms, peak, peakBatches, valve);
        }

        // Fences are a Vulkan and Metal capability (VeldridMap), so this SKIPS rather than fails on a backend
        // without them: asserting a capability the device cannot have is a red test for a feature that was never
        // claimed (#423). The two surfaces that publish it must still agree, which is asserted below.
        [GpuFact(RequiresCompletionFences = true)]
        public void Mesh_churn_survives_hundreds_of_frames_and_the_fence_path_drains_only_at_the_valve()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            // They are read through one function for exactly this reason, and this flag is the newest member to
            // have been able to drift.
            Assert.Equal(gd.Capabilities.SupportsCompletionFences, ctx.Capabilities.SupportsCompletionFences);

            // Round-robin the two policies so machine drift under the run lands on both equally.
            var fenced = new List<double>();
            var fallback = new List<double>();
            int fencedDrains = -1, fallbackDrains = -1, fencedSubmits = -1, fencedPeak = 0, fallbackPeak = 0;
            int fencedValveDrains = -1, fencedPeakBatches = 0;
            for (int round = 0; round < 3; round++)
            {
                var a = Churn(gd, suppressFences: false);
                var b = Churn(gd, suppressFences: true);
                fenced.Add(a.Ms); fallback.Add(b.Ms);
                fencedDrains = a.Drains; fallbackDrains = b.Drains; fencedSubmits = a.FencedSubmits;
                fencedValveDrains = a.ValveDrains;
                fencedPeak = Math.Max(fencedPeak, a.PeakPending);
                fallbackPeak = Math.Max(fallbackPeak, b.PeakPending);
                fencedPeakBatches = Math.Max(fencedPeakBatches, a.PeakBatches);
            }

            fenced.Sort(); fallback.Sort();
            double fencedMs = fenced[fenced.Count / 2], fallbackMs = fallback[fallback.Count / 2];

            _out.WriteLine($"{Frames} frames x {MeshesPerFrame} meshes loaded, drawn and unloaded, on {gd.Backend} " +
                           $"({gd.Capabilities.DeviceName}), median of {fenced.Count} interleaved rounds");
            _out.WriteLine($"{"policy",-24} {"drains",8} {"fenced submits",16} {"total ms",10} {"ms/frame",10} {"peak held",10}");
            _out.WriteLine($"{"fence poll (shipped)",-24} {fencedDrains,8} {fencedSubmits,16} {fencedMs,10:0.00} " +
                           $"{fencedMs / Frames,10:0.000} {fencedPeak,10}");
            _out.WriteLine($"{"frame count + drain",-24} {fallbackDrains,8} {0,16} {fallbackMs,10:0.00} " +
                           $"{fallbackMs / Frames,10:0.000} {fallbackPeak,10}");
            _out.WriteLine($"drains removed: {fallbackDrains} -> {fencedDrains}, " +
                           $"frame cost {fallbackMs / Frames:0.000} -> {fencedMs / Frames:0.000} ms/frame");
            _out.WriteLine($"peak sealed batches {fencedPeakBatches} of a {GpuRetireQueue.DefaultMaxSealedBatches} " +
                           $"cap, valve drains {fencedValveDrains}");

            // THE GATE, and it is written against the safety valve rather than against a flat zero (#425).
            //
            // A flat zero was the original assertion and it stopped being true the moment the holding got a bound.
            // The fence path holds a batch until its fence signals, so a CPU that outruns its GPU used to grow the
            // holding with no limit at all. Bounding it means the queue trades the poll for one drain past
            // MaxSealedBatches batches, which makes the drain count a property of how far ahead the CPU gets, and
            // this loop gets a long way ahead on any device because nothing here presents (see the class remark:
            // 16 drains over 400 frames on an M2 Max). Asserting zero would fail the test for doing its job.
            //
            // So three claims that hold on any device, and together say what zero used to say:
            //
            // 1. Every drain on this path came from the valve. Nothing else on the fence path stalls the CPU, which
            //    is the whole claim the fence path makes, and it is the half of the original assertion that was
            //    actually about the code rather than about the machine. The printed line above records the count
            //    for whichever device ran it.
            Assert.Equal(fencedValveDrains, fencedDrains);
            // 2. The bound held at every frame boundary of the churn. This is the assertion that goes red if the
            //    valve stops working, on the exact device where it matters.
            Assert.True(fencedPeakBatches <= GpuRetireQueue.DefaultMaxSealedBatches,
                $"the retire queue held {fencedPeakBatches} sealed batches, past its cap of "
                + $"{GpuRetireQueue.DefaultMaxSealedBatches}, so the bound is not holding");
            // 3. And the valve cannot have fired more often than its own period allows: it frees the WHOLE holding,
            //    so the count has to climb from zero again before it can fire once more. Off by one for the boundary
            //    the run happens to start on.
            int valveCeiling = Frames / (GpuRetireQueue.DefaultMaxSealedBatches + 1) + 1;
            Assert.True(fencedDrains <= valveCeiling,
                $"expected at most {valveCeiling} valve drains over {Frames} frames "
                + $"(one per {GpuRetireQueue.DefaultMaxSealedBatches + 1}), got {fencedDrains}");
            // The fallback is unchanged: sustained churn retires on every frame, so it drains on very nearly every
            // frame boundary once the delay is warm. That gap is what the fence path bought.
            Assert.True(fallbackDrains > Frames / 2,
                $"expected the unfenced fallback to drain on most frames of sustained churn, got {fallbackDrains} of {Frames}");
            Assert.True(fencedDrains * 4 < fallbackDrains,
                $"expected the fence path to drain far less than the fallback, got {fencedDrains} against {fallbackDrains}");
            // One fenced submission per frame that retired something, and not one per retired resource.
            Assert.True(fencedSubmits <= Frames + 2,
                $"expected at most one fenced submission per frame, got {fencedSubmits} over {Frames} frames");
        }
    }
}
