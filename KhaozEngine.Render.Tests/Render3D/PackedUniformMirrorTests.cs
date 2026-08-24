using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// THE BYTES, not the extent. <c>FrameUniformUploadShapeGpuTests</c> proves each converted site records ONE
    /// whole-buffer write per frame; these prove that write carries exactly what the run of partial writes it
    /// replaced used to carry. Both halves are needed: a packer that uploads the whole buffer with a slot at the
    /// wrong offset satisfies the shape guard perfectly and renders garbage.
    /// <para>
    /// Device-free, on <see cref="FakeGpuDevice"/>, so the mirrors are covered on every push and every OS rather
    /// than only on a leg with a GPU. See
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see>.
    /// </para>
    /// </summary>
    public sealed class PackedUniformMirrorTests
    {
        // ---- The tile-ground material's combined UBO: frame block at 0, the retained params appended after it ----
        //
        // The splat material had the IDENTICAL shape and these two cases were written against it. #604 unfolded that
        // one into a shared frame set plus a load-time params buffer, which left no mirror to test, so they were
        // retargeted onto the remaining combined ground UBO rather than deleted: the mechanism they pin is #408's,
        // not the splat pass's, and it is still shipped here.

        const uint FrameBytes = 1008;   // ModelRenderer.UboBytes

        static Vector4[] DistinctParams()
        {
            var p = new Vector4[TileGroundMaterialConfig.ParamsBytes / 16];
            for (int i = 0; i < p.Length; i++) p[i] = new Vector4(i * 4 + 1, i * 4 + 2, i * 4 + 3, i * 4 + 4);
            return p;
        }

        static byte[] Ramp(int n, byte seed)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(seed + i);
            return b;
        }

        [Fact]
        public void A_ground_upload_is_the_frame_block_followed_by_the_material_params()
        {
            using var gd = new FakeGpuDevice();
            IGpuBuffer buffer = gd.Factory.CreateBuffer(new GpuBufferDescription(
                FrameBytes + TileGroundMaterialConfig.ParamsBytes, GpuBufferUsage.UniformBuffer));
            Vector4[] p = DistinctParams();
            using var mirror = new TileGroundUniformBuffer(buffer, p, FrameBytes);

            var rec = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            byte[] frame = Ramp((int)FrameBytes, 3);
            mirror.Upload(rec, frame);

            RecordingGpuCommandList.Upload u = Assert.Single(rec.Uploads);
            Assert.True(u.IsWholeBuffer,
                $"the combined UBO must go up whole: got [{u.Offset}, {u.Offset + u.Bytes}) of {buffer.SizeInBytes}");

            // The concatenation of the two partial writes this replaced: the per-frame block at 0 and the
            // load-time params at FrameBytes.
            var expected = new byte[FrameBytes + TileGroundMaterialConfig.ParamsBytes];
            frame.CopyTo(expected, 0);
            MemoryMarshal.AsBytes<Vector4>(p).CopyTo(expected.AsSpan((int)FrameBytes));
            Assert.Equal(expected, u.Data);
        }

        [Fact]
        public void A_later_ground_upload_replaces_the_frame_head_and_keeps_the_params_tail()
        {
            using var gd = new FakeGpuDevice();
            IGpuBuffer buffer = gd.Factory.CreateBuffer(new GpuBufferDescription(
                FrameBytes + TileGroundMaterialConfig.ParamsBytes, GpuBufferUsage.UniformBuffer));
            Vector4[] p = DistinctParams();
            using var mirror = new TileGroundUniformBuffer(buffer, p, FrameBytes);

            var rec = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            mirror.Upload(rec, Ramp((int)FrameBytes, 3));
            byte[] second = Ramp((int)FrameBytes, 200);
            mirror.Upload(rec, second);

            byte[] bytes = rec.Uploads[1].Data!;
            Assert.Equal(second, bytes[..(int)FrameBytes]);

            // The tail is the thing the material has to RETAIN for a whole write to be possible at all: nothing
            // re-supplies it per frame, so a mirror that let it rot would upload a valid-looking block of zeros.
            var tail = new byte[TileGroundMaterialConfig.ParamsBytes];
            MemoryMarshal.AsBytes<Vector4>(p).CopyTo(tail);
            Assert.Equal(tail, bytes[(int)FrameBytes..]);
        }

        // ---- The overlay proxies: one slot per queued draw, all packed before the first draw is recorded ----

        const int OverlaySlotBytes = 256;
        const int OverlayInitialSlots = 8;

        [Fact]
        public void Overlay_slots_hold_the_view_projection_and_each_draws_own_world_at_its_own_offset()
        {
            using var gd = new FakeGpuDevice();
            var outputs = new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
                GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float);
            using var overlay = new OverlayMeshRenderer(gd, outputs);

            var vp = Matrix4x4.CreateTranslation(0.5f, 1.5f, 2.5f);
            var worlds = new[]
            {
                Matrix4x4.CreateTranslation(1f, 0f, 0f),
                Matrix4x4.CreateTranslation(0f, 2f, 0f),
                Matrix4x4.CreateTranslation(0f, 0f, 3f),
            };

            overlay.EnsureCapacity(worlds.Length);
            overlay.BeginFrame(vp);
            IGpuBuffer vb = gd.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));
            IGpuBuffer ib = gd.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.IndexBuffer));
            for (int k = 0; k < worlds.Length; k++)
                overlay.Enqueue(vb, ib, 6, GpuIndexFormat.UInt16, k, worlds[k]);

            var rec = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            overlay.Flush(rec);

            RecordingGpuCommandList.Upload u = Assert.Single(rec.Uploads);
            Assert.True(u.IsWholeBuffer, "the overlay slot buffer must go up whole, once, ahead of the draws");
            Assert.Equal((uint)(OverlayInitialSlots * OverlaySlotBytes), u.Bytes);

            for (int k = 0; k < worlds.Length; k++)
            {
                ReadOnlySpan<byte> slot = u.Data.AsSpan(k * OverlaySlotBytes, 128);
                Assert.Equal(vp, MemoryMarshal.Read<Matrix4x4>(slot));
                Assert.Equal(worlds[k], MemoryMarshal.Read<Matrix4x4>(slot[64..]));
            }
        }

        [Fact]
        public void Overlay_flush_records_nothing_when_the_frame_queued_nothing()
        {
            using var gd = new FakeGpuDevice();
            var outputs = new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
                GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float);
            using var overlay = new OverlayMeshRenderer(gd, outputs);
            overlay.EnsureCapacity(4);
            overlay.BeginFrame(Matrix4x4.Identity);

            var rec = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            overlay.Flush(rec);
            Assert.Empty(rec.Uploads);
        }

        [Fact]
        public void Overlay_slots_survive_the_grow_that_a_bigger_frame_forces()
        {
            using var gd = new FakeGpuDevice();
            var outputs = new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
                GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float);
            using var overlay = new OverlayMeshRenderer(gd, outputs);
            IGpuBuffer vb = gd.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));
            IGpuBuffer ib = gd.Factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.IndexBuffer));

            const int Draws = OverlayInitialSlots + 3;   // past the initial capacity, so the buffer regrows
            overlay.EnsureCapacity(Draws);
            overlay.BeginFrame(Matrix4x4.Identity);
            var worlds = new List<Matrix4x4>();
            for (int k = 0; k < Draws; k++)
            {
                var w = Matrix4x4.CreateTranslation(k, -k, k * 2);
                worlds.Add(w);
                overlay.Enqueue(vb, ib, 6, GpuIndexFormat.UInt16, k, w);
            }

            var rec = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            overlay.Flush(rec);

            RecordingGpuCommandList.Upload u = Assert.Single(rec.Uploads);
            Assert.True(u.IsWholeBuffer, "a grown overlay slot buffer still goes up whole");
            for (int k = 0; k < Draws; k++)
                Assert.Equal(worlds[k], MemoryMarshal.Read<Matrix4x4>(u.Data.AsSpan(k * OverlaySlotBytes + 64)));
        }
    }
}
