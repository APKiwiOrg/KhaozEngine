using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// The rule behind <see cref="ServerStatusReport.TryGetServerAddress"/>: turn the published
/// <see cref="ServerStatusReport.ServerAddress"/> string into an <see cref="IPAddress"/> a client may dial,
/// or refuse it. Refusing is the default, because the field arrives over the wire and a client acts on it by
/// opening a socket. The whole point of the field is to take the operating system resolver out of the path,
/// so a host name is REFUSED rather than resolved.
///
/// <para>Two gates. First, the literal must be CANONICAL: <see cref="IPAddress.TryParse(string, out IPAddress)"/>
/// is lenient and reads "1" as 0.0.0.1, "0x7f.1" as 127.0.0.1, "1.2.3" as 1.2.0.3, "010.1.1.1" as 8.1.1.1 and
/// "[2001:db8::1]:9050" as 2001:db8::1, so the parsed address must print back as exactly the trimmed input
/// (case insensitively for IPv6, whose canonical print is lowercase and compressed). A form that does not
/// survive that round trip is how a field like this becomes a redirect primitive. Second, the address must be
/// plain unicast: nothing that means "every local interface", "this machine", "everyone on this link" or
/// "nowhere". Private ranges are ALLOWED, because a game on a private network or a local test rig
/// legitimately publishes one.</para>
/// </summary>
internal static class DialableAddress
{
    /// <summary>
    /// Parses <paramref name="published"/> into a dialable address. Returns false, with a null
    /// <paramref name="address"/>, for null, empty, whitespace, a host name, anything carrying a port or
    /// brackets, a non-canonical literal, and every unroutable or undialable form listed on this type.
    /// </summary>
    internal static bool TryParse(string? published, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(published))
        {
            return false;
        }

        string literal = published.Trim();
        if (!IPAddress.TryParse(literal, out IPAddress? parsed))
        {
            return false;
        }

        if (!IsCanonical(parsed, literal) || !IsPlainUnicast(parsed))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// True when the address prints back as exactly the literal it was read from. IPv6 compares case
    /// insensitively, since the canonical print is lowercase hex, and it still requires the compressed form.
    /// </summary>
    private static bool IsCanonical(IPAddress parsed, string literal) =>
        string.Equals(
            parsed.ToString(),
            literal,
            parsed.AddressFamily == AddressFamily.InterNetworkV6 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>True when the address is a plain unicast address a client could plausibly dial.</summary>
    private static bool IsPlainUnicast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // An IPv4 mapped form is the same address wearing a different hat, so it answers to the IPv4
            // rules. Without this, "::ffff:169.254.10.1" would sail past every IPv6 predicate.
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPlainUnicastV4(address.MapToIPv4());
            }

            return !address.Equals(IPAddress.IPv6Any)
                && !IPAddress.IsLoopback(address)
                && !address.IsIPv6Multicast
                && !address.IsIPv6LinkLocal;
        }

        return address.AddressFamily == AddressFamily.InterNetwork && IsPlainUnicastV4(address);
    }

    /// <summary>The IPv4 half of <see cref="IsPlainUnicast"/>, also applied to an IPv4 mapped IPv6 address.</summary>
    private static bool IsPlainUnicastV4(IPAddress address)
    {
        // IPAddress.Any is the wildcard bind address and IPAddress.None is the broadcast address, neither of
        // which names a destination. IsLoopback covers the whole 127/8 range.
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.None) || IPAddress.IsLoopback(address))
        {
            return false;
        }

        byte[] octets = address.GetAddressBytes();

        // Multicast 224.0.0.0/4 addresses a group, not a server.
        if (octets[0] >= 224 && octets[0] <= 239)
        {
            return false;
        }

        // Link local 169.254.0.0/16 is what a host autoconfigures when it has no address, so it is never
        // routable to a hosted game server.
        return !(octets[0] == 169 && octets[1] == 254);
    }
}
