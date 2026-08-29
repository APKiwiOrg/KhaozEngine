using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ROLLOUT GATE MM1's THREE NUMBERS, on a real native Metal device
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/566">#566</see>): record-time
    /// <c>UpdateBuffer</c> calls per frame, encoder boundaries per frame, and record-time buffer allocations per
    /// frame, on the streamed-world scene the
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/410">#410</see> report describes.
    ///
    /// <para><b>MM1 WAS AN A/B AND IS NOW THE NATIVE HALF ALONE.</b> Its method named the incumbent Veldrid Metal
    /// backend as the first measurement and the native backend as the second. That backend was deleted in 18.0.0
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/687">#687</see>), so the first half can never
    /// be taken, and it is recorded as unmeasurable rather than skipped. What a gate can still honestly read is
    /// what this row takes: the absolute numbers, against MM1's own pass criterion, which states two of the three
    /// as absolutes anyway.</para>
    ///
    /// <para><b>THE CRITERION, IN THE DESIGN'S OWN WORDS.</b> Encoder boundaries per frame at or below the
    /// framebuffer-change count plus the compute and blit passes the frame genuinely needs, record-time
    /// allocations at zero, and frame time no worse than gate 4's baseline. The first two are what this row
    /// asserts. The third is a windowed field reading and was taken on 2026-08-11 (section 17's gate 4 table), so
    /// it is not restated here: the millisecond column below is a headless secondary signal on reduced geometry,
    /// reported and never asserted.</para>
    ///
    /// <para><b>BOTH SKINNING PATHS ARE MEASURED, BECAUSE ONE OF THE THREE NUMBERS MOVES BETWEEN THEM.</b> The
    /// CPU-skinning path re-uploads every deformed vertex every frame, which at this scene's crowd size is about
    /// 21 MB in ONE record-time upload. That is over
    /// <see cref="MetalStagingArena.DefaultRetentionBytes"/>, so the arena releases the block it took for it
    /// rather than pooling it, and the next frame takes a fresh one: an allocation per frame, by the retention
    /// cap's own design rather than against it. <see cref="Scene3D.UseGpuSkinning"/> replaces that stream with
    /// bone palettes and the allocation goes away. Reporting only the second would be picking the configuration
    /// that passes, and reporting only the first would read as a backend defect when it is the renderer's stream
    /// size meeting a documented cap.</para>
    ///
    /// <para><b>NO COUNTER WAS ADDED TO THE SEAM TO TAKE THIS, WHICH IS DECISION M-G6 HELD RATHER THAN WORKED
    /// AROUND.</b> The encoder-boundary figure comes off <see cref="MetalEncoderScope.Epoch"/>, which counts every
    /// transition by construction (each begin and each end), so a recording's boundary count is its epoch delta
    /// with the <c>BeginRecording</c> bump taken off. The allocation figure comes off
    /// <see cref="MetalStagingArena.BlocksCreated"/>, which is every native buffer this backend can allocate
    /// during a recording: a uniform write lands in the pre-allocated ring and allocates nothing at all. The
    /// upload figure comes off <see cref="CommandTallyGpuCommandList"/> at the seam, which is where "record-time
    /// <c>UpdateBuffer</c> call" is defined.</para>
    ///
    /// <para><b>IT DRIVES ONE COMMAND LIST PER FRAME</b>, which is the shape gate 4's own session note records
    /// for a streaming frame, and it reuses that one list across every frame so the steady state being measured
    /// is a list whose records and arena blocks have already grown. Gated on KE_GPU_TESTS, dormant off
    /// macOS.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalRecordCostGpuTests
    {
        const int W = 640, H = 400;

        /// <summary>The crowd size that reproduces the field reading, per the sibling upload row.</summary>
        const int Characters = 24;

        /// <summary>Frames thrown away before anything is read, and frames read after them. Two is what the
        /// sibling row discards and what the frozen-count row primes with: buffers grow to their working size and
        /// the shadow atlas is built on the first, and the second is already the steady state. A third is taken
        /// here because this row reads an ALLOCATION count, and a block the arena creates once and then recycles
        /// forever would otherwise be attributed to the steady state.</summary>
        const int Warmup = 3, Frames = 5;

        readonly ITestOutputHelper _out;
        public MetalRecordCostGpuTests(ITestOutputHelper o) => _out = o;

        /// <summary>What one recording of the scene cost, in the three currencies MM1 names plus the four the
        /// criterion is read against.</summary>
        readonly record struct Recording(
            int UpdateBuffers, int EncoderBoundaries, int EncodersOpened, int Allocations,
            int FramebufferBinds, int Dispatches, int Copies, int BlitPasses, int DrawIndexed, double FrameMs)
        {
            /// <summary>MM1's own bound on the left-hand side: the framebuffer-change count plus the compute and
            /// blit passes the frame genuinely needs.</summary>
            internal int EncoderBound => FramebufferBinds + Dispatches + Copies + BlitPasses;
        }

        [GpuFact]
        public void TheStreamedWorldFrameRecordsMm1sThreeCounts()
        {
            // Inline rather than through a helper, for the reason MetalCountersAndHeaderGpuTests gives: this is a
            // [SupportedOSPlatformGuard] and everything below records against macOS-only members.
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            var device = (MetalGpuDevice)context.GpuDevice;

            using IGpuTexture target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = device.Factory.CreateFramebuffer(null, target);
            using var scene = new Scene3D(device, fb.Outputs, StreamedWorldSceneContent.Shadows());
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            StreamedWorldSceneContent.FrameCamera(scene);
            var content = new StreamedWorldSceneContent(scene);

            // ONE LIST FOR EVERY FRAME, reused, because that is the frame shape and because a fresh list per
            // frame would bring a fresh arena with it and report every recycled block as an allocation.
            using MetalCommandList list = device.CreateCommandList();
            var tally = new CommandTallyGpuCommandList(list);

            List<Recording> gpuSkinned = Measure(device, scene, content, fb, list, tally, gpuSkinning: true);
            List<Recording> cpuSkinned = Measure(device, scene, content, fb, list, tally, gpuSkinning: false);

            Report(scene, gpuSkinned, cpuSkinned);

            // THE PATH A CLIENT RUNNING THIS SCENE SHOULD BE ON, and the one MM1's criterion is read against:
            // every number at or inside its bound, and the allocation count at the zero MM1 states.
            foreach (Recording r in gpuSkinned)
            {
                Assert.True(r.Allocations == 0,
                    $"a steady-state GPU-skinned frame allocated {Count(r.Allocations)} staging blocks at record "
                    + "time, and MM1's criterion states native record-time allocations at zero. An allocation "
                    + "here is a native MTLBuffer created inside the recording.");
                AssertEncoderBound(r, "GPU-skinned");
            }

            // AND THE PATH THAT REPRODUCES THE FIELD READING, where the one allocation is the arena's retention
            // cap meeting a per-frame upload bigger than it, which is the cap working rather than failing. What
            // must hold is that it is BOUNDED: one block per frame, flat, not a count that climbs.
            foreach (Recording r in cpuSkinned)
            {
                Assert.True(r.Allocations <= 1,
                    $"a steady-state CPU-skinned frame allocated {Count(r.Allocations)} staging blocks at record "
                    + "time. One is the deformed-vertex stream exceeding the arena's retention cap, which is "
                    + "documented; more than one is a pooling failure.");
                AssertEncoderBound(r, "CPU-skinned");
            }

            AssertSteady(gpuSkinned, "GPU-skinned");
            AssertSteady(cpuSkinned, "CPU-skinned");

            Assert.False(scene.ShadowPassSkippedLastFrame,
                "the measured frames must have rendered the depth pass, or the shadow tier's share of every "
                + "count above is missing and the reading is of a cheaper frame than the one #410 reports.");
        }

        static void AssertEncoderBound(Recording r, string path)
            => Assert.True(r.EncodersOpened <= r.EncoderBound,
                $"a {path} frame opened {Count(r.EncodersOpened)} encoders against MM1's bound of "
                + $"{Count(r.EncoderBound)} ({Count(r.FramebufferBinds)} framebuffer changes + "
                + $"{Count(r.Dispatches)} dispatches + {Count(r.Copies)} copies + {Count(r.BlitPasses)} blit "
                + "passes the frame genuinely needs). Every encoder past that bound is a split something inside "
                + "the recording asked for, which is the cost class MM1 is about.");

        /// <summary>
        /// The steady state is steady, per configuration. A frame-on-frame drift in any of these is the unbounded
        /// shape a single reading cannot see, and it is the one pathology an absolute number can still be wrong
        /// about. It also carries the claim MM1's premise turns on: the uploads do not scale with the DRAWS, so
        /// the cost class the ring removes cannot be dominant here whatever its per-write price was.
        /// </summary>
        static void AssertSteady(List<Recording> steady, string path)
        {
            Recording first = steady[0];
            foreach (Recording r in steady)
            {
                Assert.Equal(first.UpdateBuffers, r.UpdateBuffers);
                Assert.Equal(first.EncoderBoundaries, r.EncoderBoundaries);
                Assert.Equal(first.Allocations, r.Allocations);
                Assert.Equal(first.FramebufferBinds, r.FramebufferBinds);

                // EVERY BOUNDARY IS HALF OF A PAIR, which is what licenses the halving. An odd count would mean
                // an encoder was left open across the End, and every encoder number here would be off by one.
                Assert.Equal(r.EncodersOpened * 2, r.EncoderBoundaries);
            }

            Assert.True(first.UpdateBuffers < first.DrawIndexed,
                $"a {path} frame recorded {Count(first.UpdateBuffers)} record-time uploads against "
                + $"{Count(first.DrawIndexed)} indexed draws, so the upload count is tracking the draw count "
                + "rather than the pass count.");
        }

        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        static List<Recording> Measure(MetalGpuDevice device, Scene3D scene, StreamedWorldSceneContent content,
            IGpuFramebuffer fb, MetalCommandList list, CommandTallyGpuCommandList tally, bool gpuSkinning)
        {
            scene.UseGpuSkinning = gpuSkinning;

            var steady = new List<Recording>();
            for (int frame = 0; frame < Warmup + Frames; frame++)
            {
                Recording recording = RecordOneFrame(device, scene, content, fb, list, tally);
                if (frame >= Warmup) steady.Add(recording);
            }

            return steady;
        }

        /// <summary>
        /// Record and submit ONE frame, reading the three counts across the recording alone. The device is
        /// drained after the submit so the millisecond column is a whole frame rather than the CPU's half of one,
        /// and so a segment claim never waits on a frame this row is timing.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        static Recording RecordOneFrame(MetalGpuDevice device, Scene3D scene, StreamedWorldSceneContent content,
            IGpuFramebuffer fb, MetalCommandList list, CommandTallyGpuCommandList tally)
        {
            scene.Begin();
            content.Draw(scene, Characters, StreamedWorldSceneContent.ChunkMeshes);

            // Producers with GPU work of their own run here, while no list is open, which is why PrepareFrame
            // sits outside the recording rather than inside it.
            scene.PrepareFrame();

            tally.Clear();
            ulong epochBefore = list.Encoders.Epoch;
            int blocksBefore = list.Arena.BlocksCreated;
            long t0 = Stopwatch.GetTimestamp();

            using (GpuRecording.Open(device, tally, "MetalRecordCostGpuTests"))
                scene.RenderInternal(tally, W, H, fb);

            ulong epochAfter = list.Encoders.Epoch;
            int allocations = list.Arena.BlocksCreated - blocksBefore;

            // HOW MANY BLIT PASSES THE FRAME GENUINELY NEEDED, read off the arena rather than assumed. A record-
            // time upload to a non-uniform buffer takes a staging lease and then one copy, so an open block in
            // this slot means the frame owed a blit; a uniform write lands in the ring and owes nothing. Every
            // such upload in one recording can share ONE encoder, so the honest bound is one, and a backend that
            // straddled a render pass with them would open two and fail the assertion rather than widen it.
            int blitPasses = list.Arena.OpenBlockCount > 0 ? 1 : 0;

            device.Submit(list);
            device.WaitForIdle();
            double frameMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            // THE BEGIN'S OWN BUMP IS NOT A BOUNDARY. BeginRecording increments once to invalidate every record
            // made against the previous command buffer, and every encoder transition after it increments once
            // per begin and once per end.
            int boundaries = (int)(epochAfter - epochBefore) - 1;

            GpuCommandTally t = tally.Tally;
            return new Recording(
                UpdateBuffers: t[GpuCommandKind.UpdateBuffer],
                EncoderBoundaries: boundaries,
                EncodersOpened: boundaries / 2,
                Allocations: allocations,
                FramebufferBinds: t[GpuCommandKind.SetFramebuffer],
                Dispatches: t[GpuCommandKind.Dispatch],
                Copies: t[GpuCommandKind.CopyTexture] + t[GpuCommandKind.ResolveTexture],
                BlitPasses: blitPasses,
                DrawIndexed: t[GpuCommandKind.DrawIndexed],
                FrameMs: frameMs);
        }

        static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

        void Report(Scene3D scene, List<Recording> gpuSkinned, List<Recording> cpuSkinned)
        {
            _out.WriteLine($"scene: {StreamedWorldSceneContent.ChunkMeshes} chunk meshes + "
                + $"{StreamedWorldSceneContent.HlodMeshes} HLOD cluster meshes + "
                + $"{StreamedWorldSceneContent.PropInstances} prop instances = "
                + $"{StreamedWorldSceneContent.RigidInstances} rigid instances, {Characters} characters at "
                + $"{StreamedWorldSceneContent.CharacterVertices:#,0} vertices / "
                + $"{StreamedWorldSceneContent.CharacterBones} bones, {StreamedWorldSceneContent.Cascades} "
                + $"cascades at {StreamedWorldSceneContent.ShadowResolution}, target {W}x{H}");
            _out.WriteLine($"warmup: {Warmup} frames discarded per configuration, {Frames} read, one command list "
                + $"reused by all of them. shadow depth pass rendered on the last frame: "
                + $"{!scene.ShadowPassSkippedLastFrame}");
            _out.WriteLine("");
            _out.WriteLine($"{"path",-14} {"frame",-6} {"UpdateBuffer",13} {"boundaries",11} {"encoders",9} "
                + $"{"bound",6} {"allocs",7} {"fb",4} {"blits",6} {"drawIndexed",12} {"ms",8}");

            WriteRows("GPU skinning", gpuSkinned);
            WriteRows("CPU skinning", cpuSkinned);

            _out.WriteLine("");
            WriteVerdict("GPU skinning", gpuSkinned[0]);
            WriteVerdict("CPU skinning", cpuSkinned[0]);
        }

        void WriteRows(string path, List<Recording> steady)
        {
            for (int i = 0; i < steady.Count; i++)
            {
                Recording r = steady[i];
                _out.WriteLine($"{path,-14} {i,-6} {r.UpdateBuffers,13} {r.EncoderBoundaries,11} "
                    + $"{r.EncodersOpened,9} {r.EncoderBound,6} {r.Allocations,7} {r.FramebufferBinds,4} "
                    + $"{r.BlitPasses,6} {r.DrawIndexed,12} {r.FrameMs,8:0.000}");
            }
        }

        void WriteVerdict(string path, Recording r)
            => _out.WriteLine($"MM1 on {path}: {r.UpdateBuffers} record-time UpdateBuffer calls per frame, "
                + $"{r.EncodersOpened} encoders opened ({r.EncoderBoundaries} boundaries) against a bound of "
                + $"{r.EncoderBound} ({r.FramebufferBinds} framebuffer changes + {r.Dispatches} dispatches + "
                + $"{r.Copies} copies + {r.BlitPasses} blit), {r.Allocations} record-time buffer allocations.");
    }
}
