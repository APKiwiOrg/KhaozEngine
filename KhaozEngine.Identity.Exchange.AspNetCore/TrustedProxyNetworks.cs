using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// Presets for <see cref="AuthExchangeHostingOptions.TrustedProxies"/>, the networks <c>X-Forwarded-For</c> is believed
/// from, and the rule that registers every trusted network in both address families.
/// </summary>
/// <remarks>
/// <para>
/// Which family a proxy shows up in is not knowable from configuration. A dual-mode socket (Kestrel on the IPv6 any
/// address) reports an IPv4 peer as IPv4-mapped IPv6 (<c>::ffff:10.0.0.1</c>), and the same host with IPv6 disabled
/// falls back to the IPv4 any address and reports it plain (<c>10.0.0.1</c>). An <see cref="IPNetwork"/> never contains
/// an address of the other family, so a list written in one family silently distrusts the proxy in the other and drops
/// the header in exactly the deployment it exists for. <see cref="WithBothFamilies"/> answers the question once:
/// every IPv4 network gains its mapped twin and every mapped network its IPv4 twin, and the hosting applies it to
/// whatever list the game names.
/// </para>
/// <para>
/// The mapped twin is derived rather than typed. <c>::ffff:0:0/96</c> is the mapped block, so a /8 becomes a /104, a
/// /12 a /108 and a /16 a /112, which is exactly the arithmetic a hand-written list gets wrong once and then reads as
/// correct forever.
/// </para>
/// </remarks>
public static class TrustedProxyNetworks
{
    // The IPv4-mapped block is ::ffff:0:0/96, so an IPv4 prefix sits 96 bits further in on its mapped twin.
    private const int MappedPrefixOffset = 96;

    /// <summary>
    /// The three RFC 1918 private blocks (<c>10.0.0.0/8</c>, <c>172.16.0.0/12</c>, <c>192.168.0.0/16</c>), each followed
    /// by its IPv4-mapped IPv6 twin. The preset for a service behind a TLS-terminating proxy that forwards over a
    /// private address, which is how both games deploy.
    /// </summary>
    /// <remarks>
    /// It trusts EVERY host on those networks to name the client address, so any machine that can reach the service
    /// privately can choose its own rate-limit bucket. Name the proxy's own subnet instead when the private network is
    /// shared with anything you do not control.
    /// </remarks>
    public static IReadOnlyList<IPNetwork> PrivateRanges { get; } = WithBothFamilies(new[]
    {
        new IPNetwork(IPAddress.Parse("10.0.0.0"), 8),
        new IPNetwork(IPAddress.Parse("172.16.0.0"), 12),
        new IPNetwork(IPAddress.Parse("192.168.0.0"), 16),
    });

    /// <summary>
    /// The loopback networks (<c>127.0.0.0/8</c> with its mapped twin, and <c>::1/128</c>). The preset for a proxy on the
    /// same host as the service.
    /// </summary>
    public static IReadOnlyList<IPNetwork> Loopback { get; } = WithBothFamilies(new[]
    {
        new IPNetwork(IPAddress.Parse("127.0.0.0"), 8),
        new IPNetwork(IPAddress.IPv6Loopback, 128),
    });

    /// <summary>
    /// Every network in <paramref name="networks"/>, each followed by its twin in the other address family when it has
    /// one: an IPv4 network gains its IPv4-mapped IPv6 twin, and a network inside the mapped block <c>::ffff:0:0/96</c>
    /// gains its IPv4 twin. A plain IPv6 network has no twin and stands alone. Duplicates are dropped and the order is
    /// otherwise kept.
    /// </summary>
    /// <param name="networks">The networks to register.</param>
    /// <returns>A new list, safe to hand to the forwarded-headers options.</returns>
    public static IReadOnlyList<IPNetwork> WithBothFamilies(IEnumerable<IPNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var seen = new HashSet<IPNetwork>();
        var result = new List<IPNetwork>();
        foreach (IPNetwork network in networks)
        {
            if (seen.Add(network)) result.Add(network);
            if (TwinOf(network) is { } twin && seen.Add(twin)) result.Add(twin);
        }
        return result.AsReadOnly();
    }

    private static IPNetwork? TwinOf(IPNetwork network)
    {
        IPAddress baseAddress = network.BaseAddress;
        if (baseAddress.AddressFamily == AddressFamily.InterNetwork)
            return new IPNetwork(baseAddress.MapToIPv6(), network.PrefixLength + MappedPrefixOffset);
        if (baseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= MappedPrefixOffset)
            return new IPNetwork(baseAddress.MapToIPv4(), network.PrefixLength - MappedPrefixOffset);
        return null;
    }
}
