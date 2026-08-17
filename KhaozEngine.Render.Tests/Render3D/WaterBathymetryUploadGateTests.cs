using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// What <see cref="WaterBathymetryMap.Update"/> decides to upload, measured rather than claimed. The gate has
    /// to answer two different questions with one compare: is this the same field, and is it the same version of
    /// it. Getting the second right and the first wrong is
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/645">#645</see>: a same-resolution replacement
    /// was ignored and the PREVIOUS field's depths stayed on the GPU, which renders a plausible shore rather than
    /// failing.
    /// <para>
    /// Headless, on a device that records what it was handed. That is enough to pin the arithmetic AND the bytes:
    /// what it cannot see is a texture the renderer never re-bound, which is why the end-to-end claim is a
    /// <c>[GpuFact]</c> (<c>WaterBathymetrySwapGpuTests</c>) and not this file.
    /// </para>
    /// </summary>
    public sealed class WaterBathymetryUploadGateTests
    {
        const int Resolution = 8;

        static WaterBathymetry Field(float depth)
        {
            var field = new WaterBathymetry(Resolution, centerX: 0f, centerZ: 0f, halfExtentX: 40f);
            Array.Fill(field.Depths, depth);
            field.MarkChanged();
            return field;
        }

        /// <summary>The depth the first texel of an upload carries, decoded back out of the packed rgba16f.</summary>
        static float FirstDepth(byte[] data)
            => (float)BitConverter.Int16BitsToHalf((short)(data[0] | (data[1] << 8)));

        [Fact]
        public void ReplacingTheFieldWithADifferentOneOfTheSameResolutionUploadsIt()
        {
            using var gd = new UploadRecordingGpuDevice();
            using var map = new WaterBathymetryMap(gd);

            WaterBathymetry a = Field(4f);
            WaterBathymetry b = Field(400f);
            // The trap in one line: two separately built fields both sit here, so a revision-only gate sees no
            // change at all.
            Assert.Equal(a.Revision, b.Revision);
            Assert.Equal(a.Resolution, b.Resolution);

            map.Update(a);
            Assert.Equal(1, map.LastUploads);
            map.Update(b);
            Assert.Equal(1, map.LastUploads);

            Assert.Equal(2, gd.TextureUploads.Count);
            Assert.Equal(4f, FirstDepth(gd.TextureUploads[0]), 2);
            Assert.Equal(400f, FirstDepth(gd.TextureUploads[1]), 2);
        }

        [Fact]
        public void MutatingAFieldInPlaceUploadsItAgainOnMarkChanged()
        {
            using var gd = new UploadRecordingGpuDevice();
            using var map = new WaterBathymetryMap(gd);

            WaterBathymetry a = Field(4f);
            map.Update(a);
            Array.Fill(a.Depths, 9f);
            a.MarkChanged();
            map.Update(a);

            Assert.Equal(2, gd.TextureUploads.Count);
            Assert.Equal(4f, FirstDepth(gd.TextureUploads[0]), 2);
            Assert.Equal(9f, FirstDepth(gd.TextureUploads[1]), 2);
        }

        [Fact]
        public void TheSameFieldUnchangedUploadsExactlyOnce()
        {
            using var gd = new UploadRecordingGpuDevice();
            using var map = new WaterBathymetryMap(gd);

            WaterBathymetry a = Field(4f);
            for (int frame = 0; frame < 5; frame++) map.Update(a);

            // The whole cost model: a coastline baked once at load costs one upload for the life of the process.
            // The identity half of the gate must not turn a steady frame into an upload.
            Assert.Single(gd.TextureUploads);
            Assert.Equal(0, map.LastUploads);
        }

        [Fact]
        public void ADifferentResolutionRebuildsTheTextureAndUploadsThroughTheDrainPath()
        {
            using var gd = new UploadRecordingGpuDevice();
            using var map = new WaterBathymetryMap(gd);

            WaterBathymetry small = Field(4f);
            var large = new WaterBathymetry(Resolution * 2, 0f, 0f, 40f);
            Array.Fill(large.Depths, 400f);
            large.MarkChanged();

            map.Update(small);
            map.Update(large);

            // This case was never broken: the resize branch drops the texture and resets the revision, so it
            // re-uploads on its own. Pinned here so the fix for the same-resolution case cannot quietly cost it.
            Assert.Equal(2, gd.TextureUploads.Count);
            Assert.Equal(400f, FirstDepth(gd.TextureUploads[1]), 2);
            Assert.Equal(Resolution * 2 * Resolution * 2 * 8, gd.TextureUploads[1].Length);
        }

        [Fact]
        public void SwitchingTheFieldOffAndBackOnCostsNothing()
        {
            using var gd = new UploadRecordingGpuDevice();
            using var map = new WaterBathymetryMap(gd);

            WaterBathymetry a = Field(4f);
            Assert.True(map.Update(a));
            Assert.False(map.Update(null));
            Assert.True(map.Update(a));

            // Deliberate: the inactive arm keeps both the texture and the record of what is in it, and only
            // another field could have overwritten it meanwhile.
            Assert.Single(gd.TextureUploads);
        }

        /// <summary>
        /// A <see cref="FakeGpuDevice"/> that keeps a COPY of every texture upload's bytes. The copy is the point:
        /// the map packs into one reused scratch buffer, so holding the array it was handed would show every
        /// upload carrying the last one's contents. Local to this file rather than folded into the shared fake,
        /// which is a counting harness and has no business growing a buffer per upload for every test that drives
        /// a renderer through it.
        /// </summary>
        sealed class UploadRecordingGpuDevice : IGpuDevice
        {
            readonly FakeGpuDevice _inner = new();

            internal List<byte[]> TextureUploads { get; } = new();

            public GpuBackendKind Backend => _inner.Backend;
            public GpuCapabilities Capabilities => _inner.Capabilities;
            public IGpuResourceFactory Factory => _inner.Factory;
            public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
            public IGpuSampler PointSampler => _inner.PointSampler;
            public IGpuSampler LinearSampler => _inner.LinearSampler;
            public bool SyncToVerticalBlank
            {
                get => _inner.SyncToVerticalBlank;
                set => _inner.SyncToVerticalBlank = value;
            }

            public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
                => TextureUploads.Add((byte[])data.Clone());

            public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
                uint mipLevel, uint arrayLayer)
                => TextureUploads.Add((byte[])data.Clone());

            public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
            public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
            public void WaitForIdle() => _inner.WaitForIdle();
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
                => _inner.UpdateBuffer(b, offsetBytes, data);
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
                => _inner.UpdateBuffer(b, offsetBytes, data);
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
                => _inner.UpdateBuffer(b, offsetBytes, in data);
            public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
            public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
            public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
            public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);
            public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
            public void Present() => _inner.Present();
            public void Dispose() => _inner.Dispose();
        }
    }
}
