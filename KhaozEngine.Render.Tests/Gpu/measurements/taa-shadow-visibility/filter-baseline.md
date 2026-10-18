# Bilinear-input unchanged-resolve control

The PCF filter changed the scene input. These rows measure the unchanged 21.4.0 resolve on bilinear 3x3 PCF. The signal is judged against this frozen input-matched control.

## Source lock

The bilinear-only source is `02f0bd1fc885770f90e948028ef35509ad9153af`, the filter commit. All 41 temporal
source files under `KhaozEngine.Render3D` are byte-identical to `v21.4.0`, including accumulation, preparation,
display reconstruction, tuning, storage, entry and reset wiring. Their SHA-256 identities are in
[control-sources.sha256](control-sources.sha256). No visibility input, history, release helper or reset is present.

[control-inputs.sha256](control-inputs.sha256) pins the filter and fixture sources at that measurement commit.
The fresh Metal point control uses that same tree and resolve, substituting only the exact `pcfCascade` block
from `1251895ab` into `CascadeShadowFilterGlsl`. The bilinear source was restored before the baseline edits.
There is no renderer change in this baseline commit.

The original `TemporalReviewBaseline.Before` table is retained byte-for-byte, all 240 rows. Its point-filter
history is the v21.2.0 resolve at `a82078bdb`, hosted run `39069066431`, with WARP at `12a720403`, run
`39069733082`. Those temporal numbers are historical context, not paired v21.4.0 measurements. A fresh paired
v21.4.0 point control was collected on Metal. WARP and lavapipe use their existing point-filter history, as
instructed, without borrowing Metal values.

## Measurement procedure

The existing non-temporal supersampled path supplies the reference. The rim fixture is 320 by 180, with a
4x reference at 1280 by 720, 80 temporal warm-up frames and eight measured frames. Every temporal capture
aligns to the same 144-frame jitter phase, cuts history and warms independently. The reference starts at
the same authored frame and uses AA Off, then box-downsamples. Settings, stage, masks, sample definitions and
frame inventory are unchanged. Every measured rim still asserts at least 4000 samples.

There are 56 rows per device class, Native and Quality, M at 0.5, 1, 2, 4 and 8 display pixels per frame, MS at
1 and 2, each with leading and trailing, flat and textured regions. Each raw row is
`device | case | absolute error | raw change | temporal error | reference change`. All four figures retain
six decimal places. The reference figure is the reference sequence's frame-to-frame change, not spatial error.

Only `kind == "rim"` routes to `TemporalShadowFilterBaseline`. Error and temporal error use the unchanged
`(baseline + 0.0000005) * 1.1` formula. The baseline tolerance is `0.1`, rounding is `0.0000005` and reference
identity tolerance remains `0.000001`. Raw rim change remains report-only. Historical diagonal checks still
bound error, temporal error and raw change. An unmeasured device or case fails without a fallback.

## Controls and input movement

| Device class | Measured adapter | Bilinear rows | Reference movements | Old error misses | Old temporal misses | New reference and rim checks |
| --- | --- | --- | --- | --- | --- | --- |
| hardware, local MetalNative | Apple M2 Max | 56 | 24 | 1 | 0 | CPU replay and live GPU pass |
| hardware, hosted MetalNative | Apple Paravirtual device | 56 | 24 | 1 | 0 | CPU replay passes all hosted readbacks |
| WARP, Direct3D11Native | Microsoft Basic Render Driver | 56 | 24 | 1 | 0 | CPU replay passes all hosted readbacks |
| lavapipe, VulkanNative | llvmpipe (LLVM 20.1.2, 256 bits) | 56 | 24 | 1 | 0 | CPU replay passes all hosted readbacks |

The hosted Metal adapter is virtual. `TemporalDeviceClass` calls it `hardware` because it is neither
WARP nor lavapipe. This is a test classification, not a claim that the hosted adapter is a physical GPU.
The local hardware control is the physical M2 Max. Software rows retain their own measured values.

The Quality M 0.5 leading-flat error is `0.010485`. Its historical point-input baseline is `0.009307`, with
the old bound `(0.009307 + 0.0000005) * 1.1 = 0.01023825`. The fresh paired point control also measures
`0.009307`. The bilinear-input control's bound is `(0.010485 + 0.0000005) * 1.1 = 0.01153405`.
This miss is input movement, not a visibility-signal improvement. Its temporal error moves from `0.006161`
to `0.005814`, with new bound `0.00639595`.

The fresh Metal point reference equals the historical point reference in every row. Bilinear PCF moves
24 of the 56 references beyond the unchanged identity tolerance. All 56 hosted Metal rows match the local
M2 Max rows exactly. The historical temporal figures differ from the fresh v21.4.0 point measurements in
39 rows, so the paired point capture is retained separately rather than rewriting history.

[control-comparison.tsv](control-comparison.tsv) contains all 168 per-class rows with the four historical
point figures, the four paired v21.4.0 point figures where collected, the four bilinear figures, and exact
old and new error and temporal bounds. Bounds were derived with decimal arithmetic and retain eight
places. WARP and lavapipe paired-point columns say `unmeasured`. Their historical v21.2.0 values are
context only, never a substitute for the current bilinear control or evidence of a filter-only improvement.

The output excerpts retain raw six-decimal figures with trailing whitespace removed. They are [local bilinear Metal](bilinear-metal.txt),
[hosted bilinear Metal](bilinear-metal-hosted.txt), [bilinear WARP](bilinear-d3d11.txt),
[bilinear lavapipe](bilinear-vulkan.txt) and the [paired point Metal](point-metal.txt).

