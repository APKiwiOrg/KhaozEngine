using System;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Content
{
    public class ColorHexTests
    {
        [Fact]
        public void FromHex_ParsesRRGGBB_AsOpaque()
        {
            var c = Color.FromHex("#22D3EE");
            Assert.Equal(0x22 / 255f, c.R, 4);
            Assert.Equal(0xD3 / 255f, c.G, 4);
            Assert.Equal(0xEE / 255f, c.B, 4);
            Assert.Equal(1f, c.A, 4);
        }

        [Fact]
        public void FromHex_ParsesRRGGBBAA_Alpha()
        {
            var c = Color.FromHex("FF000080");
            Assert.Equal(1f, c.R, 4);
            Assert.Equal(0f, c.G, 4);
            Assert.Equal(0f, c.B, 4);
            Assert.Equal(0x80 / 255f, c.A, 4);
        }

        [Fact]
        public void FromHex_HashIsOptional()
        {
            Assert.Equal(Color.FromHex("#10203040"), Color.FromHex("10203040"));
        }

        [Theory]
        [InlineData("12345")]      // too short
        [InlineData("1234567")]    // 7
        [InlineData("zzzzzz")]     // not hex
        public void FromHex_RejectsBadInput(string bad)
        {
            Assert.ThrowsAny<Exception>(() => Color.FromHex(bad));
        }

        [Fact]
        public void ToHex_RoundTrips()
        {
            var original = "#22D3EEFF";
            Assert.Equal(original, Color.ToHex(Color.FromHex(original)));
        }

        [Fact]
        public void ToHex_ClampsOutOfRange()
        {
            Assert.Equal("#00FFFFFF", Color.ToHex(new Color(-1f, 2f, 1f, 1f)));
        }
    }
}
