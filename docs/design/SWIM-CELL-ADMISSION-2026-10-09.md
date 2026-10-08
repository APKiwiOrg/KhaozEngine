# Leased cell import admission

Task 6 requires destination classification before restored or transferred movement is published.
The previous cell importer decoded directly into the live ECS. Refusing a later actor could leave a
visible prefix, and an environment refusal was indistinguishable from a corrupt saved blob.

## Decision

Scores run from 1 to 10, with higher better and equal weighting.

| Approach | Benefit | Cost | Atomicity | Lease ownership | Existing codec reuse | Simplicity | Total |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| Live decode followed by rollback | Smallest change | Exposes unclassified state before rollback | 3 | 5 | 9 | 9 | 26 |
| Private staging, one admission read, typed publication | No visible prefix and no second wire read | Adds a reusable staging boundary | 10 | 10 | 9 | 7 | 36 |

Use private staging. `SnapshotStaging` reuses the existing decoder and registered typed component
copies. Unknown extension frames remain opaque. Frame adaptation happens in staging. Existing live
NetIds are excluded before admission, preserving the stale-save backstop.

`CellSim.ImportAdmission` returns a thread-affine `CellAdmissionRead`. The NetWorld implementation
unions the bounded cold scopes for all imported movement actors, acquires one destination lease,
classifies every actor, and derives their movement state only in staging. `CellImportPurpose`
distinguishes cold restore/relocation from live transfer. A live transfer reclassifies footing and
water while preserving its pose, velocities, timers, accepted dry jump buffer and sampled climb state.
Cold settlement cannot be reused for that continuation because it intentionally consumes buffers. The captured source must
be the destination cell's physics source. Any failed actor refuses the entire batch.

Only an accepted batch is copied into the live world. Transient marks are installed before owned
registration. The admission read remains held through publication, marks and handoff acknowledgement.
No inventory or equipment mutation is part of classification.

## Refusal and retry

`CellRestoreResult.NeedsAdmission` distinguishes environmental unavailability or refused placement
from corrupt bytes. `CellPersistence` retains both original bytes and the already-migrated body,
retries once per drain, and holds the save/eviction fence. It does not repeat migrations, quarantine
the snapshot or replace it with an empty save. Direct `SaveCellAsync` also respects that fence.

An eviction cache retains its richer freeze and transient marks under a save hold until admission
succeeds. Falling back to the durable store on an environment refusal would lose entities that were
intentionally absent from that durable snapshot. Only a decode failure takes that fallback.

Received migrations remain pending while the source stays frozen. Repeated delivery of the same
frozen entity is deduplicated. A changed snapshot for the same pending transfer is invalid. Both
source and destination cells remain ineligible for eviction until the transfer finishes. Accepted
migration retains the existing acknowledgement/release handshake.

Immediate local relocation now uses the same destination admission with a typed Migrate-channel
copy. The source stays untouched until admission succeeds, then ownership changes entirely inside
the read. Explicit sharded teleports use it instead of temporarily writing far coordinates into the
source frame. The boolean player-placement seam lets record persistence retain its save guard and
opaque game blob until the placement or configured reset succeeds.

These are generic destination admission guarantees. Profile/nav equivalence, ordinary movement
compatibility, final review and native/game adoption remain unfinished work.