The corresponding WARP Quality M 0.5 leading-flat error is `0.010531`, above its historical bound
`0.01029765`, with new bound `0.01158465`. Its temporal error is `0.005862`, with new bound `0.00644875`.
Lavapipe error is `0.010496`, above its historical bound `0.01031745`, with new bound `0.01154615`.
Its temporal error is `0.005866`, with new bound `0.00645315`. Each is recorded as input movement.

## Reproduction and verification

The local selection was exactly:

```bash
FOCUS='FullyQualifiedName~TemporalMovingShadeRimGpuTests|FullyQualifiedName~TemporalMovingShadeLagGpuTests|FullyQualifiedName~TemporalTextureLightGpuTests'
build-slot --label taa-task3-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release
build-slot --heavy --label taa-task3-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native \
  dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build \
  --filter "$FOCUS" --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=control.trx'
```

Each initial control capture ran once, through `build-slot`. The initial bilinear run executed five of five GPU facts with
zero skips. Four passed. Only the old rim assertions failed, with the reported acceptance miss and 24
reference changes. The paired point run passed all five, with zero skips. The device was MetalNative,
Apple M2 Max, hardware. The CPU replay test was written before rerouting and failed on the same old-input
assertions, one failed and two passed. This was a behavioural RED, not a compile or missing-file failure.

The final Release build passed with zero warnings and errors. The separate CPU replay passed seven of
seven tests, zero skipped. Four replay cases each apply the actual reference and rim checks to all 56
local Metal, hosted Metal, WARP and lavapipe readbacks. Two cases reject missing device or case evidence,
and the final test proves historical diagonal raw-change checks still reject a regression.

The final Metal selection passed all five GPU facts, zero skipped, under `KE_GPU_TESTS=1` and
`KE_GRAPHICS_BACKEND=metal-native` on Apple M2 Max. Its 56 raw rim rows equal the frozen control exactly.
This includes the rim acceptance fact, reused-scene parity, lag, local-light detail and local-light parity.
The TRX counters prove that all five selected GPU facts executed. CPU replay is explicitly distinct
from new software-device GPU execution.

The replay command is:

```bash
build-slot --label taa-task3-replay -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj \
  -c Release --no-build --filter 'FullyQualifiedName~TemporalShadowFilterBaselineTests'
```

The existing hosted collection is [cross-platform-gpu run 39173269032](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39173269032)
on `02f0bd1fc`. No additional hosted run was dispatched. Detailed measurements come from
`filtered-render-measurements-metal-native`, `filtered-render-measurements-direct3d11-native` and
`filtered-render-measurements-vulkan-native`. Metal device identity is retained in `device-evidence-metal-native`. The software-device identities and
classes are reported by the filter readback and control output in their own suite artifacts.

[hosted-artifacts.json](hosted-artifacts.json) records source SHA, run, job IDs, artifact names,
SHA-256 of each complete `suite-output.log`, result counts and all 38 passing filter, gather, FXC and
shader-hash gate results per backend. [hosted-test-inventory.jsonl](hosted-test-inventory.jsonl) retains
all 3297 detailed test outcomes, 1099 per backend, checked against the downloaded console artifacts.

| Backend | Job ID | Passed | Failed | Skipped | Control facts executed | Task 2 gates |
| --- | --- | --- | --- | --- | --- | --- |
| metal-native | 117810632281 | 1097 | 2 | 0 | 5 | 38 passed |
| direct3d11-native | 117810632167 | 1097 | 2 | 0 | 5 | 38 passed |
| vulkan-native | 117810632138 | 1097 | 2 | 0 | 5 | 38 passed |

Every leg failed only `TemporalGameShadowGrainGpuTests.Texture_ridges_under_a_coarse_moving_shadow_do_not_hold_bright_grain`
and the old-input `TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims`.
The hosted run conclusion is failure. The grain RED remains pending. The old rim failure is replaced
by the frozen input-matched control, and replaying each class's hosted readbacks passes its new checks.

The filtered workflow disables the TRX logger for all three unsharded legs, including Windows because
`POOL_WATCH_ARMED` explicitly excludes `renderTestFilter`. Local Metal TRX proves five selected GPU facts
executed with zero skips. For the hosted collection, the detailed console artifacts and complete result
inventory provide the actual outcomes. They are not native
TRX evidence. This limitation is reported rather than manufacturing a TRX record or dispatching another run.

## Preserved guards and review

The Task 1 textured-grain guard remains an explicit pending fix. Bilinear-only Native grain is `0.011770`
against `0.0113`, and Quality is `0.010593` against `0.008`. No grain, lag, local-light, golden or unrelated
baseline was re-recorded. This commit freezes only the measured rim control and its input identities.

Self-review checked each of the 168 table rows against its raw six-decimal output and all 672 old and
new Decimal bounds against the comparison TSV. All 56 final Metal rows equal their frozen rows. Each
hosted extract equals its source artifact, and the 3297 inventory records match the console outcomes.
The original 240 historical table rows are byte-identical. Only the rim lookup routes to the new table,
with the existing caller signature, tolerance, rounding, reference check and metric definitions intact.
Case and sample-count assertions are unchanged. No renderer, version, signal, golden or unrelated
acceptance change is included. Missing rows fail explicitly. Later signal work must use this control
without re-recording it.

Scoped format verification and the staged and whole-tree repository guards passed. No fresh hosted
run, broad local suite, timing or load run was dispatched. Task 11 retains the final broad local gate.
