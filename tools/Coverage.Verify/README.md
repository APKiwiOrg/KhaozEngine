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
.tools/ke-coverage build --manifest poses.json --table coverage.kert --evidence build-samples.json
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
Outputs publish through one recoverable transaction, described under Publication and recovery.
Every evidence root and part has one inclusive 67,108,864-byte bound. A logical document within it
stays one legacy JSON file. A larger one is published as a hashed `khaozengine.coverage-evidence-set/1`
set, described under Artifact set transport, so no record is ever omitted. Resume opens the existing
root through the strict artifact reader, which checks every part before any record is compared. A
legacy root over the bound refuses with `CapacityExceeded`. Resumed JSON syntax, field-type and part
errors retain the malformed-input refusal family and exit code 2.
Resume recomputes acceptance, then compares every logical field. Whitespace and object member order
do not matter. Matrix and weight numbers compare as binary32 words, including the sign of zero.
Changed provenance is `ProvenanceMismatch`. Changed records with matching provenance are
`ArtifactContentMismatch`. Malformed records retain the malformed-input refusal family.
Use a separate evidence path when changing commands. Build evidence includes candidate construction
inventories that sample verification does not construct, so those complete documents differ even when
their table, scope and input identities match.

```sh
dotnet tools/Coverage.Verify/bin/Release/net10.0/Coverage.Verify.dll build --manifest poses.json --table coverage.kert --evidence build-samples.json
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

Format 2 keeps those root fields and requires `poseChunks`. Each descriptor has exactly `path`,
`byteLength`, lowercase `sha256` and positive integral `recordCount`. A sample, including a dense row's
sample, contains either all three inline pose fields (`actorModel`, `palette`, `commands`) or exactly
`poseRef: {"chunk":0,"record":0}` in their place. Both indices are nonnegative integral numbers within
the actual chunk and record inventories. Mixed inline and referenced samples are valid. An all-inline
format 2 manifest uses an empty `poseChunks` array. Format 1 refuses these new members.

Each chunk record must have exactly one ordinary or dense owner. Duplicate references, unused records
and unused descriptors refuse as `PackedReferenceInvalid`. Chunk order does not select geometry order.
The manifest still determines ordinary and dense order. There are at most 4,096 nonempty chunks, each
at most 8,388,608 bytes, and at most 131,072 referenced records across both sample inventories. Ordinary
and global dense counts each remain at most 65,536. These transport limits do not increase work limits.

`CoverageBuildInput.Read` and `CoverageBuildInputJson.Read` eagerly materialize both formats into
independent resident snapshots. They retain every decoded pose. The executable does not use this eager
route. It opens an internal `CoverageInputSession`, which owns a disposable metadata campaign and private
checked chunk copies, decoding a pose on ordinal access. Its temporary directory is created under the OS
temporary root, is private on Unix, and is removed after publication. Disposal removes only that
directory. A session-backed campaign expires when its session closes. Materializing it returns an
independent snapshot whose pose access needs no temporary files. A pose that can no longer be read from
its private copy, for example after a scratch cleaner removed it, refuses as `InputIo` with `ChunkIndex`
and `RecordIndex`, exit code 2.

The builder, sample verifier and replay share their numerical cores between resident snapshots and
internal campaigns. Candidate lookup and grouping use metadata ordinals. Pairs retain IDs and failures,
and replay observations retain metrics and dense-row ordinals. Dense evidence and replay `Poses` are
read-only projections that reconstruct actual input on access without caching decoded rows. Resident
results keep their input alive. Internal session-backed projections must be consumed before disposal.
Replay summaries and acceptance checks use metrics without decoding poses. Internal `WriteTo(Stream)`
writes the complete logical evidence row by row. Public `Write()` still returns a byte array subject
to the inclusive 67,108,864-byte legacy limit.

For the internal streaming path, the controlled storage bound is
`O(T + F + C + P + S + D + K*G)`, with no `(S+D)*B` retained matrix term and no total-evidence-byte
term. Here F is at most 67,108,864 manifest bytes, C is at most 8,388,608 chunk bytes, P is at most
4,194,304 pairs, S and D are each at most 65,536 ordinary samples and dense rows, K is at most 4,096
candidates, and G is the admitted topology's position-group count. T covers topology, support, table
copies and exact/depth scratch. Metadata DOM, strings and indices add runtime-dependent O(F) overhead.
This equation is not a fixed process RSS guarantee. It describes the executable's input route.

Snapshot admission uses one 65,536-byte copy/hash buffer and one numeric record buffer, bounded by C.
A campaign hands out each decoded pose as a lease. The session's live pose count is the numeric bytes of
the pose it is decoding and binding, plus every pose whose lease is undisposed. It counts nothing else.
When a lease over a pose the packed session decoded is disposed, the session overwrites that pose's actor,
palette and command words with NaN, so exact arithmetic refuses any numerical use of a reference kept past
its lease. The overwrite writes `64 + 64*B + 132*C` bytes per released pose and allocates nothing. A lease
over a resident snapshot is non-owning, and disposing it never changes the caller's arrays.

A released pose that something still references keeps its memory, and that memory is not counted, although
its pose words are no longer usable. Copies a caller makes are caller owned and are also outside the count.
`Materialize` and replay `Poses` deep copy each session pose before its lease is released, so what they
return stays valid. Over a resident snapshot those two return the caller's own rows. Dense evidence always
projects fresh caller owned numeric arrays, whatever the input.

Every production reader releases one pose before it reads the next, so decoding and immutable binding hold
at most two numeric poses. Two guards back that property. A reader that kept its leases would exceed the
deterministic two-pose assertion, which covers building, verification, replay, evidence writing and report
collection. A reader that used a pose after releasing it would be refused, which the packed and inline
parity tests would show. Neither guard counts a released pose that is kept and never used again, so the
bound is on leased and transient storage, not on everything a reader could retain. The maximum numeric pose is
`64 + 64*1024 + 144*4 = 66,176` bytes, calculated from the transport guards. Object headers and static
command binding references add bounded overhead. Writing flushes individual records and matrices,
without a campaign-sized JSON buffer. Staging records its largest payload write and copy buffer, at most
65,536 bytes for the evidence writer whatever the document length. A single escaped metadata token
remains bounded by F. The session's deterministic storage seam records snapshot bytes, the buffer
high-water and the live-pose high-water.

Scratch disk grows with checked raw pose bytes and, when artifacts are staged, logical output and
rollback backups. The palette-only maximum is 8 GiB, calculated at the 1,024-matrix input guard. The
independent GLB importer admits at most 128 joints. Disk exhaustion refuses as `InputIo` during
snapshotting or `OutputIo` during publication. It never permits omitted records.

Admission first checks manifest syntax, duplicate/unknown members, field types and bounded inventories,
then exact reference closure and every chunk path. It copies and hashes one chunk at a time, checks
length and digest before inspecting the header or any record, and scans all numeric words before
binding host, map, configuration or support geometry. Hash mismatch therefore wins over nonfinite words
in the same chunk. Only the checked private copies supply later poses. Observable source length changes
during copying refuse as `PackedInputChanged`. A source replaced after admission cannot alter a pose.
Palette and command agreement with the checked configuration is validated before the session opens.
Dense replay phase policy remains a verification gate. Configuration manifests still require version 1.

`InputSha256` hashes exact manifest bytes. Format 2 descriptors transitively bind all checked pose bytes.
Changing a pose and refreshing its descriptor changes this input identity, while the six runtime table
identities retain their existing meanings. Original chunk paths and private copies are included among
the session's protected inputs. Public resident snapshots retain only original input paths.

### KEPP numeric transport

The packed-pose codec defines KEPP version 1 for format 2 references. Format 1 above still uses inline
numeric arrays. KEPP stores complete numeric poses,
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

This complete minimal format 2 manifest references that chunk. Its `host`, `map` and `configuration`
references stand for the consumer's own checked assets: a two-joint host with identity inverse binds and
a configuration whose only command is 23. Every other member is literal, including the chunk's real
digest. A deterministic tooling test reads this block and the fixture above from this file, regenerates
the 368 bytes independently of the codec, and builds the campaign through the executable.

```json
{
  "formatVersion": 2,
  "host": {"path": "host.glb", "sha256": "<host sha256>"},
  "map": {"path": "map.json", "sha256": "<map sha256>"},
  "configuration": {"path": "configuration.json", "sha256": "<configuration sha256>"},
  "inverseBind": [[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1], [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]],
  "stateSchemaSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "domains": [{"id": "clip", "kind": "authored-clip", "authoredFrameIds": ["frame-a"], "blendPathIds": []}],
  "candidateSampleIds": ["a"],
  "samples": [{"id": "a", "domainId": "clip", "authoredFrameId": "frame-a", "stateKey": 1,
               "poseRef": {"chunk": 0, "record": 0}}],
  "poseChunks": [{"path": "poses/000000.kepp", "byteLength": 368,
                  "sha256": "79e5da6535a760e62db55573fbb996c67a1b4b6edcd1649eafb751f3a0ad42a5", "recordCount": 1}]
}
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
Missing chunks use `PackedChunkMissing`, descriptor/header count disagreement uses `PackedCountMismatch`,
and digest disagreement uses `PackedChunkHashMismatch` with `ExpectedSha256` and `ActualSha256`.

