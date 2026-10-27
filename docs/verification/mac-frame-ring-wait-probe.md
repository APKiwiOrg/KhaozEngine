# Private Metal recording-wait archive

This never-merge archive measures ownership of existing counted CPU ring waits. It changes neither ring depth nor recording, submission, drawable, rendering, resource lifetime or exception policy. Assembly versions remain 21.13.0. It is a private measurement surface, not a proposed shipping API or an engine release.

## Enable and collect

The buffered recorder is configured once at startup with an absolute, fresh `KE_FRAME_DIAGNOSTICS_PATH`. Its existing `.start` trigger, excluded partial interval, armed baseline, ten-second duration, 4096-interval capacity and final atomic publication remain unchanged.

`KE_RING_WAIT_PROBE` accepts unset or `0` for off and `1` for on. Every other value, including an empty string, is a configuration error. It takes effect only with a configured recorder. An enabled configured recorder requires `KE_SHADOW_STAGE_MODE=counts` or `timings`. Initialization errors follow the recorder's existing explicit diagnostic and no-result path. Counts is the intended recording-wait capture mode. It does not add the shadow-stage timestamp instrumentation used by timings.

The parent prepares and audits a fresh immutable client, supplies the complete human launch command, waits for fresh readiness, and then runs one collector command. This implementation does not launch or kill clients. The command shape, after those gates, is:

```bash
python3 "$PROBE_ROOT/scripts/mac-frame-ring-waits/collect.py" \
  --preparation "$CAPTURE_ROOT/preparation.json" \
  --ready-process-id "$READY_PID" \
  --mode counts \
  --result "$CAPTURE_ROOT/recording-waits-01.json"
```

The collector audits the exact PID, process start and command, six critical mapped DLLs, code identities, payload bytes and modes, source hashes, 41 world inputs and foreground endpoints. It creates one exclusive trigger, waits at most 45 seconds for the existing recorder to finish, then repeats the identity and content checks. It never launches, profiles, reads back a client, kills a process or automatically repeats a capture. `/tmp` and `/private/tmp` are canonical aliases. Resolving an alias never permits a path outside the owned capture directory.

Result, trigger, collection metadata, temporary metadata and analysis paths must all be fresh. Before preflight, exclusive creation of collection metadata atomically reserves this invocation's paths. The metadata is a permanent filesystem claim with a collection GUID, not a runtime or build lock. A competing collector exits without overwriting the owner's evidence. An interrupted or failed invocation retains its claim, including an incomplete initial metadata write.

After preflight the collector rechecks result, trigger and other evidence paths, saves the completed preflight, then rechecks immediately before its sole exclusive trigger creation. A report published during preflight is refused. Metadata records the new trigger's device/inode identity and creation time. Acceptance requires observing that trigger absent before accepting the result, with ordered creation, consumption and result-observation timestamps. A previously armed or sampling recorder cannot consume the new trigger, so its report is refused while that trigger remains. The collector never removes an unconsumed trigger. Offline runtime acceptance requires the same ownership, preflight and consumption proof. Older collection metadata without it cannot establish complete attribution.

Failures retain result, trigger and metadata evidence. A new automatic capture is not a recovery step. Collector exits are 0 for complete audited evidence, 2 for explicitly limited evidence and 1 for invalid evidence or failure. A freshness or ownership refusal exits before the owner's failure/save path.

The preparation manifest must name `probeCommit`, `gameCommit`, `probeRoot`, `gameRoot`, `client`, `clientFiles`, `world`, `sourceHashes`, `gameSourceHashes`, `replacements`, `appCodeIdentity` and `engineCodeIdentity`. Counts are exactly 644 runtime files, 395 Content paths and 41 world inputs. File rows carry relative paths, SHA-256, byte size and mode. Sources are checked against both working bytes and the named committed bytes. Game HEAD may carry evidence commits, but the declared frozen game source and its working bytes must still match.

The required replacement closure is exactly Gpu, Gpu.Metal, Windowing, Render3D and Game.Render3D. Each replacement carries its source DLL path, SHA-256, MVID, assembly/file/informational versions and assembly references. Engine versions and references remain 21.13.0.0. The collector also checks the required dependency edges within this closure. Informational versions name the committed archive source. Grimhollow.dll is also a critical mapping and has its own frozen code identity. The collector rehashes all runtime files before and after, refuses symlinks and hardlinks, and checks critical mappings against their exact canonical client paths.

