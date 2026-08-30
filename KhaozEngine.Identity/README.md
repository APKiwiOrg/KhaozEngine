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
- **IdentitySession** - The client-side orchestrator: restores the cached session at launch (`RequiresSignIn` / `OfflineGrace` / `SignedIn`), drives interactive sign-in, renews a lapsed credential silently via `RefreshCredentialAsync`, and completes the exchange handshake via `AttachSessionTokenAsync`
- **CredentialRefreshResult** / **CredentialRefreshOutcome** - The result of `RefreshCredentialAsync`: `Refreshed` (a new rotated credential, already persisted) or `Rejected` (a dead chain, fall back to interactive sign-in)
- **SignInException** - The shared base every provider backend's sign-in failure derives from, so cross-provider code catches one type instead of one per backend

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

## One catch for every provider backend

Each provider package throws its own `IdentitySignInException` (`KhaozEngine.Identity.Oidc`'s and
`KhaozEngine.Identity.Discord`'s are separate types under their own namespaces, so neither package depends on
the other). Both derive from `SignInException`, which lives here in the core package, so a game that offers a
choice of sign-in providers writes one catch clause against the core package alone:

```csharp
try
{
    state = await session.SignInAsync(ct);
}
catch (SignInException ex)
{
    // Recoverable, whichever backend the player picked: show a retryable sign-in error.
    ShowSignInError(ex.Message);
}
```

Code that does care which backend failed still catches the provider type. The base is named `SignInException`
rather than `IdentitySignInException` on purpose: a consumer's sign-in file imports `KhaozEngine.Identity`
alongside a provider namespace, and a base sharing the providers' simple name would make every unqualified
reference in those files ambiguous.

## Durable silent refresh

When a cached session lapses to `OfflineGrace`, the game silently renews the held credential instead of
prompting for a fresh interactive sign-in. `RefreshCredentialAsync` is the turn-key path:

```csharp
CredentialRefreshResult result = await session.RefreshCredentialAsync(ct);
if (result.Outcome == CredentialRefreshOutcome.Rejected)
{
    // The refresh chain is dead (revoked or expired). Fall back to interactive sign-in.
    state = await session.SignInAsync(ct);
}
// Refreshed: session.Current now carries the rotated credential and the cache already holds it.
// Exchange it with the server and complete sign-in via the turn-key attach overload.
ProviderCredential credential = session.Current.Credential!.Value;
ExchangeResponse exchange = await PostToAuthExchangeAsync(credential.CredentialToken, ct);
state = await session.AttachSessionTokenAsync(
    exchange.Subject, exchange.DisplayName, exchange.SessionToken, exchange.ExpiresAtUtc, ct);
```

Two contracts make this durable across days and weeks:

- **Persist before exchange.** Most OAuth providers (Discord included) rotate the refresh token on every use
  and invalidate the old one the instant the refresh succeeds. `RefreshCredentialAsync` writes the rotated
  credential to the `ITokenCache` immediately, before the server exchange, so a crash in between cannot lose
  it and leave the next refresh presenting a dead token. The write replaces only the credential slot: the
  subject, session token, session-token expiry, and `LastAuthenticatedUtc` are preserved. A provider-level
  refresh does not extend the offline-grace window, only a successful `AttachSessionTokenAsync` re-anchors it.
- **Rejected vs transient.** A `Rejected` outcome (the provider returned null) means the chain is dead and
  interactive sign-in is required. A thrown exception (a 5xx or a transport fault) is transient: the consumer
  keeps the cached session and retries later rather than forcing a sign-in.

A consumer that orchestrates its own provider refresh (calling `IIdentityProvider.RefreshAsync` directly)
uses the `AttachSessionTokenAsync(subject, displayName, credential, sessionToken, expiryUtc, ct)` overload,
passing the freshly refreshed credential so the rotated token is the one persisted. The turn-key
`RefreshCredentialAsync` path already updates `session.Current`, so the shorter attach overload is correct
there too.

See [USING-KHAOZENGINE.md "Identity / sign-in"](../docs/USING-KHAOZENGINE.md) for the full exchange-model
walkthrough and [SECURITY-BASELINE.md](../docs/SECURITY-BASELINE.md) for the security posture (PKCE, the
`SessionToken` secret's home, token-at-rest deterrence).

## Sibling packages

- `KhaozEngine.Identity.Oidc` - OpenID Connect provider backend (auth-code + PKCE)
- `KhaozEngine.Identity.Discord` - Discord OAuth2 provider backend
