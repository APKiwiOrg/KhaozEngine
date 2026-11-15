# Offline coverage verification

`Coverage.Verify` ships as the `KhaozEngine.Coverage.Tool` dotnet tool, command `ke-coverage`.
Its executable and tests stay outside `KhaozEngine.slnx` and every engine umbrella, keeping offline
construction and process verification out of ordinary solution builds and test discovery.
The standard `scripts/pack-packages.sh` inventory packs both the solution and this tool. The guarded
local-feed wrapper and release publish use that inventory. No runtime package references the tool.
Public fixtures contain synthetic geometry only.
The living [equipment coverage guide](../../docs/EQUIPMENT-COVERAGE.md) connects this offline contract
to runtime selection, fallback and submitted history. Native correctness and cost require separately
approved evidence. Sample verification is not a continuous proof.

Install from the configured engine package source with the same exact version as the engine. Set
`KHAOZENGINE_VERSION` to that release before running:

```sh
dotnet tool install --tool-path .tools KhaozEngine.Coverage.Tool --version "$KHAOZENGINE_VERSION"
.tools/ke-coverage version
.tools/ke-coverage build --manifest poses.json --table coverage.kert --evidence samples.json
.tools/ke-coverage verify-samples --manifest poses.json --table coverage.kert --evidence samples.json
.tools/ke-coverage verify --manifest poses.json --table coverage.kert --evidence verified.json
```

`version` and `--version` return exit 0 and one JSON object with `engineVersion` and
`carryArithmeticContractVersion`. Record both with consumer verification evidence. A malformed invocation
returns exit 2. The packaged executable exposes the same parser, refusal codes and existing-output
preservation contract as the source-built commands below. Evidence byte identity requires the same
tool and Render3D producer builds.

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

### KEPP numeric transport

The internal packed-pose codec defines KEPP version 1. This codec does not yet add a packed manifest
route to the CLI. Format 1 above still uses inline numeric arrays. KEPP stores complete numeric poses,
with explicit little-endian integers and binary32 words. Matrix order is M11, M12, through M44.
No decimal conversion, binary64 conversion, normalization or quantization occurs in the codec.

| Header offset | Bytes | Value |
|---:|---:|---|
| 0 | 4 | ASCII `KEPP` |
| 4 | 4 | uint32 version, 1 |
| 8 | 4 | uint32 header length, 32 |
| 12 | 4 | uint32 endian marker, `0x01020304` |
| 16 | 4 | uint32 palette count B, 1 through 1,024 |
| 20 | 4 | uint32 command count C, 1 through 4 |
| 24 | 4 | uint32 record count R, positive |
| 28 | 4 | uint32 record stride, `64 + 64*B + 144*C` |

The exact chunk length is `32 + R*stride`, at most 8,388,608 bytes inclusively. Length arithmetic is
checked in 64 bits before decoding allocations. Header spans must contain exactly 32 bytes and record
spans exactly one stride. Record i starts at `32 + i*stride`. Its actor matrix occupies 64 bytes,
followed by B palette matrices in skin order. Each of its C commands then occupies 144 bytes.

| Command-relative offset | Bytes | Value |
|---:|---:|---|
| 0 | 4 | uint32 command ID |
| 4 | 8 | uint64 nonzero equipment instance ID, without alignment padding |
| 12 | 64 | Equipment-world matrix |
| 76 | 64 | Coverage-world matrix |
| 140 | 4 | Transition weight |

Commands retain their authored order and IDs are unique within a record. Configured command membership
and host palette agreement belong to asset binding. Every numeric word must be finite. Signed zeros,
subnormals and all other finite words are retained exactly. Transform and weight usability remain
numerical validation concerns. The 1,024-palette transport ceiling does not raise the GLB import limit
of 128 joints.

The independent synthetic control has B=2, C=1, R=1, stride 336 and total length 368. Its header hex is
`4b45505001000000200000000403020102000000010000000100000050010000`. This executable Python fixture
writes the exact bytes and checks their SHA-256 before saving them:

```python
import hashlib
import pathlib
import struct

header = bytes.fromhex("4b45505001000000200000000403020102000000010000000100000050010000")
identity = struct.pack("<16I", 0x3f800000, 0, 0, 0, 0, 0x3f800000, 0, 0,
                       0, 0, 0x3f800000, 0, 0, 0, 0, 0x3f800000)
record = identity * 3 + struct.pack("<IQ", 23, 17) + identity * 2 + struct.pack("<I", 0x3f800000)
chunk = header + record
assert len(chunk) == 368
assert hashlib.sha256(chunk).hexdigest() == "79e5da6535a760e62db55573fbb996c67a1b4b6edcd1649eafb751f3a0ad42a5"
path = pathlib.Path("poses/000000.kepp")
path.parent.mkdir(exist_ok=True)
path.write_bytes(chunk)
```

