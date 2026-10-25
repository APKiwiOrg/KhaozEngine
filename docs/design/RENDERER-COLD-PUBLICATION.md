# Renderer cold collection and publication

Status: implemented construction capture, bounded compiler replay and directory verification.
Headless correctness does not establish native correspondence, native payload fit or a timing result.
[Renderer performance evidence](../RENDERER-PERFORMANCE.md) owns use, analysis and comparison.
[Cross-platform admission](../CROSS-PLATFORM.md#paid-hosted-renderer-performance-mode) owns execution approval.

## Construction and correspondence, CP-1

The collector observes actual shader, layout and pipeline factory attempts, including lazy and
specialized creation, failures, repeats, unused pipelines and bind membership. It copies nested
mutable inputs before forwarding each original call once. Native returned objects remain on the
recording path. Detached recipes survive disposal. A manual renderer list cannot prove completeness.

Recipe hashing and deduplication run outside measured spans. A digest collision requires value
comparison, and source equality alone cannot merge recipes. Every occurrence retains the logical key
`(operationOrdinal, kind, creationOrdinal)`. Cross-run matching uses those keys rather than a positional
unique-recipe index. Changed source/artifact bytes are informative only after each run proves its own
production/replay correspondence and equal logical membership, layouts, code generation and settings.

Measured first creation remains charged to its raw frame and refuses steady-state promotion. There
is no observer-cost subtraction, hidden capacity growth or move into warm-up. Bind membership uses
preallocated state, not a compiler report read. Capacity exhaustion freezes incomplete capture while
the triggering factory call still forwards once. Work stops at the next safe boundary.

## Immutable settings, CP-2

Resolve backend settings once before production creation. Production explicitly requests compiler
metadata off and runtime invocations off. Replay enables compiler metadata only, through the same
native creation/adoption gate with no backend fallback or environment mutation. The stable adapter
key and driver must match. A display name alone is insufficient adapter identity.

Requested configuration and actual effective configuration are separate. Replay may differ only in
the admitted metadata request, supported executable feature and corresponding capture flag.
Runtime invocations, validation/debug state, recording choices and code-generation options cannot
differ. Effective deltas are checked structurally, not waived because configuration hashes differ.
Missing optional-settings support refuses the collector. Ordinary public device creation retains
its own defaults and fallback contract.

## Typed identity, CP-3

`GpuDiagnosticDigest` requires hash, provenance, status and reason fields even when nullable.
Available means a lowercase 64-digit SHA-256 of actual nonempty bytes, nonempty provenance and no
reason. Unsupported/Invalid means null hash and an explicit cause. Malformed companions do not erase
valid stage identities or exact typed statistics.

Stage source uses glsl.utf8/1, emitted Metal source msl.utf8/1 and native artifacts spirv.bytes/1 or
dxbc.bytes/1. Metal MSL is source, never a native artifact. File checksums cannot replace artifacts.
Graphics stages retain backend labels and order, and compute retains its resolved local size.
Production and replay compare source, layout, resolved state, code generation and primary-backend
artifacts, including per-stage aggregates. Compiler nondeterminism refuses without retry.

Canonical domains include pipeline-stage-set/1, pipeline-code-generation/1,
pipeline-diagnostic-configuration/1 and renderer-construction-recipe/1 under khaozengine.
Encoding is ordered, length-prefixed UTF-8, explicit little-endian integers and IEEE float bits,
presence bytes for nullable fields and one-byte booleans. No culture formatting, normalization,
object identity or native address enters a digest. Invalid text, enum/state or overflow refuses.
Native Boolean, Int64, UInt64 and finite Float64 retain their exact type/value instead of passing
through frame-metric doubles.

The report schema is khaozengine.renderer-perf-report/2. Report/1 refuses without migration.
The raw vector format remains metric-vectors/2. Complete optional Unsupported evidence can certify.
Supported-but-omitted capture, missing identity, Invalid or incomplete evidence cannot promote.

## Production memory, CP-4

Retained observations carry current live counts and actual backend peak logical payload bytes.
There is no PeakLiveResourceCount. Exactly one unit-final observation follows session cleanup and
precedes production-context disposal. Replay memory cannot increase the production peak. Logical
payload, staging overlap, allocation, residency and budget retain their distinct support/provenance.

## Admission and artifacts, CP-5 and CP-7

All counters and byte arithmetic are checked before copying, allocation, native filling or string
conversion. These are refusal envelopes, not evidence that a native workload fits.

| Owner | Bound |
| --- | --- |
| Construction | 43 operations, 256 pipeline and 1024 total factory attempts per operation, 1024 unique recipes. Pilot uses its actual nine operations. |
| Retained logical encoding | 64 MiB split into 32 MiB sources, 16 MiB descriptors and 16 MiB events/occurrences/witnesses. Repeated occurrences still count. |
| Graphs | 32 layout sets, 128 elements/set, 32 vertex streams, 64 elements/stream, eight output/blend attachments, 1024 UTF-8 bytes/name, 1 MiB/stage source. |
| Options | 64 entries/table, 256 UTF-8 bytes/key and 4096/value. Four maximal escaped tables need 6,717,440 encoded bytes. |
| Vulkan hard safety | 64 executables, 256 statistics/executable, 16,384 total statistics, four enumeration attempts and 256-byte native name/description fields. |
| D3D11 | Two stages of 30 fixed reflection statistics, 4096-byte Creator/description bounds and 16,384-byte raw flags. |
| Publication | Root at most 268,435,456 bytes and depth 32. Root plus new compiler sidecars fits that same byte cap. |
| Completion | 16,777,216-byte reserve, including two 32-byte accounting slots. |

PrepareCollection measures the encoded retained root, inventory and completion shape before optional
replay. One read reservation is outstanding at a time. Reserve the exact frozen request before graph
creation, then commit actual encoded bytes. GPU readers receive count/text limits, not renderer
serialization or byte-budget policy.

For remaining allowance A after retained data, prior reports and completion reserve, Vulkan requires
at least 786,432 bytes. It chooses `T = min(16384, floor((A - 786432) / 4096))` and reserves
`R = 786432 + 4096*T`. The request is `(64,256,T,256,16384,4)`. D3D11 requires 1,048,576 bytes and
uses `(2,30,60,4096,16384,4)`. Only these primary profiles are accepted. A zero T can admit genuine
zero-statistics or Unsupported evidence, never omit a positive native count.

Vulkan count/fill retries stay within the same four attempts and frozen request. Recheck cumulative
remaining capacity on every changed count. Incomplete discarded buffers are not successful rows.
Keep complete returned subsets and earlier executable prefixes on later refusal. Bounds precede
allocation/fill and UTF-8 conversion. Unknown native totals remain null with a cause, not invented
zero. D3D11 enforces its fixed profile during reflection and refuses smaller incompatible requests.
Oversized vendor fields refuse instead of being truncated into apparently complete evidence.

Producers register sidecar kind, schema, safe relative path, length, SHA-256 and decoded counts.
Directory enumeration cannot invent expected evidence. Reopen bounded regular files and verify
containment, identities, aliases/links, schema, associations, counts, aggregates and actual combined
length. Parse alone is structural decoding. Live verification precedes completion sampling, and
offline/workflow verification repeats on received files. The stricter current handle/writer-exclusion
contract is in the living performance reference.

## One lifecycle, CP-6 and CP-8

One RendererPerfUnitBudget owns the original ColdStart, absolute deadline and cancellation through
production, cleanup, freeze, replay, publication, verification and stamping. Full schedule remains
16,944 frames over 43 operations. Pilot/full deadlines remain 600/1500 seconds, individual frame limit
10,000 ms and job caps 12/35 minutes. Collection adds no frame, calibration, query block or profiler.

Dispose all production owners before replay and all diagnostic graphs/context before certification.
Check budget before and after safe native/file boundaries. Native compiler/query/disposal calls are
not promised interruptible. Cleanup still attempts all safe releases after failure or deadline.
No replay starts after a workload failure. Incomplete collection retains its actual subset.

GpuDiagnosticFailure retains the originating stage and actual Exception reference. Only the documented
GpuDiagnosticFailureException transport expands its ordered events. An operational AggregateException
stays one original error. Repeated independent occurrences remain separate even when they share the
same object. Boundaries transfer newly owned events once, preserving the original error before later
cleanup/publication failures. Typed results retain partial reports, inventory and early evidence.

Serialized error text is a separate bounded projection. Exhaustion retains actual in-memory errors,
omitted counts and required escaped UTF-8 bytes, and refuses promotion. It cannot reconstruct or
replace the original exception. Publication owns its terminal check, failed-final correction and
bounded partial recovery. No later success stamp can hide a failure outside that owner.

Synthetic fixtures prove forwarding, accounting, bounded refusal, ordering and file integrity. They
do not prove actual vendor output size, native construction correspondence, elapsed cost or deadline
acceptance. No approval, retry, larger limit or performance claim follows from those fixtures.
