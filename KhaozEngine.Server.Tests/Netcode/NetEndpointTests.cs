using System;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// Pins the address forms a player types or a config file carries. The bare-IPv6 cases are the reason this
/// parser exists: splitting on the last colon reads "::1" as host ":" port 1, which passes every bounds check
/// and dials something nobody asked for, so the failure is a plausible wrong endpoint rather than a rejection.
/// </summary>
public class NetEndpointTests
{
    private const int DefaultPort = 7777;

    [Theory]
    [InlineData("example.com", "example.com", DefaultPort)]
    [InlineData("  example.com  ", "example.com", DefaultPort)]
    [InlineData("example.com:9000", "example.com", 9000)]
    [InlineData("127.0.0.1", "127.0.0.1", DefaultPort)]
    [InlineData("127.0.0.1:9000", "127.0.0.1", 9000)]
    [InlineData("localhost:1", "localhost", 1)]
    [InlineData("localhost:65535", "localhost", 65535)]
    [InlineData("[::1]", "::1", DefaultPort)]
    [InlineData("[::1]:9000", "::1", 9000)]
    [InlineData("[fe80::1]:9000", "fe80::1", 9000)]
    public void TryParse_AcceptsTheDocumentedForms(string value, string expectedHost, int expectedPort)
    {
        Assert.True(NetEndpoint.TryParse(value, DefaultPort, out string host, out int port));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::8a2e:370:7334")]
    public void TryParse_BareIpv6TakesTheDefaultPort(string value)
    {
        // The exact shape of the bug this replaces: an unbracketed literal is the whole host, never a
        // host:port pair, because the port form requires brackets.
        Assert.True(NetEndpoint.TryParse(value, DefaultPort, out string host, out int port));
        Assert.Equal(value, host);
        Assert.Equal(DefaultPort, port);
    }

    [Fact]
    public void TryParse_BareIpv6ThatLooksLikeAPortIsStillTheWholeHost()
    {
        // "fe80::1:9000" is a valid IPv6 literal. Reading its tail as a port is the ambiguity brackets exist
        // to resolve, so the unbracketed form keeps every colon in the host.
        Assert.True(NetEndpoint.TryParse("fe80::1:9000", DefaultPort, out string host, out int port));
        Assert.Equal("fe80::1:9000", host);
        Assert.Equal(DefaultPort, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com:")]
    [InlineData(":9000")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:-1")]
    [InlineData("example.com:port")]
    [InlineData("example.com: 9000")]
    [InlineData("[]")]
    [InlineData("[]:9000")]
    [InlineData("[::1")]
    [InlineData("[::1]9000")]
    [InlineData("[::1]:")]
    [InlineData("[::1]:abc")]
    [InlineData("[::1]:0")]
    [InlineData("[not-an-address]:9000")]
    [InlineData("::not:an:address")]
    public void TryParse_RejectsMalformed(string? value)
    {
        Assert.False(NetEndpoint.TryParse(value, DefaultPort, out string host, out int port));
        Assert.Equal("", host);
        Assert.Equal(0, port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void TryParse_RejectsAnOutOfRangeDefaultPort(int defaultPort)
    {
        // A bad default is a wiring mistake in the caller, not a bad address, so it throws rather than
        // returning false and hiding behind a malformed-input result.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NetEndpoint.TryParse("example.com", defaultPort, out _, out _));
    }
}
