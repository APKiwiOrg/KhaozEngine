# Real-scene shadow visibility evidence

Status: Task 10 accepted. The owner accepted D on 2026-10-19 after real-scene clips. D ships lock release only, without a large-change weight response. The [reset-tuning history](reset-tuning.md) and [owner-accepted release-only costs](owner-release-only.md) preserve the decision and its measured costs. This acceptance does not authorize a release.

Historical response-enabled concern, superseded by D below: ground15 ground change mean rises from 0.003674704 to 0.006519368 at Native and from 0.003728160 to 0.007835931 at Quality. Ground grain rises at both presets. Quality ground dots rise from 948 to 987. The back15 improvement alone does not settle this stability trade-off.

MetalNative on Apple M2 Max, 2560x1440. Every condition uses 80 warm-up frames, 16 measured frames and start time `0.49777777777777776`. The idle player, world, camera, sharpness, light hold and shadow detail use the probe defaults. There is no diagnostic renderer build or engine instrumentation.

## Source and package identities

| Build | Engine source | Private package version | Grimhollow probe commit | Loaded renderer SHA256 |
| --- | --- | --- | --- | --- |
| baseline | `a1e82663567bda9301ad4eaceb7ebad40ee19f17` | `21.5.1` | `98030b022dc2c23bcf4bdd9338cfea0f62c08d17` | `f34f4b5d98b13287ec728f500eec2b49d01440ada1f990830598a6382c0b8470` |
| control | `1472e9609` | `21.6.0-svctrl.1` | `7ca973058ac538d0086982b69420023413a513ac` | `08aa3c0c959610f7c305709dff22a2be34a445e7594cb58d349d73e4433c5ae4` |
| final | `a8aa66e37117c5fa7126def8739f87551d935827` | `21.6.0-svfinal.1` | `c2d03e24d341f53c3d29a3cdc49ae7b34175525f` | `7faf6dc3d41e12cb3727552dbe181f04c3f9ca4c0e2ffcebd9c93cb00b91a633` |

The baseline reproduces the archived back15 character dot totals exactly, 1742 Native and 1733 Quality. The control also reproduces the archived bilinear dot totals, 851 Native and 1166 Quality. The baseline is the probe's vendored 21.5.1 point PCF. Its resolve sources match v21.4.0 byte for byte. The control has bilinear PCF and the unchanged v21.4.0 resolve. The final build adds visibility release and the rising-only large-change reset. The manifest records each source tree, relevant source hashes, all 222 package hashes per build, package repository commits and captured loaded-renderer hashes. The baseline needed no pack. Private scratch trees changed only the version property and were removed after packing. The shared feed was never an output.

The probe starts at Grimhollow `88ac70e7d8bb748a668490f45be8437261cbcf9b`. Probe-only analysis code saves compressed region bits and the loaded renderer identity after capture. It never changes renderer state or image bytes. The probe branch remains a diagnostic archive and must never merge.

## Headline, back15 character

| Build | Mode | Grain mean | Dots | Change mean |
| --- | --- | ---: | ---: | ---: |
| baseline | taa-native | 0.014286626 | 1742 | 0.017948762 |
| baseline | taa-quality | 0.014156210 | 1733 | 0.017574480 |
| control | taa-native | 0.008027197 | 851 | 0.017636894 |
| control | taa-quality | 0.011052578 | 1166 | 0.017510300 |
| final | taa-native | 0.005471380 | 364 | 0.017501558 |
| final | taa-quality | 0.008700161 | 760 | 0.017329929 |

taa-native: final versus control grain changes by -0.002555816, dots by -487, change mean by -0.000135336. Required back15 grain and dots improvement is met.

taa-quality: final versus control grain changes by -0.002352417, dots by -406, change mean by -0.000180371. Required back15 grain and dots improvement is met.

## FXAA and mask identity between control and final

back15: 0 differing FXAA pixel-frames, region masks identical.

day30: 0 differing FXAA pixel-frames, region masks identical.

back60: 0 differing FXAA pixel-frames, region masks identical.

ground15: 0 differing FXAA pixel-frames, region masks identical.

## Definitions and replay

The preserved [archive report helper](scripts/archive_shadow_filter_report.py) is the read-only method reference. [analyze_captures.py](scripts/analyze_captures.py) retains its luma, median and own-FXAA dot definitions. Grain is absolute Rec.709 luma residual from the local 3x3 median, pooled over pixel-frames. The p95 is nearest-rank over all samples. Change mean uses consecutive-frame region intersections. A dot exceeds both its local median and its own build's FXAA by strictly more than 0.05. FXAA dots are therefore zero by definition. Each build uses its own FXAA-derived masks.

The probe exports the original region masks as gzip-compressed bit fields. Lossless XZ archives retain every normal contact-sheet RGB pixel, in frame order and Native, Quality, FXAA column order. The report helper rebuilds all 72 rows from those pixels and masks, then requires grain, p95 and change to match capture metrics within 1e-12. Output-location normalization is the only change to committed capture JSON. No measurement was edited. No full frames or individual per-mode frame images are retained.

