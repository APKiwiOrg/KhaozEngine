using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class UnitAxisQuantizerTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 127)]
    [InlineData(-1f, -127)]
    [InlineData(0.5f, 64)]    // 0.5 * 127 = 63.5, rounded away-from-zero
    [InlineData(-0.5f, -64)]
    [InlineData(2f, 127)]     // clamped
    [InlineData(-2f, -127)]   // clamped
    public void Quantize_MatchesPinnedValues(float value, int expected)
    {
        Assert.Equal((sbyte)expected, UnitAxisQuantizer.Quantize(value));
    }

    [Theory]
    [InlineData((sbyte)0, 0f)]
    [InlineData((sbyte)127, 1f)]
    [InlineData((sbyte)-127, -1f)]
    public void Dequantize_MatchesPinnedValues(sbyte value, float expected)
    {
        Assert.Equal(expected, UnitAxisQuantizer.Dequantize(value), 5);
    }

    [Fact]
    public void Dequantize_MinSByte_StaysWithinUnitRange()
    {
        // Quantize never emits -128 (it clamps to [-127,127]), but a hostile or garbage wire byte
        // can carry it. Dequantize must keep the documented [-1,1] contract regardless of the byte.
        float v = UnitAxisQuantizer.Dequantize(-128);
        Assert.True(v >= -1f && v <= 1f, $"Dequantize(-128) = {v} escaped [-1,1]");
        Assert.Equal(-1f, v, 5);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(-0.8f)]
    [InlineData(0.999f)]
    public void RoundTrip_WithinOneStep(float value)
    {
        float restored = UnitAxisQuantizer.Dequantize(UnitAxisQuantizer.Quantize(value));
        Assert.True(System.MathF.Abs(restored - value) <= 1f / 127f);
    }
}
