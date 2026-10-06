# shadow-seam-analysis: offline point-shadow seam control analyzer

Stage 0 of `docs/design/POINT-SHADOW-SEAM-CONTROL-DESIGN-2026-10-07.md` (design SHA-256
`1c4ebca1d297405cb4b2f7b08f4ef585acc58946506f4a8d8569681499ea6a9d`). Python standard library only.
It reads retained or captured artifacts, validates them, computes the frozen measurements and
reports the frozen diagnostic outcomes. It never renders, starts a process, accesses the network,
invokes git, installs anything or changes an input artifact.

Status: Stage 0 behavior implemented after the observed RED window. The interfaces and constants
below are pinned. The GREEN run and the retained REPORT-ONLY analysis are separately gated, and
nothing in this file claims either has run.

## Files

| File | Responsibility |
| --- | --- |
| `shadow_inputs.py` | Bounded ZIP and directory reads, schema-v1 evidence, descriptor and atlas sidecar validation |
| `shadow_geometry.py` | Recorded-matrix floor projection, visible-floor certificate over F, footprints, convex overlap, crossing mask |
| `shadow_metrics.py` | Original float32 replay, area-band reach, detrend statistic in metres |
| `shadow_compare.py` | Gates G1 to G7, paired identity, outside-F reporting, outcome ordering, report records |
| `analyze.py` | The two explicit CLI modes |
| `test_shadow_analysis.py` | Exactly 24 finite unittest methods |

The immutable `docs/superpowers/plans/proofs/2026-10-07-point-shadow-seam-offline.py` stays the
original replay reference. Only its applicable arithmetic is ported.

## Invocation

```bash
python3 tools/shadow-seam-analysis/analyze.py retained --artifact <retained.zip> --output <report.json>
python3 tools/shadow-seam-analysis/analyze.py compare --expectation <descriptor.json> --bundle <zip-or-dir> --output <report.json>
python3 -m unittest discover -s tools/shadow-seam-analysis -p 'test_shadow_analysis.py' -v
```

Exit 0 means the requested analysis completed, not that a shader passed. Invalid input or a failed
gate exits 2 and writes an INVALID report when one can safely be formed. Unhandled internal errors
fail with a traceback. Before writing, the output path must be checked against the artifact,
descriptor and every existing file in the input bundle, including files not used by the analysis.
Lexical aliases, symlinks and hard links to any input are protected. A collision returns exit 2
without changing any input or writing a report at that protected path. For a directory bundle, any
output path that resolves beneath the bundle root is also refused, including a new file, a path
through a lexical `..` alias and a path whose parent is a symlink into the bundle. Containment is
checked after symlink resolution and again by the identity of the nearest existing ancestor
directory, so a report never becomes a bundle entry. An output path that is a directory, or a
directory bundle with more than 4096 entries to protect, also returns exit 2 with no report. There is no implicit discovery: every analysis read names an expected entry.

## Read limits

Tool resource limits, not metric parameters. ZIP and directory bundles share them.

| Limit | Value |
| --- | --- |
| Entries per bundle (`MAX_ENTRIES`) | 64. For directories every file and subdirectory counts |
| Declared uncompressed bytes per bundle (`MAX_TOTAL_BYTES`) | 64 MiB, checked at open from metadata before any payload read |
| Evidence manifest or JSON document (`MAX_MANIFEST_BYTES`) | 2 MiB per read |
| Streaming chunk (`READ_CHUNK_BYTES`) | 64 KiB |
| RGBA capture | 147,456 bytes (192 by 192 by 4) |
| Active atlas row | 1,572,864 bytes (1536 by 256 R32 float) |

`open_bundle(path, limits)` refuses at open, before payload reads, on entry count, summed declared
size, duplicate names, unsafe names and, for directories, any symlink at or below the supplied root
(ancestors of the root are not inspected). Unsafe names are absolute paths, drive prefixes, any `..`
segment and backslashes. `Bundle.names()` lists files only, as POSIX relative names.
`Bundle.read(name, max_bytes)` refuses an unsafe name, a missing name or a declared size above
`max_bytes`, then streams in bounded chunks. Actual decompressed bytes are counted while reading,
and the read refuses if the count exceeds the declared size or the aggregate cap, or ends short of
the declared size. `read_capped(stream, limit)` is the streaming primitive. It stops after at most one
chunk past `limit`. No archive is extracted and no buffer is sized from an unvalidated count.
Only ZIP_STORED and ZIP_DEFLATED entries are supported. Other methods refuse with `zip-method`
before payload reads. Decode the complete stored or deflated payload with bounded streaming reads,
counting actual output independently of the uncompressed-size declaration. An understated size with
a CRC adjusted to match only the declared prefix must still refuse. A `ZipExtFile` truncated at
`file_size` is not evidence of the actual decoded length. A size or CRC mismatch is `entry-integrity`.
If actual output exceeds a per-read or aggregate cap, that violation is `actual-bytes` before the
size-integrity result. The supplied directory root itself may not be a symlink, while ordinary
parent aliases such as macOS `/var` remain permitted. A ZIP path that is a symlink to a regular file
is read as that file.

