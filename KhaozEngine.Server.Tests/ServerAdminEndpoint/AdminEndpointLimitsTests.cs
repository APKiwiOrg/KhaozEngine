using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

/// <summary>
/// The pre-auth hardening knobs, exercised against a bare <see cref="KestrelServerLimits"/>. Deliberately NO live
/// listener: the limits are a property of what gets written onto Kestrel's options, and the live-listener shape in
/// this folder is where the #720 flake family lives, so proving it without a socket proves the same thing and cannot
/// flake.
/// </summary>
public class AdminEndpointLimitsTests
{
    private static AdminEndpointOptions Options() => new()
    {
        Port = 0,
        BearerToken = "secret",
        Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
    };

    /// <summary>The defaults exist because the TLS handshake completes before the bearer check, so an endpoint left
    /// on Kestrel's own defaults let an unauthenticated peer hold unlimited connections, 30 seconds each to send
    /// headers and 130 seconds idle. A consumer that configures nothing must still get the tighter set.</summary>
    [Fact]
    public void Defaults_are_tighter_than_Kestrels_own()
    {
        var limits = new KestrelServerLimits();
        Options().ApplyLimits(limits);

        Assert.Equal(64, limits.MaxConcurrentConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), limits.RequestHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), limits.KeepAliveTimeout);
    }

    [Fact]
    public void Configured_values_are_written_through()
    {
        var limits = new KestrelServerLimits();
        AdminEndpointOptions opts = new()
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
            MaxConcurrentConnections = 8,
            RequestHeadersTimeout = TimeSpan.FromSeconds(3),
            KeepAliveTimeout = TimeSpan.FromSeconds(5),
        };

        opts.ApplyLimits(limits);

        Assert.Equal(8, limits.MaxConcurrentConnections);
        Assert.Equal(TimeSpan.FromSeconds(3), limits.RequestHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), limits.KeepAliveTimeout);
    }

    /// <summary>Null is the documented opt-out, and it must leave what Kestrel had rather than writing a zero.</summary>
    [Fact]
    public void Null_leaves_Kestrels_own_value_untouched()
    {
        var untouched = new KestrelServerLimits();
        long? kestrelMaxConnections = untouched.MaxConcurrentConnections;
        TimeSpan kestrelHeaders = untouched.RequestHeadersTimeout;
        TimeSpan kestrelKeepAlive = untouched.KeepAliveTimeout;

        var limits = new KestrelServerLimits();
        new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
            MaxConcurrentConnections = null,
            RequestHeadersTimeout = null,
            KeepAliveTimeout = null,
        }.ApplyLimits(limits);

        Assert.Equal(kestrelMaxConnections, limits.MaxConcurrentConnections);
        Assert.Equal(kestrelHeaders, limits.RequestHeadersTimeout);
        Assert.Equal(kestrelKeepAlive, limits.KeepAliveTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_connection_cap_is_rejected(int cap)
    {
        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
            MaxConcurrentConnections = cap,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Fact]
    public void A_non_positive_timeout_is_rejected()
    {
        AdminEndpointOptions headers = new()
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
            RequestHeadersTimeout = TimeSpan.Zero,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => headers.Validate());

        AdminEndpointOptions keepAlive = new()
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
            KeepAliveTimeout = TimeSpan.FromSeconds(-1),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => keepAlive.Validate());
    }

    /// <summary>A bad limit fails at construction, not later inside Kestrel's lazily-invoked configure callback,
    /// where it would surface at StartAsync with no obvious link to the option that caused it. No listener is bound:
    /// the constructor throws before Kestrel is ever built.</summary>
    [Fact]
    public void A_bad_limit_throws_from_the_constructor()
    {
        var opts = new AdminEndpointOptions
        {
            Port = 0,
            BearerToken = "secret",
            Certificate = AdminTlsCertificate.CreateSelfSigned("localhost"),
            RequestHeadersTimeout = TimeSpan.Zero,
        };
        var admin = new ServerAdmin(new SilentAdminTarget());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdminHttpServer(admin, opts));
    }

    /// <summary>An <see cref="IAdminControllable"/> that does nothing, so a constructor-validation test needs no
    /// server, no transport and no tick loop.</summary>
    private sealed class SilentAdminTarget : IAdminControllable
    {
        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();
        public void Teleport(PlayerRef target, Vector3 position) { }
        public void Kick(PlayerRef target, string reason) { }
        public void Broadcast(string text) { }
    }
}
