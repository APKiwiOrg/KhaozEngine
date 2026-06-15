using System;
using System.Numerics;
using KhaozEngine.Content;
using Xunit;

namespace KhaozEngine.Tests.Content
{
    public class ColorHexTests
    {
        [Fact]
        public void FromHex_ParsesRRGGBB_AsOpaque()
        {
            var c = ColorHex.FromHex("#22D3EE");
            Assert.Equal(0x22 / 255f, c.X, 4);
            Assert.Equal(0xD3 / 255f, c.Y, 4);
            Assert.Equal(0xEE / 255f, c.Z, 4);
            Assert.Equal(1f, c.W, 4);
        }

        [Fact]
        public void FromHex_ParsesRRGGBBAA_Alpha()
        {
            var c = ColorHex.FromHex("FF000080");
            Assert.Equal(1f, c.X, 4);
            Assert.Equal(0f, c.Y, 4);
            Assert.Equal(0f, c.Z, 4);
            Assert.Equal(0x80 / 255f, c.W, 4);
        }

        [Fact]
        public void FromHex_HashIsOptional()
        {
            Assert.Equal(ColorHex.FromHex("#10203040"), ColorHex.FromHex("10203040"));
        }

        [Theory]
        [InlineData("12345")]      // too short
        [InlineData("1234567")]    // 7
        [InlineData("zzzzzz")]     // not hex
        public void FromHex_RejectsBadInput(string bad)
        {
            Assert.ThrowsAny<Exception>(() => ColorHex.FromHex(bad));
        }

        [Fact]
        public void ToHex_RoundTrips()
        {
            var original = "#22D3EEFF";
            Assert.Equal(original, ColorHex.ToHex(ColorHex.FromHex(original)));
        }

        [Fact]
        public void ToHex_ClampsOutOfRange()
        {
            Assert.Equal("#00FFFFFF", ColorHex.ToHex(new Vector4(-1f, 2f, 1f, 1f)));
        }
    }
}