The ZIP reader parses the end record and central directory itself, so the entry count is refused
from the end record before the central directory is read. ZIP64, multi-disk and encrypted entries,
a central directory not adjacent to its end record, a local name that differs from its central
name, and bytes after a deflate stream are `entry-integrity`. Sizes and CRCs come from the central
directory, so data-descriptor archives read normally. A directory entry that is neither a regular
file nor a directory is `unsafe-path`. JSON documents also refuse duplicate object keys as `schema`.

### Refusal codes

`InputRefused.code` is one of these. Checks run in the listed order within each validator.

| Code | Meaning |
| --- | --- |
| `entry-count` | More entries than the limit |
| `declared-bytes` | Summed declared size above the bundle cap |
| `entry-bytes` | One entry's declared size above the per-read cap |
| `actual-bytes` | A stream yielded more bytes than allowed |
| `entry-integrity` | Actual bytes differ from the declared size, or a ZIP CRC or structure error |
| `zip-method` | ZIP compression method is neither STORED nor DEFLATED |
| `duplicate-name` | Repeated entry name, or the same path mapped twice in a descriptor |
| `unsafe-path` | Absolute, drive, `..` or backslash name |
| `symlink` | A symlink at or below a directory bundle root |
| `missing-entry` | A required named entry or capture is absent |
| `schema` | Wrong schema string or version, missing or unknown field, wrong enum value |
| `non-finite` | `NaN` or `Infinity` token in any JSON input, rejected at parse |
| `identity` | Sidecar build, phase or source commit differs from the expected one |
| `dimensions` | Declared width, height, rows or byte count inconsistent with the frozen shape |
| `length` | Actual payload length differs from the declared length |
| `sha256` | Payload or archive digest differs |
| `grid` | Not exactly 13 stations by 61 indexed probes, or inconsistent grid counts |
| `recorded-mismatch` | A recorded probe pixel, byte index, red byte or float32 ratio disagrees with the capture |

Evidence order: parse (`non-finite`), `schema`, `missing-entry`, `dimensions`, `length`, `sha256`,
`grid`, `recorded-mismatch`. Atlas order: parse, `schema`, `identity`, `dimensions`, `length`,
`sha256`. `validate_evidence` does not replay station sums. A sum or reach that does not reproduce
bit for bit is `MetricInvalid("replay-mismatch")` from `original_station_reaches`.

`MetricInvalid.code` is `coverage`, `non-finite`, `stations` or `replay-mismatch`.

## Retained mode input

The committed `docs/superpowers/plans/proofs/2026-10-07-point-shadow-seam-artifact.zip`, archive
SHA-256 `ef5ce8d9e72cf5f0d2638aedb0537e48dc80094b95ac73c5313b04d58a4743b5`, checked before opening.
Named entries:

- `_temp/point-shadow-seam-37482238342-1-55a8ed392a134a15baa7fc631f47c183/point-shadow-seam.json`
- the two captures named by its `captures[].file`, relative to the manifest directory
- `KhaozEngine/KhaozEngine/TestResults/seam.trx`

The ordinary schema-v1 evidence (`khaozengine.point-shadow-seam-evidence`) is unchanged.

The archive is read once into memory under the 64 MiB cap, digest-checked, and those same bytes are
opened, so the checked archive is the one analysed. The retained candidate needs the visible-floor
certificate under the approved `Wall` draw list (the source draw list, not a supplied attestation)
and complete probe, band and control coverage. Failed coverage is `MetricInvalid("coverage")` and an
INVALID report.

`shadow_inputs` pins the retained camera, recorded statistic, station reaches, soft digest and
historical assertion transcribed from this archive. Compare mode uses them for G4 and the historical
row without rereading the archive. Retained mode rechecks every pinned value against the archive and
fails with an internal error, not an INVALID report, if one disagrees, because that is a code defect.

