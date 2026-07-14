using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

public class VersionOrderTests
{
    [Theory]
    [InlineData("0.7.9", "0.7.10", -1)]    // numeric, not lexicographic
    [InlineData("0.7.10", "0.7.9", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2", "1.2.0", 0)]        // missing segment counts as 0
    [InlineData("1.2.0", "1.2", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    public void Compare_OrdersNumerically(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, System.Math.Sign(VersionOrder.Compare(a, b)));
    }

    [Theory]
    [InlineData(null, "1.0.0", -1)]        // null = empty = all-zero, below any real version
    [InlineData("", "0.0.0", 0)]
    [InlineData("1.0.0", null, 1)]
    [InlineData("abc", "1.0.0", -1)]       // non-numeric segment counts as 0
    public void Compare_HandlesNullBlankAndGarbage(string? a, string? b, int expectedSign)
    {
        Assert.Equal(expectedSign, System.Math.Sign(VersionOrder.Compare(a, b)));
    }

    [Fact]
    public void IsBelow_IsStrict()
    {
        Assert.True(VersionOrder.IsBelow("0.7.9", "0.7.10"));
        Assert.False(VersionOrder.IsBelow("0.7.10", "0.7.10"));
        Assert.False(VersionOrder.IsBelow("0.7.11", "0.7.10"));
    }
}
