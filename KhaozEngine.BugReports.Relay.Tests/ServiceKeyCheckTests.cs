using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ServiceKeyCheckTests
{
    [Fact]
    public void Matches_SameKey_True()
        => Assert.True(ServiceKeyCheck.Matches(new string('s', 32), new string('s', 32)));

    [Fact]
    public void Matches_DifferentKeySameLength_False()
        => Assert.False(ServiceKeyCheck.Matches(new string('s', 32), new string('S', 32)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("sssssssssssssssssssssssssssssssss")]
    public void Matches_DifferentLengthOrNull_FalseWithoutThrowing(string? provided)
        => Assert.False(ServiceKeyCheck.Matches(new string('s', 32), provided));
}
