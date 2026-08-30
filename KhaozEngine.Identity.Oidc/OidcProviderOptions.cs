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
    /// <summary>Bounds the discovery and token-exchange HTTP calls only. It does nothing for the open-ended
    /// wait on the browser redirect: that is <see cref="SignInTimeout"/>.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Deadline for the WHOLE interactive sign-in, the open-ended wait on the loopback redirect
    /// included. Without it a player who opens the browser and never finishes (closes the tab, the browser
    /// crashes, they walk away) leaves the await pending forever, so the <c>using</c> on the loopback listener
    /// never runs and the bound port stays open for the life of the process. On expiry
    /// <c>SignInAsync</c> throws <c>IdentitySignInException</c> and the listener is disposed on the way out.
    /// Default five minutes, which is generous for a human completing a browser flow. Zero or negative (so
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> too) restores the unbounded wait, leaving the
    /// deadline entirely to the caller's own cancellation token.</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Local-dev opt-out from the <c>https</c> requirement on <see cref="Authority"/>. With this set, a
    /// plain-<c>http</c> authority is accepted when (and only when) its host is a loopback address
    /// (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>), so a developer can point at an authority running on
    /// their own machine. It never permits cleartext to a remote host. Default false, which refuses every
    /// non-https authority, matching the server-side validator whose <c>HttpDocumentRetriever</c> requires
    /// https for its own discovery fetch.</summary>
    public bool AllowInsecureLoopbackAuthority { get; init; }
}
