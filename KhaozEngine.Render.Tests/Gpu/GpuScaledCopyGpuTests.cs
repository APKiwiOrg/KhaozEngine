using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see cref="GpuScaledCopy"/> on a real device. The source is 64x32 split into four solid quadrants, so a
    /// flipped, mirrored or channel-swapped result lands a wrong colour on a pinned pixel. The BGRA case is the
    /// shipped one (every swapchain is B8G8R8A8UNorm) and pins that the output is RGBA in storage order. Skipped
    /// unless KE_GPU_TESTS is set.
    /// </summary>
    public sealed class GpuScaledCopyGpuTests
    {
        const int SourceWidth = 64, SourceHeight = 32;
        const int Tolerance = 2;

        static readonly byte[] TopLeft = { 200, 30, 30 };
        static readonly byte[] TopRight = { 30, 200, 30 };
        static readonly byte[] BottomLeft = { 30, 30, 200 };
        static readonly byte[] BottomRight = { 220, 220, 40 };

        static readonly byte[][] Quadrants = { TopLeft, TopRight, BottomLeft, BottomRight };

        // The same four colours a quarter turn on, so the second source differs from the first on every quadrant.
        static readonly byte[][] RotatedQuadrants = { BottomLeft, TopLeft, BottomRight, TopRight };

        [GpuFact]
        public void A_BGRA_source_downscales_four_to_one_into_RGBA_with_every_quadrant_in_place()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var copy = new GpuScaledCopy(gd);
            using IGpuTexture source = CreateQuadrantSource(gd, GpuPixelFormat.B8G8R8A8UNorm, Quadrants);

            byte[] rgba = Run(gd, copy, 16, 8, source);

            AssertPixel(rgba, 16, 3, 1, TopLeft);
            AssertPixel(rgba, 16, 12, 1, TopRight);
            AssertPixel(rgba, 16, 3, 6, BottomLeft);
            AssertPixel(rgba, 16, 12, 6, BottomRight);
        }

        [GpuFact]
        public void An_RGBA_source_at_the_same_size_keeps_every_quadrant_interior()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var copy = new GpuScaledCopy(gd);
            using IGpuTexture source = CreateQuadrantSource(gd, GpuPixelFormat.R8G8B8A8UNorm, Quadrants);

            byte[] rgba = Run(gd, copy, SourceWidth, SourceHeight, source);

            // The image border is kept on purpose: a wrap-addressed sampler would bleed the opposite edge into it.
            for (int y = 0; y < SourceHeight; y++)
                for (int x = 0; x < SourceWidth; x++)
                {
                    if (NearEdge(x, SourceWidth) || NearEdge(y, SourceHeight)) continue;
                    AssertPixel(rgba, SourceWidth, x, y, QuadrantAt(Quadrants, x, y, SourceWidth, SourceHeight));
                }
        }

        [GpuFact]
        public void Two_records_into_the_same_destination_in_one_recording_leave_the_second_source()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var copy = new GpuScaledCopy(gd);
            using IGpuTexture first = CreateQuadrantSource(gd, GpuPixelFormat.R8G8B8A8UNorm, Quadrants);
            using IGpuTexture second = CreateQuadrantSource(gd, GpuPixelFormat.R8G8B8A8UNorm, RotatedQuadrants);

            byte[] rgba = Run(gd, copy, 16, 8, first, second);

            AssertPixel(rgba, 16, 3, 1, RotatedQuadrants[0]);
            AssertPixel(rgba, 16, 12, 1, RotatedQuadrants[1]);
            AssertPixel(rgba, 16, 3, 6, RotatedQuadrants[2]);
            AssertPixel(rgba, 16, 12, 6, RotatedQuadrants[3]);
        }

        /// <summary>
        /// At 1:1 the copy is exact. A one-pixel checkerboard puts a different colour on every neighbour, so any
        /// blur, shift, flip or channel swap moves bytes. Both storage orders must read back as the same RGBA.
        /// </summary>
        [GpuFact]
        public void A_same_size_copy_is_pixel_exact()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var copy = new GpuScaledCopy(gd);
            byte[] expected = Checkerboard(GpuPixelFormat.R8G8B8A8UNorm);

            foreach (GpuPixelFormat format in new[] { GpuPixelFormat.B8G8R8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm })
            {
                using IGpuTexture source = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                    SourceWidth, SourceHeight, format, GpuTextureUsage.Sampled));
                gd.UpdateTexture(source, Checkerboard(format), 0, 0, SourceWidth, SourceHeight);

                byte[] rgba = Run(gd, copy, SourceWidth, SourceHeight, source);

                for (int i = 0; i < expected.Length; i++)
                {
                    if (rgba[i] == expected[i]) continue;
                    int pixel = i / 4;
                    Assert.Fail($"{format}: pixel ({pixel % SourceWidth},{pixel / SourceWidth}) channel {i % 4} "
                        + $"was {rgba[i]}, expected {expected[i]}");
                }
            }
        }

        // A 64x32 one-pixel checkerboard of two colours with distinct channels, in the storage order of format.
        static byte[] Checkerboard(GpuPixelFormat format)
        {
            byte[] even = { 250, 20, 90 };
            byte[] odd = { 10, 180, 240 };
            bool bgra = format == GpuPixelFormat.B8G8R8A8UNorm;
            var bytes = new byte[SourceWidth * SourceHeight * 4];
            for (int y = 0; y < SourceHeight; y++)
                for (int x = 0; x < SourceWidth; x++)
                {
                    byte[] c = (x + y) % 2 == 0 ? even : odd;
                    int i = (y * SourceWidth + x) * 4;
                    bytes[i + 0] = bgra ? c[2] : c[0];
                    bytes[i + 1] = c[1];
                    bytes[i + 2] = bgra ? c[0] : c[2];
                    bytes[i + 3] = 255;
                }
            return bytes;
        }

        // Records every source into one destination, in order, in ONE recording, then reads the destination back.
        // Every record goes through retire, and a set it replaces is freed only after the submit has completed.
        static byte[] Run(IGpuDevice gd, GpuScaledCopy copy, int width, int height, params IGpuTexture[] sources)
        {
            IGpuResourceFactory f = gd.Factory;
            var retired = new List<IDisposable>();
            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer destination = f.CreateFramebuffer(null, target);
            using (IGpuCommandList commands = f.CreateCommandList())
            {
                using (GpuRecording.Open(gd, commands, nameof(GpuScaledCopyGpuTests)))
                    foreach (IGpuTexture source in sources) copy.Record(commands, source, destination, retired.Add);
                gd.Submit(commands);
                gd.WaitForIdle();
            }
            foreach (IDisposable set in retired) set.Dispose();
            return GpuReadback.ToRgba(gd, target, width, height);
        }

        // A 64x32 source of four solid quadrants, uploaded in the storage order of its format.
        static IGpuTexture CreateQuadrantSource(IGpuDevice gd, GpuPixelFormat format, byte[][] quadrants)
        {
            bool bgra = format == GpuPixelFormat.B8G8R8A8UNorm;
            var bytes = new byte[SourceWidth * SourceHeight * 4];
            for (int y = 0; y < SourceHeight; y++)
                for (int x = 0; x < SourceWidth; x++)
                {
                    byte[] c = QuadrantAt(quadrants, x, y, SourceWidth, SourceHeight);
                    int i = (y * SourceWidth + x) * 4;
                    bytes[i + 0] = bgra ? c[2] : c[0];
                    bytes[i + 1] = c[1];
                    bytes[i + 2] = bgra ? c[0] : c[2];
                    bytes[i + 3] = 255;
                }

            IGpuTexture texture = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                SourceWidth, SourceHeight, format, GpuTextureUsage.Sampled));
            gd.UpdateTexture(texture, bytes, 0, 0, SourceWidth, SourceHeight);
            return texture;
        }

        static byte[] QuadrantAt(byte[][] quadrants, int x, int y, int width, int height)
            => quadrants[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];

        // Within two texels of the quadrant split on this axis: texels 30 to 33 of 64, 14 to 17 of 32.
        static bool NearEdge(int i, int extent) => Math.Abs(i - extent / 2 + 0.5) < 2.5;

        static void AssertPixel(byte[] rgba, int width, int x, int y, byte[] expected)
        {
            int i = (y * width + x) * 4;
            string where = $"pixel ({x},{y}) was ({rgba[i]}, {rgba[i + 1]}, {rgba[i + 2]}, {rgba[i + 3]}), "
                + $"expected ({expected[0]}, {expected[1]}, {expected[2]}, 255) within {Tolerance}";
            for (int c = 0; c < 3; c++)
                Assert.True(Math.Abs(rgba[i + c] - expected[c]) <= Tolerance, where);
            Assert.True(rgba[i + 3] == 255, where);
        }
    }
}
