using System;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using IPNetwork = System.Net.IPNetwork;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// The host-level half of an auth service: forwarded headers from named proxies only, and an optional server-wide body
/// cap. The endpoint's own bounds come with <see cref="AuthExchangeEndpoints.MapAuthExchange"/> and need none of this.
/// </summary>
public static class AuthExchangeHosting
{
    /// <summary>
    /// Configures forwarded headers and the optional Kestrel body cap from <paramref name="options"/>. Call
    /// <see cref="UseAuthExchangeHosting"/> on the built application, before its endpoints.
    /// </summary>
    /// <remarks>
    /// With trusted proxies named, <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are read from those networks only
    /// (in both address families), the framework's default loopback entries are removed, and the forward limit is one,
    /// so only the hop the trusted proxy appended is believed. With none named, forwarded headers are off.
    /// </remarks>
    /// <param name="builder">The service's builder.</param>
    /// <param name="options">The trusted proxies and the body cap, or <c>null</c> for no proxy and no cap.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null, or the proxy list is.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The body cap is not positive.</exception>
    /// <exception cref="ArgumentException">A trusted network is a catch-all (see
    /// <see cref="AuthExchangeHostingOptions.TrustedProxies"/>). The message names its position, never its
    /// value.</exception>
    public static WebApplicationBuilder AddAuthExchangeHosting(this WebApplicationBuilder builder,
        AuthExchangeHostingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new AuthExchangeHostingOptions();
        options.Validate();
        var trusted = TrustedProxyNetworks.WithBothFamilies(options.TrustedProxies);

        builder.Services.AddSingleton(new HostingMarker(options));
        builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            // Cleared first: the framework trusts loopback by default, and "trusted only from the named networks" has
            // to mean exactly that, or a same-host process could name its own bucket without being listed.
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            forwarded.ForwardLimit = 1;
            forwarded.ForwardedHeaders = trusted.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (IPNetwork network in trusted) forwarded.KnownIPNetworks.Add(network);
        });
        if (options.MaxRequestBodyBytes is long cap)
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = cap);
        return builder;
    }

    /// <summary>
    /// Adds the forwarded-headers middleware configured by <see cref="AddAuthExchangeHosting"/>. Call it before the
    /// exchange endpoint and anything else that reads the client address. Once the hosting is added,
    /// <see cref="AuthExchangeEndpoints.MapAuthExchange"/> refuses to map until this has run.
    /// </summary>
    /// <param name="app">The built application.</param>
    /// <returns><paramref name="app"/>.</returns>
    /// <exception cref="InvalidOperationException"><see cref="AddAuthExchangeHosting"/> was not called on the builder,
    /// so there is no configuration to apply.</exception>
    public static IApplicationBuilder UseAuthExchangeHosting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<HostingMarker>() is not { } marker)
            throw new InvalidOperationException(
                "UseAuthExchangeHosting needs AddAuthExchangeHosting on the builder first, which names the trusted proxies.");
        marker.MarkMiddlewareUsed();
        return app.UseForwardedHeaders();
    }

    // Registered by AddAuthExchangeHosting so the Use call can tell a composition that skipped it, and flipped by the
    // Use call so MapAuthExchange can tell a composition that added the hosting and never ran its middleware.
    internal sealed class HostingMarker(AuthExchangeHostingOptions options)
    {
        private int middlewareUsed;

        public AuthExchangeHostingOptions Options => options;

        public bool MiddlewareUsed => Volatile.Read(ref middlewareUsed) != 0;

        public void MarkMiddlewareUsed() => Volatile.Write(ref middlewareUsed, 1);
    }
}
