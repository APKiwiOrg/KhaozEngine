using System;
using System.Collections.Generic;
using System.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// What actually composes a streamed MMO frame's per-frame GPU upload, measured rather than assumed.
    /// <para>
    /// The question this harness was built to answer: a live F1 read on a Ruinborne client reported
    /// <c>BufferUpdateBytes</c> at 19.3 MB per frame on a mostly-static scene (6.4k rasterized instances, 1.2k draw
    /// calls, 447 resident chunks, the player standing still), and the standing assumption was that the rigid
    /// instance stream was re-uploading it. The total alone cannot say: it is one number over four completely
    /// different streams. So this measures the four separately (the <c>RenderFrameStats</c> upload split) on a scene
    /// built to that client's shape, and the shape itself is read off the game's shipped content rather than guessed:
    /// 447 resident chunk meshes, one merged HLOD cluster mesh per chunk, and the individual props that can exist
    /// inside the 240 m gameplay ring. The island document holds 17,279 authored placements in total, world-wide, so
    /// the rigid instance stream has a HARD CEILING of about 18k instances (2.3 MB) no matter where the camera
    /// stands, and the in-ring count is a small fraction of that.
    /// </para>
    /// <para>
    /// The answer the numbers give: the rigid instance stream is flat at about 0.47 MB and does not move when
    /// characters are added, while ONE 13.6k-vertex character costs about 0.85 MB per frame on the CPU-skinning
    /// path, because that path re-uploads every deformed vertex at 64 bytes each every frame. Two dozen characters
    /// therefore reproduce the field reading on their own, with the instance stream at a couple of percent of it.
    /// The fix that follows is not a static/dynamic instance split. It is <see cref="Scene3D.UseGpuSkinning"/>,
    /// which replaces the O(vertices) stream with an O(bones) one, measured in the A/B below.
    /// </para>
    /// <para>
    /// Geometry is deliberately light (the chunk and prop meshes are small primitives). This harness targets UPLOAD
    /// BYTES, which depend on instance and vertex COUNTS and not on triangle density, so the byte columns are exact
    /// for the shape. The millisecond columns are a secondary signal on reduced geometry and are reported, never
    /// asserted. Gated on KE_GPU_TESTS.
    /// </para>
    /// <para>
    /// THE SCENE ITSELF IS <see cref="StreamedWorldSceneContent"/>, shared with the row that measures the same
    /// frame's record-time COUNTS (<see cref="MetalRecordCostGpuTests"/>). One definition, so the two readings are
    /// of the same frame rather than of two copies that can drift apart.
    /// </para>
    /// </summary>
    public sealed class FrameUploadAttributionGpuTests
    {
        const int W = 640, H = 400;

        // The live client's scene shape, which is StreamedWorldSceneContent's. Named here so the report lines and
        // the assertions below read as they always did.
        const int Resolution = StreamedWorldSceneContent.ShadowResolution;
        const int Cascades = StreamedWorldSceneContent.Cascades;
        const int ChunkMeshes = StreamedWorldSceneContent.ChunkMeshes;
        const int HlodMeshes = StreamedWorldSceneContent.HlodMeshes;
        const int PropInstances = StreamedWorldSceneContent.PropInstances;
        const int CharacterBones = StreamedWorldSceneContent.CharacterBones;
        const int CharacterVertices = StreamedWorldSceneContent.CharacterVertices;

        static readonly int[] CharacterCounts = { 0, 1, 4, 12, 24 };

        const int Warmup = 2, Frames = 6;

        readonly ITestOutputHelper _out;
        public FrameUploadAttributionGpuTests(ITestOutputHelper o) => _out = o;

        static double Median(List<double> xs)
        {
            xs.Sort();
            return xs.Count == 0 ? 0 : xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) * 0.5;
        }

        static string Kb(long bytes) => (bytes / 1024d).ToString("#,0.0");

        /// <summary>The scene, held together so every measurement in a test runs against the SAME device and the same
        /// loaded meshes. Two configurations measured on different devices are not comparable.</summary>
        sealed class Harness : IDisposable
        {
            readonly GpuDeviceContext _ctx;
            readonly StreamedWorldSceneContent _content;
            public readonly Render3DPreview Preview;
            public Scene3D Scene => Preview.Scene;
            public readonly IGpuDevice Device;

            public Harness()
            {
                _ctx = GpuDeviceContext.CreateHeadless();
                Device = _ctx.GpuDevice;
                Preview = new Render3DPreview(Device, W, H, StreamedWorldSceneContent.Shadows());
                Scene.EnableTiming = true;
                Scene.Post.Starfield = false;
                Scene.Post.Outline = false;
                StreamedWorldSceneContent.FrameCamera(Scene);
                _content = new StreamedWorldSceneContent(Scene);
            }

            /// <summary>One frame of the scene with <paramref name="characters"/> skinned draws and
            /// <paramref name="chunks"/> resident chunks. See <see cref="StreamedWorldSceneContent.Draw"/>.</summary>
            public void DrawFrame(Scene3D s, int characters, int chunks)
                => _content.Draw(s, characters, chunks);

            public void Dispose()
            {
                Preview.Dispose();
                _ctx.Dispose();
            }
        }

        readonly record struct Sample(RenderFrameStats Stats, double FrameMs);

        /// <summary>Render the configuration for a few frames and return the last frame's split plus the median
        /// wall-clock frame time. The byte columns are deterministic, so the last frame is as good as any.</summary>
        static Sample Measure(Harness h, int characters, bool gpuSkinning, int chunks = ChunkMeshes)
        {
            h.Scene.UseGpuSkinning = gpuSkinning;
            for (int i = 0; i < Warmup; i++) h.Preview.Capture(s => h.DrawFrame(s, characters, chunks));
            h.Device.WaitForIdle();
            var ms = new List<double>();
            for (int i = 0; i < Frames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                h.Preview.Capture(s => h.DrawFrame(s, characters, chunks));
                h.Device.WaitForIdle();
                ms.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            return new Sample(h.Scene.LastFrameStats, Median(ms));
        }

        [GpuFact]
        public void Mmo_shaped_frame_upload_is_the_skinned_stream_not_the_instance_stream()
        {
            using var h = new Harness();
            _out.WriteLine($"scene: {ChunkMeshes} chunk meshes + {HlodMeshes} HLOD cluster meshes + {PropInstances} prop " +
                           $"instances = {ChunkMeshes + HlodMeshes + PropInstances} rigid instances, characters at " +
                           $"{CharacterVertices:#,0} vertices / {CharacterBones} bones, {Cascades} cascades at {Resolution}");
            _out.WriteLine("");
            _out.WriteLine($"{"characters",-11} {"total KB",11} {"instances KB",13} {"skinned KB",11} {"skin ubo KB",12} " +
                           $"{"inst share",11} {"frame ms",9}");

            var byCount = new Dictionary<int, RenderFrameStats>();
            foreach (int n in CharacterCounts)
            {
                Sample m = Measure(h, n, gpuSkinning: false);
                byCount[n] = m.Stats;
                double share = m.Stats.BufferUpdateBytes == 0 ? 0 : 100.0 * m.Stats.InstanceUploadBytes / m.Stats.BufferUpdateBytes;
                _out.WriteLine($"{n,-11} {Kb(m.Stats.BufferUpdateBytes),11} {Kb(m.Stats.InstanceUploadBytes),13} " +
                               $"{Kb(m.Stats.SkinnedUploadBytes),11} {Kb(m.Stats.SkinnedUniformUploadBytes),12} " +
                               $"{share,10:0.0}% {m.FrameMs,9:0.000}");

                Assert.Equal(m.Stats.BufferUpdateBytes, m.Stats.UploadBytesPartitioned);
            }

            // 1. The rigid instance stream is exactly the queued instance count times the 124-byte record, and it does
            //    not move when characters are added. This is the claim the issue's premise turns on.
            long expectedInstanceBytes = (long)(ChunkMeshes + HlodMeshes + PropInstances) * 124;
            foreach (int n in CharacterCounts)
                Assert.Equal(expectedInstanceBytes, byCount[n].InstanceUploadBytes);

            // 2. The CPU-skinned stream is linear in the characters' VERTEX count, at 64 bytes per vertex plus one
            //    124-byte instance record per draw. That is the term that reaches megabytes.
            long perCharacter = (long)CharacterVertices * 64 + 124;
            foreach (int n in CharacterCounts)
                Assert.Equal(perCharacter * n, byCount[n].SkinnedUploadBytes);

            // 3. At the crowd size that reproduces the field reading, the instance stream is a rounding error. The
            //    bound is deliberately loose (10 percent) so it states the SHAPE of the answer rather than pinning a
            //    number a content change would move.
            RenderFrameStats crowd = byCount[CharacterCounts[^1]];
            _out.WriteLine("");
            _out.WriteLine($"at {CharacterCounts[^1]} characters: total {Kb(crowd.BufferUpdateBytes)} KB, of which the rigid " +
                           $"instance stream is {Kb(crowd.InstanceUploadBytes)} KB " +
                           $"({100.0 * crowd.InstanceUploadBytes / crowd.BufferUpdateBytes:0.0} percent)");
            Assert.True(crowd.InstanceUploadBytes * 10 < crowd.BufferUpdateBytes,
                $"expected the rigid instance stream to be under a tenth of the frame's upload at MMO crowd size, " +
                $"got {crowd.InstanceUploadBytes} of {crowd.BufferUpdateBytes} bytes");
            Assert.Equal(0L, crowd.SkinnedUniformUploadBytes);   // CPU path uploads no skinning uniforms at all
        }

        [GpuFact]
        public void Gpu_skinning_collapses_the_dominant_stream()
        {
            using var h = new Harness();
            int n = CharacterCounts[^1];

            // Interleaved, not sequential: the first configuration otherwise pays the JIT and the device warm-up for
            // both, which is exactly how a "faster" path gets measured as slower.
            Sample cpuA = Measure(h, n, gpuSkinning: false);
            Sample gpuA = Measure(h, n, gpuSkinning: true);
            Sample cpuB = Measure(h, n, gpuSkinning: false);
            Sample gpuB = Measure(h, n, gpuSkinning: true);

            double cpuMs = Math.Min(cpuA.FrameMs, cpuB.FrameMs), gpuMs = Math.Min(gpuA.FrameMs, gpuB.FrameMs);
            RenderFrameStats cpu = cpuB.Stats, gpu = gpuB.Stats;

            _out.WriteLine($"{n} characters at {CharacterVertices:#,0} vertices / {CharacterBones} bones, {Cascades} cascades");
            _out.WriteLine($"{"path",-14} {"total KB",11} {"instances KB",13} {"skinned KB",11} {"skin ubo KB",12} {"frame ms",9}");
            _out.WriteLine($"{"CPU skinning",-14} {Kb(cpu.BufferUpdateBytes),11} {Kb(cpu.InstanceUploadBytes),13} " +
                           $"{Kb(cpu.SkinnedUploadBytes),11} {Kb(cpu.SkinnedUniformUploadBytes),12} {cpuMs,9:0.000}");
            _out.WriteLine($"{"GPU skinning",-14} {Kb(gpu.BufferUpdateBytes),11} {Kb(gpu.InstanceUploadBytes),13} " +
                           $"{Kb(gpu.SkinnedUploadBytes),11} {Kb(gpu.SkinnedUniformUploadBytes),12} {gpuMs,9:0.000}");
            _out.WriteLine("");
            _out.WriteLine($"frame upload {Kb(cpu.BufferUpdateBytes)} -> {Kb(gpu.BufferUpdateBytes)} KB " +
                           $"({(double)cpu.BufferUpdateBytes / Math.Max(1, gpu.BufferUpdateBytes):0.0}x), " +
                           $"frame {cpuMs:0.000} -> {gpuMs:0.000} ms");

            Assert.Equal(cpu.BufferUpdateBytes, cpu.UploadBytesPartitioned);
            Assert.Equal(gpu.BufferUpdateBytes, gpu.UploadBytesPartitioned);
            // The rigid stream is untouched by the flip: it is the same scene.
            Assert.Equal(cpu.InstanceUploadBytes, gpu.InstanceUploadBytes);
            Assert.Equal(0L, gpu.SkinnedUploadBytes);
            Assert.True(gpu.BufferUpdateBytes * 5 < cpu.BufferUpdateBytes,
                $"expected GPU skinning to cut the frame's upload by at least 5x, got {gpu.BufferUpdateBytes} of {cpu.BufferUpdateBytes} bytes");
        }

        [GpuFact]
        public void Chunk_churn_moves_the_instance_stream_only_by_the_chunks_that_moved()
        {
            // Streaming must keep the split honest: a frame that loads or unloads chunks changes the rigid instance
            // stream by exactly the instances that came or went, and touches nothing else. This is the scenario a
            // static instance buffer would have had to serve incrementally, and it is measured here so the claim that
            // the rigid stream is small holds while the ring is churning, not only when it is settled.
            using var h = new Harness();
            Sample full = Measure(h, characters: 1, gpuSkinning: false, chunks: ChunkMeshes);
            Sample churned = Measure(h, characters: 1, gpuSkinning: false, chunks: ChunkMeshes - 64);

            long delta = full.Stats.InstanceUploadBytes - churned.Stats.InstanceUploadBytes;
            _out.WriteLine($"chunks {ChunkMeshes} -> {ChunkMeshes - 64}: instance stream {Kb(full.Stats.InstanceUploadBytes)} -> " +
                           $"{Kb(churned.Stats.InstanceUploadBytes)} KB (delta {Kb(delta)} KB), skinned stream unchanged at " +
                           $"{Kb(churned.Stats.SkinnedUploadBytes)} KB");

            Assert.Equal(64L * 124, delta);
            Assert.Equal(full.Stats.SkinnedUploadBytes, churned.Stats.SkinnedUploadBytes);
            Assert.Equal(churned.Stats.BufferUpdateBytes, churned.Stats.UploadBytesPartitioned);
        }
    }
}