## Optional native observations

The existing device and command-list interfaces gain no required members. Render3D has no Metal dependency. The optional interfaces in `KhaozEngine.Gpu` are:

```csharp
public interface IGpuRecordingWaitSource
{
    Guid RecordingWaitListIdentity { get; }
    bool TryGetRecordingAttempt(out ulong attempt);
    bool TryGetRecordingWait(out GpuRecordingWaitReceipt receipt);
}

public interface IGpuRingWaitSource
{
    Guid RingWaitDeviceIdentity { get; }
    GpuRingWaitSnapshotStatus TryReadRingWaitTotals(out GpuRingWaitTotals totals);
}
```

Snapshot statuses are Unavailable (0), Available (1) and Contended (2). Absence of an optional interface is unsupported. A diagnostic access fault invalidates observation coverage without escaping into application execution.

`GpuRecordingWaitReceipt` contains a stable device identity, stable list identity, list-local attempt generation, captured device recording ordinal, segment and depth, counted waits, raw waited ticks, wait-call count and Stopwatch frequency. Identities are created once per allocator or list. Attempt and ordinal wrap to zero unchecked. Availability is independent of that numeric value.

The allocator's legacy `BeginRecording()` forwards to `BeginRecordingWithWaitReceipt(out receipt)`. The ordinal is captured at increment under the submit lock. Existing `blocked` and `waitedTicks` locals remain the accounting authority. The wait-call count advances beside each existing `WaitForValue` call. Multiple revalidations are one counted acquisition. A counted zero-tick wait is legal. No acquisition timestamp site, GPU operation or wait is added.

A successful allocator receipt is published only after patch replay and existing counter publication. The list adds its attempt and identity only after full Begin succeeds. Observation is available only while that recording is active. Nil native-buffer acquisition, throwing waits and failures after ring publication expose no successful or stale list receipt. End, failed End and disposal remove availability. Native-buffer acquisition is outside the measured ring scope.

`GpuRingWaitTotals` contains device identity, depth, frequency and cumulative signed 64-bit count and ticks. Reading it makes one nonblocking `Monitor.TryEnter` attempt on the existing submit lock and releases a successful hold in `finally`. It copies scalar data and calls no GPU API or callback. Both ring-acquisition publication and the depth-one device-write publication already hold this lock. The public counters and `WaitAccumulator` remain unchanged. Their separate floating-point public reads are context, not the exact reconciliation authority.

Scalar native receipt bookkeeping exists in this archive even when capture is off. Only enabled armed or sampling recorder states request snapshots and callback observations. The observer has a cost. These checks do not establish zero overhead.

## Callback ownership

`Frame.DeclareRecordingWaitPrefix(bool expected)` declares the actual optional Render3DSurface prepare helper. The historical split-active flag is only corroboration. Legacy callbacks have a not-applicable prefix. Suppressed callbacks are not rendered and open no expected recording.

`BeginRecordingWaitObservation(role, device, list)` returns a local ticket immediately before Open. `CompleteRecordingWaitObservation(in ticket)` copies the successful receipt immediately after Open, inside the prefix using scope or main try/finally. Tickets retain local references for identity checks. Buffered callback results contain values only.

The ticket must match epoch, sequence, role, device, list and unchecked prior attempt plus one. Duplicate declarations, roles, completion acknowledgements, late tickets, absent capabilities and observation faults remain explicit. Receipts are provisional until the success endpoint after render, End, submit, AfterSubmit, present and pacing. A submitted prefix cannot acknowledge a callback that later fails. Recording disposal and original application exceptions retain their existing behavior.

## Schema 4

The schema is exactly 4. All schema-3 cadence, shadow-stage, public counter and assembly identity fields remain. The old archive helpers and capture files remain historical inputs. The new helpers reject schema 3 and unknown schema versions.

The added fields are:

| Location | Value |
| --- | --- |
| `appCodeIdentity`, `engineCodeIdentity` | Assembly name, MVID and informational version, read outside sampling |
| `baseline.recordingWait`, `end.recordingWait` | Coherent endpoint status and nullable totals |
| `samples[].recordingWait` | Request, role status, callback values, start/end snapshots and interval remainder |
| `recordingWaits` | Independent coverage dimensions, role totals, window remainder and derived total milliseconds |

A recording-wait request has epoch, sequence, enabled and shadow mode. Callback values have declaration count, prefix expectation, acknowledgement count, prefix/main observation counts and observations, plus identity-violation state. Each observation has status, prior attempt and a nullable receipt.

