# Player persistence key resolver design

Status: approved for implementation under [#856](https://github.com/APKiwiOrg/KhaozEngine/issues/856).
Ruinborne [#467](https://github.com/APKiwiOrg/Ruinborne/issues/467) is the pinned and waiting consumer.

## Problem

The server correctly authenticates and deduplicates live sessions by the verified account subject. Checkpoint
persistence currently reuses that subject as its durable record key. Ruinborne keeps account identity and character
identity separate even while it allows exactly one character per account. Its movement checkpoint must therefore
be `player:character:<character-id>`, while bans, whitelist policy, duplicate-session policy, admin identity, and
the signed-token subject remain `acct:<account-id>`.

`StatePersistence<TState>` cannot resolve the key only after `PlayerJoined`. Both NetWorld heads consult resume
hints before the entity exists. A resolver that runs after spawn would restore the correct record eventually but
would seed the first snapshot from the account key, reopening the reconnect teleport defect.

Ruinborne also cannot perform a database lookup in a synchronous pre-spawn callback. The character identity must
arrive as a server-verified claim without changing the authenticated subject.

## Goals

- Keep authentication, duplicate-session admission, bans, online-player identity, and admin identity keyed by the
  verified subject.
- Carry an optional signed persistence-key claim beside that subject.
- Resolve one persistence key after authentication and before `JoinSpawn`.
- Bind the resolved key to the slot for the whole session. Never call the resolver again for save, leave, hint,
  quarantine, or game-state capture.
- Use the persistence key for every durable checkpoint concern while retaining the account subject for recycled
  seat checks and diagnostics.
- Preserve current behavior for every consumer that configures no resolver and every v1 or v2 signed token.
- Refuse two concurrently live authenticated subjects resolving to one persistence key.
- Preserve tokenless guest behavior. The resolver and signed claim do not make guests persistent.

## Non-goals

- Character rosters, character selection, account linking, or a second identity provider.
- Moving journal ownership or progression back into checkpoint persistence.
- An asynchronous database lookup in the NetWorld join path.
- Changing the default `player:{authenticated-subject}` key.
- Reinterpreting `DuplicateSessionPolicy`. It remains subject keyed.

## Signed persistence-key claim

`SignedToken` gains a v3 wire format:

```text
v3.<subject>.<display-name-b64>.<persistence-key-b64>.<expiry-unix>.<mac>
```

The signature covers every field before the MAC. Existing mint and verify overloads remain source compatible.
Existing v1 and v2 tokens remain valid. A new mint overload accepts subject, display name, persistence key, expiry,
and secret. A new verify overload surfaces all three verified string values. Existing verify overloads discard the
claim. `TryParseUnverified` accepts v3 so an updated client can still perform its structural prefilter, but the
unverified persistence key is never used for authorization or storage.

`IConnectionPersistenceKey` is an optional companion to `IConnectionAuthenticator`, matching
`IConnectionDisplayName`. `HmacTokenAuthenticator` implements it by re-verifying the accepted token and returning
the signed claim. `NetServer` reads it only after successful authentication and puts it on `ServerSessionEvent`.
An authenticator that does not implement the companion produces an empty claim.

## Resolver and session binding

`KhaozEngine.WorldStore` owns the provider-neutral types:

```csharp
public readonly record struct PersistenceKeyRequest(
    int Slot,
    string AuthenticatedAccountId,
    string VerifiedPersistenceKey);

public delegate string PersistenceKeyResolver(in PersistenceKeyRequest request);
```

`PersistenceCoreConfig`, `WorldPersistenceConfig`, and `TileWorldPersistenceConfig` expose a nullable resolver.
`StatePersistence<TState>` installs it on the host before it installs the resume-hint provider or subscribes to
join events. A host that cannot accept a non-null resolver returns false and causes construction to fail.

`IPersistenceHost<TState>` gains backward-compatible default members:

```csharp
bool TrySetPersistenceKeyResolver(PersistenceKeyResolver? resolver) => resolver is null;

bool TryGetPersistenceKey(int slot, out string persistenceKey)
    => TryGetAccountId(slot, out persistenceKey);
```

`WorldServer` and `ShardedWorldServer` install at most one resolver. On an authenticated join they call it once
with the verified subject and optional verified claim, validate the result, reject a live-key collision, retain the
result, and pass it to `JoinSpawn`. They write `accountIdBySlot` separately and keep its behavior unchanged. The
persistence key remains available while `PlayerLeaving` runs and is removed immediately afterward.

When no resolver is installed, both heads use the authenticated account ID exactly as before. For a tokenless
join they do not call the resolver. Existing guest policy remains owned by `StatePersistence<TState>`.

## State persistence identity split

Every durable record, ordering guard, load token, save baseline, hint, prewarm key, quarantine key, and game-state
capture uses the session-bound persistence key. A pending load also carries the authenticated account ID. Apply
requires the same session token, current persistence key, and current authenticated account ID. This prevents a
recycled slot or key collision from applying another session's record.

`PlayerPersistenceContext.AccountId` keeps its current effective meaning as the durable key for source
compatibility. It gains `AuthenticatedAccountId` and a three-argument constructor. New code uses the explicit
property names. Existing two-argument construction keeps both values equal.

Diagnostics name both identities when they differ. Existing quarantine and dropped-load events keep exposing the
durable key because that is the key operators use to locate the record.

## Key validation and collision policy

Resolver output must be non-empty, must fit the existing store-key contract once prefixed, and must not use the
reserved guest prefix for an authenticated session. A resolver exception or invalid result refuses the join before
entity spawn.

Two live authenticated sessions may have different subjects but resolve to one character. The engine keeps a
reverse persistence-key map and refuses the newer join. It does not kick the existing character. This collision
gate is separate from `DuplicateSessionPolicy`, which continues to govern one subject on two connections.

## Ruinborne adoption

Ruinborne.Auth ensures the whitelisted account's single character identity shell before minting the session token.
It mints a v3 token whose subject remains `acct:<account-id>` and whose persistence-key claim is
`character:<character-id>`. The identity-shell procedure stays unable to update journal-owned character fields.

Ruinborne.Server configures the resolver to require the verified `character:` claim. It verifies that the numeric
character belongs to the authenticated account before journal admission. `WorldPersistence` then loads, hints, and
saves `player:character:<character-id>`.

The existing movement rekey cutover runs while the maintenance and auto-wake fence is active. It copies every
`player:acct:<account-id>` checkpoint to `player:character:<character-id>`, checks exact counts and hashes, retires
the source rows, then opens only the named-staff canary. No game-local persistence implementation is added.

## Testing

- v1 and v2 token compatibility, v3 round trip, claim tamper rejection, malformed base64 rejection, and old
  overload behavior.
- NetServer surfaces only a verified claim and keeps subject deduplication unchanged.
- Single and sharded heads resolve before spawn, call once, retain through save and leave, isolate sequential keys,
  reject collisions, and skip the resolver for guests.
- `StatePersistence<TState>` uses the bound key for load, periodic save, leave save, prewarm, hints, quarantine,
  game-state contexts, and in-flight guards while checking authenticated account identity on apply.
- TileWorld passes through the core resolver seam without changing its default behavior.
- Full Release tests, documentation guards, file-size guard, local package pack, and the waiting Ruinborne consumer
  build against the packed release.

## Release

The change rides staged 18.36.0 unless concurrent work claims that version. At finish the branch merges current
main first, takes the next free staged version if needed, updates all guarded documentation, packs to local-feed,
merges and pushes main, and tags immediately because Ruinborne is pinned and waiting.