The codec-only WordVector contains `00000000`, `80000000`, `00000001`, `80000001`, `007fffff`,
`807fffff`, `00800000` and `3f800001`, with distinct finite words at its remaining matrix positions.
Each word is also tested in every matrix field and the transition weight. These are storage vectors,
not geometry inputs.

Malformed bytes use `CoverageBuildDataException`. Layout errors are `PackedHeaderInvalid`, unsupported
versions are `UnsupportedVersion`, zero record counts are `PackedCountMismatch`, excessive B/C or
chunk lengths are `CapacityExceeded`, inconsistent lengths are `PackedChunkLengthMismatch`, and
nonfinite words are `PackedWordNonFinite`. `Path` is the rooted manifest location. Optional `ChunkIndex`,
`RecordIndex` and absolute chunk `ByteOffset` locate the defect. Length failures include `ExpectedLength`
and `ActualLength`, capacity failures include `Count` and inclusive `Limit`, and version failures include
`FormatVersion`. `Identity` is local exception context and must not be copied into exported evidence.

Packed path admission accepts only portable relative `/` components below the declaring directory.
Empty, dot, parent, backslash, colon, NUL and rooted forms refuse as `PackedPathEscape`. Referenced
symbolic links and reparse points, including leaves and links whose targets remain inside, refuse as
`PackedPathLink`. The declaring directory itself may be an OS alias. Missing referenced components use
`PackedChunkMissing` and unresolvable metadata uses `InputIo`. Existing general asset resolution is unchanged.

The configuration file requires `formatVersion`, exact `attachment` and `companion` references, and
`parts`. Every part has `id`, an exact GLB `asset` reference, `hostJoint`, `socket` and `bodyAlignment`.
The checked attachment supplies part local transforms and mesh identities. The companion supplies
static command values, coverage frames and inward support ranges. All parts enter the canonical
configuration, including uncommanded skinned garments. Only commanded parts must be rigid supports.
Garments use an empty `hostJoint` and identity `socket` and `bodyAlignment`, matching runtime binding.
All assets must resolve, rigid host joint names must match, and companion attachment and map bindings must match. Current sample commands contain
`id`, `equipmentInstanceId`, `equipmentWorld`, `coverageWorld` and `transitionWeight`. Every configured
command occurs exactly once in each sample. Static command values come from the checked companion.