Locked share is unavailable for TAA, and not applicable for FXAA. Production has no stored-lock overlay. The existing History view recomputes `temporalResolvePixel` and tints `t.stability >= 0.5`, rather than exposing the stored-lock set requested here. SnapshotTool has no lock-view request. No paired overlay captures exist and none were fabricated. This follows the explicit Task 10 dispatch override to the earlier plan's overlay requirement.

Replay from the engine worktree, without rendering:

```bash
build-slot --heavy --label taa-replay -- python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/scripts/analyze_captures.py KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/captures KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility
python3 KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/verify_capture_manifest.py KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/manifest.json
```

The verifier finds the sibling Grimhollow repository by default. Set `GRIMHOLLOW_PROBE` if the durable probe commits are in another checkout. It validates Git objects rather than requiring the probe's current pin to match an older capture.

## Full results

| Build | Condition | Mode | Region | Grain mean | Grain p95 | Change mean | Dots | Locked share |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| baseline | back15 | taa-native | character | 0.014286626 | 0.074992941 | 0.017948762 | 1742 | unavailable |
| baseline | back15 | taa-native | ground | 0.006708474 | 0.026345098 | 0.012495262 | 485 | unavailable |
| baseline | back15 | taa-quality | character | 0.014156210 | 0.073341176 | 0.017574480 | 1733 | unavailable |
| baseline | back15 | taa-quality | ground | 0.007330379 | 0.028300392 | 0.012354459 | 1881 | unavailable |
| baseline | back15 | fxaa | character | 0.002961415 | 0.016488627 | 0.019114272 | 0 | not-applicable |
| baseline | back15 | fxaa | ground | 0.002129978 | 0.008692549 | 0.013105155 | 0 | not-applicable |
| baseline | day30 | taa-native | character | 0.008412866 | 0.049967059 | 0.003709978 | 1097 | unavailable |
| baseline | day30 | taa-native | ground | 0.005930306 | 0.023261961 | 0.002341774 | 310 | unavailable |
| baseline | day30 | taa-quality | character | 0.009148068 | 0.052065882 | 0.003660314 | 1117 | unavailable |
| baseline | day30 | taa-quality | ground | 0.006354869 | 0.025243922 | 0.002378851 | 906 | unavailable |
| baseline | day30 | fxaa | character | 0.002975726 | 0.016236863 | 0.004017949 | 0 | not-applicable |
| baseline | day30 | fxaa | ground | 0.002071400 | 0.008676863 | 0.002023307 | 0 | not-applicable |
| baseline | back60 | taa-native | character | 0.009675406 | 0.054834510 | 0.004882045 | 1355 | unavailable |
| baseline | back60 | taa-native | ground | 0.006054924 | 0.023529412 | 0.003282320 | 379 | unavailable |
| baseline | back60 | taa-quality | character | 0.010471691 | 0.059657255 | 0.004995076 | 1535 | unavailable |
| baseline | back60 | taa-quality | ground | 0.006729397 | 0.027178824 | 0.003268096 | 1303 | unavailable |
| baseline | back60 | fxaa | character | 0.002955330 | 0.015953725 | 0.004700304 | 0 | not-applicable |
| baseline | back60 | fxaa | ground | 0.002089821 | 0.008676863 | 0.003067754 | 0 | not-applicable |
| baseline | ground15 | taa-native | character | 0.002956869 | 0.011481569 | 0.001161596 | 371 | unavailable |
| baseline | ground15 | taa-native | ground | 0.005274224 | 0.019367059 | 0.003726085 | 512 | unavailable |
| baseline | ground15 | taa-quality | character | 0.003024465 | 0.011764706 | 0.001181601 | 340 | unavailable |
| baseline | ground15 | taa-quality | ground | 0.005695054 | 0.022423529 | 0.003777729 | 1305 | unavailable |
| baseline | ground15 | fxaa | character | 0.002280089 | 0.012598431 | 0.001350942 | 0 | not-applicable |
| baseline | ground15 | fxaa | ground | 0.002343219 | 0.007843137 | 0.003339447 | 0 | not-applicable |
| control | back15 | taa-native | character | 0.008027197 | 0.038932549 | 0.017636894 | 851 | unavailable |
| control | back15 | taa-native | ground | 0.005311957 | 0.020740392 | 0.012357556 | 108 | unavailable |
| control | back15 | taa-quality | character | 0.011052578 | 0.054351373 | 0.017510300 | 1166 | unavailable |
| control | back15 | taa-quality | ground | 0.006369932 | 0.024630588 | 0.012263843 | 1034 | unavailable |
| control | back15 | fxaa | character | 0.002607103 | 0.015403137 | 0.018394584 | 0 | not-applicable |
| control | back15 | fxaa | ground | 0.001654852 | 0.007560000 | 0.013100857 | 0 | not-applicable |
| control | day30 | taa-native | character | 0.005353814 | 0.028819608 | 0.003405675 | 506 | unavailable |
| control | day30 | taa-native | ground | 0.004652245 | 0.018490980 | 0.002145993 | 16 | unavailable |
| control | day30 | taa-quality | character | 0.007043855 | 0.038330196 | 0.003185126 | 784 | unavailable |
| control | day30 | taa-quality | ground | 0.005288216 | 0.020976471 | 0.002089615 | 275 | unavailable |
| control | day30 | fxaa | character | 0.002500048 | 0.014301961 | 0.003693384 | 0 | not-applicable |
| control | day30 | fxaa | ground | 0.001601914 | 0.007292549 | 0.002001172 | 0 | not-applicable |
| control | back60 | taa-native | character | 0.006320786 | 0.033842353 | 0.004511940 | 742 | unavailable |
| control | back60 | taa-native | ground | 0.004765996 | 0.019057255 | 0.003003559 | 28 | unavailable |
| control | back60 | taa-quality | character | 0.008142470 | 0.043687843 | 0.004338903 | 1065 | unavailable |
| control | back60 | taa-quality | ground | 0.005683207 | 0.023261961 | 0.002993479 | 423 | unavailable |
| control | back60 | fxaa | character | 0.002546938 | 0.013416471 | 0.004356398 | 0 | not-applicable |
| control | back60 | fxaa | ground | 0.001610824 | 0.007292549 | 0.002982424 | 0 | not-applicable |
| control | ground15 | taa-native | character | 0.002952664 | 0.011481569 | 0.001161694 | 371 | unavailable |
| control | ground15 | taa-native | ground | 0.004950930 | 0.017920000 | 0.003674704 | 396 | unavailable |
| control | ground15 | taa-quality | character | 0.003027428 | 0.011780392 | 0.001181286 | 341 | unavailable |
| control | ground15 | taa-quality | ground | 0.005404710 | 0.021578824 | 0.003728160 | 948 | unavailable |
| control | ground15 | fxaa | character | 0.002280813 | 0.012598431 | 0.001350774 | 0 | not-applicable |
| control | ground15 | fxaa | ground | 0.002293241 | 0.007575686 | 0.003328693 | 0 | not-applicable |
| final | back15 | taa-native | character | 0.005471380 | 0.029102745 | 0.017501558 | 364 | unavailable |
| final | back15 | taa-native | ground | 0.005263856 | 0.020472941 | 0.012355875 | 45 | unavailable |
| final | back15 | taa-quality | character | 0.008700161 | 0.045072157 | 0.017329929 | 760 | unavailable |
| final | back15 | taa-quality | ground | 0.006224461 | 0.024378824 | 0.012273386 | 821 | unavailable |
| final | back15 | fxaa | character | 0.002607103 | 0.015403137 | 0.018394584 | 0 | not-applicable |
| final | back15 | fxaa | ground | 0.001654852 | 0.007560000 | 0.013100857 | 0 | not-applicable |
| final | day30 | taa-native | character | 0.004947751 | 0.027131765 | 0.003423068 | 437 | unavailable |
| final | day30 | taa-native | ground | 0.004558558 | 0.017904314 | 0.002139648 | 0 | unavailable |
| final | day30 | taa-quality | character | 0.006268486 | 0.033307451 | 0.003188832 | 602 | unavailable |
| final | day30 | taa-quality | ground | 0.005219135 | 0.020709020 | 0.002073095 | 156 | unavailable |
| final | day30 | fxaa | character | 0.002500048 | 0.014301961 | 0.003693384 | 0 | not-applicable |
| final | day30 | fxaa | ground | 0.001601914 | 0.007292549 | 0.002001172 | 0 | not-applicable |
| final | back60 | taa-native | character | 0.005666259 | 0.030487059 | 0.004494207 | 601 | unavailable |
| final | back60 | taa-native | ground | 0.004691012 | 0.018538039 | 0.003001566 | 4 | unavailable |
| final | back60 | taa-quality | character | 0.007693414 | 0.041685490 | 0.004275856 | 961 | unavailable |
| final | back60 | taa-quality | ground | 0.005656938 | 0.023261961 | 0.002983260 | 316 | unavailable |
| final | back60 | fxaa | character | 0.002546938 | 0.013416471 | 0.004356398 | 0 | not-applicable |
| final | back60 | fxaa | ground | 0.001610824 | 0.007292549 | 0.002982424 | 0 | not-applicable |
| final | ground15 | taa-native | character | 0.003087123 | 0.011481569 | 0.001782407 | 393 | unavailable |
| final | ground15 | taa-native | ground | 0.005005808 | 0.018501961 | 0.006519368 | 216 | unavailable |
| final | ground15 | taa-quality | character | 0.003327763 | 0.012315294 | 0.002078012 | 405 | unavailable |
| final | ground15 | taa-quality | ground | 0.005513591 | 0.022916078 | 0.007835931 | 987 | unavailable |
| final | ground15 | fxaa | character | 0.002280813 | 0.012598431 | 0.001350774 | 0 | not-applicable |
| final | ground15 | fxaa | ground | 0.002293241 | 0.007575686 | 0.003328693 | 0 | not-applicable |

