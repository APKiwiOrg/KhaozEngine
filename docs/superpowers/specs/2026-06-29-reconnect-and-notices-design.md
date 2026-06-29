# Mid-session reconnect + server->client notice channel design

Date: 2026-06-29
Status: approved design, ready for implementation plan
Program: MMO netcode hardening (graceful server-restart survival)

## Context

Ruinborne is a client/server overworld on `WorldClient` (predicted) + `WorldServer`
(authoritative), shipped on the `NetWorld`/`Netcode`/`Replication` stack. Its server runs as an
Azure Container Instance; every deploy recreates the container (no hot swap), so the process
restarts and all UDP connections drop for a few seconds.

Today that drop is silent and unrecoverable client-side. Under client prediction each player keeps
moving locally but stops getting snapshots, so remotes freeze and nothing is authoritative.
`WorldClient` never detects the drop or reconnects: its retry logic is initial-handshake only, and
`WorldClient.Poll` swallows the `Rejected` session event so the reject/disconnect reason is lost.
Ruinborne works around the missing reason with a 12s "not joined" timeout heuristic.

This is generic netcode every game on the engine wants, so it belongs in KhaozEngine.

### What exists today (8.1.0)

- `WorldClient` wraps a `NetClient` wrapping a single injected `INetTransport`. `LocalNetId` /
  `Joined` are the only connection signals; there is no live state and no mid-session "lost the
  server" event.
- `NetClient` sends `Hello(token)` on the transport's `Connected` event behind a one-shot
  `helloSent` latch, and surfaces `Joined`/`Rejected`/`Data`/`Disconnected`. `WorldClient.Poll`
  handles only `Joined`/`Data`/`Disconnected`; it drops `Rejected`.
- `LiteNetLibClientTransport` connects on construction and has no reconnect; once the peer drops,
  it is dead.
- `NetServer.Broadcast(payload, reliability)` exists at the transport layer, but every server->client
  Data frame is funneled straight into `WorldClient.OnSnapshot` (which assumes it is an
  `EncodeSnapshotFrame`), so there is no way to send a non-snapshot message.
- `WorldPersistence.FlushAsync()` already reaches a quiescent, fully-persisted point; it is the
  primitive a graceful drain hangs off.

### Locked decisions (from brainstorming)

1. **Transport factory for reconnect.** `WorldClient` gains a `Func<INetTransport> connect` ctor
   overload. Each layer keeps one responsibility: transports stay dumb single-shot objects,
   `NetClient` stays single-session, and reconnect policy lives in `WorldClient`. The existing
   `INetTransport`-instance ctor stays as the honest "single-shot, no reconnect" path. Additive.
2. **Auto-reconnect default-on when a factory is supplied.** The instance-ctor path is unaffected
   (no factory means a drop is terminal, now observable via state + reason).
3. **Notice payload: typed + opaque escape hatch.** `ServerNotice { Kind, Message, SecondsUntil,
   Payload }` covers the maintenance/restart case first-class, with a `Custom` kind carrying an
   opaque payload for game-specific notices.

## Goals

- A consumer survives a server restart gracefully: detect the drop fast, auto-reconnect resuming the
  same token/identity, re-attach to the existing `WorldClient` (camera, prediction object, consumer
  references all stay valid), and re-sync cleanly to authority with no duplicate or desynced avatar.
- The consumer can render "reconnecting..." from observable state, and can show a server-pushed
  maintenance notice.
- The server can warn players and drain without losing state before a planned restart.
- Headless-testable, transport-agnostic (works over the LiteNetLib transport Ruinborne uses).

## Design

### 1. Connection state machine (`WorldClient`)

```csharp
public enum WorldConnectionState { Connecting, Connected, Reconnecting, Disconnected }
```

- `Connecting`: initial connect in flight (constructed, no first `Joined` yet).
- `Connected`: joined and receiving snapshots (healthy).
- `Reconnecting`: was `Connected`, lost it, now retrying (one or more attempts in flight).
- `Disconnected`: terminal. Reached when there is no factory to rebuild from, on `RejectedToken`,
  on a configured max-attempts cap, or when the consumer disposes/closes the client.

New public surface on `WorldClient`:

- `WorldConnectionState ConnectionState { get; }`
- `event Action<WorldConnectionState>? ConnectionStateChanged;` (fires on each transition)
- `int ReconnectAttempt { get; }` (current attempt number; 0 while `Connecting`/`Connected`)
- `float SecondsUntilNextRetry { get; }` (countdown while waiting on backoff; 0 otherwise)
- `DisconnectReason DisconnectReason { get; }` and `string DisconnectReasonDetail { get; }` (see 3)

`Joined` is retained, now defined as `ConnectionState == Connected`, so existing consumers and tests
that assert `client.Joined` keep working.

### 2. Mid-session disconnect detector

Two triggers, both feed the state machine:

- **Transport drop**: the `ClientSessionEventKind.Disconnected` event (a clean close, or
  LiteNetLib's own peer timeout).
- **Snapshot starvation**: no server frame for `WorldClientConfig.DisconnectTimeoutSeconds`
  (default 3.0). This catches a hard server crash faster than the transport's own timeout.

The starvation timer needs elapsed time, so `Poll` gains a defaulted `dt`:

```csharp
public void Poll(float dt = 0f)
```

Existing `client.Poll()` calls bind to it unchanged. With `dt == 0` the net pump runs but the
health timers (starvation, backoff) do not advance, so tight-loop headless tests that pump without
wall time never spuriously trip a timeout. Reconnect-capable consumers pass real frame dt. The
reconnect work (build transport, new `NetClient`, resync) runs in `Poll` regardless of dt; only the
countdowns are dt-gated.

### 3. Disconnect/reject reason (subsumes the swallowed `Rejected`)

```csharp
public enum DisconnectReason { None, RejectedToken, Unreachable, ServerShutdown, Timeout }
```

- `None`: healthy / never disconnected.
- `RejectedToken`: the server sent `Reject` (bad/expired token). `DisconnectReasonDetail` carries
  the authenticator's reason string. Terminal by default (a bad token will not fix itself);
  `WorldClientConfig.RetryOnReject` (default false) opts into retrying.
- `Unreachable`: transport dropped with no prior shutdown notice (crash / network gone / initial
  connect failed). Retried.
- `ServerShutdown`: transport dropped after a `Shutdown`-kind notice was received (a graceful
  restart). The client remembers the last notice was a shutdown and attributes the subsequent drop.
  Retried (the server is coming back).
- `Timeout`: snapshot starvation tripped while the transport was still nominally up. Retried.

`WorldClient.Poll` now switches on the `Rejected` event (instead of dropping it) to set
`RejectedToken` + detail and move to `Disconnected` (or schedule a retry if `RetryOnReject`).

### 4. Auto-reconnect, backoff, same identity

New ctor overload:

```csharp
public WorldClient(Func<INetTransport> connect, Func<float,float,float> groundHeight, MoveTuning tuning,
    WorldClientConfig? config = null, byte[]? token = null, Func<float,float,Vector3>? groundNormal = null,
    WorldBounds? bounds = null, IPhysicsWorld? physics = null)
```

`connect` is invoked once immediately for the initial connection and again per reconnect attempt.
The existing `WorldClient(INetTransport, ...)` ctor stays (single-shot, no reconnect). `WorldClient`
becomes `IDisposable` to own and dispose the transports it builds (the instance-ctor path does not
own the caller-supplied transport, matching today).

`WorldClientConfig` additions:

```csharp
public bool AutoReconnect { get; init; } = true;          // honored only when a factory is supplied
public float DisconnectTimeoutSeconds { get; init; } = 3f;
public bool RetryOnReject { get; init; } = false;
public ReconnectBackoff Reconnect { get; init; } = ReconnectBackoff.Default;
```

```csharp
public sealed class ReconnectBackoff
{
    public float InitialSeconds { get; init; } = 0.5f;
    public float Multiplier { get; init; } = 2f;
    public float MaxSeconds { get; init; } = 5f;
    public int MaxAttempts { get; init; } = 0;            // 0 = unlimited
    public static ReconnectBackoff Default => new();
}
```

Per reconnect attempt:

1. Dispose the dead transport.
2. `connect()` for a fresh transport; wrap a fresh `NetClient(transport, sameToken)`. The stored
   token is reused for every `Hello`, so the same session/identity resumes. The new `NetClient`
   resets the `helloSent` latch for free.
3. Reset `LocalNetId = -1` and rebuild the replication `world` + `view`, dropping the pre-restart
   ghost remotes (the restarted server reassigns net ids, so stale entities must not linger).
4. Keep the `prediction` object. Between the drop and the first new snapshot the local avatar keeps
   predicting on input (bounded by the reconnect), so it does not freeze.

On the attempt's `Joined` + first post-reconnect snapshot, `OnSnapshot` sees `first` (`LocalNetId < 0`)
and calls `prediction.Reset(basis)` to seed at the authoritative spawn the restarted server provides.
With `WorldPersistence` + an `IWorldStore`, that spawn is the player's saved position (load-on-join),
so the resync is a small reconcile correction (at most one save-interval of movement), never a
duplicate avatar or a map-teleport. The movement contract (prediction + reconcile) is untouched: the
same `prediction` instance simply reseeds and resumes.

State transitions:

- ctor -> `Connecting`. First `Joined` -> `Connected`.
- `Connected` + (transport drop OR starvation): if reconnect is possible -> `Reconnecting` and start
  attempt 1; else -> `Disconnected` with the reason.
- `Reconnecting`: an attempt's `Joined` -> `Connected` (resync, attempt counter resets). An attempt
  failing -> wait the backoff delay, then the next attempt; `MaxAttempts` reached -> `Disconnected`.
- Any state + `Rejected` -> `RejectedToken`; `Disconnected` unless `RetryOnReject`.

### 5. Server->client notice channel

**Frame demux (`MoveProtocol`).** Today every server->client Data frame is assumed to be an
`EncodeSnapshotFrame` (`[localNetId][ack][snapshot]`). Add a 1-byte envelope so snapshots and
notices share the Data channel:

```csharp
public enum ServerFrameKind : byte { Snapshot = 0, Notice = 1 }
public static byte[] EncodeServerFrame(ServerFrameKind kind, ReadOnlySpan<byte> payload);
public static bool TryDecodeServerFrame(ReadOnlySpan<byte> data, out ServerFrameKind kind, out byte[] payload);
```

The snapshot path becomes `EncodeServerFrame(Snapshot, EncodeSnapshotFrame(...))`; the notice path
is `EncodeServerFrame(Notice, EncodeNotice(...))`. `WorldClient` demuxes: `Snapshot` runs the
existing `OnSnapshot`; `Notice` decodes and raises the notice event. This shifts the snapshot wire
format by one leading byte. It is an internal protocol detail (server and client always ship from
the same engine version; a mismatched-version client/server is never a supported config), so it is
not a public-API break, but it is called out prominently in the changelog.

**Type (`NetWorld`).**

```csharp
public enum ServerNoticeKind : byte { Custom = 0, Maintenance = 1, Shutdown = 2 }

public readonly struct ServerNotice
{
    public ServerNoticeKind Kind { get; }
    public string Message { get; }         // human-readable, capped on the wire
    public float? SecondsUntil { get; }     // optional countdown (e.g. "restarting in N s")
    public byte[] Payload { get; }          // opaque, for Kind == Custom; empty otherwise
}
```

Codec is hostile-safe, mirroring the display-name codec: capped message
(`MaxNoticeMessageBytes` = 256) and capped payload (`MaxNoticePayloadBytes` = 512), with
truncate-on-write / clamp-on-read so a corrupt length can neither over-allocate nor desync.

**Server.** `WorldServer.BroadcastNotice(in ServerNotice notice)` encodes and broadcasts over the
existing `NetServer.Broadcast` (reliable-ordered). Parity method on `ShardedWorldServer`.

**Client.** `event Action<ServerNotice>? NoticeReceived;` and `ServerNotice? LastNotice { get; }`.
A `Shutdown`-kind notice is remembered so a following transport drop attributes
`DisconnectReason.ServerShutdown`.

### 6. Graceful drain (deterministic, tick-driven)

The engine avoids an internal wall clock, so the drain is tick-driven:

```csharp
public void BeginDrain(in ServerNotice notice, float graceSeconds);
public bool IsDraining { get; }
public bool IsDrainComplete { get; }
```

`BeginDrain` broadcasts the notice immediately and starts a countdown. Normal `Poll`/`Tick` keep
running during the grace, so players stay live and see the countdown. `Tick(dt)` advances the
countdown; when it elapses, `IsDrainComplete` flips. The host's shutdown pattern (documented):

```text
server.BeginDrain(new ServerNotice(Shutdown, "Restarting", 10f), graceSeconds: 10f);
while (!server.IsDrainComplete) { server.Poll(); server.Tick(dt); persistence.Update(dt); /* sleep dt */ }
await persistence.FlushAsync();   // flush stays the host's call: WorldServer does not own the IWorldStore
transport.Dispose();              // close sockets; clients see the drop attributed to ServerShutdown
```

Persistence flush stays the host's responsibility because the `IWorldStore` is wired through
`WorldPersistence` / `IWorldPersistenceHost`, not owned by `WorldServer`. The same `BeginDrain` +
countdown primitive lands on `ShardedWorldServer` for parity.

## Tests (headless, `KhaozEngine.Tests`)

A restartable in-memory hub (extending or alongside `InMemoryHub`) lets the client's `connect`
factory yield an endpoint to the *current* server, so a "restart" rebuilds the server + hub and the
next factory call attaches the client to the new one.

1. **Reconnect across a restart.** Connect (`Connected`). Tear down server1 + hub, build server2 +
   hub2 over the same `IWorldStore`. Drive `Poll(dt)` past the backoff. Assert the state path
   `Connected -> Reconnecting -> Connected`, that `LocalNetId` is reassigned, that replication
   resumes (the avatar is visible and controllable again), and that the local avatar resyncs near
   its saved position (no map-teleport), all without a manual `WorldClient` rebuild.
2. **Notice delivery.** `server.BroadcastNotice(maintenance)` reaches a connected client:
   `NoticeReceived` fires with the payload and `LastNotice` is set.
3. **Graceful drain.** `server.BeginDrain(shutdown, grace)` broadcasts the notice (client receives
   it); ticking past the grace flips `IsDrainComplete`; disposing the transport then drops the
   client, attributed `ServerShutdown` (because it saw the shutdown notice).
4. **Distinct reasons.** Bad token (authenticator rejects) -> `RejectedToken` + detail; transport
   drop with no notice -> `Unreachable`; notice-then-drop -> `ServerShutdown`; snapshot starvation
   driven by `Poll(dt)` past `DisconnectTimeoutSeconds` -> `Timeout`.

Existing `WorldRoundTripTests` / `Client*`/`Server*` tests keep passing unchanged (the
`Poll()`/instance-ctor paths are untouched).

## Release

`8.2.0` (additive minor): new `WorldClient` factory ctor overload, `IDisposable`, defaulted `Poll`
arg, the connection-state / reason / notice / drain surface, and the new types. The 1-byte server
frame envelope is an internal wire shift, not a public-API break, but is flagged prominently in the
changelog. Full ritual per `CLAUDE.md`: bump `<KhaozEngineVersion>` + `CHANGELOG.md` entry + the 3
guard-checked declarations + doc sweep (`CLAUDE.md` NetWorld map, `docs/USING-KHAOZENGINE.md` usage,
`docs/CONSUMERS.md`) + `dotnet pack -c Release -o ./local-feed` + commit + `git tag v8.2.0`
(push/tag held per the batch policy, confirmed before publish).

## Out of scope (this sub-project)

- Ruinborne adoption: re-pin 8.2.0, a "reconnecting..." overlay from `ConnectionState` /
  `ReconnectAttempt` / `SecondsUntilNextRetry`, displaying `ServerNotice`, and a `deploy-server.yml`
  pre-deploy step that triggers `BeginDrain` (via the game's own admin/SIGTERM signal) and waits the
  grace before recreating the ACI. This is downstream consumer work in the Ruinborne repo.
- Server-side session resume tokens / reconnect to the *same* slot mid-tick: the restarted server is
  a fresh process, so identity resumes via the connect token + persistence load, not an in-RAM
  session handle.
- Lag compensation, snapshot delta history replay across the gap: the client re-seeds from the first
  post-reconnect authoritative snapshot.
