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

The executable supports two offline commands. Both use a consumer-authored pose manifest and never sample
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
only over declared samples. It supplies neither dense replay nor whole-skin penetration evidence, and is
not a continuous proof. Those checks are separate from sample verification.
