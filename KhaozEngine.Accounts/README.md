# KhaozEngine.Accounts

The account registry behind sign-in: who has signed in, whether each account is past the whitelist gate, and the
bans filed against them. One store is the single source of truth for accounts AND bans, so the sign-in exchange,
the connect door, a join check and an admin console all read the same row.

Opt-in, in NO umbrella. It references `KhaozEngine.Netcode` and nothing else, for the `IBanStore` seam its ban
adapter implements, so a game server or an admin console takes it without the exchange or `Identity`. No SQL, no
HTTP and no third-party dependency. Durable backends are opt-in sibling packages.

## Public API

| Type | What it does |
|---|---|
| `IAccountStore` | `FindOrCreateAsync(AccountSignIn)`, `FindAsync(subject)`, `ListAsync(afterSubject, limit)` (keyset pages in ordinal order), `ListBannedAsync()` (every FILED ban, lapsed included), and the writes `SetWhitelistedAsync`, `BanAsync`, `UnbanAsync`. |
| `AccountRecord` | `Subject`, `DisplayName` (null when the provider never gave one), `Whitelisted`, `Ban` (the filed ban or null), and `IsBanActive(now)`. |
| `AccountBan` | `Reason`, `Until` (null is permanent, UTC in the engine stores), and `IsActive(now)`. |
| `AccountSignIn` | What a VERIFIED sign-in hands the store: `ProviderId`, `ProviderSubject`, `DisplayName`, `Claims`, `At`. |
| `AccountStoreRules` | The rules every engine store applies alike: `MintSubject`, `ValidateBanReason`, `IsAdmissibleSubject`, the reserved `guest:` prefix and the length limits. |
| `InMemoryAccountStore` | The dependency-free reference store for tests, tools and a local development host. |
| `AccountBanStore` | The ONE `IBanStore` over an account store: a cached `IsBanned` for the host thread, writes to the store first, and `LoadAsync` to reload. |

## The store contract

- **Find-or-create is the only way a row comes to exist.** A first sign-in creates the account, whitelisted when
  the store was built with `whitelistOnCreate: true`. The flag is create-only: a repeat keeps whatever the account
  was set to. A repeat also keeps any ban, and refreshes the stored display name when the sign-in carries one (a
  null name keeps the stored name). Concurrent first sign-ins for one subject produce one account.
- **Writes never create.** `SetWhitelistedAsync`, `BanAsync` and `UnbanAsync` name an existing account and return
  it as it stands afterwards, or `null` for an unknown subject. A whitelist or ban filed against a subject nobody
  signed in as would be a typo becoming policy.
- **A timed ban stays filed after it lapses.** `AccountRecord.Ban` is the filed ban and `IsBanActive(now)` is the
  gate, so a lapsed ban admits the player with no unban and the operator still sees the history. `UnbanAsync`
  clears the reason and the expiry. `BanAsync` on a banned account replaces both.
- **Subjects compare by code point.** Two subjects differing only in case are two accounts, and listings run in
  ordinal order. Page with `ListAsync(afterSubject: lastPage[^1].Subject)` until a page comes back empty.
- **Refusals are `ArgumentException`**, thrown before anything is written, and their messages never echo the
  value.

`whitelistOnCreate` is a required constructor argument on every engine store, with no default, because it is the
highest-consequence value in an auth composition. Decide it where the store is built.

## Subjects and limits (`AccountStoreRules`)

The engine stores mint `{ProviderId}:{ProviderSubject}`, for example `discord:80351110224678912`. They refuse, with
`ArgumentException`:

- a provider id that is blank or carries `:` or `.`, and a provider subject that is blank or carries `.` (a
  `SignedToken` splits its fields on `.`),
- a subject that would fall under the reserved `guest:` prefix, which names a tokenless connection's seat,
- a subject over 128 characters, a display name over 128 and a ban reason over 256 (UTF-16 code units, the engine
  table's column widths).

A game's own `IAccountStore` over its own schema may mint differently (a provider-neutral `acct:<id>` from an
identity column, say). Whatever it mints must still satisfy `AccountStoreRules.IsAdmissibleSubject`: non-empty, no
`.`, and not under `guest:`.

## One ban list: `AccountBanStore`

```csharp
var accounts = new InMemoryAccountStore(whitelistOnCreate: false);   // or a durable backend
var bans = new AccountBanStore(accounts, TimeProvider.System);
await bans.LoadAsync();                                             // at boot, and on a timer if others write SQL

var server = new WorldServer(transport, config, terrain.SampleHeight, MoveTuning.Default,
    authenticator: new BanGateAuthenticator(tokenAuth, bans),       // refused at the door with ke:banned
    banStore: bans);                                                 // checked again at the join
var admin = new ServerAdmin(server, bans);                          // POST /ban writes the account row
```

- `IsBanned` answers from an in-memory map of the filed bans and re-checks the expiry against the clock on every
  call, so a timed ban lapses on its own.
- `BanAsync` and `UnbanAsync` write the store FIRST and the map second. A store write that throws leaves the map
  unchanged. A subject no account has throws `ArgumentException`, which `Server.Admin` renders as a 400.
- `LoadAsync` reads `ListBannedAsync` into a new map and swaps it in whole, so it also drops a ban lifted out of
  band. A failed reload throws and keeps the previous map. Writes and reloads run one at a time, so a reload never
  overwrites a ban written while it was reading.
- `ListBans` returns the bans in force now, in ordinal order.

Hand the one instance to every ban consumer: the door, `banStore:` on a float head, `TileWorldServerConfig.BanStore`
on a tile head, and `ServerAdmin(bans:)`. Do not also wire `WorldStoreBanStore`, whose `ban:{accountId}` keys would be
a second list.

## Backends and conformance

`InMemoryAccountStore` ships here. Every engine backend runs the shared `AccountStoreConformance` suite in
`KhaozEngine.Accounts.Tests` unchanged, one subclass per backend, with the in-memory store as the reference.