## Compare mode input

All comparison schemas are local to this diagnostic tool, version 1. Unknown fields are refused.
Commits and trees are 40 lowercase hex, digests 64 lowercase hex. A phase is `[phiX, phiY]` in screen
pixels from the frozen set. Paths are POSIX names relative to the bundle root.

### Expected-proof descriptor

Supplied by the later approved proof request. Never inferred from the bundle.

```json
{
  "schema": "khaozengine.point-shadow-control-expectation",
  "schemaVersion": 1,
  "designSha256": "<design digest>",
  "baseline": {"sourceCommit": "<B commit>", "sourceTree": "<B tree>"},
  "mutant": {"sourceCommit": "<M commit>", "sourceTree": "<M tree>"},
  "mutation": {"changedPaths": ["KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs"], "diffSha256": "<pinned diff digest>"},
  "workflow": {"repository": "<owner/name>", "workflowPath": "<path>", "ref": "<explicit source ref>", "runId": 0, "runAttempt": 0, "jobId": 0},
  "host": {"computerName": "<claimed common host>", "sameVm": true},
  "expectedCollectorTestName": "<explicit fully qualified collector name>",
  "phases": [[0, 0], [0.5, 0], [0, 0.5], [0.5, 0.5]],
  "captures": [
    {"build": "B", "phase": [0, 0], "manifest": "<path>", "atlasRow": "<path>", "atlasSidecar": "<path>"}
  ],
  "trx": {"B": "<path>", "M": "<path>"},
  "attestation": "<path>"
}
```

`captures` holds exactly eight records, one per build and phase. Each manifest is ordinary schema-v1
evidence whose `captures[].file` entries sit beside it.

### Source-equivalence attestation

Written by the proof workflow. Its claims are compared with the descriptor. The tool does not
inspect git or GPU bindings itself. A missing attestation or missing field is INVALID.

```json
{
  "schema": "khaozengine.point-shadow-source-equivalence",
  "schemaVersion": 1,
  "workflow": {"repository": "", "workflowPath": "", "ref": "", "runId": 0, "runAttempt": 0, "jobId": 0},
  "host": {"computerName": "", "sameVm": true},
  "baseline": {"sourceCommit": "", "sourceTree": ""},
  "mutant": {"sourceCommit": "", "sourceTree": ""},
  "mutation": {"changedPaths": [""], "diffSha256": ""},
  "drawList": [
    {"name": "floor", "kind": "floor", "min": [-20, 0, -20], "max": [20, 0, 20]},
    {"name": "wall", "kind": "box", "min": [2, 0, -3], "max": [2.4, 3, 3]}
  ]
}
```

The workflow repository, path, explicit ref, run, attempt and job, source/mutation identities and
host claim must equal the descriptor. `sameVm` must be true. Missing or mismatched claims are
INVALID under G1. All of these are supplied claims, never independent source or VM verification.

### Two collector TRX inputs

Both descriptor-named TRX files are required, hashed and parsed as XML with the normal TRX namespace.
Each contains exactly one executed `UnitTestResult`, with `outcome="Passed"` and a `testName` exactly
equal to `expectedCollectorTestName`. Accepting any other Passed test is insufficient. No skipped,
NotExecuted, error or additional case is accepted. Result-summary counters must agree and report
zero errors, notExecuted and notRunnable. Missing, malformed or wrong-case TRX data makes G1 fail.
Each result's `computerName` must be present and equal the expected and attested host name. A missing
or different name makes G2 fail. Matching names corroborate the supplied same-VM claim only.

The future collector records evidence without asserting the original 8 mm bound. Its Passed outcome
is an executed collection result, not a passed seam assertion. The report records `assertsBound`
as false according to that collector contract. The retained Failed assertion remains the first row.

### Active-atlas sidecar

One per build and phase, beside its raw row. The row is the active base-atlas row only, little-endian
IEEE754 R32 float, row-major.

```json
{
  "schema": "khaozengine.point-shadow-active-atlas-row",
  "schemaVersion": 1,
  "build": "B",
  "phase": [0, 0],
  "sourceCommit": "<commit>",
  "activeRow": 0,
  "atlasRows": 8,
  "atlasWidth": 1536,
  "atlasHeight": 2048,
  "faceResolution": 256,
  "rowWidth": 1536,
  "rowHeight": 256,
  "format": "R32Float little-endian IEEE754",
  "bytes": 1572864,
  "sha256": "<row digest>",
  "binding": {"boundTextureIsAtlas": true, "check": "BoundPointShadowTexture is PointShadowTexture"}
}
```

