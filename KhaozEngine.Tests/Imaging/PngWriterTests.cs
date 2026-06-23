using System;
using System.IO;
using KhaozEngine.Imaging;
using StbImageSharp;
using Xunit;

namespace KhaozEngine.Tests.Imaging
{
    public class PngWriterTests
    {
        // A 2x3 RGBA image with distinct pixels (incl. partial alpha) to exercise stride + all channels.
        static byte[] SampleRgba() => new byte[]
        {
            255, 0,   0,   255,   0,   255, 0,   255,   // row 0: red, green
            0,   0,   255, 255,   255, 255, 0,   128,   // row 1: blue, semi-yellow
            10,  20,  30,  40,    200, 150, 100, 255,   // row 2: dark, tan
        };

        [Fact]
        public void Encode_emits_the_png_signature()
        {
            byte[] png = PngWriter.Encode(SampleRgba(), 2, 3);
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < sig.Length; i++) Assert.Equal(sig[i], png[i]);
        }

        [Fact]
        public void Encode_roundtrips_through_a_decoder()
        {
            byte[] rgba = SampleRgba();
            byte[] png = PngWriter.Encode(rgba, 2, 3);

            ImageResult decoded = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
            Assert.Equal(2, decoded.Width);
            Assert.Equal(3, decoded.Height);
            Assert.Equal(rgba, decoded.Data);   // byte-identical pixels after encode -> decode
        }

        [Fact]
        public void Encode_rejects_a_wrong_length_buffer()
        {
            Assert.Throws<ArgumentException>(() => PngWriter.Encode(new byte[7], 2, 3));
        }

        [Fact]
        public void Save_writes_a_decodable_png_file()
        {
            byte[] rgba = SampleRgba();
            string path = Path.Combine(Path.GetTempPath(), "ke-pngwriter-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                PngWriter.Save(path, rgba, 2, 3);
                Assert.True(File.Exists(path));
                ImageResult decoded = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
                Assert.Equal(2, decoded.Width);
                Assert.Equal(3, decoded.Height);
                Assert.Equal(rgba, decoded.Data);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
