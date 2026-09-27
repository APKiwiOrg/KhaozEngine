using System;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The luma-weighted supersampled reference on synthetic input, device free.</summary>
    public sealed class LumaWeightedReferenceTests
    {
        static byte[] Fill(int w, int h, byte r, byte g, byte b)
        {
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
            }
            return rgba;
        }

        [Fact]
        public void AFlatBlockKeepsItsColour()
        {
            byte[] small = LumaWeightedReference.Downsample(Fill(4, 4, 200, 120, 40), 4, 4, 2);
            for (int i = 0; i < small.Length; i += 4)
                Assert.Equal(new byte[] { 200, 120, 40, 255 }, small[i..(i + 4)]);
        }

        [Fact]
        public void AWhiteBarAThirdOfAPixelWideOnBlackAveragesToAFifthWhereTheBoxGivesAThird()
        {
            // One white column of three. White weighs 1 / 2, so the weighted mean is 1 / 6, which maps back to 1 / 5.
            byte[] block = Fill(3, 3, 0, 0, 0);
            for (int y = 0; y < 3; y++) block[y * 12] = block[y * 12 + 1] = block[y * 12 + 2] = 255;

            Assert.Equal(new byte[] { 51, 51, 51, 255 }, LumaWeightedReference.Downsample(block, 3, 3, 3));
            Assert.Equal(new byte[] { 85, 85, 85, 255 }, Rgba8Stats.BoxDownsample(block, 3, 3, 3));
        }

        [Fact]
        public void ASizeThatIsNotAMultipleOfTheFactorIsRefused() =>
            Assert.Throws<ArgumentException>(() => LumaWeightedReference.Downsample(Fill(5, 4, 0, 0, 0), 5, 4, 2));
    }
}
