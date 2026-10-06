# Point-shadow control Stage 0 preparation brief

Status: proposed for coordinator approval. Design CD11 is frozen at
71fddcb166bb2e3745ae2092f23fbc1dffcd57fe, design SHA-256
1c4ebca1d297405cb4b2f7b08f4ef585acc58946506f4a8d8569681499ea6a9d.
This brief grants no implementation, compiler check or verification run. It implements only
Stage 0 of POINT-SHADOW-SEAM-CONTROL-DESIGN-2026-10-07.md after separate approval.

## Outcome and ownership

Prepare a Python-standard-library offline analyzer that validates retained evidence and the future
paired B/M artifact shape, computes the frozen measurements and reports the frozen outcomes.
It never renders, starts a process, accesses the network, invokes git or changes an input artifact.
The original shader, 8 mm assertion, phases, regions and gates are unchanged.

Proposed execution worktree after preparation approval:
/Users/antonio/KhaozEngine/.worktrees/wa-shadow-control-cpu, branch fix/wa-shadow-control-cpu,
rooted at exact approved design checkpoint 71fddcb166bb2e3745ae2092f23fbc1dffcd57fe. Guard path,
branch and base. This is an isolated diagnostic continuation, not an engine integration branch.
No worktree or implementation worker is created by publishing this brief.
Copy only this accepted brief into that worktree and record its approval commit. Do not merge the
historical world-authoring planning branch or change the frozen design while implementing Stage 0.

One implementation worker owns tools/shadow-seam-analysis and its outcome records. Fresh source
review follows. Root verifies results, commits and pushes only within granted scope. No main/feed,
version, package, runtime, C# test, shader or workflow change. Retain all earlier proof branches.

## Files and responsibilities

All proposed files are new under tools/shadow-seam-analysis:

| File | Responsibility |
| --- | --- |
| shadow_inputs.py | Read bounded ZIP/directory artifacts, validate the existing evidence schema, hashes, grid and provenance, and normalize the future comparison descriptor |
| shadow_geometry.py | Recorded-matrix floor projection, fixed F visibility/coverage certification, pixel footprints, convex overlap and conservative face-crossing mask |
| shadow_metrics.py | Original float32 replay, area-band integration and the frozen detrend/statistics in metres |
| shadow_compare.py | G1 to G7, paired identity/atlas/plain-image checks, outside-F reporting and prospective outcome ordering |
| analyze.py | Two explicit offline modes, retained and compare, with JSON output and no implicit discovery |
| test_shadow_analysis.py | Exactly 24 finite unittest methods listed below |
| README.md | Invocation, input contract, units, limits, outcomes and non-claims |

Keep these concerns separate. No dependency installation, engine API or second renderer. The
validated 2026-10-07-point-shadow-seam-offline.py remains immutable and is the original replay
reference. Port only its applicable arithmetic, with bitwise regression coverage.

## Input and output contract to pin during preparation

Retained mode reads the committed 2026-10-07-point-shadow-seam-artifact.zip, expected archive digest
ef5ce8d9e72cf5f0d2638aedb0537e48dc80094b95ac73c5313b04d58a4743b5, and the existing schema-v1
manifest, raw RGBA and TRX. It reports the historical failed assertion and candidate area-band
value as REPORT-ONLY. Missing mutant, phases or atlas data are not fabricated or interpreted as
a failed paired experiment. No retained-data value changes any frozen choice.

Compare mode takes an explicit expected-proof descriptor and an explicit captured bundle. The
descriptor names the approved design digest, exact B/M source commits and trees, permitted mutation
diff hash, workflow/run identity, fixed phase list and expected file mapping. These expectations
are supplied by the later approved proof request, not inferred from whichever files are present.
The bundle supplies eight existing evidence manifests and RGBA pairs, eight active-atlas row files
and sidecars, two TRX files and the workflow's source-equivalence attestation. An atlas sidecar
names build/phase/source, active row, full atlas dimensions, row dimensions, R32 little-endian
format, byte length and hash, plus the binding-identity check from the approved fixture. Stage 0
validates these facts but does not invent a writer or independently claim to have inspected git or
GPU bindings. Missing required attestations fail INVALID.

