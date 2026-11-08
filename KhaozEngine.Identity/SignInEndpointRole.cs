namespace KhaozEngine.Identity;

/// <summary>The provider endpoint a <see cref="SignInException"/> was reading, carried by
/// <see cref="SignInException.EndpointRole"/>. The endpoint URL itself is never carried.</summary>
public enum SignInEndpointRole
{
    /// <summary>The OIDC authority's well-known discovery document.</summary>
    DiscoveryDocument,

    /// <summary>The provider's token endpoint, for the code exchange or a refresh.</summary>
    TokenEndpoint,
}
