# Grimhollow delta reliability proposal

Status: **PROPOSAL awaiting root and owner review.** No specification, implementation plan, release, or
consumer switch is approved by this document.

Program: [KhaozEngine #34](https://github.com/APKiwiOrg/KhaozEngine/issues/34) and the component defect
[KhaozEngine #1229](https://github.com/APKiwiOrg/KhaozEngine/issues/1229). Consumer gate:
[Grimhollow #399](https://github.com/APKiwiOrg/Grimhollow/issues/399), phase P8 of the continuous movement
spec at `docs/superpowers/specs/2026-10-01-continuous-movement-design.md` in the consumer repository.

Evidence base: engine commit `11a3374273e20fb211602df63642488a43b8ba51`, wire generation 13. Code and test
sources were inspected. This documentation task ran no build, test, client, GPU, or packet experiment.

## Intent and authority

Grimhollow needs an opt-in unreliable-sequenced delta stream before P8. Dropping or reordering state
packets must not strand a component at its intermediate value, resurrect a removed component, or leave
a ghost entity. Default reliable delivery, reliable game messages, per-viewer visibility, and the
transport-free package boundaries remain consumer contracts.

Round 1 decision D5 deferred #34 for its own design. D6 moved the fragment core into Netcode without a
NetWorld host change. P8 requires an engine release and normal consumer adoption before the game switch.
Those decisions authorize preparing this proposal. They do not select these options, budgets, public
APIs, or a release version. Current round D work owns predicted-state access and is a dependency of the
acceptance evidence, with no duplicate prediction API proposed here.

Success means exact convergence to each accepted server projection, coherent prediction reconciliation,
and bounded recovery after the finite fault schedules below. Endless loss cannot promise freshness.
The engine remains the owner of delivery and reconstruction. The game owns gameplay, journal mutations,
visibility policy, interaction authorization, protocol labels, and its P8 release decision.

## Observed contracts and failure

Paths below are relative to the engine root.

| Contract | Current evidence | Consequence |
|---|---|---|
| Delta writer | `KhaozEngine.Replication/AoiDeltaReplicator.cs`, `ServerReplicator.cs`, `DeltaEncoding.cs` | Both writers compare captured component bytes against an acknowledged sequence. Whole-world and AOI callers both need disposition. |
| Delta body | `DeltaEncoding.WriteChangedEntity` | Two signed 32-bit sequence fields, removed entity count and 64-bit ids, changed entity count, then `netId`, `isNew`, removed component count and ids, component frames, and a zero type terminator. `baselineSeq == -1` means full state. |
| Component framing | `ReplicationRegistry.Register<T>`, `ClientReplicationView.ReadEntityComponents` | Built-ins below type id 16 are unframed and codec-dependent. Extensions carry a 7-bit payload length, unknown extensions are skipped, and known extension readers are bounded to their own frame. Zero-byte tags are valid state. |
| Live application | `ClientReplicationView.ApplyDelta` | An older baseline is accepted if it is at or before `LastAppliedSeq`, but no per-sequence projection is retained. The delta changes the latest ECS state. `isNew` is read and ignored. |
| Client buffers | `ClientReplicationView` | `currentBytes` retains sampled components, rather than every replicated component. Fixed-delay presentation can write interpolated bytes into the ECS. These buffers cannot serve as authoritative baselines. |
| Presence patch | `AoiPresenceRecord`, `AoiDeltaReplicator.IsWholeEntry` | Last-sent whole/removal records cover AOI enter/leave edges within an ack window. They are pruned on ack and cleared on `Forget`. `ServerReplicator` has no corresponding presence record. |
| Retention and acks | `AoiDeltaReplicator.Acknowledge`, `ServerReplicator.Acknowledge` | AOI pending projections have default depth 32 and stale-ack rejection. Its acknowledged projection remains pinned separately. Whole-world history has depth 32, and its ack setter can select an older retained sequence. Neither provides wrap-safe ordering. |
| NetWorld serve | `WorldServer.Tick`, `ShardedWorldServer.Tick` | `DeltaReplication` defaults true. A `DeltaCapable` client gets deltas, others get full snapshots. Both paths send `ReliableOrdered`. Sharded serve sequences can advance even when a short frame runs no simulation sub-tick. |
| NetWorld ingest | `WorldClient.OnDelta`, `IngestServerState` | A frame has local net id and movement command ack, separate from replication ack. Apply failure disconnects as incompatible. Success reconciles once, records arrival-time interpolation samples, and sends a reliable replication ack. |
| Recovery | Writers and `WorldClient.OnSnapshot` | A no-baseline delta is full state. Whole-world history expiry also produces one. There is no explicit repair request, negotiated keyframe barrier, or periodic keyframe policy in these paths. |
| Visibility | `InterestVisibility.Filter`, `CaptureProjection.OwnerScope`, `ReplicationChannels` | Entity visibility filters interest before serialization, with the viewer's player exempt. Component ownership is separate. Ghosts lack private/server-only state. AOI baselines store the exact viewer projection, while whole-world historical captures are re-projected with a stable owner id. |
| Transport | `INetTransport`, `ChannelSplitter.ToDeliveryMethod`, both LiteNetLib transports | `UnreliableSequenced` maps to LiteNetLib `Sequenced`, on the binding's default channel. The seam has no packet-size query and does not expose LiteNetLib types. |
| Fragment core | `MessageFragmenter`, `MessageReassembler` | Five-byte chunk headers, at most 255 chunks, fixed agreed width, at most four partial assemblies per connection. The reassembler expects reliable order and refuses a skipped chunk. It cannot be used unchanged for unreliable fragments. |

Issue #1229 records an actual removed reproduction of value `1 -> 2 -> 1`: sequence S is acknowledged
with value 1, S+1 changes it to 2, and S+2 contains no change relative to S. Overlaying S+2 leaves 2.
The issue's removal/re-add case is a lead, rather than an executed reproduction. The same reasoning
predicts failure for component absence and an entity arriving then departing when the baseline lacks it.

Source tests cover ordinary movement, older-baseline non-reverting changes, tags, interpolation, AOI
presence edges, owner-only channels, and stable net ids across handoff. Relevant starting points are
`KhaozEngine.Server.Tests/Replication/{ReplicationDeltaTests,AoiDeltaReplicatorTests,
ClientReplicationViewHealTests,ReplicationChannelsTests}.cs` and
`KhaozEngine.Server.Tests/NetWorld/{WorldClientDeltaTests,WorldServerDeltaTests,
ShardedWorldServerDeltaTests,RawDeltaClient}.cs`. Their synchronous movement loops do not establish
presentation correctness with independent prediction and server phases. `LoopbackTransport` and
`InMemoryTransportHub` preserve send order even for unreliable events, so they inject no loss themselves.

## Options and recommendation

Scores are design judgments, not measurements, on a 1-10 scale with ten best. Weighted total is
`sum(weight * score / 10)`, out of 100.

| Criterion | Weight | A: last-sent reliable plus acknowledged rebuild | B: versioned reconstruction for both modes | C: independent full projections |
|---|---:|---:|---:|---:|
| Exact state under the declared delivery contract | 25 | 10 | 10 | 10 |
| Reliable default and standalone compatibility | 25 | 10 | 4 | 8 |
| Bounded packet and cache policy | 15 | 8 | 8 | 6 |
| Steady-state bandwidth | 15 | 10 | 10 | 2 |
| Implementation and test simplicity | 15 | 6 | 8 | 10 |
| Existing package seams | 5 | 10 | 10 | 10 |
| Weighted total | 100 | **91** | **79** | **77** |

**Recommend A.** Reliable order already makes the last sent projection the receiver's predecessor.
Diffing against it closes value reversion, remove/re-add, and presence gaps without changing the legacy
body or requiring a new reader. Unreliable order needs a different explicit contract: name an
acknowledged projection and reconstruct the entire next projection before publication. A shared
capture/diff/publication core can support these two declared contracts.

B gives both delivery modes the same reconstruction rules and could eventually retire the legacy
contract. It requires an explicit default-wire migration or retains a legacy adapter that still needs
its own rules. Existing standalone `ApplyDelta` consumers do not retain baseline history. Sending them
new semantics under the old body would repeat this defect. A global generation bump also rejects
otherwise compatible default peers. This compatibility cost is the reason B scores lower.

C sends each viewer's whole projection independently on the unreliable stream, with reliable recovery
for oversize state. It eliminates baseline dependencies but forfeits idle-delta savings. Dense AOIs can
keep overflowing the single-packet budget, turning the stream into repeated reliable repairs. It is a
useful diagnostic/reference implementation for equality tests, not the recommended product transport.

Option A has a real maintenance cost: tests and public docs must name which contract a method uses.
The owner must accept that cost or select B before implementation planning.

## Recommended reliable contract and public compatibility

Keep `WorldServerConfig.DeltaReplication`, `ShardedWorldServerConfig.DeltaReplication`, and
`WorldClientConfig.RequestDeltaReplication` with their existing defaults. Reliable remains the default
transport. `DeltaCapable`, `ServerFrameKind.Delta`, the signed sequence body, full-snapshot fallback,
`ClientReplicationView.ApplyDelta`, and `TryApplyDelta` keep their existing wire/application shapes.

Change both existing `WriteFor` paths to diff against the last sent **viewer-scoped** projection.
The header names that projection's sequence. First serve has baseline -1. Acks may prune diagnostics
and history but cannot choose the next reliable diff basis. Store the exact owner-filtered projection
that was sent, including absence, rather than re-projecting old raw bytes with a new owner id. Every
component and presence edge is then relative to the predecessor actually delivered by the reliable
channel. AOI presence records cease to determine this reliable diff and can be retired in this path.

This is a source-compatible correctness change with observable header/bandwidth differences during an
ack delay. Existing tests that demand repeated ack-relative changes need their contract revised. Existing
reader tests and wire framing goldens remain applicable. Living API docs must state the new writer
contract when code ships. This proposal changes none of those reference surfaces yet.

For standalone callers, each legacy `WriteFor` return is a send commitment: ship every returned payload
exactly once in order over `ReliableOrdered`. Existing direct `ServerReplicator` and AOI readers then
receive the #1229 fix without a new reader. A caller that discards a built delta, sends it on an
unreliable channel, changes sessions, or replaces the receiver must reset that slot before its next
serve. Add `ServerReplicator.Forget(int slot)` to match AOI's existing reset. An exception during send
also requires a fresh replicated receiver world/view or disconnect. `Forget` alone cannot repair an
arbitrary old receiver world: the legacy reader ignores `isNew`, and a full entity does not remove a
component omitted from that entity. NetWorld's reconnect path already creates a fresh world and view.
Future API docs must make this obligation explicit. The legacy API never advertised an unreliable
transport contract.

Legacy signed sequences must not silently wrap into negative values or the -1 sentinel. The counter
belongs to the writer instance, shared by every slot, rather than to one session. Recommend the
following global exhaustion boundary, with `int.MaxValue` the last permitted captured/sent sequence:

1. `LegacySequenceExhausted` becomes true at that value. A further `BeginTick` or `Capture` cannot
   increment the counter. The owning host stops admission and serving for that writer at the next
   boundary before attempting another capture.
2. The lifecycle owner disconnects every affected session served by that writer, including v2 sessions
   whose per-slot state the reset clears. No old receiver remains attached. Reconnect-capable NetWorld
   clients create fresh replicated worlds/views through their existing reconnect path. Standalone
   owners must create fresh receivers and connections themselves.
3. Only after those connections have ended does the owner call
   `ResetAfterLegacySequenceExhaustion()` on the writer. It requires exhaustion and clears the global
   signed writer/capture counter to zero, global history and its insertion order, shared capture caches,
   and every per-slot last-sent, acknowledged, pending, presence, candidate, and rebuild state. The first
   subsequent capture is sequence 1. The first legacy serve is full state with baseline -1.
4. Resume admission with fresh receivers. Fresh v2 sessions negotiate fresh epochs. The host's
   server-lifetime epoch allocator stays monotonic and is not reset with the writer.

`WorldServer` and `ShardedWorldServer` own this lifecycle sequence for their replicators. A standalone
caller owns it for `ServerReplicator` or `AoiDeltaReplicator`. Replication cannot disconnect transports
it does not own. `Forget(slot)` only clears that slot and never resets the global counter or shared
history. An ordinary disconnect, an empty slot table, or a repair barrier alone cannot trigger the
global reset API. It rejects calls before exhaustion, and its lifecycle precondition is that all
affected connections have ended. The owner must approve this restart policy. Bounded tests seed the
counter near exhaustion and verify the boundary, rather than running through the signed range.

## Opt-in v2 stream and wire compatibility

Add an explicit opt-in on both server configs and the client config. The proposed name is
`AllowUnreliableDeltaReplication` on servers and `RequestUnreliableDeltaReplication` on the client,
both false. They require the existing delta switch to be enabled. No move, notice, game message,
authentication, or journal payload changes channel as a side effect.

**Recommend keeping engine wire generation 13 for this extension at the evidence base.** It changes no
built-in component codec. The two-byte new capability is safely ignored by an older server's control
switch. A newer server sends new frame kinds only after that capability. Global generation mismatch
still rejects peers through `WireGenerationAuthenticator`, and the game protocol gate remains separate.
Implementation must re-read the then-current generation and unused discriminators. If the chosen work
changes an existing body or built-in codec, it needs an explicit new generation and owner review.

The current source still uses control kinds 1 and 2, ack sub-marker 0xA0, game-message sub-marker 0xB0,
and server frame kinds 0 through 3. Proposed assignments below are free at the evidence base. Existing
client control and replication ack lengths are 2 and 6. Movement is exactly 18 bytes, and the existing
game-message decoder explicitly excludes length 18. All multibyte new fields use explicit little-endian
encoding. Extension component payloads retain their existing 7-bit framing.

| New message | Proposed body and route |
|---|---|
| Capability | Existing `[0xC5][ClientControlKind.RebuildDeltaCapable = 3]`, reliable. Only a requesting client sends it. It also sends the existing `DeltaCapable = 2`. |
| Mode offer | `ServerFrameKind.ReplicationMode = 6`, then format byte 2, mode byte 0 for legacy reliable or 1 for acknowledged unreliable, reason byte, history count `ushort`, history payload bytes `uint`, maximum full keyframe bytes `uint`, entity limit `uint`, component limit `uint`, packet cap `uint`, chunks per tick `ushort`, and epoch `ulong`. Reliable. Reason 0 is selected, 1 is unavailable transport limit, and 2 is disabled server policy. Mode 0 carries zero epoch and zero v2 limits and needs no acceptance. |
| Mode acceptance | `[0xC5][0xA1][format byte 2][epoch ulong]`, exactly 11 bytes, reliable. A client accepts only an offer within its configured limits. Otherwise it disconnects with a specific replication-policy refusal, rather than silently changing the requested contract. |
| Rebuild delta | `ServerFrameKind.RebuildDelta = 4`, existing `[localNetId long][movementAck int]`, then v2 body. Unreliable-sequenced. |
| V2 body | `[format byte 2][flags byte][epoch ulong][snapshotSeq uint][baselineSeq uint]`, followed by the existing removal/changed entity sections. Flag bit 0 means keyframe, with baseline field zero and zero removed count. Otherwise the baseline id is `(epoch, baselineSeq)`. Other flags are invalid. |
| Replication ack | `[0xC5][0xA2][epoch ulong][snapshotSeq uint]`, exactly 14 bytes. Routine acks are unreliable-sequenced and repeated. A keyframe ack is reliable. |
| Repair request | `[0xC5][0xA3][format byte 2][epoch ulong][lastAcceptedSeq uint][missingBaselineSeq uint]`, exactly 19 bytes, reliable and coalesced. The format byte keeps it outside the movement length. |
| Keyframe chunk | `ServerFrameKind.RebuildKeyframeChunk = 5`, then epoch `ulong`, snapshot sequence `uint`, total logical bytes `uint`, and a `MessageFragmenter` chunk. Reliable. The assembled object is `[localNetId long][movementAck int][v2 keyframe body]`. |

Preserve length-first demux. **Every 18-byte frame belongs to the movement decode/validation path.**
Every new control decoder, including its recognized-marker malformed rejection path, must first
exclude length 18 without reading or claiming a marker. Valid move sequences `0x0000A1C5`,
`0x0000A2C5`, and `0x0000A3C5` legitimately begin with `[0xC5][0xA1]`, `[0xC5][0xA2]`, and
`[0xC5][0xA3]` in the current encoder. They must reach `commands.Store` after normal move validation.
Rate limiting, finite-axis/yaw validation, and ordinary command queue bounds/replay checks still apply.

For a non-18-byte frame, a recognized new sub-marker claims that control family before any movement
fallback. Validate its exact length, revision where present, and remaining fields there. An invalid
recognized control is rejected and cannot fall through, including a 19-byte request with a bad version
or an overlong recognized ack. Old ack marker 0xA0 and old frame kinds remain distinct.

The sender's intent is unavailable on the wire. A sender-intended malformed control that happens to be
18 bytes is treated as movement, and may be accepted if its bytes pass ordinary movement validation.
Marker inspection cannot distinguish it from a legitimate marker-prefixed move. No acceptance claim
can promise otherwise. The v2 reader rejects unknown format revisions. Unknown framed component ids
remain skippable for publication and retainable as opaque baseline bytes. Unknown unframed built-ins
remain terminal incompatibility.

The server stops issuing legacy state when it sends an unreliable mode offer. Previously queued legacy state
precedes that offer on the reliable channel. The client accepts the offer and waits for its keyframe.
It never uses the last legacy live world as the new epoch's baseline. A non-requesting client stays on
the corrected reliable path. An older server ignores the capability and keeps the reliable path.
Mixed current-wire peers can therefore coexist without an implicit default wire break. A new server can
surface reliable fallback through a mode-0 reply. An older server's silence leaves the client's selected
mode reliable and its selection reason unnegotiated, without stopping a healthy legacy session.

## Reconstruction, publication, and presence

Each retained client state is an immutable map of net id to type id to the exact captured payload bytes.
It includes unsampled components and zero-byte tags. Client interpolation samples, predicted state,
and the mutable ECS are separate. An acknowledgement means that exact projection was reconstructed and
retained successfully, not merely that a packet arrived.

For a newer delta in the current epoch, find its exact named baseline, copy/share that immutable state,
and apply its entity removals, full entries, component removals, and component replacements to the copy.
A full entry replaces that entity's entire replicated component set. A keyframe starts from empty state.
Every keyframe entity must be a full entry, and its removed entity/component sections must be empty.
An empty delta reconstructs its baseline even if the published world currently holds an intermediate
value. This is the central repair for #1229.

Validate lengths, counts, unique entity/type ids, flag values, frame termination, and complete payload
consumption before publishing. No declared count allocates beyond configured limits. Preserve raw
component payloads rather than reserializing them through a possibly different writer. Existing unframed
built-in decoding can establish each slice's boundary through the registered reader in a staging world.
Known extension readers remain confined to their frame. Registry readers must remain deterministic
value readers, as their existing `Func<BinaryReader,T>` shape permits.

Publication reconciles the new projection with the previously published authoritative projection.
Despawn absent net ids, preserve the ECS entity for surviving ids, remove registered components now
absent, and install every known authoritative component for this accepted snapshot. Installing only
bytes that changed from the previous publication is insufficient because presentation may have changed
the live ECS since then. Engine-internal typed copy delegates captured in `Register<T>` can publish the
validated staged values without invoking wire readers twice. Game components outside the replication
registry remain outside this replacement operation.

Refresh interpolation current/previous buffers and timestamp samples once per accepted projection.
Remove departed component histories so interpolation cannot re-add removed state. Keep the existing
local-avatar exclusion and teleport cuts. `IngestServerState` runs exactly once after acceptance, using
the movement ack bundled with that same authoritative projection. Rejected stale or missing-baseline
packets update neither movement ack nor prediction, sample history, ingest counters, or render state.
No new predicted-state access surface is part of this proposal.

| Baseline and delivered intermediates | New current projection | Reconstruction result |
|---|---|---|
| Value 1, intermediate 2 | Value 1, no changed bytes | Restores 1 from baseline. |
| Component present, intermediate removal | Same component re-added with baseline bytes | Restores its presence and bytes. |
| Component absent, intermediate add | Component absent | Removes the intermediate addition. |
| Entity present, intermediate AOI leave | Entity present with baseline bytes | Restores it from baseline, even without a full entry on the wire. |
| Entity absent, intermediate entry | Entity absent | Removes the intermediate entity. |

The v2 diff needs no last-sent presence patch. Presence is part of the complete reconstructed projection.
Old packets cannot undo a newer hide or removal because accepted snapshot order is monotonic.

## Retention, sequence ordering, and ack loss

Use `ReplicationPacketId(ulong Epoch, uint Sequence)` for v2. Epoch is a server-issued nonzero counter
unique among that server instance's session/reset epochs. The authenticated transport connection
isolates different server lifetimes. A slot reuse or repair gets a fresh epoch. Only a reliable mode
offer or reliable keyframe can establish the next epoch. An unknown-epoch datagram cannot establish it.
The counter never wraps during an instance lifetime. A receiver accepts a replacement epoch only when
it is numerically greater than its established one or matches its initial accepted offer. Retired
keyframe chunks cannot restart an assembly. An old epoch cannot revive a retired stream.

Within an epoch, `a` is newer than `b` exactly when the unsigned difference `a - b` is in 1 through
`0x7fffffff`. Equality is a duplicate and a half-range difference is invalid. Retention and in-flight
windows are smaller than half the sequence range. Wrap through `uint.MaxValue -> 0` is valid. Sequence
is a serve/capture identity, not a simulation tick or a movement command sequence. After a half-range
silence, start a new epoch instead of guessing order. Keyframe barrier acks compare exact ids.

Server state per viewer holds one pinned acknowledged projection, one pinned repair candidate when
needed, and a bounded insertion-ordered set of committed sends. Promote only a newer ack for an exact
id actually sent to that connection and still retained. Ignore duplicate, stale, retired-epoch, future,
pruned, and never-sent ids. Remove superseded candidates after promotion. A shared whole-world capture
must never let a standalone slot acknowledge a sequence that it was not served.

Client retention pins the latest published projection, the most recent ack target, and the newest
confirmed server baseline observed in accepted packets. These pins count within the negotiated limits,
not in an unbounded side table. Other states are pruned oldest first using an insertion ordinal. A
server can briefly use an older ack target after the client pruned it. That packet is a recoverable
baseline miss, never permission to overlay the live world. Recovery is the deliberate safe limit of
finite retention.

Routine v2 acks advertise the newest retained accepted id at most once per replication tick, coalesced
after receive draining. Repeat it even when no new packet was accepted. A dropped ack leaves the server
on its old baseline, from which subsequent deltas still reconstruct correctly. Reordered acks cannot
regress it. Send the barrier ack reliably. These control messages do not share movement command ids.
The binding's sequenced channel can also carry consumer-selected unreliable messages, so safety must
hold when those messages suppress a routine delta or ack. Consumers selecting reliable game messages
keep their existing reliable channel.

## Concrete proposed budgets and packet policy

These are review defaults, not measured Grimhollow capacity. Expose validated knobs and refuse a mode
offer that exceeds the receiver's limits. Reliable legacy callers retain their existing packet policy,
with one last-sent projection per slot. V2 introduces the following hard resource contract.

| Limit | Proposed default | Enforcement |
|---|---:|---|
| Retained projections per viewer | 32 | Includes pinned baseline, publication, ack target, and repair candidate. At least 4 are required. |
| Retained payload bytes per viewer | 2 MiB | Count all reachable retained component buffers, including opaque extensions and repair candidate. |
| Complete keyframe bytes | 64 KiB | Includes local net id, movement ack, v2 header, counts, ids, lengths, and component payloads. Enforce before retaining/sending. |
| Entities in a projection | 1,024 | Validate capture and reconstruction. |
| Component frames in a projection | 16,384 | Includes zero-byte tags and unknown extensions, bounding metadata independently of payload bytes. |
| Transport send payload cap | 512 bytes | Includes `SessionFrame`'s byte and the entire NetWorld envelope, before transport headers. Reduce to the backend's actual unfragmented limit. |
| Routine state sends | 1 per viewer per replication tick | Keep only the newest unsent projection. No backlog of obsolete deltas. |
| Reliable repair chunks | 4 per viewer per replication tick | At most one frozen keyframe per viewer. Schedule a bounded chunk run across ticks. |
| No-ack capture window | 31 new sends | Start repair before an active pinned baseline prevents bounded retention. |
| Repair request interval | 30 replication ticks | One coalesced missing-baseline request. Server coalesces all requests during its active barrier. |
| Repair/negotiation deadline | 90 replication ticks | Recommend a typed replication-recovery failure and ordinary disconnect policy on expiry. No automatic unbounded retry. |

Per-viewer retained state must use compact viewer-only buffers. Counting visible slices while retaining
an entire shared world capture would evade the byte bound and keep hidden owner bytes alive in viewer
storage. Shared immutable public segments are allowed only if their complete backing allocations are
charged to the bound. Per-tick world capture remains shared and is released when no retained projection
references it. Component/identity metadata has a count bound in addition to payload bytes. Staging,
publication, and keyframe reassembly are separately bounded by one projection each. There is no
unbounded map of rejected packets or missing sequences.

NetWorld must know the maximum unfragmented **transport payload**, rather than hard-code a UDP MTU.
Recommend an additive optional `INetTransport.MaxUnfragmentedPayloadBytes(connection, reliability)`
method. A zero result means unknown and disables unreliable serving for that connection, with reliable
fallback and a surfaced reason. It is not a guessed 1,200-byte allowance. Session facades forward the
query without exposing their transports. LiteNetLib's cached 2.1.2 XML documents the peer method
`GetMaxSinglePacketSize(DeliveryMethod)` as the unfragmented packet limit. The backend must confirm its
pinned signature before implementation and return that value through the seam. This task did not load
or execute the backend.

For an ordinary v2 delta, combined overhead is 32 bytes before removal/changed counts: one session byte,
one frame-kind byte, twelve snapshot-header bytes, and eighteen v2-header bytes. The empty delta is
40 bytes. Compare the complete framed length against `min(512, actual transport limit)` immediately
before sending. If it is too large, initiate a reliable keyframe barrier. Do not truncate entity lists,
silently omit components, or fragment an unreliable delta.

Keyframe chunks have 23 bytes of overhead including session, frame kind, epoch, full sequence, total
logical length, and the five-byte generic fragment header. At cap 512 the agreed chunk payload width
is 489. A 64 KiB complete object takes at most 135 chunks, below the generic 255-chunk cap, and takes at
most 34 scheduled ticks at four chunks per tick before transport delay. If the actual transport limit
is lower, reduce the agreed width and verify the full-object bound still fits 255 chunks before
accepting the mode. The width is fixed for an epoch, as the existing reassembler requires. A shrinking
packet limit triggers a new offer/epoch, rather than changing width mid-assembly.

All tick budgets use a fixed replication cadence at the configured `TickSeconds`, independent of
render frames, the number of host `Tick` calls, and whether a sharded simulation sub-tick ran. Short
host frames accumulate elapsed time and keep the newest unsent projection. At most one serve/chunk
allowance is consumed at a cadence boundary. The v2 client's timed ack/recovery scheduling uses elapsed
`WorldClient.Poll(dt)` time. An opted-in consumer must supply that elapsed time, with zero-duration
drain calls permitted between scheduled calls. Input production retains its own phase and accumulator.
These requirements belong in the future public options contract and phase-offset acceptance tests.

Use one reserved internal fragment stream and one active assembly per viewer, with the full epoch and
sequence outside the fragment header. The generic 16-bit chunk sequence is the low 16 bits of the full
sequence and checks assembly consistency, not stream freshness. Validate total logical length before
copying. Reject excessive chunks or mismatched identity/length and discard the partial assembly on
disconnect. This composes the existing reliable fragment core without changing its unreliable contract
or taking one of the game's message kinds. It does not implement NetWorld game-message fragmentation
on behalf of P3/P4.

## Repair and keyframe barrier

Negotiation, baseline miss, retention pressure, oversize delta, and an explicit transport-limit change
can initiate repair. No periodic keyframe is needed for correctness with ack-relative reconstruction.
The proposed no-ack window is a bounded recovery trigger even when every routine ack is lost.

1. Stop issuing state deltas for that viewer, allocate a new epoch, and freeze one complete currently
   authorized projection with its matching movement ack. Offer the selected limits reliably for initial
   negotiation or a changed packet width. A repair with unchanged limits establishes its new epoch by
   its reliable keyframe chunks.
2. Send that one keyframe reliably in at most four chunks per scheduled tick. Keep movement input,
   notices, and reliable game messages running. Cross-channel order is never assumed.
3. The client ignores retired-epoch deltas, assembles within the limit, validates privately, installs
   the full projection once, and retains its exact id. The older live world supplies no baseline data.
   The client then acknowledges that exact keyframe id reliably.
4. Only after the server receives that ack does it promote the keyframe as the new baseline and resume
   unreliable deltas from the newest current projection. State changes during the barrier collapse
   into that next projection, with no stored per-tick send queue.

A missing baseline leaves the last accepted projection visible while prediction continues through its
existing bounded pending-command behavior. It requests repair once per interval and repeats the latest
valid ack. A stale/duplicate packet is ignored before decoding its body. Malformed bytes or unknown
built-ins remain terminal decode incompatibility, distinct from a normal baseline miss. A recovery
deadline produces a specific failure, and the owner chooses how the game presents/retries that failure.
Ignored datagrams must not indefinitely refresh the client's valid-state liveness deadline.

If repeated complete projections exceed the 64 KiB or metadata cap, repair cannot fix them. Report a
capacity failure and use the owner-approved disconnect/fallback policy. Repeated reliable keyframes
under a dense AOI can remove the bandwidth benefit, even when they are correct. This needs measured
consumer sizing before adoption, not a hidden cap increase.

Visibility is applied before freezing every projection, including recovery. A hide after keyframe
capture becomes a removal in the first resumed projection. State already sent while visibility was
true cannot be recalled. The existing visibility gate is a presentation policy, not retroactive
secrecy. A stricter revocation requirement would need an owner decision about cancelling and rebuilding
a keyframe. Owner-id changes or session replacement require a fresh epoch. Handoffs preserving net id
and owner keep the same stream. Persist/Migrate-only state never enters retained viewer baselines.

## API sketches at existing seams

These declarations are proposed additions. They are not compiled signatures or an implementation plan.
Names, discriminator reservations, and policy types require review before code. Existing public methods
stay available with the reliable contract described above.

```csharp
// KhaozEngine.Replication, referencing Ecs only.
public readonly record struct ReplicationPacketId(ulong Epoch, uint Sequence);
public enum DeltaRebuildResult { Accepted, DuplicateOrStale, MissingBaseline, Invalid }
public sealed class DeltaRebuildOptions
{
    public int MaxRetainedProjections { get; init; } = 32;
    public int MaxRetainedPayloadBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxKeyframeBytes { get; init; } = 64 * 1024;
    public int MaxEntities { get; init; } = 1024;
    public int MaxComponents { get; init; } = 16384;
}
public sealed class ReplicationDeltaPacket
{
    public ReplicationPacketId Id { get; }
    public ReplicationPacketId? Baseline { get; }
    public bool IsKeyframe { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
}

// Additions to AoiDeltaReplicator and ServerReplicator.
public void StartRebuild(int slot, ulong epoch, DeltaRebuildOptions options);
public ReplicationDeltaPacket BuildRebuildFor(int slot, World world,
    IReadOnlySet<long> interestSet, long? ownerNetId = null, bool keyframe = false);
public void RecordRebuildSent(int slot, ReplicationPacketId id);
public void AcknowledgeRebuild(int slot, ReplicationPacketId id);
// ServerReplicator's overload uses its captured world, without world/interest arguments.
public void Forget(int slot); // already exists on AoiDeltaReplicator
public bool LegacySequenceExhausted { get; }
// Exhaustion only. Lifecycle owner must first end every affected connection.
// Clears global counter/history/capture caches and all per-slot state.
public void ResetAfterLegacySequenceExhaustion();

// New reconstruction owner composes the existing presentation view.
public sealed class ClientDeltaRebuild
{
    public ClientDeltaRebuild(ReplicationRegistry registry, ClientReplicationView view,
        DeltaRebuildOptions options);
    public DeltaRebuildResult TryApply(World world, ReadOnlyMemory<byte> packet,
        out ReplicationPacketId acceptedId, out ReplicationPacketId? missingBaseline,
        out string? error);
    public void Reset();
}

// KhaozEngine.Netcode, with no Replication or backend dependency.
// Additive optional INetTransport method. Zero means unknown.
int MaxUnfragmentedPayloadBytes(NetConnectionId connection,
    NetChannelReliability reliability) => 0;
// NetClient exposes the server limit, NetServer exposes the corresponding slot limit.
```

`BuildRebuildFor` retains at most one unsent candidate per slot. `RecordRebuildSent` commits it to sent
history only after a successful routine send or after all keyframe chunks were handed to reliable
transport. Nothing may acknowledge an unsent candidate. A repair candidate is pinned while being sent.
V2 packet ordering is independent of the legacy signed counter, retaining the existing
shared-once-per-world-per-tick capture property. The exhaustion lifecycle still resets the writer's
global legacy capture counter and all its state after affected sessions disconnect. A reset cannot
reuse an old v2 epoch. NetWorld's lifecycle owner checks `LegacySequenceExhausted` before capture,
ends those connections through the host's disconnect/leave path, verifies the affected session set is
empty, calls `ResetAfterLegacySequenceExhaustion()`, and then allows fresh admission. Standalone owners
perform the same sequence around their own transport/receiver lifetimes. Projection retention occurs
after viewer filtering.

Replication owns byte-state reconstruction, capture/diff, and publication. NetWorld owns negotiation,
local-net-id/movement-ack envelopes, repair scheduling, connection lifecycle, and reliability choice.
Netcode owns the existing byte transport seam and generic reliable fragment core. LiteNetLib stays an
opt-in backend. No new Replication-to-Netcode edge, backend type leakage, umbrella membership, renderer,
or game-specific authority is proposed. Public options in NetWorld can compose the engine limits and
tick scheduling knobs without making Replication know transport channels.

## Acceptance evidence required before adoption

The proposal requires future acceptance tests, not test execution in this documentation task. Place
headless replication and NetWorld integration cases in `KhaozEngine.Server.Tests`, beside the cited
tests. Use the real `WorldClient`, `WorldServer`, and `ShardedWorldServer` paths, with a small deterministic
`INetTransport` decorator over the existing in-memory endpoints. `RawDeltaClient` is useful for wire
assertions but cannot substitute for prediction/presentation acceptance.

The decorator has a finite table of packet ordinal, direction, frame kind, drop/delay/duplicate action,
and release time. Bound it to 64 queued frames and a two-server-tick maximum ordinary reordering delay.
Reliable events preserve order and eventual delivery, with explicit delayed delivery when modeling
underlying retransmission. Dropped routine v2 acks model true ack loss. Connection and terminal events
are preserved. No random loss, socket timing, sleeps, polling-until-success loops, or local stress run
is acceptance evidence.

Use finite event schedules with server simulation at 30 Hz, presentation at 60 Hz, and client command
production at 30 Hz with phase offsets 0, 1/4, and 3/4 of a server tick. Offset-zero is the control.
Each schedule has named timestamped actions and a fixed ending, for example 180 server ticks, with
recovery fault injection ending by tick 60. Client input production has its own accumulator and never
uses the server scheduler's accumulator. Poll and drain at the scheduled client/server events, then
sample `Snapshot()` after `AdvancePresentation` at every presentation event. A harness feeding input
only when the server accumulator ticks can hide a missing predicted state between ticks. Endpoint
position equality alone does not prove visible movement, heading, gait, or reconciliation behavior.

| Acceptance case | Fault schedule and real observable |
|---|---|
| Reliable default reversion | Delay replication ack delivery while values go `1 -> 2 -> 1`, flags turn on/off, a component is removed/re-added with its original bytes, and a zero-byte tag toggles. Both standalone writers and both NetWorld servers return exact state with the unchanged reliable reader. Headers name last-sent projections. |
| Unreliable reversion | Acknowledge initial state, deliver intermediate changes, drop their routine acks, then deliver a baseline-relative empty delta for the reversion. Sample authoritative bytes and the real client's public component reads. They equal the reconstructed baseline, including owner-only fields. |
| Unreliable presence | Exercise absent/enter/leave, present/leave/re-enter unchanged, component absent/add/remove, and component present/remove/re-add. Deliver and drop intermediate packets in separate finite cases. After acceptance, entity membership, component membership, and payloads equal the server viewer projection. |
| Reorder and duplicates | Delay ordinal 4 behind 5, duplicate 5, then deliver 4. Assert no rollback, repeated reconcile, extra interpolation sample, or repeated ingest count. Repeat across `uint.MaxValue -> 0`, with near-half-range and retired epochs rejected. |
| Ack loss and pruning | Drop three routine acks and reorder two later ones. Then suppress routine acks through the 31-send window. Assert exact ack promotion rules, count/byte ceilings, a reliable barrier, and bounded convergence after faults end. Force a client baseline miss and verify no partial application or incompatible-version disconnect. |
| Viewer policy and handoff | Two real clients see owner-specific components and whole-entity visibility independently. Flip hide/show inside the fault window, cross a shard boundary, and reuse a disconnected slot. Inspect outgoing bytes for private/server-only leakage, plus stable net-id identity across handoff and removal after a newer hide. Old epochs cannot affect the new session. |
| Prediction phase | At each named phase offset, walk, turn, stop, and teleport with catalog-equivalent pace cases supplied by the consumer. Deliver a stale packet with an older movement ack. Assert predictor pending-command pruning uses only accepted frames and local rendered motion is present before the next server tick. Current D's approved predicted-state accessor supplies authoritative comparison, with no new API here. |
| Remote presentation phase | Drive a real remote walker and turner while dropping one state packet, delaying the next, and repairing. Assert the accepted ids and reconstructed positions, then the fixed-delay rendered samples, hold flags, heading, and movement state. Pin expected hold frames from the exact schedule. Do not promise an interpolation bracket while loss starves it. |
| Packet boundary and repair | Inject a tiny transport packet limit, test lengths at the cap and one byte over, and cross the projection/count limits. Assert every unreliable send fits, oversized state starts one frozen reliable keyframe, chunks fit, partial state stays unpublished, and no delta resumes before its exact keyframe ack. Change visibility during the barrier. |
| Reliable messages during faults | Send ordered game events and notices during a lossy delta/repair schedule. Assert their existing reliability choice, once-only ordered delivery, and intact payloads. Replication recovery cannot dispatch a chunk as a game message. |
| Marker-prefixed movement | In three separate fresh joined sessions, send one `MoveProtocol.EncodeMove` using sequence `0x0000A1C5`, `0x0000A2C5`, or `0x0000A3C5`, with finite nonzero movement and yaw. Run one scheduled receive/simulation step on each NetWorld server. Assert all new control decoders return unclaimed for length 18, the command reaches `commands.Store`, and its movement ack/position prove it was consumed. These are explicit initial sequence values, with no 41,000-tick setup. |
| Control-length alias boundary | Send an 18-byte marker-prefixed buffer whose move fields are valid, even when assembled as a sender-intended truncated control. Assert ordinary movement acceptance. Repeat with NaN/Inf move fields and require ordinary malformed-move rejection. For each new control family, send a recognized malformed non-18 length, including 19-byte bad-version repair and overlong ack. Assert rejection without `commands.Store`, mode transition, ack promotion, or repair creation. Valid 11-byte acceptance, 14-byte ack, and 19-byte repair dispatch only to their intended handlers. |
| Global legacy exhaustion | Seed each writer's counter at `int.MaxValue - 1` through an internal test seam with two populated slots and retained history. Serve `int.MaxValue`, verify exhaustion and no further increment, then verify `Forget` alone does not reset it and reset before exhaustion is rejected in a separate fresh fixture. End every affected connection, invoke the exact global reset API, and assert zero counter plus empty shared/history/per-slot state. Fresh receivers get capture sequence 1/full baseline -1, and fresh v2 sessions use a new epoch. Use a finite handful of capture steps. |
| Mixed capability and malformed input | Exercise default/default, opt-in/default, default/opt-in, and opt-in/opt-in peers at the current builtin generation. Require reliable fallback for a peer without the new capability, explicit mode refusal on incompatible budgets, and existing rejection on builtin-generation skew. Recognized malformed controls with lengths other than 18 do not fall through to movement. All 18-byte frames receive ordinary movement validation, irrespective of sender intent. Malformed component frames cannot publish partial state. |

Each integration case records requested/selected mode, epochs, baseline ids, accepted ids, movement ack,
client and server phases, pending command count, raw authoritative position, rendered position, hold
flag, and cache/packet maxima. Exact component and membership equality is mandatory at acceptance.
Prediction/render tolerances derive from approved movement tuning and the fixed time schedule before
coding the assertions. Missing samples and skipped cases fail evidence collection. A later game parity
pass supplies gameplay animation selectors and pace data. The engine proposal cannot approve their
appearance or the owner's final P8 playtest.

## Decisions still required

| Owner decision | Recommendation and consequence |
|---|---|
| Delivery model | Select A, retaining last-sent reliable and named-baseline unreliable contracts. Selecting B requires an explicit legacy migration and wire-generation decision. |
| Legacy standalone contract | Approve `WriteFor` as a reliable send commitment, additive per-slot `Forget` on `ServerReplicator`, and the global exhaustion lifecycle/API: end every affected connection, create fresh receivers, then reset the global counter and all writer state. Consumers that intentionally discard built deltas must adopt the reset/commit contract. |
| Protocol extension | Approve negotiated format 2 with capability-gated new kinds and current builtin generation retained. Recheck free discriminator values before implementation. |
| Budgets | Approve or replace the concrete cache, metadata, keyframe, packet, ack-window, and chunk-rate values above after one bounded consumer size characterization. No silent limit increase. |
| Ack and recovery policy | Approve coalesced repeated unreliable routine acks, reliable barrier acks, no mandatory periodic keyframe, and one active repair per viewer. |
| Recovery/capacity failure | Approve typed failure plus disconnect at the proposed deadline and impossible projection size, or specify a bounded reliable fallback. Existing game reconnect policy alone does not choose this. |
| Visibility revocation | Accept current presentation-gate semantics during a frozen keyframe, or require cancellation and a revised bounded barrier policy. Previously sent bytes cannot be recalled. |
| Adoption and release | Root reviews the proposal, owner approves a written spec, then a separate implementation plan can be prepared. Engine release and Grimhollow P8 adoption remain their owners' actions. |

No release version is reserved, no issue is closed, and no production freshness or memory measurement
is claimed. The selected contracts, limits, and acceptance schedule need written review before this
proposal can become an approved specification.