## Final versus control regressions

Every increase below is retained for the owner, including small changes. One p95 increase is floating-point roundoff at 5.551e-17, shown in scientific notation rather than rounded to zero. Comparisons use each build's own masks. Grain p95 is shown alongside mean, change and dots.

| Condition | Mode | Region | Metric | Control | Final | Absolute increase |
| --- | --- | --- | --- | ---: | ---: | ---: |
| back15 | taa-quality | ground | change | 0.012263843 | 0.012273386 | 0.000009543 |
| day30 | taa-native | character | change | 0.003405675 | 0.003423068 | 0.000017393 |
| day30 | taa-quality | character | change | 0.003185126 | 0.003188832 | 0.000003707 |
| ground15 | taa-native | character | grain | 0.002952664 | 0.003087123 | 0.000134459 |
| ground15 | taa-native | character | p95 | 0.011481569 | 0.011481569 | 5.551e-17 |
| ground15 | taa-native | character | change | 0.001161694 | 0.001782407 | 0.000620713 |
| ground15 | taa-native | character | dots | 371 | 393 | 22 |
| ground15 | taa-native | ground | grain | 0.004950930 | 0.005005808 | 0.000054877 |
| ground15 | taa-native | ground | p95 | 0.017920000 | 0.018501961 | 0.000581961 |
| ground15 | taa-native | ground | change | 0.003674704 | 0.006519368 | 0.002844665 |
| ground15 | taa-quality | character | grain | 0.003027428 | 0.003327763 | 0.000300335 |
| ground15 | taa-quality | character | p95 | 0.011780392 | 0.012315294 | 0.000534902 |
| ground15 | taa-quality | character | change | 0.001181286 | 0.002078012 | 0.000896726 |
| ground15 | taa-quality | character | dots | 341 | 405 | 64 |
| ground15 | taa-quality | ground | grain | 0.005404710 | 0.005513591 | 0.000108881 |
| ground15 | taa-quality | ground | p95 | 0.021578824 | 0.022916078 | 0.001337255 |
| ground15 | taa-quality | ground | change | 0.003728160 | 0.007835931 | 0.004107771 |
| ground15 | taa-quality | ground | dots | 948 | 987 | 39 |

