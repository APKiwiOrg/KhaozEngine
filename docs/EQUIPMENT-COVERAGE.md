# Equipment coverage

Equipment coverage applies an offline-verified recession table to the current skinned host pose. The
consumer supplies an opaque animation-state key. Runtime selects a row and carries its scalars through
fixed refinement. It performs no exact construction, correction, filtered selection, continuous
certification or runtime membership test. The exact constructor and validator are offline tooling.

Headless reference, binding, publication and tooling tests are implemented. The separately approved
native units G1 to G5 have accepted evidence, listed under [verification boundaries](#verification-boundaries).
Their cost results describe a synthetic workload on two devices, not a consumer frame rate. No measured
memory footprint is claimed here. Verification at samples is not a continuous proof.

## Assets and shared identity

Load every annotated host material slice from the same source and upload it to the same `Scene3D`.
Rigid equipment may use legacy loading or incremental resource uploads. Both publication paths retain
the same immutable geometry witness. Incremental readiness releases its source arrays before publication,
while the witness stays charged to the resource until release and retirement. Failed publication, release
and legacy unload remove the generation-keyed witness, so slot reuse cannot inherit it.
`CreateHostCoverage` validates completeness, the exact skeleton owner and immutable upload evidence.
Scene-issued `SkinnedMeshHandle` values include an opaque scene stamp. Publicly constructed handles are
unowned and cannot enter coverage, even when their slot and generation match. Ordinary draws retain
existing slot/generation admission.

Host sidecars use `HostCoverageMapJson`, and rigid equipment companions use `EquipmentCoverageJson`.
Companions bind exact attachment bytes to the host map and name authored command IDs, host regions,
closed displayed solids and support ranges. Commanded parts must be rigid and include every material
slice. Uncommanded garments keep the ordinary skin compatibility rules.

`CoverageRecessionTable` owns immutable entry-major binary32 scalar rows, one shared topology-owned
representative vector and a sorted index from `CoverageStateKey` to entry. Representatives identify
refined vertices in their own position groups, with -1 for inactive groups. Runtime binding checks
ownership and activity against the complete configuration. It never chooses representatives per pose.
KERT format 1 is the little-endian asset format. See the
[Render3D asset contract](../KhaozEngine.Render3D/README.md#recession-table-assets) for byte layout,
limits and public `CoverageTableRefusal` diagnostics.

Every table carries six SHA-256 identities: exact host bytes, semantic host map, complete equipment
configuration, fixed refinement, retained carry arithmetic and the consumer's state schema.
`CoverageConfigurationIdentity.Compute(items, inverseBind)` is the single canonical configuration
producer used by runtime and tooling. It hashes a versioned domain, items sorted by exact attachment
and companion digests, parts sorted by ID with host joint name, socket and body-alignment words, then
skin-order inverse-bind words. Strings are length-prefixed UTF-8 and numeric words are explicitly
little-endian. Exact attachment and companion digests bind static parts, assets and command values.
The configuration manifest's paths and JSON formatting, current poses, actor incarnation IDs and
transition weights are excluded. Reformatting an attachment or companion file changes its exact digest
and therefore the identity.
Compatible actors therefore share one table and immutable GPU topology/representative/scalar uploads.
Arithmetic identity hashes the embedded versioned carry contract, shared by the tool and runtime.
Unrelated binary changes preserve compatibility and identity does not read an assembly file or location.
Independent frozen vectors guard scalar rounding, skin composition, refinement, representatives,
transition weighting, current recession direction and normal/tangent gathers. An incompatible arithmetic
change requires advancing the contract version and its descriptor. Exact tool and Render3D assembly
hashes remain in the evidence manifest as producer provenance only.

Carry arithmetic contract version 2 keeps position skinning, refinement, recession and alias sharing
exact, including every published position word. Normal and tangent math is native GPU binary32, with
precise (NoContraction) cancellation-sensitive operations. Direction failures cannot set the actor
failure flag. One parameterless position authority checks exact source, uncovered and transported
position records and positional aliases before the publication dispatch. Only position problems select
the complete uncovered pose. The five stages, slots, history and overflow preflight are unchanged.

A cancelled, folded, misaligned or non-finite direction recovers locally. The shared chain is the valid
candidate, then the finite uncovered current-pose direction, then the finite authored direction, then
unit Z for normals or unit X for tangents. Generated vertices use their first source corner for the
uncovered/authored recovery inputs. Recovery preserves tangent absence and handedness rules, including
finite handedness, and never changes covered positions. A rare degenerate case may show a locally odd
shaded spot instead of the whole character changing pose. Other vertices and the other direction remain
independent. Native direction words cannot publish NaN or infinity.

The `2e-5` component tolerance is conditional on well-conditioned, normal-range calculations taking the
same non-recovery branches. One sufficient forward-error envelope is an aggregate pre-normalization
relative vector perturbation at most `d = 4e-6`, native sqrt/division normal-result relative errors at
most `2^-20`, and underflow absolute errors at most `2^-126`. Normalization contributes at most
`2*d/(1-d)`, with less than `5e-6` rounding allowance, below `1.4e-5` total. This is a conditioning
restriction, not a universal palette, mesh or device guarantee. Cancellation or a small determinant can
violate that envelope even with NoContraction. In the degenerate band CPU/GPU directions may differ.
Require finite directions and identical position words there. Wrong normal, tangent and stale-direction
controls still reject under the tight non-degenerate tolerance. The approved Metal, Direct3D 11 WARP and
Vulkan device units pass these checks. That evidence does not cover other devices. The cost limit is at
most 1.3 ms added GPU time at 32 actors on a Tesla T4.

The CPU reference keeps its exact non-degenerate direction arithmetic and adopts the same local
recovery policy. Sample verification no longer creates a hole solely because a direction failed.
The exact constructor's geometry, `CoverageCertification.ValidateGeometry` and whole-skin depth gates
are unchanged. Certification independently checks positional folds, injectivity and intersections.
Whole-skin classification still compares complete CPU records, including locally recovered directions.
No offline gate reads GPU direction words. This change supplies no new continuous position guarantee.
Version-one and earlier unaccepted version-two descriptor identities require rebuilding. None have shipped.

The consumer owns phase bins, blend bins, clip semantics and their schema digest. The engine only looks
up the unsigned key, including key zero. It does not sample animation, infer bins from geometry or pick
a nearby row. An unknown key and an explicit entry -1 are both `TableHole`.

## Offline construction and blocking verification

[`Coverage.Verify`](../tools/Coverage.Verify/README.md) is outside the engine solution and umbrellas.
The standard pack and release ship it as `KhaozEngine.Coverage.Tool`, command `ke-coverage`.
Install the exact engine version from the configured engine feed:

```sh
dotnet tool install --tool-path .tools KhaozEngine.Coverage.Tool --version "$KHAOZENGINE_VERSION"
.tools/ke-coverage version
.tools/ke-coverage verify --manifest poses.json --table coverage.kert --evidence verified.json
```

Set `KHAOZENGINE_VERSION` to the engine release used by the consumer. The `version` command (also
`--version`) prints JSON containing `engineVersion` and `carryArithmeticContractVersion` and exits 0.
Record both with the verification evidence. The installed `build`, `verify-samples` and `verify`
commands use the same arguments, exit codes and atomic output preservation as the source executable.

Its strict manifest names exact assets, domains, every authored clip frame and the consumer's real
blend paths. Only authored-clip domains may provide an empty `blendPathIds` array, and the strict reader
still requires the property. Every declared frame and
path must occur in the sample inventory. The consumer supplies the poses and resolved state keys.

`build` constructs full unweighted scalar candidates, requires one canonical representative vector,
deduplicates rows by binary32 words and checks every candidate against every declared sample. Repeated
keys intersect validity across all their occurrences. Deterministic greedy selection retains rows that
cover the most remaining keys with canonical digest/byte tie-breaks. Evidence retains the complete
candidate/sample matrix, even for unused candidates. Selected claims describe only the row actually
assigned to each key. A successful candidate at some sample is not acceptance of the selected table.
A key without a common valid row is a hole. CLI builds with holes publish neither table nor evidence.

`verify-samples` rechecks supplied rows and their selected claims. It does not construct replacements.
Both commands have evidence scope `declared-samples-only`, which supplies no whole-skin acceptance.
The blocking consumer CI command is:

```sh
dotnet tools/Coverage.Verify/bin/Release/net10.0/Coverage.Verify.dll verify --manifest poses.json --table coverage.kert --evidence verified.json
```

`verify` has scope `declared-samples-and-dense-whole-skin`. It requires dense rows in every domain,
validates all sample claims and replays every supplied 240 Hz pose against the key the consumer's
60 Hz selection would use. Ordinary rows hold phase tick `denseIndex / 4`. Blend bins may change keys
inside a held phase tick. Explicit loop/clamp flags admit consumer remapping without inventing policy.
Evidence records those flags, poses, ticks and keys. Dense replay continues after holes or selected-entry
failures so the report does not hide later work. Sample and dense evidence scopes cannot resume each
other, and provenance must match the exact input, table, source and tool identities.

Whole-skin depth checking is mandatory in addition to exact geometry validation. It examines all
triangles against every current closed equipment solid, including triangles outside a command's
region. Full-weight exact validation separates other commanded regions from the equipment, but skips
full clearance and containment checks at partial transition weight. The whole-skin gate covers that
partial-weight gap. Partial weights do not inherit a full-weight containment guarantee.

Triangles are classified from every byte of all three records. Position changes, attributes-only
changes and unchanged records remain distinguishable. Any positive changed-record observation or
upper estimate blocks, including unresolved positive estimates below the depth search's resolution.
`ChangedRecordObservedMm` and `ChangedRecordUpperEstimateMm` deliberately do not claim causation.
`ChangedRecordRawDepthMm` reports the same triangles in the raw current pose. A changed triangle that
already intruded is conservatively blocked. Unchanged-record penetration remains inherited fit debt,
reported separately as `InheritedObservedMm`. It is not repaired by an accepted table. Double winding,
closest-boundary sampling and adaptive upper estimates have numerical limits and are not outward-rounded
continuous enclosures.

Exit 0 means complete accepted declared work. Exit 1 means validation or output-publication failure.
Exit 2 means malformed input or invalid invocation. Selected failures, holes, missing dense work and
positive or unresolved changed-record depth block `verify`. Typed refusals retain domain, sample,
state, entry and applicable command/equipment identifiers. Output publication is bounded and atomic,
and refusal preserves existing output files. The consumer owns sampler integration and the CI call.
Sampled evidence cannot establish safety between the poses that were checked.

All three commands accept optional `--diagnostics <path>` for a separate refusal report. Schema
`khaozengine.coverage-diagnostics/1` and scope `diagnostic-not-acceptance` explicitly exclude this file
from acceptance evidence. It records candidate construction outcomes and witnesses, complete validity
pairs, claims and holes. Blocking verification adds dense row reasons, missing domains and separate
changed-record and inherited depth findings. A successful run replaces stale diagnostics with an
accepted outcome and zero blocking findings, while retaining inherited fit information. Malformed
invocations or inputs leave diagnostics untouched. Its file name must differ from the file name of every
input and accepted output of the run, in any directory, and from the target of any of them read through
a link. The tool README lists the Windows path forms it refuses with this option.
Exact category totals and omitted counts survive deterministic truncation at the existing
64 MiB artifact limit. Acceptance gates, exit codes and refused-run table/evidence preservation stay
unchanged within one tool build. Evidence binds the producing tool and Render3D hashes, so another build
produces different provenance bytes and its evidence is refused on resume. Build, replay and depth
arithmetic, ordering and refusal order are unchanged. See the
[diagnostic schema](../tools/Coverage.Verify/README.md#optional-refusal-diagnostics)
for field names and publication details.

## Consumer example and lifetime

The complete [compiling example](examples/EquipmentCoverageConsumer.cs) is linked directly into
`KhaozEngine.Render.Tests`. `CoverageConsumerExampleTests` executes that exact source against row
selection, explicit and missing holes, complete submitted descriptors and a legacy attachment adapter.
The example is consumer code, not a new engine runtime API.

Create one `HostCoverageBinding` per actor incarnation, on the scene thread. Load attachments and their
exact companions through `AttachmentSetCache`, bind the complete equipment configuration at cold equip,
and keep its cache, host handles and scene alive. Preparation freezes the current palette, command
frames and descriptors without queuing them. Forward every complete rigid or skinned descriptor,
including borrowed garment palettes, material, tint, dissolve, shadows and coverage tokens. Do not
reconstruct a reduced draw from mesh and transform. Submit all host slices and commanded equipment.

`Scene3DTileWorldScene` exposes `IAttachmentCoverageScene` through its existing attachment delegate.
Pass the wrapper to both `AttachmentSetCache` and `HostCoverageBinding`. They retain the same actual
scene owner, including when separate wrappers refer to that scene. Another scene cannot accept that
cache's equipment resources.

The example accepts the consumer's key directly as `CoverageStateKey`. `Queue` returns the preparation
status and `CoverageFailure`. On refusal it explicitly queues each uncovered current host slice with
the same actor motion key and draws ordinary active equipment. `PreparedEquipmentDraws` is empty on a
hole, so forwarding that span alone would silently drop equipment. `CoverageFailureFor(actor)` reports
later scene-side publication refusals. Binding failures retain their original returned identifiers.

`GameApp3D` begins its scene with the window frame's existing `Frame.Submission` before `OnDraw3D`,
then prepares it before opening that frame's command list. Call the example's `Queue` from `OnDraw3D`.
The window submits once and commits history only after successful submission. Exceptions abandon the
recording. Minimized frames skip the scene callbacks. A zero viewport keeps legacy no-argument preparation.
Shared retirement defers sealing when completion capacity is full. Its later fence only extends retention.
Sealed batches plus consecutive deferrals share the queue's drain bound, counted by `ValveDrains`.

For a custom host, `SubmitFrame` begins the existing `GpuFrameSubmission`, calls `Scene3D.BeginFrame(submission)`, queues
all actors, and calls `PrepareFrame` before opening the host command list. The caller supplies its
existing render callback and the list is submitted once through that same owner. Cache callbacks if a
frame loop must avoid delegate allocation. Coverage registers one scene-owned aggregate participant
for all actors, not one per actor. The batch is one completion consumer. No consumer registration loop
is needed, so the default eight participant slots can support 32 actor histories. If participant
registration fails before recording, ordinary current skin is drawn and no actor coverage history changes,
even on abandonment or failed submission. Since that frame has no observable submission decision, a
capacity-refused frame that does submit can leave the next covered motion one frame stale.

Borrowed tokens and palettes must be consumed synchronously. An actor accepts one preparation per
frame. Repeating it returns `StaleFrame` before changing the accepted snapshot. Begin, successful
replacement, resource unload and disposal expire tokens. Unloading a host slice also expires its
equipment tokens. A refused equipment replacement preserves the active configuration. Empty equipment
selects ordinary presentation. Ordinary `IAttachmentScene` adapters remain supported without implementing
`IAttachmentCoverageScene`, as the example's `QueueOrdinary` demonstrates.

An anchored equipment draw must render the same rebased transform as its frozen absolute descriptor.
Anchor-local offsets hidden by absolute binary32 rounding are refused as `StaleFrame` before queueing.

At teardown, dispose the submission owner first to drain retained work. Dispose actor bindings before
attachment caches and scenes, then release the host command list and device. The example owns only its
binding and borrows all other resources. Follow the host's normal command-list cleanup on recording
failure. Do not reuse an unsealed recording after an exception.

The standard `GameApp3D` teardown instead disposes its scene before the window. Scene disposal drains
submitted work before releasing coverage and retirement resources, leaving the window owner to release
its own fences afterward. Dispose consumer bindings and caches before calling the base scene teardown.

## Runtime carry, refusal and history

Each active table group records five GPU dispatches in order: skin the current pose, refine and
transport the selected scalars, construct face bases, gather attributes and normals, then publish the
final surface and tight bounds. Gathering writes private candidates and actor-wide error flags.
Publication makes one position-failure decision per actor. A position failure selects the complete uncovered current
record for every vertex, without mixing covered and raw records or exposing stale scratch words.

CPU preparation first bounds the composed skin palette using a cold source envelope, the actual maximum
absolute weight sum, double arithmetic and rounding headroom. The bound is on actor-model-space skinning,
before the actor's model and world transform, so world position never enters it. Its conservative
`2^20` ceiling protects
raw coordinates, directions and later intermediates without assuming normalized weights. An unsafe
palette returns `UnsupportedTransform` before dispatch and uses ordinary current skin. This can refuse
a finite reference result. It neither clamps the pose nor fabricates a GPU raw record.

Carry and pulling graphics variants request the public `GpuShaderCompileOptions(PreciseMath: true)`
and author GLSL `precise` operations. The option preserves operation order and disables the applicable
backend fast-math/contraction optimizations. Default shader compilation and cache identity are unchanged.
Metal's pinned precise add, subtract and multiply helpers use `[[clang::always_inline]]` instead of
`[[clang::optnone]]` to avoid unoptimized per-operation calls. Fast math remains disabled, and the
signed-zero corrections and helper bodies stay fixed. Unknown helper forms still refuse compilation.
Unsupported precise requests refuse through the existing typed shader/capability families. Graphics
owners publish readiness only after successful construction and CPU compilation, and release partial resources on failure.
A sticky graphics refusal also suppresses coverage rebuilds when ordinary temporal, HDR or MSAA outputs change. This is not
a promise of identical transcendental or denormal behavior on all devices. See the
[GPU precision contract](USING-KHAOZENGINE.md#precise-compute-and-vertex-arithmetic).

Deferred backends expose the CPU-only join through `IGpuShaderSet.EnsureCompilationReady` and its compute
sibling. Coverage queues each cold batch first. All graphics owners are constructed before joining their
programs, and no host surface is published until every required join succeeds. Output variants use the
same refusal transaction, and join only after the scene has queued every renderer's programs for that
output rebuild. A compiler failure latches `UnsupportedAdapter` and draws ordinary current skin.
Legacy parameterless preparation and settings changed after preparation can introduce a variant at render
target sizing. That refusal is propagated before caster collection and draw classification, cancelling the
pending surfaces before any coverage command is recorded.
Native realization remains at the deferred backend's consuming boundary. Ready warm frames never rejoin,
and the join adds no GPU wait or new compiler work. Cold-frame latency has not been measured here.

Output storage reserves `K = D + 2` equal slots per actor, where `D` is the resolved frames-in-flight
depth. These cover in-flight surfaces, their predecessor and a pending write. Pending output never
overwrites committed or retained output. Completion polls do not wait. A full ring can still commit
logical history, retaining its slots until a later fence proves earlier work complete. Exhausted output
slots refuse without waiting. Cold growth uses a history lease and the existing retire queue to keep
old storage alive. Slot counts describe reservation policy, not a measured memory footprint.
Once history retains are gone, storage disposal attempts all three buffer releases before rethrowing
a single release error or an `AggregateException` for multiple errors. Later disposal is idempotent.

The final indexed surface is shared by colour/depth, temporal motion, cascaded and point shadows and
full/visible outlines. Per-material descriptors stay paired with that actor. Both ordinary CPU and GPU
skinning settings use this same surface when coverage is admitted. Only successful submission commits
entry, final slot, absolute model, view and configuration correspondence together. Encoding, preparation
and registration alone do not commit history. Abandonment preserves prior history. A submission exception
can follow partial device work, so pending storage remains quarantined until completion is proven.

Refusal has three presentation paths:

- A `TableHole` with a usable pipeline and safe slot runs all five kernels with entry -1. It publishes
  every word of the uncovered current pose from the frozen submitted palette after overflow preflight.
  It uses no CPU-skinned upload. Covered/raw transitions read the previously submitted final surface.
- Missing compute, completion fences or precise pipeline support is a sticky device refusal. No covered
  surface exists on that device. Ordinary current skin and ordinary motion history remain in use.
- Other per-actor or transient refusals, including unsafe transforms and no safe slot, draw ordinary
  current skin. An actual encoded ordinary draw records a discontinuity only on successful submission.
  The former committed slot stays retained until proven complete. The next covered frame seeds previous
  from current for one frame of camera-only motion. Abandonment leaves prior history untouched.

Holes can cause visible collar jumps. The consumer owns collar tuning, state-bin granularity and any
future alternative presentation. Actor, configuration, view or submitted-frame discontinuities also
seed correspondence again. Origin rebasing preserves absolute model/surface correspondence.

Before recording carry, the scene cancels an actor's pending surface when no main, cascade, possible
point-shadow or outline consumer remains. That actor contributes no carry uniforms or dispatch work,
and its submitted history behaves like a frame with no queued host draw. Outline consumers are retained
conservatively because they have separate full and visible mask passes.

Skin's source and uncovered source rows share storage, so it validates the completed records without a
self-copy. A raw table hole still records all five stages, while Gather skips candidate stores because
Publish selects the complete uncovered Before row. The exact position graph and all five stages remain. Native direction arithmetic does not establish
a measured GPU saving.

Zero warm managed allocation requires cold reservations for the fixed actor population, topology,
equipment, descriptors, renderer bindings and output slots, plus an unchanged warm configuration.
Equipping, loading, growth, shader/pipeline creation and teardown are cold work and can allocate. A
headless zero-allocation assertion establishes neither native allocation behavior nor device cost.

## Verification boundaries

The free tooling CI suite `coverage` explicitly builds the outside-solution executable and tests,
requires a nonempty test inventory, executed CLI process tests and zero skips, and sets `KE_GPU_TESTS=0`.
Coverage formats, carry and tooling changes select it. It does not authorize native or cost execution.

The headless Diagnostic fact `CoverageCarryInstructionProfileTests` executes the five compiled SPIR-V
stages on the synthetic body-size fixture and reports instruction counts by stage and helper. Counts
include labels and control flow, count vector instructions once, and attribute exact-worker descendants
without double counting. They are a relative guide, not GPU timings. The bounded serial executor does
not model occupancy, divergence, memory bandwidth or device arithmetic. The published surface is also
compared with the exact CPU reference: positions and other non-direction words match bit for bit,
normal/tangent XYZ use the conditional `2e-5` non-degenerate component tolerance. Degenerate cases compare
positions exactly and require finite local directions. Default CI excludes this Diagnostic profile.

G1, G2 and G3 are separately approved Metal, Direct3D 11 WARP and Vulkan correctness units. G3 also
requires Vulkan synchronization validation. The cross-frame read of the slot written by the previous
submission remains open for G3 because Vulkan hazard tracking resets at Begin. Existing headless
scheduling evidence does not settle that hazard. Software devices prove output and synchronization,
not hardware speed.

G4 and G5 are separately approved hardware cost units with frozen synthetic workloads. Both require
`KE_COVERAGE_COST_UNIT=Real_body_size_carry_and_scene_overhead_for_1_and_32_actors` in addition to the
normal hardware opt-ins and exact fact selection. Missing or different values skip before probing
hardware. Hosted benchmark dispatch forwards its optional `coverage-cost-unit` input to that variable,
and ordinary shard filters exclude this fact even without Diagnostic filtering. Do not use a broad
Diagnostic selection as cost-unit authorization. See [performance evidence](RENDERER-PERFORMANCE.md#equipment-coverage-cost-evidence).

Cost schema `khaozengine.coverage-recession-cost/6` measures carry-only and baseline/covered scenes for
one and 32 actors. The adjacent `baseline-scene` and `covered-scene-outer` rows supply the 1.3 ms added GPU
and 2 ms added CPU preparation checks at 32 actors. Both carry only two timestamp markers, so the
comparison cancels per-marker cost. All rows retain the zero warm managed allocation check. Rows ending in `-outer`
repeat carry-only and covered-scene with the baseline row's two markers and no stage brackets, so a
backend whose marker ends the native encoder can be compared at equal marker counts. Those rows report
no stage times. Additional rows ending
in `-passes` enable the existing CPU recording buckets with `Scene3D.EnableTiming` and bracket the
existing `PassObserver` callbacks with `GpuTimestamps`. They report per-sample and per-pass min, median,
p95, p99 and max GPU microseconds, including exclusive durations, executed status and fused motion.
Colour, depth and motion share the opaque MRT draw here. Fused motion is metadata, not another interval.
Group intervals contain children, so adding all inclusive times would count work twice.
Samples retain begin/end marker indices for audit against their raw ticks. `UnscopedGpuMicroseconds`
is the outer total minus exclusive pass times, including inter-pass gaps, marker overhead and the End tail.
Skipped callbacks retain zero-duration metadata without GPU markers.

Pass observations are separate because timestamp markers can split Metal encoders and change the
resulting load/store work. Their aggregate times are not frame costs on Metal and cannot replace the
equal-marker scene pair. Cold layout discovery precedes the existing conditioning schedule. Layout drift
refuses the row. Resolution, aggregation, readback and drains remain outside measured frame regions.
Between conditioning and the observed frames the unit runs a blocking collection, waits for pending
finalizers and collects again. A finalizer or background collection that runs while a measured window is
being established loses that window's no-GC region with no collection counted, which would report the
row as an operation failure before its evidence is read.
Carry dispatch brackets remain available beside the pass observations.

These are native stream brackets around logical recording scopes, not isolated draw costs. On Metal a
timestamp closes an open encoder but leaves pending attachment clears untouched. A later draw can
consume earlier scopes' colour, motion, visibility or depth clears as native load actions. Flushing them
at their logical closing marker would add a clear-only render pass with its own tile load/store work,
so the observations preserve the original fusion.

Every interval has `MetalClearAttributionAvailable`. When true, `AbsorbedDeferredClears` names each
encoded clear's request ordinal, source scope and begin marker, attachment, and consuming scope.
Provenance comes from recorded clear commands and a read-only query of successful native clear
consumption, without recognizing scope names. Replaced requests and failed native pass begins do not
count as executed clears. Inclusive parents repeat their children's records, with `ConsumedBy` naming
the innermost owner. Do not add those records or durations twice. `UnscopedDeferredClears` retains work
flushed only by native End or between scopes. False availability means the backend does not supply this
Metal-specific provenance, rather than proving that no work was deferred.

Framebuffer load/store intent is also declarative. Loads execute when the native render encoder opens
and stores when it ends. Their cost stays inside those native intervals. A marker can therefore add a
store and a later load. Pipeline, resource, vertex-stream, viewport and scissor state invalidated at that
boundary is re-emitted by the next consuming draw or dispatch. Those costs are part of the bracket.
An explicit MSAA resolve opens and closes its own native pass before returning, so its resolve work is
bracketed there, while pending clears remain pending. Texture copies flush pending clears first and
receive their clear annotations. Buffer copies, compute dispatches and mip generation do not perform
that clear flush. A subsequent draw, framebuffer replacement or End can still consume those clears.
None of these annotations separates shader execution from native load/store, resolve or state work.
The existing cost driver's counting wrapper uses the interface's default framebuffer-store fallback,
so it records default store actions. This instrumentation preserves that recording policy. It does not
establish the cost of the scene owner's dead-attachment discard optimization.

The seven layouts at each population, in order, are `baseline-scene`, `covered-scene-outer`,
`carry-only-outer`, `carry-only`, `covered-scene`, `baseline-scene-passes` and `covered-scene-passes`.
Both populations therefore attempt 14 rows. Only `baseline-scene`, `covered-scene-outer` and
`carry-only-outer` are acceptance rows. The stage-bracketed and pass rows are diagnostic.
An acceptance failure ends the unit with a structured `CoverageRecessionRowException`. Diagnostic failures
at construction, recording, harvest or evidence readback make that row unavailable for the rest of its
population. The next population attempts the layout again. Remaining rows keep their original schedule.

Every row failure names layout, actors, phase, phase-local frame, stage brackets seen and expected,
dispatch count, distinct owner coverage failure codes and captured factory failures. Phase is
`conditioning` or `observed`. Frame `-1` means construction before a frame, and discovery frames use
conditioning frame numbers. Shader, pipeline and resource factory exceptions keep their type and message,
plus shader refusal reason, label and stage. The observer rethrows the original exception. Publication is
checked after preparation, before a native list or timestamp pool begins. A later incomplete carry still
fails the bracket guard. Failed partial recordings are discarded before submission teardown drains and
releases timestamp pools and carry storage.

The row's `Error`, factory failures and cleanup failures retain each exception's type, message and stack,
with `InnerExceptions` preserving wrappers and aggregate branches down to twelve levels and sixteen
siblings per aggregate. `OmittedInnerExceptions` counts what a bound left out, so the snapshot always fits
the JSON writer. A member whose getter throws is named in `UnreadableMembers` and costs only that member,
because a snapshot taken while a failure is being reported must never replace it.
An allocation measurement exception at any level adds `Allocation` with its typed reason, description,
requested budget, process, thread and other-thread byte counts, reported-message count, generation collection
counts and runtime reason. Counters stay null when a window never opened. These diagnostics flow into
`unavailableRows` and acceptance exceptions through the same row details. They identify a recurrence's
failure path without establishing the cause of an earlier failure whose original error was discarded.

Cleanup failures retain their type and available shader refusal identifiers in `CleanupFailures`,
alongside the original row failure. Safe cleanup continues after a non-drain failure. Row cleanup
snapshots owner coverage codes before disposal, since the disposed scene rejects status queries.
Construction cleanup attempts every tracked handle in reverse creation order. A failed submission drain sets
`DrainFailed` and ends the entire unit, including for diagnostic layouts, because later timings cannot
be trusted. That row retains its timestamp pools, carry storage and programs, and submitted command
lists. Teardown still attempts every remaining row as far as is safe. Failures from those rows appear
in `AdditionalRowFailures` with their own row context and never replace the original failure.

The schema's `unavailableRows` array retains each row identity and its structured reason. Diagnostic deltas
that depend on a missing row are null. JSON and `COVERAGE_RECESSION_COST_JSON` are written before assertions.
The original zero warm bytes, 2000 microseconds CPU preparation and 1300 microseconds added GPU checks stay
in force. An additional assertion requires no unavailable rows.

`addedGpuMicroseconds`, `aggregateGpuOverheadPercent` and `addedCpuPreparationMicroseconds` compare
`covered-scene-outer` against `baseline-scene`. The diagnostic `addedGpuMicrosecondsStageMarkers`
subtracts baseline-scene from covered-scene and is null if that staged row is unavailable. Metal markers
end the encoder and encode a separate blit pass between event waits, so the ten extra stage markers
cost real GPU time. They must not inflate the acceptance delta. Vulkan markers are timestamp writes.
Every covered row reads back published carry words against the exact oracle. An exact-word mismatch
uses the distinct `ArithmeticMismatch` reason, unavailable for diagnostic rows and fatal for acceptance.
Headless `PreciseShaderCompileTests` guards the shipped inlinable helpers across every coverage vertex
program and all five carry kernels. It establishes emission, not native arithmetic or image acceptance.

`CoverageRecessionDrawInventoryTests` records the same synthetic 32-actor body-size scene without a GPU.
Its `COVERAGE_DRAW_INVENTORY_JSON` output groups submitted draw calls and element counts by innermost
pass, indexed flag, coverage route and vertex compile policy. It also reports pipeline sets and changes,
resource-set binds and changes, vertex-buffer binds and index-buffer binds. Elements are index references
or pulled corners multiplied by instances, not distinct vertex shader invocations. Changes compare
requested handles and dynamic offsets across the recording, not backend encoder state. These counts
guide a separately approved quiet Metal run. They cannot identify the cause of a GPU cost difference.

All five units have accepted evidence at commit `ea7c9d362`, each run once on 2026-11-10 UTC:

- G1, hosted Metal: 9 of 9 selected tests passed.
- G2, Direct3D 11 WARP: 9 of 9 passed.
- G3, lavapipe Vulkan: 10 of 10 passed, including the validation control. The synchronization job
  reported no error-severity message and no hazard.
- G4, Tesla T4 Vulkan: 1218 microseconds added GPU time at 32 actors against the 1300 limit, and 110
  microseconds worst added CPU preparation, over 32 observed frames per row.
- G5, Apple M2 Max Metal: 734 microseconds added GPU time at 32 actors and 102 microseconds worst added
  CPU preparation, over nine observed frames per row.

The G3 result is an absence of reported hazards under an active checker. No deliberate cross-submission
hazard control exists. The G4 margin is small and each cost figure is one run of a synthetic workload,
so a change to carry, publication, coverage vertex or precise shader code needs all five units again at
its own SHA. This evidence alone authorizes no release, package publication or
consumer adoption. CPU runtime carry, different float semantics, collar retuning, private table generation,
consumer sampler/CI adoption, leather and precise dual-quaternion skinning remain separate follow-up work.
