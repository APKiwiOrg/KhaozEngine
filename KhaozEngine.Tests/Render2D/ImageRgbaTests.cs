using System;
using System.IO;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    // ImageRgba is a CPU-side decoded image (no GPU): Render2DSurface.LoadImageRgba feeds it for opaque-pixel
    // collision masks. These exercise decode + the alpha/opacity queries fully headlessly, round-tripping
    // through the existing Png encoder so no asset file is needed.
    public class ImageRgbaTests
    {
        // 2x2 RGBA: TL opaque red, TR fully transparent, BL semi (alpha 128), BR opaque blue.
        static byte[] Sample() => new byte[]
        {
            255, 0,   0,   255,   0,   0,   0,   0,     // row 0: red(opaque), transparent
            9,   9,   9,   128,   0,   0,   255, 255,   // row 1: grey(alpha 128), blue(opaque)
        };

        [Fact]
        public void Decode_recovers_dimensions_and_pixels()
        {
            byte[] png = Png.Encode(Sample(), 2, 2);

            ImageRgba img = ImageRgba.Decode(png);

            Assert.Equal(2, img.Width);
            Assert.Equal(2, img.Height);
            Assert.Equal(Sample(), img.Pixels);
        }

        [Fact]
        public void Load_reads_a_png_file_from_disk()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ke-imagergba-{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, Png.Encode(Sample(), 2, 2));
            try
            {
                ImageRgba img = ImageRgba.Load(path);
                Assert.Equal(2, img.Width);
                Assert.Equal(2, img.Height);
                Assert.Equal((byte)255, img.AlphaAt(0, 0));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void AlphaAt_returns_the_alpha_channel_at_a_pixel()
        {
            ImageRgba img = ImageRgba.Decode(Png.Encode(Sample(), 2, 2));

            Assert.Equal((byte)255, img.AlphaAt(0, 0));   // opaque red
            Assert.Equal((byte)0, img.AlphaAt(1, 0));     // transparent
            Assert.Equal((byte)128, img.AlphaAt(0, 1));   // semi
            Assert.Equal((byte)255, img.AlphaAt(1, 1));   // opaque blue
        }

        [Fact]
        public void IsOpaqueAt_thresholds_alpha_for_a_collision_mask()
        {
            ImageRgba img = ImageRgba.Decode(Png.Encode(Sample(), 2, 2));

            Assert.True(img.IsOpaqueAt(0, 0));            // 255 >= default threshold 1
            Assert.False(img.IsOpaqueAt(1, 0));           // 0 < 1
            Assert.True(img.IsOpaqueAt(0, 1));            // 128 >= 1
            Assert.False(img.IsOpaqueAt(0, 1, threshold: 200)); // 128 < 200
        }

        [Fact]
        public void AlphaAt_rejects_out_of_bounds()
        {
            ImageRgba img = ImageRgba.Decode(Png.Encode(Sample(), 2, 2));

            Assert.Throws<ArgumentOutOfRangeException>(() => { img.AlphaAt(2, 0); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { img.AlphaAt(0, -1); });
        }

        [Fact]
        public void Constructor_rejects_a_mismatched_buffer_length()
        {
            Assert.Throws<ArgumentException>(() => new ImageRgba(new byte[7], 2, 2));
        }
    }
}
