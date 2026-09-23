using System;
using System.Collections.Generic;
using System.Net;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// What <see cref="AuthExchangeHosting.AddAuthExchangeHosting"/> configures on the host: which proxies are believed
/// about the client address, and an optional server-wide body cap.
/// </summary>
public sealed class AuthExchangeHostingOptions
{
    /// <summary>
    /// The networks <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are believed from, each registered in both
    /// address families (<see cref="TrustedProxyNetworks.WithBothFamilies"/>). Empty by default, which turns forwarded
    /// headers OFF: no peer is believed, loopback included, and every limit keys on the TCP peer address. Behind a proxy
    /// that means every caller shares the proxy's bucket, so a proxied service names its proxies, usually
    /// <see cref="TrustedProxyNetworks.PrivateRanges"/> or the proxy's own subnet. Only the last hop is read, so a
    /// client cannot choose its bucket by prepending addresses of its own.
    /// </summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; init; } = Array.Empty<IPNetwork>();

    /// <summary>
    /// A server-wide Kestrel cap on every request body, in bytes, or <c>null</c> to leave Kestrel's own default. The
    /// exchange endpoint carries its own cap either way (<see cref="AuthExchangeEndpointOptions.MaxRequestBodyBytes"/>),
    /// so this one bounds whatever other routes the service maps. Both games set 8 KiB.
    /// </summary>
    public long? MaxRequestBodyBytes { get; init; }

    internal void Validate()
    {
        if (TrustedProxies is null)
            throw new ArgumentNullException(nameof(TrustedProxies), "Pass an empty list to trust no proxy.");
        if (MaxRequestBodyBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes), "The server-wide body cap must be positive.");
    }
}
