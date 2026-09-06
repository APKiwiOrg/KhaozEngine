# KhaozEngine.Netcode

Game-agnostic, transport-free netcode primitives.

## In-memory transports

`LoopbackTransport.CreatePair()` provides one deterministic two-endpoint link. `InMemoryTransportHub` provides one
server endpoint and any number of isolated client endpoints for headless multi-client tests and single-process local
hosts. `CreateClient` assigns stable positive server-side connection ids. Data is copied and reaches the target on
its next `Poll`, with no sockets, threads, or wall clock.

Plain disconnects preserve data already sent and then surface one terminal event on both sides. A disconnect with a
reason supersedes only that connection's unpolled frames, matching the rejection contract. Disconnect is idempotent,
traffic after it is ignored, disposing a client releases its server connection, and disposing the hub or server
disconnects every remaining client and prevents new connections.

## UnitAxisQuantizer

8-bit quantization of a unit-range axis (`[-1,1]`) to a signed byte and back, rounding away-from-zero.

```csharp
sbyte qx = UnitAxisQuantizer.Quantize(moveDir.X);   // -127..127
float x  = UnitAxisQuantizer.Dequantize(qx);        // ~moveDir.X, within 1/127
```

The game keeps its own command record and decides which fields to pack; this just does the per-axis math.

> Determinism: if you dequantize commands before they enter a host-authoritative deterministic sim, this
> rounding is part of your sim hash. The scheme is fixed (round away-from-zero, `*127`) for that reason.

## ClientPrediction&lt;TState, TCommand&gt;

Client-side prediction with authoritative reconciliation. You supply the state shape and the per-tick
physics; the engine owns the pending-command buffer, ack-prune, rebase + replay, and the decaying
render-offset smoothing (hard-snap + dead-zone).

```csharp
readonly record struct ShipState(Vector2 Position, Vector2 Velocity) : IPredictedState<ShipState>
{
    public ShipState WithPosition(Vector2 p) => this with { Position = p };
}

sealed class ShipSim : ITickSimulator<ShipState, MyCommand>
{
    public ShipState Step(in ShipState s, in MyCommand c, float dt) => /* integrate */;
}

var prediction = new ClientPrediction<ShipState, MyCommand>(new ShipSim());      // PredictionSettings.Default
prediction.Reset(initialState);

int seq = prediction.Predict(command);                                          // local tick, send seq with command
var result = prediction.Reconcile(tick, authoritativeBasis, lastAckedSeq);      // on snapshot
prediction.AdvancePresentation(elapsedSeconds);                                 // per render frame
Draw(prediction.RenderedState);
```

Tune via `PredictionSettings` (tick rate, buffer cap, hard-snap distance, correction rate, dead-zone).

`AdvancePresentation` refuses a frame time that is not a finite positive number of seconds (negative, zero,
infinite, or not a number): it is treated as zero and advances nothing. The inter-tick clock accumulates, so one
bad frame would otherwise make every `RenderedState` after it NaN for the rest of the session.

`Reconcile` is **C1-continuous** (since 9.23.0): a non-hard-snap rebase does NOT collapse the in-flight inter-tick
interpolation onto the new basis. It folds only the genuine misprediction into the decaying render offset, so a
matching (loopback) rebase - fired every tick - perturbs neither the rendered position nor its velocity. The
pre-9.23.0 collapse pinned the inter-tick contribution at zero each tick, leaving only the offset decay to carry
motion for the rest of the tick: a per-tick velocity dip that read as a 30 Hz camera sawtooth. A hard snap still
collapses (an intentional teleport).

Since 10.7.0 the C1 rebase **translates the whole inter-tick segment** (`previous -> predicted`) by the rebase
delta, so its VELOCITY is preserved rather than just leaving `previous` pinned. This makes it C1 across ANY rebase,
not only a steady/matching one (whose delta is zero, so behaviour there is unchanged). It fixes the decel-to-stop
shake: when the local player stops, the authority is an input-RTT behind and its basis dips backward for a tick or
two before catching up; pinning `previous` let the inter-tick lerp drag the render backward toward the dipped target.
Translating the segment gives it zero velocity for a stopped player, so the transient dip lives entirely in the
render offset. That offset now decays with a **critically-damped** (velocity-carrying) filter on **both axes**
(planar since 10.7.0, vertical since 10.33.0), whose inertia holds the render steady through the transient instead of
chasing it, turning a sharp reversal into a sub-dead-zone sag. The vertical axis took the same filter because the
surface-swim buoyancy spring emits a continuous stream of small vertical corrections that a first-order decay chased
into a fast, jerky camera bob.

