# Draft: clamped-face negative control for the point-shadow seam probe

Status: DRAFT for coordinator review under CD11. Design only. Nothing here authorizes code, builds, tests, captures, GPU work,
workflow execution or implementation/proof-branch publication. Code preparation and the single future GPU job each need their own approval.
Source and evidence anchors are in [the source register](../superpowers/plans/proofs/2026-10-07-point-shadow-control-source-evidence.md).

## 1. Question and what stays fixed

The retained WARP probe fails the 8 mm seam assertion (bump -8.728723 mm). Offline resampling of the same RGBA
moves that number by more than the failure margin (bilinear -6.291010 mm, quarter-pixel shifts -5.58 to -10.50 mm).
That shows the statistic is phase sensitive. It does not show whether the production filter seams.

The question this design answers: under a measurement that is demonstrably able to detect a deliberately clamped
face kernel in the same scene, does the production Soft filter show a crossing bump above 8 mm?

Fixed throughout:

- The original test, its 8 mm assertion, its messages and the production shaders are unchanged.
- The retained artifact (archive ef5ce8d9...) and its -8.728723 mm result are the historical baseline. Every result
  table reports that row first, unrecomputed.
- The 8 mm threshold `T = 0.008 m` is not widened, retuned or replaced. A candidate that cannot meet it is a
  finding, not a reason to move it.
- No metric is adopted into the test by this design. Any later metric change is separate, reviewed work.

## 2. Active path, pinned from source and evidence

The seam scene draws a floor and one box wall with a static shadow request. It has no skinned caster, so the
transient row is -1 and `samplePointShadowCombined` returns through `params.w < 0` to the base-only
`samplePointShadow`, then `samplePointShadowSoft`. The retained manifest confirms `transientAtlasRows: 0`.

The active Soft route is therefore:

1. `samplePointShadowSoft` builds `dir`, the tangent basis `tx, ty`, the dither rotation and `searchAngle`.
2. `pointShadowBlockerSearch`: 6 taps, each through `pointShadowDepthAt`. No hit returns 1.0 (no filter run).
3. Kernel angle from the blocker distance, capped at `maxAngle = 16 * (pi/2) / 256 = 0.0981748 rad`.
4. `pointShadowFilterDisc`: 9 taps, each through `pointShadowDepthAt`, compare then average.

`pointShadowDepthAt` runs `pointShadowFace` per tap, so a tap whose direction leaves the receiver's face lands in
the neighbouring face cell. That per-tap reselection is the thing a seam would break. The combined variants are not
reached and are not part of the control.

## 3. The negative control, precisely

Mutant `M` (search and filter both clamped). In `samplePointShadowSoft` only:

- Compute `baseFace` once per receiver: `pointShadowFace(dir, baseFace, unusedUv)`.
- Every blocker-search tap and every filter tap keeps its own direction `t`, exactly as now (same `tx, ty`,
  rotation, ring radii, tap counts, angles, `normalize`).
- Instead of `pointShadowDepthAt(atlas, samp, t, slot)`, the tap calls a proof-only
  `pointShadowDepthAtOnFace(atlas, samp, t, baseFace, slot)` that projects `t` onto `baseFace`'s plane using that
  face's own row of the convention table, not `t`'s dominant axis:

  | baseFace | ma | sc | tc |
  | --- | --- | --- | --- |
  | 0 (+X) | t.x | -t.z | -t.y |
  | 1 (-X) | -t.x | t.z | -t.y |
  | 2 (+Y) | t.y | t.x | t.z |
  | 3 (-Y) | -t.y | t.x | -t.z |
  | 4 (+Z) | t.z | t.x | -t.y |
  | 5 (-Z) | -t.z | -t.x | -t.y |

  then `uv = (sc, tc) / max(ma, 1e-6) * 0.5 + 0.5`, and the existing cell arithmetic with `face = baseFace`:
  same `cellSize`, `cellMin = (baseFace, slot) * cellSize`, same half-texel inset clamp, same
  `textureLod(..., 0.0).r`. An off-face tap therefore clamps to the edge texels of the original face.