`atlasWidth` must equal 6 times `faceResolution`, `atlasHeight` must equal `atlasRows` times
`faceResolution` and `0 <= activeRow < atlasRows`. The binding is a supplied claim written by the
workflow. The analyzer reads it and never inspects the GPU binding itself. A `false` binding is
recorded as `binding_verified = False` and fails G5. A missing binding is `schema`. Gate and
certificate reasons describe the binding and the attested draw list as supplied claims that are
not independently verified.

## Conventions

- Units are metres. Display values in millimetres are labelled separately. JSON keeps full precision.
- Matrices are System.Numerics row-major M11 to M44, row vectors, renderOrigin subtracted first.
- Pixel (i, j) is the screen square [i, i+1) by [j, j+1), shaded at its centre.
  Screen x = (ndc_x + 1) * 96 and screen y = (1 - ndc_y) * 96 for the 192 by 192 capture.
- Floor polygons are `(x, z)` tuples on y = 0. Footprints are exact preimages of pixel squares.
- A pixel differs when any of its four RGBA bytes differs. Pixel lists are sorted by `(x, y)`.
- Phases are tuples `(phiX, phiY)` as Python keys and `[phiX, phiY]` in JSON.

## Frozen parameters

| Name | Value |
| --- | --- |
| `T_METRES` | 0.008 |
| `PHASE_BUDGET_METRES` | T/4 = 0.002 |
| `PHASES` | (0, 0), (0.5, 0), (0, 0.5), (0.5, 0.5) |
| `PHASE_TOLERANCE_PX` | 0.02 |
| `CAMERA_RELATIVE_TOLERANCE` | 1e-6, as `abs(a-b) <= 1e-6 * max(1, abs(a), abs(b))` |
| `F_REGION` | y = 0, X [1.95, 5.25], Z [3.75, 7.15] |
| `NON_SEAM_CONTROLS` | X [2.0, 2.4] Z [6.7, 7.0], X [4.8, 5.2] Z [6.8, 7.1] |
| `MAX_ANGLE` | 16 * (pi/2) / 256 rad |
| `MASK_MARGIN_FACTOR` | 1 + 1e-3 |
| `ACROSS_XZ`, `ALONG_XZ` | (1.5, -1) / sqrt(3.25), (1, 1.5) / sqrt(3.25) |
| `BAND_HALF_WIDTH` | sqrt(3.25) / 40 m |
| `BAND_HALF_LENGTH` | 0.9 m |
| `ENDPOINT_STRIP` | abs(u) in [0.85, 0.9]. The lit strip at negative u must average above 0.9, the shadow strip at positive u below 0.1 |
| `BUMP_STATIONS` | 3, 4, 5 |
| `COMPLETENESS_RELATIVE_TOLERANCE` | 1e-9 |

`SCENE_EXPECTATION` (G3) and `DEVICE_EXPECTATION` (G2) in `shadow_compare.py` hold the retained
scene and device values from design section 6. `APPROVED_DRAWS` in `shadow_geometry.py` is the
approved `Wall` draw list.

## Gates, metrics and outcomes

Gates G1 to G7 follow design section 6. Each returns `GateResult(gate, passed, reason)`.
`gate_phase_inventory` is an additional completeness gate. `classify_metric` returns INVALID when
any supplied gate failed, any of G1 to G7 is missing, a phase is missing or extra, or a bump is
non-finite. The reason names the first failed gate, and no phase range or separation is reported.

`gate_g2_device(manifests)` requires all eight slots, the frozen device facts and an identical
complete device section in every manifest. `gate_g3_scene(manifests)` requires all eight slots and
the frozen scene facts. Recorded station `on`, `from`, `to`, probe `world`, scene `across`, `window`
and `frameHalfExtent` must be float32 bit-identical across B/M and all phases, including signed zero.
The fixed light and approved draw geometry remain unchanged. `frameCentre` may vary with phase but
must match between B and M within that phase.

`G7PhaseFacts(min_plain_red, original_endpoints, candidate_endpoints)` contains the minimum plain
red over both metrics' read sets and 13 `(lit, shadow)` pairs per metric. `gate_g7_preconditions(facts)`
requires all eight slots, `min_plain_red > 24` and finite endpoints. Original values are rounded to
float32 and compared strictly against C# float32 `0.9f` and `0.1f`. Candidate endpoint means remain
double and compare strictly against double `0.9` and `0.1`. Equality fails for either metric.
No rounding of candidate endpoints to float32 is permitted.

