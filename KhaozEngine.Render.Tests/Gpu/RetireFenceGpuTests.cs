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
    /// process is not comparable. The asserted claim is the drain COUNT, which is deterministic. The milliseconds
    /// are printed, not gated, because a shared dev machine's timings are not a stable gate.</para>
    /// <para>Gated on KE_GPU_TESTS. This leg runs on Metal locally. The Direct3D11/WARP and Vulkan/lavapipe legs
    /// run in CI on tag, and lavapipe is the one that matters for the crash: until it runs there, the Vulkan fence
    /// path is unproven and only the argument plus the Metal evidence stands behind it. Direct3D11 never takes the
    /// fence path at all (its Veldrid fence is a CPU submit receipt), so it stays on the fallback the
    /// suppressed-capability leg here exercises.</para>
    /// </summary>
    public sealed class RetireFenceGpuTests
    {
        const int W = 320, H = 200;
        const int Frames = 400;          // hundreds of frames of load-draw-unload
        const int MeshesPerFrame = 6;

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
        (int Drains, int FencedSubmits, double Ms, int PeakPending) Churn(IGpuDevice inner, bool suppressFences)
        {
            var spy = new SpyGpuDevice(inner, suppressFences);
            using var preview = new Render3DPreview(spy, W, H);
            Scene3D scene = preview.Scene;
            scene.Post.Starfield = false;
            scene.Camera.Frame(Vector3.Zero, new Vector3(6f, 6f, 6f));

            var previous = new List<MeshHandle>();
            var current = new List<MeshHandle>();
            int peak = 0;

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
            }

            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            int drains = spy.WaitForIdleCalls - drainsBefore;
            int fenced = spy.FencedSubmitCalls - fencedBefore;

            // Settle: unload the tail and run the pool until it is empty, so "drains to zero at idle" is a real
            // assertion and not a snapshot taken mid-burst.
            foreach (MeshHandle h in previous) scene.UnloadMesh(h);
            for (int i = 0; i < 300 && scene.RetiredResourceCount > 0; i++)
            {
                preview.Capture(_ => { });
                if (scene.RetiredResourceCount > 0) System.Threading.Thread.Sleep(1);
            }
            Assert.Equal(0, scene.RetiredResourceCount);

            return (drains, fenced, ms, peak);
        }

        [GpuFact]
        public void Mesh_churn_survives_hundreds_of_frames_and_the_fence_path_removes_every_drain()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            Assert.True(gd.Capabilities.SupportsCompletionFences,
                $"the {gd.Backend} device reports no GPU-completion fence, so this test is measuring the wrong thing");
            // The two surfaces that publish capabilities must agree: they are read through one function for exactly
            // this reason, and this flag is the newest member to have been able to drift.
            Assert.Equal(gd.Capabilities.SupportsCompletionFences, ctx.Capabilities.SupportsCompletionFences);

            // Round-robin the two policies so machine drift under the run lands on both equally.
            var fenced = new List<double>();
            var fallback = new List<double>();
            int fencedDrains = -1, fallbackDrains = -1, fencedSubmits = -1, fencedPeak = 0, fallbackPeak = 0;
            for (int round = 0; round < 3; round++)
            {
                var a = Churn(gd, suppressFences: false);
                var b = Churn(gd, suppressFences: true);
                fenced.Add(a.Ms); fallback.Add(b.Ms);
                fencedDrains = a.Drains; fallbackDrains = b.Drains; fencedSubmits = a.FencedSubmits;
                fencedPeak = Math.Max(fencedPeak, a.PeakPending);
                fallbackPeak = Math.Max(fallbackPeak, b.PeakPending);
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

            // The gate is the drain count, which is deterministic. Sustained churn retires on every frame, so the
            // fallback pays a drain on very nearly every frame boundary once the delay is warm, and the fence path
            // pays none at all.
            Assert.Equal(0, fencedDrains);
            Assert.True(fallbackDrains > Frames / 2,
                $"expected the unfenced fallback to drain on most frames of sustained churn, got {fallbackDrains} of {Frames}");
            // One fenced submission per frame that retired something, and not one per retired resource.
            Assert.True(fencedSubmits <= Frames + 2,
                $"expected at most one fenced submission per frame, got {fencedSubmits} over {Frames} frames");
        }
    }
}