Packed path admission accepts only portable relative `/` components below the declaring directory.
Empty, dot, parent, backslash, colon, NUL and rooted forms refuse as `PackedPathEscape`. Referenced
symbolic links and reparse points, including leaves and links whose targets remain inside, refuse as
`PackedPathLink`. The declaring directory uses `Path.GetFullPath`, matching the runtime's lexical resolution
when it opens the manifest. The OS follows any aliases within that base. Each referenced prefix is checked
using its own attributes, including the leaf, so links below the base are refused. Missing referenced
components use `PackedChunkMissing` and unreadable metadata uses `InputIo`. Existing general asset resolution
is unchanged.

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


## Artifact set transport

`CoverageArtifactReader.Open(rootPath, scratchDirectory)` reads legacy logical evidence, a logical
diagnostic report and the `khaozengine.coverage-evidence-set/1` and
`khaozengine.coverage-diagnostics-set/1` envelopes. Any other version of either set schema refuses with
`UnsupportedVersion`, `Path` `$.schema` and the version in `FormatVersion`, so a newer envelope is never
read as a malformed report. A suffix that is not a positive decimal version of at most 18 digits without
leading zeros, such as `0`, `01`, `x` or a 19-digit number, refuses with `ArtifactSetInvalid` at `$.schema`
and carries no `FormatVersion`. The returned disposable session exposes the SHA-256 of the exact root
bytes and `OpenPayload()`, which reads a private checked snapshot. Close payload streams before disposing
the session. Every new `Open` verifies the original files again.