Compare orchestration assigns every failure to a gate, so a report can always be formed:

| Failure | Gate |
| --- | --- |
| Attestation missing, unparseable or mismatched, manifest provenance, collector TRX missing, malformed or not one executed Passed case with the explicit name | G1 |
| Device facts, TRX `computerName` missing or differing from the expected or attested host | G2 |
| Each build's cameras against the pinned retained camera, and B and M cameras differing within a phase | G4 |
| Atlas sidecar or row refused, plain or atlas pairs not identical | G5 |
| Certificate (attestation `drawList`), coverage, candidate `coverage` | G6 |
| Candidate `non-finite` (a zero plain byte in a band), plain red or endpoints | G7 |
| Manifest or capture refused, original `replay-mismatch` | phase-inventory (completeness) |

A refused descriptor or bundle, or a failed phase inventory, returns an INVALID report whose gates
are all failed with a "not evaluated" reason.

A failed visible-floor certificate, required probe/band/control coverage, or area completeness
(`MetricInvalid("coverage")`) makes G6 fail before either metric is classified. Coverage is reported
per build and phase. Its reason and uncovered items stay visible. No clipping or region movement
turns failed coverage into a valid measurement.

Then, per metric, in order: INCONCLUSIVE-PHASE when `W_B > T/4` or `W_M > T/4`. INCONCLUSIVE-UNDETECTED
when `abs(bump_M) <= T` at any phase, or `min abs(bump_M) - max abs(bump_B) <= max(T/4, 2 max(W_B, W_M))`.
BASELINE-EXCEEDS-BOUND when `abs(bump_B) > T` at any phase. Otherwise BASELINE-WITHIN-BOUND.
`W` is the signed bump range over the four phases. Every outcome after INVALID reports both phase
ranges. INCONCLUSIVE-UNDETECTED and both BASELINE outcomes also report the separation and its guard.

`area_band_reach(band, samples)` takes `(footprint, plain_red, soft_red)` samples. `V = soft / plain`
in double precision, constant over the footprint. The overlap areas must sum to the band area within
1e-9 relative, otherwise `coverage`. A zero plain byte is `non-finite`.
Candidate reaches and their detrend use double precision with `single_precision=False`. Only the
original replay uses `single_precision=True` and the recorded float32 arithmetic.

`check_measurement_coverage` reports uncovered items as `("probe", (i, j))`, `("band", index)` and
`("control", index)`. It never clips or moves a band, control or F.

`crossing_mask(camera, light, pixels)` returns the subset of `pixels` in the conservative mask.

## Report records

Classification record keys: `metric`, `outcome`, `reason`, `phaseRange` (`B`, `M`), `separation`,
`separationGuard`, `bumps` (`B` and `M` lists of `[phiX, phiY, bump]` in `PHASES` order), `scope` and
`nonClaims`. Scope is "diagnostic, this scene, these four phases, certified floor only". Non-claims
are "no metric adoption", "no Catalog C waiver", "no shader-defect attribution" and "no render-pass
or Catalog-clear flag". No record carries a pass, clear, adoption or waiver flag.

Difference record keys: `certifiedInsideMask`, `certifiedOutsideMask` and
`outside-certified-floor-region`, each `{"count", "pixels"}`. Outside-F pixels carry no
floor-confinement conclusion.

Retained report keys: `tool`, `mode` (`retained`), `status` (`REPORT-ONLY`, or `INVALID` on refusal),
`designSha256`, `units` (`m`), `parameters`, `inputs` (`artifactSha256`, `manifestSha256`,
`trxSha256`, `captureSha256`), `coverage` (`certificate`, `measurements`, `drawList`), `rows`,
`historicalAssertion` (`outcome`, `testName`, `boundMetres`) and `notAvailable` (`mutant`, `phases`,
`atlas`). Rows, first to last: `retained-historical` (the recorded statistic, unrecomputed, status
`HISTORICAL`), `retained-replay-original` and `retained-candidate-area-band` (status `REPORT-ONLY`).
Each row has `label`, `metric`, `bump`, `bumpMillimetres`, `worstElsewhere`, `reach` and `status`.
The replay and candidate rows add `residuals`, `minPlainRed` and `endpoints`. The retained report
carries no section 9 outcome and no B or M data. A refused retained run writes `tool`, `mode`,
`status` (`INVALID`), `designSha256`, `units`, `parameters` and `refusal` (`code`, `detail`).

