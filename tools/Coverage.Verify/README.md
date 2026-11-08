# Offline coverage verification

`Coverage.Verify` is a nonpackable executable outside `KhaozEngine.slnx` and every engine umbrella.
Runtime code never references it. Public fixtures contain synthetic geometry only.

`CoverageExactConstructor` owns a copy of the inverse-bind palette and reusable per-pose scratch.
`TryConstruct` skins the source, applies frozen refinement, conforms and corrects the core, blends
transition weights, and gathers normals and tangents. It reuses Render3D's retained carry arithmetic
through the `Coverage.Verify` friend grant. Cold asset, support and refinement validation remain in
Render3D.

The three caller-owned output spans must have exact topology lengths and disjoint storage. On success,
geometry contains the transitioned candidate, `fullRecessions` contains full unweighted binary32
scalars by position group, and `representatives` contains refined vertex indices or -1 for inactive
groups. The constructor copies every output. Reusing its scratch cannot mutate a previous export.
A refusal leaves every caller output unchanged and returns `CoverageFailure` with its host and
applicable command and equipment context.

Construction alone supplies no geometry certificate. Pass the interpolated posed surface and the
candidate to `CoverageCertification.ValidateGeometry` with a `CoverageScratch` for the exact topology
instance. Certification checks protected bytes, welds, world recession budgets, caps, core domain,
folding, projected injectivity, affected host intersections, and commanded triangles against the
actual equipment solid. Full clearance and containment checks apply at full transition weight.
Ambiguous predicates refuse. A sampled certificate establishes only the supplied pose.

Geometry refusals use `CoverageFailureCode`, never message classification. A hierarchy walk storage
refusal throws `CoverageToolStateException` with `Reason`, `Required` and `Capacity`. Plain argument
checks retain BCL exception types.

Build and test the outside-solution projects explicitly from the repository root. GPU tests are not
required.

```sh
build-slot --label coverage-exact-tool -- dotnet test tools/Coverage.Verify.Tests/Coverage.Verify.Tests.csproj -c Release --filter FullyQualifiedName~Exact
```

## Deterministic tables and sample evidence

The executable supports three offline commands. They use a consumer-authored pose manifest and never sample
an animation or infer a key. Exit codes are 0 for complete accepted work, 1 for validation or output
publication failure, and 2 for malformed input. Holes are explicit in the in-memory build result, but a
CLI run containing a hole publishes neither table nor evidence. Refused runs preserve existing outputs.
Outputs are staged as temporary siblings before rename, with rollback if publishing the pair fails.
Evidence serialization and resumed file admission share one inclusive byte bound. Oversized output
refuses with the typed capacity reason before either accepted file is replaced. Resumed JSON syntax
and field-type errors retain the malformed-input refusal family and exit code 2.

```sh
dotnet tools/Coverage.Verify/bin/Release/net10.0/Coverage.Verify.dll build --manifest poses.json --table coverage.kert --evidence samples.json
dotnet tools/Coverage.Verify/bin/Release/net10.0/Coverage.Verify.dll verify-samples --manifest poses.json --table coverage.kert --evidence samples.json
```

Format 1 manifests require `formatVersion`, `host`, `map`, `configuration`, `inverseBind`,
`stateSchemaSha256`, `domains`, `candidateSampleIds` and `samples`. Every external reference is an
object with `path` and lowercase `sha256`, checked against exact bytes before parsing. References resolve
beside their declaring file. Host and part assets are self-contained GLBs. Every material slice is
validated. `inverseBind` must equal the imported host palette. Matrices are arrays of sixteen finite
binary32 values in row-major order. Duplicate and unknown members, invalid digests, nonfinite numbers,
invalid membership and over-capacity inventories refuse with `CoverageBuildDataException.Reason` and a
rooted `Path`. Wrapped asset refusals retain their typed inner exception.

