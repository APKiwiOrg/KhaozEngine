# Real-scene shadow visibility evidence

Status: evidence collected, owner decision pending. This record does not accept Task 10 or authorize a release.

Material concern: ground15 ground change mean rises from 0.003674704 to 0.006519368 at Native and from 0.003728160 to 0.007835931 at Quality. Ground grain rises at both presets. Quality ground dots rise from 948 to 987. The back15 improvement alone does not settle this stability trade-off.

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

The orchestrator must obtain the owner's look decision and the Task 7b residual trade-off decision. The synthetic values supplied by the plan are Quality textured grain 0.008031 against 0.008, Quality M 1.0 leading textured rim error 0.0030106876217401947 against 0.00271205, and temporal error 0.004819472651560228 against 0.00432135. These are plan-supplied synthetic results, not measurements derived from the real scene. This task changes no bounds or skips and does not rebaseline any candidate. Those failures remain for the owner and orchestrator to resolve.

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

The probe deleted 160 intermediate per-frame images after every successful capture. Full Grimhollow tests and navigation rebakes were deliberately omitted under the probe dispatch. This evidence does not replace the engine's final correctness gates or owner acceptance.