Compare report keys are exactly `tool`, `mode` (`compare`), `status` (`COMPLETED` or `INVALID`),
`designSha256`, `units` (`m`), `parameters`, `inputs`, `claims`, `rows`, `historicalAssertion`, `gates`,
`coverage`, `differences`, `classifications` and `reportedOnly`. COMPLETED means the analysis
completed. It is not a render or seam pass. On INVALID, unavailable values may be null or lists
may be empty, but every G1 to G7 result and the phase-inventory result carries its reason, and both
classification records are INVALID with null ranges and separation.

- `inputs` has `expectationSha256`, `files` (bundle-relative name to SHA-256 for every required
  input read), and `trx` keyed by B and M. Each TRX record includes `sha256`, `testName`,
  `computerName`, `executed`, `passed`, `skipped`, `errors` and `assertsBound` (false).
- `claims` has `sourceEquivalence` (`kind: "supplied-claim"`, `matchesExpectation`), `workflow`
  (`kind: "supplied-claim"`, `value` equal to the supplied workflow object), `host`
  (`kind: "supplied-claim"`, `value` equal to the supplied host object, `trxComputerNamesAgree`),
  and `independentlyVerified` (false). Missing claims remain unavailable rather than invented.
- `rows` starts with `retained-historical`, preserving its recorded original bump and Failed
  assertion. A completed compare then has sixteen rows, one per build/phase/metric, in build,
  frozen phase and original/candidate order. Paired rows include `label` (`paired-original` or
  `paired-candidate-area-band`), `build`, `phase`, `metric`, `reach`, `residuals`, `bump`,
  `bumpMillimetres`, `worstElsewhere`, `minPlainRed`, `endpoints` (13 `[lit, shadow]` pairs) and
  `status` (`DIAGNOSTIC`). The historical row comes from the pinned retained record. The original
  `minPlainRed` covers the 793 probes, the candidate one covers every positive-overlap band pixel,
  and G7 uses the smaller.
- `gates` is a list of `{gate, passed, reason}` for G1 to G7 followed by phase-inventory.
- `coverage` has eight `{build, phase, nominalPhase, achievedPhase, bandCentres, certificate,
  measurements, controls}` records in build then frozen phase order. `phase` and `nominalPhase` are
  the descriptor phase `[x, y]` in pixels. `achievedPhase` is the absolute `[x, y]` screen shift in
  pixels that G4 compares with `nominalPhase` against the 0.02 px tolerance: the projection of
  station 4's recorded on-point under this camera minus its projection under the same build's
  (0, 0) camera. It is `[0, 0]` at phase (0, 0) and null when either manifest is unavailable or
  the cameras cannot project the point. `bandCentres` lists the 13 recorded on-points `[x, z]` in
  metres that centre the station bands, in station order, or null when the evidence is unavailable.
  These records report data only and change no gate, tolerance, region or outcome.
  `certificate` has `valid`, `reason` and `certifiedPixelCount`. `measurements` has `valid`,
  `reason` and `uncovered`. The two control records have `index`, `outsideMaskArea` in square metres
  and `outsideMaskPixelCount`, for wholly outside-mask certified footprints.
- `differences` has four records in frozen phase order, each with `phase` and the three difference
  record keys above. Every differing pixel appears in exactly one group.
- `classifications` maps `original` and `candidate` to their classification records above.
- `reportedOnly` has `baselineZeroSoftEqualsRetained`, comparing fresh B(0,0) soft bytes to the
  retained soft bytes with digest `ae1914c9d740bee9c730689c6451cf4262602c3def73247aedfd00cc518da405`.
  This boolean never gates the experiment. `historicalAssertion` has the retained outcome,
  testName and boundMetres, as in retained mode.

## Non-claims

No result here adopts a metric, changes the 8 mm bound, clears Catalog C or attributes a shader
defect. Synthetic test fixtures prove analyzer arithmetic only. They are not renders of the clamped
control, which remains mandatory evidence from a separately approved hosted job.

## Pre-GREEN numerical interpretation

Geometric projection and overlap use the recorded JSON matrix numbers directly as double-precision
values. Do not round them back to float32. Float32 is used only for the original replay and the
explicit source-bit identity/original-endpoint checks. Candidate endpoint means are the sum of
(overlap area * visibility) divided by the sum of overlap areas over the endpoint strip.
Output/input alias checks occur before analysis, keeping refusal paths cheap and inputs unchanged.
