# KhaozEngine.Netcode

Game-agnostic, transport-free netcode primitives.

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
