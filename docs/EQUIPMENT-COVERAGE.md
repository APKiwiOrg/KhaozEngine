# Equipment coverage

Equipment coverage applies an offline-verified recession table to the current skinned host pose. The
consumer supplies an opaque animation-state key. Runtime selects a row and carries its scalars through
fixed refinement. It performs no exact construction, correction, filtered selection, continuous
certification or runtime membership test. The exact constructor and validator are offline tooling.

Headless reference, binding, publication and tooling tests are implemented. Native correctness and cost
are unproven until the separately approved G1 to G5 units are accepted at the actual code SHA. No cost
result or measured memory footprint is claimed here. Verification at samples is not a continuous proof.

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
controls still reject under the tight non-degenerate tolerance. Native accuracy remains unproven until
approved device runs. The cost limit is at most 1.3 ms added GPU time at 32 actors on a Tesla T4.

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

The example accepts the consumer's key directly as `CoverageStateKey`. `Queue` returns the preparation
status and `CoverageFailure`. On refusal it explicitly queues each uncovered current host slice with
the same actor motion key and draws ordinary active equipment. `PreparedEquipmentDraws` is empty on a
hole, so forwarding that span alone would silently drop equipment. `CoverageFailureFor(actor)` reports
later scene-side publication refusals. Binding failures retain their original returned identifiers.

`SubmitFrame` begins the existing `GpuFrameSubmission`, calls `Scene3D.BeginFrame(submission)`, queues
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

Until all five units have accepted evidence at the actual SHA, native correctness, synchronization and
aggregate cost remain unproven. This implementation alone authorizes no release, package publication or
consumer adoption. CPU runtime carry, different float semantics, collar retuning, private table generation,
consumer sampler/CI adoption, leather and precise dual-quaternion skinning remain separate follow-up work.