- Bias, slope bias, `d`, the early-out, the kernel width formula, atlas row (`params.x`), atlas layout
  (`PointShadowAtlas`), sampler and the Hard path are untouched. The combined functions are untouched.

Property that makes it a clean control: for every tap whose dominant axis equals `baseFace`, the table row is the
same expression `pointShadowFace` already evaluates, so `M` matches production for every receiver whose taps never
leave its face. `M` differs only where a tap crosses a face boundary. Section 6 gate G6 checks this empirically.

Why both stages are clamped. Clamping the filter alone leaves the blocker search reading across the boundary, so
the kernel width and the early-out stay correct and the control is weaker than the "every tap clamped inside its
own face cell" failure the test claims to catch. Clamping the search alone moves only the kernel width, a
second-order effect. `M` clamps both. Filter-only and search-only variants are excluded from the default job
(they add builds without changing the acceptance question).

Geometry predicts the control is exercised over a wide band, not three stations. Approximating the blocker as the
wall's vertical edge at (2, y, 3), the search disc at the station on-points reaches the -Y/+Z boundary for stations
0 to 9 and the filter disc for stations 2 to 8 (estimates, not results). The detrend line may therefore absorb part
of a clamped signature. That is a risk to detection, handled by the outcome rules, not by changing the statistic.

## 4. Mutant mechanism: options

No supported source-override hook was found in the receiver path examined. `ModelFrag` is a const string compiled at
`ModelRenderer.cs:291`, and Render3D and Gpu expose no source override or test hook. Options:

| Criterion (1 to 10) | M1 proof-only mutant branch | M2 test-only replica pipeline | M3 production source-transform seam |
| --- | ---: | ---: | ---: |
| Fidelity to the real receiver path | 9 | 4 | 9 |
| Isolation from shipped code | 8 | 9 | 3 |
| Engineering cost (higher is cheaper) | 8 | 3 | 6 |
| Production risk (higher is safer) | 9 | 10 | 3 |
| Reviewability | 8 | 4 | 5 |
| Total | 42 | 30 | 26 |

- M1. A disposable branch, never integrated, whose only difference from the baseline proof tree is the section 3
  change in `ShaderSources.Lighting.cs`. Same precedent as proof 8632b57. The real Scene3D pipeline renders it.
- M2. Rebuild the lit model pipeline in the test assembly from a modified `LightingCommonGlsl`. It would need the
  frame block, light buffer, cluster buffer and atlas bindings replicated, and its own proof that the unmutated
  replica matches production bytes. Too much new surface for one control.
- M3. An internal override hook in `ModelRenderer`. A production feature knob, shader hash pin churn and engine
  review for a diagnostic. Rejected by the brief's preference.

Recommendation: M1.

## 5. Measurement: options

All options keep the original detrend and bump definition (least-squares line over 13 station reaches, bump =
mean residual of stations 3 to 5, worst elsewhere = largest absolute residual of the others, `T = 8 mm`). They
differ only in how a station's reach is integrated and in what evidence exists.

| Criterion (1 to 10) | A1 original metric, rendered controls | A2 original plus area-band metric, rendered controls | A3 offline only, retained RGBA plus synthetic injection |
| --- | ---: | ---: | ---: |
| Can separate filter seam from sampling artefact | 6 | 9 | 2 |
| Cannot smooth away a real seam | 8 | 7 | 2 |
| Phase robustness | 3 | 8 | 6 |
| Cost (higher is cheaper) | 8 | 6 | 10 |
| Production risk (higher is safer) | 10 | 10 | 10 |
| Review burden (higher is lighter) | 8 | 6 | 9 |
| Total | 43 | 46 | 39 |

- A1 keeps 61 truncated point reads per station. The retained capture already shows a 4.9 mm phase swing on it, so
  it is likely to land in the phase-inconclusive outcome. It still runs inside A2 as the side-by-side baseline.
