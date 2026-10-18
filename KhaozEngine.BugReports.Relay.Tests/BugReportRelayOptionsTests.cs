using System;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class BugReportRelayOptionsTests
{
    [Fact]
    public void Constructor_PublicMediaBaseAt512Characters_Accepted()
        => Assert.NotNull(new RelayHarness(o => o with { PublicMediaBase = MediaBase(512) }).Relay);

    [Fact]
    public void Constructor_PublicMediaBaseAt513Characters_Throws()
        => Assert.Throws<ArgumentException>(() => new RelayHarness(o => o with { PublicMediaBase = MediaBase(513) }));

    static Uri MediaBase(int length)
    {
        const string origin = "https://media.example.test/";
        return new Uri(origin + new string('a', length - origin.Length - 1) + "/");
    }
}
