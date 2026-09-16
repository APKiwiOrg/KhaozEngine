# KhaozEngine.Server.Admin

Opt-in HTTPS admin endpoint for a KhaozEngine game server. A minimal Kestrel listener (TLS + a single bearer token)
exposing the generic `ServerAdmin` surface as a small REST API: list/teleport/kick/broadcast online players,
enumerate persisted accounts, ban/unban, and any game-registered admin actions.

This is the only KhaozEngine package that references ASP.NET Core (via a `FrameworkReference`), and it is **not**
bundled in the `KhaozEngine.Server` umbrella - add it explicitly when you want an admin endpoint, so a sim server
that does not need one never pulls the web stack.

```csharp
var admin = new ServerAdmin(worldServer, new WorldStoreBanStore(store), store);
admin.RegisterAction("set-time", payload =>
{
    float t = payload?.GetProperty("timeOfDay").GetSingle() ?? 0f;
    gameClockQueue.Enqueue(t);
    return AdminActionResult.Accepted();
});
await using var endpoint = new AdminHttpServer(admin, new AdminEndpointOptions
{
    Port = 9443,
    BearerToken = "<long-random-secret>",
    Certificate = AdminTlsCertificate.CreateSelfSigned("my-game-admin"),
});
await endpoint.StartAsync();
```

`Port = 0` asks the OS for a free port instead, and `endpoint.BoundPort` reports the one Kestrel took once
`StartAsync` has returned. Prefer that over picking a port from a throwaway probe socket: the probe has to release
the port before Kestrel can bind it, and another listener on the host can take it in that window.

## Pre-auth exposure

The TLS handshake completes before the bearer token is ever read, so an unauthenticated peer can make this endpoint
do RSA work and hold connections open no matter what the token is. Three `AdminEndpointOptions` knobs bound that,
written onto `KestrelServerLimits`, and they default tighter than Kestrel's own because an admin endpoint serves an
operator and a script rather than the public web:

| Option | Default | Kestrel's default | What it bounds |
|---|---|---|---|
| `MaxConcurrentConnections` | 64 | unlimited | How many handshakes an unauthenticated peer can hold at once. |
| `RequestHeadersTimeout` | 10 s | 30 s | Slowloris. Runs before the bearer check, so it is the pre-auth one. |
| `KeepAliveTimeout` | 30 s | 130 s | How long an idle connection keeps occupying a slot. |

Set any of them to `null` to leave Kestrel's own value. A non-positive value throws from the `AdminHttpServer`
constructor rather than later inside Kestrel's start. None of this replaces the two real mitigations: keep the
endpoint on loopback or behind a tunnel, and keep the token long and random.

## Content catalog actions

`CatalogAdminActions.Register(admin, store, registry)` registers the content authoring API as ordinary
actions on this endpoint, so a game console reaches the whole catalog through `GET`/`POST
/admin/actions/{name}` with no second transport, no second listener and no second token. Call it once at
startup, beside your own registrations. Registering twice throws, which is the `ServerAdmin` duplicate-name
rule.

The helper lives here rather than in `KhaozEngine.Catalog.Authoring` because it needs `ServerAdmin` and the
authoring store together. The edge the other way would put the whole netcode stack behind every opt-in SQL
catalog provider, and this package already references `NetWorld`, already owns the dispatch, and is outside
the `KhaozEngine.Server` umbrella, so nothing inherits the cost.

```csharp
var registry = new ContentTypeRegistry();
// ... register your content types ...
IContentAuthoringStore store = new SqliteContentAuthoringStore(path, registry);
await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
CatalogAdminActions.Register(admin, store, registry);
```

Every handler runs on the HTTP request thread and touches only the authoring store, never the simulation, so
the threading contract holds by construction. The read actions:

| Action | Verb | Request | Response |
|---|---|---|---|
| `catalog-schema` | GET | none | `{ generation, types[] }`, each type with its id, key, visibility, chunk slots and its whole field list |
| `catalog-list` | POST | `{ typeKey, version, keyPrefix, includeRetired, skip, take }` | `{ version, total, skip, take, rows[] }` |
| `catalog-get` | POST | `{ typeKey, id }` or `{ typeKey, key }`, plus `includeAudit` | `{ typeKey, id, key, row, history[], audit }` |
| `catalog-draft` | GET | none | `{ draft, edits[] }`, and a null `draft` when none is open |
| `catalog-versions` | GET | none | `{ activeVersion, pinnedVersion, versions[] }` |

`version` 0 means the current live set and the response says which version it read at, so a console never has
to assume which number 0 resolved to. `take` is capped at 500 server side and the response echoes the take it
applied, which is what makes a console page rather than conclude the catalog holds what one page happened to
carry. Every refusal is a 400 carrying a reason that names what was wrong, never a throw the dispatch turns
into a 500.

`catalog-get` returns the row plus its full version HISTORY, which is the temporal model's payoff: "when did
this price change and what was it before" is answered from the row table rather than reconstructed from an
audit. `includeAudit` adds the row's own audit entries, newest first and filtered to that row, and is off by
default because the history is the answer to the usual question.

The seven mutating actions:

