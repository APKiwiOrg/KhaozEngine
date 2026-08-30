using System;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace KhaozEngine.Server.Admin;

/// <summary>Consumer-supplied configuration for the admin endpoint. Off until you construct an
/// <see cref="AdminHttpServer"/> with these. Binds to loopback by default (the console runs on the same host or
/// reaches it through a tunnel).
/// <para>The three limit knobs below bound what an UNAUTHENTICATED peer can hold, which matters because the TLS
/// handshake completes before the bearer token is ever read. They ship with defaults tighter than Kestrel's own,
/// sized for an admin endpoint (one operator and a script, not a public web app). Set any of them to null to leave
/// Kestrel's default in place instead.</para></summary>
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

    /// <summary>Cap on simultaneously open connections, applied to
    /// <see cref="KestrelServerLimits.MaxConcurrentConnections"/>. Bounds how many RSA handshakes an unauthenticated
    /// peer can hold at once, which is the only pre-auth cost this endpoint can be made to pay. Default 64, which is
    /// far above what an operator console and a monitoring script need. Null leaves Kestrel's default, which is
    /// unlimited. Must be positive when set.</summary>
    public int? MaxConcurrentConnections { get; init; } = 64;

    /// <summary>How long a connection may take to send its complete request headers, applied to
    /// <see cref="KestrelServerLimits.RequestHeadersTimeout"/>. This is the slowloris bound, and it runs BEFORE the
    /// bearer check, so it is the knob that stops an unauthenticated peer parking a connection. Default 10 seconds
    /// against Kestrel's 30. Null leaves Kestrel's default. Must be positive when set.</summary>
    public TimeSpan? RequestHeadersTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long an idle keep-alive connection is held, applied to
    /// <see cref="KestrelServerLimits.KeepAliveTimeout"/>. Default 30 seconds against Kestrel's 130, so an
    /// authenticated console reconnects a little more often and an idle peer stops occupying a connection slot for
    /// over two minutes. Null leaves Kestrel's default. Must be positive when set.</summary>
    public TimeSpan? KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Throws if any limit is set to a value Kestrel would reject or that would disable the limit by
    /// accident. Called from <see cref="AdminHttpServer"/>'s constructor, so a bad value fails there rather than
    /// inside Kestrel's lazily-invoked configure callback at start.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is set to a non-positive value.</exception>
    internal void Validate()
    {
        if (MaxConcurrentConnections is int max && max <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentConnections), max,
                "must be positive, or null to leave Kestrel's default (unlimited)");
        if (RequestHeadersTimeout is TimeSpan headers && headers <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestHeadersTimeout), headers,
                "must be positive, or null to leave Kestrel's default");
        if (KeepAliveTimeout is TimeSpan keepAlive && keepAlive <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(KeepAliveTimeout), keepAlive,
                "must be positive, or null to leave Kestrel's default");
    }

    /// <summary>Writes whichever limits are set onto <paramref name="limits"/>, leaving the rest as Kestrel had
    /// them. Split out from the <c>ConfigureKestrel</c> callback so it can be exercised against a bare
    /// <see cref="KestrelServerLimits"/>, with no listener and no socket.</summary>
    internal void ApplyLimits(KestrelServerLimits limits)
    {
        Validate();
        if (MaxConcurrentConnections is int max) limits.MaxConcurrentConnections = max;
        if (RequestHeadersTimeout is TimeSpan headers) limits.RequestHeadersTimeout = headers;
        if (KeepAliveTimeout is TimeSpan keepAlive) limits.KeepAliveTimeout = keepAlive;
    }
}