Observation states are Missing (0), Pending (1), Available (2), NotApplicable (3), Unsupported (4), Unavailable (5), Fault (6), IdentityMismatch (7), InvalidReceipt (8), Duplicate (9) and Late (10).

Callback role states are Off (0), Valid (1), NotRendered (2), IncompleteCallback (3), MissingDeclaration (4), DuplicateDeclaration (5), MissingRole (6), DuplicateRole (7), IdentityMismatch (8), Unavailable (9), Fault (10), InvalidReceipt (11), AcknowledgementCardinality (12), PairingMismatch (13) and ModeChanged (14).

Boundary states are Off (0), Available (1), Unsupported (2), Unavailable (3), Contended (4) and Fault (5). A non-available boundary has no totals. An available zero is evidence, not a missing observation.

Reconciliation states are Off (0), Available (1), MissingBoundary (2), InvalidRoles (3), IdentityMismatch (4), InvalidTotals (5) and NegativeRemainder (6). Only Available has count and tick values. No invalid value is clamped.

For an interval, exact signed integer arithmetic subtracts prefix and main contributions from the coherent end-minus-start count and ticks. Whole-window arithmetic independently subtracts all role contributions from the two endpoint totals. Identity, clock and depth must agree across all available data. Missing interior snapshots remove the affected interval remainders without erasing valid role totals or valid endpoint-based whole-window arithmetic. Missing endpoints forbid window reconciliation. No nearby snapshot is substituted.

`validRoleRows`, `availableBoundaries`, `reconciledIntervals` and `window.status` retain the four coverage dimensions. `fullyAttributed` is conservative and requires all four to be complete. An interior gap can retain an Available window remainder while the report remains limited. The full callback count stays in the cadence denominator, including every invalid or missing observation interval.

Prefix and main count/tick totals are nullable when unobserved, not applicable or overflowing. An overflowing role total does not erase the other role total or observed role coverage. Their millisecond fields are totals, computed outside sampling as ticks times 1000 divided by frequency. A partial role total is not a complete-window mean. The public counter deltas preserve their previous definitions and denominator.

The unattributed counted remainder can include other recordings, depth-one writes and acquisitions whose later Begin or callback failed. A foreign wait can begin before the window and publish inside it. The remainder does not identify a thread or prove a particular source.

## Verification and limits

Deterministic fake tests own exact accounting, overlapping acquisitions, revalidation, failure publication, generation wrap, callback rejection, boundary contention, integer reconciliation and allocation proof. Held workers have explicit release and bounded join cleanup. IL checks retain exactly the two existing acquisition timestamp sites and the unchanged two depth-one write sites.

Strict native tests use `KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native KE_METAL_REQUIRED=1 MTL_DEBUG_LAYER=1`. They compare eight-frame prepared output and same-frame capture RGBA bytes with observation off and on, preserve draw order, and inspect real Metal validation evidence. Native tests may observe zero stalls. No native load is manufactured.

The analyzer independently checks exact integers, role cardinality, epochs, sequences, attempts, device metadata, both boundary copies, every coverage dimension and summary arithmetic. It rejects early or capacity completion presented as duration completion. Complete runtime acceptance also requires fresh collector identity evidence. Offline-only evidence is explicitly limited.

Every integer and enum used in acceptance rejects JSON booleans and floating-point substitutes, including equal-valued `sampleCount=2.0` and observation `status=2.0`. The following inventory matches the C# carriers and collector evidence. `integer()` enforces exact type and range. `exact()` compares independently computed ring summaries and remainders recursively with exact types.

