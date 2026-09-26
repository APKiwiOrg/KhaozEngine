using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Upload and readback for the half and single float targets the temporal tests read, one float per
    /// channel, rows top to bottom. <see cref="GpuReadback.ToRgba"/> takes RGBA8 only.</summary>
    internal static class TemporalTextureIo
    {
        public static int Channels(GpuPixelFormat format) => format switch
        {
            GpuPixelFormat.R16G16B16A16Float => 4,
            GpuPixelFormat.R16G16Float => 2,
            GpuPixelFormat.R32Float => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "not a float format the temporal tests read"),
        };

        public static void Upload(IGpuDevice gd, IGpuTexture texture, float[] values)
        {
            Assert(values.Length == texture.Width * texture.Height * Channels(texture.Format), "value count");
            byte[] bytes;
            if (texture.Format == GpuPixelFormat.R32Float)
            {
                bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
            }
            else
            {
                var halves = new Half[values.Length];
                for (int i = 0; i < values.Length; i++) halves[i] = (Half)values[i];
                bytes = MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
            }
            gd.UpdateTexture(texture, bytes, 0, 0, texture.Width, texture.Height);
        }

        public static float[] Read(IGpuDevice gd, IGpuTexture texture)
        {
            int channels = Channels(texture.Format);
            int w = (int)texture.Width, h = (int)texture.Height;
            using IGpuTexture staging = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                texture.Width, texture.Height, texture.Format, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = gd.Factory.CreateCommandList())
            {
                using (GpuRecording.Open(gd, cl, "TemporalTextureIo.Read")) cl.CopyTexture(texture, staging);
                gd.Submit(cl);
                gd.WaitForIdle();
            }
            var values = new float[w * h * channels];
            MappedData map = gd.Map(staging, GpuMapMode.Read);
            try
            {
                bool single = texture.Format == GpuPixelFormat.R32Float;
                for (int y = 0; y < h; y++)
                {
                    IntPtr row = map.Data + (int)(y * map.RowPitch);
                    for (int i = 0; i < w * channels; i++)
                        values[y * w * channels + i] = single
                            ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(row, i * 4))
                            : (float)BitConverter.Int16BitsToHalf(Marshal.ReadInt16(row, i * 2));
                }
            }
            finally
            {
                gd.Unmap(staging);
            }
            return values;
        }

        static void Assert(bool condition, string what)
        {
            if (!condition) throw new ArgumentException($"TemporalTextureIo: {what} does not match the texture");
        }
    }
}
