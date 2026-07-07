# KhaozEngine.Identity

Pluggable player-identity seam: provider sign-in + server-side verified-subject validation + HMAC session tokens, via the exchange model.

## Overview

`KhaozEngine.Identity` provides transport-agnostic core types and interfaces for authenticating players across KhaozEngine games. It defines:

- **IIdentityProvider** - Client-side sign-in integration (e.g., OIDC, Discord)
- **IIdentityValidator** - Server-side credential verification to a stable subject
- **ITokenCache** / **FileTokenCache** - Persisted sign-in session (provider credential + session token), so a returning player skips an interactive sign-in
- **IBrowserLauncher** / **ILoopbackListener** - The OS/network seams an interactive sign-in flow drives (opening the system browser, capturing the redirect)
- **ProviderCredential** - Client sign-in result with refresh state
- **VerifiedIdentity** - Server-verified subject + claims
- **CachedSession** / **IdentityState** - The persisted and in-memory session shapes `IdentitySession` reads and writes
- **SessionToken** - A stateless HMAC-SHA256 session token: mint it on the server after validating a credential, verify it on every subsequent request
- **IdentitySession** - The client-side orchestrator: restores the cached session at launch (`RequiresSignIn` / `OfflineGrace` / `SignedIn`), drives interactive sign-in, and completes the exchange handshake via `AttachSessionTokenAsync`

Provider implementations (OIDC, Discord) are opt-in sibling packages. This core package depends only on `KhaozEngine.Diagnostics` and `KhaozEngine.Serialization`; it has no HTTP/ASP.NET dependency of its own.

## Usage

```csharp
// Client: restore the cached session, then sign in if needed.
// (using KhaozEngine.Identity.Oidc's OidcClientProvider here; swap in Discord's DiscordClientProvider
// for Discord sign-in, both implement IIdentityProvider)
IIdentityProvider provider = new OidcClientProvider(oidcOptions, browser, port => new HttpLoopbackListener(port));
ITokenCache cache = new FileTokenCache(sessionFilePath);
IdentitySession session = new(provider, cache, new IdentitySessionOptions());

IdentityState state = await session.RestoreAsync(ct);
if (state.Status == IdentityStatus.RequiresSignIn)
{
    state = await session.SignInAsync(ct);
    // POST state.Credential!.Value.CredentialToken to the consumer's own /auth/exchange endpoint,
    // then complete sign-in with the server's verified subject + minted session token:
    state = await session.AttachSessionTokenAsync(subject, displayName, sessionToken, expiryUtc, ct);
}

// Server: validate the credential, then mint a session token.
// (matching validator for the provider used above, e.g. OidcTokenValidator or DiscordTokenValidator)
IIdentityValidator validator = new OidcTokenValidator(oidcOptions);
VerifiedIdentity? verified = await validator.ValidateAsync(credentialTokenFromClient);
if (verified is VerifiedIdentity identity)
{
    string token = SessionToken.Mint(identity.Subject, identity.DisplayName, expiry, secret);
    // return { token, expiry, identity.Subject, identity.DisplayName } to the client
}
```

A consumer that supports multiple providers at once builds its own lookup, e.g. a
`IReadOnlyDictionary<string, IIdentityValidator>` keyed by provider id, and dispatches to
`validator.ValidateAsync` for whichever provider the client used. `KhaozEngine.Identity` itself has no
such registry: it is a pair of interfaces plus the orchestration types above, not a service locator.

See [USING-KHAOZENGINE.md "Identity / sign-in"](../docs/USING-KHAOZENGINE.md) for the full exchange-model
walkthrough and [SECURITY-BASELINE.md](../docs/SECURITY-BASELINE.md) for the security posture (PKCE, the
`SessionToken` secret's home, token-at-rest deterrence).

## Sibling packages

- `KhaozEngine.Identity.Oidc` - OpenID Connect provider backend (auth-code + PKCE)
- `KhaozEngine.Identity.Discord` - Discord OAuth2 provider backend
