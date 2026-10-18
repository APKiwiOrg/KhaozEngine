using System;
using KhaozEngine.Imaging;
using StbImageSharp;
using Xunit;

namespace KhaozEngine.Tests.Imaging
{
    public class JpegWriterTests
    {
        [Fact]
        public void Encode_brackets_the_stream_with_SOI_and_EOI()
        {
            byte[] jpg = JpegWriter.Encode(Gradient(16, 16), 16, 16, 90);
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpg[..2]);
            Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpg[^2..]);
        }

        [Fact]
        public void Encode_writes_a_baseline_SOF0_with_4_2_0_sampling()
        {
            byte[] jpg = JpegWriter.Encode(Gradient(40, 24), 40, 24, 90);
            byte[] sof = Segment(jpg, 0xC0);
            Assert.Equal(8, sof[0]);                         // precision
            Assert.Equal(24, (sof[1] << 8) | sof[2]);        // height
            Assert.Equal(40, (sof[3] << 8) | sof[4]);        // width
            Assert.Equal(3, sof[5]);                         // components
            Assert.Equal(0x22, sof[7]);
            Assert.Equal(0x11, sof[10]);
            Assert.Equal(0x11, sof[13]);
            Assert.Null(SegmentOrNull(jpg, 0xC2));           // never progressive
        }

        [Theory]
        [InlineData(90, 35.0)]
        [InlineData(50, 30.0)]
        public void A_smooth_gradient_round_trips_through_the_reference_decoder(int quality, double minPsnr)
        {
            byte[] src = Gradient(97, 61);
            ImageResult d = ImageResult.FromMemory(JpegWriter.Encode(src, 97, 61, quality), ColorComponents.RedGreenBlue);
            Assert.Equal((97, 61), (d.Width, d.Height));
            double psnr = Psnr(src, d.Data);
            Assert.True(psnr >= minPsnr, $"PSNR {psnr:0.00} dB is below {minPsnr} dB at quality {quality}.");
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(7, 9)]
        [InlineData(17, 15)]
        [InlineData(33, 1)]
        public void Sizes_that_are_not_multiples_of_the_MCU_decode_at_their_own_size(int w, int h)
        {
            byte[] src = Solid(w, h, 40, 120, 200);
            ImageResult d = ImageResult.FromMemory(JpegWriter.Encode(src, w, h, 90), ColorComponents.RedGreenBlue);
            Assert.Equal((w, h), (d.Width, d.Height));
            for (int p = 0; p < w * h; p++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int diff = Math.Abs(d.Data[p * 3 + c] - src[p * 4 + c]);
                    Assert.True(diff <= 4, $"pixel {p} channel {c} is off by {diff}.");
                }
            }
        }

        [Theory]
        [InlineData(255, 0, 0)]
        [InlineData(0, 255, 0)]
        [InlineData(0, 0, 255)]
        public void Primary_colours_keep_their_channel_order(byte r, byte g, byte b)
        {
            ImageResult d = ImageResult.FromMemory(JpegWriter.Encode(Solid(32, 32, r, g, b), 32, 32, 90), ColorComponents.RedGreenBlue);
            int centre = (16 * 32 + 16) * 3;
            Assert.InRange(d.Data[centre], Math.Max(0, r - 8), Math.Min(255, r + 8));
            Assert.InRange(d.Data[centre + 1], Math.Max(0, g - 8), Math.Min(255, g + 8));
            Assert.InRange(d.Data[centre + 2], Math.Max(0, b - 8), Math.Min(255, b + 8));
        }

        [Fact]
        public void Alpha_is_ignored()
        {
            byte[] opaque = Gradient(24, 24);
            byte[] clear = (byte[])opaque.Clone();
            for (int i = 3; i < clear.Length; i += 4) clear[i] = 0;
            Assert.Equal(JpegWriter.Encode(opaque, 24, 24, 90), JpegWriter.Encode(clear, 24, 24, 90));
        }

        [Fact]
        public void The_output_is_deterministic()
        {
            byte[] src = Textured(64, 48);
            Assert.Equal(JpegWriter.Encode(src, 64, 48, 70), JpegWriter.Encode(src, 64, 48, 70));
        }

        [Fact]
        public void Higher_quality_costs_bytes_and_buys_fidelity()
        {
            byte[] src = Textured(128, 96);
            byte[] low = JpegWriter.Encode(src, 128, 96, 30);
            byte[] high = JpegWriter.Encode(src, 128, 96, 90);
            Assert.True(low.Length < high.Length, $"q30 is {low.Length} bytes, q90 is {high.Length} bytes.");
            double lowPsnr = Psnr(src, ImageResult.FromMemory(low, ColorComponents.RedGreenBlue).Data);
            double highPsnr = Psnr(src, ImageResult.FromMemory(high, ColorComponents.RedGreenBlue).Data);
            Assert.True(lowPsnr < highPsnr, $"q30 PSNR {lowPsnr:0.00} dB, q90 PSNR {highPsnr:0.00} dB.");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public void A_quality_outside_1_to_100_is_refused(int q)
            => Assert.Throws<ArgumentOutOfRangeException>(() => JpegWriter.Encode(Gradient(8, 8), 8, 8, q));

        [Theory]
        [InlineData(0, 8)]
        [InlineData(8, -1)]
        [InlineData(65536, 1)]
        public void A_size_outside_1_to_65535_is_refused(int w, int h)
            => Assert.Throws<ArgumentOutOfRangeException>(() => JpegWriter.Encode(new byte[4], w, h, 70));

        [Fact]
        public void A_buffer_that_is_not_width_times_height_times_four_is_refused()
            => Assert.Throws<ArgumentException>(() => JpegWriter.Encode(new byte[8 * 8 * 4 - 1], 8, 8, 70));

        // R ramps across x, G ramps down y, B is mid grey, alpha opaque.
        static byte[] Gradient(int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    rgba[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                    rgba[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                    rgba[i + 2] = 128;
                    rgba[i + 3] = 255;
                }
            }
            return rgba;
        }

        // The gradient plus a deterministic high-frequency ripple, so the AC coefficients carry real energy.
        static byte[] Textured(int w, int h)
        {
            byte[] rgba = Gradient(w, h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    int ripple = (x * 31 + y * 17) % 64 - 32;
                    for (int c = 0; c < 3; c++) rgba[i + c] = (byte)Math.Clamp(rgba[i + c] + ripple, 0, 255);
                }
            }
            return rgba;
        }

        static byte[] Solid(int w, int h, byte r, byte g, byte b)
        {
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
            return rgba;
        }

        // The payload of the first marker segment with this code, after its two length bytes, searched up to SOS.
        static byte[]? SegmentOrNull(byte[] jpg, byte marker)
        {
            int i = 2;
            while (i + 4 <= jpg.Length && jpg[i] == 0xFF)
            {
                byte code = jpg[i + 1];
                int length = (jpg[i + 2] << 8) | jpg[i + 3];
                if (code == marker) return jpg[(i + 4)..(i + 2 + length)];
                if (code == 0xDA) return null;
                i += 2 + length;
            }
            return null;
        }

        static byte[] Segment(byte[] jpg, byte marker)
        {
            byte[]? payload = SegmentOrNull(jpg, marker);
            Assert.NotNull(payload);
            return payload;
        }

        static double Psnr(byte[] rgbaSource, byte[] rgbDecoded)
        {
            int pixels = rgbaSource.Length / 4;
            double sum = 0;
            for (int p = 0; p < pixels; p++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double d = rgbaSource[p * 4 + c] - rgbDecoded[p * 3 + c];
                    sum += d * d;
                }
            }
            double mse = sum / (pixels * 3.0);
            return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
        }
    }
}