The [comparison panels](owner-look/comparisons) show control and final, Native, Quality and FXAA, for both regions in every condition. They use each capture's densest edge window at frame 15, with the fixed cobble window for ground15 ground. The automatic world-band view is also retained.

## Owner look set and residual decisions

The [owner look directory](owner-look) contains all three builds' back15 character 4x crops for Native, Quality and FXAA, normal-scale back15 contact sheets, and ground15 ground 4x crops. Combined labeled panels preserve mode order. Character crops use the archive's fixed 96x96 window at crop coordinates (144, 200), frame 15. Ground crops use the fixed cobble-edge window (260, 270) to (356, 366), frame 15, overlapping the archive's ground transects. The capture's densest ground-band windows include a signboard, because the inherited ground mask covers world receivers rather than a terrain-only surface. Those original automatic windows remain in `owner-look/world-window`. No mask or metric changed when adding the cobble views. Per-condition normal pixels and masks remain available for reviewing every regression, including day30 and back60.

The owner accepted the real-scene D clips and the remaining lock-release costs on 2026-10-19. The synthetic grain, absolute 8 px tail and Quality M 1.0 leading textured rim costs are recorded in [owner-release-only.md](owner-release-only.md). The earlier response-enabled metrics remain above as history. This confirmation changes no bounds, skips or renderer source.

## Capture and pack commands

Run each command separately, sequentially through build-slot. `CAPTURE_ROOT`, `PACK_TREE` and `KHAOZENGINE_FEED` are externally configured scratch and private-feed locations. No command below runs a windowed client or publishes packages.

Private control pack, from the engine task worktree:

```bash
git worktree add --detach "$PACK_TREE" 1472e9609
```

In that detached scratch tree only, set `KhaozEngineVersion` to `21.6.0-svctrl.1`, then run:

```bash
build-slot --heavy --label taa-pack-control -- scripts/pack-local-feed.sh
```

Remove that scratch tree. Prepare a second detached scratch tree at `a8aa66e37`, set its version to `21.6.0-svfinal.1` and run the same pack wrapper with label `taa-pack-final`. Both packs set the explicit private `KHAOZENGINE_FEED`. Neither scratch version change is committed.

For the baseline use the probe's vendored packages as the feed. For each private build set its pin and both `ke-tileedit` and `ke-sfxbake` versions, then run from the guarded probe worktree:

```bash
scripts/refresh-engine.sh
build-slot --label taa-probe-build -- dotnet build tools/SnapshotTool/SnapshotTool.csproj -c Release
```

Each build succeeded with zero warnings and errors. The focused `ShadowGrainTests` selection passed 28 tests with zero skips. Formatting verification passed for both changed probe source files. Every pin was committed and pushed on the probe branch before its captures. Capture commands below also set `KE_GRAPHICS_BACKEND=metal-native` and redirect logs into the external capture root. No run passes `--keep-frames`.

