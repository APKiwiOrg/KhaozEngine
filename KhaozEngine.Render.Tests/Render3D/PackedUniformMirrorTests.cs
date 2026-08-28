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
    /// Device-free, on <see cref="FakeGpuDevice"/>, so the packers are covered on every push and every OS rather
    /// than only on a leg with a GPU. See
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see>.
    /// </para>
    /// <para>
    /// THE TWO GROUND-MIRROR CASES THAT USED TO OPEN THIS FILE ARE GONE, and they were deleted rather than
    /// retargeted a second time. They were written against <c>SplatUniformBuffer</c>, retargeted onto
    /// <c>TileGroundUniformBuffer</c> when
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see> unfolded the splat pass, and
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/727">#727</see> unfolded the tile-ground pass
    /// too. What they pinned was a CPU mirror rebuilding a COMBINED frame-plus-params buffer so the per-frame
    /// re-sync was a whole write instead of a partial one, and there is no combined buffer and no such mirror left
    /// in the tree to point them at. A material's params are written once at load now, whole, from offset 0, which
    /// is the cheap Direct3D 11 route by construction. The overlay proxies below are the packers that remain, and
    /// they still pin the same #408 property for the destination that still has it.
    /// </para>
    /// </summary>
    public sealed class PackedUniformMirrorTests
    {
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
