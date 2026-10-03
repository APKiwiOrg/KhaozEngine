# Delta reliability acceptance evidence

Bounded, deterministic evidence for the delta reliability program
([#34](https://github.com/APKiwiOrg/KhaozEngine/issues/34),
[#1229](https://github.com/APKiwiOrg/KhaozEngine/issues/1229)): the corrected legacy reliable contract and the opt-in
format 2 acknowledged rebuild. The living contracts are in the
[Replication README](../KhaozEngine.Replication/README.md), the
[NetWorld README](../KhaozEngine.NetWorld/README.md), the [Netcode README](../KhaozEngine.Netcode/README.md) and
[USING-KHAOZENGINE.md](USING-KHAOZENGINE.md). The rationale is the
[approved design](design/GRIMHOLLOW-DELTA-RELIABILITY-DESIGN-2026-10-02.md).

This page records what the tests prove and one bounded size characterization. It claims no production freshness,
no measured memory, no safe area-of-interest population and no adopted consumer. Grimhollow P8 adoption, its catalog
parity, production capacity and the final appearance and playtest stay with the consumer owner.

## Finite acceptance proofs

Every case runs headless in `KhaozEngine.Server.Tests` on both `WorldServer` and `ShardedWorldServer`, with real
`WorldClient`s over the in-memory hub and a deterministic fault decorator (`DeltaFaultTransport`). Schedules are
finite tables of packet ordinal, direction, frame kind and action, bounded to 64 held frames and a two-tick delay,
with every fault ending by server tick 60 of a 180-tick run. Server simulation runs at 30 Hz, presentation at 60 Hz
and client input at 30 Hz on its own accumulator, at phase offsets of 0, 1/4 and 3/4 of a server tick. There is no
random loss, socket timing, sleep or retry loop.

| Requirement | Proof |
| --- | --- |
| Reliable default reversion (#1229) | `ReliableDefaultDelayedAckRestoresAllEdges`: values going `1 -> 2 -> 1`, flags, component removal and re-add, and a zero-byte tag all return exactly through the unchanged reliable reader while acks are delayed. Headers name last-sent projections. |
| Unreliable reversion and presence | `AckRelativeEmptyDeltaRestoresAllEdges`: dropped intermediate acks, then a baseline-relative empty delta restores the baseline, owner-only fields included. Presence edges reconstruct to the server viewer projection. |
| Reorder and duplicates | `Ordinal4After5AndDuplicate5NeverReingests`: no rollback, repeated reconcile, extra interpolation sample or repeated ingest, repeated across `uint.MaxValue -> 0`, with half-range and retired epochs ignored. Each stale frame arrives alone in its poll. |
| Ack loss and pruning | `LostAndReorderedAcksRemainBounded` and `PrunedClientBaselineIsRecoverable`: exact ack promotion, count and byte ceilings, the 31-send window forcing one reliable barrier, and a client baseline miss recovered without partial publication or an incompatible-version disconnect. |
| Viewer policy and handoff | `OwnerVisibilityHandoffAndSlotReuseRemainScoped` and `VisibilityHideDuringFrozenKeyframeRemovesOnResume`: owner-scoped bytes, hide and show in the fault window, a shard crossing with a stable net id, slot reuse on a fresh epoch, and a hide during a frozen keyframe removed on the first resumed projection. |
| Prediction phase | `PredictionWalkTurnStopTeleportAtEachPhase`: walk, turn, stop and teleport at each phase, with prediction pruning only on accepted frames and local motion rendered before the next server tick. |
| Remote presentation phase | `RemoteWalkerAndTurnerRenderAtEachPhase`: accepted ids, reconstructed positions, fixed-delay samples, hold flags, heading and movement state against an independent oracle and hand-pinned hold frames. |
| Packet, keyframe and metadata boundaries | `PacketAtCapAndOneByteOverSelectCorrectPath`: datagrams at the cap and one byte over, caps 512 and 281, unknown and infeasible (280) limits, a limit drop during repair, a 64 KiB keyframe and one byte more, and the frame cap and one more. |
| Reliable messages during faults | `ReliableNoticesAndGameEventsSurviveDeltaFaults`: game events and notices keep their reliability, arrive once and in order, and no replication chunk is dispatched as a game message. |

Earlier task suites prove the rest of the traceability table: legacy exhaustion and restart, the four-way capability
matrix and generation skew, marker-prefixed MOVE aliases and malformed control lengths, malformed component frames,
typed deadlines and send failures, and the LiteNetLib packet-size query. The task-by-task record is the Outcome
section of the [implementation plan](superpowers/plans/2026-10-03-grimhollow-delta-reliability.md).

## Consumer size characterization

`DeltaConsumerSizeCharacterizationTests.ConsumerProjectionSizeCharacterization` serializes four fixed viewer
projections through NetWorld's real built-in codecs plus opaque payloads of the consumer's current extension widths.
An independent length calculation from the source widths and the documented layout must equal the real encoder, and
its capacity decision, computed from those lengths and the approved caps, must equal the decision the real writer and
the real `RebuildServerStream` make. The real writer and stream stay the enforcement oracle.

It characterizes the proposed NetWorld adaptation of the consumer's current extension bytes. The consumer builds a
tile registry today, so this is an adaptation estimate, not a production P8 capture and not an approved entity
count. It is not a benchmark, a throughput claim or a randomized AOI sweep.

### Fixture

- **Source widths.** Rechecked field by field against `Grimhollow.Shared/GrimhollowProtocol.Components.cs` as last
  changed in Grimhollow `a4557c8c`: MonsterKind 1 byte, ActorAction 30, ActorWorn 12, ActorFanfare 2,
  ActorVitalEffect 4, DurableLootSource 16 and CarcassState 50. Type ids start at `TileProtocol.FirstGameTypeId` in
  the consumer's order. Only widths are copied, payloads are opaque bytes. NetWorld built-ins are the real codecs:
  position 16 bytes, MovementState 56, PlayerIdentity a 2-byte length then UTF-8, MovementOwnerState 8 on the owner's
  wire only.
- **Entities.** The first visible entity is the viewer's own player. The rest cycle player, monster, carcass. Every
  player and monster carries the full actor mix (ActorAction, ActorWorn, ActorFanfare and ActorVitalEffect), a
  monster adds MonsterKind, and a carcass carries CarcassState and DurableLootSource. Every player name is the
  32-byte UTF-8 string `Ærin Þorvald of the Grimhollow`, the longest the fixture uses.
- **Changing state.** Every player and monster changes position and movement state. Carcasses stay put.
- **Caps.** The approved defaults: 512-byte packets, 64 KiB keyframe objects, 1,024 entities, 16,384 frames, 2 MiB
  retained bytes, a 31-send no-ack window and four chunks per tick of 489 bytes.

### Columns

- **Payload B** is component payload bytes in the viewer projection, frame headers excluded.
- **Keyframe object B** is the complete keyframe, 12-byte envelope included, as the stream declared it in its chunk
  headers. **Chunks** and **Chunk ticks** are the reliable chunks the stream sent and the replication ticks they took.
- **Changing datagram B** is the transport payload of the first routine delta after every actor moved, session byte
  included, from the real encoder.
- **Retained** is what one writer slot holds once 31 committed sends went unacknowledged: projections and their
  distinct reachable backing bytes. The writer then requests repair.
- **Decision** is `FitsDelta` (the changing datagram goes out unreliably), `OversizeDeltaKeyframe` (the stream sends
  a reliable keyframe repair instead) or `CapacityExceeded` (a typed terminal failure).

### Result

Command, run once to GREEN after implementation, from the worktree root:

```sh
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~DeltaConsumerSizeCharacterizationTests' --logger 'console;verbosity=detailed'
```

Recorded on `feature/grimhollow-delta-networld` at commit `4f4a87853` (the test as run, on engine 20.20.0 staging),
Release configuration, .NET 10.0.12, xUnit 2.9.2, Apple silicon macOS. Exit 0, 1 of 1 passed in 373 ms. Independent
and actual sizes are equal in every row.

| Row | Visible | Frames | Payload B | Keyframe object B | Chunks | Chunk ticks | Changing datagram B | Retained projections | Retained B | Independent | Actual |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| owner-only | 1 | 8 | 162 | 235 | 1 | 1 | 131 | 32 | 5184 | FitsDelta | FitsDelta |
| party-32 | 32 | 185 | 3886 | 4892 | 11 | 3 | 2042 | 32 | 124352 | OversizeDeltaKeyframe | OversizeDeltaKeyframe |
| crowd-128 | 128 | 729 | 15310 | 19196 | 40 | 10 | 7866 | 32 | 489920 | OversizeDeltaKeyframe | OversizeDeltaKeyframe |
| town-256 | 256 | 1453 | 30507 | 38230 | 79 | 20 | 15601 | 32 | 976224 | OversizeDeltaKeyframe | OversizeDeltaKeyframe |

A metadata-only boundary uses 1,024 entities of sixteen synthetic zero-byte extension tags each, outside the
consumer's id range, and the same with one more tag on the first entity:

| Boundary | Entities | Frames | Keyframe object B (independent) | Keyframe object B (stream) | Independent | Stream | Writer |
| --- | ---: | ---: | ---: | ---: | --- | --- | --- |
| tags-at-cap | 1024 | 16384 | 64550 | 64550 | FitsDelta | FitsDelta | FitsDelta |
| tags-one-over | 1024 | 16385 | 64553 | refused | CapacityExceeded | CapacityExceeded | CapacityExceeded |

### Reading

- Every complete projection in the table fits the approved keyframe, entity, frame and byte caps. The largest
  keyframe, 256 visible, is 58 percent of 64 KiB and takes 20 replication ticks at four chunks per tick.
- Only the owner-only row fits a routine datagram. At 32 visible and above, a tick in which every actor moves
  produces a delta of 2 to 16 KB, which the stream never fragments, so each such tick becomes a reliable keyframe
  repair. That is correct and bounded, but it removes the bandwidth benefit of the unreliable stream. With movement
  state alone at 91 bytes per moving entity on the wire, about five moving entities fill a 512-byte datagram.
- Writer retention at the window stays under 1 MiB for 256 visible, within the 2 MiB budget, so no committed send is
  pruned for bytes before the window fires.
- The frame cap is exact: 16,384 frames are served and one more is refused by both the writer and the stream.

### Consumer adoption constraints

These are recorded results, never permission to raise a limit. A consumer adopting format 2 for P8 must, before
switching:

- Keep its routine changing state per viewer within the packet cap, for example by a smaller interest radius, by
  replicating less per moving entity, or by accepting reliable deltas for dense views.
- Measure real P8 projections on its own registry and catalog, because these widths are an adaptation of today's
  tile-registry extensions.
- Treat a `ReplicationCapacityExceeded` disconnect as a content or interest-policy fault to fix, not a cap to raise.

## Final verification

Run once on `feature/grimhollow-delta-networld` after merging current `origin/main` (merge `952407a53`, with the
characterization test at `4f4a87853`), each command through the shared build slot:

| Command | Exit | Result |
| --- | ---: | --- |
| `dotnet build KhaozEngine.slnx -c Release` | 0 | 0 warnings, 0 errors |
| `dotnet test <each of the 29 test projects> -c Release --no-build --filter "Category!=LiveSocket"` | 0 each | 24,921 passed, 0 failed, 1,325 skipped, 26,246 total |
| `KhaozEngine.Server.Tests` with `Category=LiveSocket` and the four named NetWorld and LiteNetLib classes | 0 | 6 of 6 passed |
| `check-dashes.sh --tree`, `check-prose.sh --tree`, `check-file-size.sh --tree`, `check-agent-instructions.sh --tree`, `bash check-doc-versions.sh` | 0 each | Clean |

The suite ran one test project per call so that no single call could outlast the slot. Skips are the repository's
existing GPU, SQL Server and environment-gated facts.