- A2 adds the area-band metric (section 7) on the same captures. It removes truncation phase jumps by exact
  footprint quadrature, with an along-edge band narrow enough not to average across stations.
- A3 needs no GPU but cannot produce the clamped negative control, which the brief makes mandatory. Its synthetic
  half survives as the analyzer self-test in stage 0.

Recommendation: A2 with M1. A3 is not a substitute.

## 6. Matched conditions and validity gates

Every gate is evaluated before any metric. Any failure makes the run INVALID.

| Gate | Requirement |
| --- | --- |
| G1 provenance | Baseline tree `B` = code checkpoint plus the proof test only. Mutant tree `M` = `B` plus the section 3 change only: `git diff --name-only B M` is exactly `KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs`, and the diff SHA-256 is pinned in the proof request before dispatch. Both commits are never integrated. Assembly revisions agree with each tree's commit, as the existing evidence provenance does. |
| G2 device | Both builds run in ONE hosted Windows job, same VM: Direct3D11Native by environment override, adapter "Microsoft Basic Render Driver", software adapter, no device loss. Identical device sections in every manifest. |
| G3 scene and atlas | Every manifest matches the retained `scene` block: light (0,5,0), radius 30, intensity 1, id 307, faceResolution 256, bias 0.01, slopeBias 0.02, Soft, lightSize 0.5, maxPenumbraTexels 16, maxShadowedLights 8, not degraded, `transientAtlasRows` 0, `softShadowedLights` 1, `plainShadowedLights` 0. |
| G4 camera | Projection and view rotation (upper 3 by 3) match the retained values within `abs(a-b) <= 1e-6 * max(1, abs(a), abs(b))`, render origin (0,0,0). At phase (0,0) the view-projection equals the retained one bit for bit. Achieved phase (section 8) within 0.02 px of nominal. |
| G5 matched data | For every phase, plain(B) and plain(M) are byte identical. The active base-atlas row is also byte identical between B and M, using the readback below. The receiver binding must reference that atlas. Any mismatch is INVALID before interpreting metrics. |
| G6 mutant exercised and confined | For every phase, soft(M) differs from soft(B) in at least one certified floor pixel whose footprint overlaps a station 3, 4 or 5 band, and in zero certified floor pixels outside the crossing mask. Certification is restricted to the fixed visible-floor region below. Differences on all other pixels are reported separately without a floor-confinement claim. Crossing mask: for each pixel-centre floor point, use the receiver direction and its dominant face. Measure angular distance to each boundary plane between that face and another axis, and conservatively expand by the pixel-footprint angular radius plus `maxAngle * (1 + 1e-3)`. The radius is the maximum angle between the centre ray and the four footprint-corner rays, computed with clamped dot products. The floor footprint lies inside that convex ray cone. Only the four planes bounding the receiver's dominant face matter, not a tie between two non-dominant axes. Do not use only corner inclusion, which can miss a band crossing a footprint. All actual taps are within `atan(maxAngle) <= maxAngle` of the centre ray. Certified floor pixels outside this conservative mask cannot cross a face under either kernel. Require a nonempty outside-mask set in both fixed non-seam rectangles defined below. |
| G7 preconditions | The existing per-station conditions hold for B and M at every phase: plain red above 24 on every read, cross-window endpoints above 0.9 lit and below 0.1 shadowed, for both metrics' read sets. |

**Certified visible-floor region for G6.** Fix `F = { y=0, X in [1.95,5.25], Z in [3.75,7.15] }`
now, for every phase and both builds. A pixel is certified only when its entire unprojected floor
footprint is contained in F and the following source/geometry visibility proof succeeds. The
approved `Wall` callback draws only the 40 m square floor at y=0 (X and Z in [-20,20]) and the
box wall X=[2,2.4], Y=[0,3], Z=[-3,3]. All of F is inside the floor. For each recorded orthographic
camera, require its toward-camera ray direction to have positive Y and nonnegative Z. From any
floor point in F, the ray toward the camera then stays at Z>=3.75, separated from the wall's
maximum Z=3. Require all four F corners to project inside the viewport and depth interval, and
verify the approved draw list and transforms are unchanged. These facts establish floor visibility
without classifying receiver geometry from pixel colour. If the projection is not orthographic,
the ray direction changes sign, another occluder is drawn, or this proof is otherwise unavailable,
classify INVALID. Do not infer floor visibility from a ray's intersection with y=0 alone.

