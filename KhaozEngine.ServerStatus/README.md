# KhaozEngine.ServerStatus

Out-of-band server-status capability for a live game (an MMO first). A small **public HTTP endpoint**,
hosted per game as cloud infra (an Azure Function reading a status DB, **not** part of the game server
process), reports server health and version facts. The game client polls it to drive accurate reconnect
behaviour, forced-update prompts, and a "server is updating, back soon" waiting screen.

This package ships the reusable engine pieces. The cloud infra (the Function, the CI/CD steps that write
deploy facts) lands in the game-template repo, implemented against the wire contract below.

GPU-free. Pure .NET plus `KhaozEngine.Diagnostics` (logging) and `KhaozEngine.Primitives` (the shared
version comparer). No cloud-specific code and no database driver, so both a game client and a headless
server can reference it.

**Frameworks: `net8.0` and `net10.0`.** This package (and its `Diagnostics` + `Primitives` chain)
multi-targets `net8.0` alongside the engine-wide `net10.0` on purpose, so the status endpoint can run as
an Azure Functions isolated-worker app on the **Linux Consumption (Y1) plan**. That plan supports .NET 8
(its newest LTS) but not .NET 10, so a net10.0-only package could only host the endpoint on Windows. The
game client and headless server keep resolving the `net10.0` assets automatically.

## Design authority split

- **CI/CD** writes deploy facts to the status DB on each release (server version, min/latest client
  version, expected downtime window, MOTD).
- **The game server** heartbeats a liveness row on a timer (it already has SQL access) via
  `IServerHeartbeatSink`.
- **The endpoint** derives `health` from the newest heartbeat's age plus the deploy window, and serves the
  `ServerStatusReport`.
- **The client** polls with `ServerStatusClient`, then maps the report to an actionable state with
  `ServerStatusEvaluator`.

## The wire contract

`ServerStatusReport` is the versioned, **tolerant-read** payload the endpoint serves. Tolerant read is
deliberate so the endpoint can evolve ahead of shipped clients: unknown fields are ignored, missing optional
fields fall back to defaults, and an unrecognized `health` token degrades to `unknown` rather than failing
the parse. The game-template Function implements against this exact shape:

```json
{
  "schemaVersion": 1,
  "health": "healthy",
  "serverVersion": "1.4.2",
  "minClientVersion": "1.4.0",
  "latestClientVersion": "1.4.2",
  "lastHeartbeatUtc": "2026-07-14T09:41:12Z",
  "lastDeployUtc": "2026-07-14T09:30:00Z",
  "expectedBackUtc": null,
  "motd": "Double XP weekend is live."
}
```

`health` is one of `healthy`, `restarting`, `down`, `unknown`. During a deploy window the endpoint serves
`"health": "restarting"` with `expectedBackUtc` set to the ETA. Outside a window with a stale heartbeat it
serves `"health": "down"`. `motd` and `expectedBackUtc` are nullable. Version fields are the games' `x.y.z`
scheme (compared numerically, so `0.7.10` is newer than `0.7.9`).

Parse / serialize in code with `ServerStatusReport.TryParse(...)` (never throws, returns null on garbage) and
`report.ToJson()`.

## Client wiring (poller + state)

```csharp
using KhaozEngine.ServerStatus;

var source = new HttpServerStatusSource(new HttpServerStatusSourceOptions
{
    StatusUrl = "https://status.mygame.example.com/status",   // https enforced
});
var client = new ServerStatusClient(source);                   // default 30 s poll interval

// Run the poll loop for the app's lifetime (or call client.PollOnceAsync() from your own tick):
_ = client.RunAsync(appLifetimeToken);

// Each frame / before a reconnect attempt, derive the actionable state:
ServerStatusView view = ServerStatusEvaluator.Evaluate(
    client.Current, BuildConfig.Version, DateTimeOffset.UtcNow);

switch (view.State)
{
    case ServerStatusState.ServerOk:        /* connect / reconnect normally */        break;
    case ServerStatusState.ServerRestarting:/* "back soon" screen, view.ExpectedBackUtc */ break;
    case ServerStatusState.ServerDown:      /* backoff + retry */                     break;
    case ServerStatusState.UpdateRequired:  /* forced-update prompt (below min) */    break;
    case ServerStatusState.UpdateAvailable: /* optional update nudge */               break;
    case ServerStatusState.StatusUnknown:   /* endpoint unreachable / report stale */ break;
}
```

