using System;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// GPU-to-CPU readback. <see cref="ToRgba"/> / <see cref="ToRgbaMip"/> copy a rendered texture back to a
    /// tightly-packed CPU RGBA8 buffer (<c>width * height * 4</c> bytes, row-major, top-left origin): they
    /// allocate a staging texture, blit into it, map it, and de-stride the driver's
    /// <see cref="MappedData.RowPitch"/> into packed rows. Shared by the Render2D and Render3D headless snapshot
    /// helpers (their readback used to be duplicated verbatim). <see cref="ReadBuffer{T}"/> is the buffer
    /// equivalent, for reading a compute-written storage buffer back as a typed array. The source resource must
    /// be done being written before any of these is called (each submits its own copy and drains, but the work
    /// that PRODUCED the data has to have been submitted first).
    /// </summary>
    public static class GpuReadback
    {
        /// <summary>Read <paramref name="src"/> back as packed RGBA8. Requires a mappable device (a Metal GPU here).</summary>
        public static byte[] ToRgba(IGpuDevice gd, IGpuTexture src, int width, int height)
        {
            var f = gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.CopyTexture(src, staging);
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            var outBytes = new byte[width * height * 4];
            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        uint si = (uint)(y * (int)map.RowPitch + x * 4);
                        int di = (y * width + x) * 4;
                        outBytes[di + 0] = data[si + 0];
                        outBytes[di + 1] = data[si + 1];
                        outBytes[di + 2] = data[si + 2];
                        outBytes[di + 3] = data[si + 3];
                    }
            }
            gd.Unmap(staging);
            return outBytes;
        }

        /// <summary>Read one mip level + array layer of <paramref name="src"/> back as packed RGBA8
        /// (<paramref name="mipWidth"/> x <paramref name="mipHeight"/>, the mip's own dimensions). Copies that
        /// subresource into a staging texture, maps it, and de-strides the rows. Useful for verifying a generated
        /// mip chain (e.g. that a high mip is a real blurred downsample, not a copy of mip 0 or empty). Requires a
        /// mappable device.</summary>
        public static byte[] ToRgbaMip(IGpuDevice gd, IGpuTexture src, uint mipLevel, uint arrayLayer, int mipWidth, int mipHeight)
        {
            var f = gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)mipWidth, (uint)mipHeight, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.CopyTextureSubresource(src, mipLevel, arrayLayer, staging, (uint)mipWidth, (uint)mipHeight);
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            var outBytes = new byte[mipWidth * mipHeight * 4];
            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < mipHeight; y++)
                    for (int x = 0; x < mipWidth; x++)
                    {
                        uint si = (uint)(y * (int)map.RowPitch + x * 4);
                        int di = (y * mipWidth + x) * 4;
                        outBytes[di + 0] = data[si + 0];
                        outBytes[di + 1] = data[si + 1];
                        outBytes[di + 2] = data[si + 2];
                        outBytes[di + 3] = data[si + 3];
                    }
            }
            gd.Unmap(staging);
            return outBytes;
        }

        /// <summary>Read <paramref name="elementCount"/> elements of <typeparamref name="T"/> back from
        /// <paramref name="src"/> (typically a <see cref="GpuBufferUsage.StructuredBufferReadWrite"/> buffer a
        /// compute pass just wrote), starting at <paramref name="srcOffsetBytes"/>. Allocates a
        /// <see cref="GpuBufferUsage.Staging"/> buffer, copies into it, drains, maps, and copies out - the buffer
        /// counterpart of <see cref="ToRgba"/>, so a compute consumer does not re-derive the sequence.
        /// <typeparamref name="T"/>'s layout must match the shader's, which for a std430 buffer means watching the
        /// usual scalar/vec3 padding rules (a <c>vec3</c> member occupies 16 bytes).</summary>
        public static T[] ReadBuffer<T>(IGpuDevice gd, IGpuBuffer src, int elementCount, uint srcOffsetBytes = 0)
            where T : unmanaged
        {
            if (elementCount < 0) throw new ArgumentOutOfRangeException(nameof(elementCount));
            var result = new T[elementCount];
            if (elementCount == 0) return result;

            uint sizeBytes;
            unsafe { sizeBytes = (uint)(elementCount * sizeof(T)); }

            var f = gd.Factory;
            using IGpuBuffer staging = f.CreateBuffer(new GpuBufferDescription(sizeBytes, GpuBufferUsage.Staging));
            // Drain BEFORE the copy, not only after it. The compute work that produced the data was submitted on
            // another command list, and a copy in a later submission is not ordered against it on every backend
            // (Veldrid's Vulkan submissions carry no semaphores, and its buffer copy emits no barrier ahead of
            // itself). A readback is a synchronous operation anyway, so an extra drain on an already-idle device
            // costs nothing and removes the footgun.
            gd.WaitForIdle();
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.CopyBuffer(src, srcOffsetBytes, staging, 0, sizeBytes);
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                var span = new ReadOnlySpan<T>((void*)map.Data, elementCount);
                span.CopyTo(result);
            }
            gd.Unmap(staging);
            return result;
        }
    }
}