| Fields | Integer contract |
| --- | --- |
| Schema, report PID, sample count, frequency | Schema exactly 4, PID 2 through signed 32-bit max, sample count 1 through 4096, positive signed 64-bit frequency |
| Baseline/end timestamps and sample start/end timestamps | Signed 64-bit |
| All endpoint/completed/boundary windows | Nonnegative signed 32-bit width, height and cap Hz, nonnegative signed 64-bit submission/composite counts |
| Device counters and public counter delta | Nonnegative signed 64-bit frame/drain/backpressure/acquire/deferred/outstanding counts, signed 64-bit outstanding change |
| Every shadow/ring request copy | Nonnegative signed 64-bit sequence, mode enum 0 through 2, accepted capture mode 1 or 2 |
| Shadow row/snapshot/coverage status, foliage origin | Status enum 0 through 9, origin enum 0 through 3 |
| Shadow callback, coverage and timing summaries | Nonnegative signed 32-bit producer count, coverage rows and timing sample counts bounded by 4096 |
| Shadow configuration | Signed 32-bit directional mode, cascade merge gap/resolution/count, point face resolution/rows/transient rows/budgets/filter |
| Shadow work counts | Nonnegative signed 32-bit request/touch/acquire/signature/selection/slot/row/rebuild/render/demand/draw/index/population/cascade/mask/span/instance counts and query calls, signed 64-bit issued-instance/stream-proxy counts and query exact-tests/accepted/sort-inputs |
| Shadow timings | Positive signed 64-bit frequency, nonnegative signed 32-bit timestamp reads, nullable nonnegative signed 64-bit ticks for every stage |
| Ring callback and observations | Role status 0 through 14, observation status 0 through 10 including not-applicable prefix, nonnegative signed 32-bit declaration/acknowledgement/role counts, unsigned 64-bit prior attempt |
| Ring receipts and every endpoint/interval boundary copy | Unsigned 64-bit attempt/ordinal, positive signed 32-bit depth, nonnegative signed 32-bit segment/wait calls, count 0 or 1, nonnegative signed 64-bit ticks, positive signed 64-bit frequency, boundary status 0 through 5 and nullable totals |
| Ring reconciliation, role totals and coverage summary | Exact independently derived signed 64-bit count/ticks or null, reconciliation status 0 through 6, row/boundary/interval counts bounded by the 4096 samples and 4097 boundaries |
| Collector and preparation evidence | Exact integer PID/focus endpoints, ten requested seconds, 644 runtime/395 Content/41 world counts, nonnegative signed 64-bit payload sizes, unsigned 64-bit device/inode identities, nonnegative signed 64-bit file sizes/timestamps |

Every declared Boolean used by acceptance requires an exact JSON `true` or `false` before truthiness or equality checks. Numeric and string substitutes, null and absent required Boolean fields are invalid. The bounded Boolean inventory is:

| Carrier and copies | Required Boolean fields |
| --- | --- |
| Baseline/end window and every completed/boundary window | `focused`, `minimized`, `renderSuppressed`, `vsync`, `rendererInitializesSwapchain`, `preparePhaseSplitActive` |
| Baseline/end and every sample's public counters | `hasValue` |
| Every shadow snapshot configuration | `hudTiming`, `terrainCastsShadows`, `cascadeCulling`, `gpuSkinning`, `pointEnabled` |
| Every non-null shadow work-count carrier | `hasPointRequests`, `pointAtlasAvailable`, `directionalActive`, `directionalRendered`, `directionalSkipped`, `rigidIndexBuilt`, `foliageIndexBuilt` |
| Every shadow callback | `completed` |
| Cadence summary | `allForegroundRenderedStableSize`, `cadenceCapped`, `bufferCapacityReached`, `capacityLimited`, `allPreparePhaseSubmissionsPaired` |
| Shadow summary | `comparable` |
| Every recording-wait row/callback request copy | `enabled` |
| Every recording-wait callback | `prefixExpected`, `identityViolation` |
| Recording-wait summary | `enabled`, `roleCoverageComplete`, `boundaryCoverageComplete`, `intervalCoverageComplete`, `fullyAttributed` |
| Collection evidence | `freshPaths`, `triggerCreated`, `ownershipReserved`, `preflightRechecked`, `triggerConsumed` |

Boolean fields have no nullable or default-on-missing path. Nullable containing carriers retain their existing availability rules. Milliseconds, interval seconds, cadence statistics and per-interval averages retain floating-point values. Nullable unavailable timings, boundaries, public counter delta, role totals and remainders remain distinct from measured zero. The helper tests round-trip integer and Boolean mutations, exercise both capture modes and retain valid zero/nonzero remainders, legacy prefixes, owned collection and limited interior/endpoint coverage. In particular, `completed=1.0`, `comparable=1` and `capacityLimited=0` are invalid evidence and cannot advertise full attribution.

These are CPU wait measurements and callback cadence. They establish neither GPU completion, physical display cadence, production overhead, speedup, a sustained frame-rate target nor a recommended ring depth. Parent-owned independent review, full frozen-solution verification, archive push, immutable client preparation and fresh human readiness remain separate gates before the one runtime capture.