The ordinary schema-v1 evidence is unchanged. Any additional comparison schema is local to this
diagnostic tool. Record its exact field names in README before review so Stage 1 can produce it.
No private game or oracle input is permitted. Read only named files under the supplied bundle.
Reject duplicate names, traversal/absolute paths, inconsistent dimensions, incomplete data or
non-finite measurement inputs. Proposed read limits are 64 entries and 64 MiB declared uncompressed
bundle data, checked before reads, with each evidence manifest at its existing 2 MiB cap. The
expected atlas row is 1,572,864 bytes and RGBA capture 147,456 bytes. These are tool resource limits,
not metric changes. Never extract an unchecked archive or allocate from an unvalidated count.

Output records input hashes, design digest, mode, units, every gate outcome and reason, region
coverage, per-phase metrics, phase ranges, separation and diagnostic classifications. The retained
historical row appears first. All differing pixels outside the certified floor set retain their
coordinates and count without a floor-confinement claim. Preserve full precision in JSON and label
display values in millimetres separately. Never emit a generic render-pass or Catalog-clear flag.
Exit 0 means the requested analysis completed, not that a shader passed. Invalid input/gates return
exit 2 with an INVALID report when a report can safely be formed. Unhandled internal errors fail.

## Finite test inventory

One method per row. Small deterministic subcases are permitted inside a named method. No sleep,
timing assertion, repeated test invocation, contention load or random sweep. Reuse bounded immutable
fixture bytes rather than cloning a full bundle per assertion. Synthetic adapters are file records,
not an engine or GPU mock that pretends a render occurred.

| # | Method | Observable requirement |
| --- | --- | --- |
| 1 | test_bundle_schema_and_resource_limits | Required schema, entry/byte limits and duplicate/path refusals occur before payload allocation |
| 2 | test_capture_dimensions_lengths_and_hashes | Wrong RGBA/atlas length, shape or hash refuses |
| 3 | test_grid_and_recorded_byte_integrity | Exactly 13 by 61 indexed samples, correct capture bytes and finite ratios |
| 4 | test_original_float32_replay | Original sums, reaches, residuals and statistics reproduce the recorded bit patterns |
| 5 | test_provenance_and_mutation_attestation | Missing or mismatched expected B/M identity or mutation proof is INVALID |
| 6 | test_exact_phase_inventory_and_achieved_phase | Four fixed phases only, no duplicate/missing phase, achieved camera phase satisfies the frozen check |
| 7 | test_active_atlas_identity | Paired active-row bytes, row metadata and binding attestation must agree |
| 8 | test_plain_capture_identity | A changed paired plain byte invalidates comparison |
| 9 | test_visible_floor_geometry_certificate | Orthographic rays, fixed F, approved draw bounds and viewport/depth coverage are required, never inferred from colour |
| 10 | test_every_measurement_footprint_is_certified | Any required probe/band/control footprint outside certified F invalidates, with no clipping or moved region |
| 11 | test_noncertified_differences_are_reported | Outside-F changes are retained and receive no floor-confinement conclusion |
| 12 | test_crossing_mask_exercise_and_confinement | Mutation must affect the frozen crossing bands, with no certified-floor change outside its conservative mask |
| 13 | test_polygon_overlap_closed_forms | Areas 0, 1 and 7/8 match the design within 1e-12 square metres |
| 14 | test_constant_visibility_physical_reach | Visibility 0, 0.5 and 1 yield 1.8, 0.9 and 0 m within 1e-9 m |
| 15 | test_halfplane_pixel_edge_at_four_phases | The four analytic pixel-edge fixtures each yield 0.9 m within 1e-9 m |
| 16 | test_detrend_constant_and_known_step | Constant has zero bump, the frozen 0.04 m three-station step yields 0.04*(10/13-12/182) within 1e-12 m |
| 17 | test_area_coverage_and_physical_units | Incomplete band coverage or a unit conversion error cannot produce a valid area metric |
| 18 | test_frozen_parameters | T, phase budget, strict separation, F, bands and control regions equal the approved values |
| 19 | test_invalid_has_priority | Any failed validity gate prevents metric classification |
| 20 | test_phase_inconclusive | Phase range above T/4 yields INCONCLUSIVE-PHASE, preserving the boundary rule |
| 21 | test_control_undetected_and_separation_boundary | Any mutant at/below T or insufficient/equal separation yields INCONCLUSIVE-UNDETECTED, including zero-range equal effects |
| 22 | test_baseline_exceeds_bound | A detected control and baseline over T report BASELINE-EXCEEDS-BOUND, never a proved shader cause |
| 23 | test_baseline_within_bound | The frozen positive diagnostic classification is scene/phase-specific and grants no metric adoption or Catalog waiver |
| 24 | test_retained_mode_is_report_only | A single retained artifact produces its two numerical metrics and failed historical assertion, never fabricated paired-control evidence |