The internal staging writer preserves legacy bytes when the complete payload fits the inclusive
67,108,864-byte artifact limit. Larger payloads become an index plus ordered raw fragments. The index
binds scope, completeness, input and table hashes, all six identities, both producer hashes, the logical
payload schema, exact inventory totals, payload length and digest, part count, and every part descriptor.
Each descriptor has `ordinal`, `path`, `byteLength` and lowercase `sha256`. Paths have the form
`<root-file-name>.parts/<payload-sha256>/<five-digit-ordinal>-<part-sha256>.part`.

Concatenating parts in ordinal order yields exactly one complete logical JSON document. Fragments
are not separate JSON documents. A token, escape sequence, UTF-8 code point or complete record may
cross a boundary. Non-final parts have the target length. The production target and inclusive ceiling
are 67,108,864 bytes. There are at most 65,536 parts and the index shares the inclusive artifact limit.
Internal tests may lower these limits. No successful staging omits a descriptor or logical record.

The reader checks confined generated paths without links, contiguous ordinals, exact lengths, every
part digest, the aggregate digest, strict typed logical fields, inventory closure and the final EOF.
Missing, truncated and substituted parts carry `ArtifactPartMissing`, `ArtifactPartLengthMismatch`
or `ArtifactPartHashMismatch`, with the descriptor's rooted `Path` and zero-based `ChunkIndex`.
Other broken closure is `ArtifactSetInvalid`. A previously successful read never authenticates later
bytes by path alone. Keep the root and the complete parts directory together when copying a set.
Invalid UTF-8 and unpaired UTF-16 surrogate escapes retain `InvalidJson` classification.

Staging uses fixed 65,536-byte copy buffers and scratch files, with no aggregate payload byte array.
Typed reading retains one bounded token, current object field hashes and bounded inventory indices.
The conservative payload envelope is `12*K*F + 64 GiB`, at most 3 TiB plus 64 GiB for K=4,096 and
F=67,108,864. This fits 65,536 production parts. Index capacity is checked while writing, before
publication. Staging needs scratch space for about twice the logical payload, because the complete
payload is written and checked once and then copied into parts. A checked read needs space for one
payload snapshot. Insufficient disk refuses.
The variable string term allows two pair inventories, each repeating bounded metadata at most K
times with at most six JSON bytes per escaped character. The remaining bound covers fixed record
syntax and numeric fields. At the count guards, dense poses contain at most
`65,536*(16 + 16*1,024 + 4*33)` binary32 values. Reserving 32 JSON bytes per value and 512 fixed bytes
for each of two 4,194,304-pair inventories leaves room within 64 GiB for all other bounded inventories
and replay metrics. The exact index preflight includes the escaped root name and every descriptor.
Staging owns only its private scratch directory and performs no publication. The publisher below makes a
staged set visible. The CLI stages both its evidence and its diagnostics through this transport. Evidence
within the bound stays one legacy file and larger evidence becomes a set. Diagnostics are described under
Optional refusal diagnostics.

