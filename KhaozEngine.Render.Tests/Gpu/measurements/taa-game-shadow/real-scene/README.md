# Real-scene shadow grain attribution

Which part of the temporal resolve puts white dots on the moving sun shadow edge across the Grimhollow
character under TAA. This is attribution, not a fix. The branch `fix/taa-grain-switches` is a diagnostic
archive and is never merged.

## Builds

- Engine: this branch, version `21.5.0-grain.1`, packed into a private feed. The switches are in
  `KhaozEngine.Render3D/Internal/TemporalResolveDiagnostics.cs` and under `#ifdef TemporalDiagnostics` in
  `ShaderSources.TemporalAccumulate.cs` and `ShaderSources.TemporalDebug.cs`.
- Game: Grimhollow `probe/taa-grain-engine`, originally `73201a64`, with this round vendored at `f5cf414dd`, pinned to `21.5.0-grain.1`. Its `shadowgrain`
  verb takes `--temporal-diag <tokens>` and names the mask the engine parsed in `metrics.txt`.
- Metal, Apple Silicon, 2560x1440, the `back-edge` spot (100.9, 89.5, yaw 230) unless named otherwise.

## The switches

`KE_TEMPORAL_DIAG` holds comma-separated tokens. The mask is read once and reaches both entry points through
`Params.w` of the resolve block. The switches' GLSL compiles only while the mask is set.