Each domain has `id`, `kind` (`authored-clip` or `blend-path`), `authoredFrameIds` and `blendPathIds`.
Authored frames are nonempty in every domain. Only an authored-clip domain can have no blend paths.
Each sample declares `id`, `domainId`, `stateKey` (an unsigned 64-bit number), `actorModel`, `palette`
and `commands`, and at least one of `authoredFrameId` and `blendPathId`. Both identifiers, when present,
must belong to that domain. Every declared frame and path needs a sample. Candidate IDs are unique,
ordered and must name samples. Limits are 65536 samples or inventory items, 4096 candidates, 4194304
candidate/sample pairs, 1024 palette matrices, four commands and 64 MiB per input or published artifact.

The configuration file requires `formatVersion`, exact `attachment` and `companion` references, and
`parts`. Every part has `id`, an exact GLB `asset` reference, `hostJoint`, `socket` and `bodyAlignment`.
The checked attachment supplies part local transforms and mesh identities. The companion supplies
static command values, coverage frames and inward support ranges. All parts must resolve, host joint
names must match, and companion attachment and map bindings must match. Current sample commands contain
`id`, `equipmentInstanceId`, `equipmentWorld`, `coverageWorld` and `transitionWeight`. Every configured
command occurs exactly once in each sample. Static command values come from the checked companion.

Source identities are produced by `CoverageBuildInputJson`. Host identity hashes exact host bytes. Map
identity uses the existing semantic sidecar hash. Configuration identity hashes a versioned domain,
exact checked configuration bytes (which bind attachment, companion, part assets and authored transforms)
and inverse-bind words in explicit little-endian order. Refinement identity hashes versioned source
ordinals, refined records, integer recipes, indices and triangle ownership in little-endian order.
Arithmetic identity binds the retained binary32 flush contract and the exact Render3D assembly hash.
It conservatively changes for any Render3D code change. State schema identity is supplied by the consumer.
Current actor models, palettes and equipment instance IDs are outside configuration identity.

`CoverageTableBuilder.Build` constructs full unweighted scalars with the exact constructor, requires one
canonical topology-owned representative vector, and deduplicates scalar rows by their binary32 words.
The exact constructor and verifier use Render3D's shared active-representative selection. Its activity
predicate uses retained arithmetic, so strength at 2^-126 or below is inactive and the next normal word
above that boundary is active.
It certifies every deduplicated candidate at every sample through the retained carry path and exact
validator. For a repeated key it intersects validity across every occurrence. Deterministic greedy cover
chooses the row covering most unassigned keys, with canonical scalar SHA-256 and then bytes as tie-breaks.
The table retains only greedily selected rows. Evidence retains every deduplicated candidate and its
complete sample matrix. Its `entries` inventory maps pair indices and scalar hashes to compact table
indices, with -1 for unused candidates. A key with no common valid row becomes a hole. The builder re-reads the serialized table and verifies it again.
`CoverageSampleVerifier.Verify` only applies supplied rows, never generates candidates, and authenticates
all digests, state inventory and topology-dependent representatives before checking geometry.

Evidence contains all six source digests, exact manifest and table hashes, tool code identity, complete
domain/sample/candidate inventories, candidate hashes, the complete pair matrix, selected claims and holes.
Resuming an evidence path requires matching input, table, tool and source provenance. Verification is
only over the declared work. `build` and `verify-samples` have scope `declared-samples-only` and supply
no whole-skin acceptance. `verify` has scope `declared-samples-and-dense-whole-skin` and is the blocking
consumer-CI command. These are sampled checks, never continuous proof.


## Blocking dense replay and whole-skin evidence

Build the tool in Release, then run this portable command in consumer CI against the exact checked
manifest and serialized table. Use a separate evidence path from sample-only output. Evidence scopes
cannot resume each other. A nonzero exit blocks adoption. The consumer owns its sampler and CI wiring.

```sh
dotnet tools/Coverage.Verify/bin/Release/net10.0/Coverage.Verify.dll verify --manifest poses.json --table coverage.kert --evidence verified.json
```

