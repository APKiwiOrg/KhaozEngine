using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

/// <summary>
/// Full matrix for the shared numeric x.y.z comparer: ordering, equality, null/blank/garbage segments,
/// segment-count mismatches, and leading zeros. This is the one place the rule is pinned, and both
/// KhaozEngine.Updates.UpdateVersion and KhaozEngine.ServerStatus.VersionOrder delegate here and keep
/// their own test suites (which must keep passing unchanged) as thin-wrapper coverage.
/// </summary>
public class VersionComparerTests
{
    [Theory]
    [InlineData("0.7.9", "0.7.10", -1)]    // numeric, not lexicographic
    [InlineData("0.7.10", "0.7.9", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0", "1.1.0", -1)]
    [InlineData("1.2.9", "1.2.10", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void Compare_OrdersNumerically(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, System.Math.Sign(VersionComparer.Compare(a, b)));

    [Theory]
    [InlineData("1.2", "1.2.0", 0)]     // missing trailing segment counts as 0
    [InlineData("1.2.0", "1.2", 0)]
    [InlineData("1", "1.0.0", 0)]
    [InlineData("1.0.0", "1", 0)]
    [InlineData("1.2.1", "1.2", 1)]     // missing segment still loses to a real one
    [InlineData("1.2", "1.2.1", -1)]
    public void Compare_MissingSegmentsCountAsZero(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, System.Math.Sign(VersionComparer.Compare(a, b)));

    [Theory]
    [InlineData(null, "1.0.0", -1)]     // null = empty = all-zero, below any real version
    [InlineData("1.0.0", null, 1)]
    [InlineData(null, null, 0)]
    [InlineData("", "0.0.0", 0)]
    [InlineData("   ", "0.0.0", 0)]     // whitespace-only treated the same as blank
    [InlineData("abc", "1.0.0", -1)]    // non-numeric segment counts as 0
    [InlineData("1.abc.0", "1.0.0", 0)] // non-numeric middle segment counts as 0, rest still compares
    public void Compare_HandlesNullBlankAndGarbage(string? a, string? b, int expectedSign)
        => Assert.Equal(expectedSign, System.Math.Sign(VersionComparer.Compare(a, b)));

    [Theory]
    [InlineData("01.02.03", "1.2.3", 0)]   // leading zeros parse fine
    [InlineData("1.02.3", "1.2.3", 0)]
    public void Compare_LeadingZerosParseNumerically(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, System.Math.Sign(VersionComparer.Compare(a, b)));
}