```bash
build-slot --heavy --label taa-baseline-back15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/baseline/back15" --spot back-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-baseline-day30 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/baseline/day30" --spot back-edge --frame-time 1/15 --day-minutes 30 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-baseline-back60 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/baseline/back60" --spot back-edge --frame-time 1/60 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-baseline-ground15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/baseline/ground15" --spot ground-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-control-back15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/control/back15" --spot back-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-control-day30 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/control/day30" --spot back-edge --frame-time 1/15 --day-minutes 30 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-control-back60 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/control/back60" --spot back-edge --frame-time 1/60 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-control-ground15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/control/ground15" --spot ground-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-final-back15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/final/back15" --spot back-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-final-day30 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/final/day30" --spot back-edge --frame-time 1/15 --day-minutes 30 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-final-back60 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/final/back60" --spot back-edge --frame-time 1/60 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-final-ground15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain "$CAPTURE_ROOT/final/ground15" --spot ground-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
```

The ground look refinement is reproducible with `scripts/make_ground_look.py` against the committed lossless contact-sheet archives. It changes presentation only.

The probe deleted 160 intermediate per-frame images after every successful capture. Full Grimhollow tests and navigation rebakes were deliberately omitted under the probe dispatch. The historical round does not replace the engine's final correctness gates. The owner acceptance and exact shipping-source confirmation are recorded below.


## Accepted D shipping-head confirmation

The exact shipping source is `8307e93361ee66ef30b9a379d2b1eeaf0eb7501b`, including the Task 7d response deletion and the D3D11 non-finite-depth fix. A detached scratch worktree changed only `Directory.Build.props` to private package `21.6.0-svship.1`. The new version did not exist in the private feed and no same-version NuGet extraction existed. The feed was `~/.cache/khaozengine-probe-feed/taa-shadow-visibility`. The pack produced 222 package files and the scratch worktree was removed. The probe pin, both tool pins and all 222 vendored files are committed and pushed at `fc0684fe3cb54a8d92ba7ff8aa0752a1135e7b9d` on the diagnostic archive branch. No game shipping branch changed.

| Identity | SHA256 |
| --- | --- |
| Render3D nupkg | `d5e70f8a65447ece539e4a3b1a947a39391799252f94ed026a316569f4be65a4` |
| Render3D symbols | `dec2a30966fe9535f6658e75268bb762aa0ed02b351d13db12efcc2f0e290705` |
| Loaded Render3D DLL, both captures | `0cb4f7252e46fe566356ba7ffb25bc58833825996ad3b18550120cd96c254b75` |

The [manifest](manifest.json) records the source tree, relevant source hashes, all 222 package hashes, package repository commit, probe commit and both captured loaded-binary identities in its separate `shipping_build` record. The D reference names private package `21.6.0-svtune-d.1`, probe `e9e994d8017c3ec2ff98ee72a839e18bc519aa78` and loaded DLL `cc9d7583b1f3e506a086a3d092a929a2f974bc20faad356870f39cd4a4ea3ccf`. The original three historical builds retain all four conditions. Shipping confirmation requires only back15 and ground15 under the authorized Task 10 brief.

One capture ran per condition, without retries, on MetalNative / Apple M2 Max. Both used 2560x1440, dt 1/15, a five-minute day, start time `0.49777777777777776`, 80 warm-up frames and 16 measured frames. Native, Quality and FXAA each retained a continuous history. Every build, pack, capture and archive analysis used build-slot. Pack and build completed with zero warnings and errors. Each capture checked the loaded renderer against the vendored nupkg before image analysis and deleted its 160 per-frame images.

Analysis called the unchanged archive `read_run` path. Every grain mean, nearest-rank p95, change mean, dot count and sample count matches D exactly across both regions and all modes. The six primary rows also match the Task 7c printed values at all nine decimal places. Locked share remains unavailable for TAA and not applicable for FXAA. Neither round has lock overlays.

Each numeric cell below is shipping / D. No difference was rounded away.

| Condition | Region | Mode | Grain mean | Grain p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| back15 | character | taa-native | 0.005474607 / 0.005474607 | 0.029102745 / 0.029102745 | 0.017496412 / 0.017496412 | 364 / 364 |
| back15 | ground | taa-native | 0.005263856 / 0.005263856 | 0.020472941 / 0.020472941 | 0.012355875 / 0.012355875 | 45 / 45 |
| back15 | character | taa-quality | 0.008704202 / 0.008704202 | 0.045072157 / 0.045072157 | 0.017329273 / 0.017329273 | 759 / 759 |
| back15 | ground | taa-quality | 0.006224461 / 0.006224461 | 0.024378824 / 0.024378824 | 0.012273386 / 0.012273386 | 821 / 821 |
| back15 | character | fxaa | 0.002607103 / 0.002607103 | 0.015403137 / 0.015403137 | 0.018394584 / 0.018394584 | 0 / 0 |
| back15 | ground | fxaa | 0.001654852 / 0.001654852 | 0.007560000 / 0.007560000 | 0.013100857 / 0.013100857 | 0 / 0 |
| ground15 | character | taa-native | 0.002937400 / 0.002937400 | 0.011214118 / 0.011214118 | 0.001168977 / 0.001168977 | 371 / 371 |
| ground15 | ground | taa-native | 0.004815313 / 0.004815313 | 0.017416471 / 0.017416471 | 0.003688205 / 0.003688205 | 262 / 262 |
| ground15 | character | taa-quality | 0.003011179 / 0.003011179 | 0.011816471 / 0.011816471 | 0.001193931 / 0.001193931 | 330 / 330 |
| ground15 | ground | taa-quality | 0.005046021 / 0.005046021 | 0.020724706 / 0.020724706 | 0.003739381 / 0.003739381 | 782 / 782 |
| ground15 | character | fxaa | 0.002280813 / 0.002280813 | 0.012598431 / 0.012598431 | 0.001350774 / 0.001350774 | 0 / 0 |
| ground15 | ground | fxaa | 0.002293241 / 0.002293241 | 0.007575686 / 0.007575686 | 0.003328693 / 0.003328693 | 0 / 0 |

