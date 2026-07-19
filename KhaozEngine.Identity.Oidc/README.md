# KhaozEngine.Identity.Oidc

Generic OIDC provider and JWKS JWT validator for `KhaozEngine.Identity`: signs a player in via the
authorization-code + PKCE flow, using the system browser and a local loopback redirect.

## Overview

- **OidcClientProvider** - `IIdentityProvider` implementation that drives the authorization-code + PKCE
  (RFC 7636) flow against any standards-compliant OIDC authority: discovers the authorize/token endpoints
  from the authority's well-known document, launches the system browser, and exchanges the returned code
  (with the PKCE verifier and a checked `state`) for an id_token. `CredentialToken` is the OIDC `id_token`.
  `RefreshAsync` follows the refresh rejection contract: a 400 or 401 on the refresh grant (a revoked or
  expired chain, or an empty stored refresh token) returns null so the caller falls back to interactive
  sign-in, while any other non-success status (a 5xx, say) throws `IdentitySignInException` as a transient
  error to retry later. The interactive sign-in code exchange is unchanged: a 400 there is still a hard
  `IdentitySignInException`.
- **OidcTokenValidator** - `IIdentityValidator` implementation that validates an id_token against the
  issuer's discovery document + JWKS (via `Microsoft.IdentityModel.Protocols.OpenIdConnect` /
  `JsonWebTokens`), checks issuer/audience/lifetime/signature, and maps the `sub` claim (+ `name` or
  `preferred_username`) to a `VerifiedIdentity`.
- **SystemBrowserLauncher** - `IBrowserLauncher` implementation that opens the sign-in URL in the OS
  default browser via `KhaozEngine.Platform.Browser`.
- **HttpLoopbackListener** - `ILoopbackListener` implementation that binds a short-lived
  `HttpListener` on `http://127.0.0.1:<port>/`, captures the provider's redirect, and returns the
  parsed query string. The completion page shown to the browser after sign-in is the constructor's
  second argument (`new HttpLoopbackListener(port, myLocalizedHtml)`), defaulting to the raw
  `HttpLoopbackListener.DefaultCompletionPageHtml` when omitted. Pass a localized or branded page here
  (the identity packages do not reference the localization catalog, so the page is caller-supplied).
- **OidcProviderOptions** - authority, client id, scopes (default `openid profile email`), loopback port,
  and HTTP timeout.

It is opt-in and not part of any umbrella package: reference it directly when a game wants generic
OIDC sign-in (Auth0, Okta, Azure AD, or any standards-compliant OIDC provider). Discord and other
provider-specific integrations are separate sibling packages.

## Usage

```csharp
using KhaozEngine.Identity;
using KhaozEngine.Identity.Oidc;

OidcProviderOptions options = new() { Authority = "https://your-tenant.example.com", ClientId = "your-client-id" };
IBrowserLauncher launcher = new SystemBrowserLauncher();
OidcClientProvider provider = new(options, launcher, port => new HttpLoopbackListener(port));

ProviderCredential credential = await provider.SignInAsync();
// credential.CredentialToken is the OIDC id_token; send it to the server for verification.

OidcTokenValidator validator = new(options);
VerifiedIdentity? identity = await validator.ValidateAsync(credential.CredentialToken);
```

See [KhaozEngine.Identity](../KhaozEngine.Identity/README.md) for the full client `IdentitySession` +
server exchange walkthrough (this provider/validator pair is constructed directly there, the same way
as above), and
[USING-KHAOZENGINE.md "Identity / sign-in"](../docs/USING-KHAOZENGINE.md) for the end-to-end sequence.

## Dependencies

References `KhaozEngine.Identity` (core seam) and `KhaozEngine.Platform` (browser launch), plus
`Microsoft.IdentityModel.Protocols.OpenIdConnect` and `Microsoft.IdentityModel.JsonWebTokens` for
discovery-document and JWKS-backed token validation. These HTTP/IdentityModel dependencies are
intentionally isolated to this opt-in package; `KhaozEngine.Identity` itself stays transport-agnostic.