Every original probe pixel and every pixel footprint used by a station-band or fixed non-seam
control must be certified. F includes a fixed margin around those regions, but the actual coverage
check is mandatory. If a required footprint is not fully certified, classify INVALID, never shrink
or move a band, control or F after seeing results. G6's zero-outside-mask claim applies only within
this certified set. Report the count and coordinates of all differing pixels outside the set as
`outside-certified-floor-region`, retaining both captures. Those pixels may be wall, background,
other geometry or uncertified floor. They receive no floor-mask confinement conclusion and cannot
be silently omitted from the report. All diagnostic classifications are scoped to the certified floor.

**Atlas identity, without a production API.** The existing internal
`Scene3D.DebugReadPointShadowAtlas(out width, out height)` in `Scene3D.PointShadowPass.cs:328` returns
row-major R32Float samples. `PointShadowBaseSlotForLight(0)` in `Scene3D.SkinnedPointShadows.cs:37`
identifies the single light's active row. A proof-only fixture accessor reads these immediately after
each soft capture, before any next capture releases the atlas. Check the row is valid, texture shape
matches six face columns and the declared rows, and `BoundPointShadowTexture` is the same object as
`PointShadowTexture`. Serialize the active row as little-endian IEEE754 bits, with dimensions, row,
format, source SHA and SHA-256 in a separate proof sidecar. Compare paired B/M bytes exactly.
Unused rows are not compared. At 256 resolution the saved row is 1,572,864 bytes, eight rows across
both builds and four phases, 12 MiB total. The existing helper transiently reads the full atlas, about
12 MiB at eight rows, and that array is released after row extraction. This is readback only, not
another render or a per-tap instrumentation feature. No readback runs under the design-only grant.

**Fixed non-seam controls.** Use world-floor rectangles X=[2.0,2.4], Z=[6.7,7.0] and
X=[4.8,5.2], Z=[6.8,7.1], within the existing viewport. For each, report its overlap area outside the
conservative crossing mask, require at least one wholly outside pixel footprint and byte-identical
paired soft captures on those outside pixels. These are pairing/confound controls, not a new
brightness or seam threshold. If a region is not covered or has no outside footprint, classify INVALID
rather than moving it after results. Atlas and non-seam comparisons are prospective requirements.

Reported, not gating: whether soft(B) at phase (0,0) equals the retained soft capture (ae1914c9...). Equal means
the fresh run reproduces the retained baseline exactly. Unequal means runner drift since run 37482238342. The
retained -8.728723 mm stays the historical row either way, and fresh `B` is the paired comparison for `M`.

## 7. Candidate area-band metric

Frame: world floor plane y = 0, absolute coordinates (render origin is 0).

- Across axis `a = (1.5, 0, -1) / sqrt(3.25) = (0.8320503, 0, -0.5547002)`, from lit to shadowed, the test's `across`.
- Along axis `e = (1, 0, 1.5) / sqrt(3.25) = (0.5547002, 0, 0.8320503)`, increasing station index.
- Station on-point `o_s = (2.9 + 0.1 s, 0, 1.5 (2.9 + 0.1 s))`, s = 0..12, along spacing 0.1802776 m.
- Band `R_s = { o_s + u a + v e : u in [-0.9, 0.9], v in [-h, h] }`, `h = sqrt(3.25) / 40 m`, approximately 0.0450694 m (a quarter of the station
  spacing). Bands are 0.0901 m wide (about 4.7 px), separated by 0.0901 m gaps, and never overlap.

