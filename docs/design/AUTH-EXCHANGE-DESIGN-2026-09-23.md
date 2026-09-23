# The auth exchange, the account store and the signing secret

Status: proposed, awaiting owner review. Engine issue
[#707](https://github.com/APKiwiOrg/KhaozEngine/issues/707). No code exists yet.

Rationale only. Once shipped, the API and its use belong in the package READMEs, the "Identity / sign-in"
section of [USING-KHAOZENGINE.md](../USING-KHAOZENGINE.md) and the rows of
[DEPENDENCY-SEAMS.md](../DEPENDENCY-SEAMS.md). This document then stays as history.

## 1. The problem and the evidence

Ruinborne and Grimhollow each run a small ASP.NET Core service that trades a Discord access token for a
`SignedToken` the game server's `HmacTokenAuthenticator` verifies at the connect door. Grimhollow's R2 copied
Ruinborne's service file for file. The engine supplies both ends (`IIdentityValidator` and `SignedToken`) but
nothing in the middle, so each game wrote the middle itself.

| Concern | Ruinborne | Grimhollow |
|---|---|---|
| Secret loader | `Ruinborne.Core/Auth/HmacTokenSecret.cs:12` | `Grimhollow.Shared/GrimhollowTokenSecret.cs:27` |
| Exchange decision | `Ruinborne.Auth/AuthExchange.cs:30` | `Grimhollow.Auth/AuthExchange.cs:57` |
| Body cap, forwarded headers, rate limits | `Ruinborne.Auth/Program.cs:44-126` | `Grimhollow.Auth/Program.cs:16-77` |
| Status mapping and fault ladder | `Ruinborne.Auth/Program.cs:212-298` | `Grimhollow.Auth/AuthExchangeEndpoint.cs:28-93` |
| Trusted proxy list | `Ruinborne.Auth/Program.cs:65-71` | `Grimhollow.Auth/TrustedProxyNetworks.cs` |
| Wire DTOs and status tokens | `Ruinborne.Core/Auth/AuthProtocol.cs` | `Grimhollow.Shared/AuthProtocol.cs` |
| Account store contract | identity and ban members of `Ruinborne.Core/Persistence/IRuinborneStore.cs:23-40,210-213` | `Grimhollow.Persistence/AccountsStore.cs:58` |
| Store backends | `Ruinborne.Persistence/SqlRuinborneStore.cs:41-240,422-476` plus `InMemoryRuinborneStore` | `SqliteAccountsStore.cs`, `SqlServerAccountsStore.cs` |
| Ban seam adapter | `Ruinborne.Persistence/RelationalBanStore.cs` | `Grimhollow.Server/Admin/AccountsBanStore.cs` |
| Dev composition | `Ruinborne.Auth/Program.cs:132-180` | `Grimhollow.Auth/AuthBoot.cs:41` |

One correction to the issue. Only Grimhollow has the flat accounts table (four columns, since widened to
seven) and a SQLite backend. Ruinborne's accounts live in its SSDT game schema (`account`, `account_login`
and `ban` under `Ruinborne.Database/dbo/Tables/`), the `account_id` surrogate is a foreign key target for
`player_character`, and its backends are SQL Server and in-memory. That fact shapes the migration in
section 6: Ruinborne adapts the engine seam over its own schema rather than adopting the engine tables.

### Identical in both

The 8 KiB Kestrel body cap, the 4096 character credential cap, the `discord` provider check, the per-IP fixed
window of 5 per minute with no queue, the global concurrency bound of 20 with a 20 deep oldest-first queue,
the 429 with `Retry-After`, `X-Forwarded-For` plus `X-Forwarded-Proto` trusted from the RFC 1918 ranges, the
unlimited `/healthz`, the ban-then-whitelist order, the 7 day token lifetime, the secret rule (standard
base64, trimmed, at least 32 decoded bytes, unset means null, set but invalid throws), the wire field names
and status strings, 200, 403, 403 and 401 for ok, not whitelisted, banned and invalid credential, and a fault
ladder that turns transport failures, timeouts and store exceptions into the `unavailable` envelope.

### Different by accident

The engine picks one answer for each and both games converge on it.

| Difference | Ruinborne | Grimhollow | Engine |
|---|---|---|---|
| Validation call | `ValidateAsync`, so a Discord 5xx or 429 answers 401 and the client discards a good token | `ValidateDetailedAsync`, outage answers 503 | detailed |
| Unavailable status code | 502 for a provider transport failure, 503 otherwise | 503 always | 503 always |
| Provider call deadline | none, `HttpClient` default of 100 s | 10 s (`DiscordHttp.cs:21`) | 10 s in the exchange core, any validator |
| Trusted proxy families | IPv4-mapped IPv6 only, prefixes typed by hand | both families, mapped form derived | both, derived |
| Dev signing key | a source constant bound to the in-memory store | random per process | random per process, never a constant |
| Ban adapter write order | unban clears the cache before the store write | store first | store first, reload swaps atomically |
| Dev whitelist | exchange-level bypass flag | store-level `whitelistOnCreate` | both, for different reasons (section 3) |

