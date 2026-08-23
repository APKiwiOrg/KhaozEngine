using System.Net;

namespace KhaozEngine.Server.Admin;

/// <summary>Consumer-supplied configuration for the admin endpoint. Off until you construct an
/// <see cref="AdminHttpServer"/> with these. Binds to loopback by default (the console runs on the same host or
/// reaches it through a tunnel).</summary>
public sealed class AdminEndpointOptions
{
    /// <summary>
    /// The admin listen port (separate from the game transport port). 0 asks the OS for a free port, which
    /// <see cref="AdminHttpServer.BoundPort"/> reports once the endpoint has started.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>The single bearer token required on every request (constant-time compared).</summary>
    public required string BearerToken { get; init; }

    /// <summary>The TLS certificate Kestrel binds.</summary>
    public required AdminTlsCertificate Certificate { get; init; }

    /// <summary>Bind address; defaults to loopback.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Route prefix for every admin endpoint.</summary>
    public string PathBase { get; init; } = "/admin";
}
