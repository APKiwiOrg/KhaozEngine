using System;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterOpticsFieldTests
    {
        [Fact]
        public void CallerMutationCannotRewriteTheOpticalBed()
        {
            float[] values = [-0.2f, 2f, 30f, 100f];
            var field = new WaterOpticsField(2, 2, values);
            values[0] = 99f;

            Assert.Equal(-0.2f, field.SampleDepth(0, 0));
            Assert.Equal(100f, field.SampleDepth(1, 1));
            Assert.InRange(MathF.Abs(field.SampleDepth(.5f, .5f) - 32.95f), 0, .00001f);
            Assert.Equal(16L, field.SampleBytes);
            Assert.Equal(3f, WaterOpticsMath.BedDepth(2, 5, 6));
            Assert.Equal(-0.2f, field.RestDepths[0]);
        }

        [Fact]
        public void ASpanSliceIsCopiedWithoutRetainingCallerStorage()
        {
            float[] values = [float.NaN, 2f, 30f, 100f, -0.25f, float.NaN];
            var field = new WaterOpticsField(2, 2, values.AsSpan(1, 4));
            Array.Fill(values, float.NaN);

            Assert.Equal(4, field.RestDepths.Length);
            Assert.Equal(2f, field.RestDepths[0]);
            Assert.Equal(-0.25f, field.RestDepths[3]);
            Assert.Equal(32.9375f, field.SampleDepth(.5f, .5f));
        }

        [Theory]
        [InlineData(0f, 0f, -0.2f)]
        [InlineData(1f, 0f, 2f)]
        [InlineData(0f, 1f, 30f)]
        [InlineData(1f, 1f, 100f)]
        public void CornersPreserveSignedBanksAndRepresentativeDepths(float u, float v, float expected)
        {
            var field = new WaterOpticsField(2, 2, [-0.2f, 2f, 30f, 100f]);

            Assert.Equal(expected, field.SampleDepth(u, v));
        }

        [Theory]
        [InlineData(.25f, .25f, 7.5f)]
        [InlineData(.75f, .5f, 27f)]
        [InlineData(.5f, 0f, 4f)]
        public void RectangularLatticesUseRowMajorCellsAndIndependentAxisScales(float u, float v, float expected)
        {
            // At (.25, .25), the row blends are 2 and 24, then one quarter of the way between is 7.5.
            var field = new WaterOpticsField(3, 2, [0f, 4f, 16f, 20f, 28f, 60f]);
            var transposed = new WaterOpticsField(2, 3, [0f, 20f, 4f, 28f, 16f, 60f]);

            Assert.Equal(3, field.Width);
            Assert.Equal(2, field.Height);
            Assert.Equal(24L, field.SampleBytes);
            Assert.Equal(expected, field.SampleDepth(u, v));
            Assert.Equal(expected, transposed.SampleDepth(v, u));
        }

        [Theory]
        [InlineData(.25f, 1f, 86f)]
        [InlineData(.75f, 1f, 106f)]
        [InlineData(1f, .25f, 38f)]
        [InlineData(1f, .75f, 90f)]
        [InlineData(1f, 1f, 120f)]
        public void LastRowAndColumnNeverReadPastThePayload(float u, float v, float expected)
        {
            var field = new WaterOpticsField(3, 3, [0f, 4f, 16f, 20f, 28f, 60f, 80f, 92f, 120f]);

            Assert.Equal(expected, field.SampleDepth(u, v));
        }

        [Fact]
        public void NegativeBanksRemainSigned()
        {
            var field = new WaterOpticsField(2, 2, [-.5f, 0f, .5f, 1f]);

            Assert.Equal(-.5f, field.SampleDepth(0f, 0f));
            Assert.Equal(0f, field.SampleDepth(1f, 0f));
            Assert.Equal(-.375f, field.SampleDepth(.25f, 0f));
            Assert.Equal(.25f, field.SampleDepth(.5f, .5f));
        }

        [Theory]
        [InlineData(2f, 2.013671875f)]
        [InlineData(30f, 30.013671875f)]
        [InlineData(100f, 100.013671875f)]
        public void SubCentimetreBedChangesSurviveAtRepresentativeDepths(float depth, float expected)
        {
            // The plane rises 1/128 m on X and 1/64 m on Z. At (.25, .75) it adds 7/512 m.
            var field = new WaterOpticsField(2, 2,
                [depth, depth + .0078125f, depth + .015625f, depth + .0234375f]);

            Assert.Equal(expected, field.SampleDepth(.25f, .75f));
        }

        [Fact]
        public void OppositeFiniteSignedExtremesDoNotOverflowInterpolation()
        {
            var field = new WaterOpticsField(2, 2,
                [-float.MaxValue, float.MaxValue, float.MaxValue, -float.MaxValue]);

            Assert.Equal(-float.MaxValue, field.SampleDepth(0f, 0f));
            Assert.Equal(float.MaxValue, field.SampleDepth(1f, 0f));
            Assert.Equal(0f, field.SampleDepth(.5f, .5f));
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 2)]
        [InlineData(-1, 2)]
        [InlineData(1025, 2)]
        [InlineData(int.MaxValue, 2)]
        [InlineData(2, 0)]
        [InlineData(2, 1)]
        [InlineData(2, -1)]
        [InlineData(2, 1025)]
        [InlineData(2, int.MaxValue)]
        [InlineData(65536, 65536)]
        public void DimensionsAndPayloadAreValidated(int width, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaterOpticsField(width, height, []));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        [InlineData(7)]
        public void PayloadLengthMustExactlyMatchTheRectangularSampleCount(int length)
        {
            Assert.Throws<ArgumentException>(() => new WaterOpticsField(3, 2, new float[length]));
        }

        [Theory]
        [InlineData(2, 1024, 8192L)]
        [InlineData(1024, 2, 8192L)]
        [InlineData(1024, 1024, 4194304L)]
        public void MaximumDimensionsRetainTheLastSampleAndAccountForItsBytes(int width, int height, long bytes)
        {
            var values = new float[checked(width * height)];
            values[^1] = 100f;
            var field = new WaterOpticsField(width, height, values);

            Assert.Equal(width, field.Width);
            Assert.Equal(height, field.Height);
            Assert.Equal(bytes, field.SampleBytes);
            Assert.Equal(100f, field.SampleDepth(1f, 1f));
        }

        [Theory]
        [InlineData(0, float.NaN)]
        [InlineData(1, float.PositiveInfinity)]
        [InlineData(2, float.NegativeInfinity)]
        [InlineData(3, float.NaN)]
        public void NonFiniteDepthsAreRefused(int index, float value)
        {
            float[] values = [2f, 30f, 100f, -.2f];
            values[index] = value;

            Assert.Throws<ArgumentException>(() => new WaterOpticsField(2, 2, values));
        }

        [Theory]
        [InlineData(-float.Epsilon, .5f)]
        [InlineData(1.0000001f, .5f)]
        [InlineData(.5f, -float.Epsilon)]
        [InlineData(.5f, 1.0000001f)]
        [InlineData(float.NaN, .5f)]
        [InlineData(float.PositiveInfinity, .5f)]
        [InlineData(float.NegativeInfinity, .5f)]
        [InlineData(.5f, float.NaN)]
        [InlineData(.5f, float.PositiveInfinity)]
        [InlineData(.5f, float.NegativeInfinity)]
        public void InvalidCoordinatesAreRefusedRatherThanClamped(float u, float v)
        {
            var field = new WaterOpticsField(2, 2, [2f, 30f, 100f, -.2f]);

            Assert.Throws<ArgumentOutOfRangeException>(() => field.SampleDepth(u, v));
        }

        [Theory]
        [InlineData(6f, 3f)]
        [InlineData(5f, 2f)]
        [InlineData(4f, 1f)]
        [InlineData(3f, 0f)]
        [InlineData(2f, 0f)]
        public void ReferenceLevelStaysFixedWhenWaterMoves(float displacedSurfaceY, float expected)
        {
            var field = new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]);

            Assert.Equal(expected, WaterOpticsMath.BedDepth(field.SampleDepth(.5f, .5f), 5f, displacedSurfaceY));
            Assert.Equal(2f, field.SampleDepth(.5f, .5f));
        }

        [Theory]
        [InlineData(5f, 0f)]
        [InlineData(5.25f, 0f)]
        [InlineData(5.5f, .25f)]
        public void RisingWaterMustOvertopTheSignedBankBeforeItHasBedDepth(float displacedSurfaceY, float expected)
        {
            Assert.Equal(expected, WaterOpticsMath.BedDepth(-.25f, 5f, displacedSurfaceY));
        }
    }
}