Fixtures for classification supply known scalar series and validity facts. They do not claim real
negative-control rendering. Geometry and input fixtures separately prove those computations.

## Preparation and verification gates

1. After explicit code-preparation approval, create the named worktree and author tests first.
   Define the minimum interfaces required for collection. Do not fill in behavior merely to make
   tests pass before their RED is observed. Record exactly what is absent or fails.
2. Request one finite RED window for the command below. Expected failures must identify absent
   analyzer behavior, not unexplained environment, syntax or collection errors. Stop and report
   the result, preserving the log. No automatic retry.
3. Implement only the approved Stage 0 behavior after the tests-first checkpoint and its permitted
   code scope. Perform fresh source review and fix findings without running ungranted commands.
4. Request one GREEN invocation of the same 24-method suite, with zero skips. Only after it passes,
   request or use an explicitly granted single retained-artifact analysis command. No hidden GPU
   environment, shader compiler, C# build, full engine suite or external process is used.
5. Root independently checks counts, output hashes, original bitwise replay and REPORT-ONLY status,
   then records the candidate value without changing any frozen parameter. Save the report under
   docs/superpowers/plans/proofs and push the scoped validated result after light guards. Later
   Stage 1, compiler checks and Stage 2 hosted proof remain separate approvals.

Proposed test command, from the future worktree, one invocation per separately granted RED/GREEN:

```bash
bash /tmp/grimhollow-orch/slot-run.sh 'wa:shadow-control-cpu-<red-or-green>' /tmp/grimhollow-orch/wa-shadow-control-cpu/<red-or-green>.log -- python3 -m unittest discover -s tools/shadow-seam-analysis -p 'test_shadow_analysis.py' -v
```

Expected GREEN is exactly 24 tests, zero failures/errors/skips. Initial RED is recorded honestly,
with all 24 methods collected where possible and no passing claim. Unexpected failure stops the
window. Do not install missing dependencies or widen the run.

Proposed retained-data command, only after GREEN and its explicit finite grant:

```bash
bash /tmp/grimhollow-orch/slot-run.sh 'wa:shadow-control-retained-analysis' /tmp/grimhollow-orch/wa-shadow-control-cpu/retained.log -- python3 tools/shadow-seam-analysis/analyze.py retained --artifact docs/superpowers/plans/proofs/2026-10-07-point-shadow-seam-artifact.zip --output /tmp/grimhollow-orch/wa-shadow-control-cpu/retained-analysis.json
```

Expected exit 0 is analysis completion only, with original failed assertion and REPORT-ONLY candidate
data. Missing input fails. No newly rendered control exists at Stage 0. The Stage 0 allowance stays
6 to 9 engineer-hours, provisional, within the approved design's total planning estimate.

## Outcome

Brief proposed. No Stage 0 worktree, worker, code, RED/GREEN command or new capture has started.
Record approval, source hashes, review, commands/exits, exact test counts, report hash and commit
when authorized. Catalog C remains closed.
