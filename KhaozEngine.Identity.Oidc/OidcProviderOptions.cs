using System;

namespace KhaozEngine.Identity.Oidc;

/// <summary>Configuration for a single generic OIDC provider (authority, client, and loopback/http knobs).</summary>
public sealed class OidcProviderOptions
{
    /// <summary>The OIDC issuer's base URL. Must be <c>https</c>: discovery and the token exchange both run
    /// against it, so a plain-http authority would put the PKCE <c>code_verifier</c> and the returned
    /// <c>id_token</c>/<c>refresh_token</c> on the wire in cleartext. See
    /// <see cref="AllowInsecureLoopbackAuthority"/> for the local-dev opt-out.</summary>
    public required string Authority { get; init; }
    public required string ClientId { get; init; }
    public string Scopes { get; init; } = "openid profile email";
    public int LoopbackPort { get; init; }
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Local-dev opt-out from the <c>https</c> requirement on <see cref="Authority"/>. With this set, a
    /// plain-<c>http</c> authority is accepted when (and only when) its host is a loopback address
    /// (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>), so a developer can point at an authority running on
    /// their own machine. It never permits cleartext to a remote host. Default false, which refuses every
    /// non-https authority, matching the server-side validator whose <c>HttpDocumentRetriever</c> requires
    /// https for its own discovery fetch.</summary>
    public bool AllowInsecureLoopbackAuthority { get; init; }
}
