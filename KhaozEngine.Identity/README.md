# KhaozEngine.Identity

Pluggable player-identity seam: provider sign-in + server-side verified-subject validation + HMAC session tokens, via the exchange model.

## Overview

`KhaozEngine.Identity` provides transport-agnostic core types and interfaces for authenticating players across KhaozEngine games. It defines:

- **IIdentityProvider** - Client-side sign-in integration (e.g., OIDC, Discord)
- **IIdentityValidator** - Server-side credential verification to a stable subject
- **IdentityValidation** / **IdentityValidationOutcome** - The three-outcome result of `ValidateDetailedAsync`: `Verified`, `Refused`, or `ProviderUnavailable`
- **ITokenCache** / **FileTokenCache** - Persisted sign-in session (provider credential, session token, subject, and display name), so a returning player skips an interactive sign-in
- **IBrowserLauncher** / **ILoopbackListener** - The OS and network seams an interactive sign-in flow drives
- **Interactive.SystemBrowserLauncher** / **Interactive.HttpLoopbackListener** - Ready-to-use browser launch and loopback callback implementations shared by every provider. The child namespace avoids shadowing the source-compatible names in `KhaozEngine.Identity.Oidc`
- **ProviderCredential** - Client sign-in result with refresh state
- **VerifiedIdentity** - Server-verified subject + claims
- **CachedSession** / **IdentityState** - The persisted and in-memory session shapes `IdentitySession` reads and writes. `CachedSession.DisplayName` is nullable so cache files written before the property existed still load
- **SessionToken** - A stateless HMAC-SHA256 token for a request path of the consumer's own: mint it after validating a credential, verify it on every later request. The engine exchange mints a `SignedToken` (`KhaozEngine.Netcode`) for the game server's connect door instead
- **IdentitySession** - The client-side orchestrator: restores the cached session at launch (`RequiresSignIn` / `OfflineGrace` / `SignedIn`), drives interactive sign-in, renews a lapsed credential silently via `RefreshCredentialAsync`, and completes the exchange handshake via `AttachSessionTokenAsync`
- **CredentialRefreshResult** / **CredentialRefreshOutcome** - The result of `RefreshCredentialAsync`: `Refreshed` (a new rotated credential, already persisted) or `Rejected` (a dead chain, fall back to interactive sign-in)
- **SignInException** - The shared base every provider backend's sign-in failure derives from, so cross-provider code catches one type instead of one per backend
- **AuthExchangeRequest** / **AuthExchangeResponse** / **AuthExchangeStatuses** - The `/auth/exchange` wire contract a client and an auth service share, so the JSON shape cannot drift between them

Provider implementations (OIDC, Discord) and the exchange behind `/auth/exchange` are opt-in sibling packages. This
core package depends on `KhaozEngine.Diagnostics`, `KhaozEngine.Platform` and `KhaozEngine.Serialization`. The
loopback listener uses only the BCL and does not add an HTTP or ASP.NET package dependency.

## Usage

```csharp
using System.Net;
using System.Net.Http.Json;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Interactive;

// Client: restore the cached session, then sign in if needed.
// (using KhaozEngine.Identity.Oidc's OidcClientProvider here. Swap in Discord's DiscordClientProvider
// for Discord sign-in, both implement IIdentityProvider)
IBrowserLauncher browser = new SystemBrowserLauncher();
IIdentityProvider provider = new OidcClientProvider(oidcOptions, browser, port => new HttpLoopbackListener(port));
ITokenCache cache = new FileTokenCache(sessionFilePath);
IdentitySession session = new(provider, cache, new IdentitySessionOptions());

IdentityState state = await session.RestoreAsync(ct);
if (state.Status == IdentityStatus.RequiresSignIn)
{
    state = await session.SignInAsync(ct);
    ProviderCredential credential = state.Credential!.Value;

    // Exchange it with the game's auth service, which mounts the engine exchange.
    using HttpResponseMessage reply = await authHttp.PostAsJsonAsync("/auth/exchange",
        new AuthExchangeRequest(credential.ProviderId, credential.CredentialToken), ct);
    AuthExchangeResponse exchange = reply.StatusCode switch
    {
        HttpStatusCode.OK or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            => await reply.Content.ReadFromJsonAsync<AuthExchangeResponse>(ct)
                ?? new AuthExchangeResponse(AuthExchangeStatuses.Unavailable),
        HttpStatusCode.TooManyRequests => new AuthExchangeResponse(AuthExchangeStatuses.RetryLater),   // no body
        HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
            => new AuthExchangeResponse(AuthExchangeStatuses.InvalidCredential),                        // no body
        _ => new AuthExchangeResponse(AuthExchangeStatuses.Unavailable),
    };

    // Complete sign-in with the verified subject and the minted token, the game server's connect token.
    if (exchange.Status == AuthExchangeStatuses.Ok)
        state = await session.AttachSessionTokenAsync(
            exchange.Subject!, exchange.DisplayName, exchange.SessionToken!, exchange.ExpiresAtUtc!.Value, ct);
}
```

The server side is the engine's too. The game's auth service composes an `AuthExchange`
(`KhaozEngine.Identity.Exchange`) from the matching validator (`OidcTokenValidator` or `DiscordTokenValidator`), an
account store and a key loaded with `SigningSecret`, and mounts it with `MapAuthExchange`
(`KhaozEngine.Identity.Exchange.AspNetCore`). The token it mints is a `SignedToken`, which the game server's
`HmacTokenAuthenticator` verifies under the same key. See those packages' READMEs.