Use the recorded float on-points for the actual band centres. Their nominal definition above is not
recomputed to a different precision for the candidate. On-points, across axis and scene geometry
must be bit-identical between B and M and across phases. The candidate's unit axes and overlap
calculation use double precision with physical normalization. Record both nominal and achieved data.

Pixel model: pixel (i, j) is the screen square [i, i+1) by [j, j+1), shaded once at its centre (i+0.5, j+0.5),
which matches D3D11 rasterization, the test's `(int)` truncation and `offline.py`. Its floor footprint `F_ij` is the
exact preimage of that square on y = 0 under the recorded view-projection (orthographic, so the preimage is a
parallelogram, here axis aligned at 0.01875 m by 0.0192165 m). Visibility `V_ij = soft_ij / plain_ij` on the red
channel, held constant over the footprint (box reconstruction of the centre sample, no interpolation).

`reachA(s) = (1 / 2h) * sum_ij area(F_ij intersect R_s) * (1 - V_ij)`, in metres. Overlaps by convex polygon
clipping in double precision. Completeness check: the overlap areas sum to `area(R_s)` within 1e-9 relative.
Endpoint analogue of G7: mean `V` over the strips `u in [-0.9, -0.85]` above 0.9 and `u in [0.85, 0.9]` below 0.1.
Endpoint weights: the candidate has none beyond exact overlap. The original keeps its 61 unit weights unchanged.

Detrend, bump (stations 3 to 5) and worst elsewhere exactly as the original, applied to `reachA`.

Why this needs a rendered negative-control gate. Across the edge it integrates the same window as the
original. The only added averaging is 0.09 m along the edge, under half the station spacing and far narrower than
the predicted multi-station crossing band. Area averaging can attenuate a narrow discontinuity even without interpolation. No claim of
seam preservation follows from the reconstruction alone. Whether it keeps the clamped signature is tested: section 9
requires the candidate to flag `M`. A candidate that passes `B` but misses `M` is rejected.

## 8. Pixel phases

Finite set, fixed now: `Phi = {(0, 0), (0.5, 0), (0, 0.5), (0.5, 0.5)}` px in screen (x, y). These cover four fixed grid alignments, not a proof of the worst case over all continuous phases. No other phases are rendered, swept or added later in the same evaluation.

A phase is a sub-pixel camera translation, which moves the pixel grid against the world while scene, light, atlas
and world-anchored dither stay put: `FrameOn(frameCentre + delta, 1.8f)` with
`delta = (phi_x * 0.0187500, 0, phi_y * 0.0192165)` m, the recorded metres per pixel along x and along z on the
floor (`2 / (M11 * 192)` and `2 / (abs(VP_32) * 192)`). Station positions stay in world space. Achieved phase is
measured as `screen(o_4, phi) - screen(o_4, (0,0))` from each manifest's recorded matrix, in absolute value, and
G4 requires it within 0.02 px of nominal. The sign of the shift is irrelevant to the set.

The phase envelope of a statistic on a build is `W = max over Phi - min over Phi` of its bump. With four phases this
is a sample range, not a bound, and no confidence statement is drawn from it.

## 9. Outcomes, fixed before any result

Evaluated per metric (original O and candidate A), in this order. Gates first.

| Outcome | Rule |
| --- | --- |
| INVALID | Any gate G1 to G7 fails, the job does not finish, the artifact is incomplete, or the mutant fails to compile. No metric is interpreted. No automatic rerun. |
| INCONCLUSIVE-PHASE | `W_B > T/4` or `W_M > T/4` (2 mm). A phase swing over a quarter of the bound can flip a reading near T, so the metric cannot classify this scene. |
| INCONCLUSIVE-UNDETECTED | `abs(bump_M(phi)) <= T` at any phase, or the separation `min_phi abs(bump_M) - max_phi abs(bump_B) <= max(T/4, 2 * max(W_B, W_M))`. The metric does not reliably flag the clamped kernel at the unchanged bound. Nothing about production follows. |
| BASELINE-EXCEEDS-BOUND | Control detected (above two rules pass) and `abs(bump_B(phi)) > T` at any phase. The unchanged path exceeds this scene-specific measurement bound. This does not identify a shader defect rather than legitimate penumbra curvature or a measurement defect. |
| BASELINE-WITHIN-BOUND | Control detected and `abs(bump_B(phi)) <= T` at every phase. Under this metric, at these four phases, the production filter does not show a crossing bump above 8 mm in this scene. |