| Action | Verb | Request | Response |
|---|---|---|---|
| `catalog-edit` | POST | `{ operator, note, edits[] }` | `{ draft, applied }` |
| `catalog-discard` | POST | `{ operator }` | `{ discarded, editCount }` |
| `catalog-validate` | POST | none | `{ valid, findingCount, findings[], baseVersion, candidateVersion }` |
| `catalog-diff` | POST | `{ from, to }`, where `to` 0 is the draft-applied candidate | `{ from, to, provisionalIds, changes[], chunkSummary[] }` |
| `catalog-publish` | POST | `{ operator, note, expectedBaseVersion, minimumServerBuild, minimumClientBuild }` | `{ version, serverManifestHash, clientManifestHash, chunksWritten, chunksReused, bytesWritten, rulesAppended, elapsedMs }` |
| `catalog-pin` | POST | `{ operator, version }`, or an explicit null version to clear the hold | `{ pinnedVersion, configPinnedVersion, warnings[] }` |
| `catalog-rollback` | POST | `{ operator, toVersion, note }` | `{ draftCreated, editCount, blockedByRules[] }` |

An edit's `op` is `add`, `update`, `retire` or `fork`. A `fork` is an op VALUE rather than an action of its
own, because it is an edit against the open draft like the other three and it is saved, validated, diffed and
published through the same path. Every edit in one request applies in ONE transaction or none of them does,
so a batch save from a grid is atomic, and the edits are checked against the schema AT THE BOUNDARY with the
response carrying EVERY finding rather than the first.

**No edit ever carries a localized text key.** The key is derived from the type key, the row key and the
field name, so a payload naming a marker field is refused with `KEC0004` and the message names the derived
key. A retire names `placeholder` or `replacement`, and a replacement whose `replacementKey` resolves to no
live row is `KEC0017`. A fork names a `forkKey` that is free and a `flagField` the schema declares as `Bool`,
and fails any of those with `KEC0041`.

**`expectedBaseVersion` on a publish is REQUIRED optimistic concurrency.** Two consoles cannot both publish
the same draft: the second one's expectation is stale and it gets a 409 naming BOTH numbers. There is
deliberately no validation override flag anywhere in these actions, because a publish that bypassed the
validator would make boot the only real gate while boot fails closed. The repair path for a validator bug is
an engine patch: export the failing candidate and replay it in a unit test.

`catalog-validate` builds the candidate and runs the full sweep without allocating an id and without writing
anything, so a green validate followed by a red publish can only mean the draft changed in between. Ids on
rows the draft ADDS are provisional there and in a diff against the candidate, which `provisionalIds` says:
the allocator issues the real ones at publish.

`catalog-pin` writes the operator's hold, and a version pinned in the SERVER'S OWN CONFIG wins over it,
always. A pin against such a server is a 200 carrying `configPinnedVersion` and a warning naming it, because
the write happened and takes effect the moment the config pin is removed, while a bare 200 for a call with no
effect on the next restart is the failure that answer exists to prevent. A pin naming a version whose
`minimumServerBuild` exceeds the running build is accepted with a warning, because an operator may be pinning
ahead of an upgrade on purpose and boot is the real gate. Pass a `CatalogAdminActionOptions` to `Register` to
tell the action about both numbers.

A schema field carries `derived`, and that is what makes ONE generic editor possible: a derived field is not
the console's to set, so the cell renders read only without the console having to know which kinds are
derived. The one derived kind today is `LocalizedTextKey`, whose value is the key derived from the type key,
the row key and the field name.

Field values render by kind, the same way in every action:

| Kind | JSON |
|---|---|
| `Int`, `ScaledInt`, `KeyReference` | a number, the STORED integer, so a scaled value is the value times the schema's `scale` |
| `Bool` | `true` or `false` |
| `TagList` | an array of tag ids in authored order, never sorted |
| `OpaqueBytes` | lower hex, the same rendering the audit ledger uses |
| `LocalizedTextKey` | the derived key string, read only |
| absent | `null`, never a sentinel |

## What an action's result maps to

| `AdminActionResult` | Status | Body |
|---|---|---|
| `Ok()` | 200 | none |
| `Ok(payload)` | 200 | the payload as JSON |
| `Accepted()` | 202 | none |
| `BadRequest(error)` | 400 | `{ "error": "<message>" }` |
| `BadRequest(payload)` | 400 | the payload as JSON |
| `Conflict(payload)` | 409 | the payload as JSON |

The string `BadRequest` keeps its exact body, one `error` property and nothing else, so every caller written
against it is unaffected by the object overload beside it. The two object arms are what a handler uses when one
sentence cannot carry the answer: a finding list from a validator that accumulates every finding, or the pair of
version numbers an optimistic caller needs after losing a race. A 409 without them was a 500 with no body, which
reads as a server fault rather than as a race the caller resolves by re-reading and retrying.

Every catalog refusal carries an OBJECT body rather than a bare string, so one console parser reads all of
them. A 400 is `{ error, reason, findingCount, findings[] }`, with `findings` empty when the refusal is about
the REQUEST rather than about the content. A 409 carries `error`, `reason` and whatever the race needs: a
stale publish adds `expectedBaseVersion` and `actualBaseVersion`, a draft a publish is holding adds a
`remedy`, and a blocked rollback adds `code`, `blockedByRules[]` and a `remedy`.

An unknown action name is a 404 from the action lookup. The 501 arms belong to the four built-in routes
(`/accounts`, `/bans`, `/ban`, `/unban`), each gated on a `ServerAdmin` capability flag, so no registered action
ever returns one.

Routes (under `/admin`, all require `Authorization: Bearer <token>`): `GET /online`, `POST /teleport`, `POST /kick`,
`POST /broadcast`, `GET /accounts?prefix=`, `GET /bans`, `POST /ban`, `POST /unban`, `GET /actions` (lists registered
action names), `GET /actions/{name}` (dispatches with a null payload), `POST /actions/{name}` (dispatches with an
optional JSON body, an absent, empty, whitespace-only, or JSON-null body all reaching the handler as null). See
`docs/USING-KHAOZENGINE.md` ("Server administration").