The poller **never throws**: a failed fetch retains the last-known report and advances a staleness/failure
counter (`ServerStatusSnapshot`). The evaluator treats a report older than its `MaxStaleness` window (default
90 s, three poll intervals) as `StatusUnknown` so a brief blip does not flip the UI, but a real outage does.

**State precedence** (first match wins): `StatusUnknown` (no report / too stale / health unknown) ->
`ServerDown` -> `ServerRestarting` -> `UpdateRequired` (healthy but client below min) -> `UpdateAvailable`
(healthy but below latest) -> `ServerOk`. Transient health beats the version gates on purpose: during a
deploy the "back soon" screen wins, and the update gate applies once the server is healthy again. A consumer
that wants a different policy can read the raw report off `view.Report`.

## Status readout (for an in-game status page)

`ServerStatusReadout.Build` is the pure structure for an Esc-menu "server status" page: it turns a snapshot
+ evaluated view into an ordered, stable list of rows, one per fact a page typically shows. GPU-free and
Gui-free (this package has no Gui dependency) - the engine ships the row structure as data, a game supplies
the labels and the widgets.

```csharp
IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(
    client.Current, view, BuildConfig.Version, DateTimeOffset.UtcNow);

foreach (ServerStatusReadoutRow row in rows)
{
    string label = Localize(row.Key);       // map the key to your localized label (row.Key is one of
                                             // ServerStatusReadoutKeys, never string-match by hand)
    DrawStatusRow(label, row.Value);        // row.Value is already a preformatted, ready-to-draw string
}
```

Each row is `(string Key, string Value, object? Raw)`. `Key` is one of the constants in
`ServerStatusReadoutKeys` (`Health`, `ServerVersion`, `MinClientVersion`, `LatestClientVersion`,
`ClientVersion`, `LastHeartbeat`, `LastDeploy`, `ExpectedBack`, `Staleness`, `State`, `Motd`, in that order -
see `ServerStatusReadoutKeys.All`). The row set is stable: a fact with nothing to show (no report ever, or an
optional field left unset) emits an empty `Value` and a null `Raw` instead of dropping the row, so a page can
render a fixed layout and just gray out an empty row.

Duration rows (`LastHeartbeat`, `LastDeploy`, `ExpectedBack`, `Staleness`) are preformatted as compact,
invariant-culture, English strings ("12 s ago", "3 min ago", "2 h ago", "in 5 min") - deliberately not
localized, since this package has no localization catalog dependency and these strings feed a
game-localized page anyway. A game that wants a fully localized duration formats it from the row's `Raw`
value (a `DateTimeOffset?` or `TimeSpan?`, per key) instead of `Value`.

`Build` takes no clock of its own (`nowUtc` is a parameter) and does no IO, so it is fully deterministic:
same inputs always produce the same 11 rows in the same order.

## Server heartbeat wiring

The engine ships only the seam plus a cadence driver. The game owns the SQL. Implement `IServerHeartbeatSink`
against your status DB (a one-row upsert), then drive it:

```csharp
IServerHeartbeatSink sink = new MyStatusDbHeartbeatSink(connectionString); // game-side upsert
var heartbeat = new ServerHeartbeatService(sink, BuildConfig.Version);      // default 15 s interval

// From the server's fixed-tick loop (writes only when an interval has elapsed):
await heartbeat.TickAsync(DateTimeOffset.UtcNow, ct);
// ...or run it on its own background loop: _ = heartbeat.RunAsync(() => DateTimeOffset.UtcNow, ct);
```

