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

    private static readonly IPAddress MappedBlockBase = IPAddress.Any.MapToIPv6();

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
    /// <remarks>
    /// A catch-all is refused rather than paired, so the list this returns never trusts every IPv4 caller: a /0 in
    /// either family (which is what <c>default(IPNetwork)</c> is, so an unfilled slot or a failed parse used anyway
    /// counts), or an IPv6 network holding the whole mapped block, whose IPv4 twin would be a /0.
    /// </remarks>
    /// <param name="networks">The networks to register.</param>
    /// <returns>A new list, safe to hand to the forwarded-headers options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="networks"/> is null.</exception>
    /// <exception cref="ArgumentException">A network is a catch-all. The message names its position, never its
    /// value.</exception>
    public static IReadOnlyList<IPNetwork> WithBothFamilies(IEnumerable<IPNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var seen = new HashSet<IPNetwork>();
        var result = new List<IPNetwork>();
        int index = 0;
        foreach (IPNetwork network in networks)
        {
            if (IsCatchAll(network))
                throw new ArgumentException(CatchAllRefusal(nameof(networks), index), nameof(networks));
            if (seen.Add(network)) result.Add(network);
            if (TwinOf(network) is { } twin && seen.Add(twin)) result.Add(twin);
            index++;
        }
        return result.AsReadOnly();
    }

    /// <summary>
    /// Whether <paramref name="network"/> trusts every IPv4 caller in some form: a /0 in either family, or an IPv6
    /// network that holds the whole IPv4-mapped block, which a dual-mode socket reports every IPv4 peer inside.
    /// </summary>
    internal static bool IsCatchAll(IPNetwork network)
    {
        if (network.PrefixLength == 0) return true;
        if (network.BaseAddress.AddressFamily != AddressFamily.InterNetworkV6 || network.PrefixLength > MappedPrefixOffset)
            return false;

        // The network holds the block when its prefix bits match the block's base. Compared bit by bit rather than
        // through IPNetwork.Contains, which on .NET 10 answers true for ::ffff:0.x.x.x inside unrelated networks such
        // as fd00::/8.
        Span<byte> networkBytes = stackalloc byte[16];
        Span<byte> blockBytes = stackalloc byte[16];
        network.BaseAddress.TryWriteBytes(networkBytes, out _);
        MappedBlockBase.TryWriteBytes(blockBytes, out _);
        int wholeBytes = network.PrefixLength / 8;
        if (!networkBytes[..wholeBytes].SequenceEqual(blockBytes[..wholeBytes])) return false;
        int spareBits = network.PrefixLength % 8;
        if (spareBits == 0) return true;
        int mask = (0xFF << (8 - spareBits)) & 0xFF;
        return (networkBytes[wholeBytes] & mask) == (blockBytes[wholeBytes] & mask);
    }

    /// <summary>The refusal for a catch-all at <paramref name="index"/> in <paramref name="list"/>. Names the rule
    /// and the position, never the network.</summary>
    internal static string CatchAllRefusal(string list, int index) =>
        $"{list}[{index}] trusts every IPv4 caller: it is a /0 (as an unset IPNetwork is) or it holds the whole " +
        "IPv4-mapped block. Trusting it lets any caller choose its own rate-limit bucket through X-Forwarded-For. Name " +
        "the proxies' own networks.";

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
