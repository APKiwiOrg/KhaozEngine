using System.Net;
using System.Net.Sockets;

namespace KhaozEngine.Tests;

/// <summary>
/// Shared free-TCP-port probe for the loopback HTTPS tests (the admin endpoint harness), replacing the identical
/// private <c>FreePort()</c> helpers each admin test file used to carry. Binds a <see cref="TcpListener"/> to an
/// OS-assigned loopback port, reads the port, and releases the socket.
///
/// This returns only the port number because the callers hand it to Kestrel, which binds the socket itself, so a
/// residual probe-release-rebind race is unavoidable here (the caller cannot hold the socket across the handoff).
/// Where a caller DOES own the bind, prefer the retry-on-bind-failure pattern in
/// <see cref="LiveSocketSupport.TryBindServer"/> (UDP) or <c>HttpLoopbackListener</c> (loopback HTTP) instead.
/// </summary>
internal static class TcpPortSupport
{
    public static int FreeTcpPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