Source identities are produced by `CoverageBuildInputJson`. Host identity hashes exact host bytes. Map
identity uses the existing semantic sidecar hash. Configuration identity uses the public engine-owned `CoverageConfigurationIdentity` implementation shared
with runtime binding. It hashes a versioned domain, items sorted by exact attachment and companion
SHA-256, parts sorted by ID with host joint name, socket and body-alignment matrix words, then
skin-order inverse-bind words. Strings are length-prefixed UTF-8 and numeric words are explicitly
little-endian. Exact attachment and companion identities bind mesh assets and authored command values.
The current manifest authors one item, while the shared function accepts a complete item list.
Configuration file paths, member order and whitespace do not affect the digest. Refinement identity hashes versioned source
ordinals, refined records, integer recipes, indices and triangle ownership in little-endian order.
Arithmetic identity hashes the embedded versioned carry-contract descriptor used by both the tool and
runtime, without reading assembly files or locations. Independent frozen vectors guard its output words
and refusal boundaries. Contract version 2 keeps positions and position-failure publication exact.
CPU non-degenerate direction math stays exact, while failed directions recover locally through the
shared policy. A direction problem alone no longer makes an entry/sample pair invalid. Exact geometry
certification and whole-skin depth/classification remain unchanged and never read GPU directions.
GPU directions are native. Their `2e-5` component comparison is conditional on the well-conditioned
[forward-error envelope](../../docs/EQUIPMENT-COVERAGE.md#assets-and-shared-identity), not universal.
In the degenerate band require finite directions and identical positions. Earlier descriptor identities
require rebuilding. Exact assembly hashes remain evidence provenance only.
Changes to scalar rounding, skin composition, refinement, representatives,
transition weighting, recession direction or gathers must advance the contract version and descriptor.
Unrelated binary changes preserve compatibility. Exact `toolCodeSha256` and `render3DCodeSha256` values
remain producer provenance in evidence only. State schema identity is supplied by the consumer.
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


## Optional refusal diagnostics

Append `--diagnostics <path>` to `build`, `verify-samples` or `verify` to export a separate
non-acceptance JSON document. For example:

```sh
.tools/ke-coverage build --manifest poses.json --table coverage.kert --evidence samples.json --diagnostics diagnostics.json
.tools/ke-coverage verify --manifest poses.json --table coverage.kert --evidence verified.json --diagnostics diagnostics.json
```

The schema is `khaozengine.coverage-diagnostics/1`, with scope `diagnostic-not-acceptance`. This file
is never accepted evidence and cannot replace either evidence scope. It changes no selection,
certification or depth gate. Within one tool build, enabling or omitting the option produces identical
table and evidence bytes, refusal rules, existing-output preservation, stderr and exit codes for distinct
writable destinations. Build, replay and depth arithmetic, ordering and refusal order are unchanged.
Evidence includes the producing tool and Render3D assembly hashes, so evidence from another build is refused on
resume with `ProvenanceMismatch`, as before. Cross-build evidence bytes are not identical.

The diagnostics file name must be unique within the run. It cannot equal the file name of the
manifest, the table, the evidence or any checked source, in any directory, ignoring case and Unicode
composition. When one of those files is read through a link, the name of the file it finally points at
is protected as well. Linked directories and mounts can spell one directory in ways no path comparison
can enumerate, but they keep the file name, so the rule does not depend on resolving them. On Windows a
device prefix (`\\?\`, `\\.\`, `\??\`) or a stream suffix on any path of the run cannot be identified by
name, so diagnostics are refused for those forms. A link whose target cannot be resolved is refused the
same way, because its target name is unknown. Each such invocation exits 2 before publication and leaves
existing files untouched. This guards against accidental aliases. It is not a security boundary against
a caller who constructs one, for example with a second hard link under another name. The
accepted-output checks are unchanged: they compare normalized path spellings and refuse with
`OutputAlias` and exit 1. Malformed input or resumed evidence also leaves existing diagnostics untouched.

For parsed work, exit 0 writes `outcome: "accepted"` and zero blocking findings, replacing stale
refusal diagnostics. Exit 1 writes `outcome: "refused"`. Accepted artifacts and diagnostics stage
atomically. A refusal publishes only diagnostics and preserves existing table and evidence bytes.
Output I/O failure retains exit 1 and may prevent the diagnostic file itself from being published.
No timestamps, machine names or file paths enter the document.

Top-level fields are `schema`, `scope`, `command`, `outcome`, `inputSha256`, the six-digest `identity`,
`tableSha256` when computed, `reason`, `findingCount` and the following category objects. Each category
has exact `total` and `omitted` counts and an `items` array, including when empty.

| Category | Items |
|---|---|
| `domains`, `samples` | Domain and sample inventories, including dense row counts and opaque sample keys |
| `candidates` | Every declared candidate's witness `sampleId`, `outcome` (`constructed` or `refused`), public `reason`, exact `failure` and scalar hash when constructed |
| `candidateEntries`, `candidatePairs` | Deduplicated constructed rows and their complete entry/sample validity matrix |
| `entries`, `pairResults` | Rows and validity pairs for the table actually checked, or the diagnostic build after refused constructions |
| `selectedClaims`, `selectedFailures` | Selected state keys, entry indices, sample witnesses, validity and exact failure codes |
| `holes` | Every uncovered state key with all sample ids sharing it |
| `replayFailures` | Dense row failures with domain, dense index, resolved state key, sample, reason and applicable entry, command, equipment and exact failure identifiers |
| `missingDenseWork` | Every domain without dense rows |
| `changedRecordDepth`, `inheritedDepth` | Nonzero or unresolved per-pose, per-command class depth, with domain, dense index, resolved key, sample, entry, command, equipment, triangle count, observed millimetres, upper estimate, query count and resolved flag |
| `analysisRefusals`, `runRefusals` | Typed diagnostic analysis and original run refusal reasons |

Changed-record depth includes the raw-pose depth of the same triangle class. Inherited depth remains
fit information even in an accepted document. Invalid unselected candidate pairs also remain visible
without becoming blocking findings. `findingCount` is zero on acceptance. On refusal it sums candidate
construction refusals, holes, selected failures, replay failures, missing work and analysis/run refusal
records. One pose can have several such observations.

Candidate construction continues for diagnostic purposes after a refusal. Successfully constructed
candidates are checked through the existing builder without publishing its result. Sample verification
still checks the supplied table and never replaces it. Dense diagnostics continue through missing
work, declaration failures and unpreparable poses to report later rows. They reuse the same replay
measurement and keep changed-record and inherited depth separate.

The inclusive artifact limit remains 64 MiB. Metadata and exact totals are reserved first, then items
are retained in the listed category order and the tool's deterministic order within each category.
The first item that does not fit and all following items are counted as omitted. Truncation never
changes acceptance or fails the run. Identical inputs and outcomes produce identical diagnostic bytes.


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

## Headless tooling CI

The free `tooling-validation.yml` suite `coverage` builds this executable and its test project explicitly
on hosted Ubuntu. It requires a nonempty inventory, executed CLI process tests and zero skips, with
`KE_GPU_TESTS=0`. Coverage format, carry and tooling changes select the suite. This is deterministic
headless verification, not native correctness or cost acceptance. The Diagnostic hardware cost fact
requires its separate exact `KE_COVERAGE_COST_UNIT` opt-in and owner-approved invocation, documented in
[performance evidence](../../docs/RENDERER-PERFORMANCE.md#equipment-coverage-cost-evidence).
