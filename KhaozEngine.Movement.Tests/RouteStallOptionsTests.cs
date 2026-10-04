using System;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RouteStallOptionsTests
{
    [Theory]
    [InlineData(0, 0.1f, "windowTicks")]
    [InlineData(-1, 0.1f, "windowTicks")]
    [InlineData(65536, 0.1f, "windowTicks")]
    [InlineData(15, 0f, "travelMetres")]
    [InlineData(15, -0.1f, "travelMetres")]
    [InlineData(15, float.NaN, "travelMetres")]
    [InlineData(15, float.PositiveInfinity, "travelMetres")]
    public void OptionsRequireAWindowAndAPositiveFiniteTravel(int ticks, float travel, string parameter)
        => Assert.Equal(parameter,
            Assert.Throws<ArgumentOutOfRangeException>(() => new RouteStallOptions(ticks, travel)).ParamName);

    [Fact]
    public void TheWindowBoundIsAccepted()
        => Assert.Equal(65535, new RouteStallOptions(65535, 0.1f).WindowTicks);

    [Fact]
    public void EqualWindowsAreEqual()
    {
        Assert.True(new RouteStallOptions(15, 0.1f) == new RouteStallOptions(15, 0.1f));
        Assert.True(new RouteStallOptions(15, 0.1f) != new RouteStallOptions(16, 0.1f));
    }
}
