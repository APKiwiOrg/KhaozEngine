namespace KhaozEngine.Identity;

/// <summary>Rules that refuse a sign-in or a credential refresh, carried by <see cref="SignInException.Reason"/>.
/// No member's identifiers carry a token, an authorization code, a <c>state</c> value, a client secret, a raw
/// authority URL or a response body.</summary>
public enum SignInRefusal
{
    // Interactive flow.

    /// <summary>Refuses an interactive sign-in that outlived the provider's sign-in timeout, which
    /// <see cref="SignInException.Timeout"/> carries.</summary>
    TimedOut,

    /// <summary>Refuses an interactive sign-in because the browser launcher reported that it could not open the
    /// authorize URL.</summary>
    BrowserLaunchFailed,

    /// <summary>Refuses a loopback redirect whose <c>state</c> is absent or differs from the one the provider sent.
    /// Neither value is carried.</summary>
    RedirectStateMismatch,

    /// <summary>Refuses a loopback redirect whose authorization <c>code</c> is absent or empty.</summary>
    AuthorizationCodeMissing,

    // OIDC authority and discovery.

    /// <summary>Refuses an OIDC authority that is not an absolute URL. The authority is not carried.</summary>
    AuthorityNotAbsolute,

    /// <summary>Refuses an OIDC authority that is not https, other than a plain-http loopback authority under
    /// the local development opt-out. <see cref="SignInException.ActualScheme"/> carries its scheme.</summary>
    AuthorityInsecure,

    /// <summary>Refuses a discovery document request answered with a non-success status.</summary>
    DiscoveryHttpFailure,

    /// <summary>Refuses a discovery document that lacks a required endpoint member, named by
    /// <see cref="SignInException.MemberName"/>.</summary>
    DiscoveryEndpointMissing,

    /// <summary>Refuses a discovery document whose required endpoint member, named by
    /// <see cref="SignInException.MemberName"/>, is null or empty.</summary>
    DiscoveryEndpointEmpty,

    // Token endpoint.

    /// <summary>Refuses a token request answered with a non-success status, other than the 400 or 401 a refresh
    /// reads as a dead refresh chain.</summary>
    TokenHttpFailure,

    /// <summary>Refuses a token response that lacks its credential member, named by
    /// <see cref="SignInException.MemberName"/>.</summary>
    TokenMemberMissing,

    /// <summary>Refuses a token response whose credential member, named by
    /// <see cref="SignInException.MemberName"/>, is null or empty.</summary>
    TokenMemberEmpty,

    /// <summary>Refuses a token response whose <c>expires_in</c> is present but not a JSON number within the
    /// range of <see cref="int"/>.</summary>
    TokenExpiryMalformed,

    // Either endpoint.

    /// <summary>Refuses a success response, from the endpoint <see cref="SignInException.EndpointRole"/> names,
    /// whose body is not JSON. The parser's exception is the inner exception.</summary>
    EndpointJsonMalformed,
}
