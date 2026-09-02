# KhaozEngine.Identity.Discord

Discord provider and userinfo validator for `KhaozEngine.Identity`: signs a player in via Discord's
OAuth2 authorization-code + PKCE flow, using the system browser and a local loopback redirect.

## Overview

- **DiscordClientProvider** - `IIdentityProvider` implementation that drives the Discord OAuth2
  auth-code flow against Discord's fixed authorize/token endpoints (no discovery document, unlike
  generic OIDC). `CredentialToken` is the Discord `access_token`. `RefreshAsync` follows the refresh
  rejection contract: a 400 or 401 on the refresh grant (a revoked or expired chain, or an empty stored
  refresh token) returns null so the caller falls back to interactive sign-in, while any other non-success
  status (a 5xx, say) throws `IdentitySignInException` as a transient error to retry later. The interactive
  sign-in code exchange is unchanged: a 400 there is still a hard `IdentitySignInException`.
- **DiscordTokenValidator** - `IIdentityValidator` implementation that verifies a Discord access
  token by calling `GET https://discord.com/api/oauth2/@me` (token introspection) and mapping the nested
  `user` object's `id`/`username`/`email` to a `VerifiedIdentity`. It takes a required `expectedClientId`
  and **rejects any token whose issuing `application.id` is not that id**. This is the audience check that
  makes the validator safe to exchange for a session token: the plain `users/@me` endpoint answers for ANY
  application's access token and never names the issuing app, so validating against it would accept a token
  minted for a *different* Discord app (an account-takeover vector). A non-success response, a token from a
  different app, or a malformed body all return null (fail-closed). `ValidateDetailedAsync` is the member
  that splits the outage case out of that null: any 5xx, a 429 rate limit, a 408, or a request that never
  completed report `ProviderUnavailable`, and every other non-success reports `Refused`. The distinction
  matters because a client that reads an outage as a refusal discards a good token and re-runs sign-in
  against a provider that is already down. `ValidateAsync` keeps its exact old behaviour, transport failure
  included, so existing callers are unaffected.
- **DiscordProviderOptions** - client id, scopes (default `identify email`), loopback port, and HTTP
  timeout. `SignInTimeout` (default 5 minutes) bounds the **whole** interactive sign-in, the open-ended wait
  on the browser redirect included: `HttpTimeout` only feeds the `HttpClient` used for the token exchange, so
  without `SignInTimeout` an abandoned flow (the player closes the tab, the browser crashes, they walk away)
  parks forever and holds the bound loopback port for the life of the process. On expiry `SignInAsync` throws
  `IdentitySignInException` and the listener is disposed on the way out. Set it to zero or negative to restore
  the unbounded wait and bound sign-in with your own cancellation token instead.
- **IdentitySignInException** - this package's recoverable sign-in failure, thrown by every path above. It
  derives from `KhaozEngine.Identity.SignInException`, the shared base in the core package, so a game that
  offers a choice of sign-in providers catches that one type instead of one per backend. Catch this type when
  you need to know the Discord backend specifically. The type stays under this package's own namespace, so
  this package still does not depend on the Oidc sibling, and the base carries a different simple name on
  purpose, so a file importing `KhaozEngine.Identity` alongside this namespace still resolves the unqualified
  `IdentitySignInException`.

Discord is opaque-token OAuth2, not OIDC: there is no ID token or JWKS to validate, so this package
has no `Microsoft.IdentityModel` dependency. It is opt-in and not part of any umbrella package;
reference it directly when a game wants Discord sign-in. `KhaozEngine.Identity.Oidc` and other
provider-specific integrations are separate sibling packages.

## Public clients and refresh-token rotation

- Secretless PKCE sign-in and the refresh grant (no client secret) both require the "Public Client"
  toggle to be enabled in the Discord application's Developer Portal, on the OAuth2 tab. Without it
  the token endpoint rejects both grants.
- Discord refresh tokens are single-use. Every refresh grant rotates the token and invalidates the
  prior one immediately, with no reuse window. That is why the engine persists the rotated credential
  right away on a successful refresh, before any server exchange.
- A stale, already-rotated-away refresh token is documented by Discord as a 400 `invalid_grant`. A 401
  has also been observed in the field for the same case. Both are treated as a definitive rejection:
  `RefreshAsync` returns null and the caller falls back to interactive sign-in.

## Usage

```csharp
using KhaozEngine.Identity;
using KhaozEngine.Identity.Discord;

DiscordProviderOptions options = new() { ClientId = "your-client-id" };
IBrowserLauncher launcher = new SystemBrowserLauncher(); // from KhaozEngine.Identity.Oidc, or your own
// The listener factory is the seam for a localized/branded sign-in completion page: pass it as the
// listener's second argument, e.g. port => new HttpLoopbackListener(port, myLocalizedHtml). Omitted, it
// serves the raw HttpLoopbackListener.DefaultCompletionPageHtml.
DiscordClientProvider provider = new(options, launcher, port => new HttpLoopbackListener(port));

ProviderCredential credential = await provider.SignInAsync();
// credential.CredentialToken is the Discord access_token; send it to the server for verification.

// Pass the consumer's own Discord client id: the validator rejects any token minted for a different app.
DiscordTokenValidator validator = new(options.ClientId);
VerifiedIdentity? identity = await validator.ValidateAsync(credential.CredentialToken);

// Or, to keep a Discord outage from reading as a bad token (503 to the client, not 401):
IdentityValidation result = await validator.ValidateDetailedAsync(credential.CredentialToken);
if (result.Outcome == IdentityValidationOutcome.ProviderUnavailable) { /* back off and retry */ }
```

See [KhaozEngine.Identity](../KhaozEngine.Identity/README.md) for the full client `IdentitySession` +
server exchange walkthrough (this provider/validator pair is constructed directly there, the same way
as above), and
[USING-KHAOZENGINE.md "Identity / sign-in"](../docs/USING-KHAOZENGINE.md) for the end-to-end sequence.

## Dependencies

References `KhaozEngine.Identity` (core seam) and `KhaozEngine.Platform`. Deliberately does not
reference `KhaozEngine.Identity.Oidc`: the PKCE helper and sign-in exception are small, stable, and
duplicated here rather than pulled in as a cross-package dependency.
