# KhaozEngine.Identity.Discord

Discord provider and userinfo validator for `KhaozEngine.Identity`: signs a player in via Discord's
OAuth2 authorization-code + PKCE flow, using the system browser and a local loopback redirect.

## Overview

- **DiscordClientProvider** - `IIdentityProvider` implementation that drives the Discord OAuth2
  auth-code flow against Discord's fixed authorize/token endpoints (no discovery document, unlike
  generic OIDC). `CredentialToken` is the Discord `access_token`.
- **DiscordTokenValidator** - `IIdentityValidator` implementation that verifies a Discord access
  token by calling `GET https://discord.com/api/users/@me` and mapping `id`/`username`/`email` to a
  `VerifiedIdentity`.
- **DiscordProviderOptions** - client id, scopes (default `identify email`), loopback port, and HTTP
  timeout.

Discord is opaque-token OAuth2, not OIDC: there is no ID token or JWKS to validate, so this package
has no `Microsoft.IdentityModel` dependency. It is opt-in and not part of any umbrella package;
reference it directly when a game wants Discord sign-in. `KhaozEngine.Identity.Oidc` and other
provider-specific integrations are separate sibling packages.

## Usage

```csharp
using KhaozEngine.Identity;
using KhaozEngine.Identity.Discord;

DiscordProviderOptions options = new() { ClientId = "your-client-id" };
IBrowserLauncher launcher = new SystemBrowserLauncher(); // from KhaozEngine.Identity.Oidc, or your own
DiscordClientProvider provider = new(options, launcher, port => new HttpLoopbackListener(port));

ProviderCredential credential = await provider.SignInAsync();
// credential.CredentialToken is the Discord access_token; send it to the server for verification.

DiscordTokenValidator validator = new();
VerifiedIdentity? identity = await validator.ValidateAsync(credential.CredentialToken);
```

See [KhaozEngine.Identity](../KhaozEngine.Identity/README.md) for the full client `IdentitySession` +
server exchange walkthrough (this provider/validator pair is constructed directly there, the same way
as above), and
[USING-KHAOZENGINE.md "Identity / sign-in"](../docs/USING-KHAOZENGINE.md) for the end-to-end sequence.

## Dependencies

References `KhaozEngine.Identity` (core seam) and `KhaozEngine.Platform`. Deliberately does not
reference `KhaozEngine.Identity.Oidc`: the PKCE helper and sign-in exception are small, stable, and
duplicated here rather than pulled in as a cross-package dependency.
