using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

/// <summary>Coverage for the large-number formatter (notation modes + edge cases). Culture-invariant output.</summary>
[Collection("NumberFormatterSerial")]
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
    [InlineData(-1500d, "-1.50K")]
    [InlineData(0.05d, "0.05")]     // the reported bug: used to render "0.1" - 2x the real value
    [InlineData(0.1d, "0.10")]
    [InlineData(0.15d, "0.15")]
    [InlineData(0.25d, "0.25")]
    [InlineData(0.5d, "0.50")]
    [InlineData(0.001d, "0.001")]   // smaller magnitudes extend further rather than rounding to "0.00"
    [InlineData(0.0005d, "0.0005")]
    [InlineData(-0.05d, "-0.05")]
    [InlineData(0.9999999d, "1.00")]   // rounds up to the next whole number just below the >= 1 threshold
    [InlineData(1.0d, "1.0")]          // at/above the threshold: unchanged decimalsSmall-only formatting
    [InlineData(1.5d, "1.5")]      // >= 1: regression - unaffected by the small-value fix
    [InlineData(127.3d, "127.3")]
    public void Simple_formats_suffixes_signs_and_fractional_precision(double value, string expected)
        => Assert.Equal(expected, NumberFormatter.Format(value, NumberNotation.Simple));

    [Fact]
    public void Simple_falls_back_to_scientific_beyond_the_suffix_table()
        => Assert.Equal("1.00E+036", NumberFormatter.Format(1e36, NumberNotation.Simple));

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

    [Fact]
    public void Small_value_precision_applies_to_scientific_and_engineering_too()
    {
        // All three notations share the same below-1000 tail, so the fix applies uniformly.
        Assert.Equal("0.05", NumberFormatter.Format(0.05, NumberNotation.Scientific));
        Assert.Equal("0.05", NumberFormatter.Format(0.05, NumberNotation.Engineering));
    }

    [Fact]
    public void Explicit_decimal_counts_are_respected_as_floors_for_small_values()
    {
        // decimalsSmall above the computed floor wins.
        Assert.Equal("0.050", NumberFormatter.Format(0.05, NumberNotation.Simple, decimalsSmall: 3, decimalsLarge: 2));

        // A magnitude smaller than the floor extends past both explicit parameters to stay truthful.
        Assert.Equal("0.0005", NumberFormatter.Format(0.0005, NumberNotation.Simple, decimalsSmall: 1, decimalsLarge: 2));
    }

    [Fact]
    public void Zero_small_decimals_opts_out_of_the_small_value_precision_boost()
    {
        // decimalsSmall: 0 is an explicit "no fractional digits" request (FormatInt's contract) - respected
        // even for sub-1 magnitudes, so integer-like counts never grow spurious decimals. Plain "F0" rounding
        // still applies (0.6 rounds up to "1"), it just never gains the small-value decimal expansion.
        Assert.Equal("1", NumberFormatter.Format(0.6, NumberNotation.Simple, decimalsSmall: 0));
        Assert.Equal("0", NumberFormatter.FormatInt(0.05, NumberNotation.Simple));
    }

    [Fact]
    public void Zero_still_formats_as_a_plain_whole_number()
    {
        // Regression: the small-value branch must not fire for an exact zero.
        Assert.Equal("0.0", NumberFormatter.Format(0d, NumberNotation.Simple));
        Assert.Equal("0", NumberFormatter.FormatInt(0d, NumberNotation.Simple));
    }
}

/// <summary>Isolates the process-wide default notation from every other test collection.</summary>
[CollectionDefinition("NumberFormatterSerial", DisableParallelization = true)]
public sealed class NumberFormatterSerialCollection { }
