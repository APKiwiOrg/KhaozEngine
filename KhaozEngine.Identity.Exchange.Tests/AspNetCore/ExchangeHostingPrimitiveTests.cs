using System;
using System.Linq;
using System.Net;
using KhaozEngine.Identity.Exchange.AspNetCore;
using Xunit;
using IPNetwork = System.Net.IPNetwork;

namespace KhaozEngine.Tests.Identity.Exchange.AspNetCore;

/// <summary>
/// The pure pieces under the hosting: the per-client partition key, the trusted proxy presets and their both-families
/// rule, and the option bounds that fail at composition rather than per request.
/// </summary>
public class ExchangeHostingPrimitiveTests
{
    private static string Key(string address, int prefix = 64) => AuthExchangeClientKey.For(IPAddress.Parse(address), prefix);

    [Fact]
    public void AnIPv4Caller_IsKeyedOnTheWholeAddress()
    {
        Assert.Equal("203.0.113.7", Key("203.0.113.7"));
        Assert.NotEqual(Key("203.0.113.7"), Key("203.0.113.8"));
    }

    [Fact]
    public void AMappedCaller_IsFoldedToItsIPv4Address()
    {
        Assert.Equal(Key("203.0.113.7"), Key("::ffff:203.0.113.7"));
    }

    [Fact]
    public void AnIPv6Caller_IsGroupedBySlash64_ByDefault()
    {
        Assert.Equal("2001:db8:1:2::/64", Key("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        Assert.Equal(Key("2001:db8:1:2::1"), Key("2001:db8:1:2:ffff:ffff:ffff:ffff"));
        Assert.NotEqual(Key("2001:db8:1:2::1"), Key("2001:db8:1:3::1"));
        Assert.Equal(64, AuthExchangeClientKey.DefaultIpv6PrefixLength);
        Assert.Equal(Key("2001:db8:1:2::1"), AuthExchangeClientKey.For(IPAddress.Parse("2001:db8:1:2::1")));
    }

    [Fact]
    public void TheIPv6Prefix_IsConfigurable_DownToTheBit()
    {
        Assert.Equal("2001:db8:1::/48", Key("2001:db8:1:ffff::1", 48));
        Assert.Equal("2001:db8:1:200::/56", Key("2001:db8:1:200::1", 56));
        Assert.Equal(Key("2001:db8:1:200::1", 56), Key("2001:db8:1:2ff::9", 56));
        Assert.NotEqual(Key("2001:db8:1:200::1", 56), Key("2001:db8:1:300::1", 56));
        // A prefix that ends mid-byte masks the low bits of that byte only.
        Assert.Equal("2001:db8:1:f000::/52", Key("2001:db8:1:fabc::1", 52));
        Assert.Equal("2001:db8::1/128", Key("2001:db8::1", 128));
    }

    [Fact]
    public void NoPeerAddress_SharesOneBucket_AndCollidesWithNoAddress()
    {
        Assert.Equal(AuthExchangeClientKey.NoPeerAddress, AuthExchangeClientKey.For(null));
        Assert.NotEqual(AuthExchangeClientKey.NoPeerAddress, Key("::"));
        Assert.NotEqual(AuthExchangeClientKey.NoPeerAddress, Key("0.0.0.0"));
        // The IPv4 form carries no '/', so no IPv4 key can equal an IPv6 one.
        Assert.DoesNotContain("/", Key("10.0.0.0"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void AnIPv6PrefixOutOfRange_IsRefused(int prefix)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthExchangeClientKey.For(IPAddress.Loopback, prefix));
    }

    [Fact]
    public void ThePrivateRanges_AreRfc1918_InBothFamilies_TwinAfterEachBlock()
    {
        IPNetwork[] expected =
        {
            IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("::ffff:10.0.0.0/104"),
            IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("::ffff:172.16.0.0/108"),
            IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("::ffff:192.168.0.0/112"),
        };

        Assert.Equal(expected, TrustedProxyNetworks.PrivateRanges.ToArray());
    }

    [Fact]
    public void TheLoopbackPreset_CoversBothFamiliesAndIPv6Loopback()
    {
        Assert.Equal(
            new[] { IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("::ffff:127.0.0.0/104"), IPNetwork.Parse("::1/128") },
            TrustedProxyNetworks.Loopback.ToArray());
    }

    [Fact]
    public void WithBothFamilies_DerivesEitherTwin_KeepsPlainIPv6_AndDropsDuplicates()
    {
        IPNetwork v4 = IPNetwork.Parse("100.64.0.0/10");
        IPNetwork mapped = IPNetwork.Parse("::ffff:100.64.0.0/106");
        IPNetwork v6 = IPNetwork.Parse("fd00::/8");

        Assert.Equal(new[] { v4, mapped }, TrustedProxyNetworks.WithBothFamilies(new[] { v4 }).ToArray());
        Assert.Equal(new[] { mapped, v4 }, TrustedProxyNetworks.WithBothFamilies(new[] { mapped }).ToArray());
        Assert.Equal(new[] { v6 }, TrustedProxyNetworks.WithBothFamilies(new[] { v6 }).ToArray());
        Assert.Equal(new[] { v4, mapped }, TrustedProxyNetworks.WithBothFamilies(new[] { v4, mapped, v4 }).ToArray());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("::ffff:0:0/96")]
    [InlineData("::/80")]
    public void ACatchAllNetwork_IsRefused_ByTheHelperAndAtAdd_NamingTheRuleAndNotTheValue(string scenario)
    {
        // default(IPNetwork) is 0.0.0.0/0: an unfilled array slot or a failed parse used anyway. ::/80 holds the whole
        // mapped block, so on a dual-mode socket it trusts every IPv4 caller just as ::ffff:0:0/96 does.
        IPNetwork catchAll = scenario == "default" ? default : IPNetwork.Parse(scenario);
        IPNetwork[] trusted = { IPNetwork.Parse("10.0.0.0/8"), catchAll };

        ArgumentException helper = Assert.Throws<ArgumentException>(() => TrustedProxyNetworks.WithBothFamilies(trusted));
        ArgumentException atAdd = Assert.Throws<ArgumentException>(() =>
            Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder().AddAuthExchangeHosting(
                new AuthExchangeHostingOptions { TrustedProxies = trusted }));

        Assert.Equal("networks", helper.ParamName);
        Assert.Equal(nameof(AuthExchangeHostingOptions.TrustedProxies), atAdd.ParamName);
        foreach (ArgumentException refusal in new[] { helper, atAdd })
        {
            Assert.Contains("[1]", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("every", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(catchAll.ToString(), refusal.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OneBitInsideTheCatchAll_StillPairs_AndThePresetsPass()
    {
        // The rule refuses a catch-all and nothing narrower: a /97 in the mapped block is half of IPv4, not all of it.
        IPNetwork mapped = IPNetwork.Parse("::ffff:0:0/97");

        Assert.Equal(new[] { mapped, IPNetwork.Parse("0.0.0.0/1") }, TrustedProxyNetworks.WithBothFamilies(new[] { mapped }).ToArray());
        // Wide IPv6 networks that do not hold the mapped block pass. IPNetwork.Contains misjudges ::ffff:0.x.x.x
        // addresses against networks like fd00::/8, so the rule must not lean on it.
        IPNetwork[] wide = { IPNetwork.Parse("fd00::/8"), IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("::/81") };
        Assert.Equal(wide, TrustedProxyNetworks.WithBothFamilies(wide).ToArray());
        Assert.Equal(6, TrustedProxyNetworks.WithBothFamilies(TrustedProxyNetworks.PrivateRanges).Count);
        Assert.Equal(3, TrustedProxyNetworks.WithBothFamilies(TrustedProxyNetworks.Loopback).Count);
    }

    [Fact]
    public void TheEndpointDefaults_AreTheDesignedOnes()
    {
        var options = new AuthExchangeEndpointOptions();

        Assert.Equal("/auth/exchange", options.Pattern);
        Assert.Equal(8 * 1024, options.MaxRequestBodyBytes);
        Assert.Equal(5, options.PermitsPerClientPerMinute);
        Assert.Equal(64, options.Ipv6PartitionPrefixLength);
        Assert.Equal(20, options.MaxConcurrentExchanges);
        Assert.Equal(20, options.MaxQueuedExchanges);
        Assert.False(options.IncludeBanDetails);

        var hosting = new AuthExchangeHostingOptions();
        Assert.Empty(hosting.TrustedProxies);
        Assert.Null(hosting.MaxRequestBodyBytes);
    }

    [Theory]
    [InlineData("pattern", nameof(AuthExchangeEndpointOptions.Pattern))]
    [InlineData("zero body cap", nameof(AuthExchangeEndpointOptions.MaxRequestBodyBytes))]
    [InlineData("huge body cap", nameof(AuthExchangeEndpointOptions.MaxRequestBodyBytes))]
    [InlineData("zero permits", nameof(AuthExchangeEndpointOptions.PermitsPerClientPerMinute))]
    [InlineData("zero prefix", nameof(AuthExchangeEndpointOptions.Ipv6PartitionPrefixLength))]
    [InlineData("long prefix", nameof(AuthExchangeEndpointOptions.Ipv6PartitionPrefixLength))]
    [InlineData("zero concurrency", nameof(AuthExchangeEndpointOptions.MaxConcurrentExchanges))]
    [InlineData("negative queue", nameof(AuthExchangeEndpointOptions.MaxQueuedExchanges))]
    public void AnEndpointOptionOutOfRange_IsRefusedAtMapTime_NamingIt(string scenario, string option)
    {
        AuthExchangeEndpointOptions options = scenario switch
        {
            "pattern" => new AuthExchangeEndpointOptions { Pattern = "auth/exchange" },
            "zero body cap" => new AuthExchangeEndpointOptions { MaxRequestBodyBytes = 0 },
            "huge body cap" => new AuthExchangeEndpointOptions { MaxRequestBodyBytes = AuthExchangeEndpointOptions.MaxBodyCapBytes + 1 },
            "zero permits" => new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 0 },
            "zero prefix" => new AuthExchangeEndpointOptions { Ipv6PartitionPrefixLength = 0 },
            "long prefix" => new AuthExchangeEndpointOptions { Ipv6PartitionPrefixLength = 129 },
            "zero concurrency" => new AuthExchangeEndpointOptions { MaxConcurrentExchanges = 0 },
            "negative queue" => new AuthExchangeEndpointOptions { MaxQueuedExchanges = -1 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        using var app = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder().Build();

        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => app.MapAuthExchange(
            ExchangeFixture.Build(ScriptedValidator.Users(), new KhaozEngine.Accounts.InMemoryAccountStore(true)), options));
        Assert.Equal(option, refused.ParamName);
    }
}
