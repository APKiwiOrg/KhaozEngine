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
KHAOZENGINE_FEED=~/.cache/khaozengine-probe-feed/taa-grain build-slot --heavy --label pack -- scripts/pack-local-feed.sh
KE_TEMPORAL_DIAG=locks-off KE_GPU_TESTS=1 build-slot --heavy --label guards -- dotnet test \
  KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~TemporalShadowEdgeLagGpuTests|FullyQualifiedName~TemporalMovingShadeLagGpuTests|FullyQualifiedName~TemporalStableDetailGpuTests|FullyQualifiedName~TemporalStablePeakGpuTests|FullyQualifiedName~TemporalGameShadowGrainGpuTests" \
  --logger "console;verbosity=detailed"
```

Game, from the `probe/taa-grain-engine` worktree:

```bash
KHAOZENGINE_FEED=~/.cache/khaozengine-probe-feed/taa-grain scripts/refresh-engine.sh
build-slot --label build -- dotnet build tools/SnapshotTool/SnapshotTool.csproj -c Release
# baseline, then each token
build-slot --heavy --label shadowgrain -- dotnet run --project tools/SnapshotTool -c Release --no-build -- \
  shadowgrain /tmp/grimhollow-shadowgrain/engine-diag/baseline --spot back-edge --frame-time 1/15 \
  --start-time 0.49777777777777776
build-slot --heavy --label shadowgrain -- dotnet run --project tools/SnapshotTool -c Release --no-build -- \
  shadowgrain /tmp/grimhollow-shadowgrain/engine-diag/locks-off --spot back-edge --frame-time 1/15 \
  --start-time 0.49777777777777776 --temporal-diag locks-off
# other conditions add --frame-time 1/60, --day-minutes 30 or --spot ground-edge
```

Analysis, with numpy and Pillow, over the capture root:

```bash
python3 scripts/pixdiff.py <reference-run> <probe-run>
python3 scripts/coincide.py /tmp/grimhollow-shadowgrain/engine-diag
python3 scripts/sparkcount.py /tmp/grimhollow-shadowgrain/engine-diag baseline noop locks-off
python3 scripts/hold.py /tmp/grimhollow-shadowgrain/engine-diag/baseline
python3 scripts/mdtable.py /tmp/grimhollow-shadowgrain/engine-diag baseline locks-off
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
left to right, as the other crops do. The full capture is under `/tmp/grimhollow-shadowgrain/engine-diag/show-locked`.

```bash
build-slot --heavy --label grain-show-locked -- dotnet run --project tools/SnapshotTool -c Release --no-build -- \
  shadowgrain /tmp/grimhollow-shadowgrain/engine-diag/show-locked --spot back-edge --frame-time 1/15 \
  --start-time 0.49777777777777776 --temporal-diag show-locked
python3 scripts/coincide.py /tmp/grimhollow-shadowgrain/engine-diag show-locked
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
  /Users/antonio/Grimhollow/.worktrees/taa-grain-probe /tmp/grimhollow-shadowgrain/lock-rule-round
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/lock_rule_tables.py \
  /tmp/grimhollow-shadowgrain/lock-rule-round
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-game-shadow/real-scene/scripts/coincide.py \
  /tmp/grimhollow-shadowgrain/lock-rule-round back15-show-locked back15-noop back15-baseline
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