### Different on purpose

These stay game policy and the engine must let each game keep them.

- **Subject minting.** Ruinborne mints the provider-neutral `acct:<account_id>` and links provider logins in
  `account_login`. Grimhollow uses `discord:<snowflake>` as the primary key.
- **Token claims.** Ruinborne ensures a character identity shell after admission and mints a v3 token with
  its persistence key. Grimhollow mints v2.
- **Ban storage and visibility.** Ruinborne keeps bans in their own table and sends the reason and expiry to
  the banned client. Grimhollow keeps ban columns on the account row and sends neither.
- **Account data kept.** Ruinborne stores email, status and who whitelisted the account. Grimhollow stores a
  per-account `debug` flag.
- **Local development.** Ruinborne has `/auth/local-profile`, `LocalDevelopmentMode` and a deep readiness
  probe. Grimhollow has a local SQLite accounts file.
- **Unconfigured start.** Ruinborne boots and reports unready. Grimhollow refuses to start when a hosted
  database has no secret.
- **Display name fallback.** Ruinborne stores null when the provider gives no name. Grimhollow stores the
  provider subject. Stored rows depend on this, so it stays a policy hook.

## 2. Package layout and dependency edges

```
KhaozEngine.Netcode                        + SigningSecret, no new edge
KhaozEngine.Identity                       + the /auth/exchange wire DTOs, no new edge
KhaozEngine.Accounts                    -> KhaozEngine.Netcode   (IBanStore, BanRecord for AccountBanStore)
KhaozEngine.Accounts.Sqlite             -> KhaozEngine.Accounts, KhaozEngine.Sqlite, Microsoft.Data.Sqlite, SQLitePCLRaw.lib.e_sqlite3
KhaozEngine.Accounts.SqlServer          -> KhaozEngine.Accounts, Microsoft.Data.SqlClient
KhaozEngine.Identity.Exchange           -> KhaozEngine.Identity, KhaozEngine.Accounts, KhaozEngine.Netcode
KhaozEngine.Identity.Exchange.AspNetCore -> KhaozEngine.Identity.Exchange, Microsoft.AspNetCore.App [FrameworkReference]
```

Every edge is forward and acyclic. Nothing references any of the five new packages back.

**Dependency-free parts.** `SigningSecret` sits beside `SignedToken` in `Netcode` because both ends need it:
the auth service mints under the secret and the game server verifies under it, and the game server must not
reference the exchange to load its key. The account store seam, `InMemoryAccountStore` and the `IBanStore`
adapter are `KhaozEngine.Accounts`, a seam package in the `DEPENDENCY-SEAMS.md` sense (engine edges only, no
third-party package, headless-testable against the in-memory store). The exchange's decision logic is
`KhaozEngine.Identity.Exchange`, which takes a validator, a store and a secret and returns an outcome with no
HTTP type in sight. A separate `Accounts` package, rather than a section of the exchange, is what lets the
game server and an admin console take the store and the ban adapter without the exchange or `Identity`.

**The ASP.NET Core part.** The minimal-API handler, the per-endpoint body cap, forwarded-header
configuration, the trusted proxy presets and the rate limits live in
`KhaozEngine.Identity.Exchange.AspNetCore`, the second package after `KhaozEngine.Server.Admin` to take
`Microsoft.AspNetCore.App` as a `FrameworkReference`. The name follows the third-party-named backend
convention (`Physics.Bepu`, `Netcode.LiteNetLib`, `Catalog.AzureBlob`).

**The backends.** `KhaozEngine.Accounts.Sqlite` and `.SqlServer` sit beside the `WorldStore`, `Commerce` and
`Catalog` provider pairs. The SQLite one opens through `SqliteStoreConnection`, like every SQLite store in the
engine. The drivers are declared in these two packages and nowhere below them.

**Umbrellas.** None of the five enters any umbrella. The web package follows the `Server.Admin` precedent: a
sim server that runs no auth endpoint must not carry the ASP.NET Core shared framework. The exchange core is
needed only by a dedicated auth service, which should not take the sim stack either (Grimhollow's csproj
says so). The two backends follow the rule every SQL provider follows. `KhaozEngine.Accounts` could join the
`Server` umbrella the way the `WorldStore` core did, but the `Commerce` precedent (a server-only seam stays
opt-in until more than its first consumers want it) fits better today. Owner question 4.

**Architecture tests.** All five join `OptInBackends`. The two drivers gain the SQLite and SQL Server homes
in `ThirdPartyHomes`. Two new facts pin the edges above (`Accounts_ReferencesOnlyNetcode`,
`IdentityExchange_ReferencesOnlyIdentityAccountsAndNetcode`). A new fact,
`AspNetCoreFramework_IsContainedToWebPackages`, allows the `Microsoft.AspNetCore.App` framework reference
only in `Server.Admin` and `Identity.Exchange.AspNetCore` among packable projects, because today nothing
enforces the "only package that references ASP.NET Core" claim and this change makes it false.