`PredictedHorizontalSpeed` is the local player's planar (ground-plane) speed in units/sec, recomputed each
`Predict` from the per-tick position delta over `TickSeconds` (`IPredictedState.Position` is planar, so it is
horizontal for free). It is computed **only** on the commanded `Predict` path, never in `Reconcile`, so a
reconciliation rebase/snap never registers as movement - a clean steady value for a speed HUD, footstep audio,
or a locomotion blend, unlike differencing `RenderedState.Position` (which carries the decaying render offset
and wobbles under lag). Zero until the first `Predict`; zeroed by `Reset` / `Reseed`.

On a mid-session **reconnect**, call `Reseed(basis)` (not `Reset`) when the first post-reconnect snapshot lands.
It re-seeds the predicted state to the authoritative basis but keeps the command sequence counter **monotonic**:
the fresh server has already advanced its per-connection ack from the commands sent in the join gap, so a `Reset`
back to seq 0 would make every subsequent command land at or below that ack and be rejected as stale (the player
freezes). `Reset` is only for the genuine initial connect. `Reconcile` then prunes acked / replays unacked from
the retained pending buffer.

Since 10.65.0 a **teleport epoch** makes an intentional teleport cut regardless of distance. `IPredictedState<T>`
carries an optional monotonic `TeleportEpoch` (a default-interface member returning 0, so a state with no teleport
concept is unchanged). When the epoch on the authoritative basis ADVANCES, `Reconcile` force-hard-snaps (bypassing
the `HardSnapDistance` gate, so a short in-session teleport cuts instead of gliding) and returns
`ReconciliationResult.Teleported = true`. The host is expected to advance the epoch ONLY at real teleport sites, and
normal movement leaves it unchanged.

