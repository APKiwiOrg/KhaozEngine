using System;
using System.Net;
using System.Net.Sockets;
using KhaozEngine.Netcode.LiteNetLib;

namespace KhaozEngine.Tests;

/// <summary>
/// Support for the live-socket tests (those marked <c>[Trait("Category","LiveSocket")]</c>), which bind a
/// real UDP socket. They must never hardcode a port: a fixed port collides with any other process - a stale
/// server, a parallel test run, an unrelated app - that happens to hold it, failing the bind. Instead bind
/// the server transport to an OS-assigned ephemeral port and hand the actual port to the client.
/// </summary>
internal static class LiveSocketSupport
{
    /// <summary>Reason surfaced (via <c>ITestOutputHelper</c>) when a live-socket test skips for want of a port.</summary>
    public const string NoFreePortReason =
        "skipped: could not secure a free ephemeral UDP port for a live socket (host appears port-starved)";

    /// <summary>
    /// Constructs a <see cref="LiteNetLibServerTransport"/> bound to a free OS-assigned UDP port and returns
    /// it plus the actual bound port in <paramref name="port"/>. Probing a free port then binding it has an
    /// inherent (tiny) race - another process can grab the port in the gap - so this retries across fresh
    /// ports up to <paramref name="attempts"/> times, returning <c>null</c> (and <paramref name="port"/> = 0)
    /// only if every attempt loses the race or the host is genuinely out of ports. The returned transport is
    /// owned by the caller (wrap it in <c>using</c>); a <c>null</c> return is safe to <c>using</c> (no-op).
    /// </summary>
    public static LiteNetLibServerTransport? TryBindServer(out int port, string connectionKey = "khaoz", int attempts = 16)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int candidate = ProbeFreeUdpPort();
            try
            {
                LiteNetLibServerTransport transport = new(candidate, connectionKey);
                port = candidate;
                return transport;
            }
            catch (InvalidOperationException)
            {
                // Lost the probe-to-bind race (the transport's Start failed); try a fresh port.
            }
        }
        port = 0;
        return null;
    }

    /// <summary>
    /// Binds a throwaway UDP socket to an OS-assigned port on the same wildcard address LiteNetLib's
    /// <c>NetManager.Start</c> uses, reads the port the OS chose, releases the socket, and returns the port.
    /// </summary>
    private static int ProbeFreeUdpPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