## 3. Public API sketches

### Signing secret (`KhaozEngine.Netcode`)

```csharp
public static class SigningSecret
{
    public const int MinimumBytes = 32;

    // Null when raw is unset or blank. Throws InvalidOperationException naming sourceName (never the value)
    // when raw is not standard base64 or decodes to fewer than MinimumBytes.
    public static byte[]? Decode(string? raw, string sourceName);

    // Decode(read(variable), variable). The environment arrives as a delegate so a test writes no process state.
    public static byte[]? Load(Func<string, string?> read, string variable);

    // MinimumBytes from RandomNumberGenerator. Local development only: a key that cannot leave the process.
    public static byte[] CreateEphemeral();
}
```

The issue proposed an `ISigningSecretSource` interface. Every source both games use is a string read from
configuration (Key Vault reaches both as an environment variable), so the shared part is the validation and
a delegate carries the source. Key rotation is where an interface would earn its place. Owner question 8.
The variable names stay game-owned (`RUINBORNE_TOKEN_SECRET`, `GRIMHOLLOW_TOKEN_SECRET`).

### Wire contract (`KhaozEngine.Identity`)

```csharp
public sealed record AuthExchangeRequest(string Provider, string AccessToken);

public sealed record AuthExchangeResponse(
    string Status, string? SessionToken = null, DateTimeOffset? ExpiresAtUtc = null,
    string? Subject = null, string? DisplayName = null,
    string? BanReason = null, DateTimeOffset? BanExpiresAtUtc = null);

public static class AuthExchangeStatuses
{
    public const string Ok = "ok";
    public const string NotWhitelisted = "not_whitelisted";
    public const string Banned = "banned";
    public const string InvalidCredential = "invalid_credential";
    public const string Unavailable = "unavailable";
    public const string RetryLater = "retry_later";   // client-synthesized from a bare 429, never sent
}
```

These go in `Identity` because a client needs them and `Identity` is already in every client graph through
`Foundation`. The shapes are exactly the union of the two games' records, so both clients can switch without
a wire change. Owner question 3.

### Account store (`KhaozEngine.Accounts`)

```csharp
public readonly record struct AccountBan(string Reason, DateTimeOffset? Until)
{
    public bool IsActive(DateTimeOffset now) => Until is not { } until || until > now;
}

// Ban is the FILED ban. A lapsed timed ban may stay filed, which is Grimhollow's operator history.
public sealed record AccountRecord(string Subject, string? DisplayName, bool Whitelisted, AccountBan? Ban)
{
    public bool IsBanActive(DateTimeOffset now) => Ban is { } ban && ban.IsActive(now);
}

// What a verified sign-in hands the store. The STORE mints the subject: Ruinborne's acct:<id> needs its
// identity column, and the engine backends mint {ProviderId}:{ProviderSubject}, which is Grimhollow's format.
public readonly record struct AccountSignIn(
    string ProviderId, string ProviderSubject, string? DisplayName,
    IReadOnlyDictionary<string, string> Claims, DateTimeOffset At);

public interface IAccountStore
{
    Task<AccountRecord> FindOrCreateAsync(AccountSignIn signIn, CancellationToken ct = default);
    Task<AccountRecord?> FindAsync(string subject, CancellationToken ct = default);
    Task<IReadOnlyList<AccountRecord>> ListAsync(string? afterSubject = null, int limit = 500, CancellationToken ct = default);
    Task<IReadOnlyList<AccountRecord>> ListBannedAsync(CancellationToken ct = default);

    // Writes name an existing account and return it as it stands afterwards, or null for an unknown subject.
    // None creates a row: a whitelist or ban filed against a subject nobody signed in as is a typo becoming policy.
    Task<AccountRecord?> SetWhitelistedAsync(string subject, bool whitelisted, CancellationToken ct = default);
    Task<AccountRecord?> BanAsync(string subject, string reason, DateTimeOffset? until, CancellationToken ct = default);
    Task<AccountRecord?> UnbanAsync(string subject, CancellationToken ct = default);
}

public sealed class InMemoryAccountStore(bool whitelistOnCreate) : IAccountStore { /* reference and test store */ }

// The ONE IBanStore over the account store. IsBanned answers from a cache and re-checks expiry on every call.
// Writes go to the store first and the cache second. An unknown subject throws ArgumentException, which
// Server.Admin already renders as a 400. LoadAsync reads ListBannedAsync into a new map and swaps it in.
public sealed class AccountBanStore(IAccountStore accounts, TimeProvider clock) : IBanStore
{
    public Task LoadAsync(CancellationToken ct = default);
}
```