## Publication and recovery

One publisher makes the table, diagnostics and evidence visible as a set. The output directory holds that
set: the roots, the table and every `.parts` directory belong together.

Before the first write, every output is identified by the operating system, never by comparing spellings.
An existing file is its volume and file identity, so a hard link, a linked parent directory, a link whose
target contains dot segments and an alias such as `/tmp` on macOS all reach one identity. An absent
destination is the identity of its existing parent plus its final name, folded for case and Unicode
composition because a volume may merge such names. Two absent outputs that differ only in letter case or
composition are therefore refused on every volume, including a case-sensitive one. Two outputs with one
identity, or an output with the identity of the manifest, a checked source, a pose chunk or a table being
verified, refuse with `OutputAlias` and exit 1. A destination the OS cannot identify, such as a dangling
or looping link, refuses the same way. An absent output parent refuses with `OutputIo`. An input-side
path the OS cannot identify, such as a manifest in a missing directory, reaches no output, so input
admission reports it with exit 2.

Identity uses `stat` on Linux and macOS and the file id on Windows. The C library is bound at run time:
`stat` on glibc 2.33 and later, on musl and on macOS, and `__xstat` on older glibc. Linux and macOS are
covered on 64-bit x86 and Arm. On any other host, or when the C library or its entry point cannot be
bound, every output is unidentifiable and the run refuses with `OutputAlias` and exit 1 before writing.
Generated part paths must contain no link. Deliberate concurrent renaming by another process is outside
this check.

Publication then runs in this order:

1. Parts are copied beside their final names, flushed, checked against their descriptors and moved in
   without replacing anything. Parts are immutable and content addressed. An existing part with the same
   name is reused only when its bytes match. Otherwise publication refuses and leaves it untouched.
   Missing generation directories are created beneath the existing output parent.
2. Each mutable output is copied to a sibling `<name>.<transaction>.tmp` and checked.
3. A journal `<commit-root>.coverage-journal` is written beside the commit root with owner-only
   permissions on Unix. It records each output's old and new length, SHA-256 and Unix mode, or that it
   did not exist, and the parts and directories this transaction created. A marker
   `<name>.coverage-incomplete` is created beside every mutable output, then byte-for-byte backups
   `<name>.<transaction>.bak` of existing outputs. These local recovery files name local paths and are
   never evidence. On Windows they inherit the directory's permissions.
4. The table, then the diagnostics, then the evidence root are replaced. The last replacement is the
   commit point. A refusal publishes diagnostics only, and the diagnostic root is then the commit root.
5. Backups, markers and the journal are removed. Older generations stay, because a reader may still hold
   an older root.

A handled failure before the commit point attempts every restore in reverse order, removes only files
this transaction created and keeps every pre-existing byte. One failed restore never skips the rest. A
restored output gets back its old Unix mode. Hard links to a replaced or restored output are not
preserved, because replacement and restore both rename another file into place. The refusal is
`OutputIo`. Its inner exception is the original failure and `RollbackFailures` lists every rollback step
that also failed. When anything could not be undone, the journal, markers and remaining backups stay. A
cleanup failure after the commit point also keeps them and reports `OutputIo`.

Readers refuse an output that has a marker or journal with `OutputRecoveryRequired`, whichever bytes it
holds. The next run on the same evidence and diagnostics paths recovers first. Recovery reads and checks
the whole journal before touching any path. When every output holds its new bytes and every part
verifies, it only removes recovery files. Otherwise it restores each output that holds its new bytes from
its checked backup, or removes it when it did not exist before. An output holding neither recorded state,
for example after an independent edit, refuses with `OutputRecoveryRequired` and nothing is overwritten.

The guarantee is the complete dependency closure at the commit point and exact preservation after a
handled failure. Independently named files cannot be renamed atomically together, so a concurrent reader
may briefly see a new table beside an old evidence root and must refuse the hash mismatch. Process
interruption leaves a recoverable transaction or an unreferenced generation. Stage files
`<name>.<transaction>.tmp` left by an interruption before the journal exists are unreferenced and safe to
delete. Durability after power loss
is not claimed. Old generations are never deleted automatically. Copy or remove a set as a root together
with its whole `.parts` directory.