Each existing domain record gains `denseRows`. These rows are optional for sample-only commands, but
`verify` requires a nonempty sequence in every domain. The global bound is 65536 dense rows in addition
to the sample inventory. Every row explicitly contains:

- `denseIndex`, a contiguous zero-based integer at 240 Hz within that domain.
- `precedingPhaseTick`, a nonnegative integer at 60 Hz. Ordinary rows require `denseIndex / 4` using
  integer division. Thus indices 0 through 3 hold phase tick 0 and index 4 takes phase tick 1.
- `resolvedStateKey`, the consumer's unsigned 64-bit key. Current blend bins may change this key at any
  dense index, including inside a held phase tick. The engine never derives bins from geometry.
- `sample`, the current pose with the same fields as an ordinary sample except `stateKey`. The enclosing
  row supplies the resolved key. Its `domainId`, frame and path identifiers belong to this domain.
- `looped` and `clamped`, required booleans. They cannot both be true. Either explicitly permits the
  consumer's phase remapping at a loop or path endpoint. The engine uses the supplied tick, pose and key
  without adding clamp, extrapolation or loop policy. Flags and complete pose data appear in evidence.

`CoverageReplay.Verify` processes every row, including rows after a hole or selected-entry failure.
A hole uses the uncovered current pose and classifies all its triangles as inherited. A carry refusal
cannot publish a partial surface. An unpreparable raw pose refuses the entire operation so an earlier
row's buffers cannot masquerade as current work. The exact sample matrix and each selected dense pose
pass through `CoverageCertification.ValidateGeometry`. Whole-skin depth is a second, independent gate.

`CoverageSkinClasses` compares all bytes of all three records of every triangle, regardless of region
membership. It separates position changes, attributes-only changes and unchanged records. A changed
normal is a changed record even when its position is identical. Every class is checked against every
current command's complete closed equipment solid, in world metres after both actor and equipment
transforms. Open solids and unsupported or nonfinite arithmetic refuse with public verification reasons.

The depth search samples triangle interiors, computes double winding classification and closest
boundary distances, then bisects triangles adaptively without a tolerated-depth band. It reports
observed depth and a Lipschitz-based upper estimate separately in millimetres. Its inclusive default
budget is 4096 signed-depth queries per class, equipment and pose. Unqueried work retains a finite
upper estimate. `Resolved` means that estimate is within 0.1 mm of the observation, never that a
positive depth is acceptable. Any positive changed-record observation or upper estimate blocks,
including a positive estimate smaller than the resolution. Positive values are never rounded to zero.
These double estimates are not outward-rounded certificates. Winding, transform and distance arithmetic
have numerical limitations. The search supplies sampled evidence, not a certified continuous enclosure.

Replay reports `ChangedRecordObservedMm`, `ChangedRecordUpperEstimateMm`, `ChangedRecordRawDepthMm`,
`InheritedObservedMm` and `UnresolvedChangedRecordPoses` (camelCase in JSON), complete per-pose records,
triangle classification counts, equipment counts and query counts. Raw depth measures the same changed
triangles in the raw current pose. A changed triangle may already have intruded, so neither its name nor
its gate claims the entry caused the intrusion. The conservative gate still blocks that triangle.
Unchanged-record penetration and unresolved inherited depth remain separate inherited fit evidence.

Selected sample or replay failures, holes, missing replay work, unresolved changed-record depth or any
positive changed-record depth block publication. `CoverageVerificationException.Reason` identifies the
rule. The CLI emits a JSON refusal containing that public reason and applicable domain, dense index,
sample, state key, entry, command, equipment and exact-failure identifiers, then exits 1. Malformed
JSON retains `CoverageBuildDataException`, its rooted path and exit 2. Accepted evidence reuses the
same bounded serialization, provenance checks and atomic publication as sample-only commands.