`whitelistOnCreate` is a required constructor argument on every store, with no default, because it is the
highest-consequence value in the composition (Grimhollow made it public for the same reason). A composition
root decides it from the store instance it built, never from a stray variable.

The backends take the table location and a schema mode:

```csharp
public sealed class SqliteAccountStore(string connectionString, bool whitelistOnCreate,
    AccountTableOptions? table = null) : IAccountStore, IDisposable;

public sealed class SqlServerAccountStore(string connectionString, bool whitelistOnCreate,
    SqlServerAccountStoreOptions? options = null) : IAccountStore;

public sealed record AccountTableOptions(string Table = "accounts");
public sealed record SqlServerAccountStoreOptions(string Schema = "dbo", string Table = "accounts",
    AccountSchemaMode SchemaMode = AccountSchemaMode.AutoCreate);
```

A table or schema name is validated as a plain SQL identifier and bracket-quoted, so configuration cannot
inject SQL. The SQL Server backend bootstraps lazily on the first call (a constructor that opens a connection
turns an auto-paused Azure SQL database into a service that cannot start) and offers `ValidateOnly` for a
runtime identity without DDL rights, as `SECURITY-BASELINE.md` asks.

### Exchange core (`KhaozEngine.Identity.Exchange`)

```csharp
public enum AuthExchangeOutcome { Ok, NotWhitelisted, Banned, InvalidCredential, Unavailable, Malformed }

public sealed record AuthExchangeResult(
    AuthExchangeOutcome Outcome, string? SessionToken = null, DateTimeOffset? ExpiresAtUtc = null,
    string? Subject = null, string? DisplayName = null, AccountBan? Ban = null,
    Exception? Fault = null)   // for the host's log, never serialized
{
    public AuthExchangeResponse ToResponse(bool includeBanDetails);
}

public sealed class AuthExchangeOptions
{
    public required TimeSpan TokenLifetime { get; init; }
    public bool RequireWhitelist { get; init; } = true;
    public int MaxCredentialChars { get; init; } = 4096;
    public int MaxDisplayNameChars { get; init; } = 64;
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

public interface IAuthExchangePolicy
{
    // Default: the provider's name, else the provider subject (Grimhollow). Null means the provider gave none.
    string? ResolveDisplayName(VerifiedIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.Subject : identity.DisplayName;

    // Runs only after admission. Default: the stored name, else the subject, and no persistence key (v2 token).
    ValueTask<SessionClaims> IssueClaimsAsync(AccountRecord account, CancellationToken ct) =>
        ValueTask.FromResult(new SessionClaims(account.DisplayName ?? account.Subject, PersistenceKey: null));
}

public readonly record struct SessionClaims(string DisplayName, string? PersistenceKey);

// The fixed gate, public so a game's own issuer (Ruinborne's local profile) reuses the same order.
public static class AuthAdmission
{
    public static AuthExchangeOutcome Decide(AccountRecord account, DateTimeOffset now, bool requireWhitelist);
}

public sealed class AuthExchange(
    IEnumerable<IIdentityValidator> validators, IAccountStore accounts, byte[] signingSecret,
    AuthExchangeOptions options, IAuthExchangePolicy? policy = null)
{
    public Task<AuthExchangeResult> ExchangeAsync(string? provider, string? accessToken, CancellationToken ct = default);
}
```

`ExchangeAsync` runs one fixed sequence. The shape check (a known provider id, a non-blank credential no
longer than `MaxCredentialChars`) answers `Malformed` before any provider call. `ValidateDetailedAsync` runs
under `ProviderTimeout`, and `ProviderUnavailable` or the deadline answers `Unavailable` while `Refused`
answers `InvalidCredential`, both before any account is touched. The policy resolves the display name, which
is clamped to `MaxDisplayNameChars`, and the store finds or creates the account. The minted subject is
checked against what `SignedToken` and the join gate accept (non-empty, no `.`, not under the reserved
`guest:` prefix), and a store that returns anything else is a server fault. `AuthAdmission.Decide` refuses a
banned account first and an unwhitelisted one second. Only then does the policy issue claims, and the core
mints v3 when a persistence key is present and v2 otherwise, expiring at the clock plus `TokenLifetime`. Any
store or policy exception becomes `Unavailable` with the exception in `Fault`. Caller cancellation
propagates. The constructor refuses a secret shorter than `SigningSecret.MinimumBytes`.

How each game plugs in:

- **Validator.** Any `IIdentityValidator`, keyed by `ProviderId`. Both games pass one `DiscordTokenValidator`.
- **Policy.** Ruinborne implements `IAuthExchangePolicy` to pass the provider name through unchanged and to
  ensure the character identity shell in `IssueClaimsAsync`, returning its persistence key. Grimhollow uses
  the defaults.
