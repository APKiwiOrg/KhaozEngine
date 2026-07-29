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
    /// </summary>
    public sealed class FrameUploadAttributionGpuTests
    {
        const int W = 640, H = 400;
        const int Resolution = 2048, Cascades = 4;

        // The live client's scene shape.
        const int ChunkMeshes = 447;        // resident chunks: each is its own mesh, so each is its own run + draw
        const int HlodMeshes = 447;         // one merged HLOD cluster mesh per chunk (the tree layer)
        const int PropInstances = 3000;     // authored placements inside the 240 m gameplay ring, over shared meshes
        const float ChunkSize = 60f;

        // Ruinborne's player mesh is 13,637 vertices (player_human_male.glb). BuildTube's vertex count is
        // (rings + 1) * radial, so 340 x 40 lands at 13,640: the same order, and exact enough that the per-character
        // byte cost below is the real one.
        const int CharacterRings = 340, CharacterRadial = 40, CharacterBones = 48;
        const int CharacterVertices = (CharacterRings + 1) * CharacterRadial;

        static readonly int[] CharacterCounts = { 0, 1, 4, 12, 24 };

        const int Warmup = 2, Frames = 6;

        readonly ITestOutputHelper _out;
        public FrameUploadAttributionGpuTests(ITestOutputHelper o) => _out = o;

        static ShadowSettings Shadows() => new()
        {
            Mode = ShadowMode.ShadowMap,
            ShadowMapResolution = Resolution,
            ShadowCascadeCount = Cascades,
        };

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
            public readonly Render3DPreview Preview;
            public Scene3D Scene => Preview.Scene;
            public readonly IGpuDevice Device;
            readonly List<MeshHandle> _chunks = new();
            readonly List<MeshHandle> _hlod = new();
            readonly MeshHandle[] _props = new MeshHandle[4];
            readonly SkinnedMeshHandle _character;
            readonly Matrix4x4[] _palette;
            readonly List<Matrix4x4> _propWorlds = new();
            readonly List<Vector3> _chunkOrigins = new();

            public Harness()
            {
                _ctx = GpuDeviceContext.CreateHeadless();
                Device = _ctx.GpuDevice;
                Preview = new Render3DPreview(Device, W, H, Shadows());
                Scene.EnableTiming = true;
                Scene.Post.Starfield = false;
                Scene.Post.Outline = false;
                Scene.Camera.Azimuth = 0.6f;
                Scene.Camera.Elevation = 0.35f;
                Scene.Camera.Frame(Vector3.Zero, new Vector3(70f, 25f, 70f));

                // Chunks + their merged HLOD clusters: DISTINCT mesh handles, because that is what makes a streamed
                // world one run (and one draw call) per chunk rather than one big instanced run.
                int side = (int)MathF.Ceiling(MathF.Sqrt(ChunkMeshes));
                for (int i = 0; i < ChunkMeshes; i++)
                {
                    int cx = i % side, cz = i / side;
                    _chunkOrigins.Add(new Vector3((cx - side / 2) * ChunkSize, 0f, (cz - side / 2) * ChunkSize));
                    _chunks.Add(Scene.LoadMesh(MeshPrimitives.Tile(ChunkSize, 0.4f)));
                }
                for (int i = 0; i < HlodMeshes; i++) _hlod.Add(Scene.LoadMesh(MeshPrimitives.RoundedBox(3f, 0.5f, 3)));

                _props[0] = Scene.LoadMesh(MeshPrimitives.Box(0.8f));
                _props[1] = Scene.LoadMesh(MeshPrimitives.Cone(0.5f, 2.2f, 6));
                _props[2] = Scene.LoadMesh(MeshPrimitives.Sphere(1.6f, 10, 12));
                _props[3] = Scene.LoadMesh(MeshPrimitives.RoundedBox(1.4f, 0.3f, 4));

                uint seed = 0x51AB_C0DE;
                float Next() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (float)(1 << 24); }
                for (int i = 0; i < PropInstances; i++)
                {
                    float x = (Next() - 0.5f) * 480f, z = (Next() - 0.5f) * 480f;
                    _propWorlds.Add(Matrix4x4.CreateRotationY(Next() * 6.28f) * Matrix4x4.CreateTranslation(x, 0f, z));
                }

                SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, CharacterRings, CharacterRadial, CharacterBones, Axis.Z);
                _character = Scene.LoadSkinnedMesh(tube);
                _palette = (Matrix4x4[])tube.RestPose.Clone();
            }

            /// <summary>One frame of the scene with <paramref name="characters"/> skinned draws, all placed inside the
            /// camera frustum so every one of them really is skinned (a culled draw skips its upload, which would make
            /// the sweep measure the cull instead of the stream).</summary>
            public void DrawFrame(Scene3D s, int characters, int chunks)
            {
                for (int i = 0; i < chunks; i++)
                    s.Draw(_chunks[i], Matrix4x4.CreateTranslation(_chunkOrigins[i]), new Color(0.5f, 0.52f, 0.46f, 1f));
                for (int i = 0; i < HlodMeshes; i++)
                    s.Draw(_hlod[i], Matrix4x4.CreateTranslation(_chunkOrigins[i] + new Vector3(0f, 1.5f, 0f)),
                        new Color(0.3f, 0.45f, 0.28f, 1f));
                for (int i = 0; i < _propWorlds.Count; i++)
                    s.Draw(_props[i & 3], _propWorlds[i], new Color(0.35f, 0.55f, 0.3f, 1f));
                for (int i = 0; i < characters; i++)
                {
                    float a = i * 0.7f;
                    var world = Matrix4x4.CreateTranslation(MathF.Cos(a) * (2f + i * 0.35f), 1f, MathF.Sin(a) * (2f + i * 0.35f));
                    s.DrawSkinned(_character, _palette, world, Color.White);
                }
            }

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
