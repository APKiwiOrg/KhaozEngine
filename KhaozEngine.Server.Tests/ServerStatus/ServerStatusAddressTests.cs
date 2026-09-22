using System;
using System.Net;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

/// <summary>
/// The <c>serverAddress</c> field and its typed accessor. The field exists so a client can dial the address
/// the publisher vouches for instead of an operating-system DNS answer that may still be cached from before
/// the host rotated its address. The accessor is the gate: it accepts only a canonical IP literal that a
/// client could plausibly dial, so the field can never redirect a client at a name lookup, a port, or an
/// address that means "here" or "everyone".
/// </summary>
public class ServerStatusAddressTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ServerStatusReport Report(string? published) =>
        new() { Health = ServerHealth.Healthy, ServerAddress = published };

    private static ServerStatusSnapshot Fresh(ServerStatusReport report) => new()
    {
        LastReport = report,
        LastSuccessUtc = Now,
        LastAttemptUtc = Now,
        ConsecutiveFailures = 0,
    };

    [Theory]
    [InlineData("4.254.6.139")]         // a measured public ACI address
    [InlineData("203.0.113.7")]         // public IPv4
    [InlineData("2001:db8::1")]         // public IPv6 in the canonical compressed form
    [InlineData("2001:DB8::1")]         // same address, uppercase hex: IPv6 compares case insensitively
    [InlineData("10.0.0.5")]            // private 10/8 is allowed: a private network or a local rig may publish one
    [InlineData("172.16.3.4")]          // private 172.16/12
    [InlineData("192.168.1.20")]        // private 192.168/16
    [InlineData("fc00::1")]             // IPv6 unique local, the private equivalent
    [InlineData("::ffff:4.254.6.139")]  // the IPv4 mapped form of a dialable public address
    [InlineData("  4.254.6.139  ")]     // surrounding whitespace is trimmed before the canonical compare
    public void TryGetServerAddress_AcceptsPlainUnicastLiteral(string published)
    {
        Assert.True(Report(published).TryGetServerAddress(out IPAddress? address));
        Assert.Equal(IPAddress.Parse(published.Trim()), address);
    }

    [Fact]
    public void TryGetServerAddress_ReturnsNullAddress_WhenItRefuses()
    {
        Assert.False(Report("status.example.com").TryGetServerAddress(out IPAddress? address));
        Assert.Null(address);
    }

    [Fact]
    public void TryGetServerAddress_RefusesNull()
    {
        // The default: a publisher that does not know the address sets nothing at all.
        Assert.False(Report(null).TryGetServerAddress(out IPAddress? _));
        Assert.False(new ServerStatusReport().TryGetServerAddress(out IPAddress? _));
    }

    [Theory]
    [InlineData("")]                        // empty
    [InlineData("   ")]                     // whitespace only
    [InlineData("\t")]                      // whitespace only
    [InlineData("status.example.com")]      // a host name, refused rather than resolved: no second DNS lookup
    [InlineData("localhost")]               // a host name, even a well-known one
    [InlineData("4.254.6.139:9050")]        // an address with a port
    [InlineData("[2001:db8::1]:9050")]      // a bracketed IPv6 with a port
    [InlineData("[2001:db8::1]")]           // brackets alone: the parser takes them, the canonical form does not
    [InlineData("1")]                       // lenient parse: reads as 0.0.0.1
    [InlineData("0x7f.1")]                  // lenient parse: reads as 127.0.0.1
    [InlineData("1.2.3")]                   // lenient parse: reads as 1.2.0.3
    [InlineData("010.1.1.1")]               // lenient parse: reads as 8.1.1.1
    [InlineData("2001:db8:0:0:0:0:0:1")]    // a valid but uncompressed IPv6 form, not the canonical one
    [InlineData("0.0.0.0")]                 // IPAddress.Any means "every local interface", not a destination
    [InlineData("::")]                      // IPAddress.IPv6Any, same reason
    [InlineData("255.255.255.255")]         // IPAddress.None and the IPv4 broadcast address
    [InlineData("127.0.0.1")]               // loopback points a client at itself
    [InlineData("127.9.9.9")]               // the whole 127/8 loopback range
    [InlineData("::1")]                     // IPv6 loopback
    [InlineData("224.0.0.1")]               // IPv4 multicast 224/4
    [InlineData("239.255.255.250")]         // the top of IPv4 multicast
    [InlineData("ff02::1")]                 // IPv6 multicast
    [InlineData("169.254.10.1")]            // IPv4 link local 169.254/16, an unroutable autoconfigured address
    [InlineData("fe80::1")]                 // IPv6 link local
    [InlineData("fe80::1%12")]              // IPv6 link local with a scope id
    [InlineData("::ffff:0:0")]              // the IPv4 mapped form of 0.0.0.0
    [InlineData("::ffff:127.0.0.1")]        // the IPv4 mapped form of loopback
    [InlineData("::ffff:169.254.10.1")]     // the IPv4 mapped form of a link local address
    [InlineData("::ffff:224.0.0.1")]        // the IPv4 mapped form of a multicast address
    [InlineData("::ffff:255.255.255.255")]  // the IPv4 mapped form of the broadcast address
    public void TryGetServerAddress_RefusesEverythingThatIsNotADialableLiteral(string published)
    {
        Assert.False(Report(published).TryGetServerAddress(out IPAddress? _));
    }

    [Fact]
    public void View_SurfacesTheParsedAddress_LikeExpectedBackUtc()
    {
        ServerStatusView view = ServerStatusEvaluator.Evaluate(Fresh(Report("4.254.6.139")), "1.0.0", Now);

        Assert.Equal(ServerStatusState.ServerOk, view.State);
        Assert.Equal(IPAddress.Parse("4.254.6.139"), view.ServerAddress);
    }

    [Fact]
    public void View_ServerAddressIsNull_WhenTheFieldIsUnsetOrRefused()
    {
        Assert.Null(ServerStatusEvaluator.Evaluate(Fresh(Report(null)), "1.0.0", Now).ServerAddress);
        Assert.Null(ServerStatusEvaluator.Evaluate(Fresh(Report("127.0.0.1")), "1.0.0", Now).ServerAddress);
        Assert.Null(ServerStatusEvaluator.Evaluate(ServerStatusSnapshot.Empty, "1.0.0", Now).ServerAddress);
    }

    [Fact]
    public void View_StillSurfacesTheAddress_WhileTheReportIsRetainedButStale()
    {
        // Same rule as Motd and ExpectedBackUtc: a retained report keeps answering. Whether to DIAL it is the
        // consumer's call, and the documented answer is only while the state reads ServerOk.
        ServerStatusSnapshot stale = Fresh(Report("4.254.6.139")) with { LastSuccessUtc = Now.AddMinutes(-5) };
        ServerStatusView view = ServerStatusEvaluator.Evaluate(stale, "1.0.0", Now);

        Assert.Equal(ServerStatusState.StatusUnknown, view.State);
        Assert.Equal(IPAddress.Parse("4.254.6.139"), view.ServerAddress);
    }
}
