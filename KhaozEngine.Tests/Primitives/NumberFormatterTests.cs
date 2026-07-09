using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

/// <summary>Coverage for the large-number formatter (notation modes + edge cases). Culture-invariant output.</summary>
public class NumberFormatterTests
{
    [Theory]
    [InlineData(0d, "0.0")]
    [InlineData(5d, "5.0")]
    [InlineData(999d, "999.0")]
    [InlineData(1000d, "1.00K")]
    [InlineData(1500d, "1.50K")]
    [InlineData(1_500_000d, "1.50M")]
    [InlineData(1_500_000_000d, "1.50B")]
    [InlineData(1.5e12, "1.50T")]
    [InlineData(1.5e33, "1.50Dc")]
    public void Simple_uses_suffixes(double value, string expected)
        => Assert.Equal(expected, NumberFormatter.Format(value, NumberNotation.Simple));

    [Fact]
    public void Simple_falls_back_to_scientific_beyond_the_suffix_table()
        => Assert.Equal("1.00E+036", NumberFormatter.Format(1e36, NumberNotation.Simple));

    [Fact]
    public void Negative_values_keep_the_sign()
        => Assert.Equal("-1.50K", NumberFormatter.Format(-1500, NumberNotation.Simple));

    [Fact]
    public void Nan_and_infinity_have_safe_sentinels()
    {
        Assert.Equal("0", NumberFormatter.Format(double.NaN));
        Assert.Equal("Inf", NumberFormatter.Format(double.PositiveInfinity));
        Assert.Equal("Inf", NumberFormatter.Format(double.NegativeInfinity));
    }

    [Fact]
    public void FormatInt_drops_small_decimals_but_still_suffixes()
    {
        Assert.Equal("999", NumberFormatter.FormatInt(999, NumberNotation.Simple));
        Assert.Equal("1.50K", NumberFormatter.FormatInt(1500, NumberNotation.Simple));
    }

    [Theory]
    [InlineData(999d, "999.0")]        // below 1000 stays a plain fixed-point value
    [InlineData(1234d, "1.23E+003")]
    public void Scientific_uses_exponent(double value, string expected)
        => Assert.Equal(expected, NumberFormatter.Format(value, NumberNotation.Scientific));

    [Theory]
    [InlineData(999d, "999.0")]
    [InlineData(45_000d, "45.00E3")]   // exponent snapped to a multiple of 3
    [InlineData(1_500_000d, "1.50E6")]
    public void Engineering_snaps_exponent_to_multiples_of_three(double value, string expected)
        => Assert.Equal(expected, NumberFormatter.Format(value, NumberNotation.Engineering));

    [Fact]
    public void Default_notation_is_used_by_the_parameterless_overloads()
    {
        NumberNotation saved = NumberFormatter.Notation;
        try
        {
            NumberFormatter.Notation = NumberNotation.Simple;
            Assert.Equal("1.50K", NumberFormatter.Format(1500));

            NumberFormatter.Notation = NumberNotation.Scientific;
            Assert.Equal("1.23E+003", NumberFormatter.Format(1234));
        }
        finally
        {
            NumberFormatter.Notation = saved;
        }
    }
}
