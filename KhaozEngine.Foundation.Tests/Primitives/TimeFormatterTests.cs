using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

/// <summary>Coverage for the duration formatter: both styles, rounding, and non-finite/edge input.</summary>
public class TimeFormatterTests
{
    [Theory]
    [InlineData(18d, "18s")]
    [InlineData(154d, "2:34")]
    [InlineData(3754d, "1:02:34")]
    [InlineData(273754d, "3d 4:02:34")]
    public void Clock_shows_highest_unit_down(double seconds, string expected)
        => Assert.Equal(expected, TimeFormatter.Format(seconds));   // Clock is the default style

    [Fact]
    public void Clock_rounds_up_to_the_next_whole_second()
    {
        Assert.Equal("2:34", TimeFormatter.Format(153.2));   // ceil(153.2) = 154
        Assert.Equal("1s", TimeFormatter.Format(0.5));       // ceil(0.5) = 1
    }

    [Theory]
    [InlineData(18d, "18s")]
    [InlineData(2730d, "45m 30s")]
    [InlineData(8100d, "2h 15m")]
    [InlineData(86400d, "1d 0h")]
    [InlineData(273754d, "3d 4h")]
    public void Coarse_shows_two_units_from_the_top(double seconds, string expected)
        => Assert.Equal(expected, TimeFormatter.Format(seconds, DurationStyle.Coarse));

    [Theory]
    [InlineData(300d, 1, "5m")]
    [InlineData(300d, 2, "5m 0s")]
    [InlineData(30d, 1, "30s")]
    [InlineData(273754d, 3, "3d 4h 2m")]
    public void Coarse_honours_the_unit_count(double seconds, int units, string expected)
        => Assert.Equal(expected, TimeFormatter.Format(seconds, DurationStyle.Coarse, units));

    [Theory]
    [InlineData(DurationStyle.Clock)]
    [InlineData(DurationStyle.Coarse)]
    public void Zero_and_negative_render_zero_seconds(DurationStyle style)
    {
        Assert.Equal("0s", TimeFormatter.Format(0, style));
        Assert.Equal("0s", TimeFormatter.Format(-42, style));
    }

    [Theory]
    [InlineData(DurationStyle.Clock)]
    [InlineData(DurationStyle.Coarse)]
    public void Non_finite_renders_the_dash_sentinel(DurationStyle style)
    {
        Assert.Equal("---", TimeFormatter.Format(double.NaN, style));
        Assert.Equal("---", TimeFormatter.Format(double.PositiveInfinity, style));
    }
}
