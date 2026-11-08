using System;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>A structured refusal of a trusted proxy list, from <see cref="TrustedProxyNetworks.WithBothFamilies"/> and
/// <see cref="AuthExchangeHosting.AddAuthExchangeHosting"/>. Existing <see cref="ArgumentException"/> catches remain
/// valid. <see cref="ArgumentException.ParamName"/> names the refused list: <c>networks</c> from the helper and
/// <c>TrustedProxies</c> when the hosting is added. No identifier carries a network value.</summary>
public sealed class TrustedProxyConfigurationException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason, retaining the parameter name.</summary>
    /// <param name="reason">The rule that refused the list.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    /// <param name="paramName">The refused list.</param>
    public TrustedProxyConfigurationException(TrustedProxyConfigurationRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the list.</summary>
    public TrustedProxyConfigurationRefusal Reason { get; }

    /// <summary>The zero-based position of the refused network in the list as the caller passed it, for
    /// <see cref="TrustedProxyConfigurationRefusal.CatchAllNetwork"/>. The network itself is not carried.</summary>
    public int? Index { get; init; }
}