- **Whitelist.** `whitelistOnCreate` on the store is what a new row says, so every reader of the row (the
  exchange, a join backstop, a console) agrees. `RequireWhitelist = false` is the exchange policy for an open
  game. Ruinborne's dev bypass becomes `RequireWhitelist = false` bound to its in-memory store instance.
- **The order is not pluggable.** Ban before whitelist is a disclosure property (a banned player never learns
  whether they were whitelisted), so a policy can add claims but cannot reorder the gate.
- **DTO extensions.** The response is fixed. Ruinborne's ban fields ride `IncludeBanDetails`. Grimhollow's
  client-only `RetryAfterSeconds` stays in its client. A game that needs a different wire maps its own route
  over the core and the public mapping helpers below, which is also how Ruinborne keeps `/auth/local-profile`.

### HTTP handler (`KhaozEngine.Identity.Exchange.AspNetCore`)

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddAuthExchangeHosting(new AuthExchangeHostingOptions
{
    TrustedProxies = TrustedProxyNetworks.PrivateRanges,   // empty by default, which trusts loopback only
    MaxRequestBodyBytes = 8 * 1024,                         // server-wide Kestrel cap
});
WebApplication app = builder.Build();
app.UseAuthExchangeHosting();                               // UseForwardedHeaders

app.MapGet("/healthz", ...);                                // game-owned, untouched by the exchange limits
app.MapAuthExchange(exchange, new AuthExchangeEndpointOptions
{
    Pattern = "/auth/exchange",
    IncludeBanDetails = false,
});
app.Run();
```

```csharp
public sealed class AuthExchangeEndpointOptions
{
    public string Pattern { get; init; } = "/auth/exchange";
    public long MaxRequestBodyBytes { get; init; } = 8 * 1024;   // IRequestSizeLimitMetadata on the endpoint
    public int PermitsPerClientPerMinute { get; init; } = 5;
    public int Ipv6PartitionPrefixLength { get; init; } = 64;
    public int MaxConcurrentExchanges { get; init; } = 20;
    public int MaxQueuedExchanges { get; init; } = 20;
    public bool IncludeBanDetails { get; init; }
}

