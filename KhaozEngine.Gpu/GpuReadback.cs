namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Copies a rendered GPU texture back to a tightly-packed CPU RGBA8 buffer (<c>width * height * 4</c> bytes,
    /// row-major, top-left origin). Allocates a staging texture, blits into it, maps it, and de-strides the
    /// driver's <see cref="MappedData.RowPitch"/> into packed rows. Shared by the Render2D and Render3D headless
    /// snapshot helpers (their readback used to be duplicated verbatim). The source texture must be done
    /// rendering before this is called (the caller submits + waits on its render work first).
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
    }
}