| Token | Effect |
| --- | --- |
| `show-locked` | Active lock this frame, refreshed or carried, drawn pure cyan by the same post-chain overlay. Show-locked takes precedence if both overlays are set. |
| `show-released` | Released pixels drawn pure magenta over the final image by a pass that re-evaluates the resolve after the post chain. History never holds the colour. Visual only. |
| `release-off` | `released` forced false on both release paths (#1427 still and #1448 moving). |
| `release-floor-2`, `release-floor-4` | A released pixel keeps its history clipped to a box of half the gamma, with accumulated weight capped at 2 or 4 frames, and keeps its display share. |
| `locks-off` | Thin-feature locks never set or held. |
| `current-only` | History weight zero. The output is the clamped internal reconstruction with no display share. Sharpen still runs. |
| `recon-nonneg` | Internal and display reconstructions take exp(-2.29 x^2) truncated at abs(x) < 2, the Blackman-Harris approximation, in place of Lanczos 2. The sample weight stays on Lanczos 2. |
| `dering-near` | Internal and display reconstructions clamped to the range of the 2x2 texels whose jittered samples bracket the pixel centre, in place of the 3x3. |
| `noop` | Compiles the switches in and changes nothing. The control for the others. |

### Unset is the base, bit for bit

The first version used uniform branches with no define. Unset, it moved TAA output against 21.4.0 by up to 112
of 255 in a channel over about 28k pixels a frame. Metal compiles with fast math, so a change in program text moves
rounding, and the shading release's thresholds turn a rounding step into a visible one. With the define, an
unset probe capture is pixel-identical to the 21.4.0 reference in all 16 frames of TAA Native, TAA Quality and
FXAA, and `runs/baseline/metrics.txt` matches it digit for digit (`scripts/pixdiff.py`).

`noop` moves character grain by 0.000007 at Native and 0.000032 at Quality, so codegen noise is two orders below
every effect below.

### The reference sun

The reference numbers came from a capture build whose `--start-time` was the time at frame 0. At 06601de4 it is
the time at the first measured frame. Every run here passes `--start-time 0.49777777777777776`, the reference's
first measured frame, so they compare with the reference directly. `runs/baseline-start0.498` is the 06601de4
default for comparison. The reference's dt 1/60 run measured a different sun (time 0.4844), so its 0.00713 is
not comparable with the dt 1/15 figure. At the same sun, dt 1/60 measures 0.009675 (below).

## Results, back-edge, dt 1/15, five-minute day

Luma 0..1, pooled over 16 frames. The `show-released` TAA rows include the magenta and are not a measurement.

| Run | Mode | Character grain mean | Character grain p95 | Character change mean | Ground grain mean | Ground grain p95 | Ground change mean |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `baseline` | Native | 0.014287 | 0.07499 | 0.01795 | 0.006708 | 0.02635 | 0.01249 |
| `baseline` | Quality | 0.014156 | 0.07334 | 0.01757 | 0.007330 | 0.02830 | 0.01235 |
| `noop` | Native | 0.014280 | 0.07499 | 0.01795 | 0.006708 | 0.02635 | 0.01249 |
| `noop` | Quality | 0.014124 | 0.07334 | 0.01756 | 0.007330 | 0.02830 | 0.01235 |
| `show-released` | Native | 0.019308 | 0.11355 | 0.03204 | 0.021390 | 0.03672 | 0.05980 |
| `show-released` | Quality | 0.017790 | 0.09238 | 0.03168 | 0.022163 | 0.04230 | 0.06256 |
| `release-off` | Native | 0.012611 | 0.06640 | 0.00975 | 0.009870 | 0.04560 | 0.01020 |
| `release-off` | Quality | 0.009798 | 0.04988 | 0.00815 | 0.009844 | 0.04655 | 0.00855 |
| `release-floor-2` | Native | 0.012251 | 0.06191 | 0.01668 | 0.005331 | 0.01965 | 0.01204 |
| `release-floor-2` | Quality | 0.011584 | 0.05600 | 0.01604 | 0.006027 | 0.02353 | 0.01152 |
| `release-floor-4` | Native | 0.012361 | 0.06220 | 0.01632 | 0.005206 | 0.01934 | 0.01193 |
| `release-floor-4` | Quality | 0.011711 | 0.05655 | 0.01574 | 0.005974 | 0.02353 | 0.01139 |
| `locks-off` | Native | 0.007125 | 0.04005 | 0.01883 | 0.006238 | 0.02383 | 0.01252 |
| `locks-off` | Quality | 0.008529 | 0.04754 | 0.01829 | 0.006368 | 0.02494 | 0.01247 |
| `current-only` | Native | 0.007197 | 0.03751 | 0.03004 | 0.007032 | 0.02663 | 0.01735 |
| `current-only` | Quality | 0.007990 | 0.03963 | 0.03386 | 0.005428 | 0.02045 | 0.01890 |
| `recon-nonneg` | Native | 0.013735 | 0.07196 | 0.01776 | 0.005545 | 0.02212 | 0.01237 |
| `recon-nonneg` | Quality | 0.012836 | 0.06578 | 0.01737 | 0.006338 | 0.02436 | 0.01213 |
| `dering-near` | Native | 0.014230 | 0.07476 | 0.01793 | 0.006693 | 0.02633 | 0.01249 |
| `dering-near` | Quality | 0.014125 | 0.07341 | 0.01758 | 0.007226 | 0.02829 | 0.01233 |
| every run | FXAA | 0.002961 | 0.01649 | 0.01911 | 0.002130 | 0.00869 | 0.01311 |

Bright dots in the character band, counted over 16 frames as pixels whose luma lies more than 0.05 above their
3x3 median and more than 0.05 above FXAA (`scripts/sparkcount.py`):

| Run | Native | Quality |
| --- | ---: | ---: |
| `baseline` | 1742 | 1733 |
| `noop` | 1739 | 1733 |
| `release-off` | 1371 | 762 |
| `release-floor-2` | 1561 | 1370 |
| `release-floor-4` | 1590 | 1430 |
| `locks-off` | 591 | 771 |
| `current-only` | 484 | 475 |
| `recon-nonneg` | 1668 | 1584 |
| `dering-near` | 1736 | 1727 |

## Are the dots released pixels

No. The overlay leaves every other pixel exactly as `noop` draws it (0 differing pixels outside the magenta), so
the magenta is the released set of that frame (`scripts/coincide.py`).

| | Native | Quality |
| --- | ---: | ---: |
| Released share of the character band | 7.3% | 7.0% |
| Dots that are released pixels that frame | 1.2% | 0.3% |
| Dots released the frame before | 1.8% | 3.3% |
| Dots within 1 px of a released pixel | 42.7% | 40.1% |

Dots fall on released pixels at a sixth of the chance rate or less. The released pixels form a thin strip along
the arm's outer silhouette (`crops/show-released-character-015-4x.png`). The dots spread over the shadowed arm
inward of that strip (`crops/baseline-character-015-4x.png`). `locks-off` clears them
(`crops/locks-off-character-015-4x.png`). `current-only` shows stair-stepped silhouettes and no dots
(`crops/current-only-character-015-4x.png`), so the dots are carried by history, not drawn by any one frame.
Crop column order is TAA Native, TAA Quality, FXAA.

## The two best tokens on other conditions

`locks-off` and `current-only` measure lowest on character grain. Each condition has its own baseline.

Back-edge, dt 1/60, five-minute day:

| Run | Mode | Character grain mean | Character grain p95 | Character change mean | Ground grain mean | Ground grain p95 | Ground change mean |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `dt60-baseline` | Native | 0.009675 | 0.05484 | 0.00488 | 0.006055 | 0.02353 | 0.00328 |
| `dt60-baseline` | Quality | 0.010472 | 0.05966 | 0.00500 | 0.006729 | 0.02718 | 0.00327 |
| `dt60-locks-off` | Native | 0.006230 | 0.03358 | 0.00482 | 0.005363 | 0.01992 | 0.00325 |
| `dt60-locks-off` | Quality | 0.007109 | 0.03920 | 0.00487 | 0.005514 | 0.02073 | 0.00322 |
| `dt60-current-only` | Native | 0.007293 | 0.03778 | 0.02007 | 0.006833 | 0.02608 | 0.01009 |
| `dt60-current-only` | Quality | 0.007961 | 0.03920 | 0.02496 | 0.005294 | 0.02016 | 0.01238 |
| every run | FXAA | 0.002955 | 0.01595 | 0.00470 | 0.002090 | 0.00868 | 0.00307 |

Back-edge, dt 1/15, thirty-minute day:

| Run | Mode | Character grain mean | Character grain p95 | Character change mean | Ground grain mean | Ground grain p95 | Ground change mean |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `day30-baseline` | Native | 0.008413 | 0.04997 | 0.00371 | 0.005930 | 0.02326 | 0.00234 |
| `day30-baseline` | Quality | 0.009148 | 0.05207 | 0.00366 | 0.006355 | 0.02524 | 0.00238 |
| `day30-locks-off` | Native | 0.005653 | 0.03105 | 0.00377 | 0.005272 | 0.01965 | 0.00231 |
| `day30-locks-off` | Quality | 0.006261 | 0.03471 | 0.00354 | 0.005076 | 0.01934 | 0.00232 |
| `day30-current-only` | Native | 0.007272 | 0.03805 | 0.01851 | 0.006815 | 0.02609 | 0.00948 |
| `day30-current-only` | Quality | 0.007873 | 0.03860 | 0.02392 | 0.005285 | 0.02017 | 0.01191 |
| every run | FXAA | 0.002976 | 0.01624 | 0.00402 | 0.002071 | 0.00868 | 0.00202 |

Ground-edge (100.3, 88.5, yaw 180), dt 1/15, five-minute day. The ground columns are the measured region here:

| Run | Mode | Character grain mean | Character grain p95 | Character change mean | Ground grain mean | Ground grain p95 | Ground change mean |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `ground-baseline` | Native | 0.002957 | 0.01148 | 0.00116 | 0.005274 | 0.01937 | 0.00373 |
| `ground-baseline` | Quality | 0.003024 | 0.01176 | 0.00118 | 0.005695 | 0.02242 | 0.00378 |
| `ground-locks-off` | Native | 0.002877 | 0.01065 | 0.00120 | 0.004504 | 0.01713 | 0.00384 |
| `ground-locks-off` | Quality | 0.002878 | 0.01148 | 0.00121 | 0.004389 | 0.01710 | 0.00387 |
| `ground-current-only` | Native | 0.004555 | 0.01624 | 0.01458 | 0.006071 | 0.02076 | 0.01615 |
| `ground-current-only` | Quality | 0.005838 | 0.02800 | 0.01984 | 0.004836 | 0.01740 | 0.02105 |
| every run | FXAA | 0.002280 | 0.01260 | 0.00135 | 0.002343 | 0.00784 | 0.00334 |

Combinations at back-edge, dt 1/15, five-minute day. The release floor and the non-negative kernel each helped
partly alone:

| Run | Mode | Character grain mean | Character grain p95 | Character change mean | Ground grain mean | Ground grain p95 | Ground change mean |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `locks-off` | Native | 0.007125 | 0.04005 | 0.01883 | 0.006238 | 0.02383 | 0.01252 |
| `locks-off` | Quality | 0.008529 | 0.04754 | 0.01829 | 0.006368 | 0.02494 | 0.01247 |
| `locks-off+release-floor-2` | Native | 0.006023 | 0.03386 | 0.01784 | 0.004844 | 0.01739 | 0.01210 |
| `locks-off+release-floor-2` | Quality | 0.007117 | 0.03750 | 0.01702 | 0.004966 | 0.01880 | 0.01170 |
| `locks-off+recon-nonneg` | Native | 0.006712 | 0.03806 | 0.01864 | 0.005180 | 0.02043 | 0.01239 |
| `locks-off+recon-nonneg` | Quality | 0.007818 | 0.04308 | 0.01801 | 0.005522 | 0.02211 | 0.01222 |
| every run | FXAA | 0.002961 | 0.01649 | 0.01911 | 0.002130 | 0.00869 | 0.01311 |

`locks-off` cuts character grain by 50% (Native) and 40% (Quality) at dt 1/15, and by 32% to 36% at dt 1/60
and at the thirty-minute day, with change mean at the baseline's and FXAA's. Ground grain falls 15% to 23% at
the ground edge. What is left is two to three times FXAA, near `current-only`. The release floor takes
another 15% to 17% off what `locks-off` leaves.

`release-off` lowers grain only by roughly halving the change mean against FXAA's, which is the lag the release
removed coming back. `current-only` raises change mean to 1.6 to 6 times FXAA's, frame-to-frame flicker of the
aliased current frame.

## Prior-round GPU guards with the token set

Metal, `KE_GPU_TESTS=1`, one filtered run per token over `TemporalShadowEdgeLagGpuTests`,
`TemporalMovingShadeLagGpuTests`, `TemporalStableDetailGpuTests`, `TemporalStablePeakGpuTests` and
`TemporalGameShadowGrainGpuTests`. Unset and `noop` pass 7 of 7 with identical printed rows. Each token passes
2 of 7. Failure messages, measured against bound, are in `guards/`.

`locks-off`:

- Lag holds. `TemporalShadowEdgeLagGpuTests` passes its travel tail ratio and its Quality band at 1 px. It fails
  only the still-light textured error at Quality (band 0.0072 against 0.0064, control 0.0064 against 0.0057).
  `TemporalMovingShadeLagGpuTests` band errors on moving shadows fall (Native S 1 px advancing 0.0162 to 0.0125,
  Quality 0.0208 to 0.0184). Its Native S 8 px tail ratio fails narrowly (0.0046 against 1.5 times 0.0030, and
  0.0081 against 1.5 times 0.0053), mostly because the 1 px tail fell (0.0033 to 0.0030, 0.0057 to 0.0053).
- Detail fails, as expected of removing the feature locks exist for. `TemporalStablePeakGpuTests`: Quality flat
  error 0.02191 against 0.01504 plus a tenth. `TemporalStableDetailGpuTests` at Quality: still 1 px texels error
  0.01407 against 0.00535 plus a tenth, slow pan 0.8 px error 0.01758 against 0.01346 plus a tenth.
  `TemporalMovingShadeLagGpuTests` Quality camera pan 2 px: band 0.0106 against 0.0055 plus a tenth.
- `TemporalGameShadowGrainGpuTests` matches the base within 0.00002 on every row. Its faceted receiver has no texture
  ridges, so it never takes a lock and does not reproduce this mechanism.

`current-only` fails every stability bound by an order of magnitude, as a control should: still peak change
0.07830 against 0.00203 plus a tenth at Quality flat, Nyquist detail temporal error 0.04023 against 0.00105
plus a tenth.

## Light hold

`metrics.json` records whether the sun shadow fit re-recorded each frame (`scripts/hold.py`). At the
five-minute day and dt 1/15 the light matrix changes on 16 of 16 frames: the sun turns about 1.4e-3 rad a
frame, past the hold's threshold every frame, so the hold never engages. That is why hold texels 0 was
bit-identical. At the thirty-minute day and dt 1/15 it changes on 4 of 16 frames, and at the five-minute day
and dt 1/60 on 5 of 16, so the hold engages there and the shadow moves in steps about every fourth frame.

## Prior-round reading

The working hypothesis does not hold. Released pixels are not the dots. The dominant source is the thin-feature
lock. A texel that stands out from both neighbours, a texture highlight on the skin or a lit texel the jittered
coarse shadow edge leaves between shadowed ones, reads as a thin feature and takes a whole lock. The hold then
keeps that pixel's lit history luma out of the clip for about four frames and lets it go over four more, while
its unlocked neighbours clip to the new shadow. The release fires beside these pixels but almost never on them
(1.2% and 0.3% above), and the sharpen lifts a lone bright pixel against a dark ring further. At that checkpoint the lock attribution
was inferred from `locks-off` and the coincidence figures. The direct cyan overlay in this round confirms it.

The remainder, about twice FXAA, matches `current-only`: the aliased jittered frame at the silhouette. The
reconstruction kernel and its clamp contribute little (`recon-nonneg` minus 4% to 10%, `dering-near` nothing).

A fix should stop a lock forming or holding on a shading-only feature rather than drop locks, which the
detail guards need. Candidates to test: refresh a lock only where the ridge also stands in the reprojected
history or persists over the jitter cycle, release a carried lock when the pixel's 3x3 is crossing a shading
step, or bound a held luma by the current box plus a margin where the 3x3 changed this frame. The synthetic
grain test needs a textured receiver to reproduce this before any fix is measured on it.

## Commands

Engine, from this worktree:

```bash
build-slot --label build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
KHAOZENGINE_FEED="${KHAOZENGINE_FEED:?set a private feed first}" build-slot --heavy --label pack -- scripts/pack-local-feed.sh
KE_TEMPORAL_DIAG=locks-off KE_GPU_TESTS=1 build-slot --heavy --label guards -- dotnet test \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~TemporalShadowEdgeLagGpuTests|FullyQualifiedName~TemporalMovingShadeLagGpuTests|FullyQualifiedName~TemporalStableDetailGpuTests|FullyQualifiedName~TemporalStablePeakGpuTests|FullyQualifiedName~TemporalGameShadowGrainGpuTests" \
  --logger "console;verbosity=detailed"
```

Game, from the `probe/taa-grain-engine` worktree:

```bash
scripts/refresh-engine.sh
build-slot --label build -- dotnet build tools/SnapshotTool/SnapshotTool.csproj -c Release
# baseline, then each token
build-slot --heavy --label shadowgrain -- dotnet run --project tools/SnapshotTool -c Release --no-build -- \
  shadowgrain captures/engine-diag/baseline --spot back-edge --frame-time 1/15 \
  --start-time 0.49777777777777776
build-slot --heavy --label shadowgrain -- dotnet run --project tools/SnapshotTool -c Release --no-build -- \
  shadowgrain captures/engine-diag/locks-off --spot back-edge --frame-time 1/15 \
  --start-time 0.49777777777777776 --temporal-diag locks-off
# other conditions add --frame-time 1/60, --day-minutes 30 or --spot ground-edge
```

Analysis, with numpy and Pillow, over the capture root:

```bash
python3 scripts/pixdiff.py <reference-run> <probe-run>
python3 scripts/coincide.py captures/engine-diag
python3 scripts/sparkcount.py captures/engine-diag baseline noop locks-off
python3 scripts/hold.py captures/engine-diag/baseline
python3 scripts/mdtable.py captures/engine-diag baseline locks-off
```

## Direct lock coincidence

The `show-locked` overlay reads `TemporalPixel.stability > 0` from this frame's per-pixel resolve. It reports the
stored lock after refresh, decay, motion and reactive release. The shading release only drops its hold for the
current frame and leaves that stored lock intact, so those pixels remain cyan. The overlay never enters history.

Back-edge, dt 1/15, five-minute day, start time `0.49777777777777776`, all 16 frames. The same `coincide.py`
median and FXAA criterion as the release round, using the `noop` image underneath. Outside cyan, zero pixels
differ from `noop` at either preset.

| Metric | Native | Quality |
| --- | ---: | ---: |
| Character band pixels | 39873 | 39873 |
| Locked band pixels | 26048 | 28979 |
| Locked share of band | 65.3274% | 72.6783% |
| Bright dots | 1739 | 1733 |
| Bright dots locked this frame | 1685 | 1678 |
| Share of bright dots locked | 96.8948% | 96.8263% |
| Dots within 1 px of a lock | 98.9% | 99.2% |

The direct evidence passes the brief's gate. The earlier thin-feature inference is now measured.
Crop [show-locked character frame 15, 4x](crops/show-locked-character-015-4x.png) places Native, Quality and FXAA
left to right, as the other crops do. The full capture is under `captures/engine-diag/show-locked`.

```bash
build-slot --heavy --label grain-show-locked -- dotnet run --project tools/SnapshotTool -c Release --no-build -- \
  shadowgrain captures/engine-diag/show-locked --spot back-edge --frame-time 1/15 \
  --start-time 0.49777777777777776 --temporal-diag show-locked
python3 scripts/coincide.py captures/engine-diag show-locked
```

## Lock-rule round

The three new tokens are independent diagnostic arms. They do not change tuning or default behavior.

| Token | Computation |
| --- | --- |
| `lock-history-ridge` | Refresh only if history is valid and a ridge of the same direction and polarity exists in both neighbourhoods. Four directions, horizontal, vertical and both diagonals. Current luma is weighted Y from the current internal texels around the centre. History luma is weighted Y from bilinear taps around the reprojected display pixel at the same internal-texel spacing (`1 / Sizes.xy`). Each side uses `max(LockRidgeAbsolute, LockRidgeRelative * centreLuma)`. A bright ridge must be above both neighbours on both sides of the comparison, or a dark ridge below both. Carried locks still decay normally. |
| `lock-shade-release` | Keep the existing shading eligibility, history-versus-box, own-shift and ring-mean thresholds, including strict ring distance and `ShadingLockRingShare` for a carried lock. Drop only the opposite-pair condition. Where that evidence fires on a carried lock, clear its stored lock and run the normal full shading release, zero hold and accumulated weight. A refreshed ridge follows the normal release. |
| `lock-bound-current` | Use the same shading evidence with the opposite-pair condition omitted. Where it fires, set hold to zero for the variance clip, so the current `mean +/- gamma * deviation` box bounds the history with no luma exemption. Keep stored lock and normal accumulated weight. Pixels the normal release already releases still take that full release. No extra box margin. |

The shader helpers and branches remain under `TemporalDiagnostics`. The paired synthetic case temporarily overrides
only the diagnostic mask, creates its scene with `noop` so all arms compile, and restores the process mask in a
`finally` block. Its test class is in the nonparallel `NativeDeviceLifecycle` collection.

### Synthetic fixture and limits

The existing untextured torso fails the separation test, Native 0.001566 with either arm. Dense wall noise at
0.8 display pixels per texel gives Native 0.026380 versus 0.023567, a ratio of 1.119, where actual texture grain
dominates the measure. Sparse highlights give Native 1.153 and Quality 1.433. Reducing highlight contrast and
ambient does not improve Native's separation.

The retained fixture uses diagonal one-texel bright lines (`(x + y) % 8 == 0`, greys 255 and 32), at 0.8 display
pixels per texture texel, on the existing faceted receiver. Camera and receiver are still, the shadow advances
4 display pixels per frame, cascade texels are about 5 display pixels, light hold is one texel, ambient is 0.03,
and sharpness is 0.25. It measures the 12-pixel band for 16 frames after 80 warm-up frames, against the existing
4x spatial reference with no temporal resolve or light hold. It also prints a still-light control using its own
spatial reference. Each capture resets held light, history and jitter phase.

The acceptance bound is on Quality only, baseline band output grain must exceed `1.3 * locks-off`. Both presets
are printed. Quality reproduces roughly the real scene's 1.66 ratio. Native remains weaker than the real scene's
2.00 ratio, so this is not a complete Native reproduction. The retained case deliberately leaves that limitation
visible. The test does not claim the whole difference is unwanted detail, so reference error grain and the
still-light control are also printed.

### Synthetic values and guard results

Metal hardware, one filtered `dotnet test` process per candidate, `KE_GPU_TESTS=1`. All eight facts execute,
zero skipped. Full measured rows and failure messages are committed in [guards](guards/).

| Preset | Moving baseline grain | Moving locks-off grain | Ratio | History ridge | Shade release | Bound current | Still baseline | Still locks-off |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Native | 0.012026 | 0.010435 | 1.152 | 0.011864 | 0.011481 | 0.011450 | 0.009340 | 0.009313 |
| Quality | 0.010852 | 0.006727 | 1.613 | 0.010680 | 0.009964 | 0.009803 | 0.008210 | 0.005315 |

The synthetic Quality gate is 0.010852 > 1.3 * 0.006727 = 0.008746. The still-light control shows Quality's
ratio partly reflects legitimate detail retention. Native's still grain is almost identical between arms, while
the moving edge separates them, but its moving ratio is too small to match the real scene. Moving error-image
grain is 0.007782 versus 0.006190 at Native and 0.009786 versus 0.009510 at Quality. The spatial reference's
grain is 0.009643. This fixture demonstrates the lock contribution and roughly matches Quality output grain,
but does not fully isolate or reproduce the real-scene defect. A stronger synthetic Native reproduction remains open
within the diagnosis. The separation assertion covers Quality only. Native remains diagnostic evidence and does not meet 1.3x separation.

| GPU fact | History ridge | Shade release | Bound current |
| --- | --- | --- | --- |
| New textured grain case | Pass | Pass | Pass |
| Shadow-edge lag | Pass | Pass | Pass |
| Moving-shade lag | Fail | Pass | Pass |
| Slow texture pan | Pass | Pass | Pass |
| Near-Nyquist texture detail | Fail | Pass | Pass |
| Stable peaks | Pass | Pass | Pass |
| Existing faceted grain measurement | Pass | Pass | Pass |
| Shared-scene parity | Pass | Pass | Pass |
| Total | 6 pass, 2 fail | 8 pass | 8 pass |

Representative values against their actual bounds, rather than pass status alone:

| Guard metric | Bound | History ridge | Shade release | Bound current |
| --- | ---: | ---: | ---: | ---: |
| Quality still 1 px texture error | <= 0.005885 | 0.00811 fail | 0.00548 pass | 0.00548 pass |
| Quality still 1 px texture temporal error | <= 0.001155 | 0.00128 fail | 0.00112 pass | 0.00112 pass |
| Quality still 0.5 px texture error | <= 0.007931 | 0.01391 fail | 0.00724 pass | 0.00724 pass |
| Quality camera pan 2 px textured band error | <= 0.00605 | 0.0076 fail | 0.0055 pass | 0.0055 pass |
| Quality camera pan 2 px textured tail error | <= 0.00429 | 0.0057 fail | 0.0039 pass | 0.0039 pass |
| Quality flat stable-peak error | <= 0.016544 | 0.01532 pass | 0.01504 pass | 0.01504 pass |
| Quality flat stable-peak change | <= 0.002233 | 0.00207 pass | 0.00203 pass | 0.00203 pass |

Existing acceptance bounds and shipping tuning were unchanged. The existing lag assertions also retain their fast-tail ratio,
control-error, moving-tail-excess and band-grain limits. The full per-case values are in the logs above.

### Broader verification and scope limits

The engine solution Release build passed with zero warnings. The broader engine run completed 41 assemblies,
35109 passed, 1424 skipped and one failure, the raw shader-text kernel-count assertion in
`TemporalResolveShaderTests.The_separable_Lanczos_kernel_takes_twelve_evaluations_per_pixel`. It expects five
`lanczos2(` occurrences, while starting commit `f22cb609f` already has nine in the diagnostic shader's current
and default alternatives. The new lock helpers add none. This pre-existing diagnostic-archive assertion was
reported without changing it. See [the failure](guards/engine-broad-failure.txt).

The game solution Release build passed with zero warnings. Its broader suite ended with 9321 passed, 10 failed
and 6 skipped, then the test host aborted on an unhandled `ObjectDisposedException`, the cancellation token
source was disposed in a clock-host test thread. The ten assertion failures are navigation bake expectations.
The diagnostic branch already pins `21.5.0-grain.1`, while the committed bakes name `21.4.0`, and the refusal
says `EngineChanged`. No navigation content was rebaked, no clock-host code was changed, no tests were retried
and no issues were filed under this brief. See [the game run](guards/game-broad-suite.txt).

The final private pack is based on engine code commit `dc7b51a7d`. The game stays at `21.5.0-grain.1`.
The refresh evicted 78 extracted package versions, then the engine build DLL, the vendored DLL and SnapshotTool's
loaded `KhaozEngine.Render3D.dll` all matched SHA-256
`5c83039e6403bf4ecead6cc8cf58fbe687d76122029a39c2a7b840d7be86a35e`.
The private feed contains 222 refreshed package files. The shared engine feed was not used as an output.

### Final-binary baseline and overlay recheck

Fresh captures from the final private binary reproduce every prior baseline frame in all four conditions,
16 frames each at Native, Quality and FXAA, 192 images total. There are zero differing pixels, maximum channel
difference zero, and all four `metrics.txt` files are byte-identical. The prior baseline was independently
pixel-identical to 21.4.0, so the unset invariant still holds. See [the identity report](runs/lock-rules/baseline-identity.txt).
The final-binary `noop` row also matches the old round's printed metrics, Native character grain 0.014280 and
Quality 0.014124, within 0.000007 and 0.000032 of the unset values. The game format verification passed.

The cyan overlay was recaptured after adding all candidate code and repacking. Its coincidence is unchanged,
1685 of 1739 bright dots locked at Native (96.8948%), 1678 of 1733 at Quality (96.8263%). Locked band share is
26048 of 39873 (65.3274%) and 28979 of 39873 (72.6783%). Outside cyan, zero pixels differ from the new `noop`.
See [the counts](runs/lock-rules/lock-coincidence.txt) and
[the final 4x crop](crops/lock-rule-show-locked-character-015-4x.png).

### Real-scene candidate comparisons

The tables below are the complete requested comparison, including both character and ground regions. Luma is
0..1, mean and p95 are pooled over 16 frames, and change is the mean absolute per-frame change. Bright dots are
positive residuals over the 3x3 median greater than 0.05 and also greater than 0.05 above the baseline FXAA pixel,
counted over the same 16 frames. Character uses the green region mask, ground the yellow mask. FXAA therefore
has zero excess dots by definition. All arms use the baseline FXAA masks and dot reference. All 288 non-baseline FXAA crops were also verified pixel-identical to their corresponding baseline, see [the FXAA control proof](runs/lock-rules/fxaa-control-identity.txt).

| Condition | Spot | Frame time | Day minutes |
| --- | --- | --- | ---: |
| back15 | back-edge | 1/15 | 5 |
| day30 | back-edge | 1/15 | 30 |
| back60 | back-edge | 1/60 | 5 |
| ground15 | ground-edge | 1/15 | 5 |

Every capture is Metal, 2560x1440, 80 warm-up frames and start time `0.49777777777777776`. All 22 captures exited
zero, including the four baselines, one noop, one cyan overlay, four locks-off and twelve candidate captures.
See [capture options and exits](runs/lock-rules/capture-headers-and-exits.txt), [sweep](runs/lock-rules/capture-sweep.txt),
[all machine-readable measurements](runs/lock-rules/summary.json) and each run's `metrics.txt` and `metrics.json`
under [runs/lock-rules](runs/lock-rules/). The enlarged first-condition candidate crops are
[history ridge](crops/lock-rule-back15-lock-history-ridge-character-015-4x.png),
[shade release](crops/lock-rule-back15-lock-shade-release-character-015-4x.png), and
[bound current](crops/lock-rule-back15-lock-bound-current-character-015-4x.png).
All four conditions have corresponding character and ground 4x crops under [crops](crops/), named
`lock-rule-<condition>-<arm>-<region>-015-4x.png`. Panels are Native, Quality and FXAA from left to right.

### Real-scene lock-history-ridge

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

### Real-scene lock-shade-release

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-shade-release | taa-native | 0.013702 | 0.07111 | 0.01803 | 1648 | 0.006661 | 0.02607 | 0.01251 | 440 |
| back15 | lock-shade-release | taa-quality | 0.013554 | 0.07074 | 0.01763 | 1645 | 0.007209 | 0.02799 | 0.01238 | 1743 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-shade-release | taa-native | 0.008315 | 0.04953 | 0.00368 | 1072 | 0.005903 | 0.02315 | 0.00235 | 295 |
| day30 | lock-shade-release | taa-quality | 0.009226 | 0.05265 | 0.00366 | 1128 | 0.006322 | 0.02500 | 0.00238 | 894 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-shade-release | taa-native | 0.009612 | 0.05446 | 0.00487 | 1348 | 0.006005 | 0.02327 | 0.00328 | 356 |
| back60 | lock-shade-release | taa-quality | 0.010367 | 0.05932 | 0.00501 | 1498 | 0.006664 | 0.02690 | 0.00327 | 1215 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-shade-release | taa-native | 0.002962 | 0.01148 | 0.00116 | 371 | 0.005261 | 0.01934 | 0.00373 | 507 |
| ground15 | lock-shade-release | taa-quality | 0.003039 | 0.01203 | 0.00118 | 350 | 0.005657 | 0.02216 | 0.00378 | 1259 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

### Real-scene lock-bound-current

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-bound-current | taa-native | 0.014024 | 0.07249 | 0.01793 | 1663 | 0.006687 | 0.02624 | 0.01250 | 443 |
| back15 | lock-bound-current | taa-quality | 0.013738 | 0.07079 | 0.01756 | 1660 | 0.007264 | 0.02825 | 0.01237 | 1765 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-bound-current | taa-native | 0.008270 | 0.04800 | 0.00370 | 1023 | 0.005903 | 0.02317 | 0.00234 | 286 |
| day30 | lock-bound-current | taa-quality | 0.009203 | 0.05341 | 0.00365 | 1127 | 0.006338 | 0.02523 | 0.00238 | 871 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-bound-current | taa-native | 0.009568 | 0.05430 | 0.00488 | 1335 | 0.006034 | 0.02350 | 0.00328 | 352 |
| back60 | lock-bound-current | taa-quality | 0.010418 | 0.05934 | 0.00499 | 1497 | 0.006692 | 0.02715 | 0.00326 | 1209 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-bound-current | taa-native | 0.002962 | 0.01148 | 0.00116 | 371 | 0.005271 | 0.01936 | 0.00373 | 505 |
| ground15 | lock-bound-current | taa-quality | 0.003044 | 0.01184 | 0.00118 | 343 | 0.005670 | 0.02238 | 0.00378 | 1262 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |


### Recommendation from this round

Continue the history-ridge direction as diagnosis, but implement none of these tested rules as a shipping fix.
It is the only substantial reduction. At back15 it cuts character grain by 34.9% at Native and 18.9% at Quality,
and dots by 45.1% and 30.1%. At day30 and back60 the character grain reductions are 15.7% and 16.5% at Native,
12.1% and 10.8% at Quality. Ground-edge grain falls 4.4% and 16.1%. Change mean stays near the baseline and FXAA,
so the benefit is not a return of the old shadow lag. But the fine-detail and camera-pan guard failures prevent
adoption of this predicate.

The carried-lock opposite-pair exception is not the dominant source of the real-scene dots. Dropping it while
clearing the carried lock cuts back15 character grain only 4.1% and 4.3%, and dots 5.4% and 5.1%. Bounding the
hold only at that same evidence cuts grain 1.8% and 3.0%, and dots 4.5% and 4.2%. Their other-condition changes
are mostly around one percent or less, sometimes worse, despite both arms passing all eight GPU facts. They
preserve the guards but do not address most of the visible problem.

The next proper design should make history ridge confirmation aware of jitter and subpixel feature support,
rather than requiring a full current-strength ridge in the accumulated history neighbourhood. This is a
recommendation for further design, not a measured fix. The tested history threshold is absolute 0.02 and
relative 0.1, independently at the history centre. No safe tuning interval was established. Lower history
contrast thresholds and phase-aware matching require their own paired measurements and the same detail guards,
not a looser acceptance bound. The other two candidates used the existing shading thresholds unchanged and a
zero extra clip margin. No range beyond those tested points is claimed.

The synthetic Native ratio remains unresolved. Its rigid orthographic receiver and authored repeating lines do
not match the real skinned idle player's perspective geometry and material/shadow detail. Quality's ratio also
contains stable texture retention, as the still-light control shows. These are fixture differences, not a proven
causal explanation of the remaining gap. A stronger synthetic reproduction is required before a production fix.

### Reproduce this round

From the engine worktree, after the private pack and game refresh/build commands above. The capture script checks
the game root and branch before every serial capture and clears any inherited diagnostic variable for the baseline.

```bash
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_lock_rules.py \
  "$GAME_PROBE" captures/lock-rule-round
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/lock_rule_tables.py \
  captures/lock-rule-round
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/coincide.py \
  captures/lock-rule-round back15-show-locked back15-noop back15-baseline
```

Each GPU command below ran once, sequentially. The filter includes the new synthetic method in the existing class.

```bash
grain_guard_filter='FullyQualifiedName~TemporalShadowEdgeLagGpuTests|FullyQualifiedName~TemporalMovingShadeLagGpuTests|FullyQualifiedName~TemporalStableDetailGpuTests|FullyQualifiedName~TemporalStablePeakGpuTests|FullyQualifiedName~TemporalGameShadowGrainGpuTests'
KE_TEMPORAL_DIAG=lock-history-ridge KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native \
  build-slot --heavy --label grain-history-guards -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-build --filter "$grain_guard_filter" --logger 'console;verbosity=detailed'
KE_TEMPORAL_DIAG=lock-shade-release KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native \
  build-slot --heavy --label grain-shade-guards -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-build --filter "$grain_guard_filter" --logger 'console;verbosity=detailed'
KE_TEMPORAL_DIAG=lock-bound-current KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native \
  build-slot --heavy --label grain-bound-guards -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-build --filter "$grain_guard_filter" --logger 'console;verbosity=detailed'
```

Final whole-tree dash, prose, file-size, instruction-budget and version guards passed. The staged whitespace check
also passed. See [repository guard output](guards/repository-guards.txt) and
[the exact measured binary provenance](runs/lock-rules/provenance.json).
Both branches remain diagnostic archives. Neither is a shipping fix or a merge candidate.

## Release-gate and lock-mechanism round

This round traces which release condition keeps each dot held, then tests two lock mechanisms the resolve lacks
and AMD FSR 2 has, a lock trust ramp and a lock kill on relative low-frequency luma change. It stays diagnosis on
this archive. No tuning or default behavior changed.

### Tokens

| Token | Computation |
| --- | --- |
| `show-gate` | On a pixel with hold above zero that the shading release left, a flat colour for the first condition that blocked it, in the release's own order: red the `Jitter.z < ShadingMaxUpscale` gate, green the ridge or box-width gate, blue history within `(1 + ShadingResetExtents) * releaseExtent` of the box centre, yellow an upscaled pixel whose own shift stayed under its extents (`ownChanged` false, so the ring never runs), magenta the ring threshold, cyan the opposite-pair requirement, white no history. |
| `show-gate-extent` | For the same pixels past the upscale and ridge or box-width gates, the box half-width over `ridgeStep` and whether the same evidence fires with `releaseExtent = ridgeStep`. Green at most one `ridgeStep`, so `releaseExtent` is `ridgeStep` already. Yellow one to three and fires, blue one to three and still blocked. Red over three and fires, magenta over three and still blocked. |
| `show-lock-age` | On a locked pixel, red a ridge refreshed it this frame, else the carried lock's frames since its last ridge, green one, blue two to three, yellow four or more. Age is read from the stored lock as `round((1 - lock) / LockDecay) + 1`, exact for a still pixel, an overestimate where motion or reactive release also scaled the lock. |
| `lock-trust-2`, `-3`, `-4` | A ridge raises the lock last frame stored by 1/K, capped at 1, in place of setting it to 1. No ridge decays by `LockDecay` as before. A pixel with no history, or one whose lock `heldFromFarther` drops, starts from zero. |
| `lock-luma-kill-0.1`, `-0.2` | Lock and hold drop to zero where `1 - min/max` of this frame's and last frame's low-frequency luma exceeds T. This frame is the 3x3 mean weighted luma from the gathered moments. Last frame is the mean of the four `temporalHistoryRingShift` taps and a bilinear centre at the reprojected position. Runs after the motion and reactive release, only with valid history. |
| `lock-luma-kill-wide-0.1`, `-0.2` | The same with a 5x5 footprint on both sides, 25 internal texels around the centre texel against 25 bilinear history taps one internal texel apart in display space around the reprojected position. The engine builds no luma mip for the resolve, so no mip variant was possible. |

Tokens compose with commas. The combination is `lock-trust-2,lock-luma-kill-0.2`, archived as
`lock-trust-2+lock-luma-kill-0.2`. All new GLSL sits under `#ifdef TemporalDiagnostics` in
`ShaderSources.TemporalDiagnosticLocks.cs`, with call sites in `ShaderSources.TemporalAccumulate.cs` and the overlay
in `ShaderSources.TemporalDebug.cs`.

**State cost.** The trust ramp needs no separate trust value. It ramps the stored lock itself, which keeps the
moved, band and followed encoding in `temporalStoreLock` as it was. A separate trust value would need a new
target or a repacked channel, because `HistoryConfidence` is R16G16Float with both channels in use. The narrow
kill reads only the history colour target the ring test already samples, so it costs no state either: four
bilinear taps and a centre per locked pixel with history. The wide kill costs 25 texel fetches and 24 bilinear
taps per locked pixel.

### Unset is still the base

Engine code commit `e8312813f`, packed as `21.5.0-grain.1` into the private feed and re-vendored. The engine build
DLL, the vendored nupkg's DLL and SnapshotTool's loaded `KhaozEngine.Render3D.dll` all match SHA-256
`a55ac5bd07aba93e508791f7edcb3f21f07f590f96ec1658fcf5315cb2d951c8`. Fresh unset captures in all four conditions are
pixel-identical to the prior round's baselines in all 16 frames of TAA Native, TAA Quality and FXAA, with
byte-identical `metrics.txt` ([identity](runs/lock-trust-round/baseline-identity.txt)). The prior baselines were
pixel-identical to 21.4.0, so the unset invariant holds. `noop` measures character grain 0.014280 and 0.014124,
the same as last round. The unset Metal guard run passes 8 of 8.

### Which condition holds the dots

Back-edge, dt 1/15, five-minute day, all 16 frames. Dots are `coincide.py`'s on the `noop` image. Outside the
code colours, zero pixels differ from `noop` in any overlay, and no `noop` band pixel already equals a code colour.
Released means locked with no gate colour, so the release fired. Full output, including the extent and age
tables, is in [gate-histogram.md](runs/lock-trust-round/gate-histogram.md).

| First blocking condition | Native band | Native dots | Quality band | Quality dots |
| --- | ---: | ---: | ---: | ---: |
| unlocked | 34.7% | 3.1% | 27.3% | 3.2% |
| locked and released | 6.2% | 1.2% | 6.3% | 0.3% |
| upscale gate | 0.0% | 0.0% | 0.0% | 0.0% |
| ridge or box-width gate | 23.5% | 40.0% | 16.3% | 16.0% |
| history within reset extents | 33.5% | 25.7% | 46.2% | 38.7% |
| upscaled, `ownChanged` false | 0.0% | 0.0% | 2.0% | 13.2% |
| ring threshold | 2.0% | 28.2% | 1.8% | 26.7% |
| opposite pair | 0.2% | 1.8% | 0.1% | 2.0% |

Band is 39873 pixels over 16 frames. Dots are 1739 at Native and 1733 at Quality.

| Lock source | Native band | Native dots | Quality band | Quality dots |
| --- | ---: | ---: | ---: | ---: |
| ridge this frame | 5.3% | 11.6% | 10.7% | 21.1% |
| carried, age 1 | 9.3% | 24.6% | 12.5% | 27.1% |
| carried, age 2 to 3 | 20.1% | 38.8% | 21.6% | 32.5% |
| carried, age 4+ | 30.5% | 22.0% | 27.9% | 16.2% |

| Extent class, gates 3 to 6 | Native dots | Quality dots |
| --- | ---: | ---: |
| box at most `ridgeStep` | 5.1% | 2.1% |
| box 1 to 3 `ridgeStep`, fires at `ridgeStep` | 14.0% | 23.8% |
| box 1 to 3 `ridgeStep`, still blocked | 18.8% | 23.4% |
| box over 3 `ridgeStep`, fires at `ridgeStep` | 8.5% | 13.8% |
| box over 3 `ridgeStep`, still blocked | 9.3% | 17.4% |

Dots by gate and age, Native (Quality in [gate-histogram.md](runs/lock-trust-round/gate-histogram.md)):

| Gate | ridge this frame | age 1 | age 2 to 3 | age 4+ |
| --- | ---: | ---: | ---: | ---: |
| released | 20 | 0 | 0 | 1 |
| ridge or box-width gate | 0 | 143 | 359 | 193 |
| within reset extents | 181 | 129 | 74 | 63 |
| ring threshold | 0 | 138 | 229 | 123 |
| opposite pair | 0 | 18 | 12 | 2 |

Readings:

- The upscale gate blocks nothing at Native or Quality. Quality's internal texel is under `ShadingMaxUpscale`.
- At Native the largest blocker is the ridge or box-width gate, 40% of dots, every one a carried lock with no ridge
  now. A carried lock passes that gate only where the box is wider than `ShadingLockFlatShare * ridgeStep`. These
  dots sit on a 3x3 that is flat this frame: the shadow has covered the whole neighbourhood, and the gate reads that
  as a thin feature the jitter missed on flat ground. That is the exemption keeping their old lit luma.
- History within the reset extents is the largest Quality blocker, 39%, and includes most ridge-this-frame dots
  (181 Native, 243 Quality). A fresh ridge's history is not yet far from its box, so nothing releases it, and it
  then carries a full lock into the following frames.
- The ring threshold blocks 27% to 28% of dots, the opposite pair under 2%. That matches last round's finding that
  dropping the pair requirement barely moved grain.
- The `releaseExtent` hypothesis is partly right. Of all dots, 22.5% at Native and 37.6% at Quality are blocked
  past the box gates with a box wider than `ridgeStep` and would release with `releaseExtent = ridgeStep`. That is
  a ceiling for a release-normalisation fix, not a measured fix, and it would not reach the flat-3x3 class.
- Lock age confirms the hold. 63% of Native dots and 60% of Quality dots are carried at age 1 to 3, the frames
  where `hold = clamp(lock * LockHoldGain)` is still whole. The `show-lock-age` crop shows the age bands laid down
  in the sweeping staircase's wake.

Crops, Native, Quality and FXAA left to right:
[show-gate](crops/lock-trust-back15-show-gate-character-015-4x.png),
[show-gate-extent](crops/lock-trust-back15-show-gate-extent-character-015-4x.png),
[show-lock-age](crops/lock-trust-back15-show-lock-age-character-015-4x.png), with ground crops beside them.

### Real-scene results

Change relative to each condition's baseline, Native then Quality. The measured region is the character, except
ground15, where it is the ground.

| Arm | back15 grain | back15 dots | day30 grain | back60 grain | ground15 grain | ground15 dots |
| --- | --- | --- | --- | --- | --- | --- |
| `locks-off` | -50% / -40% | -66% / -56% | -33% / -32% | -36% / -32% | -15% / -23% | -49% / -47% |
| `lock-history-ridge` | -35% / -19% | -45% / -30% | -16% / -12% | -16% / -11% | -4% / -16% | -37% / -36% |
| `lock-trust-2` | -7% / -2% | -8% / -3% | -1% / -1% | -1% / +1% | -2% / -3% | -17% / -16% |
| `lock-trust-3` | -13% / -4% | -15% / -7% | -2% / -1% | -3% / +1% | -3% / -5% | -26% / -24% |
| `lock-trust-4` | -17% / -5% | -22% / -8% | -3% / -2% | -6% / -0% | -3% / -8% | -28% / -27% |
| `lock-luma-kill-0.1` | -49% / -37% | -66% / -54% | -27% / -26% | -30% / -27% | -10% / -14% | -47% / -45% |
| `lock-luma-kill-0.2` | -35% / -29% | -40% / -41% | -12% / -9% | -14% / -14% | -3% / -4% | -7% / -6% |
| `lock-luma-kill-wide-0.1` | -49% / -37% | -66% / -55% | -25% / -23% | -27% / -23% | -6% / -11% | -47% / -42% |
| `lock-luma-kill-wide-0.2` | -33% / -23% | -40% / -28% | -10% / -3% | -12% / -7% | -1% / -0% | -9% / -5% |
| `lock-trust-2+lock-luma-kill-0.2` | -37% / -30% | -44% / -44% | -12% / -8% | -16% / -14% | -5% / -8% | -24% / -22% |

No token brings lag back. Every change mean in every condition and region lies within 5.3% of its baseline's, and
the largest moves are upward, towards FXAA's. The largest is back15 Native character, 0.01889 for
`lock-luma-kill-0.1` against baseline 0.01795 and FXAA 0.01911. The complete per-token tables follow, each next to baseline, `locks-off`, `lock-history-ridge` and
FXAA. Baseline is this round's capture, `locks-off` and `lock-history-ridge` are last round's, which this round's
identical baselines make directly comparable. Machine-readable values are in
[summary.json](runs/lock-trust-round/summary.json), and every run's `metrics.txt` and `metrics.json` is under
[runs/lock-trust-round](runs/lock-trust-round/).

#### Real-scene lock-trust-2

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-trust-2 | taa-native | 0.013303 | 0.07052 | 0.01818 | 1609 | 0.006571 | 0.02553 | 0.01253 | 364 |
| back15 | lock-trust-2 | taa-quality | 0.013855 | 0.07109 | 0.01767 | 1679 | 0.007092 | 0.02796 | 0.01243 | 1693 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-trust-2 | taa-native | 0.008366 | 0.04980 | 0.00370 | 1076 | 0.005790 | 0.02269 | 0.00234 | 232 |
| day30 | lock-trust-2 | taa-quality | 0.009093 | 0.05191 | 0.00366 | 1115 | 0.006150 | 0.02460 | 0.00236 | 814 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-trust-2 | taa-native | 0.009558 | 0.05458 | 0.00490 | 1376 | 0.005844 | 0.02270 | 0.00327 | 233 |
| back60 | lock-trust-2 | taa-quality | 0.010543 | 0.05992 | 0.00500 | 1554 | 0.006459 | 0.02603 | 0.00327 | 1011 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-trust-2 | taa-native | 0.002953 | 0.01148 | 0.00117 | 370 | 0.005189 | 0.01907 | 0.00373 | 423 |
| ground15 | lock-trust-2 | taa-quality | 0.003041 | 0.01176 | 0.00118 | 341 | 0.005518 | 0.02211 | 0.00379 | 1100 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

#### Real-scene lock-trust-3

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-trust-3 | taa-native | 0.012453 | 0.06550 | 0.01833 | 1474 | 0.006470 | 0.02496 | 0.01254 | 253 |
| back15 | lock-trust-3 | taa-quality | 0.013643 | 0.07052 | 0.01774 | 1617 | 0.006866 | 0.02721 | 0.01246 | 1455 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-trust-3 | taa-native | 0.008210 | 0.04871 | 0.00368 | 1063 | 0.005685 | 0.02186 | 0.00234 | 182 |
| day30 | lock-trust-3 | taa-quality | 0.009035 | 0.05125 | 0.00367 | 1119 | 0.005889 | 0.02353 | 0.00235 | 613 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-trust-3 | taa-native | 0.009376 | 0.05385 | 0.00489 | 1339 | 0.005721 | 0.02188 | 0.00327 | 155 |
| back60 | lock-trust-3 | taa-quality | 0.010569 | 0.06048 | 0.00499 | 1559 | 0.006250 | 0.02491 | 0.00326 | 820 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-trust-3 | taa-native | 0.002947 | 0.01148 | 0.00116 | 370 | 0.005123 | 0.01876 | 0.00373 | 379 |
| ground15 | lock-trust-3 | taa-quality | 0.003029 | 0.01176 | 0.00119 | 342 | 0.005385 | 0.02134 | 0.00380 | 989 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

#### Real-scene lock-trust-4

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-trust-4 | taa-native | 0.011814 | 0.06235 | 0.01847 | 1360 | 0.006395 | 0.02463 | 0.01253 | 206 |
| back15 | lock-trust-4 | taa-quality | 0.013445 | 0.06859 | 0.01779 | 1587 | 0.006736 | 0.02690 | 0.01248 | 1320 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-trust-4 | taa-native | 0.008174 | 0.04789 | 0.00369 | 1038 | 0.005590 | 0.02129 | 0.00233 | 143 |
| day30 | lock-trust-4 | taa-quality | 0.008967 | 0.05123 | 0.00367 | 1125 | 0.005708 | 0.02298 | 0.00234 | 467 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-trust-4 | taa-native | 0.009128 | 0.05153 | 0.00490 | 1254 | 0.005633 | 0.02132 | 0.00326 | 115 |
| back60 | lock-trust-4 | taa-quality | 0.010460 | 0.06016 | 0.00500 | 1534 | 0.006082 | 0.02436 | 0.00326 | 654 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-trust-4 | taa-native | 0.002948 | 0.01148 | 0.00116 | 370 | 0.005106 | 0.01853 | 0.00373 | 370 |
| ground15 | lock-trust-4 | taa-quality | 0.003027 | 0.01176 | 0.00119 | 342 | 0.005247 | 0.02071 | 0.00381 | 950 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

#### Real-scene lock-luma-kill-0.1

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-luma-kill-0.1 | taa-native | 0.007217 | 0.04087 | 0.01889 | 592 | 0.006338 | 0.02436 | 0.01251 | 144 |
| back15 | lock-luma-kill-0.1 | taa-quality | 0.008852 | 0.04898 | 0.01830 | 805 | 0.006706 | 0.02685 | 0.01242 | 1036 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-luma-kill-0.1 | taa-native | 0.006122 | 0.03414 | 0.00374 | 512 | 0.005494 | 0.02076 | 0.00230 | 49 |
| day30 | lock-luma-kill-0.1 | taa-quality | 0.006733 | 0.03666 | 0.00352 | 497 | 0.005632 | 0.02241 | 0.00231 | 291 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-luma-kill-0.1 | taa-native | 0.006755 | 0.03676 | 0.00489 | 729 | 0.005594 | 0.02126 | 0.00324 | 61 |
| back60 | lock-luma-kill-0.1 | taa-quality | 0.007599 | 0.04142 | 0.00487 | 756 | 0.006034 | 0.02405 | 0.00320 | 490 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-luma-kill-0.1 | taa-native | 0.002926 | 0.01091 | 0.00119 | 345 | 0.004760 | 0.01877 | 0.00381 | 269 |
| ground15 | lock-luma-kill-0.1 | taa-quality | 0.003004 | 0.01176 | 0.00120 | 335 | 0.004922 | 0.02073 | 0.00382 | 723 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

#### Real-scene lock-luma-kill-0.2

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-luma-kill-0.2 | taa-native | 0.009261 | 0.05068 | 0.01866 | 1039 | 0.006698 | 0.02632 | 0.01248 | 464 |
| back15 | lock-luma-kill-0.2 | taa-quality | 0.010115 | 0.05459 | 0.01820 | 1030 | 0.007308 | 0.02855 | 0.01235 | 1812 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-luma-kill-0.2 | taa-native | 0.007382 | 0.04335 | 0.00374 | 905 | 0.005920 | 0.02325 | 0.00234 | 309 |
| day30 | lock-luma-kill-0.2 | taa-quality | 0.008329 | 0.04701 | 0.00362 | 889 | 0.006349 | 0.02542 | 0.00237 | 870 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-luma-kill-0.2 | taa-native | 0.008314 | 0.04731 | 0.00497 | 1141 | 0.006037 | 0.02353 | 0.00328 | 364 |
| back60 | lock-luma-kill-0.2 | taa-quality | 0.009046 | 0.05095 | 0.00497 | 1245 | 0.006736 | 0.02718 | 0.00327 | 1305 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-luma-kill-0.2 | taa-native | 0.002938 | 0.01120 | 0.00116 | 372 | 0.005108 | 0.01936 | 0.00378 | 478 |
| ground15 | lock-luma-kill-0.2 | taa-quality | 0.003022 | 0.01176 | 0.00120 | 337 | 0.005449 | 0.02241 | 0.00380 | 1232 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

#### Real-scene lock-luma-kill-wide-0.1

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-luma-kill-wide-0.1 | taa-native | 0.007319 | 0.04113 | 0.01888 | 589 | 0.006380 | 0.02462 | 0.01251 | 154 |
| back15 | lock-luma-kill-wide-0.1 | taa-quality | 0.008869 | 0.04800 | 0.01832 | 787 | 0.006776 | 0.02692 | 0.01243 | 1073 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-luma-kill-wide-0.1 | taa-native | 0.006271 | 0.03560 | 0.00378 | 510 | 0.005581 | 0.02126 | 0.00231 | 68 |
| day30 | lock-luma-kill-wide-0.1 | taa-quality | 0.007006 | 0.03750 | 0.00357 | 542 | 0.005858 | 0.02354 | 0.00231 | 371 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-luma-kill-wide-0.1 | taa-native | 0.007022 | 0.03838 | 0.00491 | 783 | 0.005730 | 0.02215 | 0.00324 | 106 |
| back60 | lock-luma-kill-wide-0.1 | taa-quality | 0.008012 | 0.04367 | 0.00493 | 873 | 0.006307 | 0.02546 | 0.00323 | 756 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-luma-kill-wide-0.1 | taa-native | 0.002942 | 0.01120 | 0.00116 | 369 | 0.004942 | 0.01903 | 0.00379 | 270 |
| ground15 | lock-luma-kill-wide-0.1 | taa-quality | 0.003036 | 0.01176 | 0.00118 | 337 | 0.005086 | 0.02104 | 0.00381 | 754 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

#### Real-scene lock-luma-kill-wide-0.2

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-luma-kill-wide-0.2 | taa-native | 0.009562 | 0.05159 | 0.01868 | 1052 | 0.006704 | 0.02633 | 0.01249 | 480 |
| back15 | lock-luma-kill-wide-0.2 | taa-quality | 0.010934 | 0.05960 | 0.01819 | 1250 | 0.007331 | 0.02830 | 0.01236 | 1883 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-luma-kill-wide-0.2 | taa-native | 0.007540 | 0.04342 | 0.00373 | 845 | 0.005931 | 0.02326 | 0.00234 | 310 |
| day30 | lock-luma-kill-wide-0.2 | taa-quality | 0.008887 | 0.05121 | 0.00364 | 1035 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-luma-kill-wide-0.2 | taa-native | 0.008483 | 0.04767 | 0.00496 | 1092 | 0.006053 | 0.02353 | 0.00328 | 379 |
| back60 | lock-luma-kill-wide-0.2 | taa-quality | 0.009703 | 0.05487 | 0.00499 | 1423 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-luma-kill-wide-0.2 | taa-native | 0.002961 | 0.01148 | 0.00116 | 371 | 0.005240 | 0.01934 | 0.00373 | 464 |
| ground15 | lock-luma-kill-wide-0.2 | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005671 | 0.02242 | 0.00379 | 1237 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

#### Real-scene lock-trust-2+lock-luma-kill-0.2

| Condition | Arm | Mode | Character grain mean | p95 | Change mean | Dots | Ground grain mean | p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.07499 | 0.01795 | 1742 | 0.006708 | 0.02635 | 0.01249 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.07334 | 0.01757 | 1733 | 0.007330 | 0.02830 | 0.01235 | 1881 |
| back15 | locks-off | taa-native | 0.007125 | 0.04005 | 0.01883 | 591 | 0.006238 | 0.02383 | 0.01252 | 124 |
| back15 | locks-off | taa-quality | 0.008529 | 0.04754 | 0.01829 | 771 | 0.006368 | 0.02494 | 0.01247 | 1004 |
| back15 | lock-history-ridge | taa-native | 0.009307 | 0.04953 | 0.01882 | 957 | 0.006253 | 0.02389 | 0.01252 | 130 |
| back15 | lock-history-ridge | taa-quality | 0.011486 | 0.05932 | 0.01810 | 1211 | 0.006432 | 0.02573 | 0.01246 | 1025 |
| back15 | lock-trust-2+lock-luma-kill-0.2 | taa-native | 0.008971 | 0.04981 | 0.01873 | 971 | 0.006563 | 0.02550 | 0.01252 | 350 |
| back15 | lock-trust-2+lock-luma-kill-0.2 | taa-quality | 0.009923 | 0.05373 | 0.01822 | 968 | 0.007089 | 0.02800 | 0.01242 | 1632 |
| back15 | fxaa | fxaa | 0.002961 | 0.01649 | 0.01911 | 0 | 0.002130 | 0.00869 | 0.01311 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.04997 | 0.00371 | 1097 | 0.005930 | 0.02326 | 0.00234 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.05207 | 0.00366 | 1117 | 0.006355 | 0.02524 | 0.00238 | 906 |
| day30 | locks-off | taa-native | 0.005653 | 0.03105 | 0.00377 | 461 | 0.005272 | 0.01965 | 0.00231 | 25 |
| day30 | locks-off | taa-quality | 0.006261 | 0.03471 | 0.00354 | 467 | 0.005076 | 0.01934 | 0.00232 | 203 |
| day30 | lock-history-ridge | taa-native | 0.007094 | 0.04003 | 0.00380 | 792 | 0.005293 | 0.01986 | 0.00231 | 34 |
| day30 | lock-history-ridge | taa-quality | 0.008039 | 0.04534 | 0.00361 | 919 | 0.005126 | 0.01959 | 0.00232 | 215 |
| day30 | lock-trust-2+lock-luma-kill-0.2 | taa-native | 0.007426 | 0.04314 | 0.00375 | 891 | 0.005775 | 0.02244 | 0.00234 | 230 |
| day30 | lock-trust-2+lock-luma-kill-0.2 | taa-quality | 0.008377 | 0.04701 | 0.00364 | 864 | 0.006137 | 0.02440 | 0.00236 | 815 |
| day30 | fxaa | fxaa | 0.002976 | 0.01624 | 0.00402 | 0 | 0.002071 | 0.00868 | 0.00202 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.05484 | 0.00488 | 1355 | 0.006055 | 0.02353 | 0.00328 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.05966 | 0.00500 | 1535 | 0.006729 | 0.02718 | 0.00327 | 1303 |
| back60 | locks-off | taa-native | 0.006230 | 0.03358 | 0.00482 | 619 | 0.005363 | 0.01992 | 0.00325 | 42 |
| back60 | locks-off | taa-quality | 0.007109 | 0.03920 | 0.00487 | 724 | 0.005514 | 0.02073 | 0.00322 | 394 |
| back60 | lock-history-ridge | taa-native | 0.008081 | 0.04534 | 0.00490 | 1057 | 0.005387 | 0.02013 | 0.00325 | 45 |
| back60 | lock-history-ridge | taa-quality | 0.009345 | 0.05120 | 0.00497 | 1271 | 0.005586 | 0.02127 | 0.00321 | 416 |
| back60 | lock-trust-2+lock-luma-kill-0.2 | taa-native | 0.008147 | 0.04678 | 0.00496 | 1104 | 0.005829 | 0.02269 | 0.00327 | 219 |
| back60 | lock-trust-2+lock-luma-kill-0.2 | taa-quality | 0.008967 | 0.05043 | 0.00500 | 1221 | 0.006468 | 0.02606 | 0.00327 | 1019 |
| back60 | fxaa | fxaa | 0.002955 | 0.01595 | 0.00470 | 0 | 0.002090 | 0.00868 | 0.00307 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.01148 | 0.00116 | 371 | 0.005274 | 0.01937 | 0.00373 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.01176 | 0.00118 | 340 | 0.005695 | 0.02242 | 0.00378 | 1305 |
| ground15 | locks-off | taa-native | 0.002877 | 0.01065 | 0.00120 | 321 | 0.004504 | 0.01713 | 0.00384 | 259 |
| ground15 | locks-off | taa-quality | 0.002878 | 0.01148 | 0.00121 | 272 | 0.004389 | 0.01710 | 0.00387 | 686 |
| ground15 | lock-history-ridge | taa-native | 0.002939 | 0.01077 | 0.00117 | 363 | 0.005042 | 0.01740 | 0.00373 | 323 |
| ground15 | lock-history-ridge | taa-quality | 0.003018 | 0.01176 | 0.00119 | 340 | 0.004777 | 0.01794 | 0.00383 | 839 |
| ground15 | lock-trust-2+lock-luma-kill-0.2 | taa-native | 0.002933 | 0.01093 | 0.00117 | 371 | 0.005007 | 0.01907 | 0.00378 | 389 |
| ground15 | lock-trust-2+lock-luma-kill-0.2 | taa-quality | 0.003033 | 0.01176 | 0.00120 | 337 | 0.005251 | 0.02188 | 0.00382 | 1015 |
| ground15 | fxaa | fxaa | 0.002280 | 0.01260 | 0.00135 | 0 | 0.002343 | 0.00784 | 0.00334 | 0 |

### Metal guards with each token

One filtered `dotnet test` per token, `KE_GPU_TESTS=1`, Metal hardware, the same filter as last round: the
synthetic textured-receiver case and the rest of `TemporalGameShadowGrainGpuTests`, `TemporalShadowEdgeLagGpuTests`,
`TemporalMovingShadeLagGpuTests`, `TemporalStableDetailGpuTests` and `TemporalStablePeakGpuTests`, 8 facts. Values are
the logs' printed four-decimal figures, so a printed value can sit on its bound while the unrounded one passes.
Full logs are in [guards/lock-trust-round](guards/lock-trust-round/), the table in
[guard-values.md](runs/lock-trust-round/guard-values.md).

| Guard metric | Bound | unset | trust-2 | trust-3 | trust-4 | kill-0.1 | kill-0.2 | wide-0.1 | wide-0.2 | trust-2 + kill-0.2 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Facts passed of 8 | | 8 | 7 | 7 | 6 | 7 | 8 | 7 | 7 | 7 |
| Quality still 1 px texture error | <= 0.005885 | 0.00548 | 0.00549 | 0.00550 | 0.00554 | 0.00554 | 0.00548 | 0.00548 | 0.00548 | 0.00549 |
| Quality still 1 px temporal error | <= 0.001155 | 0.00112 | 0.00112 | 0.00111 | 0.00112 | 0.00112 | 0.00112 | 0.00112 | 0.00112 | 0.00112 |
| Quality still 0.5 px texture error | <= 0.007931 | 0.00724 | 0.00735 | 0.00760 | **0.00814** | 0.00725 | 0.00724 | 0.00724 | 0.00724 | 0.00735 |
| Quality camera pan 2 px textured band | <= 0.00605 | 0.0055 | **0.0061** | **0.0070** | **0.0078** | **0.0061** | 0.0056 | 0.0058 | 0.0055 | **0.0061** |
| Quality camera pan 2 px textured tail | <= 0.00429 | 0.0039 | **0.0043** | **0.0053** | **0.0062** | 0.0039 | 0.0039 | 0.0039 | 0.0039 | **0.0043** |
| Quality flat stable-peak error | <= 0.016544 | 0.01504 | 0.01511 | 0.01515 | 0.01513 | 0.01503 | 0.01504 | 0.01504 | 0.01504 | 0.01511 |
| Quality flat stable-peak change | <= 0.002233 | 0.00203 | 0.00205 | 0.00207 | 0.00211 | 0.00203 | 0.00203 | 0.00203 | 0.00203 | 0.00205 |
| Native S 1 px advancing textured band (lag) | lower is better | 0.0162 | 0.0153 | 0.0146 | 0.0142 | 0.0127 | 0.0136 | 0.0126 | 0.0140 | 0.0135 |
| Quality S 1 px advancing textured band (lag) | lower is better | 0.0208 | 0.0206 | 0.0203 | 0.0200 | 0.0176 | 0.0184 | 0.0177 | 0.0193 | 0.0184 |
| Native S 8 px advancing textured tail | <= 1.5 x 1 px tail | 0.0044 / 0.0033 | 0.0045 / 0.0033 | 0.0044 / 0.0032 | 0.0045 / 0.0031 | **0.0046 / 0.0030** | 0.0047 / 0.0031 | **0.0046 / 0.0030** | 0.0047 / 0.0032 | 0.0047 / 0.0032 |
| Native S 8 px retreating textured tail | <= 1.5 x 1 px tail | 0.0070 / 0.0057 | 0.0072 / 0.0055 | 0.0073 / 0.0056 | 0.0075 / 0.0055 | **0.0082 / 0.0055** | 0.0083 / 0.0057 | **0.0082 / 0.0055** | 0.0082 / 0.0057 | 0.0082 / 0.0057 |
| Quality S 8 px advancing textured tail | <= 1.5 x 1 px tail | 0.0042 / 0.0031 | 0.0042 / 0.0031 | 0.0041 / 0.0031 | 0.0042 / 0.0030 | 0.0043 / 0.0031 | 0.0043 / 0.0032 | 0.0043 / 0.0031 | **0.0043 / 0.0029** | 0.0043 / 0.0032 |
| Synthetic textured grain, Native | diagnostic | 0.012026 | 0.012025 | 0.011883 | 0.011837 | 0.010442 | 0.010529 | 0.010444 | 0.010502 | 0.010529 |
| Synthetic textured grain, Quality | locks-off 0.006727 | 0.010852 | 0.010849 | 0.010820 | 0.010792 | 0.006745 | 0.007110 | 0.007095 | 0.008126 | 0.007110 |

Bold is a failed assertion. The tail rows show the 8 px tail over the 1 px tail it is bounded by.

- The trust ramp weakens texture locks before shading ones. Every K fails the Quality 2 px camera pan, worse as K
  grows, and K = 4 also fails Quality 0.5 px still detail. A texture ridge finer than a texel shows on only some
  jitter phases, so it climbs slowly, while the staircase's lit texels ridge on several consecutive frames and climb
  to a hold as fast. The synthetic case barely moves (Quality 0.010852 to 0.010792).
- The narrow kill at 0.1 keeps every still-detail and stable-peak value at the unset run's. It fails the same two
  Native 8 px tail ratios `locks-off` failed last round, and the Quality pan band, printed 0.0061 against 0.00605. The synthetic case
  falls to `locks-off` (0.006745 against 0.006727).
- The narrow kill at 0.2 passes all 8. Its Native 8 px tails sit on their bounds in print, 0.0047 against 1.5 x
  0.0031 and 0.0083 against 1.5 x 0.0057, so it has no margin there.
- The wide footprint does not buy the margin. At 0.1 it fixes the pan but keeps both Native tail failures, and at
  0.2 it fails a Quality 8 px tail ratio and cuts less grain than the narrow 0.2.
- Every kill lowers the 1 px moving-shadow band error, Native 0.0162 to between 0.0126 and 0.0140, so killing the lock on a
  shading change also removes lag it held. The cost is the 8 px retreating textured tail, 0.0070 to 0.0082 or
  0.0083: behind a fast shadow, texture locks are dropped as the edge crosses and that texture re-converges
  without its hold.

### Crops

Panels are Native, Quality and FXAA left to right, frame 15, at 4x, `crops/lock-trust-<condition>-<arm>-<region>-015-4x.png`.
Back15 character and ground crops exist for every token and the combination, and the other three conditions
for `lock-luma-kill-0.2`. For example
[baseline](crops/lock-rule-back15-baseline-character-015-4x.png),
[kill 0.2](crops/lock-trust-back15-lock-luma-kill-0.2-character-015-4x.png),
[kill 0.1](crops/lock-trust-back15-lock-luma-kill-0.1-character-015-4x.png),
[trust 4](crops/lock-trust-back15-lock-trust-4-character-015-4x.png) and
[combination](crops/lock-trust-back15-lock-trust-2+lock-luma-kill-0.2-character-015-4x.png). The kill clears most
specks on the shadowed forearm, with a few left at Quality.

### Recommendation from this round

Implement the low-frequency luma kill properly and drop the trust ramp.

The kill acts on the class the gate trace found. The largest Native blocker is a carried lock on a 3x3 that went
flat because the shadow covered it, and the ridge-this-frame dots are inside the reset extents before any release
evidence exists. A relative change in the neighbourhood's low-frequency luma catches both without asking the
release to tell shading from texture. That it reaches those two classes is inferred from the trace and the grain, not
traced with the overlays under the kill. `lock-luma-kill-0.1` comes within 1 to 3 points of `locks-off` grain at
back15 and within 5 to 9 points in the other conditions, with no lag returning, and unlike `locks-off` it keeps the
still-detail and stable-peak guards. `lock-luma-kill-0.2` passes all
eight guards and still cuts back15 character grain 35% and 29% and dots 40% and 41%.

Tuning to carry into a proper implementation: the narrow footprint, this frame's 3x3 mean against last frame's ring
plus centre, with a threshold between 0.1 and 0.2. 0.2 is the tested safe point, 0.1 the tested effective one.
The 8 px tail ratio decides where between them it lands, and that needs its own paired measurement, not a looser
bound. The trust ramp gives 2% to 17% at the cost of guard failures at every K and should not be pursued in this
form.

State: none. The kill reads the history colour the ring test already samples and stores nothing. A trust value
separate from the lock would need a channel `HistoryConfidence` does not have.

Risks the guards do not cover:

- The measure is relative with a 1e-4 floor. On a dark surface a small absolute change is a large relative one, so
  night scenes, deep shade and dark materials may lose texture locks to noise. A proper version likely wants an
  absolute floor as well, untested here.
- Animated light on texture, a torch flicker or a moving point light, changes low-frequency luma every frame and
  would keep that texture unlocked. No guard has a textured receiver under a flickering or moving local light.
- Fast shadows over texture lose detail for a few frames behind the edge, which is what the 8 px tail ratio
  measures. No guard measures how long that detail takes to return.
- Balanced and lower presets were not measured. The upscale gate held nothing at Native or Quality, so the trace
  says nothing about where `Jitter.z >= ShadingMaxUpscale`.
- `lock-luma-kill-0.2`'s ground15 dots fall only 7% and 6%, against 47% and 45% at 0.1 and `locks-off`'s 49% and
  47%. On the ground edge the threshold matters more than on the character, which a tuning pass has to measure.

### Reproduce this round

Engine pack and game refresh as in Commands above, then from the engine worktree:

```bash
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_lock_rules.py \
  "$GAME_PROBE" captures/lock-trust-round \
  baseline noop show-gate show-lock-age show-gate-extent lock-trust-2 lock-trust-3 lock-trust-4 \
  lock-luma-kill-0.1 lock-luma-kill-0.2 lock-luma-kill-wide-0.1 lock-luma-kill-wide-0.2 \
  lock-trust-2,lock-luma-kill-0.2
cd KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene
python3 scripts/gate_histogram.py captures/lock-trust-round back15
python3 scripts/lock_trust_tables.py captures/lock-trust-round \
  captures/lock-rule-round runs/lock-trust-round/summary.json lock-trust-2 lock-trust-3 \
  lock-trust-4 lock-luma-kill-0.1 lock-luma-kill-0.2 lock-luma-kill-wide-0.1 lock-luma-kill-wide-0.2 \
  lock-trust-2+lock-luma-kill-0.2
python3 scripts/make_crops.py captures/lock-trust-round crops lock-trust back15-show-gate
python3 scripts/guard_values.py guards/lock-trust-round/*.txt
```

The combination's capture directories were renamed from the comma form to `+` after capture. Overlays and `noop`
capture at back15 only. Guards, one run per token, sequentially:

```bash
KE_TEMPORAL_DIAG=lock-luma-kill-0.2 KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native \
  build-slot --heavy --label grain-guards -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-build --filter "$grain_guard_filter" --logger 'console;verbosity=detailed'
```

All 40 captures this round exited zero ([sweep](runs/lock-trust-round/capture-sweep.txt)). Both branches remain
diagnostic archives. Neither is a shipping fix or a merge candidate.


## Bilinear shadow filter seed diagnostic

The two new tokens affect the shared sun-shadow lookup used by rigid models, skinned models, splat terrain and
tile ground, including motion and dissolve variants. Each filter is compiled only while its token is set.
Resolve-only tokens leave the lighting program untouched. Filter-only tokens leave the resolve program without
its diagnostic define. Unset keeps the original nine-point path behind the preprocessor alternative.

| Token | Footprint | Depth fetches and comparisons per cascade lookup | Relative to nine |
| --- | --- | ---: | ---: |
| unset | 3x3 point comparisons | 9 | 1.00x |
| shadow-bilinear-3x3 | Nine bilinear comparison taps shared over 4x4 texels | 16 | 1.78x |
| shadow-bilinear-1 | One bilinear comparison tap over 2x2 texels | 4 | 0.44x |

These are manual point depth fetches followed by comparison and interpolation. They never interpolate depths.
For texel fraction f, the shared 3x3 weights are [1-f, 1, 1, f] in each axis, divided by nine. The single tap uses
[1-f, f]. Every texel centre is clamped inside its cascade column. Bias, normal offset, selection and cascade blend
are unchanged. A blended receiver can do two lookups, so the costs become 18, 32 and 8. Outside coverage there
are no atlas fetches. These are source fetch counts, not measured GPU timings. No textureGather support is needed.

The textured synthetic probe preserves the selected shadow filter when creating its scene and switching temporal
lock arms. Its old mask reset would silently run the point filter despite the token. Its existing reproduction
gate still asks whether baseline locks exceed locks-off by 1.3x at Quality. A failure under a new filter is
reported as loss of the fixture's original separation, without changing that bound.

### Measurement method

All captures use Metal, 2560x1440, 80 warm-up and 16 measured frames, with start time 0.49777777777777776.
back15 means back-edge at dt 1/15 with a five-minute day. day30 changes that to a thirty-minute day.
back60 uses dt 1/60 with a five-minute day. ground15 uses ground-edge at dt 1/15 with a five-minute day.
Every arm supplies its own FXAA, no-shadow frames and region masks. Grain, p95 and change come from the capture's
pooled metrics. Dots retain the archive criterion, luma over the 3x3 median by 0.05 and over that arm's FXAA by 0.05.
FXAA's relative dot count is zero by definition. Modified shadows never reuse baseline FXAA.

Locked share counts pure cyan in the same token combined with show-locked, divided by that token's character band.
The overlay compiles the diagnostic resolve, so its non-cyan pixels are not used as grain evidence.
The summary also records the share of each arm's dots covered by cyan.

Look widths are FXAA display-pixel transect distances between 10% and 90% of local attenuation,
(no-shadow luma minus shadowed luma) divided by no-shadow luma. Endpoint medians set the plateaus, three pixels on the character and five on the ground.
Isotonic regression removes material fluctuations without spatial blur. Linear interpolation locates crossings.
Profiles need attenuation contrast of 0.15 and both crossings inside the window.
Character transects are horizontal across the right upper arm at crop y 202, 204 and 206, x 213 through 230.
The complete profile plus a one-pixel border must be inside the visible body. The shadow-side attenuation median
must exceed 0.35 and the lit-side median must be below 0.15, rejecting incomplete crossings and silhouette edges.
Only frames meeting these checks contribute.
Back-edge ground transects are horizontal at y 80 through 110 in steps of five, x 90 through 219.
Ground-edge ground transects are vertical at x 280 through 330 in steps of ten, y 270 through 379.
Medians pool valid transects across 16 frames. Quartiles, counts and every accepted width are retained.
These are fixed-direction look measurements on a textured animated model, not an isolated PCF impulse response
or a screen-normal penumbra estimate. ground15 has no comparable upper-arm crossing, so character width is n/a.

### Reproduction

Set GAME_PROBE to the game probe worktree, KHAOZENGINE_FEED to an isolated private feed, CAPTURE_ROOT to untracked
captures and GUARD_ROOT to untracked guard output. Paths below are relative to the engine worktree unless a
variable supplies the root. Both branches remain diagnostic archives. No goldens are updated.

~~~bash
KHAOZENGINE_FEED="${KHAOZENGINE_FEED:?set a private feed first}" build-slot --heavy --label grain-pack -- scripts/pack-local-feed.sh
# From the game probe, refresh with the same private feed and build SnapshotTool.
scripts/refresh-engine.sh
build-slot --label grain-probe-build -- dotnet build tools/SnapshotTool/SnapshotTool.csproj -c Release
# Back in the engine worktree.
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_shadow_filters.py \
  "$GAME_PROBE" "$CAPTURE_ROOT" baseline shadow-bilinear-3x3 shadow-bilinear-1
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_shadow_filters.py \
  "$GAME_PROBE" "$CAPTURE_ROOT" show-locked shadow-bilinear-3x3,show-locked shadow-bilinear-1,show-locked \
  shadow-bilinear-3x3,lock-luma-kill-0.2 shadow-bilinear-1,lock-luma-kill-0.2 \
  shadow-bilinear-3x3,lock-luma-kill-0.2,show-locked shadow-bilinear-1,lock-luma-kill-0.2,show-locked
build-slot --label grain-test-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/run_shadow_filter_guards.py \
  "$GUARD_ROOT"
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/shadow_filter_report.py \
  "$CAPTURE_ROOT" KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/runs/shadow-filters \
  shadow-bilinear-3x3 shadow-bilinear-1 \
  shadow-bilinear-3x3+lock-luma-kill-0.2 shadow-bilinear-1+lock-luma-kill-0.2
~~~


### Bilinear results and decision

Unset is pixel-identical to the prior archive baseline in all 192 full frames, with every metrics.txt byte-identical.
That archive baseline was already compared with 21.4.0. This round adds a fresh link to that identity chain,
rather than claiming a new 21.4.0 build. See [identity](runs/shadow-filters/baseline-identity.txt).
The engine Release DLL, the vendored DLL and SnapshotTool's loaded DLL share SHA-256
d386a38992517447d58256a0055dd8edf64a8989d6b5aaf2ca83599dfd338b60.
The version stays 21.5.0-grain.1. The private pack replaced 222 package files and the refresh evicted all 78
extracted diagnostic package versions. The shared engine feed was not an output.

Neither filter reaches 1.5x its own FXAA at the back-edge conditions, so both requested luma-kill combinations
were also measured. At back15, character grain divided by own FXAA is Native / Quality as follows.

| Arm | Character grain / own FXAA, Native / Quality |
| --- | ---: |
| Baseline | 4.83 / 4.78 |
| shadow-bilinear-3x3 | 3.08 / 4.24 |
| shadow-bilinear-1 | 3.67 / 4.60 |
| shadow-bilinear-3x3 + lock-luma-kill-0.2 | 2.42 / 3.35 |
| shadow-bilinear-1 + lock-luma-kill-0.2 | 2.21 / 2.71 |

The shared 3x3 filter removes some seeds, not the lock mechanism. Back15 character dots drop from 1742 to 851
at Native and 1733 to 1166 at Quality. Character lock coverage drops from 65.33% to 55.19% and 72.68% to 65.92%.
Of its remaining dots, 97.06% and 93.74% are cyan in the lock overlay. The single tap leaves 1199 and 1634 dots,
with 62.16% and 71.00% lock coverage. Making interpolation continuous therefore does not by itself stop shadow
ridges or held bright history in this scene. It would be too strong to claim that point-PCF quantisation is the
only seed, or that this experiment proves a monotonic sampled receiver under jitter and moving light fits.

Back15 FXAA widths are 12.41 character / 6.27 ground pixels at baseline, 12.33 / 6.24 for shared 3x3 and
11.73 / 4.68 for the single tap. The 3x3 look stays close to the existing filter. The single tap visibly sharpens
the ground edge. Character widths use 11 to 12 valid upper-arm transects at back15, versus up to 48 at slower
conditions, because the moving edge leaves the narrow receiver. Counts and quartiles matter when reading
differences smaller than a pixel.

If further filter work is chosen, use shared 3x3 as the starting point. It has the strongest pure-filter result at
the reported low-frame-rate back edge and preserves the current softness. Its 16 fetches cost 78% more than nine,
so this is a candidate for proper design and later cost measurement, not a shipping recommendation.
The single tap is cheaper but leaves more back15 dots and has larger moving-rim guard misses.
Neither combination rehabilitates the blocked resolve-side luma-kill implementation. Its full-suite failures
remain recorded on the separate blocked branch, which this round did not change.

### Four-condition tables

Each metric cell contains grain mean / grain p95 / change mean / dot count. C and G mean character and ground.
The separate own-FXAA column is essential because each filter changes FXAA too.
The same tables and machine-readable data are [retained here](runs/shadow-filters/tables.md) and
[in summary.json](runs/shadow-filters/summary.json).

### shadow-bilinear-3x3

| Condition | Arm | Region | Native grain / p95 / change / dots | Quality grain / p95 / change / dots | Own FXAA grain / p95 / change / dots | Character locked % N / Q | FXAA width C / G px |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 | 0.014156 / 0.07334 / 0.01757 / 1733 | 0.002961 / 0.01649 / 0.01911 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 | 0.007330 / 0.02830 / 0.01235 / 1881 | 0.002130 / 0.00869 / 0.01311 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | shadow-bilinear-3x3 | character | 0.008027 / 0.03893 / 0.01764 / 851 | 0.011053 / 0.05435 / 0.01751 / 1166 | 0.002607 / 0.01540 / 0.01840 / 0 | 55.19 / 65.92 | 12.33 / 6.24 |
| back15 | shadow-bilinear-3x3 | ground | 0.005312 / 0.02074 / 0.01236 / 108 | 0.006370 / 0.02463 / 0.01226 / 1034 | 0.001655 / 0.00756 / 0.01310 / 0 | 55.19 / 65.92 | 12.33 / 6.24 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 | 0.009148 / 0.05207 / 0.00366 / 1117 | 0.002976 / 0.01624 / 0.00402 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 | 0.006355 / 0.02524 / 0.00238 / 906 | 0.002071 / 0.00868 / 0.00202 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | shadow-bilinear-3x3 | character | 0.005354 / 0.02882 / 0.00341 / 506 | 0.007044 / 0.03833 / 0.00318 / 784 | 0.002500 / 0.01430 / 0.00369 / 0 | 54.73 / 64.07 | 11.52 / 6.30 |
| day30 | shadow-bilinear-3x3 | ground | 0.004652 / 0.01849 / 0.00215 / 16 | 0.005288 / 0.02098 / 0.00209 / 275 | 0.001602 / 0.00729 / 0.00200 / 0 | 54.73 / 64.07 | 11.52 / 6.30 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 | 0.010472 / 0.05966 / 0.00500 / 1535 | 0.002955 / 0.01595 / 0.00470 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 | 0.006729 / 0.02718 / 0.00327 / 1303 | 0.002090 / 0.00868 / 0.00307 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | shadow-bilinear-3x3 | character | 0.006321 / 0.03384 / 0.00451 / 742 | 0.008142 / 0.04369 / 0.00434 / 1065 | 0.002547 / 0.01342 / 0.00436 / 0 | 51.77 / 63.18 | 12.34 / 6.38 |
| back60 | shadow-bilinear-3x3 | ground | 0.004766 / 0.01906 / 0.00300 / 28 | 0.005683 / 0.02326 / 0.00299 / 423 | 0.001611 / 0.00729 / 0.00298 / 0 | 51.77 / 63.18 | 12.34 / 6.38 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 | 0.003024 / 0.01176 / 0.00118 / 340 | 0.002280 / 0.01260 / 0.00135 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 | 0.005695 / 0.02242 / 0.00378 / 1305 | 0.002343 / 0.00784 / 0.00334 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | shadow-bilinear-3x3 | character | 0.002953 / 0.01148 / 0.00116 / 371 | 0.003027 / 0.01178 / 0.00118 / 341 | 0.002281 / 0.01260 / 0.00135 / 0 | 40.65 / 48.09 | n/a / 3.97 |
| ground15 | shadow-bilinear-3x3 | ground | 0.004951 / 0.01792 / 0.00367 / 396 | 0.005405 / 0.02158 / 0.00373 / 948 | 0.002293 / 0.00758 / 0.00333 / 0 | 40.65 / 48.09 | n/a / 3.97 |

### shadow-bilinear-1

| Condition | Arm | Region | Native grain / p95 / change / dots | Quality grain / p95 / change / dots | Own FXAA grain / p95 / change / dots | Character locked % N / Q | FXAA width C / G px |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 | 0.014156 / 0.07334 / 0.01757 / 1733 | 0.002961 / 0.01649 / 0.01911 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 | 0.007330 / 0.02830 / 0.01235 / 1881 | 0.002130 / 0.00869 / 0.01311 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | shadow-bilinear-1 | character | 0.010720 / 0.05352 / 0.02177 / 1199 | 0.013428 / 0.06218 / 0.02170 / 1634 | 0.002919 / 0.01679 / 0.02228 / 0 | 62.16 / 71.00 | 11.73 / 4.68 |
| back15 | shadow-bilinear-1 | ground | 0.006844 / 0.02571 / 0.01329 / 844 | 0.007336 / 0.02516 / 0.01330 / 1883 | 0.001958 / 0.00814 / 0.01351 / 0 | 62.16 / 71.00 | 11.73 / 4.68 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 | 0.009148 / 0.05207 / 0.00366 / 1117 | 0.002976 / 0.01624 / 0.00402 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 | 0.006355 / 0.02524 / 0.00238 / 906 | 0.002071 / 0.00868 / 0.00202 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | shadow-bilinear-1 | character | 0.006718 / 0.03639 / 0.00397 / 697 | 0.007998 / 0.04113 / 0.00361 / 874 | 0.002949 / 0.01679 / 0.00435 / 0 | 62.28 / 69.66 | 10.53 / 5.26 |
| day30 | shadow-bilinear-1 | ground | 0.006249 / 0.02411 / 0.00240 / 453 | 0.006328 / 0.02354 / 0.00237 / 1021 | 0.001923 / 0.00813 / 0.00210 / 0 | 62.28 / 69.66 | 10.53 / 5.26 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 | 0.010472 / 0.05966 / 0.00500 / 1535 | 0.002955 / 0.01595 / 0.00470 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 | 0.006729 / 0.02718 / 0.00327 / 1303 | 0.002090 / 0.00868 / 0.00307 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | shadow-bilinear-1 | character | 0.008310 / 0.04701 / 0.00511 / 1147 | 0.008743 / 0.04285 / 0.00494 / 1101 | 0.002920 / 0.01595 / 0.00487 / 0 | 59.42 / 67.94 | 11.94 / 4.75 |
| back60 | shadow-bilinear-1 | ground | 0.006258 / 0.02381 / 0.00335 / 552 | 0.006545 / 0.02329 / 0.00337 / 1264 | 0.001905 / 0.00813 / 0.00312 / 0 | 59.42 / 67.94 | 11.94 / 4.75 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 | 0.003024 / 0.01176 / 0.00118 / 340 | 0.002280 / 0.01260 / 0.00135 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 | 0.005695 / 0.02242 / 0.00378 / 1305 | 0.002343 / 0.00784 / 0.00334 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | shadow-bilinear-1 | character | 0.002954 / 0.01148 / 0.00116 / 371 | 0.003028 / 0.01176 / 0.00118 / 341 | 0.002281 / 0.01260 / 0.00135 / 0 | 40.67 / 48.07 | n/a / 2.72 |
| ground15 | shadow-bilinear-1 | ground | 0.005390 / 0.01961 / 0.00380 / 582 | 0.005579 / 0.02099 / 0.00389 / 1100 | 0.002360 / 0.00784 / 0.00333 / 0 | 40.67 / 48.07 | n/a / 2.72 |

### shadow-bilinear-3x3+lock-luma-kill-0.2

| Condition | Arm | Region | Native grain / p95 / change / dots | Quality grain / p95 / change / dots | Own FXAA grain / p95 / change / dots | Character locked % N / Q | FXAA width C / G px |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 | 0.014156 / 0.07334 / 0.01757 / 1733 | 0.002961 / 0.01649 / 0.01911 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 | 0.007330 / 0.02830 / 0.01235 / 1881 | 0.002130 / 0.00869 / 0.01311 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | shadow-bilinear-3x3+lock-luma-kill-0.2 | character | 0.006315 / 0.03359 / 0.01759 / 535 | 0.008730 / 0.04646 / 0.01745 / 770 | 0.002607 / 0.01540 / 0.01840 / 0 | 44.58 / 54.11 | 12.33 / 6.24 |
| back15 | shadow-bilinear-3x3+lock-luma-kill-0.2 | ground | 0.005309 / 0.02076 / 0.01235 / 109 | 0.006400 / 0.02468 / 0.01226 / 1058 | 0.001655 / 0.00756 / 0.01310 / 0 | 44.58 / 54.11 | 12.33 / 6.24 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 | 0.009148 / 0.05207 / 0.00366 / 1117 | 0.002976 / 0.01624 / 0.00402 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 | 0.006355 / 0.02524 / 0.00238 / 906 | 0.002071 / 0.00868 / 0.00202 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | shadow-bilinear-3x3+lock-luma-kill-0.2 | character | 0.005124 / 0.02744 / 0.00339 / 431 | 0.006570 / 0.03556 / 0.00320 / 636 | 0.002500 / 0.01430 / 0.00369 / 0 | 51.19 / 61.37 | 11.52 / 6.30 |
| day30 | shadow-bilinear-3x3+lock-luma-kill-0.2 | ground | 0.004651 / 0.01849 / 0.00215 / 16 | 0.005287 / 0.02095 / 0.00209 / 275 | 0.001602 / 0.00729 / 0.00200 / 0 | 51.19 / 61.37 | 11.52 / 6.30 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 | 0.010472 / 0.05966 / 0.00500 / 1535 | 0.002955 / 0.01595 / 0.00470 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 | 0.006729 / 0.02718 / 0.00327 / 1303 | 0.002090 / 0.00868 / 0.00307 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | shadow-bilinear-3x3+lock-luma-kill-0.2 | character | 0.006008 / 0.03219 / 0.00451 / 683 | 0.007686 / 0.04170 / 0.00437 / 962 | 0.002547 / 0.01342 / 0.00436 / 0 | 46.96 / 59.32 | 12.34 / 6.38 |
| back60 | shadow-bilinear-3x3+lock-luma-kill-0.2 | ground | 0.004768 / 0.01906 / 0.00300 / 28 | 0.005684 / 0.02326 / 0.00299 / 422 | 0.001611 / 0.00729 / 0.00298 / 0 | 46.96 / 59.32 | 12.34 / 6.38 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 | 0.003024 / 0.01176 / 0.00118 / 340 | 0.002280 / 0.01260 / 0.00135 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 | 0.005695 / 0.02242 / 0.00378 / 1305 | 0.002343 / 0.00784 / 0.00334 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | shadow-bilinear-3x3+lock-luma-kill-0.2 | character | 0.002938 / 0.01093 / 0.00117 / 372 | 0.003023 / 0.01176 / 0.00120 / 337 | 0.002281 / 0.01260 / 0.00135 / 0 | 37.52 / 46.57 | n/a / 3.97 |
| ground15 | shadow-bilinear-3x3+lock-luma-kill-0.2 | ground | 0.004786 / 0.01794 / 0.00373 / 343 | 0.005178 / 0.02157 / 0.00376 / 893 | 0.002293 / 0.00758 / 0.00333 / 0 | 37.52 / 46.57 | n/a / 3.97 |

### shadow-bilinear-1+lock-luma-kill-0.2

| Condition | Arm | Region | Native grain / p95 / change / dots | Quality grain / p95 / change / dots | Own FXAA grain / p95 / change / dots | Character locked % N / Q | FXAA width C / G px |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 | 0.014156 / 0.07334 / 0.01757 / 1733 | 0.002961 / 0.01649 / 0.01911 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 | 0.007330 / 0.02830 / 0.01235 / 1881 | 0.002130 / 0.00869 / 0.01311 / 0 | 65.33 / 72.68 | 12.41 / 6.27 |
| back15 | shadow-bilinear-1+lock-luma-kill-0.2 | character | 0.006445 / 0.03613 / 0.02207 / 578 | 0.007908 / 0.04339 / 0.02204 / 761 | 0.002919 / 0.01679 / 0.02228 / 0 | 41.19 / 49.88 | 11.73 / 4.68 |
| back15 | shadow-bilinear-1+lock-luma-kill-0.2 | ground | 0.006630 / 0.02526 / 0.01325 / 534 | 0.007361 / 0.02693 / 0.01327 / 1701 | 0.001958 / 0.00814 / 0.01351 / 0 | 41.19 / 49.88 | 11.73 / 4.68 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 | 0.009148 / 0.05207 / 0.00366 / 1117 | 0.002976 / 0.01624 / 0.00402 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 | 0.006355 / 0.02524 / 0.00238 / 906 | 0.002071 / 0.00868 / 0.00202 / 0 | 66.05 / 72.29 | 11.20 / 6.15 |
| day30 | shadow-bilinear-1+lock-luma-kill-0.2 | character | 0.005539 / 0.03162 / 0.00392 / 507 | 0.006598 / 0.03529 / 0.00365 / 629 | 0.002949 / 0.01679 / 0.00435 / 0 | 55.68 / 63.87 | 10.53 / 5.26 |
| day30 | shadow-bilinear-1+lock-luma-kill-0.2 | ground | 0.006175 / 0.02388 / 0.00240 / 428 | 0.006454 / 0.02414 / 0.00235 / 1030 | 0.001923 / 0.00813 / 0.00210 / 0 | 55.68 / 63.87 | 10.53 / 5.26 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 | 0.010472 / 0.05966 / 0.00500 / 1535 | 0.002955 / 0.01595 / 0.00470 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 | 0.006729 / 0.02718 / 0.00327 / 1303 | 0.002090 / 0.00868 / 0.00307 / 0 | 62.54 / 70.67 | 12.44 / 6.39 |
| back60 | shadow-bilinear-1+lock-luma-kill-0.2 | character | 0.006264 / 0.03528 / 0.00518 / 776 | 0.007232 / 0.03837 / 0.00505 / 837 | 0.002920 / 0.01595 / 0.00487 / 0 | 51.21 / 60.38 | 11.94 / 4.75 |
| back60 | shadow-bilinear-1+lock-luma-kill-0.2 | ground | 0.006178 / 0.02353 / 0.00333 / 501 | 0.006608 / 0.02410 / 0.00335 / 1253 | 0.001905 / 0.00813 / 0.00312 / 0 | 51.21 / 60.38 | 11.94 / 4.75 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 | 0.003024 / 0.01176 / 0.00118 / 340 | 0.002280 / 0.01260 / 0.00135 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 | 0.005695 / 0.02242 / 0.00378 / 1305 | 0.002343 / 0.00784 / 0.00334 / 0 | 40.72 / 48.15 | n/a / 3.80 |
| ground15 | shadow-bilinear-1+lock-luma-kill-0.2 | character | 0.002936 / 0.01093 / 0.00117 / 373 | 0.003022 / 0.01176 / 0.00120 / 337 | 0.002281 / 0.01260 / 0.00135 / 0 | 37.54 / 46.56 | n/a / 2.72 |
| ground15 | shadow-bilinear-1+lock-luma-kill-0.2 | ground | 0.005159 / 0.01958 / 0.00384 / 439 | 0.005361 / 0.02132 / 0.00390 / 1003 | 0.002360 / 0.00784 / 0.00333 / 0 | 37.54 / 46.56 | n/a / 2.72 |



### Bilinear guards and limits

Both named temporal subsets pass all eight facts, zero skipped, including the faceted and textured grain cases.
The full shadow and golden selection used one Metal test process per filter, with KE_GPU_TESTS=1.
Each process ran 1047 tests, 1045 passed, two failed and zero skipped. One failure in each was the field-only
shader-discovery test after the diagnostic fragments became properties. That test now discovers those properties
and explicitly checks the model, skinned, splat and tile-ground members. The correction passed all 35 focused
token and frame-block tests. No renderer code changed after the captures or GPU runs.

The remaining failure is TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims.
Besides the expected pinned spatial-reference changes, it reports these acceptance misses.

| Moving-rim metric | Bound including baseline rounding | shadow-bilinear-3x3 | shadow-bilinear-1 |
| --- | ---: | ---: | ---: |
| Quality M 0.5 leading flat error | <= 0.01023825 | 0.010485 fail | 0.010718 fail |
| Native MS 1.0 leading flat error | <= 0.00596585 | no miss | 0.008094 fail |
| Native MS 1.0 leading flat temporal error | <= 0.00900405 | no miss | 0.014099 fail |
| Maximum pinned-reference movement | <= 0.000001 | 0.000336 fail | 0.000964 fail |

| Guard metric | Bound | shadow-bilinear-3x3 | shadow-bilinear-1 |
| --- | --- | ---: | ---: |
| Named temporal facts passed | 8 of 8 | 8 | 8 |
| Quality still 1 px texture error | <= 0.005885 | 0.00548 | 0.00548 |
| Quality still 1 px texture temporal error | <= 0.001155 | 0.00112 | 0.00112 |
| Quality still 0.5 px texture error | <= 0.007931 | 0.00724 | 0.00724 |
| Quality camera pan 2 px textured band error | <= 0.00605 | 0.0042 | 0.0049 |
| Quality camera pan 2 px textured tail error | <= 0.00429 | 0.0039 | 0.0039 |
| Quality flat stable-peak error | <= 0.016544 | 0.01504 | 0.01504 |
| Quality flat stable-peak change | <= 0.002233 | 0.00203 | 0.00203 |
| Native S 1 px advancing textured band error | lag, lower is better | 0.0147 | 0.0145 |
| Quality S 1 px advancing textured band error | lag, lower is better | 0.021 | 0.02 |
| Native S 1 px advancing textured tail | reference for the 8 px ratio | 0.0031 | 0.0034 |
| Native S 8 px advancing textured tail | <= 1.5 x the 1 px tail | 0.0045 | 0.0044 |
| Native S 1 px retreating textured tail | reference for the 8 px ratio | 0.0056 | 0.0046 |
| Native S 8 px retreating textured tail | <= 1.5 x the 1 px tail | 0.0071 | 0.0067 |
| Quality S 1 px advancing textured tail | reference for the 8 px ratio | 0.0033 | 0.0026 |
| Quality S 8 px advancing textured tail | <= 1.5 x the 1 px tail | 0.0043 | 0.0036 |
| Synthetic textured grain, Native | diagnostic | 0.01177 | 0.012785 |
| Synthetic textured grain, Quality | diagnostic requested grain, gate below | 0.010593 | 0.010358 |
| Quality textured reproduction gate | baseline > 1.3 x locks-off | 0.010593 > 0.008638 | 0.010358 > 0.008327 |


All 58 grid golden comparisons stay within their existing 0.01 bound. The changed shadow-bearing grids have
these maximum channel differences against their committed golden cell values. The complete grids' delta logs
are retained, including the passing small differences. No golden was rebaked.

| Golden scene | shadow-bilinear-3x3 worst cell | shadow-bilinear-1 worst cell |
| --- | ---: | ---: |
| scene3d_shadow_map | 0.0007 | 0.0031 |
| scene3d_splat_shadow | 0.0005 | 0.0011 |
| scene3d_cascade_wide | 0.0005 | 0.0008 |
| scene3d_cascade_handoff | 0.0004 | 0.0018 |
| scene3d_sky | 0.0007 | 0.0031 |

These are distances to committed grids, not a separate new baseline render subtracted from the candidate.
The shadow lookup changes actual pixels while staying below the existing golden tolerance.

Both complete solutions build Release with zero warnings. The broader Render3D namespace run, with GPU facts
off, reports 3775 passed, 50 skipped and two failed. The discovery failure above is corrected. The remaining
raw Lanczos source-count assertion expects five calls and sees nine, the same pre-existing archive failure
documented in earlier rounds. The full engine format check reports 138 whitespace diagnostics in 11 untouched
archive files. None is in a changed file. These are reported without unrelated repairs.
The full Grimhollow suite and navigation rebakes were deliberately omitted for the diagnostic pin.

Logs and measured guard rows are under [guards](runs/shadow-filters/guards) and
[verification](runs/shadow-filters/verification). [guard-results.json](runs/shadow-filters/guard-results.json)
records the exact named facts and both original failure messages.

### Bilinear owner-look evidence

Each character sheet is 4x nearest-neighbour, with Native, Quality and that arm's FXAA left to right.
Separate per-mode crops are retained alongside the sheets. Each contact sheet is the complete captured
normal-scale contact-sheet frame, showing the character and ground together.

| Arm | Character 4x | Normal-scale contact sheet |
| --- | --- | --- |
| Baseline | [crop](runs/shadow-filters/visuals/baseline-character-015-4x.png) | [sheet](runs/shadow-filters/visuals/baseline-contact-sheet-015.png) |
| shadow-bilinear-3x3 | [crop](runs/shadow-filters/visuals/shadow-bilinear-3x3-character-015-4x.png) | [sheet](runs/shadow-filters/visuals/shadow-bilinear-3x3-contact-sheet-015.png) |
| shadow-bilinear-1 | [crop](runs/shadow-filters/visuals/shadow-bilinear-1-character-015-4x.png) | [sheet](runs/shadow-filters/visuals/shadow-bilinear-1-contact-sheet-015.png) |
| shadow-bilinear-3x3 + kill | [crop](runs/shadow-filters/visuals/shadow-bilinear-3x3+lock-luma-kill-0.2-character-015-4x.png) | [sheet](runs/shadow-filters/visuals/shadow-bilinear-3x3+lock-luma-kill-0.2-contact-sheet-015.png) |
| shadow-bilinear-1 + kill | [crop](runs/shadow-filters/visuals/shadow-bilinear-1+lock-luma-kill-0.2-character-015-4x.png) | [sheet](runs/shadow-filters/visuals/shadow-bilinear-1+lock-luma-kill-0.2-contact-sheet-015.png) |

The [2x ground comparison](runs/shadow-filters/visuals/ground-edge-fxaa-015-2x.png) uses each arm's own FXAA.
Its order is baseline, shared 3x3, single tap, shared 3x3 plus kill, single tap plus kill.

## Lit visibility signal diagnostic

This round tests visibility from the renderer instead of inferring shading from colour. These remain archive
branches. The earlier luma-kill recommendation above was rejected by the full temporal suite on the production
attempt and is not an accepted fix.

### Plumbing and token semantics

Only a process started with a `vis-*` token builds this path. The rigid, GPU-skinned, CPU-skinned, foliage,
splat-terrain and tile-ground lighting fragments, including motion and dissolve variants, write the exact
`keyShadow` returned by their existing `sampleKeyShadow` call. The normal offset, bias, cascade selection,
coverage fallback, strength and cascade blend are therefore the ones used by the lit colour. No shadow is
sampled again. A disabled shadow writes 1, and the background clears to 1.

The signal is an extra R32Float attachment at internal resolution, at location 3 without motion and location 4
with motion. Transparent model writers explicitly emit zero into that slot with the existing zero/one destination-preserving blend. Their motion variants shift the slot to location 4. Later colour-only passes cannot overwrite it. The
MSAA variant attaches and resolves an extra multisampled signal too. No lit path required a fallback pass.
R32Float uses a format already supported by the GPU seam, which has no single-channel R16Float member.

`TemporalResolveRenderer` owns two internal-resolution R32Float histories, following the colour history's read
and write indices and target generation. After either entry point finishes, `CopyTexture` stores this frame's
lit attachment into the write history. The read history stays intact for the diagnostic overlay. Both entry
points bind the same visibility resources and run the same comparison helper. Their previous-jitter uniform
is updated from the last resolved frame, and invalid colour history suppresses visibility comparisons too.
Standalone resolve tests without a lit input get a constant fully lit signal.

The resolve compares each current texel of the centre's 3x3 footprint with bilinear visibility history at its
corresponding reprojected position. It removes current jitter and adds previous jitter when addressing the
stored internal grid. It takes the maximum of the nine absolute changes. This covers a shading change beside
a locked sample, including diagonals, while comparing corresponding positions rather than unrelated footprint
extrema. Single-texel and 2x2 variants were not measured in this round.

| Token | Rule where history is usable and maximum visibility change exceeds T |
| --- | --- |
| `vis-release-0.1` | T 0.1, clear stored lock and clip hold |
| `vis-release-0.2` | T 0.2, clear stored lock and clip hold |
| `vis-release-0.3` | T 0.3, clear stored lock and clip hold |
| `vis-reset-0.2` | T 0.2, also clear accumulated weight and display share, as the shading release does |
| `show-vis-release` | Paint the firing set magenta after the post chain, without entering history |

The visibility bits occupy bits 24 to 28 of the diagnostic mask. They travel as the small integer `mask >> 24`
in the previously reserved `Params.x`. Existing bits stay in `Params.w` as `mask & 0xFFFFFF`, so composing an
overlay with a visibility token does not round low bits away in a float uniform. With multiple threshold tokens,
0.1 takes precedence, then 0.2, then 0.3. The reset bit adds the reset action. Measurements select one arm.

Unset allocates no visibility attachment, history, uniform buffer or extra bindings. It adds no compiled GLSL.
All four final baseline conditions are compared in [the identity proof](runs/visibility/baseline-identity.txt).

### Visibility measurement method

The same four conditions, own FXAA, region masks and dot criterion as the bilinear round apply. Every capture
passes `--start-time 0.49777777777777776`, with 80 warm-up and 16 measured frames. Lock share comes from the
same arm plus `show-locked`. Release coincidence comes from the same arm plus `show-vis-release`. Overlay
colours are never included in grain or dot measurements.

### Visibility cost

Costs below are source fetch and storage counts, not GPU timing. No timing benchmark or load test ran.
At a 2560x1440 display, Native renders 2560x1440 internally. Quality renders 1707x960.

| Resource | Native bytes (MiB) | Quality bytes (MiB) |
| --- | ---: | ---: |
| Lit R32Float attachment | 14745600 (14.0625) | 6554880 (6.2512) |
| One R32Float visibility history | 14745600 (14.0625) | 6554880 (6.2512) |
| Two history textures | 29491200 (28.1250) | 13109760 (12.5024) |
| Signal plus history pair | 44236800 (42.1875) | 19664640 (18.7537) |

There is also one 16-byte previous-jitter uniform buffer per resolve. The fully lit fallback is allocated only
for standalone tests that omit a lit input. Normal non-MSAA scenes allocate no multisampled visibility target.

The lit fragment adds one scalar output store and no texture fetch or shadow comparison. The resolve adds nine
current `texelFetch` operations and nine bilinear history samples, nine absolute differences and nine max
reductions, plus coordinate arithmetic and the threshold branch. A bilinear history sample covers four R32F
texels, so the uncached upper source-read count is 45 texels or 180 bytes per display pixel. Cache reuse and
compression are not measured. The signal store is 4 bytes per shaded internal sample. The history copy reads
and writes 4 bytes each per internal pixel, another 8 bytes. This is 192 bytes per pixel at Native before clear
traffic, or about 185.33 bytes per display pixel at Quality. Clearing the signal adds 4 bytes per internal pixel.
The bilinear shadow combination adds the previously measured 16 versus 9 shadow-depth fetches per cascade
lookup. None of these figures is an instruction-level ISA measurement or a frame-time claim.

### Reproduce visibility

The workflow has no diagnostic environment input. `TemporalVisibilityArchiveGpuTests` temporarily selects the
chosen token only in this archive branch's `cross-platform-gpu` workflow dispatch, inside the test assembly.
Its GPU fact asserts the startup mask and MRT flag and prints the token. It does not select a token in engine
packages, game captures, other branches, other workflows or local runs. No workflow file was changed.

From the engine archive, with `GAME_PROBE`, `KHAOZENGINE_FEED`, `CAPTURE_ROOT` and `GUARD_ROOT` set as above:

~~~bash
KHAOZENGINE_FEED="${KHAOZENGINE_FEED:?set a private feed first}" build-slot --heavy --label vis-pack -- scripts/pack-local-feed.sh
# Refresh and build from the game probe, using the same private feed.
build-slot --label vis-refresh -- scripts/refresh-engine.sh
build-slot --label vis-probe-build -- dotnet build Grimhollow.slnx -c Release
# Back in the engine archive.
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_shadow_filters.py \
  "$GAME_PROBE" "$CAPTURE_ROOT" baseline vis-release-0.1 vis-release-0.2 vis-release-0.3 vis-reset-0.2
build-slot --label vis-test-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/run_shadow_filter_guards.py \
  "$GUARD_ROOT" vis-release-0.1 vis-reset-0.2 shadow-bilinear-3x3,vis-release-0.1
~~~


### Full local guard reading

Every candidate runs the complete temporal correctness selection plus shadow and golden tests, one Metal process
per token with `KE_GPU_TESTS=1`. The selection covers 48 temporal correctness classes and 1360 tests, zero skipped.
The two `Temporal*CostPerfGpuTests` classes are repeated timing benchmarks and are excluded under the no-load rule.
No assertion or acceptance bound is relaxed. Failure messages and every measured bound are retained in
[local-failures.md](runs/visibility/local-failures.md), with complete outcomes in
[local-guard-results.json](runs/visibility/local-guard-results.json).

| Arm | Passed | Failed | Interpretation |
| --- | ---: | ---: | --- |
| `noop` | 1356 | 4 | Original archive entry-identity failures only |
| MRT, release disabled (`show-vis-release` alone) | 1355 | 5 | Same four, plus the expected debug-pass-built assertion under an explicitly selected overlay |
| `vis-release-0.1` | 1354 | 6 | Same four, moving-shade lag and moving-rim acceptance failures |
| `vis-release-0.2` | 1354 | 6 | Same four, moving-shade lag and moving-rim acceptance failures |
| `vis-release-0.3` | 1355 | 5 | Same four and moving-rim acceptance failure |
| `vis-reset-0.2` | 1353 | 7 | Same four, still-shadow, moving-shade lag and moving-rim acceptance failures |
| `shadow-bilinear-3x3,vis-release-0.1` | 1354 | 6 | Same four, fast-tail ratios and moving-rim acceptance failures, plus expected bilinear pinned-reference movement inside the rim fact |

The four entry-identity failures are not introduced by this signal. An isolated test-output copy loaded the
original a787 archive Render3D DLL from the probe's 471b package. Its SHA-256 is
`d386a38992517447d58256a0055dd8edf64a8989d6b5aaf2ca83599dfd338b60`.
With `noop`, it produces exactly the same four failure messages, differing-value counts and maxima as the new
binary. Unset passes all four facts. Native and Quality have identical colour in that fixture but stored locks
differ by up to 0.00048828125. Performance also differs in colour by 0.010253906.
See the [original control](runs/visibility/controls/original-noop-identity.json),
[new noop control](runs/visibility/controls/noop-identity.json), and
[unset control](runs/visibility/controls/unset-identity.json). The diagnostic archive therefore cannot claim strict
state identity for either entry point. This pre-existing diagnostic compilation issue was recorded, not repaired.

Both the `noop` and disabled-release MRT controls pass every shadow, texture and thin-geometry guard. The MRT
control's additional failure expects the debug pass not to exist with `DebugView.None`, while the explicit overlay
correctly builds it. That is a control-only structural mismatch, not a measured allocation failure. These controls
separate the candidate shadow regressions from the new attachment and visibility-history plumbing.

The static unshadowed detail and stable-peak values stay at the archive's reference values. The visibility rule
still changes the static-light camera-pan case and the moving textured rim. Inference from those controls: a raw
jittered visibility sample is not invariant under resampling. Bilinear history, neighbouring geometry and the
3x3 maximum can report a visibility change even when lighting of the corresponding texture did not change.
The footprint uses the resolved surface's motion plus each texel offset, without separate per-neighbour motion
or depth qualification. This round does not isolate those mechanisms individually.

The reset arm has the lowest back15 grain but makes slow-condition character change about twice its own FXAA.
The release-only 0.1 arm is the stronger overall grain and stability choice. Increasing the threshold to 0.3 removes
the lag fact's misses, but its Quality moving-rim temporal error is still 0.009746 against 0.00886655.
The full guards do not accept any arm as a shipping fix.

The three initial plumbing checks also have executable regressions. The token and float-mask contract suite
passes 30 facts including LF and CRLF sources. The transparent-interface suite first fails all ten base and
motion cases, then passes after every preserving fragment declares the extra output. The combined suite is
40 of 40 with a visibility token. The fully lit synthetic fallback has a separate red/green proof using the
existing seeded-history blend fact. These changes do not loosen a visual acceptance bound.

The extra transparent target uses the existing zero/one blend, since the GPU seam has no per-target write mask.
Its dummy scalar output is zero and the opaque visibility survives. Any blend traffic for covered transparent
fragments is backend dependent and is not included in the opaque-sample bandwidth estimate above.


### Visibility real-scene results

The tables include each candidate beside point-PCF baseline, bilinear-only and each arm's own FXAA.
Both character and ground regions are reported, with the locked share of that region rather than reusing the
character share on ground rows. All machine-readable values and band counts are in
[summary.json](runs/visibility/summary.json). The disabled-release MRT control has Native/Quality grain
0.014280/0.014124 at back15, the archive noop's values, and 1739/1733 dots. Its field and history alone do not
produce the reduction.

Numbers are grain mean / p95 / change mean / dots / locked percent. FXAA omits locked percent.

### vis-release-0.1

| Condition | Arm | Region | Native | Quality | Own FXAA |
| --- | --- | --- | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 / 65.33 | 0.014156 / 0.07334 / 0.01757 / 1733 / 72.68 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 / 14.16 | 0.007330 / 0.02830 / 0.01235 / 1881 / 24.96 | 0.002130 / 0.00869 / 0.01311 / 0 |
| back15 | shadow-bilinear-3x3 | character | 0.008027 / 0.03893 / 0.01764 / 851 / 55.19 | 0.011053 / 0.05435 / 0.01751 / 1166 / 65.92 | 0.002607 / 0.01540 / 0.01840 / 0 |
| back15 | shadow-bilinear-3x3 | ground | 0.005312 / 0.02074 / 0.01236 / 108 / 9.70 | 0.006370 / 0.02463 / 0.01226 / 1034 / 19.11 | 0.001655 / 0.00756 / 0.01310 / 0 |
| back15 | vis-release-0.1 | character | 0.007271 / 0.04113 / 0.01878 / 624 / 30.67 | 0.008822 / 0.04981 / 0.01824 / 820 / 31.17 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | vis-release-0.1 | ground | 0.006320 / 0.02435 / 0.01251 / 123 / 6.32 | 0.006654 / 0.02635 / 0.01243 / 1020 / 11.23 | 0.002130 / 0.00869 / 0.01311 / 0 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 / 66.05 | 0.009148 / 0.05207 / 0.00366 / 1117 / 72.29 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 / 14.85 | 0.006355 / 0.02524 / 0.00238 / 906 / 26.58 | 0.002071 / 0.00868 / 0.00202 / 0 |
| day30 | shadow-bilinear-3x3 | character | 0.005354 / 0.02882 / 0.00341 / 506 / 54.73 | 0.007044 / 0.03833 / 0.00318 / 784 / 64.07 | 0.002500 / 0.01430 / 0.00369 / 0 |
| day30 | shadow-bilinear-3x3 | ground | 0.004652 / 0.01849 / 0.00215 / 16 / 10.01 | 0.005288 / 0.02098 / 0.00209 / 275 / 19.19 | 0.001602 / 0.00729 / 0.00200 / 0 |
| day30 | vis-release-0.1 | character | 0.005762 / 0.03191 / 0.00374 / 467 / 30.81 | 0.006544 / 0.03583 / 0.00352 / 501 / 30.50 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | vis-release-0.1 | ground | 0.005384 / 0.02043 / 0.00230 / 25 / 7.56 | 0.005395 / 0.02074 / 0.00231 / 202 / 12.87 | 0.002071 / 0.00868 / 0.00202 / 0 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 / 62.54 | 0.010472 / 0.05966 / 0.00500 / 1535 / 70.67 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 / 15.25 | 0.006729 / 0.02718 / 0.00327 / 1303 / 27.14 | 0.002090 / 0.00868 / 0.00307 / 0 |
| back60 | shadow-bilinear-3x3 | character | 0.006321 / 0.03384 / 0.00451 / 742 / 51.77 | 0.008142 / 0.04369 / 0.00434 / 1065 / 63.18 | 0.002547 / 0.01342 / 0.00436 / 0 |
| back60 | shadow-bilinear-3x3 | ground | 0.004766 / 0.01906 / 0.00300 / 28 / 9.87 | 0.005683 / 0.02326 / 0.00299 / 423 / 19.25 | 0.001611 / 0.00729 / 0.00298 / 0 |
| back60 | vis-release-0.1 | character | 0.006500 / 0.03501 / 0.00480 / 655 / 31.02 | 0.007556 / 0.04170 / 0.00487 / 831 / 31.78 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | vis-release-0.1 | ground | 0.005474 / 0.02051 / 0.00324 / 42 / 7.61 | 0.005868 / 0.02325 / 0.00320 / 394 / 13.06 | 0.002090 / 0.00868 / 0.00307 / 0 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 / 40.72 | 0.003024 / 0.01176 / 0.00118 / 340 / 48.15 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 / 9.89 | 0.005695 / 0.02242 / 0.00378 / 1305 / 18.78 | 0.002343 / 0.00784 / 0.00334 / 0 |
| ground15 | shadow-bilinear-3x3 | character | 0.002953 / 0.01148 / 0.00116 / 371 / 40.65 | 0.003027 / 0.01178 / 0.00118 / 341 / 48.09 | 0.002281 / 0.01260 / 0.00135 / 0 |
| ground15 | shadow-bilinear-3x3 | ground | 0.004951 / 0.01792 / 0.00367 / 396 / 8.25 | 0.005405 / 0.02158 / 0.00373 / 948 / 17.27 | 0.002293 / 0.00758 / 0.00333 / 0 |
| ground15 | vis-release-0.1 | character | 0.002924 / 0.01120 / 0.00117 / 360 / 34.29 | 0.002984 / 0.01176 / 0.00119 / 319 / 39.25 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | vis-release-0.1 | ground | 0.004612 / 0.01852 / 0.00383 / 261 / 4.49 | 0.004730 / 0.02016 / 0.00384 / 694 / 9.24 | 0.002343 / 0.00784 / 0.00334 / 0 |

### vis-release-0.2

| Condition | Arm | Region | Native | Quality | Own FXAA |
| --- | --- | --- | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 / 65.33 | 0.014156 / 0.07334 / 0.01757 / 1733 / 72.68 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 / 14.16 | 0.007330 / 0.02830 / 0.01235 / 1881 / 24.96 | 0.002130 / 0.00869 / 0.01311 / 0 |
| back15 | shadow-bilinear-3x3 | character | 0.008027 / 0.03893 / 0.01764 / 851 / 55.19 | 0.011053 / 0.05435 / 0.01751 / 1166 / 65.92 | 0.002607 / 0.01540 / 0.01840 / 0 |
| back15 | shadow-bilinear-3x3 | ground | 0.005312 / 0.02074 / 0.01236 / 108 / 9.70 | 0.006370 / 0.02463 / 0.01226 / 1034 / 19.11 | 0.001655 / 0.00756 / 0.01310 / 0 |
| back15 | vis-release-0.2 | character | 0.007830 / 0.04340 / 0.01868 / 722 / 36.60 | 0.009400 / 0.05176 / 0.01818 / 943 / 37.76 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | vis-release-0.2 | ground | 0.006359 / 0.02440 / 0.01251 / 131 / 7.22 | 0.006720 / 0.02690 / 0.01243 / 1045 / 13.38 | 0.002130 / 0.00869 / 0.01311 / 0 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 / 66.05 | 0.009148 / 0.05207 / 0.00366 / 1117 / 72.29 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 / 14.85 | 0.006355 / 0.02524 / 0.00238 / 906 / 26.58 | 0.002071 / 0.00868 / 0.00202 / 0 |
| day30 | shadow-bilinear-3x3 | character | 0.005354 / 0.02882 / 0.00341 / 506 / 54.73 | 0.007044 / 0.03833 / 0.00318 / 784 / 64.07 | 0.002500 / 0.01430 / 0.00369 / 0 |
| day30 | shadow-bilinear-3x3 | ground | 0.004652 / 0.01849 / 0.00215 / 16 / 10.01 | 0.005288 / 0.02098 / 0.00209 / 275 / 19.19 | 0.001602 / 0.00729 / 0.00200 / 0 |
| day30 | vis-release-0.2 | character | 0.006443 / 0.03583 / 0.00368 / 588 / 45.20 | 0.007285 / 0.03922 / 0.00351 / 671 / 44.51 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | vis-release-0.2 | ground | 0.005516 / 0.02101 / 0.00230 / 43 / 10.20 | 0.005625 / 0.02242 / 0.00231 / 290 / 17.71 | 0.002071 / 0.00868 / 0.00202 / 0 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 / 62.54 | 0.010472 / 0.05966 / 0.00500 / 1535 / 70.67 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 / 15.25 | 0.006729 / 0.02718 / 0.00327 / 1303 / 27.14 | 0.002090 / 0.00868 / 0.00307 / 0 |
| back60 | shadow-bilinear-3x3 | character | 0.006321 / 0.03384 / 0.00451 / 742 / 51.77 | 0.008142 / 0.04369 / 0.00434 / 1065 / 63.18 | 0.002547 / 0.01342 / 0.00436 / 0 |
| back60 | shadow-bilinear-3x3 | ground | 0.004766 / 0.01906 / 0.00300 / 28 / 9.87 | 0.005683 / 0.02326 / 0.00299 / 423 / 19.25 | 0.001611 / 0.00729 / 0.00298 / 0 |
| back60 | vis-release-0.2 | character | 0.007269 / 0.03945 / 0.00477 / 782 / 40.94 | 0.008327 / 0.04589 / 0.00486 / 1011 / 42.79 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | vis-release-0.2 | ground | 0.005648 / 0.02151 / 0.00324 / 76 / 10.10 | 0.006086 / 0.02411 / 0.00321 / 524 / 17.62 | 0.002090 / 0.00868 / 0.00307 / 0 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 / 40.72 | 0.003024 / 0.01176 / 0.00118 / 340 / 48.15 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 / 9.89 | 0.005695 / 0.02242 / 0.00378 / 1305 / 18.78 | 0.002343 / 0.00784 / 0.00334 / 0 |
| ground15 | shadow-bilinear-3x3 | character | 0.002953 / 0.01148 / 0.00116 / 371 / 40.65 | 0.003027 / 0.01178 / 0.00118 / 341 / 48.09 | 0.002281 / 0.01260 / 0.00135 / 0 |
| ground15 | shadow-bilinear-3x3 | ground | 0.004951 / 0.01792 / 0.00367 / 396 / 8.25 | 0.005405 / 0.02158 / 0.00373 / 948 / 17.27 | 0.002293 / 0.00758 / 0.00333 / 0 |
| ground15 | vis-release-0.2 | character | 0.002921 / 0.01120 / 0.00117 / 360 / 34.45 | 0.002989 / 0.01176 / 0.00119 / 319 / 39.73 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | vis-release-0.2 | ground | 0.004627 / 0.01854 / 0.00383 / 271 / 4.77 | 0.004761 / 0.02038 / 0.00383 / 728 / 9.82 | 0.002343 / 0.00784 / 0.00334 / 0 |

### vis-release-0.3

| Condition | Arm | Region | Native | Quality | Own FXAA |
| --- | --- | --- | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 / 65.33 | 0.014156 / 0.07334 / 0.01757 / 1733 / 72.68 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 / 14.16 | 0.007330 / 0.02830 / 0.01235 / 1881 / 24.96 | 0.002130 / 0.00869 / 0.01311 / 0 |
| back15 | shadow-bilinear-3x3 | character | 0.008027 / 0.03893 / 0.01764 / 851 / 55.19 | 0.011053 / 0.05435 / 0.01751 / 1166 / 65.92 | 0.002607 / 0.01540 / 0.01840 / 0 |
| back15 | shadow-bilinear-3x3 | ground | 0.005312 / 0.02074 / 0.01236 / 108 / 9.70 | 0.006370 / 0.02463 / 0.01226 / 1034 / 19.11 | 0.001655 / 0.00756 / 0.01310 / 0 |
| back15 | vis-release-0.3 | character | 0.008482 / 0.04617 / 0.01857 / 822 / 42.66 | 0.010478 / 0.05489 / 0.01806 / 1135 / 45.54 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | vis-release-0.3 | ground | 0.006421 / 0.02469 / 0.01251 / 181 / 8.56 | 0.006832 / 0.02743 / 0.01243 / 1156 / 16.33 | 0.002130 / 0.00869 / 0.01311 / 0 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 / 66.05 | 0.009148 / 0.05207 / 0.00366 / 1117 / 72.29 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 / 14.85 | 0.006355 / 0.02524 / 0.00238 / 906 / 26.58 | 0.002071 / 0.00868 / 0.00202 / 0 |
| day30 | shadow-bilinear-3x3 | character | 0.005354 / 0.02882 / 0.00341 / 506 / 54.73 | 0.007044 / 0.03833 / 0.00318 / 784 / 64.07 | 0.002500 / 0.01430 / 0.00369 / 0 |
| day30 | shadow-bilinear-3x3 | ground | 0.004652 / 0.01849 / 0.00215 / 16 / 10.01 | 0.005288 / 0.02098 / 0.00209 / 275 / 19.19 | 0.001602 / 0.00729 / 0.00200 / 0 |
| day30 | vis-release-0.3 | character | 0.007555 / 0.04332 / 0.00370 / 873 / 55.96 | 0.007955 / 0.04369 / 0.00359 / 817 / 58.36 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | vis-release-0.3 | ground | 0.005726 / 0.02216 / 0.00231 / 158 / 12.89 | 0.005981 / 0.02410 / 0.00234 / 504 / 22.86 | 0.002071 / 0.00868 / 0.00202 / 0 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 / 62.54 | 0.010472 / 0.05966 / 0.00500 / 1535 / 70.67 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 / 15.25 | 0.006729 / 0.02718 / 0.00327 / 1303 / 27.14 | 0.002090 / 0.00868 / 0.00307 / 0 |
| back60 | shadow-bilinear-3x3 | character | 0.006321 / 0.03384 / 0.00451 / 742 / 51.77 | 0.008142 / 0.04369 / 0.00434 / 1065 / 63.18 | 0.002547 / 0.01342 / 0.00436 / 0 |
| back60 | shadow-bilinear-3x3 | ground | 0.004766 / 0.01906 / 0.00300 / 28 / 9.87 | 0.005683 / 0.02326 / 0.00299 / 423 / 19.25 | 0.001611 / 0.00729 / 0.00298 / 0 |
| back60 | vis-release-0.3 | character | 0.008926 / 0.05093 / 0.00478 / 1263 / 54.20 | 0.009726 / 0.05512 / 0.00493 / 1370 / 58.21 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | vis-release-0.3 | ground | 0.005900 / 0.02299 / 0.00326 / 224 / 13.24 | 0.006448 / 0.02597 / 0.00325 / 975 / 23.09 | 0.002090 / 0.00868 / 0.00307 / 0 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 / 40.72 | 0.003024 / 0.01176 / 0.00118 / 340 / 48.15 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 / 9.89 | 0.005695 / 0.02242 / 0.00378 / 1305 / 18.78 | 0.002343 / 0.00784 / 0.00334 / 0 |
| ground15 | shadow-bilinear-3x3 | character | 0.002953 / 0.01148 / 0.00116 / 371 / 40.65 | 0.003027 / 0.01178 / 0.00118 / 341 / 48.09 | 0.002281 / 0.01260 / 0.00135 / 0 |
| ground15 | shadow-bilinear-3x3 | ground | 0.004951 / 0.01792 / 0.00367 / 396 / 8.25 | 0.005405 / 0.02158 / 0.00373 / 948 / 17.27 | 0.002293 / 0.00758 / 0.00333 / 0 |
| ground15 | vis-release-0.3 | character | 0.002924 / 0.01120 / 0.00117 / 361 / 34.83 | 0.002989 / 0.01176 / 0.00119 / 319 / 40.56 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | vis-release-0.3 | ground | 0.004655 / 0.01878 / 0.00383 / 305 / 5.45 | 0.004824 / 0.02073 / 0.00382 / 776 / 11.48 | 0.002343 / 0.00784 / 0.00334 / 0 |

### vis-reset-0.2

| Condition | Arm | Region | Native | Quality | Own FXAA |
| --- | --- | --- | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 / 65.33 | 0.014156 / 0.07334 / 0.01757 / 1733 / 72.68 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 / 14.16 | 0.007330 / 0.02830 / 0.01235 / 1881 / 24.96 | 0.002130 / 0.00869 / 0.01311 / 0 |
| back15 | shadow-bilinear-3x3 | character | 0.008027 / 0.03893 / 0.01764 / 851 / 55.19 | 0.011053 / 0.05435 / 0.01751 / 1166 / 65.92 | 0.002607 / 0.01540 / 0.01840 / 0 |
| back15 | shadow-bilinear-3x3 | ground | 0.005312 / 0.02074 / 0.01236 / 108 / 9.70 | 0.006370 / 0.02463 / 0.01226 / 1034 / 19.11 | 0.001655 / 0.00756 / 0.01310 / 0 |
| back15 | vis-reset-0.2 | character | 0.006435 / 0.03528 / 0.02081 / 450 / 36.60 | 0.006427 / 0.03386 / 0.02213 / 331 / 37.76 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | vis-reset-0.2 | ground | 0.006545 / 0.02443 / 0.01350 / 73 / 7.22 | 0.005371 / 0.02016 / 0.01399 / 29 / 13.38 | 0.002130 / 0.00869 / 0.01311 / 0 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 / 66.05 | 0.009148 / 0.05207 / 0.00366 / 1117 / 72.29 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 / 14.85 | 0.006355 / 0.02524 / 0.00238 / 906 / 26.58 | 0.002071 / 0.00868 / 0.00202 / 0 |
| day30 | shadow-bilinear-3x3 | character | 0.005354 / 0.02882 / 0.00341 / 506 / 54.73 | 0.007044 / 0.03833 / 0.00318 / 784 / 64.07 | 0.002500 / 0.01430 / 0.00369 / 0 |
| day30 | shadow-bilinear-3x3 | ground | 0.004652 / 0.01849 / 0.00215 / 16 / 10.01 | 0.005288 / 0.02098 / 0.00209 / 275 / 19.19 | 0.001602 / 0.00729 / 0.00200 / 0 |
| day30 | vis-reset-0.2 | character | 0.006429 / 0.03529 / 0.00688 / 576 / 45.20 | 0.006326 / 0.03274 / 0.00920 / 426 / 44.51 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | vis-reset-0.2 | ground | 0.006129 / 0.02356 / 0.00361 / 36 / 10.20 | 0.005119 / 0.01962 / 0.00468 / 11 / 17.71 | 0.002071 / 0.00868 / 0.00202 / 0 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 / 62.54 | 0.010472 / 0.05966 / 0.00500 / 1535 / 70.67 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 / 15.25 | 0.006729 / 0.02718 / 0.00327 / 1303 / 27.14 | 0.002090 / 0.00868 / 0.00307 / 0 |
| back60 | shadow-bilinear-3x3 | character | 0.006321 / 0.03384 / 0.00451 / 742 / 51.77 | 0.008142 / 0.04369 / 0.00434 / 1065 / 63.18 | 0.002547 / 0.01342 / 0.00436 / 0 |
| back60 | shadow-bilinear-3x3 | ground | 0.004766 / 0.01906 / 0.00300 / 28 / 9.87 | 0.005683 / 0.02326 / 0.00299 / 423 / 19.25 | 0.001611 / 0.00729 / 0.00298 / 0 |
| back60 | vis-reset-0.2 | character | 0.006614 / 0.03546 / 0.00847 / 605 / 40.94 | 0.006721 / 0.03418 / 0.01044 / 532 / 42.79 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | vis-reset-0.2 | ground | 0.006107 / 0.02350 / 0.00456 / 40 / 10.10 | 0.005109 / 0.01936 / 0.00535 / 8 / 17.62 | 0.002090 / 0.00868 / 0.00307 / 0 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 / 40.72 | 0.003024 / 0.01176 / 0.00118 / 340 / 48.15 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 / 9.89 | 0.005695 / 0.02242 / 0.00378 / 1305 / 18.78 | 0.002343 / 0.00784 / 0.00334 / 0 |
| ground15 | shadow-bilinear-3x3 | character | 0.002953 / 0.01148 / 0.00116 / 371 / 40.65 | 0.003027 / 0.01178 / 0.00118 / 341 / 48.09 | 0.002281 / 0.01260 / 0.00135 / 0 |
| ground15 | shadow-bilinear-3x3 | ground | 0.004951 / 0.01792 / 0.00367 / 396 / 8.25 | 0.005405 / 0.02158 / 0.00373 / 948 / 17.27 | 0.002293 / 0.00758 / 0.00333 / 0 |
| ground15 | vis-reset-0.2 | character | 0.003195 / 0.01120 / 0.00288 / 395 / 34.45 | 0.003617 / 0.01315 / 0.00389 / 432 / 39.73 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | vis-reset-0.2 | ground | 0.005664 / 0.01963 / 0.01190 / 204 / 4.77 | 0.005007 / 0.01851 / 0.01565 / 294 / 9.82 | 0.002343 / 0.00784 / 0.00334 / 0 |

### shadow-bilinear-3x3+vis-release-0.1

| Condition | Arm | Region | Native | Quality | Own FXAA |
| --- | --- | --- | ---: | ---: | ---: |
| back15 | baseline | character | 0.014287 / 0.07499 / 0.01795 / 1742 / 65.33 | 0.014156 / 0.07334 / 0.01757 / 1733 / 72.68 | 0.002961 / 0.01649 / 0.01911 / 0 |
| back15 | baseline | ground | 0.006708 / 0.02635 / 0.01249 / 485 / 14.16 | 0.007330 / 0.02830 / 0.01235 / 1881 / 24.96 | 0.002130 / 0.00869 / 0.01311 / 0 |
| back15 | shadow-bilinear-3x3 | character | 0.008027 / 0.03893 / 0.01764 / 851 / 55.19 | 0.011053 / 0.05435 / 0.01751 / 1166 / 65.92 | 0.002607 / 0.01540 / 0.01840 / 0 |
| back15 | shadow-bilinear-3x3 | ground | 0.005312 / 0.02074 / 0.01236 / 108 / 9.70 | 0.006370 / 0.02463 / 0.01226 / 1034 / 19.11 | 0.001655 / 0.00756 / 0.01310 / 0 |
| back15 | shadow-bilinear-3x3+vis-release-0.1 | character | 0.005048 / 0.02687 / 0.01754 / 313 / 29.75 | 0.007642 / 0.03975 / 0.01731 / 602 / 30.93 | 0.002607 / 0.01540 / 0.01840 / 0 |
| back15 | shadow-bilinear-3x3+vis-release-0.1 | ground | 0.005175 / 0.02017 / 0.01236 / 26 / 6.37 | 0.006096 / 0.02381 / 0.01229 / 735 / 11.24 | 0.001655 / 0.00756 / 0.01310 / 0 |
| day30 | baseline | character | 0.008413 / 0.04997 / 0.00371 / 1097 / 66.05 | 0.009148 / 0.05207 / 0.00366 / 1117 / 72.29 | 0.002976 / 0.01624 / 0.00402 / 0 |
| day30 | baseline | ground | 0.005930 / 0.02326 / 0.00234 / 310 / 14.85 | 0.006355 / 0.02524 / 0.00238 / 906 / 26.58 | 0.002071 / 0.00868 / 0.00202 / 0 |
| day30 | shadow-bilinear-3x3 | character | 0.005354 / 0.02882 / 0.00341 / 506 / 54.73 | 0.007044 / 0.03833 / 0.00318 / 784 / 64.07 | 0.002500 / 0.01430 / 0.00369 / 0 |
| day30 | shadow-bilinear-3x3 | ground | 0.004652 / 0.01849 / 0.00215 / 16 / 10.01 | 0.005288 / 0.02098 / 0.00209 / 275 / 19.19 | 0.001602 / 0.00729 / 0.00200 / 0 |
| day30 | shadow-bilinear-3x3+vis-release-0.1 | character | 0.004521 / 0.02380 / 0.00339 / 358 / 31.05 | 0.005447 / 0.02890 / 0.00314 / 363 / 33.32 | 0.002500 / 0.01430 / 0.00369 / 0 |
| day30 | shadow-bilinear-3x3+vis-release-0.1 | ground | 0.004516 / 0.01764 / 0.00214 / 0 / 8.67 | 0.005031 / 0.01962 / 0.00208 / 138 / 15.25 | 0.001602 / 0.00729 / 0.00200 / 0 |
| back60 | baseline | character | 0.009675 / 0.05484 / 0.00488 / 1355 / 62.54 | 0.010472 / 0.05966 / 0.00500 / 1535 / 70.67 | 0.002955 / 0.01595 / 0.00470 / 0 |
| back60 | baseline | ground | 0.006055 / 0.02353 / 0.00328 / 379 / 15.25 | 0.006729 / 0.02718 / 0.00327 / 1303 / 27.14 | 0.002090 / 0.00868 / 0.00307 / 0 |
| back60 | shadow-bilinear-3x3 | character | 0.006321 / 0.03384 / 0.00451 / 742 / 51.77 | 0.008142 / 0.04369 / 0.00434 / 1065 / 63.18 | 0.002547 / 0.01342 / 0.00436 / 0 |
| back60 | shadow-bilinear-3x3 | ground | 0.004766 / 0.01906 / 0.00300 / 28 / 9.87 | 0.005683 / 0.02326 / 0.00299 / 423 / 19.25 | 0.001611 / 0.00729 / 0.00298 / 0 |
| back60 | shadow-bilinear-3x3+vis-release-0.1 | character | 0.005019 / 0.02546 / 0.00448 / 447 / 30.79 | 0.006535 / 0.03556 / 0.00425 / 662 / 33.11 | 0.002547 / 0.01342 / 0.00436 / 0 |
| back60 | shadow-bilinear-3x3+vis-release-0.1 | ground | 0.004633 / 0.01824 / 0.00300 / 0 / 8.32 | 0.005370 / 0.02101 / 0.00298 / 256 / 14.65 | 0.001611 / 0.00729 / 0.00298 / 0 |
| ground15 | baseline | character | 0.002957 / 0.01148 / 0.00116 / 371 / 40.72 | 0.003024 / 0.01176 / 0.00118 / 340 / 48.15 | 0.002280 / 0.01260 / 0.00135 / 0 |
| ground15 | baseline | ground | 0.005274 / 0.01937 / 0.00373 / 512 / 9.89 | 0.005695 / 0.02242 / 0.00378 / 1305 / 18.78 | 0.002343 / 0.00784 / 0.00334 / 0 |
| ground15 | shadow-bilinear-3x3 | character | 0.002953 / 0.01148 / 0.00116 / 371 / 40.65 | 0.003027 / 0.01178 / 0.00118 / 341 / 48.09 | 0.002281 / 0.01260 / 0.00135 / 0 |
| ground15 | shadow-bilinear-3x3 | ground | 0.004951 / 0.01792 / 0.00367 / 396 / 8.25 | 0.005405 / 0.02158 / 0.00373 / 948 / 17.27 | 0.002293 / 0.00758 / 0.00333 / 0 |
| ground15 | shadow-bilinear-3x3+vis-release-0.1 | character | 0.002921 / 0.01120 / 0.00117 / 360 / 34.29 | 0.002986 / 0.01176 / 0.00119 / 320 / 39.21 | 0.002281 / 0.01260 / 0.00135 / 0 |
| ground15 | shadow-bilinear-3x3+vis-release-0.1 | ground | 0.004340 / 0.01712 / 0.00378 / 201 / 4.46 | 0.004593 / 0.01962 / 0.00379 / 618 / 9.22 | 0.002293 / 0.00758 / 0.00333 / 0 |

### Dot and release coincidence

The release set is magenta, drawn after the post chain. Outside magenta there are zero differing crop pixels
against the corresponding plain arm in all six back15 comparisons. No overlay colour enters history or the
grain measurements. For the combination, source dots below mean the bilinear-only control's dots. The other
arms use point-PCF baseline dots. Counts use each arm's own character band.

| Arm | Mode | Release share of band | Source dots released now | Source dots within 1 px of release | Source dots touched in current or previous 3 screen frames | Surviving arm dots released now |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| vis-release-0.1 | taa-native | 31.04% | 55.05% | 60.79% | 69.92% | 60.26% |
| vis-release-0.1 | taa-quality | 37.64% | 52.74% | 58.86% | 67.05% | 59.39% |
| vis-reset-0.2 | taa-native | 22.91% | 46.38% | 55.22% | 64.70% | 35.11% |
| vis-reset-0.2 | taa-quality | 27.92% | 44.66% | 50.78% | 62.26% | 42.90% |
| shadow-bilinear-3x3+vis-release-0.1 | taa-native | 29.92% | 34.08% | 39.37% | 47.47% | 34.82% |
| shadow-bilinear-3x3+vis-release-0.1 | taa-quality | 36.87% | 46.23% | 49.49% | 56.69% | 52.33% |

The four-frame figure is a same-screen-position association over the saved frames, not a reprojected surface
trace. It has no release masks from the warm-up frames before the first saved frame. The same-frame and 1 px
counts do not need that approximation. The set has much stronger coincidence with source dots than the earlier
colour-release overlay, but it does not cover every dot. Some surviving dots are released pixels themselves,
so clearing the lock is not sufficient to eliminate the residual. Full counts are in
[release-coincidence.json](runs/visibility/release-coincidence.json).

### Final binary and entry proof

The final engine library, private feed, vendored library and SnapshotTool-loaded library match SHA-256
`f5c53b878f934515ebac8d2a57ab9264c6d21d7a7f63b15ee343bb660744a5a8`, from renderer source `a90f4e4e4`.
See [binary identity](runs/visibility/final-binary-identity.json). No shipping pin or shared feed changed.
Final-package unset reproduces all 192 original full frames at Native, Quality and FXAA across all four
conditions, maximum channel difference zero, with every metrics file byte-identical. Final release 0.1 also
reproduces its 192 prior full frames and metrics exactly. The final cyan overlay reproduces its earlier 48
full frames exactly. That validates reuse of the main measurement set after the synthetic fallback, CRLF and
transparent-output plumbing corrections. The higher threshold arms compile the same visibility program and
change only the uniform flags. See [the final frame proof](runs/visibility/final-package-image-identity.txt),
[the earlier 576-frame check](runs/visibility/corrected-capture-identity.txt), and
[the final cyan check](runs/visibility/lock-overlay-final-identity.txt).

The forced fused and forced split real-scene captures at back15 with release 0.1 are pixel-identical in all
16 frames at each of Native, Quality and FXAA. See [the colour proof](runs/visibility/entry-colour-identity.txt).
This colour result does not waive the inherited synthetic stored-state identity failures documented above.

### Visibility crops and reading

Each 4x sheet is Native, Quality and that arm's own FXAA, left to right. Individual per-mode 4x crops are next
to each sheet. The contact sheets retain one display pixel per image pixel.

| Arm | Character crop, 4x | Normal-scale sheet |
| --- | --- | --- |
| baseline | [crop](runs/visibility/visuals/baseline-character-015-4x.png) | [sheet](runs/visibility/visuals/baseline-contact-sheet-015.png) |
| shadow-bilinear-3x3 | [crop](runs/visibility/visuals/shadow-bilinear-3x3-character-015-4x.png) | [sheet](runs/visibility/visuals/shadow-bilinear-3x3-contact-sheet-015.png) |
| vis-release-0.1 | [crop](runs/visibility/visuals/vis-release-0.1-character-015-4x.png) | [sheet](runs/visibility/visuals/vis-release-0.1-contact-sheet-015.png) |
| vis-release-0.2 | [crop](runs/visibility/visuals/vis-release-0.2-character-015-4x.png) | [sheet](runs/visibility/visuals/vis-release-0.2-contact-sheet-015.png) |
| vis-release-0.3 | [crop](runs/visibility/visuals/vis-release-0.3-character-015-4x.png) | [sheet](runs/visibility/visuals/vis-release-0.3-contact-sheet-015.png) |
| vis-reset-0.2 | [crop](runs/visibility/visuals/vis-reset-0.2-character-015-4x.png) | [sheet](runs/visibility/visuals/vis-reset-0.2-contact-sheet-015.png) |
| shadow-bilinear-3x3+vis-release-0.1 | [crop](runs/visibility/visuals/shadow-bilinear-3x3+vis-release-0.1-character-015-4x.png) | [sheet](runs/visibility/visuals/shadow-bilinear-3x3+vis-release-0.1-contact-sheet-015.png) |

The renderer signal removes most of the observed excess dots in the best combination, but not all of them,
and the complete guard suite does not accept it without touching detail. Back15 combination dots fall from
1742/1733 to 313/602, Native/Quality, reductions of 82.0% and 65.3%. Grain falls 64.7% and 46.0%, while change
mean stays close to own FXAA. Locked band share falls from 65.3%/72.7% to 29.7%/30.9%.

The next proper prototype should keep release-only visibility evidence and the bilinear 3x3 filter. It should
first fix correspondence and confidence of the visibility comparison, then meet the camera-pan and moving-rim
bounds before any shipping proposal. The current maximum over a raw reprojected 3x3 is not a reliable test of
physical lighting change. The reset variant adds too much slow-condition temporal change and has more guard
failures. A production implementation also needs a cheaper signal representation and comparison placement,
with measured performance after separate timing approval. This archive does not supply a frame-time result.
None of these branches is a merge candidate.

### Reproduce the visibility evidence

The capture helpers pass the required start time themselves. Run the commands serially, with private feed and
probe paths supplied through the same environment variables as the earlier reproduction section.

~~~bash
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_shadow_filters.py \
  "$GAME_PROBE" "$CAPTURE_ROOT" baseline shadow-bilinear-3x3 \
  vis-release-0.1 vis-release-0.2 vis-release-0.3 vis-reset-0.2 \
  shadow-bilinear-3x3,vis-release-0.1 show-vis-release
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_shadow_filters.py \
  "$GAME_PROBE" "$CAPTURE_ROOT" show-locked shadow-bilinear-3x3,show-locked \
  vis-release-0.1,show-locked vis-release-0.2,show-locked vis-release-0.3,show-locked \
  vis-reset-0.2,show-locked shadow-bilinear-3x3,vis-release-0.1,show-locked
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/capture_visibility_tail.py \
  "$GAME_PROBE" "$CAPTURE_ROOT"
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/shadow_filter_report.py \
  "$CAPTURE_ROOT" KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/runs/visibility \
  shadow-bilinear-3x3 vis-release-0.1 vis-release-0.2 vis-release-0.3 vis-reset-0.2 \
  shadow-bilinear-3x3+vis-release-0.1 show-vis-release
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/visibility_report.py \
  "$CAPTURE_ROOT" KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/runs/visibility/release-coincidence.json \
  --back15-only vis-release-0.1 vis-reset-0.2 shadow-bilinear-3x3+vis-release-0.1
~~~

`visibility_tables.py` regenerates the comparison tables from `summary.json`. `collect_visibility_guards.py`
extracts every local result and bound from TRX and detailed logs. `hosted_visibility_report.py` extracts every
hosted failure from the workflow's detailed measurement artifacts. Both collectors remove machine filesystem
prefixes before writing public evidence. They never change test outcomes or retry tests.

### Broader verification and archive limits

Both complete solutions build Release with zero warnings. The game format check passes. The engine-wide format
check reports 120 whitespace diagnostics in nine untouched files. The changed C# files pass their scoped format
check. Whole-tree dash, prose, file-size, contributor-instruction and version-document checks pass, with exit
codes recorded in [verification](runs/visibility/verification/repo-checks.json).

The one broad engine headless run covers 41 assemblies and reports 35132 passed, 1425 skipped and two raw-shader
assertion failures. The new `Params.x` diagnostic use made one old reserved-lane text assertion stale. It now
checks the non-visibility program and passes. The subsequent Render3D/source-validation/motion-contract slice
reports 3859 passed, 50 GPU-gated skips and only the original Lanczos source-count assertion, expected 5 calls
and actual 9. Its old diagnostic alternatives already contain those nine calls. No unrelated source was changed
and the broad suite was not repeated. The full game suite and navigation rebakes are omitted under the probe
brief. See [the aggregate](runs/visibility/verification/headless-suite-summary.json) and
[the final scoped run](runs/visibility/verification/headless-render-slice.txt).

### Hosted run provenance

The first matrix is [39131215312](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39131215312), source
`e62236e56`, token `vis-release-0.1` with point PCF. The test-scoped stamp confirms the token, mask 16777216 and
MRT enabled on each downloaded backend. Metal reports 1350 passed and 10 failed. Three seeded-lock failures
come from the fully lit synthetic-history initialization corrected in `dab3a8981`. Four are the inherited
entry-identity class. Two are the measured shadow guards. The tenth is an invalid no-GC allocation measurement,
with zero measured bytes and a runtime refusal, not evidence of an allocation regression.

The first Windows leg aborts after 893 passes and 312 failures. The dominant failure is a CRLF-sensitive shader
splice before visibility can be measured. Resource exhaustion follows the constructor failures and the test
host crashes, so total coverage is unknown. The line-ending regression fails under CRLF, then passes after
normalization in `83f63d024`. These first-run Windows results do not evaluate the visibility hypothesis.
Every completed failing test and its full error is retained in the [first-run result inventory](runs/visibility/hosted/39131215312/results.json).

The owner authorized one corrected matrix after the first run fully finishes. The corrected selection is
`shadow-bilinear-3x3,vis-release-0.1`, the best measured overall arm, with mask 20971520. Its test-only selection
and exact dispatch-environment proof are in `b4b03ece4` and
[the stamp proof](runs/visibility/verification/host-token-proof.txt). The corrected source includes the synthetic
fallback, CRLF and explicit transparent-output fixes. The two runs must be read separately. No third matrix is
authorized after an infrastructure failure in the corrected run.


First-run Vulkan completes all 1360 tests, 1355 passed and five failed. Three are the seeded-lock initialization
bug fixed before the corrected run. Two are moving-shade lag and moving-rim acceptance failures. The software
Vulkan entry-identity facts use their existing bounded policy and pass. The Vulkan synchronization-validation
job passes. The first run is finally marked cancelled because the Windows runner remains stuck in cleanup after
its aborted tests. All three test artifacts were collected first, then normal and forced cancellation were used
to terminate only that orphaned cleanup. The first run and every job were confirmed completed before dispatching
the corrected matrix. This cancellation did not discard any running test work.

The corrected matrix is [39139863925](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39139863925), source
`50c8c6f3657e23df28fe33fffea845691169f0e1`, token `shadow-bilinear-3x3,vis-release-0.1`.
It is the one additional matrix the owner approved. Its results are recorded separately from the first point-PCF
run, because both the source and selected filter differ.