`ServerHeartbeat` is the whole row the engine defines: `TimestampUtc` + `ServerVersion`. The expected DB row
the sink upserts (keyed by server/shard id) is `{ serverId, lastHeartbeatUtc, serverVersion }` (column names
and types are the game's). A write failure is contained (logged, surfaced via `ConsecutiveFailures` /
`LastError`, never rethrown into the server loop) and skips at most one beat rather than storming. Use
`NullServerHeartbeatSink` for local runs and `InMemoryServerHeartbeatSink` in tests.

## Idle shutdown (a server head that stops costing money when nobody is playing)

A server billed by the second charges the same for an empty world as a full one, so a game with scheduled or
infrequent sessions spends most of its month serving nobody. `IdleShutdownService` watches a player-count
accessor and asks the host to shut down once the server has been empty continuously for a chosen window.

```csharp
var idle = new IdleShutdownService(
    () => world.ConnectedPlayerCount,           // read fresh every tick
    idleAfter: TimeSpan.FromMinutes(60),
    enabled: !isLocalDevRun);                   // a dev server should not vanish mid-session

idle.IdleShutdownRequested += () => hostLifetime.StopApplication();

// Drive it from the server loop...
if (idle.Tick(DateTimeOffset.UtcNow)) { /* returns true exactly once */ }
// ...or await the built-in loop and exit when it returns:
await idle.RunAsync(ct);
```

It decides WHEN, never HOW: ending the process is the host's business, which is what keeps it headless and
lets a listen server or a test host reuse it. A player arriving clears both the streak and the latch, so the
next empty streak gets a full fresh window. A player-count accessor that THROWS is treated as occupied, never
as empty, because shutting a live server down on a failed read is the one mistake it must not make.

**Exit code 0 is load-bearing on Azure Container Instances.** Billing stops only when the container group
reaches a terminal state. Under the default `restartPolicy: Always` a group never terminates on its own and
bills forever. Under `OnFailure` a clean exit 0 reaches `Succeeded` and the meter stops, while a real crash
still restarts. Pair this with a per-game wake path (something that starts the group again on demand), or all
you have built is a server that becomes unreachable.

## Pieces

- **`ServerStatusReport`** / **`ServerHealth`** - the tolerant-read wire contract + health enum (`TryParse` /
  `ToJson`).
- **`IServerStatusSource`** / **`HttpServerStatusSource`** - the fetch seam and its default HTTPS
  implementation (TLS enforced, response size-capped, never throws).
- **`ServerStatusClient`** / **`ServerStatusSnapshot`** - the never-throwing poller and its degradable
  last-known snapshot.
- **`ServerStatusEvaluator`** / **`ServerStatusState`** / **`ServerStatusView`** - the pure report +
  version + staleness -> actionable state map.
- **`ServerStatusReadout`** / **`ServerStatusReadoutRow`** / **`ServerStatusReadoutKeys`** - the pure,
  ordered row list for an in-game "server status" page (GPU-free, Gui-free).
- **`IServerHeartbeatSink`** / **`ServerHeartbeat`** / **`ServerHeartbeatService`** - the liveness-write seam,
  its row value type, and the cadence driver (+ `Null` / `InMemory` reference sinks).
- **`IdleShutdownService`** - watches a player count and requests a graceful shutdown once the server has been
  empty for a configured window, for server heads billed by the second.
- **`VersionOrder`** - numeric `x.y.z` comparison for the version gates. A thin wrapper over
  `KhaozEngine.Primitives.VersionComparer`, the rule shared with `KhaozEngine.Updates.UpdateVersion` so
  the two packages cannot drift apart.

In the `Foundation` umbrella, so it flows to every game client (`Game2D` / `Game3D`) and headless server
(`Server`) with one reference.