## Optional refusal diagnostics

Append `--diagnostics <path>` to `build`, `verify-samples` or `verify` to export a separate, complete
non-acceptance report. For example:

```sh
.tools/ke-coverage build --manifest poses.json --table coverage.kert --evidence samples.json --diagnostics diagnostics.json
.tools/ke-coverage verify --manifest poses.json --table coverage.kert --evidence verified.json --diagnostics diagnostics.json
```

The logical schema is `khaozengine.coverage-diagnostics/2`, with scope `diagnostic-not-acceptance`. This
report is never accepted evidence and cannot replace either evidence scope. It changes no selection,
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
accepted-output checks compare OS identities instead, as described under Publication and recovery, and
refuse with `OutputAlias` and exit 1. Malformed input or resumed evidence also leaves existing diagnostics untouched.

For parsed work, exit 0 writes `outcome: "accepted"` and zero blocking findings, replacing stale
refusal diagnostics. Exit 1 writes `outcome: "refused"`. Accepted artifacts and diagnostics publish
in one transaction. A refusal publishes only diagnostics and preserves existing table and evidence bytes.
If the report cannot be published, the run exits 1 with `OutputIo` and the previous report stays exactly
as it was, or absent. A failed accepted transaction carried the report already, so no second report
replaces the previous one. The report is collected in full before any output is staged. Collection
decodes poses, so a pose that can no longer be read during collection refuses as `InputIo` with exit
code 2, never as `OutputIo`, and every output and the previous report keep their exact bytes. No
timestamps, machine names, nonces or file paths enter the document.

Top-level fields are `schema`, `scope`, `command`, `outcome`, `inputSha256`, the six-digest `identity`,
`tableSha256` when computed, `reason`, `findingCount`, `toolCodeSha256`, `render3DCodeSha256`,
`resumedEvidenceSha256` and the following category objects. Each category has `total`, `omitted` and
`items`, including when empty. `total` equals the number of items and `omitted` is always 0.

The two producer hashes are the SHA-256 of the tool and Render3D assemblies that wrote the report.
`resumedEvidenceSha256` is the SHA-256 of the exact existing evidence root bytes that resume admission
read, including any noncanonical whitespace. For an evidence set it is the hash of the index root, which
binds every part, not of the joined payload. It is recorded once the root is read, so a resume refusal
still names it. Null means no existing evidence was read. It is never the hash of newly produced
evidence. These fields name the producer and the input it read. They do not prove that a report is fresh,
because two identical invocations write identical reports. A consumer that needs per-invocation freshness
uses a fresh report path or removes the old report before the run.

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

Every collected item is exported, in the listed category order and the tool's deterministic order
within each category. A report within the inclusive 67,108,864-byte artifact limit is one JSON file. A
larger report becomes a `khaozengine.coverage-diagnostics-set/1` index at the requested path with hashed
parts beside it, as described under Artifact set transport. Its index also carries
`resumedEvidenceSha256`, `command`, `outcome` and the exact total of every category. Read either form
with `CoverageArtifactReader`, which admits a set only with every part, the exact payload and matching
totals, and keep the root and its `.parts` directory together. An index that cannot name every part
refuses before publication with `OutputIo` and exit 1. Analysis that stops records a typed analysis
refusal and the observations actually made, never work that did not run. Identical inputs and outcomes
produce identical diagnostic bytes.


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
same streamed serialization, provenance checks and recoverable publication as sample-only commands.

## Headless tooling CI

The free `tooling-validation.yml` suite `coverage` builds this executable and its test project explicitly
on hosted Ubuntu. It requires a nonempty inventory, executed CLI process tests and zero skips, with
`KE_GPU_TESTS=0`. Coverage format, carry and tooling changes select the suite. `CoveragePackageProcessTests` runs
`version`, `build`, `verify-samples` and `verify` over format 1 and the documented format 2 campaign, checks
every evidence and report root through the strict reader, and resumes a multipart evidence set with a
substituted part and with the intact part. In CI it starts the source-built tool. A candidate package runs the
same assertions when `KE_COVERAGE_TOOL_EXECUTABLE` names its installed `ke-coverage` by absolute path, and the
test refuses a relative or missing path. The installed tool must report the engine version the tests were built
with. This is deterministic
headless verification, not native correctness or cost acceptance. The Diagnostic hardware cost fact
requires its separate exact `KE_COVERAGE_COST_UNIT` opt-in and owner-approved invocation, documented in
[performance evidence](../../docs/RENDERER-PERFORMANCE.md#equipment-coverage-cost-evidence).