The shipping Native, Quality and FXAA normal contact-sheet pixels are byte-identical to D in both conditions. Both region-mask streams are byte-identical to D. FXAA is also byte-identical to A. Differing pixel-frame count is zero in every comparison. Back15 character grain and dots remain below bilinear-only at both presets, with Native 0.005474607 / 364 versus 0.008027197 / 851 and Quality 0.008704202 / 759 versus 0.011052578 / 1166.

The committed shipping inputs retain the lossless normal RGB archive, region bits, capture provenance and metrics. Summaries are [shipping-summary.json](shipping-summary.json) and [shipping-D-reference-summary.json](shipping-D-reference-summary.json). Contact sheets, edge crops, logs and the replay comparison remain under `/tmp/grimhollow-shadow-visibility/ship/`. There are no per-frame leftovers. The evidence verifier preserves every historical check and separately requires the two shipping conditions, three modes, both regions, complete hashes and the dated D acceptance. It accepts a pending shipping decision only when the D comparison differs. This round did not run GPU test suites, timing tests or other backends.


## 7f shipping capture

Source `8fe6c5927c6fd0fa4960bcd6dda88e2c209f8ff5`, private package `21.8.0-svcut.1`, probe `c480240d588a023dc8ac3f00311ddd478913f20a`. The detached pack changed only the version declaration and wrote exclusively to `~/.cache/khaozengine-probe-feed/taa-shadow-visibility`. All 222 package hashes and relevant source hashes are recorded in the manifest's separate `cost_cut_build` record. The pack scratch worktree was removed. The diagnostic probe pin and both tool pins were committed and pushed before capture, and its remote SHA was verified.

| Identity | SHA256 |
| --- | --- |
| Render3D nupkg | `61f8905cc09cc26b5656e89e99c492d614d0c62518c977bb806a6b8090978a80` |
| Render3D symbols | `fd5c33cd1cf704ef8332d9bf8f1f74eb51b25a2d3dd2581394a5d99541631a87` |
| Loaded renderer, build and both captures | `d831e1f1b591df59e49a70028b791351eb6e6e48dd0cd514d44d746350db6a90` |

The two captures each ran once through build-slot --heavy with shadowgrain, MetalNative on Apple M2 Max, 2560x1440, dt 1/15, a five-minute day, start time 0.49777777777777776, 80 warm-up frames and 16 measured frames. Native, Quality and FXAA retained continuous histories. The built renderer matched the vendored nupkg before capture. Both provenance records match that hash. Both runs exited 0 and each deleted 160 per-frame images. No per-frame images remain.

Run from the guarded Grimhollow probe worktree, with logs redirected under `/tmp/grimhollow-taa-7f5/`.

```bash
build-slot --heavy --label taa-7f5-back15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain /tmp/grimhollow-taa-7f5/capture/back15 --spot back-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-7f5-ground15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain /tmp/grimhollow-taa-7f5/capture/ground15 --spot ground-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
```

The unchanged archive `read_run` replay agrees with the tool within 1e-12. Every full-precision replay row, including samples and dots, equals the [shipping D summary](shipping-summary.json). The [7f summary](7f-summary.json) and [comparison](7f-comparison.json) retain the exact values. Each numeric cell below is shipping D / 7f, using full Python printed precision.

