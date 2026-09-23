using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Identity.Exchange.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using IPNetwork = System.Net.IPNetwork;

namespace KhaozEngine.Tests.Identity.Exchange.AspNetCore;

/// <summary>
/// <c>X-Forwarded-For</c> is believed from the named proxy networks and from nothing else, loopback included, and a
/// proxy is trusted whichever address family it shows up in: the plain IPv4 peer a host with IPv6 disabled reports, and
/// the IPv4-mapped IPv6 peer a dual-mode socket reports. A list typed in one family only (the Ruinborne defect) still
/// trusts the proxy in the other.
/// </summary>
[Collection(ExchangeKestrelCollection.Name)]
public class ExchangeForwardedHeadersTests
{
    private static async Task<ExchangeHttpHost> StartAsync(params IPNetwork[] trusted) =>
        await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 1 },
            new AuthExchangeHostingOptions { TrustedProxies = trusted });

    // Two callers behind the proxy. With the header believed they are two clients, and without it they are the proxy.
    private static async Task<bool> ForwardedHeaderIsBelievedAsync(ExchangeHttpHost host)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.ExchangeAsync("discord", "bad", "198.51.100.1")).Status);
        return (await host.ExchangeAsync("discord", "bad", "198.51.100.2")).Status == HttpStatusCode.Unauthorized;
    }

    [Fact]
    public async Task APlainIPv4Proxy_InAPlainIPv4Network_IsBelieved()
    {
        await using ExchangeHttpHost host = await StartAsync(new IPNetwork(IPAddress.Parse("127.0.0.0"), 8));

        Assert.True(await ForwardedHeaderIsBelievedAsync(host));
    }

    [Fact]
    public async Task APlainIPv4Proxy_InANetworkTypedOnlyAsMappedIPv6_IsStillBelieved()
    {
        // Ruinborne typed its list in the mapped form only, which distrusts the proxy the moment the host reports it plain.
        await using ExchangeHttpHost host = await StartAsync(new IPNetwork(IPAddress.Parse("::ffff:127.0.0.0"), 104));

        Assert.True(await ForwardedHeaderIsBelievedAsync(host));
    }

    [Fact]
    public async Task APeerOutsideTheNamedNetworks_IsNotBelieved_LoopbackIncluded()
    {
        // The framework trusts loopback by default. Naming only the private ranges must remove that, so the header from
        // this 127.0.0.1 peer is ignored and both callers share the peer's single permit.
        await using ExchangeHttpHost host = await StartAsync(TrustedProxyNetworks.PrivateRanges.ToArray());

        Assert.False(await ForwardedHeaderIsBelievedAsync(host));
    }

    [Fact]
    public async Task WithNoProxyNamed_ForwardedHeadersAreOff()
    {
        await using ExchangeHttpHost host = await StartAsync();

        Assert.False(await ForwardedHeaderIsBelievedAsync(host));
    }

    [Theory]
    [InlineData("10.20.30.40", true)]
    [InlineData("::ffff:10.20.30.40", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("::ffff:172.31.255.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("::ffff:172.32.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("203.0.113.9", false)]
    public async Task ThePrivateRangesPreset_TrustsAProxyInEitherFamily(string proxy, bool believed)
    {
        // The dual-mode peer is awkward to produce on a loopback socket, so the configured middleware runs directly
        // against a request whose peer is exactly the address under test.
        HttpContext context = await RunForwardedHeadersAsync(TrustedProxyNetworks.PrivateRanges, IPAddress.Parse(proxy),
            forwardedFor: "198.51.100.77");

        Assert.Equal(believed ? IPAddress.Parse("198.51.100.77") : IPAddress.Parse(proxy), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task AMappedPeer_IsBelieved_FromANetworkTypedOnlyAsIPv4()
    {
        HttpContext context = await RunForwardedHeadersAsync(new[] { new IPNetwork(IPAddress.Parse("10.0.0.0"), 8) },
            IPAddress.Parse("::ffff:10.0.0.5"), forwardedFor: "198.51.100.77");

        Assert.Equal(IPAddress.Parse("198.51.100.77"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task OnlyTheLastHop_IsRead_SoAClientCannotPrependItsOwnBucket()
    {
        HttpContext context = await RunForwardedHeadersAsync(TrustedProxyNetworks.PrivateRanges,
            IPAddress.Parse("10.0.0.5"), forwardedFor: "203.0.113.1, 198.51.100.77");

        Assert.Equal(IPAddress.Parse("198.51.100.77"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public void TheHosting_ConfiguresExactlyTheNamedNetworks_InBothFamilies_WithAForwardLimitOfOne()
    {
        ForwardedHeadersOptions options = ConfiguredOptions(new[] { new IPNetwork(IPAddress.Parse("10.1.0.0"), 16) });

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownProxies);
        Assert.Equal(new[] { new IPNetwork(IPAddress.Parse("10.1.0.0"), 16), new IPNetwork(IPAddress.Parse("::ffff:10.1.0.0"), 112) },
            options.KnownIPNetworks.ToArray());

        ForwardedHeadersOptions off = ConfiguredOptions(Array.Empty<IPNetwork>());
        Assert.Equal(ForwardedHeaders.None, off.ForwardedHeaders);
        Assert.Empty(off.KnownIPNetworks);
        Assert.Empty(off.KnownProxies);
    }

    [Fact]
    public void UseWithoutAdd_IsRefused_AndABadHostingOptionIsRefusedAtAdd()
    {
        WebApplication bare = WebApplication.CreateSlimBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => bare.UseAuthExchangeHosting());

        Assert.Throws<ArgumentNullException>(() => WebApplication.CreateSlimBuilder().AddAuthExchangeHosting(
            new AuthExchangeHostingOptions { TrustedProxies = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebApplication.CreateSlimBuilder().AddAuthExchangeHosting(
            new AuthExchangeHostingOptions { MaxRequestBodyBytes = 0 }));
    }

    [Fact]
    public async Task AddWithoutUse_IsRefusedWhenTheExchangeIsMapped()
    {
        // Named proxies with no middleware would read no forwarded header at all: every caller in the proxy's bucket,
        // and nothing said so. The map is where that composition is caught.
        AuthExchange exchange = await ExchangeAccounts.BuildAsync();
        await using WebApplication app = BuildWithHosting();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => app.MapAuthExchange(exchange));

        Assert.Contains(nameof(AuthExchangeHosting.UseAuthExchangeHosting), refusal.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AuthExchangeHosting.AddAuthExchangeHosting), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddThenUseThenMap_Maps_AndAnAppWithNoHostingStillMaps()
    {
        AuthExchange exchange = await ExchangeAccounts.BuildAsync();
        await using WebApplication composed = BuildWithHosting();
        await using WebApplication bare = WebApplication.CreateSlimBuilder().Build();

        composed.UseAuthExchangeHosting();

        Assert.NotNull(composed.MapAuthExchange(exchange));
        Assert.NotNull(bare.MapAuthExchange(exchange));
    }

    private static WebApplication BuildWithHosting()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.AddAuthExchangeHosting(new AuthExchangeHostingOptions { TrustedProxies = TrustedProxyNetworks.PrivateRanges });
        return builder.Build();
    }

    private static ForwardedHeadersOptions ConfiguredOptions(IPNetwork[] trusted)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.AddAuthExchangeHosting(new AuthExchangeHostingOptions { TrustedProxies = trusted });
        using WebApplication app = builder.Build();
        return app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    private static async Task<HttpContext> RunForwardedHeadersAsync(System.Collections.Generic.IReadOnlyList<IPNetwork> trusted,
        IPAddress peer, string forwardedFor)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.AddAuthExchangeHosting(new AuthExchangeHostingOptions { TrustedProxies = trusted });
        await using WebApplication app = builder.Build();
        app.UseAuthExchangeHosting();
        RequestDelegate pipeline = ((IApplicationBuilder)app).Build();

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Connection.RemoteIpAddress = peer;
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        await pipeline(context);
        return context;
    }
}
