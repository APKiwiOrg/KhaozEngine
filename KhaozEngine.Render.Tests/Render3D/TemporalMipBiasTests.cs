using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The material mip bias: minus log2 of the display over internal scale plus the offset, and exactly zero
    /// when the frame does not resolve.</summary>
    public sealed class TemporalMipBiasTests
    {
        [Fact]
        public void OffIsExactlyZeroInBothLanes()
        {
            Vector2 lod = TemporalMipBias.For(false, 2f, -0.5f);
            Assert.Equal(0, BitConverter.SingleToInt32Bits(lod.X));
            Assert.Equal(0, BitConverter.SingleToInt32Bits(lod.Y));
        }

        [Theory]
        [InlineData(1f, -0.5f, -0.5)]       // Native
        [InlineData(1.5f, -0.5f, -1.085)]   // Quality, 1/1.5
        [InlineData(1.7f, -0.5f, -1.266)]   // Balanced, 1/1.7
        [InlineData(2f, -0.5f, -1.5)]       // Performance, 1/2
        [InlineData(3f, -0.5f, -2.085)]     // UltraPerformance, 1/3
        [InlineData(2f, 1f, 0.0)]           // the offset cancels the ratio
        public void TheBiasIsLog2OfTheRatioPlusTheOffset(float displayOverInternal, float offset, double want)
        {
            Vector2 lod = TemporalMipBias.For(true, displayOverInternal, offset);
            Assert.Equal(want, lod.X, 3);
            Assert.Equal(Math.Pow(2.0, lod.X) - 1.0, lod.Y, 1e-6);
        }

        [Fact]
        public void AnOffsetThatCancelsTheRatioGivesExactZeros()
        {
            Vector2 lod = TemporalMipBias.For(true, 2f, 1f);
            Assert.Equal(0, BitConverter.SingleToInt32Bits(lod.X));
            Assert.Equal(0, BitConverter.SingleToInt32Bits(lod.Y));
        }

        [Fact]
        public void TheOffsetIsClamped()
        {
            Assert.Equal(TemporalMipBias.MinOffset, TemporalMipBias.For(true, 1f, -9f).X, 5);
            Assert.Equal(TemporalMipBias.MaxOffset, TemporalMipBias.For(true, 1f, 5f).X, 5);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void ANonFiniteOffsetCountsAsZero(float offset)
        {
            Vector2 lod = TemporalMipBias.For(true, 2f, offset);
            Assert.Equal(-1f, lod.X);
            Assert.Equal(-0.5f, lod.Y);
        }

        [Fact]
        public void TheBiasIsClamped()
        {
            Vector2 low = TemporalMipBias.For(true, 32f, -2f);
            Assert.Equal(TemporalMipBias.MinBias, low.X);
            Assert.Equal(MathF.Pow(2f, TemporalMipBias.MinBias) - 1f, low.Y, 6);
            Vector2 high = TemporalMipBias.For(true, 0.1f, 1f);
            Assert.Equal(TemporalMipBias.MaxBias, high.X);
            Assert.Equal(MathF.Pow(2f, TemporalMipBias.MaxBias) - 1f, high.Y, 6);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void ADegenerateScaleGivesNoBias(float displayOverInternal) =>
            Assert.Equal(Vector2.Zero, TemporalMipBias.For(true, displayOverInternal, -0.5f));
    }
}