| Condition | Region | Mode | Grain mean | Grain p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| back15 | character | taa-native | 0.005474607155653376 / 0.005474607155653376 | 0.029102745098039118 / 0.029102745098039118 | 0.017496411969800285 / 0.017496411969800285 | 364 / 364 |
| back15 | ground | taa-native | 0.00526385609353138 / 0.00526385609353138 | 0.020472941176470583 / 0.020472941176470583 | 0.012355875438918482 / 0.012355875438918482 | 45 / 45 |
| back15 | character | taa-quality | 0.008704202242702883 / 0.008704202242702883 | 0.04507215686274513 / 0.04507215686274513 | 0.017329273445767134 / 0.017329273445767134 | 759 / 759 |
| back15 | ground | taa-quality | 0.0062244609736071465 / 0.0062244609736071465 | 0.024378823529411675 / 0.024378823529411675 | 0.01227338604965782 / 0.01227338604965782 | 821 / 821 |
| back15 | character | fxaa | 0.0026071029228602943 / 0.0026071029228602943 | 0.015403137254901944 / 0.015403137254901944 | 0.018394583895005687 / 0.018394583895005687 | 0 / 0 |
| back15 | ground | fxaa | 0.0016548521211947336 / 0.0016548521211947336 | 0.0075599999999999 / 0.0075599999999999 | 0.013100857420120631 / 0.013100857420120631 | 0 / 0 |
| ground15 | character | taa-native | 0.00293740035335003 / 0.00293740035335003 | 0.01121411764705893 / 0.01121411764705893 | 0.0011689773847883102 / 0.0011689773847883102 | 371 / 371 |
| ground15 | ground | taa-native | 0.004815313021091819 / 0.004815313021091819 | 0.017416470588235367 / 0.017416470588235367 | 0.003688204569631017 / 0.003688204569631017 | 262 / 262 |
| ground15 | character | taa-quality | 0.0030111794193593325 / 0.0030111794193593325 | 0.011816470588235206 / 0.011816470588235206 | 0.0011939310203571874 / 0.0011939310203571874 | 330 / 330 |
| ground15 | ground | taa-quality | 0.005046020977145357 / 0.005046020977145357 | 0.020724705882352867 / 0.020724705882352867 | 0.0037393807392348626 / 0.0037393807392348626 | 782 / 782 |
| ground15 | character | fxaa | 0.002280812938047255 / 0.002280812938047255 | 0.012598431372548957 / 0.012598431372548957 | 0.0013507738528787314 / 0.0013507738528787314 | 0 / 0 |
| ground15 | ground | fxaa | 0.0022932411255083716 / 0.0022932411255083716 | 0.007575686274509774 / 0.007575686274509774 | 0.003328692573683128 / 0.003328692573683128 | 0 / 0 |

Identical verdict: true. All 12 rows match exactly. Both lossless RGB streams and region-mask streams are byte-identical to shipping D. Differing pixel-frame counts are zero for Native, Quality and FXAA in each condition. The previous shipping comparison already proves D identity. Locked share and lock overlays remain unavailable with the historical explicit reason.

| Condition | RGB SHA256, shipping D and 7f | Region-mask SHA256, shipping D and 7f |
| --- | --- | --- |
| back15 | `42a6ad43168c03470b5138a51ea26ba6d1d3b2affd30ac4cd3e0314d448ecb94` | `adfbc3c6c2f74775770413f0290d8ac6f62fe717c8e294139e7e144888394986` |
| ground15 | `fac3d2356e5d35d4aa8a44bb71f89d9aa957a0e4444f373f493af0a1545bdc9c` | `fe19a50ced8e1ce8100f896cfc14e82573291993588a08de0689b922890bd9cf` |

