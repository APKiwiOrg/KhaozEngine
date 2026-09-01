using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

#nullable enable

namespace KhaozEngine.Netcode;

/// <summary>
/// Parses the server address a player types or a config file carries into a host and a port. The forms are the
/// ones a URL authority allows: a bare host, <c>host:port</c>, a bracketed IPv6 literal with or without a port,
/// and a bare (unbracketed) IPv6 literal, which takes the caller's default port.
///
/// <para><b>Why this is not a last-colon split.</b> The obvious implementation splits on the last colon, and
/// that reading is wrong for every unbracketed IPv6 literal: <c>"::1"</c> splits into a host of <c>":"</c> and a
/// port of 1, and both halves pass an ordinary bounds check. The result is not a rejection but a plausible-looking
/// endpoint nobody asked for, dialled silently. Brackets are what tell an address's colons apart from the port
/// separator, so an unbracketed literal keeps every colon in the host and never yields a port.</para>
///
/// <para><b>Why not the BCL.</b> <see cref="IPEndPoint.Parse(string)"/> requires an IP literal and rejects a DNS
/// host name, which is the common case here. <see cref="Uri"/> parses a full URI, so a bare <c>host:port</c>
/// needs a synthetic scheme prepended and its failure modes are harder to report cleanly.</para>
/// </summary>
public static class NetEndpoint
{
    /// <summary>Lowest port this accepts. Port 0 means "any free port" to a socket, never a server to dial.</summary>
    private const int MinPort = 1;

    /// <summary>Highest port this accepts.</summary>
    private const int MaxPort = 65535;

    /// <summary>
    /// Parses <paramref name="value"/> into a host and a port, without touching DNS or a socket.
    /// </summary>
    /// <param name="value">
    /// The address. Surrounding whitespace is trimmed. Accepted: <c>host</c>, <c>host:port</c>, <c>[ipv6]</c>,
    /// <c>[ipv6]:port</c>, and a bare <c>ipv6</c> literal. Null, empty, or whitespace is rejected rather than
    /// treated as a default, so a caller decides for itself what an unset address means.
    /// </param>
    /// <param name="defaultPort">Port for the forms that carry none. Must be in [1, 65535].</param>
    /// <param name="host">
    /// The parsed host: a DNS name, an IPv4 literal, or an IPv6 literal with its brackets stripped (so it can be
    /// handed straight to a socket API). Empty string when this returns false.
    /// </param>
    /// <param name="port">The parsed or default port. Zero when this returns false.</param>
    /// <returns>True when the address is one of the accepted forms.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultPort"/> is outside [1, 65535]. A bad default is a wiring mistake in the caller
    /// rather than bad input, so it throws instead of hiding inside a malformed-address false.
    /// </exception>
    public static bool TryParse(string? value, int defaultPort, out string host, out int port)
    {
        if (defaultPort < MinPort || defaultPort > MaxPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultPort), defaultPort, $"Default port must be in [{MinPort}, {MaxPort}].");
        }

        host = "";
        port = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();

        if (text[0] == '[')
        {
            return TryParseBracketed(text, defaultPort, ref host, ref port);
        }

        int colon = text.IndexOf(':');
        if (colon < 0)
        {
            // A bare host name or IPv4 literal. Not validated any further: resolving it is the socket's job,
            // and rejecting an unusual but legal name here would be worse than letting the connect fail.
            host = text;
            port = defaultPort;
            return true;
        }

        if (colon != text.LastIndexOf(':'))
        {
            // More than one colon and no brackets, so the only reading left is a bare IPv6 literal. It must
            // actually be one: this is the branch where a malformed value would otherwise sail through.
            if (!IsIpv6Literal(text))
            {
                return false;
            }

            host = text;
            port = defaultPort;
            return true;
        }

        string name = text[..colon];
        if (name.Length == 0 || !TryPort(text[(colon + 1)..], out int parsed))
        {
            return false;
        }

        host = name;
        port = parsed;
        return true;
    }

    /// <summary>
    /// Handles <c>[ipv6]</c> and <c>[ipv6]:port</c>. The bracketed literal is validated, because brackets are a
    /// claim that what is inside them is an address, and honouring a malformed one would defeat the point of
    /// requiring them.
    /// </summary>
    private static bool TryParseBracketed(string text, int defaultPort, ref string host, ref int port)
    {
        int close = text.IndexOf(']');
        if (close < 2)
        {
            return false;
        }

        string literal = text[1..close];
        if (!IsIpv6Literal(literal))
        {
            return false;
        }

        string tail = text[(close + 1)..];
        if (tail.Length == 0)
        {
            host = literal;
            port = defaultPort;
            return true;
        }

        if (tail[0] != ':' || !TryPort(tail[1..], out int parsed))
        {
            return false;
        }

        host = literal;
        port = parsed;
        return true;
    }

    /// <summary>
    /// True when the text is an IPv6 literal (scope id included, IPv4-mapped forms included). Address family is
    /// checked rather than trusting <see cref="IPAddress.TryParse(string, out IPAddress)"/> alone, which also
    /// accepts IPv4 text and would let a dotted quad through a branch that only IPv6 belongs in.
    /// </summary>
    private static bool IsIpv6Literal(string text) =>
        IPAddress.TryParse(text, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// Parses a port with no sign, no thousands separator and no surrounding whitespace, then bounds it. The
    /// strict number style is what keeps <c>"host: 9000"</c> and <c>"host:-1"</c> out.
    /// </summary>
    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port >= MinPort
        && port <= MaxPort;
}
