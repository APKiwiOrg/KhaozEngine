namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>Rules that refuse a trusted proxy list, carried by <see cref="TrustedProxyConfigurationException.Reason"/>.
/// No member's identifiers carry a network value.</summary>
public enum TrustedProxyConfigurationRefusal
{
    /// <summary>Refuses a proxy network that trusts every IPv4 caller: a /0 in either family, which an unset
    /// <see cref="System.Net.IPNetwork"/> is, or an IPv6 network holding the whole IPv4-mapped block.
    /// <see cref="TrustedProxyConfigurationException.Index"/> carries its position in the list.</summary>
    CatchAllNetwork,
}