The 2 mm minimum paired separation is a prospective engineering guard equal to the phase budget,
not a statistically inferred confidence interval. It prevents a zero-range or nearly unchanged mutant
from qualifying merely because both images already exceed 8 mm. The metric must flag the mutant at
all phases and separate it from the unchanged path by more than that guard. The 0.02 px phase check
bounds setup error to well below one pixel and about 0.4 mm in this frame, not a shadow tolerance.

These are diagnostic classifications only. They cannot turn the unchanged baseline test green or
clear Catalog C. An eventual metric change or gate disposition requires separate reviewed evidence.
A threshold exceedance is not, by itself, proof of a cube-face shader defect.

Joint interpretation, also fixed now:

- O BASELINE-WITHIN-BOUND: the original test's bound and metric already detect the control and production passes them at every
  phase. The retained failure then needs another explanation before anything else is concluded.
- A BASELINE-WITHIN-BOUND with O INCONCLUSIVE-PHASE: the retained failure is consistent with sampling phase, and the candidate is
  a reviewed option for a later, separately approved metric change. It is not adopted here.
- A BASELINE-EXCEEDS-BOUND: route to filter investigation, whatever O says.
- A INCONCLUSIVE-UNDETECTED: the scene and statistic cannot resolve a clamped kernel at 8 mm (likely if the
  detrend absorbs the broad crossing band of section 3). The next step is a separately approved scene or statistic
  redesign. Widening T, adding phases or rerunning until green is not a remedy.
- Any INCONCLUSIVE or INVALID leaves Catalog C unresolved and the original failure standing.

## 10. Offline existing RGBA versus new rendered controls

| | Offline on the retained RGBA | New rendered captures |
| --- | --- | --- |
| Source | Artifact 11421552678 only, unchanged | One future hosted job, B and M builds |
| What it can show | Candidate value on the failing capture at phase (0,0), analyzer correctness on synthetic images | Detection of a real clamped kernel, phase envelopes, paired B versus M |
| What it cannot show | Anything about a clamped kernel or about other phases' real renders | Anything outside this scene, backend and phase set |
| Weight in section 9 | None. Reported only. | Decides the outcome |

The retained-capture candidate value is computed and written to a pinned JSON before the GPU job is requested, so
it cannot be chosen after seeing rendered results. The existing quarter-pixel offline numbers stay sensitivity only.

## 11. Staged work and provisional costs

Costs are provisional engineering hours and future capture counts, not authority to run anything.

| Stage | Work | Needs approval for | Estimate |
| --- | --- | --- | --- |
| 0 | Offline analyzer in the style of `offline.py` (stdlib Python, reads artifacts, never renders): gates G1 to G7, crossing mask, footprint clipping, both metrics, outcome table. CPU self-tests are pinned below. They cover overlap geometry, physical normalization, pixel conventions and a known station-domain step. They are not GPU renders or substitutes for the real mutant. Then the reported-only value on the retained RGBA. | Code prep, CPU-only verification | 6 to 9 h |
| 1 | Proof-only baseline tree: one proof-branch `[GpuFact]` in `PointShadowFilterGpuTests` (reusing the private `Wall`, `Profile` and `Detrend`) that loops the four phases, builds per-phase options with `PointShadowSeamEvidenceOptions.Read` mapped to a per-phase subdirectory, and records each through the existing `PointShadowSeamEvidenceRun` without asserting the bound. Manifest schema v1 already records frame centre and matrices. Add a separate active-atlas sidecar through the existing readback hook, with no production API or ordinary evidence-schema change. Mutant tree: the section 3 change only. | Code prep, then a light compile check under its own grant | 5 to 7 h |
| 2 | One isolated proof workflow, never integrated, explicit Windows shell, cancel-in-progress false: verify both trees (G1), build B, build M, run the proof test once per build, upload. | Proof review, then one dispatch grant | 2 h prep, job about 4 to 6 min wall (two 90 s builds, two test steps of about 25 s) |
| 3 | Run the stage 0 analyzer on the new artifact, report the section 9 table with the retained row first. | Root review | 1 to 2 h |

