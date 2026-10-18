# Real-scene shadow grain attribution

Which part of the temporal resolve puts white dots on the moving sun shadow edge across the Grimhollow
character under TAA. This is attribution, not a fix. The branch `fix/taa-grain-switches` is a diagnostic
archive and is never merged.

## Builds

- Engine: this branch, version `21.5.0-grain.1`, packed into a private feed. The switches are in
  `KhaozEngine.Render3D/Internal/TemporalResolveDiagnostics.cs` and under `#ifdef TemporalDiagnostics` in
  `ShaderSources.TemporalAccumulate.cs` and `ShaderSources.TemporalDebug.cs`.
- Game: Grimhollow `probe/taa-grain-engine` at `73201a64`, pinned to `21.5.0-grain.1`. Its `shadowgrain`
  verb takes `--temporal-diag <tokens>` and names the mask the engine parsed in `metrics.txt`.
- Metal, Apple Silicon, 2560x1440, the `back-edge` spot (100.9, 89.5, yaw 230) unless named otherwise.

## The switches

`KE_TEMPORAL_DIAG` holds comma-separated tokens. The mask is read once and reaches both entry points through
`Params.w` of the resolve block. The switches' GLSL compiles only while the mask is set.

| Token | Effect |
| --- | --- |
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

## GPU guards with the token set

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

## Reading

The working hypothesis does not hold. Released pixels are not the dots. The dominant source is the thin-feature
lock. A texel that stands out from both neighbours, a texture highlight on the skin or a lit texel the jittered
coarse shadow edge leaves between shadowed ones, reads as a thin feature and takes a whole lock. The hold then
keeps that pixel's lit history luma out of the clip for about four frames and lets it go over four more, while
its unlocked neighbours clip to the new shadow. The release fires beside these pixels but almost never on them
(1.2% and 0.3% above), and the sharpen lifts a lone bright pixel against a dark ring further. That the dots are
held locks is inferred from `locks-off` and the coincidence figures. No lock overlay was captured.

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