public static class AuthExchangeEndpoints
{
    public static RouteHandlerBuilder MapAuthExchange(this IEndpointRouteBuilder routes, AuthExchange exchange,
        AuthExchangeEndpointOptions? options = null);
    public static IResult ToHttpResult(AuthExchangeResult result, bool includeBanDetails);
    public static IResult Unavailable();
}
```

The mapped endpoint carries its own bounds, so they hold whatever the host's middleware is. The body cap is
endpoint metadata, which routing applies to `IHttpMaxRequestBodySizeFeature`. The handler takes the
`HttpContext` rather than a bound parameter, acquires the per-client fixed window and then the global
concurrency limiter (both engine-owned `System.Threading.RateLimiting` instances created at map time), and
only then reads and parses the body. A flood is rejected before any JSON parsing, as in both games today, and
nothing depends on the host calling `UseRateLimiter` or leaving its `GlobalLimiter` alone. The body is read
and written with fixed web-default JSON options, so a host's global JSON settings cannot change the wire.
If forwarded headers are not configured when the endpoint is mapped, a startup warning names the peer
address the limiter will partition on.

## 4. One source of truth for accounts and bans

**Bans.** The account store is the only place a ban is filed. `AccountBanStore` adapts it to `IBanStore`, and
a game hands that one instance to every ban consumer: the `BanGateAuthenticator` inside `ConnectionGate`,
`WorldServer` or `ShardedWorldServer` as `banStore:`, `TileWorldServerConfig.BanStore`, and
`ServerAdmin(bans:)`. A `POST /ban` on `Server.Admin` therefore writes the account row, the door refuses at
once from the cache, and the next exchange reads the same row directly. A game on this seam must not also
wire `WorldStoreBanStore`, whose `ban:{accountId}` keys would be a second list. Ruinborne already retired
those keys, and Grimhollow never used them. The `DEPENDENCY-SEAMS.md` ban row gains that sentence.

**Out-of-band writes.** A console that writes SQL directly, or a second server head, changes rows the door
cache has not seen. The exchange is unaffected because it reads the store per request. For the door,
`AccountBanStore.LoadAsync` is an idempotent reload the host may run on a timer. Both games also re-read the
account row at the join, Grimhollow for the ban and the whitelist and Ruinborne for the whitelist.

**Server.Admin's account listing is a different fact.** `ServerAdmin.ListAccountsAsync` and `GET /accounts`
enumerate `IEnumerableWorldStore` entries, which answer "who has persisted state", not "who may sign in". They
stay as shipped and the Server.Admin README says so. The account registry reaches operators through each
game's console today. The natural engine home for registry reads and the whitelist toggle is a set of
registered actions in `Server.Admin` (`AccountAdminActions`, the `CatalogAdminActions` precedent, adding the
edge `Server.Admin -> Accounts`). Owner question 6.

## 5. Security properties

| Property | Mechanism | Baseline section |
|---|---|---|
| Secret strength | `SigningSecret.Decode` requires standard base64 and at least 32 decoded bytes, throws on a set but invalid value, and never echoes it. `AuthExchange` refuses a shorter key. Local development uses `CreateEphemeral`, never a source constant | 4, player identity |
| Secret custody | The production key stays in the consumer's secret store under the game's own variable. The engine loads and validates it and stores nothing | 4 |
| Constant-time compares | The exchange compares no secret. The verify side is `SignedToken.TryVerify`, which already compares MACs with `FixedTimeEquals`. No bearer secret guards this public endpoint | out of scope list, unchanged |
| Per-client rate limit | Fixed window of 5 per minute, no queue, partitioned per IPv4 address, IPv4-mapped IPv6 folded to IPv4, IPv6 grouped by /64 so one host's address block is one client | new text for category 4 |
| Global bound | Concurrency of 20 plus a 20 deep oldest-first queue, scoped to the exchange endpoint only, so a health probe is never starved | 4 |
| Bounded provider cost | `ProviderTimeout` of 10 s around any validator, so a stalled provider cannot park every permit for 100 s | 4 |
| Body caps | Endpoint metadata of 8 KiB, the optional server-wide Kestrel cap, and the 4096 character credential cap before any provider call | 4 |
| Forwarded headers | Off unless the game names trusted proxies. `TrustedProxyNetworks.PrivateRanges` is RFC 1918 in both address families, `ForwardLimit` stays 1, and the README warns that the preset trusts every host on the private network | per-game responsibilities |
| No account oracle | Every answer naming an account follows a verified credential and describes the caller's own account. The store is find-or-create, so no answer means "no such account". `InvalidCredential` and `Malformed` return before any lookup | 4 |
| Disclosure order | Ban before whitelist. Ban reason and expiry are sent only with `IncludeBanDetails` | 4 |
| One failure envelope | Provider outage, provider deadline, store fault and policy fault all answer 503 `unavailable`, so the response does not say which dependency failed | 4 |
| Token hygiene | Every exchange response carries `Cache-Control: no-store`. No CORS headers are emitted and the token is never a cookie | new text |
| Logging | One line per exchange with the outcome, the provider id, the HTTP status and the elapsed time. Faults log the exception. Never the credential, the session token, the secret, the display name, the subject, email or a connection string. Backends describe a SQLite database by its file, never its connection string | 5, the logging rule |
| Least privilege | `AccountSchemaMode.ValidateOnly` lets the runtime identity run with DML only. The engine backends store no email or other claim | 5 |
| Reserved subjects | A subject with `.` or under `guest:` is never minted | USING, persistence guests |

The status mapping:

| Outcome | HTTP | Body |
|---|---|---|
| `Ok` | 200 | `ok` with token, expiry, subject, display name |
| `NotWhitelisted` | 403 | `not_whitelisted` with subject and display name |
| `Banned` | 403 | `banned` with subject and display name, ban fields only when enabled |
| `InvalidCredential` | 401 | `invalid_credential` |
| `Unavailable` | 503 | `unavailable` |
| `Malformed` | 400 | none |
| body over the cap | 413 | none |
| rate limited | 429 | none, `Retry-After` when the limiter knows it |

The exchange serves HTTP behind a TLS-terminating proxy, which is how both games deploy. A game exposing
Kestrel directly configures TLS itself. `SECURITY-BASELINE.md` category 4 gains the exchange, and the
per-game list gains "name your trusted proxies".

## 6. Migration

Both games adopt on their next repin, after the engine release. Nothing moves in a game repo before then.

### Ruinborne

Deletes `Ruinborne.Auth/AuthExchange.cs`, the hosting, limiter, mapping and fault code in
`Ruinborne.Auth/Program.cs`, `Ruinborne.Core/Auth/HmacTokenSecret.cs`, the three exchange types in
`Ruinborne.Core/Auth/AuthProtocol.cs` (keeping `LocalProfileRequest` and `invalid_profile`), and
`Ruinborne.Persistence/RelationalBanStore.cs`, with the tests that covered them.

Adds `RuinborneAccountStore : IAccountStore`, an adapter over `IRuinborneStore` that maps
`FindOrCreateAccountByLoginAsync` (passing the `email` claim), `GetAccountBySubjectAsync`,
`ListAccountsAsync`, `SetWhitelistedAsync`, `GetActiveBanAsync`, `ListActiveBansAsync`, `AddBanAsync` and
`RemoveBanAsync`, and a policy that ensures the character identity shell and returns
`CharacterPersistenceKey.Format(characterId)`.

Keeps its schema, `acct:<id>` subjects, `AuthReadiness`, `AuthReadinessCache`, `/healthz`,
`LocalProfileIssuer` and `/auth/local-profile` (reusing `AuthAdmission.Decide` and `SignedToken.Mint`),
`LocalDevelopmentMode`, its secret variable and `IncludeBanDetails = true`.

Behaviour changes it accepts: a provider outage answers 503 instead of 401, a provider transport failure
answers 503 instead of 502, the provider call has a 10 s deadline, plain IPv4 proxy addresses are trusted,
IPv6 callers share a /64 bucket, an unban reaches the store before the cache, and banning a subject with no
account row is refused.

Compatibility that must hold: the same secret bytes and the same v3 format, so every outstanding 7 day token
still verifies at the game server. The same route, request and response field names, status strings, and 200,
401, 403 and 503 codes. The client reads the body status, so 502 becoming 503 changes nothing it shows. No
storage change at all.

### Grimhollow

Deletes `Grimhollow.Auth/AuthExchange.cs`, `AuthExchangeEndpoint.cs`, `DiscordHttp.cs`,
`TrustedProxyNetworks.cs` and most of `Program.cs`, `Grimhollow.Shared/GrimhollowTokenSecret.cs` (the
variable name moves beside `GrimhollowAuthDoor`), the exchange types in `Grimhollow.Shared/AuthProtocol.cs`,
the interface and record in `Grimhollow.Persistence/AccountsStore.cs`, `SqliteAccountsStore.cs`,
`SqlServerAccountsStore.cs` and `Grimhollow.Server/Admin/AccountsBanStore.cs`, with the tests that covered
them. `SqliteUnpooled` stays because other Grimhollow stores use it. The null-validator branch in
`Program.cs` goes too, since `GrimhollowDiscordApp.ProductionClientId` is baked in and makes it unreachable.

Keeps `AuthBoot` (the fail-closed rule, now selecting engine backends), `GrimhollowAccountsDatabase`,
`AccountJoinBackstop` (reading `IAccountStore.FindAsync`), `GrimhollowAuthDoor`, `GrimhollowDiscordApp`, its
variables, its audit log and the default policy. The `debug` flag moves to a game-owned narrow store over the
same column, with its own additive `ALTER` (owner question 5). Callers that caught `KeyNotFoundException`
from a write check for null instead.

Compatibility that must hold: the engine backends read and write the existing table in place. That means the
table name `accounts` (`dbo.accounts` on SQL Server), the columns `subject`, `display_name`, `whitelisted`,
`banned`, `ban_reason` and `ban_until` with their current types, subjects of the form `discord:<snowflake>`,
`ban_until` as round-trip UTC text on SQLite and `DATETIMEOFFSET` on SQL Server, a timed ban that stays filed
after it lapses, and an unban that clears the reason and the expiry. The schema ensure is additive only,
guarded by `PRAGMA table_info` and `COL_LENGTH`, and never selects, alters or drops a column it does not own,
so `debug` survives untouched. A fresh table pins a binary collation on `subject` (the `Commerce` and
`Catalog` precedent) and an existing table keeps its collation. Tokens keep the same secret and the v2
format, and the wire is already identical to the engine mapping.

## 7. Test plan and implementation plan

### Tests

All headless. Two new test projects keep push CI's reference-graph selection narrow:
`KhaozEngine.Accounts.Tests` (seam, both backends, ban adapter) and `KhaozEngine.Identity.Exchange.Tests`
(core and handler, with the `Microsoft.AspNetCore.App` framework reference). `SigningSecret` tests join
`KhaozEngine.Server.Tests/Netcode/` beside the `SignedToken` tests. Namespaces stay `KhaozEngine.Tests.*`.

- **Signing secret.** Unset and blank answer null. Non-base64 and 31 bytes throw, and the message names the
  variable and not the value. 32 bytes with surrounding whitespace load. `CreateEphemeral` returns 32 bytes
  that differ per call.
- **Store conformance**, one abstract suite run against in-memory, SQLite and SQL Server. First sign-in
  applies `whitelistOnCreate`. A repeat refreshes the display name and keeps whitelist and ban. Concurrent
  first sign-ins produce one row. Writes on an unknown subject return null and create nothing. Timed ban,
  lapse, unban clearing reason and expiry, `ListBannedAsync` including a lapsed filing, keyset paging in
  ordinal order, and subjects minted as `{provider}:{subject}`. The SQL Server leg is gated on
  `KE_ACCOUNTS_SQLSERVER`, the `KE_CATALOG_SQLSERVER` precedent, because CI has no instance.
- **Grimhollow layout fixtures.** A SQLite file holding the original four-column table and one holding the
  seven-column table with `debug`, both with rows. The engine store reads them, widens only what is missing,
  writes a ban, and leaves `debug` values intact. The same fixture runs against SQL Server under the gate.
- **Ban adapter.** `IsBanned` honours expiry through a fake `TimeProvider`. A store that throws on ban or
  unban leaves the cache unchanged. An unknown subject throws `ArgumentException`. A reload drops a ban that
  was removed out of band.
- **Exchange core**, with a fake validator and the in-memory store. Every outcome, the ban-first order,
  `RequireWhitelist = false`, no store call on `InvalidCredential`, `Unavailable` or `Malformed`, the provider
  deadline, caller cancellation propagating, a throwing store and a throwing policy answering `Unavailable`
  with `Fault` set, a store-minted subject with `.` or `guest:` refused, display name clamping, a short secret
  refused, and the minted token verifying through `HmacTokenAuthenticator` as v2 without a persistence key
  and as v3 with one, expiring at the fake clock plus the lifetime.
- **In-process HTTP.** A real Kestrel on `127.0.0.1:0` (the `AdminHttpServerTests` precedent, so the byte
  cap is exercised and no test host package is added) mounting `MapAuthExchange` beside a `/healthz`. Each
  outcome's status code and JSON body, with the property names asserted against both games' client records.
  A bare 400 for a malformed body, a 413 over the endpoint cap, a 429 with `Retry-After` once the window is
  spent, a blocking validator filling the concurrency bound and queue so the next caller gets 429 while
  `/healthz` still answers, `Cache-Control: no-store` on every exchange response, and a captured log holding
  no credential, token or display name. The partition key and `TrustedProxyNetworks.PrivateRanges` are pure
  functions with their own facts (IPv4, mapped IPv4, IPv6 /64, null peer, both families present).
- **Architecture.** The two edge pins, the new `OptInBackends` and `ThirdPartyHomes` rows, and the
  framework-reference containment fact.

### Steps

Each step is one worktree and one review. Step 1 stages the minor version and opens the `CHANGELOG.md`
entry, and every later step rides that unreleased version, extends the entry, sweeps the docs its names touch
and runs `scripts/pack-local-feed.sh`.

1. `SigningSecret` in `Netcode`, with its tests.
2. `KhaozEngine.Accounts`: seam, records, `InMemoryAccountStore`, `AccountBanStore`, the conformance suite,
   the new test project, architecture rows, the `DEPENDENCY-SEAMS.md` and `README.md` rows.
3. `KhaozEngine.Accounts.Sqlite` on `SqliteStoreConnection`, with conformance and the Grimhollow fixtures.
4. `KhaozEngine.Accounts.SqlServer`: lazy bootstrap, both schema modes, gated conformance and fixtures.
5. The wire DTOs in `Identity` and `KhaozEngine.Identity.Exchange`, with the core tests.
6. `KhaozEngine.Identity.Exchange.AspNetCore` with the in-process HTTP tests, the framework-reference fact,
   and every "only package that references ASP.NET Core" claim corrected (`README.md`, `DEPENDENCY-SEAMS.md`,
   the `Server.Admin` csproj description and README).
7. Release: the rewrite of the USING "Identity / sign-in" section around the engine handler (it shows
   `SessionToken` today, while a game-server door verifies `SignedToken`), `SECURITY-BASELINE.md` category 4
   and the per-game list, the design index row, and linked consumer issues in both game repos.
8. Optional, after both games adopt: `AccountAdminActions` in `Server.Admin`, if owner question 6 says yes.

Not in this program: a shared client for the exchange (both clients keep theirs, now over the engine DTOs),
key rotation, an engine join backstop, and subjects from providers whose `sub` may contain `.`, which the
engine backends refuse until a surrogate-id backend exists.

## 8. Open questions for the owner

1. **Five packages or fewer?** Folding the account seam into the exchange saves one package but makes the game
   server and the admin console take the exchange and `Identity` to reach the ban adapter. Recommend the five.
2. **Engine table layout.** Adopt Grimhollow's `accounts` layout as the engine schema, so Grimhollow migrates
   in place and Ruinborne adapts over its own schema, or define an engine-owned table and migrate Grimhollow's
   rows. Recommend Grimhollow's layout, with the table name configurable.
3. **Where the wire DTOs live.** In `Identity`, client-safe, so both clients share them, or in the exchange
   package with each client keeping a copy. Recommend `Identity`.
4. **`Accounts` in the `Server` umbrella?** Recommend not yet. Revisit when a third game adopts.
5. **Grimhollow's `debug` flag.** A game-owned narrow store over the same column, or a generic attribute bag
   on `AccountRecord`. Recommend the game-owned store, which keeps the engine record free of game fields.
6. **Account registry in `Server.Admin`.** Registered actions for list, get and whitelist, the
   `CatalogAdminActions` shape, as step 8. Recommend yes, after both games adopt, and not in the first release.
7. **Defaults that change behaviour.** `IncludeBanDetails` off and IPv6 grouped by /64. Recommend both as
   stated, with Ruinborne opting in to ban details.
8. **Key rotation.** Rotating either game's secret today invalidates every outstanding token. Recommend a
   separate issue for a verify set with one minting key, where an `ISigningSecretSource` would belong, and
   the plain loader now.
