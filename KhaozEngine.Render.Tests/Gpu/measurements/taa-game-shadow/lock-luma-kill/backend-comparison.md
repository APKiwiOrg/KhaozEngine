# Hosted temporal correctness comparison

Status: blocked. [Run 39119425055](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39119425055) completed
with failure at code commit `507f0d1357c10a8712aef46ce596547dbebac79d`. Exactly one run was dispatched.
Later commits add reports only. No renderer, test assertion or tuning constant changed after this code commit.

## Results

| Backend | Device class | Passed | Failed | Skipped | Total | Guard values |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| metal-native | hardware | 908 | 11 | 0 | 919 | [394 rows](guard-values/metal-native.csv) |
| direct3d11-native | WARP | 908 | 11 | 0 | 919 | [394 rows](guard-values/direct3d11-native.csv) |
| vulkan-native | lavapipe | 908 | 11 | 0 | 919 | [394 rows](guard-values/vulkan-native.csv) |

The sync-validation job also fails the Quality upscaling chart. Its log reports no validation messages, so that
job is not cited as proof of clean validation instrumentation. The main legs complete their correctness scans.
The local run before the added fixture parity fact passed 907 of 918, with the same 11 failures as hosted Metal.
The added parity fact passes on every backend. Timing benchmark classes were excluded.

## Synthetic textured receiver

The 21.4.0 behavior on local Metal is Native 0.012026, Quality 0.010852 and Balanced 0.010526. The diagnostic
archive's kill-0.1 is Native 0.010442 and Quality 0.006745. The production candidate's values below are measured
on each backend. A per-backend 21.4.0 baseline for this newly introduced fixture was not run. Balanced remains
report-only. No tuning was made for it.

| Mode | Metal | Direct3D 11 | Vulkan | Bound |
| --- | ---: | ---: | ---: | ---: |
| Native | 0.010803 | 0.010814 | 0.010806 | 0.011300 |
| Quality | 0.009229 | 0.009231 | 0.009254 | 0.008000 |
| Balanced | 0.010526 | 0.010511 | 0.010534 | report only |

## Absolute 1 px and 8 px tails

Before values are from the successful exact-tag 21.4.0 release gate and are printed to four decimals. After
values are this run's six-decimal measurements. Device classes differ, so each backend uses its own release
measurement. Both Native 8 px absolute tails are worse on every backend. Quality rows are included for completeness. No ratio was replaced with a looser absolute bound.

| Backend | Mode | Direction | Before 1 px | Before 8 px | After 1 px | After 8 px | Current 1.5 x 1 px bound |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| metal-native | Native | advancing | 0.0033 | 0.0044 | 0.003204 | 0.004724 | 0.00480600 |
| metal-native | Native | retreating | 0.0057 | 0.0070 | 0.005460 | 0.008256 | 0.00819000 |
| metal-native | Quality | advancing | 0.0031 | 0.0042 | 0.003110 | 0.004524 | 0.00466500 |
| metal-native | Quality | retreating | 0.0069 | 0.0060 | 0.007663 | 0.006670 | 0.01149450 |
| direct3d11-native | Native | advancing | 0.0033 | 0.0044 | 0.003196 | 0.004724 | 0.00479400 |
| direct3d11-native | Native | retreating | 0.0056 | 0.0070 | 0.005457 | 0.008240 | 0.00818550 |
| direct3d11-native | Quality | advancing | 0.0032 | 0.0042 | 0.003135 | 0.004510 | 0.00470250 |
| direct3d11-native | Quality | retreating | 0.0069 | 0.0060 | 0.007629 | 0.006659 | 0.01144350 |
| vulkan-native | Native | advancing | 0.0033 | 0.0045 | 0.003269 | 0.004719 | 0.00490350 |
| vulkan-native | Native | retreating | 0.0057 | 0.0070 | 0.005528 | 0.008309 | 0.00829200 |
| vulkan-native | Quality | advancing | 0.0032 | 0.0042 | 0.003245 | 0.004579 | 0.00486750 |
| vulkan-native | Quality | retreating | 0.0070 | 0.0062 | 0.007824 | 0.006752 | 0.01173600 |

## Camera pan and local lighting

The Quality 2 px pan band is back within its existing per-device-class bound. This does not compensate for the
absolute fast-tail or detail regressions. Values below are actual / bound. The full CSVs include every still-detail,
peak, shadow-edge, moving-band, diagonal and rim guard value. Borderline printed comparisons defer to the actual
assertions. The light baselines were measured on local Metal, as documented in the new test. They were not
recalibrated to green on another backend.

| Guard | Metal | Direct3D 11 | Vulkan |
| --- | --- | --- | --- |
| Quality C pan 2.0 px band error | 0.005846 / 0.00605000 | 0.005844 / 0.00605000 | 0.005951 / 0.00616000 |
| light dark Native error grain | 0.000356 / 0.00039160 | 0.000360 / 0.00039160 | 0.000353 / 0.00039160 |
| light dark Quality error grain | 0.000877 / 0.00096470 | 0.000873 / 0.00096470 | 0.000872 / 0.00096470 |
| light flicker Native error grain | 0.006545 / 0.00605330 | 0.006532 / 0.00605330 | 0.006549 / 0.00605330 |
| light flicker Quality error grain | 0.006138 / 0.00587400 | 0.006124 / 0.00587400 | 0.006125 / 0.00587400 |
| light moving Native error grain | 0.003824 / 0.00420420 | 0.003830 / 0.00420420 | 0.003824 / 0.00420420 |
| light moving Quality error grain | 0.004559 / 0.00486310 | 0.004572 / 0.00486310 | 0.004564 / 0.00486310 |

## Failure sets and entry identity

The six thin-feature failures, flickering-light failure, Quality textured-grain target, Quality upscaling chart
and moving-shadow tail failure are common. Metal also fails the Quality M 1 trailing textured rim's temporal
error. WARP instead fails the follow-stop inner-pixel guard at Quality, phase 8, one pixel against zero.
Vulkan instead fails Native entry identity, one stored lock differing by 1 at frame 47. Its confidence and mark
remain identical. Its colour differs within the already permitted software range, but stored-lock equality is
required and was not weakened. The `precise` arithmetic that fixes Metal and WARP is insufficient for Vulkan.

Complete failed-case names are in each backend's JSON export, with the actual assertion messages and all printed
figures in `logs/hosted-<backend>.txt`. All 46 selected existing thin-feature, chart and rim facts pass with the
original shader sources on local Metal. The archived release comparison's moving-shadow fact passed on all
three device classes at exact 21.4.0.

## Handoff

A different shading-evidence design is required before another production candidate. The two mean lumas alone
cannot distinguish a missed feature from changing illumination. The tested corner refinements still regress
real held detail, flickering light and fast-shadow reconvergence, and the native lock predicate differs across
Vulkan entry points. No bound was raised, no state was added and no package or consumer pin was produced.
The four-condition real-scene capture, 4x character crops and probe repin remain blocked on correctness.