The committed capture inputs retain lossless RGB, masks, metrics and provenance. Contact sheets and crops remain under `/tmp/grimhollow-taa-7f5/capture/`. The [local gate record](verification.md#7f-local-gate-and-byte-identical-shipping-capture) records the Release solution build with zero warnings and errors, the headless suite with 37,110 passed, zero failed and 1,484 expected skips across 41 assemblies, and the Metal gate with 1,687 inventoried, 1,687 passed, zero failed and zero skipped. The selected Metal skipped list is empty. No cost, perf or bench class was selected. All command exits are zero. Formatting verification and the whole-tree guards passed. The formatting process retained the historical workspace-loading warning. This round changes no source, tests, bounds or baselines and supplies no timing or other-backend proof.


The guarded source already had three stale artifact hashes (filter-baseline.md, non-finite-depths.md and reset-tuning.md) and four omitted artifact entries (benchmark-request.md, benchmark-result.md, local-metal-inventory.jsonl and verification.md). Their bytes were checked against the source commit. The three stale documents are unchanged. The artifact ledger now hashes the current source documents and includes the omitted entries. The [drift record](7f-preexisting-manifest-drift.json) preserves the old hashes. Every historical source, package, capture, baseline, D-reference and owner-acceptance check remains unchanged.


## 7g.3a barrier lock release shipping capture

Source `ef97639b6d2b33973715652a35858d55e0665a0f`, private package `21.8.0-svbar.1`, probe `bd396fd8bdbf6e811d35848fd2983337eec21a4c`. The source replaces the `precise` lock release with the barrier-ordered form, the same arithmetic on every backend. The detached pack changed only the version declaration and wrote exclusively to `~/.cache/khaozengine-probe-feed/taa-shadow-visibility`. The version did not exist in that feed or the NuGet cache before packing. All 222 package hashes and relevant source hashes are recorded in the manifest's separate `barrier_build` record. The pack scratch worktree was removed. The diagnostic probe pin and both tool pins were committed and pushed before capture, and its remote SHA was verified.

| Identity | SHA256 |
| --- | --- |
| Render3D nupkg | `f896ca17f41f4acacc2aee30999032fdd261b5fde79bfef54111e2de3c538e7e` |
| Render3D symbols | `702db3d112af8ad7768bdd37dee42b1ab8bccd546e76a43ed7593a8ae244bd36` |
| Loaded renderer, build and both captures | `d96d16e01a1909547e2195fdff70ae4810aff8beb9b1e62e84574389f30d5766` |

The two captures each ran once through build-slot --heavy with shadowgrain, MetalNative on Apple M2 Max, 2560x1440, dt 1/15, a five-minute day, start time 0.49777777777777776, 80 warm-up frames and 16 measured frames. Native, Quality and FXAA retained continuous histories. The built renderer matched the vendored nupkg before capture. Both provenance records match that hash and name the source. Both runs exited 0 and each deleted 160 per-frame images. No per-frame images remain.

Run from the guarded Grimhollow probe worktree, with logs redirected under `/tmp/grimhollow-taa-7g3a/`.

```bash
build-slot --heavy --label taa-7g3a-back15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain /tmp/grimhollow-taa-7g3a/capture/back15 --spot back-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
build-slot --heavy --label taa-7g3a-ground15 -- env KE_GRAPHICS_BACKEND=metal-native dotnet run --project tools/SnapshotTool -c Release --no-build -- shadowgrain /tmp/grimhollow-taa-7g3a/capture/ground15 --spot ground-edge --frame-time 1/15 --day-minutes 5 --start-time 0.49777777777777776 --warmup 80 --frames 16
```

The unchanged archive `read_run` replay agrees with the tool within 1e-12. The [7g.3a summary](7g3a-summary.json) is byte-identical to the [shipping D summary](shipping-summary.json), and the [comparison](7g3a-comparison.json) retains the exact values. Each numeric cell below is shipping D / 7g.3a, using full Python printed precision.

| Condition | Region | Mode | Grain mean | Grain p95 | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| back15 | character | taa-native | 0.005474607155653376 / 0.005474607155653376 | 0.029102745098039118 / 0.029102745098039118 | 0.017496411969800285 / 0.017496411969800285 | 364 / 364 |
| back15 | ground | taa-native | 0.00526385609353138 / 0.00526385609353138 | 0.020472941176470583 / 0.020472941176470583 | 0.012355875438918482 / 0.012355875438918482 | 45 / 45 |
| back15 | character | taa-quality | 0.008704202242702883 / 0.008704202242702883 | 0.04507215686274513 / 0.04507215686274513 | 0.017329273445767134 / 0.017329273445767134 | 759 / 759 |
| back15 | ground | taa-quality | 0.0062244609736071465 / 0.0062244609736071465 | 0.024378823529411675 / 0.024378823529411675 | 0.01227338604965782 / 0.01227338604965782 | 821 / 821 |
| back15 | character | fxaa | 0.0026071029228602943 / 0.0026071029228602943 | 0.015403137254901944 / 0.015403137254901944 | 0.018394583895005687 / 0.018394583895005687 | 0 / 0 |
| back15 | ground | fxaa | 0.0016548521211947336 / 0.0016548521211947336 | 0.0075599999999999 / 0.0075599999999999 | 0.013100857420120631 / 0.013100857420120631 | 0 / 0 |
| ground15 | character | taa-native | 0.00293740035335003 / 0.00293740035335003 | 0.01121411764705893 / 0.01121411764705893 | 0.0011689773847883102 / 0.0011689773847883102 | 371 / 371 |
| ground15 | ground | taa-native | 0.004815313021091819 / 0.004815313021091819 | 0.017416470588235367 / 0.017416470588235367 | 0.003688204569631017 / 0.003688204569631017 | 262 / 262 |
| ground15 | character | taa-quality | 0.0030111794193593325 / 0.0030111794193593325 | 0.011816470588235206 / 0.011816470588235206 | 0.0011939310203571874 / 0.0011939310203571874 | 330 / 330 |
| ground15 | ground | taa-quality | 0.005046020977145357 / 0.005046020977145357 | 0.020724705882352867 / 0.020724705882352867 | 0.0037393807392348626 / 0.0037393807392348626 | 782 / 782 |
| ground15 | character | fxaa | 0.002280812938047255 / 0.002280812938047255 | 0.012598431372548957 / 0.012598431372548957 | 0.0013507738528787314 / 0.0013507738528787314 | 0 / 0 |
| ground15 | ground | fxaa | 0.0022932411255083716 / 0.0022932411255083716 | 0.007575686274509774 / 0.007575686274509774 | 0.003328692573683128 / 0.003328692573683128 | 0 / 0 |

Identical verdict: true. All 12 rows match exactly. Both lossless RGB streams and region-mask streams are byte-identical to shipping D. Differing pixel counts are zero for Native, Quality and FXAA in each condition. Locked share and lock overlays remain unavailable with the historical explicit reason.

| Condition | RGB SHA256, shipping D and 7g.3a | Region-mask SHA256, shipping D and 7g.3a |
| --- | --- | --- |
| back15 | `42a6ad43168c03470b5138a51ea26ba6d1d3b2affd30ac4cd3e0314d448ecb94` | `adfbc3c6c2f74775770413f0290d8ac6f62fe717c8e294139e7e144888394986` |
| ground15 | `fac3d2356e5d35d4aa8a44bb71f89d9aa957a0e4444f373f493af0a1545bdc9c` | `fe19a50ced8e1ce8100f896cfc14e82573291993588a08de0689b922890bd9cf` |

The committed capture inputs retain lossless RGB, masks, metrics and provenance. The [local gate record](verification.md#7g3a-barrier-lock-release-local-gate-and-byte-identical-shipping-capture) records the build, the headless suite, the Metal gate and the resolve A/B against the program D shipped. This round supplies no timing or other-backend proof.