Prospective CPU analyzer checks, without a rendered-control claim:

- Polygon overlap has closed-form fixtures: disjoint area 0, a contained unit square area 1,
  and the unit square intersected with `abs(x-0.5)+abs(y-0.5) <= 0.75`, area 7/8.
  Absolute area error must be <=1e-12 square metres for these unit fixtures.
- Constant visibility 0, 0.5 and 1 over a fully covered band must return 1.8, 0.9 and 0 metres
  within 1e-9 m. Use exact byte ratios, such as plain=200 and shadow=100 for 0.5.
- At each specified phase, a synthetic pixel-edge-aligned half-plane through the band centre
  covers half the symmetric band. Its known reach is 0.9 m within 1e-9 m. Choose the synthetic
  centre on that phase's recorded grid edge and record it. This checks mapping and weighting,
  not physical invariance of an unknown shader under camera motion.
- Detrending a 13-station constant 0.9 m vector yields zero bump within 1e-12 m. Adding 0.04 m
  only to stations 3, 4 and 5 yields bump `0.04 * (10/13 - 12/182)`, approximately
  0.028131868131868 m, within 1e-12 m. This proves the statistic detects that explicit signal.
- The real clamped shader remains mandatory because these analytic module checks cannot show
  whether band averaging preserves a rendered seam. No synthetic result grants that conclusion.

Future GPU footprint, fixed: one job, one dispatch, no retry, no matrix. 2 builds by 4 phases, 16 RGBA captures
(8 plain, 8 soft, 147,456 bytes each, about 2.4 MB), 8 evidence manifests, 8 active-atlas rows
(12 MiB total) with sidecars, and 2 TRX files. An infrastructure failure is
INVALID and goes back for a fresh decision, not a rerun.

Total provisional: 14 to 20 engineering hours plus reviews. The extra hour range includes atlas identity capture and its sidecar checks.

## 12. Risks

- Mutant GLSL may fail cross-compilation on the D3D11 path. A C# build does not exercise this. A separately granted focused compiler check may catch it, otherwise the hosted fixture startup returns INVALID. Never add a whole shader-corpus compile as a hidden diagnostic step.
- The clamped signature may be broad and partly absorbed by the detrend, giving INCONCLUSIVE-UNDETECTED for both
  metrics. That is a real answer about the test's design, not a failure of this control.
- `Frame` refits `OrthoSize` from absolute coordinates, so a shifted centre can move projection by float ulps. G4
  tolerates 1e-6 relative, about 1e-4 px across the frame.
- Specular depends on the eye, which moves 9 to 10 mm per half-pixel phase. It scales with the same attenuation as
  diffuse, so the visibility ratio cancels it up to 8-bit quantization, and B and M share it within each phase.

## 13. Out of scope

Production shader or setting changes, metric adoption, bound changes, Hard filter, combined/transient route,
other backends, per-tap readback, performance, stress, repeat runs, private game content. Atlas readback is limited to the existing hook for the matched active-row identity check above.

## 14. Review decisions requested

Recommend A2 with M1, the four fixed phases, active-atlas equality, the 2 mm phase budget and the
strict paired separation greater than max(2 mm, twice the observed phase range). These are
prospective engineering guard bands, not inferred confidence intervals. If any control is invalid
or undetected, return an inconclusive result without relaxing a number.

Approval requested now is for this diagnostic design only. Source preparation, finite verification
and the exact future hosted workflow are separate gates. The 14 to 20 hour allowance is provisional,
and no source or rendered assertion is changed by publishing this document. Catalog C stays closed.
