# KhaozEngine.Identity.Exchange

The decision behind `POST /auth/exchange`: trade a provider credential for the `SignedToken` a game server's
`HmacTokenAuthenticator` verifies at the connect door. Pure logic with no HTTP type in it, so the same core runs
behind any host and every branch is testable without a web server, a provider or a database.

Opt-in, in NO umbrella. It references `KhaozEngine.Identity` (the validator seam and the wire DTOs),
`KhaozEngine.Accounts` (the account store) and `KhaozEngine.Netcode` (`SignedToken`, `SigningSecret`) and nothing
else. A dedicated auth service references it. A game server and a game client never need to.

## Public API

| Type | What it does |
|---|---|
| `AuthExchange` | `new AuthExchange(validators, accounts, signingSecret, options, policy?)` and `ExchangeAsync(provider, accessToken, ct)`. One instance serves every request concurrently. |
| `AuthExchangeResult` | `Outcome`, `SessionToken`, `ExpiresAtUtc`, `Subject`, `DisplayName`, `Ban`, and for the host's log only `Fault` and `Cause`. `ToResponse(includeBanDetails = false)` builds the wire body. |
| `AuthExchangeOutcome` | `Ok`, `NotWhitelisted`, `Banned`, `InvalidCredential`, `Unavailable`, `Malformed`. |
| `AuthExchangeCause` | Why it ended so, for the log line: `UnknownProvider`, `CredentialShape`, `CredentialRefused`, `ProviderUnavailable`, `ProviderTimeout`, `StoreFault`, `InadmissibleSubject`, `PolicyFault`, or `None` for a decision. Never sent. |
| `AuthExchangeOptions` | `TokenLifetime` (required, at most `MaxTokenLifetime` of 365 days), `RequireWhitelist` (true), `MaxCredentialChars` (4096), `MaxDisplayNameChars` (64, at most the store limit of 128), `ProviderTimeout` (10 s), `Clock`. |
| `IAuthExchangePolicy` | The game's display name and claims hooks, both with defaults. |
| `SessionClaims` | The token's display name and optional persistence key. |
| `AuthAdmission` | `Decide(account, now, requireWhitelist)`: the fixed ban-then-whitelist gate, public for a game's own issuer. |

The wire records `AuthExchangeRequest`, `AuthExchangeResponse` and `AuthExchangeStatuses` live in
`KhaozEngine.Identity`, so a client shares them without referencing this package.

## Composing it

```csharp
byte[] key = SigningSecret.Load(Environment.GetEnvironmentVariable, "MYGAME_TOKEN_SECRET")
    ?? throw new InvalidOperationException("MYGAME_TOKEN_SECRET is not set.");
IAccountStore accounts = /* an engine backend, or the game's own adapter */;

var exchange = new AuthExchange(
    new IIdentityValidator[] { new DiscordTokenValidator(clientId, discordHttp) },
    accounts,
    key,
    new AuthExchangeOptions { TokenLifetime = TimeSpan.FromDays(7) });

AuthExchangeResult result = await exchange.ExchangeAsync(request.Provider, request.AccessToken, ct);
```

For local development, `SigningSecret.CreateEphemeral()` gives a key that dies with the process, and an
`InMemoryAccountStore` gives a store that does. Never a source constant.

## The sequence

`ExchangeAsync` runs one fixed order:

1. **Shape.** An unknown provider id (ordinal), or a credential that is empty, over `MaxCredentialChars` or carries
   any character outside visible ASCII (`!` to `~`, so no control character, space or non-ASCII character), is
   `Malformed` before any provider call.
2. **Provider.** `ValidateDetailedAsync` runs under `ProviderTimeout`. A reported outage (a provider 5xx, 429 or 408),
   a validator that throws, or the deadline is `Unavailable`. A refusal is `InvalidCredential`. Neither touches an
   account. The deadline holds even for a validator that ignores its cancellation token or blocks synchronously
   before returning its task, because the call starts on the thread pool.
3. **Account.** The policy resolves the display name, clamped to `MaxDisplayNameChars` without splitting a surrogate
   pair, and the store finds or creates the account. A subject that `AccountStoreRules.IsAdmissibleSubject` refuses
   (empty, carrying `.`, or under `guest:`) is a server fault and never a token.
4. **Gate.** `AuthAdmission.Decide` refuses an active ban first and a missing whitelist flag second. A lapsed timed
   ban admits.
5. **Token.** Only now does the policy issue claims. A persistence key mints v3, none mints v2, expiring at the
   clock plus `TokenLifetime`.

A store or policy exception is `Unavailable` with the exception in `Fault`. The caller's own cancellation
propagates as `OperationCanceledException`. The constructor refuses a key shorter than `SigningSecret.MinimumBytes`,
an empty or duplicated validator set, and any option out of range.

## Mapping a result to HTTP

`KhaozEngine.Identity.Exchange.AspNetCore` mounts the exchange as a minimal-API endpoint with this mapping, its own body
cap, per-client window and global bound, and the log line below. A host on another stack maps it the same way.

| Outcome | Status | Body |
|---|---|---|
| `Ok` | 200 | `ok` with token, expiry, subject and display name |
| `NotWhitelisted` | 403 | `not_whitelisted` with subject and display name |
| `Banned` | 403 | `banned` with subject and display name, ban reason and expiry only with `includeBanDetails` |
| `InvalidCredential` | 401 | `invalid_credential` |
| `Unavailable` | 503 | `unavailable`, whichever dependency failed |
| `Malformed` | 400 | none (`ToResponse` throws for it) |

What a response can reveal is the security property of a public endpoint:

- **No account oracle.** Every answer naming an account follows a verified credential and describes the caller's own
  account. The store is find-or-create, so a first sign-in reads exactly like a returning unwhitelisted one, and no
  answer means "no such account". `InvalidCredential` and `Malformed` return before any lookup.
- **Ban before whitelist.** A banned caller never learns whether they were whitelisted. Ban details are off unless
  the game passes `includeBanDetails: true`.
- **One failure envelope.** A provider outage, the deadline, a store fault and a policy fault all answer the same
  503 body. `Cause` tells the host's log which it was.

## Policy

The defaults suit a game whose subject is `{provider}:{providerSubject}` and whose token is v2: the provider's name,
else the provider subject, is stored, and the token carries the stored name, else the subject. A game overrides
either member:

```csharp
sealed class MyPolicy(ICharacterShells shells) : IAuthExchangePolicy
{
    // Store null when the provider gives no name.
    public string? ResolveDisplayName(VerifiedIdentity identity) => identity.DisplayName;

    // Runs only for an admitted account, so no state is made for a banned or unwhitelisted caller.
    public async ValueTask<SessionClaims> IssueClaimsAsync(AccountRecord account, CancellationToken ct)
    {
        long characterId = await shells.EnsureAsync(account.Subject, ct);
        return new SessionClaims(account.DisplayName ?? account.Subject, $"character:{characterId}");
    }
}
```

The gate order is not part of the policy and cannot be changed by one. `RequireWhitelist = false` is the policy of an
open game. `whitelistOnCreate` on the store is what a new account row says, which every reader of the row agrees on.

## Logging

The core logs nothing. The host logs one line per exchange with the outcome, the cause, the provider id, the HTTP
status and the elapsed time, and the `Fault` when there is one. Never the credential, the session token, the key,
the subject or the display name. `AuthExchangeResult.ToString()` prints only the outcome and the cause, and the wire
records print only the provider or the status, so formatting one into a log line leaks nothing. The engine stores'
exception messages never quote account data. A game's own store may, so log `Fault` where account data may go.
