# Journal admitted state design

**Status:** In flight. Extends
[`DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md`](DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md) sections 4.3, 4.4, 9
and 11. Engine program [#868](https://github.com/APKiwiOrg/KhaozEngine/issues/868). Consumer decision record
[Grimhollow #170](https://github.com/APKiwiOrg/Grimhollow/issues/170), taken by the owner on 2026-09-13. Shipped
API and usage live in the
[`KhaozEngine.WorldStore` package README](../../KhaozEngine.WorldStore/README.md) and
[`USING-KHAOZENGINE.md`](../USING-KHAOZENGINE.md). This document keeps the rationale.

## 1. Decision

`MutationJournalExecutor` gains an admitted state layer. An accepted operation changes the executor's live
admitted view of every stream it touches at the moment of admission, and the durable commit follows behind it.
A consumer validates, plans and presents against that view on the simulation thread, on the tick the action
happened, without waiting for a database round trip and without reading the store.

Three rules bound it:

1. The journal is still the only durable record. The admitted view is memory in front of it, never beside it.
2. The server is still the authority. What moved is when the accepted result is shown, not who decided it.
3. An operation the store refuses is corrected, not forgotten. The executor rolls the admitted view back, refuses
   everything queued behind the failure without sending it to the store, and hands the consumer the list of
   sections to resync.

An operation may opt out with `PresentAtCommit`, which keeps today's contract for value that moves between
players.

## 2. Why this exists

Section 4.3 of the journal design has the host validate against committed state, submit, and apply only when the
completion is drained on a later frame. That order is correct for durability and wrong for feel. Every item, coin
and experience change in Grimhollow arrives at least one commit latency after the click that caused it, and the
per-stream reservation means a second click inside that window is refused with `StreamBusy` and dropped
([Grimhollow #167](https://github.com/APKiwiOrg/Grimhollow/issues/167)). A player chopping a tree while an
experience award is in flight loses the chop. The consumer worked around the same wait once already with a
per-blow combat suspension ([Grimhollow #166](https://github.com/APKiwiOrg/Grimhollow/issues/166)), and it reloads
a whole player journal to build a loot claim because the committed copy is the only copy it can read
([Grimhollow #169](https://github.com/APKiwiOrg/Grimhollow/issues/169)). Ruinborne runs the same host shape and
inherits the same lag.

The source game this lineage comes from does not wait. It shows the item, then persists. The gap it accepts is a
process loss between the two, and the value it buys is that no player action is ever refused because the disk is
busy.

Every consumer that needs this needs exactly the same machinery: an ordered queue per stream, a live overlay of
the projection sections, and a correction path when the store says no. That is engine work by the engine-first
rule, not one game's queue.

## 3. Goals and non-goals

### 3.1 Goals

- Admission is a usable answer. A consumer can present the result of an accepted operation on the tick it was
  accepted.
- A second operation on a busy stream queues in admission order instead of being refused.
- Validation reads a live view rather than the store. No consumer needs a database read to build a plan.
- A commit failure produces one bounded, explicit correction rather than silent divergence.
- The engine plays no sound, sends no packet and knows no item. Presentation stays game-side.
- Submit, completion and correction stay O(mutations in flight), never O(streams) or O(connected players).

### 3.2 Non-goals

- Client prediction. The client still sends intent only.
- Coalescing progression commits. How coarse an operation is remains a consumer choice.
- Cross-host admitted state. The view is one process, like the live state it fronts.
- Surviving a crash between admission and commit. Section 6 below accepts that loss deliberately.
- Reducing SQL round trips, which is [#867](https://github.com/APKiwiOrg/KhaozEngine/issues/867) and independent.

## 4. The model

### 4.1 Ordering

Each stream carries an ordered queue of admitted operations. `Submit` appends to every stream the operation
touches, so a multi-stream operation waits on all of them. An operation is sent to the store only when it is at
the head of every one of its queues, which happens when each operation ahead of it has reached a terminal result
and the consumer has acknowledged it. Acknowledgement stays the one release point, exactly as it is today, so
capacity and reservation accounting do not change shape.

`ExpectedVersion` for a queued operation is checked against the admitted head version of the stream, which is the
version the operation ahead of it will produce, rather than against the committed version. A chain of queued
operations therefore carries contiguous expected versions and commits contiguously. A mismatch is refused at
admission with `VersionConflict` instead of being admitted to fail at the store and cascade a correction, which
is the cheaper place to find a stale plan.

### 4.2 The live view

Per stream the executor keeps the committed version, the committed projection sections, the admitted head version
and the sections written by admitted uncommitted operations, layered over the committed ones in admission order.
A read returns the layered answer with a flag saying which layer it came from.

The executor never reads the store to build this. The consumer seeds the committed baseline with `SeedCommitted`
when it loads a stream, which is the same `JournalProjectionRead` it already performs at login, and the executor
advances the baseline as operations are acknowledged `Handled`. That keeps the store contract unchanged and keeps
the executor free of a read path it would have to make consistent with its own queue.

A stream nobody seeded is still tracked while it has admitted operations, because the queue needs a head version
either way. It adopts the first operation's `ExpectedVersion` as its baseline and is forgotten when its queue
empties, so the untracked path costs no memory per player and reports only what is in flight.

### 4.3 Presentation class

`JournalCommit.PresentAtCommit` marks an operation the consumer must not present early. It is a statement about
the consumer's own contract rather than a switch inside the executor: the operation still queues, still reserves,
still blocks what is behind it and still changes the admitted view, because the operations behind it must build on
what it did or the chain is not contiguous. What it changes is the consumer's reading of its own submission
result.

The owner's rule for it is value moving between players: a trade and a contested loot claim, section 11 of the
journal design. Those are the two cases where the crash window between admission and commit could mint value
across accounts rather than lose one player's own progress.

### 4.4 Correction

A terminal failure is a fatal store fault, a quarantine, a `VersionConflict` or an `OperationConflict`. On one of
those the executor, inside the same lock that queues the completion:

1. Rolls every stream the operation touched back to its committed baseline plus the operations still queued ahead
   of it, which is the state those streams would have had if the failure had never been admitted.
2. Refuses every operation queued behind it on those streams, transitively across the streams those refusals
   touch, with a terminal `SupersededByFailure` completion. None of them ever reaches the store, because an
   operation that is not at the head of all its queues has not been dispatched.
3. Attaches one `JournalCorrection` to the failed operation's own completion, naming the streams rolled back, the
   projection sections to resync and the superseded operation ids.

One correction per failure, carried on a completion the consumer already drains and acknowledges, rather than a
second completion with its own acknowledgement bookkeeping. The superseded operations each get their own
completion so their bytes and reservations are released through the one existing path.

A transient retry corrects nothing. The operation is still admitted, its view still stands, and the store is still
being asked. That is the fail-closed behaviour section 9 already specifies and it is unchanged.

A `Quarantined` acknowledgement supersedes the dependants and resets the touched streams to their committed
baseline the same way, without a correction record, because the consumer is already reloading those streams from
snapshot and tail before it accepts more work.

## 5. Public shape

Additive. Nothing existing changes meaning except that a busy stream now queues by default.

- `MutationJournalExecutor.SeedCommitted`, `ForgetStream`, `TryGetAdmittedProjection`, `TryGetAdmittedStream`.
- `JournalSubmission.AdmittedStreams` and `ChangedSections`, so the tick that submitted can present.
- `JournalCommit.PresentAtCommit` and `QueueBehindAdmitted`.
- `JournalExecutorOptions.StreamQueueDepth`.
- `JournalSubmissionStatus.VersionConflict`, `JournalCompletionKind`, `JournalCorrection`,
  `JournalAdmittedSection`, `JournalAdmittedStream`, `JournalAdmittedStreamHead`, `JournalProjectionSectionKey`.
- Metrics for admitted uncommitted depth, its per-stream peak, its oldest age, corrections and supersessions.

`StreamBusy` stays reachable two ways. A quarantined stream still refuses everything, because a queue behind a
blocked stream is a queue nobody can drain. And `QueueBehindAdmitted: false` on the commit is the explicit
opt-out for a caller that wants the old refuse-now answer, which is the right shape for an action a player would
rather see refused than see queued.

## 6. Crash semantics

Admitted uncommitted operations die with the process. The design already accepts this for client-originated work,
which resubmits by the same operation id after reconnect and resolves against the receipt if the commit did
happen. Server-caused work such as an experience award or a drop roll is simply lost, which is the trade the
owner took in the consumer decision: the exposure is losing the last fraction of a second of one player's own
progress, never duplicating value, and `PresentAtCommit` keeps cross-account value out of the window entirely.

`StopAsync` is unchanged in shape. Admission stops, the operations already dispatched drain for the grace period,
and every admitted operation is reported unresolved in submission order, which now includes operations that were
queued and never started. The host must not acknowledge those, so nothing behind them starts either.

## 7. Complexity

Admission touches the operation's own streams and its own projection writes. Completion and acknowledgement touch
the operation's streams and the head of each of those queues. Correction touches the failed operation plus the
transitive set of operations superseded by it, each once. All of those are bounded by the operation's own size and
by the in-flight set, never by the number of streams the executor knows or the number of players connected. The
per-stream depth gauge is a peak rather than a live maximum for exactly this reason: a live maximum would cost a
pass over every stream on every change.

## 8. Alternatives rejected

### Keep the commit-before-apply order and make the commit faster

This is [#867](https://github.com/APKiwiOrg/KhaozEngine/issues/867), and it is worth doing, but it cannot get to
zero. Even a group-committed round trip is tens of milliseconds away from a tick that has already drawn. It also
leaves the refusal behaviour intact: a faster commit still reserves the stream and still drops the second click.

### Let the game hold its own optimistic layer

The first shape considered, and it is where the consumer would have gone without the engine-first rule. Two games
would have written the same queue, the same overlay and the same rollback, and the rollback is the part that is
easy to write wrongly and hard to notice: a game that applies optimistically and forgets one correction path
diverges from the durable record silently, which is precisely the failure the journal exists to prevent. Doing it
once, next to the thing that knows when a commit failed, is the only place the correction can be complete by
construction.

### Present at admission for everything, with no opt-out

Rejected by the owner on the cross-account case. A trade presented at admission and lost to a crash between
admission and commit takes value out of one account and does not put it in the other. `PresentAtCommit` costs one
flag and keeps that window empty.

### Refuse a queued operation whose expected version does not match, at the store

Simpler to write and worse to run. It admits work that is already doomed, spends a round trip proving it, and then
corrects a chain that never needed to exist. Checking against the admitted head at admission turns the same
condition into one synchronous answer on the tick that caused it.

### A separate correction completion, not attached to the failed operation

It reads cleanly and it breaks the accounting: every completion in this executor belongs to an admitted operation
whose acknowledgement releases its bytes and reservations, and a completion with no operation behind it would need
its own lifecycle. The failed operation is already the thing the consumer is handling when it needs the
correction, so the correction rides on it.

### Roll the whole stream back to committed on any failure

The obvious rollback, and it is too coarse. An operation ahead of the failure that is still queued and still valid
would be discarded along with it, which turns one refused action into an arbitrary number of refused actions. The
rebuild replays what remains, so a failure refuses exactly the operations that depended on it.

## 9. Proof of completion

- Queueing replaces the refusal, and a chain of queued operations commits in admission order with contiguous
  versions.
- The admitted view reflects an admitted operation before its commit and the committed baseline after
  acknowledgement.
- A forced terminal failure mid-chain rolls the view back, supersedes the dependants without a store call and
  emits the correction with the right sections and ids.
- A `PresentAtCommit` operation still queues and still blocks what is behind it.
- The per-stream depth cap answers `Backpressure`, and a multi-stream operation queues on every stream it touches.
- Shutdown reports queued but never started operations as unresolved.
- The metrics move.
- The store conformance suite is unchanged and still passes against in-memory, SQLite and SQL Server, because no
  store contract moved.