Advance means strictly greater than the highest epoch seen so far, and the client holds that highest value as a
watermark. Any inequality is not enough: the epoch reads 0 whenever the host serves a state whose movement component
is momentarily unreadable, so a real stream dips and recovers, and treating either edge as a teleport fired the cut on
ordinary snapshots (#409).

`Teleported` means **the local player's world position changed discontinuously**, and nothing else sets it:

| What happened | `Teleported` |
| --- | --- |
| First reconcile after `Reset` (a first-ever join) | yes, always: there is no prior position to be continuous with |
| First reconcile after `Reseed` (a reconnect), resume position within `HardSnapDistance` | **no** |
| First reconcile after `Reseed`, resume position at or beyond `HardSnapDistance` | yes: the player moved while away |
| An authoritative epoch advance (respawn, admin move, fast travel) | yes |
| An ordinary smoothed correction | no |

The reconnect row is the one that bites. A reconnect rebuilds everything about the session, and it used to report a
teleport unconditionally, so a consumer honouring the contract paid its full teleport reaction on every drop and a
client on a lossy link paid it repeatedly. `Reseed` measures the resume displacement instead (3D, in absolute space so
an island re-anchor across the reconnect counts as zero) and stays quiet when the player did not actually move. The
epoch cannot decide this: a rejoining client is a fresh authoritative entity whose epoch counts from its own zero, so
it bears no relation to the one the previous session ended on. Tighten `HardSnapDistance` if a consumer wants a
shorter leash on what counts as "moved".

The same verdict decides whether the avatar cuts or glides, because the consumer's camera warp hangs off the signal.
A reported teleport cuts: `Reseed` drops the render offsets, so the avatar is on the resume position the frame the
seed lands and the warp meets it there. A quiet resume glides: the sub-threshold displacement is re-anchored into the
decaying render offset (in absolute space, so a resume arriving in a different island frame glides nothing) and
decays away like an ordinary correction, because nothing warps the camera on that path and an instant cut would leave
the avatar ahead of it.

What the client measures is the resume snapshot, so the SERVER decides whether a reconnect is quiet. A server that
spawns the rejoiner and restores their stored position afterwards hands the client the spawn first, which is a
teleport for anyone standing further than `HardSnapDistance` from it, and then a second one when the restore advances
the epoch. `KhaozEngine.NetWorld`'s `WorldServer` plus `WorldPersistence` are that shape today
([#642](https://github.com/APKiwiOrg/KhaozEngine/issues/642)).

A **frame anchor** makes a floating-origin shift invisible instead of catastrophic. `IPredictedState<T>` carries an
optional `FrameAnchor` (a default-interface member returning `Vector2.Zero`, so a state with no frame concept is
unchanged) naming the planar space its `Position` is expressed against, plus a `WithFrameAnchor(anchor, position)`
wither whose default THROWS. At the very top of `Reconcile`, above every capture, the carried presentation state is
converted into the incoming basis's frame. Without that, a simulation island re-anchoring - which moves the world
by an exact multiple of the frame grid and is a no-op in world space - would measure as a whole anchor delta of
prediction error, trip the `HardSnapDistance` gate, and then glide the avatar a frame-width across the screen while
the render offset decayed. `renderOffset` and the vertical axis are untouched: an offset is a delta and is
frame-invariant, and Y is never framed.

The throwing default is the honest shape for the wither. It has to construct a `TSelf` and nothing else on the
interface can carry a new anchor, so no default body could be correct. Making it abstract would break every
existing implementer, which is what the default-member pattern exists to avoid. It is unreachable unless the two
anchors actually differ, which is impossible for a state that left `FrameAnchor` at its default, so anything that
reaches it opted into frames on one side and not the other - and should say so loudly rather than silently drop the
conversion.

## RemoteCommandQueue&lt;TCommand&gt;

Host-side per-slot, seq-ordered command queue. Dedups retransmits and negative seqs, returns a neutral
command for an empty slot, and tracks the last acknowledged seq per slot to stamp on snapshots. As anti-replay
it also rejects any seq at or below a slot's processed high-water mark, so a slot's state must be cleared when
that slot is released for reuse: `Forget(slot)` drops the slot's buffered commands and high-water mark
(idempotent), letting the next session that recycles the slot restart its seqs from 0. The authoritative servers
(`WorldServer`/`ShardedWorldServer`) call this on disconnect; without it a recycled slot freezes the new player.

Optional backlog catch-up: pass `catchUpThreshold` (default 0 = off) and a slot whose buffered backlog grows
deeper than it collapses to its newest command on the next `Dequeue` (the high-water jumps past everything
skipped), so the host stays at most that many commands behind live instead of crawling a deep backlog one per
tick (e.g. a reconnect flush or a delivery burst replaying stale input). It is **lossy** (skipped commands are
discarded), so only enable it for a latest-wins stream such as movement; the authoritative servers wire it via
`WorldServerConfig.MaxInputBacklog`.

```csharp
var queue = new RemoteCommandQueue<MyCommand>(neutralCommand: MyCommand.Idle);
queue.Store(slot, seq, command);                       // on receive
var cmd = queue.Dequeue(slot, out int lastAckedSeq);   // once per sim tick
queue.Forget(slot);                                    // on disconnect, before the slot is recycled

// latest-wins streams can cap how far behind live the host falls under a backlog:
var moves = new RemoteCommandQueue<MyCommand>(neutralCommand: MyCommand.Idle, catchUpThreshold: 8);
```

## BoundedEventQueue

The defensive hard cap behind the `NetServer` session inbox and the LiteNetLib transport inboxes. FIFO, and at
capacity a new item evicts the oldest (drop-oldest, keep-newest) so a host that stalls or is flooded cannot grow
undrained events without bound. `DroppedCount` counts the evictions, and stays 0 for a host that drains to empty
each tick as contracted.

`EnqueueTerminal` posts an item the cap does not apply to and an eviction will never drop. Use it for events
whose loss corrupts state rather than merely losing traffic, because nothing re-announces them: a peer's
Disconnected is what releases its player slot, and a `Left` is what frees the host's per-player state. Dropping
one of those under a flood leaked that state permanently, one seat at a time, until real players were refused
against a server that was not full. Terminal items are rare and self-limiting (at most one per connected peer,
and no payload buffer), so buffering all of them is far cheaper than losing any, and `Count` may exceed
`Capacity` by however many are currently buffered. Drain order is untouched: a terminal item still comes out
between the ordinary items it arrived between.

```csharp
var inbox = new BoundedEventQueue<NetEvent>(maxQueuedEvents);
inbox.Enqueue(NetEvent.FromData(id, payload, reliability)); // capped, evictable
inbox.EnqueueTerminal(NetEvent.Disconnected(id));           // exempt, never dropped
while (inbox.TryDequeue(out NetEvent ev)) { /* handle */ }  // drain to empty every tick
```

## IChannelSplittable&lt;TSelf&gt; + NetChannelReliability

The transport-free channel-split contract. A batch DTO declares its unreliable (position/transient,
latest-wins) vs reliable (spawns/destroys/events, must-arrive-ordered) content and extracts each
sub-batch. Because it names no transport type, a DTO that lives in a transport-agnostic project (e.g.
one shared with a web server) can implement it without referencing any UDP library.

> These two types now physically live in the zero-dependency **`KhaozEngine.Netcode.Abstractions`**
> package (BCL only, no MonoGame). A MonoGame-free DTO project should reference **only** that package.
> `KhaozEngine.Netcode` type-forwards both, so referencing `KhaozEngine.Netcode` still binds them with
> no source change. The namespace stays `KhaozEngine.Netcode` either way.

```csharp
readonly record struct EntityBatch(/* ...fields... */) : IChannelSplittable<EntityBatch>
{
    public bool HasUnreliableContent => /* any position/transient field set */;
    public bool HasReliableContent   => /* any event field set */;
    public EntityBatch ExtractUnreliable() => /* copy with event fields nulled */;
    public EntityBatch ExtractReliable()   => /* copy with position fields nulled */;
}
```

`NetChannelReliability` (`UnreliableSequenced` / `ReliableOrdered`) names the two channels. The
LiteNetLib `DeliveryMethod` mapping and the `ChannelSplitter.Send` orchestration live in the
**`KhaozEngine.Netcode.LiteNetLib`** package, so adding the split only pulls in a UDP transport on the
sending side, not in the DTO project.

## NetTransportStats + INetTransport.Stats (since 8.2.0)

`INetTransport` carries an optional `Stats` member implemented as a **default interface method** returning
`NetTransportStats.Unavailable`, so the in-memory `LoopbackTransport` and any external transport keep compiling
unchanged (not a breaking change). `NetTransportStats` is a transport-agnostic snapshot (`Connected`, `RttMs`,
`PacketLoss` 0..1, cumulative `BytesReceivedTotal` / `BytesSentTotal`). `NetClient.TransportStats` forwards it;
the `KhaozEngine.Netcode.LiteNetLib` client binding sets `NetManager.EnableStatistics` and fills it from the
server peer. Games read it (with the snapshot rate + prediction-correction magnitude) via
`KhaozEngine.NetWorld`'s `WorldClient.NetStats`.

## Session send path: frame once, borrow the span

`SessionFrame` carries two `Write` overloads. The allocating one returns a fresh `byte[]` and is right for the
handshake frames, which happen once per session. The other writes into a caller-supplied `Span<byte>` and returns
the bytes written (`SessionFrame.FrameLength(bodyLength)` sizes it, and a short destination throws
`ArgumentException` rather than truncating a frame onto the wire).

`NetServer.SendTo` and `NetServer.Broadcast` use the second one against a buffer the server keeps and grows to the
largest payload it has seen, so a fixed-rate authoritative host settles on one allocation rather than a frame array
per send, and a broadcast frames ONCE and hands the same span to every peer.

What makes reusing that buffer legitimate is a contract on the seam: `INetTransport.Send` BORROWS its payload for
the duration of the call and no longer. An implementation that needs the bytes afterwards copies them - the
loopback stages a copy, and the LiteNetLib binding passes the span to `NetPeer.Send`, which copies into its own
packet before returning (it used to call `payload.ToArray()` first, so a broadcast to N players made N copies of
identical bytes on top of the frame). A third-party transport must honour the same rule and never stash the span.

## Reject delivery: the reason rides the disconnect

`NetServer` refuses a pending peer by sending a reliable `Reject` frame AND carrying the same framed reject on
the disconnect via `INetTransport.Disconnect(NetConnectionId, ReadOnlySpan<byte> reason)` (a **default interface
method** that drops the reason and does a plain disconnect, so an external transport with nowhere to put one keeps
compiling unchanged). Over a real socket the immediate teardown can outrun the reliable flush, so the reason on the
disconnect - delivered as part of the shutdown handshake (LiteNetLib `NetPeer.Disconnect(byte[])` ->
`DisconnectInfo.AdditionalData`, surfaced on the `NetEvent` `Disconnected` payload) - is what makes the reject reach
the client. `NetClient` turns a disconnect that carries a `Reject` frame into a `Rejected` session event, so
`WorldClient` classifies a version/token reject terminally instead of treating the bare drop as a transient outage
and auto-reconnecting forever.

`LoopbackTransport` implements the same overload rather than falling through to the default (#129). Being lossless
it would otherwise deliver BOTH copies of the reject, and the reasonless default delivered the drop first, so a
rejected loopback client observed `Disconnected` and only then `Rejected` - exactly the bare-drop-then-reconnect
sequence the reason on the disconnect exists to prevent, and a consumer that reacts to the first event it sees
fired before the `Rejected` was drained. A reason-carrying loopback disconnect now supersedes whatever the peer has
not polled and ends the session with the single `Disconnected` that carries the reject, matching the wire. An empty
reason is not a refusal and stays a plain, lossless disconnect: everything already sent still lands, then the drop.

`NetServer.Disconnect(slot, reason)` rides the same two paths for a KICK, so a client kicked by the game (a
repeated-offense flood kick out of an `OnSuspiciousActivity` handler, say) learns why instead of reading a bare drop
as a transient outage and reconnecting into the same kick. The reason is a stable token, not display text: a
headless server owns no string catalog, so send something the client matches and renders from its own localization.
It surfaces on `WorldClient` as `DisconnectReasonDetail`. `NetServer.Disconnect(slot)`, `WorldServer` and
`ShardedWorldServer` all keep the reasonless overload unchanged, and both world servers carry the reason-carrying
one too.

## Pending-connection cap

A connection the transport has accepted holds no slot until a valid `Hello` arrives, and until then the
per-connection rate limiter has nothing to limit. `NetServer`'s optional `maxPendingConnections` (0, the default,
leaves it unlimited) bounds how many such connections the server will hold at once, so a connection flood degrades
to refused handshakes instead of unbounded server-side state. It is the only flood mitigation available without a
remote address, which `INetTransport` deliberately does not expose.

The refusal is a BARE disconnect, not the framed `Reject` above: a cap whose job is to shed a flood must not answer
every flooded connect with bytes of its own. A legitimate client refused this way reads a plain drop and comes back
on its backoff, which is right for a transient capacity limit. A pre-slot `Hello` is accepted only while its
connection remains in the pending set, so one already queued behind a cap-refused connect cannot claim a player
slot after the disconnect. Two counters make it visible:
`PendingConnectionCount` (connections in flight toward a join right now) and `RefusedPendingConnectionCount` (total
refused by the cap). Both are forwarded by `WorldServer` and `ShardedWorldServer`, which take the cap itself as
`MaxPendingConnections` on their configs. Size it above the concurrent-join burst a launch or a restart produces,
not at `MaxPlayers`: a pending connection is cheap, and a cap that bites normal traffic locks out real players.

## One account, one live session

`NetServer`'s join gate keys a live session by the SUBJECT the authenticator verified, not by the connection, so
two clients presenting one account's connect token cannot become two live sessions. That shape is unrepresentable
above this layer: `WorldPersistence` keys one record per account, so two sessions share it and the last one to
write wins, which cost the earlier session its restore and then its stored position (#662).

There is no `NetServerConfig` type: `NetServer` is configured by its constructor, so the knob is the
`duplicateSessions` argument (`DuplicateSessionPolicy`, default `KickOlder`). Both server heads surface it as
`WorldServerConfig.DuplicateSessions` / `ShardedWorldServerConfig.DuplicateSessions`.

- **`KickOlder`** (default): the new session wins. The older one is disconnected with
  `SessionRejectReason.SignedInElsewhere` and its `Left` is enqueued BEFORE the newcomer's `Joined`, so a host
  draining events in order runs the old session's leave (and its save-on-leave) ahead of the new session's join and
  load-on-join, and never sees the two overlap. This is what a reconnect over a half-dead link needs: the old
  connection may be a corpse the transport has not buried yet, and refusing the newcomer would lock the player out
  until it times out.
- **`RefuseNewer`**: the live session keeps the seat and the second Hello is refused with
  `SessionRejectReason.AlreadySignedIn`. Safer for a server with no session-takeover story, at the cost of that
  reconnect case.

Both reasons are stable wire tokens, not display text: `WorldClient` maps them to `DisconnectReason.SignedInElsewhere`
/ `DisconnectReason.AlreadySignedIn` and the game shows its own localized line. What the client DOES about them
differs, and the split is the point:

| Reject | Client answer | Why |
| --- | --- | --- |
| `SignedInElsewhere` (the kick) | terminal, goes to `Disconnected` | Retrying displaces the session that just displaced this one, and the two clients trade the seat forever. |
| `AlreadySignedIn` (the refusal) | retried on the backoff, stays `Reconnecting` | Retrying a refusal displaces nobody. What holds the seat is usually this player's OWN half-dead connection, which the server keeps until its transport timeout expires. |

That second row used to be terminal too, which the numbers say was wrong. The engine leaves LiteNetLib's
`DisconnectTimeout` at its default 5 s, and the default `ReconnectBackoff` (0.5 s, doubling, capped at 5 s) spends
its first three attempts inside that window, so on a `RefuseNewer` server a one-second blip dumped the player at a
manual sign-in screen while their own dead peer was still being buried. From attempt four (7.5 s in) the backoff has
outlasted the window and the seat is free. The trade is that a seat genuinely held by someone else is now asked for
every 5 s rather than once: cap it with `ReconnectBackoff.MaxAttempts`, or turn `WorldClientConfig.AutoReconnect`
off, and show your own line while the state is `Reconnecting`.

A TOKENLESS connection authenticates to an EMPTY subject and is never deduped under either policy. It is anonymous
rather than an account, and two guests are two people.

**The gate is only as strong as the authenticator under it.** `KickOlder` ends a live session on the say-so of
whoever presents its subject, and the dev-default `AllowAllAuthenticator` takes the client's raw token bytes AS the
subject, so on a server running that gate any client can evict any other by sending someone else's account id. This
is the authenticator's own "never use as the only gate on an exposed server" with a sharper edge than it had: a
forged subject used to buy a second session, and now it buys somebody else's seat. Expose this to untrusted clients
only behind a real authenticator (`HmacTokenAuthenticator` over `SignedToken`, or the game's own). `RefuseNewer` does
not carry the edge, since a forged subject there only refuses the forger.

## SignedToken connect tokens

`SignedToken` is a zero-dependency HMAC-SHA256 connect-token primitive binding a `subject` (the stable account/player
id) to an expiry. The wire format is `v1.<subject>.<expUnix>.<sig>`, or `v2.<subject>.<nameB64>.<expUnix>.<sig>` which
adds a base64url display-name claim (cosmetic, distinct from the verified subject). `Mint(subject, expiry, secret)` and
`Mint(subject, displayName, expiry, secret)` issue tokens; `TryVerify(token, secret, now, out subject[, out displayName],
out reason)` is the authoritative server-side check (fixed-time HMAC compare, then expiry). `HmacTokenAuthenticator`
wraps `TryVerify` as an `IConnectionAuthenticator`.

- **`TryParseUnverified(token, out subject, out expUnix, out displayName) -> bool`** (14.9.0)
  A **secret-free STRUCTURAL parse**: extracts the subject, expiry (Unix seconds), and optional v2 display name without
  the HMAC secret, for a **client-side shape pre-filter** (sanity-checking a pasted or launch-supplied token before a
  connect, where the secret lives only on the server). It does **NOT** verify the signature and does **NOT** check
  expiry, so a genuine, a tampered, and an expired token all parse the same - it is **not authentication**. Its
  acceptance mirrors `TryVerify`'s own structural gate (non-empty; the v1 4-field / v2 5-field split with the matching
  version prefix; a `NumberStyles.None` numeric expiry), so a consumer deferring to it cannot drift from the format if
  a v3 is ever added. `displayName` is `null` for a v1 token, the empty string for a v2 empty-name claim, else the
  decoded name.

## Connect-time gate: ConnectionGate + HandshakeToken (17.40.0)

Promoted out of Ruinborne, because two games need the identical door and a tile server cannot reference
`KhaozEngine.NetWorld`, where the codec used to live.

**`HandshakeToken`** is the connect-token LAYER codec. A connect token is a nest of labelled layers, outermost
first, each `[magic][labelLen:byte][label utf8][inner]`, so a gate peels one layer, decides, and hands the rest to
the gate inside it. `Wrap(label, innerToken)` adds a layer, `TryUnwrap(token, out label, out innerToken)` peels
one. An unlabelled or corrupt token unwraps to label `""` with the whole token as the inner and returns `false`,
so a legacy peer that never opted in is handled as "unknown" rather than throwing. `MaxLabelBytes` (255) is the
label cap, one length byte. These are byte-for-byte the layers `KhaozEngine.NetWorld.ProtocolHandshake` has always
put on the wire: that type now delegates here, so the engine has one implementation of the format and nothing on
the wire moved.

It also owns the engine's refusal REASON tokens. Those are stable WIRE tokens, not display text: a client matches
the token and shows its own localized string.

- `IncompatibleVersionReason(requiredVersion)` / `TryParseIncompatibleVersion` build and read
  `ke:incompatible-version:<version>`, carrying the version the server requires.
- `WorldMismatchReason(serverHash, clientHash)` / `TryParseWorldMismatch` build and read
  `ke:world-mismatch:<server>|<client>`, carrying BOTH hashes so the client can say which world it built against
  (a hex hash never contains a pipe).
- `BannedReason` is the flat `ke:banned`. It carries no detail, because a ban reason is an operator concern rather
  than something to hand the banned client.

**`ConnectionGate.Wrap(tokenAuth, protocolVersion, worldHash, log?, isBanned?)`** composes the door and returns
the `IConnectionAuthenticator` the server takes. Order is load-bearing:

1. `VersionGateAuthenticator` is outermost, so a version-skewed client gets the ordinary out-of-date refusal and,
   having sent no world layer, never reaches the world check. `Wrap` gates on EXACT equality with
   `protocolVersion`. A head wanting a range or a compatibility window composes `VersionGateAuthenticator` itself
   with its own rule and nests the rest by hand.
2. `WorldIdentityGateAuthenticator` sits just inside it, refusing a client built against a different world so it
   can never join and render its own map while the server simulates another. Distinct from the version gate on
   purpose: a patch that leaves the world alone still interoperates. `log` receives both hashes on every refusal.
3. The real token authenticator (`HmacTokenAuthenticator`, or `AllowAllAuthenticator` for dev) is next, reached
   only once version and world both match.
4. `BanGateAuthenticator` WRAPS the token authenticator when `isBanned` is supplied, so its check runs last, after
   the token produced a subject (a ban keys on the VERIFIED subject and only the token check produces one). The
   predicate is called synchronously on the host thread, so keep it an in-memory view over whatever store the head
   owns. An empty subject is not ban checked, because an anonymous admit produces no account id to key a ban on.

**There are two ban paths, and a `WorldServer` game has both.** `BanGateAuthenticator` is the AT-THE-DOOR one: it
refuses a subject the head ALREADY knows is banned during authentication, with `ke:banned`, before any join, so the
client reads a refused connect. `KhaozEngine.NetWorld.IBanStore` is the LIVE one: a `WorldServer` consults it at
JOIN and kicks with a typed `ServerNotice(ServerNoticeKind.Banned)`, which is the route a ban applied mid-session
takes and the one a game banned-player banner renders. The check here is a `Func<string,bool>` rather than an
`IBanStore` because `IBanStore` lives in `KhaozEngine.NetWorld`, which this package cannot reference. A
`WorldServer` game wiring both puts the SAME store behind both, as the `banStore:` ctor arg and as
`isBanned: store.IsBanned`, so the two can never disagree about who is banned.

The three decorators are public and compose on their own when a head wants a different order, or only one of them.

`ConnectionGate.BuildToken(protocolVersion, worldHash, innerToken)` builds the version layer wrapping the world
layer wrapping the real auth token. On a plain `NetServer` / `NetClient` pair that is the whole token a client
presents to the door. A `KhaozEngine.NetWorld` game needs one layer more, see below.

```csharp
IConnectionAuthenticator auth = ConnectionGate.Wrap(
    new HmacTokenAuthenticator(secret, () => DateTimeOffset.UtcNow),
    protocolVersion: "grimhollow-1",
    worldHash: worldHash,
    log: Console.WriteLine,
    isBanned: bans.IsBanned);

byte[] token = ConnectionGate.BuildToken("grimhollow-1", worldHash, Encoding.UTF8.GetBytes(sessionToken));
```

That example is a plain `NetServer` / `NetClient` pair, which is the tile server's shape. **A
`KhaozEngine.NetWorld` game needs one layer more.** `WorldServer` installs a `WireGenerationAuthenticator` around
whatever authenticator it is handed, and `WorldClient` always stamps the matching `ke-wire:<generation>` label
outermost through `ProtocolHandshake.BuildClientToken`, so `BuildToken` output presented as-is is refused by the
wire gate before the version gate ever sees it. In other words `BuildToken` produces the INNER token and
`ProtocolHandshake` wraps it. There, pass `ConnectionGate.Wrap(...)` as the `authenticator:` arg, leave
`WorldClientConfig.ProtocolVersion` to carry the version layer, and set the connect token to
`HandshakeToken.Wrap(worldHash, authToken)` alone, so the layers arrive as
`[ke-wire:N][ProtocolVersion][worldHash][auth]`.

Ruinborne still emits its own `rb:world-mismatch:` reason token (`Ruinborne.Shared.RuinborneWorldIdentity`), so its
gate is NOT yet an alias of this one. Swapping it over changes a wire reason token its shipped clients already
match on, which needs a Ruinborne protocol-version bump plus a client-side reason mapping. The promoted parser is
also the stricter of the two: `RuinborneWorldIdentity` reads a body with no pipe as all-server-hash, where
`TryParseWorldMismatch` returns false. The engine's own producer always writes the pipe, so nothing breaks today,
but the swap has to account for the dropped tolerance.

## NetEndpoint: parsing the address a player typed

`NetEndpoint.TryParse` turns a server address into a host and a port. It covers a bare host, `host:port`, a
bracketed IPv6 literal with or without a port, and a bare (unbracketed) IPv6 literal, which takes the default
port the caller supplies.

```csharp
if (!NetEndpoint.TryParse(configuredAddress, defaultPort: 7777, out string host, out int port))
{
    // One rejection for every malformed form, including the ones that used to parse into something plausible.
    return null;
}

await transport.ConnectAsync(host, port);
```

Two games hand-rolled this and one of them was wrong, which is why it is here. The obvious implementation
splits on the last colon, and that reading breaks on every unbracketed IPv6 literal: `"::1"` splits into a host
of `":"` and a port of `1`, and both halves pass an ordinary bounds check, so the client silently dials an
endpoint nobody asked for instead of reporting a bad address. Brackets are what separate an address's colons
from the port separator, so an unbracketed literal keeps all of its colons and never yields a port. That also
settles `"fe80::1:9000"`, which is one whole IPv6 literal here rather than a host and a port.

The rest of the contract: surrounding whitespace is trimmed, a null or blank address is rejected rather than
defaulted (what an unset address means is the caller's decision), ports are bounded to `[1, 65535]` with no
sign and no separators, and a bracketed literal must actually parse as IPv6. A `defaultPort` outside
`[1, 65535]` throws `ArgumentOutOfRangeException`, because that is a wiring mistake in the caller rather than
bad input. Nothing here touches DNS or a socket: the host comes back as written (brackets stripped) and
resolving it stays the transport's job.