`AttachSessionTokenAsync` persists the server-verified subject and display name. A later `RestoreAsync` returns
both values for `SignedIn` and `OfflineGrace`, including after a provider credential refresh. Older cache JSON
without `DisplayName` remains valid and restores it as null.

## The `/auth/exchange` wire contract

`AuthExchangeRequest(Provider, AccessToken)` is the body a client posts, and `AuthExchangeResponse` is the body the
service answers with. `Status` is one of the `AuthExchangeStatuses` wire tokens (`ok`, `not_whitelisted`, `banned`,
`invalid_credential`, `unavailable`), and every other member is null unless the exchange got far enough to know it.
`BanReason` and `BanExpiresAtUtc` are set only on `banned`, and only when the service opts in to ban details.
`RetryLater` (`retry_later`) is never sent: a client synthesizes it from a bare 429, whose `Retry-After` is the full
one minute window when the per-client limit refused it and absent when the global bound did. A 400 or a 413 has no
body either, and means the request itself was refused.

```json
{"provider":"discord","accessToken":"..."}
{"status":"ok","sessionToken":"v2...","expiresAtUtc":"2026-09-30T12:00:00+00:00","subject":"discord:80351110224678912","displayName":"Wren","banReason":null,"banExpiresAtUtc":null}
```

The JSON names are pinned on the records, so the wire does not depend on the caller's serializer options. The shape
is exactly what the engine's exchange writes and what the games' clients read, so a client written against an older
copy of these records reads it unchanged, skipping any member it does not know. The records' `ToString` never prints
the credential, the session token or the account.

The decision behind the endpoint is `KhaozEngine.Identity.Exchange` and the endpoint is
`KhaozEngine.Identity.Exchange.AspNetCore`, opt-in packages for the auth service. A client needs only these records.

## Telling a refused credential from a provider outage

`ValidateAsync` returns null for everything that is not a verified identity, so a Discord 500 or a 429 rate
limit reads exactly like a bad token. That is the wrong answer to act on: a client that treats an outage as a
refusal throws away a good credential and re-runs sign-in against a provider that is already down, which is a
retry loop pointed at an outage.

`ValidateDetailedAsync` reports which of the three happened. `AuthExchange` calls it and answers an outage with 503
`unavailable`. A consumer calling a validator on a path of its own reads it the same way:

```csharp
IdentityValidation result = await validator.ValidateDetailedAsync(credentialTokenFromClient, ct);
switch (result.Outcome)
{
    case IdentityValidationOutcome.Verified:
        VerifiedIdentity identity = result.Identity!.Value;   // the verified subject
        break;
    case IdentityValidationOutcome.Refused:
        // 401 to the client: sign in again.
        break;
    case IdentityValidationOutcome.ProviderUnavailable:
        // 503 to the client: keep the credential, back off, retry.
        break;
}
```

It is a default interface member, so every existing validator already has it: the default calls
`ValidateAsync` and maps null to `Refused`, which is exactly what null meant. A backend that can see more
overrides it. `DiscordTokenValidator` splits on the HTTP status class (any 5xx, 429 and 408 are unavailable,
every other non-success is refused) and treats a request that never completed as unavailable too.
`OidcTokenValidator` reports discovery, JWKS and transport failures as unavailable after keeping them separate
from token signature and claim validation. `result.Detail` carries a developer-facing note (a status code, an
exception message) and is never localized or shown to a player.

Being a default interface member has one consequence worth knowing: an implementation that does not override it
is reachable through the interface rather than through its concrete type.

`AuthExchange` takes one validator per provider and dispatches on `ProviderId`, the `provider` a client posts from
`ProviderCredential.ProviderId`. `KhaozEngine.Identity` itself has no such registry: it is a pair of interfaces plus
the orchestration types above, not a service locator.

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
// Exchange it with the auth service (the POST under Usage) and complete sign-in via the turn-key attach overload.
ProviderCredential credential = session.Current.Credential!.Value;
AuthExchangeResponse exchange = await ExchangeAsync(credential, ct);   // the game's wrapper around that POST
if (exchange.Status == AuthExchangeStatuses.Ok)
    state = await session.AttachSessionTokenAsync(
        exchange.Subject!, exchange.DisplayName, exchange.SessionToken!, exchange.ExpiresAtUtc!.Value, ct);
```

Two contracts make this durable across days and weeks:

- **Persist before exchange.** Most OAuth providers (Discord included) rotate the refresh token on every use
  and invalidate the old one the instant the refresh succeeds. `RefreshCredentialAsync` writes the rotated
  credential to the `ITokenCache` immediately, before the server exchange, so a crash in between cannot lose
  it and leave the next refresh presenting a dead token. The write replaces only the credential slot: the
  subject, display name, session token, session-token expiry, and `LastAuthenticatedUtc` are preserved. A
  provider-level refresh does not extend the offline-grace window, only a successful `AttachSessionTokenAsync`
  re-anchors it.
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
signing key's home, the exchange's guarantees, token-at-rest deterrence).

## Sibling packages

- `KhaozEngine.Identity.Oidc` - OpenID Connect provider backend (auth-code + PKCE)
- `KhaozEngine.Identity.Discord` - Discord OAuth2 provider backend
- `KhaozEngine.Identity.Exchange` - the exchange decision an auth service runs
- `KhaozEngine.Identity.Exchange.AspNetCore` - the endpoint that mounts it as `POST /auth/exchange`
