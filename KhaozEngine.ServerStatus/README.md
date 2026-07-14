# KhaozEngine.ServerStatus

Out-of-band server-status capability for a live game (an MMO first). A small **public HTTP endpoint**,
hosted per game as cloud infra (an Azure Function reading a status DB, **not** part of the game server
process), reports server health and version facts. The game client polls it to drive accurate reconnect
behaviour, forced-update prompts, and a "server is updating, back soon" waiting screen.

This package ships the reusable engine pieces. The cloud infra (the Function, the CI/CD steps that write
deploy facts) lands in the game-template repo, implemented against the wire contract below.

GPU-free. Pure .NET plus `KhaozEngine.Diagnostics` (logging). No cloud-specific code and no database driver,
so both a game client and a headless server can reference it.

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

## Server heartbeat wiring

The engine ships only the seam plus a cadence driver; the game owns the SQL. Implement `IServerHeartbeatSink`
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

## Pieces

- **`ServerStatusReport`** / **`ServerHealth`** - the tolerant-read wire contract + health enum (`TryParse` /
  `ToJson`).
- **`IServerStatusSource`** / **`HttpServerStatusSource`** - the fetch seam and its default HTTPS
  implementation (TLS enforced, response size-capped, never throws).
- **`ServerStatusClient`** / **`ServerStatusSnapshot`** - the never-throwing poller and its degradable
  last-known snapshot.
- **`ServerStatusEvaluator`** / **`ServerStatusState`** / **`ServerStatusView`** - the pure report +
  version + staleness -> actionable state map.
- **`IServerHeartbeatSink`** / **`ServerHeartbeat`** / **`ServerHeartbeatService`** - the liveness-write seam,
  its row value type, and the cadence driver (+ `Null` / `InMemory` reference sinks).
- **`VersionOrder`** - numeric `x.y.z` comparison for the version gates.

In the `Foundation` umbrella, so it flows to every game client (`Game2D` / `Game3D`) and headless server
(`Server`) with one reference.
