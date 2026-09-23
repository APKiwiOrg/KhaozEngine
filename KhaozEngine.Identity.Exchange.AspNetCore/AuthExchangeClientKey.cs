using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// The per-client rate-limit partition: which callers share one fixed window. A pure function of the peer address, so
/// a game mapping its own route beside the exchange can partition it the same way.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An IPv4 caller is its own client, keyed on the address.</item>
/// <item>An IPv4-mapped IPv6 caller (<c>::ffff:203.0.113.7</c>, what a dual-mode socket reports) is folded to its IPv4
/// address first, so one caller is one client whichever socket family the host bound.</item>
/// <item>An IPv6 caller is grouped by its network prefix, /64 by default. One host is routinely handed a whole /64
/// and can rotate through it freely, so keying on the full address would give one machine billions of buckets.</item>
/// <item>A connection with no peer address (a Unix socket, an in-memory test server) shares ONE bucket, so a missing
/// address fails closed rather than escaping the limit.</item>
/// </list>
/// The key is an opaque string. The IPv4 and IPv6 forms cannot collide (only the IPv6 form carries a <c>/</c>), and
/// neither is ever written to a log.
/// </remarks>
public static class AuthExchangeClientKey
{
    /// <summary>The one key every connection without a peer address shares.</summary>
    public const string NoPeerAddress = "none";

    /// <summary>The IPv6 prefix length a caller is grouped by unless the endpoint says otherwise.</summary>
    public const int DefaultIpv6PrefixLength = 64;

    /// <summary>The partition key for a caller at <paramref name="peer"/>.</summary>
    /// <param name="peer">The caller's address as the host sees it, after forwarded headers when they are trusted.</param>
    /// <param name="ipv6PrefixLength">How many leading bits of an IPv6 address name one client, 1 to 128.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ipv6PrefixLength"/> is outside 1 to 128.</exception>
    public static string For(IPAddress? peer, int ipv6PrefixLength = DefaultIpv6PrefixLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ipv6PrefixLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ipv6PrefixLength, 128);
        if (peer is null) return NoPeerAddress;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        if (peer.AddressFamily != AddressFamily.InterNetworkV6) return peer.ToString();

        Span<byte> bytes = stackalloc byte[16];
        if (!peer.TryWriteBytes(bytes, out int written) || written != bytes.Length) return NoPeerAddress;
        int fullBytes = ipv6PrefixLength / 8;
        int partialBits = ipv6PrefixLength % 8;
        if (fullBytes < bytes.Length)
        {
            if (partialBits != 0) bytes[fullBytes] &= (byte)(0xFF << (8 - partialBits));
            bytes[(fullBytes + (partialBits != 0 ? 1 : 0))..].Clear();
        }
        // No scope id: two link-local callers on different interfaces with the same prefix are one client.
        return new IPAddress(bytes).ToString() + "/" + ipv6PrefixLength.ToString(CultureInfo.InvariantCulture);
    }
}
