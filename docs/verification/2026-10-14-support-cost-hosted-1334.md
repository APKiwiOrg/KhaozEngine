# Hosted support cost verdict for #1334

**Verdict: FAIL.** The SG-24 normalized per-row medians pass 77 of 234 timed entries and reject 157.
151 entries exceed a time ceiling. Warm allocation limits pass in every iteration. No iteration passes
all timed entries, so #1334 remains open. SG-21 aggregate actor CPU and consumer adoption remain separate.

## Run identity

- Owner-approved workflow [38552723316](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38552723316).
- `stress-test.yml`, 20 iterations, `load=0`, `support-cost=true`. The workflow succeeded.
- Measured candidate: `376971dda8964849cac1a13fa7cad17a26bd45a5` on engine main.
- Recorded source baseline: `06b734c5722bc2cc95cea459255d6ec93e9236c7`.
- Evaluation checkout: engine main `5960836e8`, branch `fix/support-cost-1334-hosted`.
- GitHub-hosted AMD EPYC 7763 64-Core Processor, X64, Ubuntu 26.04.1 LTS, .NET 10.0.13.
- Release, `DOTNET_TieredCompilation=0`. One first-use sample, one unmeasured priming call and one warm sample per row.
- Inputs: `run-1.json` through `run-20.json` from the workflow artifacts. Each has schema 2, no pending finalization,
  `ProcessExitStatus=0` and `HostedNormalizationPassed=true`.
- Capture interval: `2026-10-14T10:58:28.9841066+00:00` through `2026-10-14T11:24:35.5810059+00:00`.
- These are data-only inputs. Evaluation does not invoke a locomotion operation or repeat the timing proof.

## Evaluation and aggregation

The evaluator loaded the worktree Release test assembly and invoked the existing internal
`SupportCostVerdict.TryHostedScale` and `SupportCostVerdict.Evaluate` methods by reflection.
The methods in [SupportCostArtifact.cs](../../KhaozEngine.Game.Tests/Locomotion/Contacts/SupportCostArtifact.cs)
are unchanged between the measured candidate and this checkout. No verdict formula was substituted.

The reference is the committed `baseline` section of
[2026-10-11-support-gates-cost.json](2026-10-11-support-gates-cost.json), exactly the path used by
`SupportGateCostTests.OneShotCostArtifact`. Its Apple M2 Max Arm64 legacy flat-box warm whole tick is
**23.916 us**. Its recorded candidate is `91a533dd736549cbbe8406fc8db1638ad31b1fc5`,
captured at `2026-10-11T13:58:13.820358+00:00`, finalized with exit 0.
Only that section calibrates these results. Later changed sections do not replace the reference.

All 20 calibrations pass the existing metadata, physical-scene, tuning and moving-tick checks.
Every recalculated scale equals the recorded `HostScale`. All 8,340 sample rows were passed through
`Evaluate`. The hosted geometry preconditions from `SupportCostArtifact.Write` were also applied.
Every non-null recorded `GatePassed` matches the reevaluated result.

SG-12, SG-24 and the verdict code define no cross-iteration aggregation rule. This report therefore
uses **the median per row after dividing each candidate and its paired legacy time by that iteration's k**.
This puts each observation in M2 Max equivalent us before taking a median. The median candidate
and median paired legacy samples were passed back through `Evaluate` with `hostScale=1`.
Deterministic eligibility fields, substep counts, mode segment counts and allocation bytes are identical
across all 20 iterations. Median construction does not repair a refusal, failed geometry or an ineligible tick.

For each timed row, the worst iteration is the largest candidate-to-binding-ceiling ratio, using its own
k and same-invocation legacy. This can differ from the largest raw hosted time. The table shows that
worst candidate and ceiling in equivalent us, its iteration number, and its actual verdict.
Iteration pass counts and the full row pass counts also remain visible. No percentile claim is made.

SG-24 scales absolute time limits only. Byte limits remain unchanged. An ordinary whole tick must
pass both its absolute limit and `legacy_hosted + max(k * 100 us, 0.25 * legacy_hosted)`.
Dense whole ticks have the absolute limit only. SG-12 permits at most three timed substeps for the ordinary
whole-tick row. `Evaluate` also rejects non-comparable, stationary and mixed-mode tick envelopes.
Correct capacity refusals and absent support never become successful cost gates.

## Calibration and iteration disposition

k ranges from **0.616616491 to 1.614107710**, median **0.628156882**.
The full spread is 0.997491219, maximum/minimum 2.618x.
The central half is 0.625648102 to 0.639383676. Iterations 9, 14 and 20 have the highest calibration scales.
This yardstick spread matters to near-ceiling rows. It does not remove the large median overruns.
Iteration 18 has the lowest timed pass count, 73 of 234, with 161 rejections. Iteration 9 has the
highest pass count, 110 of 234. Both fail the complete gate.

| Iteration | Legacy flat-box hosted us | k | Timed pass/234 | SHA-256 of input JSON |
| --- | ---: | ---: | ---: | --- |
| 1 | 15.168 | 0.634219769 | 77 | `540e445cec90357ca8300a9ec2cf18ccd7a9990ba5ea4fcf951c1295a0ce876b` |
| 2 | 14.748 | 0.616658304 | 77 | `1cc1bf62d494315021816a9b2376a2dffaa917fc7a3184aa6666354f51c37d6c` |
| 3 | 14.908 | 0.623348386 | 77 | `a4e204f4d85b60d51144b311deff5bcd23028d9cc30386ea742d03d77fafb630` |
| 4 | 15.279 | 0.638861014 | 76 | `e5014981c4d022ed257b82f4478d673cf5ebb781332caadc253f723c214931af` |
| 5 | 15.028 | 0.628365947 | 77 | `8fac99404d6655bd9969665febfcdc6fb91aed6ae7240bef2d119947ef567408` |
| 6 | 15.018 | 0.627947817 | 76 | `0ec5dcc426b9df87442cf28a21fe9f03ef46255fdcf934a710ecafdf4c217781` |
| 7 | 15.208 | 0.635892290 | 77 | `fe9afe422bca0a59d61031921648e414320e03fd10e6c3cb305dacaae27179d9` |
| 8 | 15.459 | 0.646387356 | 77 | `c4a0939fae248d23dc7f9878e4aacd9382c0b0bb856f8353c51afaae1017b49c` |
| 9 | 38.603 | 1.614107710 | 110 | `ac6cd7d54fa550b38cc81ce784bb129be938721292cc0424b0c7b5d5c43a2854` |
| 10 | 15.017 | 0.627906004 | 77 | `439acb7834063c00354033b651d4a239b544a2eeaf11af32fa9f0bd864e39ff4` |
| 11 | 15.089 | 0.630916541 | 77 | `08a296f635429e1c81d04a88498d1024400f934bcaf4bd9170a9e9f4c0aa18cb` |
| 12 | 14.948 | 0.625020907 | 77 | `5fd4b27c4daba03156408ffd8f4b29da997cb2a7bc6b7349d74bb8f8182b9af1` |
| 13 | 15.329 | 0.640951664 | 76 | `b1633e1a31a6e0158f7b9984d05728af63d3341511f7ed5be051d878f930b1a7` |
| 14 | 21.160 | 0.884763338 | 90 | `af3451c545be26390857447ae3ba4eb654853b614dc495f3e301553745cae933` |
| 15 | 14.979 | 0.626317110 | 76 | `771c23e7f8c330f57fc29bacf63f621ab2ef57807c469738e00c232cb69e3fa4` |
| 16 | 14.968 | 0.625857167 | 75 | `4d413be0dddbb0294204501d7dfb206a04665d3ffe4c1a648705baaceed43d38` |
| 17 | 15.018 | 0.627947817 | 77 | `f5d151b310bc4d9d825bf4d61534a927ac1b4c35f18b05018d350882b7fe13d4` |
| 18 | 14.747 | 0.616616491 | 73 | `e15f31ffc89de54836abe588cd2bb5ed1e2b11cf46206c5aa602f661aeae7443` |
| 19 | 14.928 | 0.624184646 | 75 | `c3934c47e699df99d50ff1672e84c60c2a988a1a4ed19a3631b523ef19c60629` |
| 20 | 23.544 | 0.984445559 | 97 | `f75d8b413462a7c59703491cc98279d7d197c3f67efa21211a7cc30953d45032` |

## Per-row timed verdicts

All table times are M2 Max equivalent us. `C` is candidate, `L` is the paired legacy, `Limit` is the
binding median ceiling and `Margin = Limit - C`. Negative margin is an overrun. Worst is written
`candidate / ceiling @ iteration`. Verdict is median/worst. Pass count is the actual hosted gate count.

`Find` pairs with `LegacyDownPass`. Substeps and whole ticks pair with `LegacyTick`.
`B0` and `B1` mean `GroundSubstep/GroundTickBorrowed/0` and `/1`.
`D0` and `D1` mean `GroundSubstep/DryTick/0` and `/1`.
`E0` and `E1` mean `GroundSubstep/ExplicitTick/0` and `/1`.
Dry and Explicit name the acquired whole ticks. Ordinary Find/substep/tick absolute limits are
50/200/1000 us. Dense limits are 500/1000/4000 us. Ordinary tick limits in the table are the smaller
of the absolute and legacy-relative ceilings. These are existing SG-12 limits, not new budgets.

Failure codes: `T` exceeds a time limit, `G` fails a geometry/oracle/support precondition, `E` is outside
the comparable moving single-mode envelope, and `L` has an unsuccessful ordinary legacy geometry result.
These codes can overlap. They do not label expected refusals or exceptional envelopes as newly found defects.

### Find

| Scene | Operation | Median C | Median L | Limit | Margin | Worst C / limit @ run | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- | ---: |
| flat box | Find | 148.555 | 6.421 | 50.000 | -98.555 | 197.010 / 50.000 @ 2 | FAIL T/FAIL | 0 |
| stairs box | Find | 878.967 | 10.060 | 50.000 | -828.967 | 976.898 / 50.000 @ 3 | FAIL T/FAIL | 0 |
| flat hull | Find | 234.824 | 6.466 | 50.000 | -184.824 | 267.801 / 50.000 @ 8 | FAIL T/FAIL | 0 |
| stairs hull | Find | 977.503 | 15.208 | 50.000 | -927.503 | 1022.585 / 50.000 @ 6 | FAIL T/FAIL | 0 |
| flat mesh | Find | 115.694 | 8.328 | 50.000 | -65.694 | 155.853 / 50.000 @ 19 | FAIL T/FAIL | 0 |
| stairs mesh | Find | 419.626 | 20.861 | 50.000 | -369.626 | 500.574 / 50.000 @ 3 | FAIL T/FAIL | 0 |
| analytic only | Find | 14.268 | 0.659 | 50.000 | 35.732 | 14.884 / 50.000 @ 3 | PASS/PASS | 20 |
| analytic plus props | Find | 123.401 | 3.086 | 50.000 | -73.401 | 199.069 / 50.000 @ 12 | FAIL T/FAIL | 1 |
| hidden crate | Find | 1319.881 | 14.853 | 50.000 | -1269.881 | 1403.978 / 50.000 @ 18 | FAIL T/FAIL | 0 |
| sphere | Find | 145.364 | 1.188 | 50.000 | -95.364 | 191.873 / 50.000 @ 11 | FAIL T/FAIL | 0 |
| capsule | Find | 248.627 | 1.223 | 50.000 | -198.627 | 307.660 / 50.000 @ 1 | FAIL T/FAIL | 0 |
| cylinder | Find | 295.566 | 3.664 | 50.000 | -245.566 | 404.266 / 50.000 @ 16 | FAIL T/FAIL | 0 |
| grid 100 | Find | 316130.628 | 55114.573 | 500.000 | -315630.628 | 323321.982 / 500.000 @ 2 | FAIL T,G/FAIL | 0 |
| fan 96 | Find | 4568.848 | 186.034 | 500.000 | -4068.848 | 4664.595 / 500.000 @ 18 | FAIL T/FAIL | 0 |
| fan 256 | Find | 15993.908 | 478.471 | 500.000 | -15493.908 | 16374.252 / 500.000 @ 6 | FAIL T/FAIL | 0 |
| fan 257 | Find | 9508.005 | 505.359 | 500.000 | -9008.005 | 9903.389 / 500.000 @ 2 | FAIL T,G/FAIL | 0 |
| fan 300 | Find | 10316.764 | 585.270 | 500.000 | -9816.764 | 16222.593 / 500.000 @ 8 | FAIL T,G/FAIL | 0 |
| cluster 8 mesh | Find | 351.300 | 39.719 | 500.000 | 148.700 | 395.772 / 500.000 @ 17 | PASS/PASS | 20 |
| cluster 8 compound | Find | 1058.283 | 51.401 | 500.000 | -558.283 | 1091.158 / 500.000 @ 18 | FAIL T/FAIL | 1 |
| cluster 8 split | Find | 830.156 | 46.967 | 500.000 | -330.156 | 871.123 / 500.000 @ 3 | FAIL T/FAIL | 1 |
| cluster 32 mesh | Find | 1448.885 | 154.031 | 500.000 | -948.885 | 1797.450 / 500.000 @ 6 | FAIL T/FAIL | 0 |
| cluster 32 compound | Find | 4244.382 | 203.067 | 500.000 | -3744.382 | 4401.694 / 500.000 @ 12 | FAIL T/FAIL | 0 |
| cluster 32 split | Find | 3431.182 | 185.275 | 500.000 | -2931.182 | 3646.948 / 500.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 mesh | Find | 7437.692 | 619.480 | 500.000 | -6937.692 | 7607.601 / 500.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 128 compound | Find | 19243.725 | 813.296 | 500.000 | -18743.725 | 19726.569 / 500.000 @ 3 | FAIL T/FAIL | 0 |
| cluster 128 split | Find | 15852.810 | 750.288 | 500.000 | -15352.810 | 16955.057 / 500.000 @ 11 | FAIL T/FAIL | 0 |
| cluster 256 mesh | Find | 19701.574 | 1240.627 | 500.000 | -19201.574 | 21056.235 / 500.000 @ 5 | FAIL T/FAIL | 0 |
| cluster 256 compound | Find | 44772.829 | 1490.212 | 500.000 | -44272.829 | 46299.171 / 500.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 split | Find | 38002.303 | 1488.123 | 500.000 | -37502.303 | 39161.211 / 500.000 @ 2 | FAIL T/FAIL | 0 |
| stacked irrelevant layers | Find | 124.987 | 26.446 | 50.000 | -74.987 | 167.378 / 50.000 @ 16 | FAIL T/FAIL | 0 |
| stationary | Find | 120.346 | 5.981 | 50.000 | -70.346 | 192.479 / 50.000 @ 13 | FAIL T/FAIL | 1 |
| initial | Find | 120.641 | 6.001 | 50.000 | -70.641 | 133.487 / 50.000 @ 13 | FAIL T/FAIL | 1 |
| wall retry | Find | 14.191 | 0.544 | 50.000 | 35.809 | 14.471 / 50.000 @ 16 | PASS/PASS | 20 |
| pushed seat | Find | 122.553 | 2.955 | 50.000 | -72.553 | 162.043 / 50.000 @ 4 | FAIL T/FAIL | 1 |
| descending landing | Find | 122.733 | 3.103 | 50.000 | -72.733 | 203.504 / 50.000 @ 15 | FAIL T/FAIL | 0 |
| slide opposing face | Find | 14.256 | 3.323 | 50.000 | 35.744 | 15.159 / 50.000 @ 18 | PASS/PASS | 20 |
| explicit entry | Find | 14.307 | 0.545 | 50.000 | 35.693 | 15.153 / 50.000 @ 19 | PASS/PASS | 20 |
| explicit rounding handoff | Find | 14.135 | 0.513 | 50.000 | 35.865 | 58.592 / 50.000 @ 1 | FAIL G/FAIL | 0 |
| explicit changed-axis rounding | Find | 14.120 | 0.524 | 50.000 | 35.880 | 19.649 / 50.000 @ 13 | FAIL G/FAIL | 0 |
| explicit wade | Find | 13.976 | 0.548 | 50.000 | 36.024 | 14.494 / 50.000 @ 18 | PASS/PASS | 20 |
| explicit swim | Find | 13.848 | 0.539 | 50.000 | 36.152 | 14.314 / 50.000 @ 18 | FAIL G/FAIL | 0 |
| explicit medium descent retry | Find | 13.855 | 0.522 | 50.000 | 36.145 | 14.283 / 50.000 @ 18 | PASS/PASS | 20 |
| explicit dry swim handoff | Find | 14.070 | 0.532 | 50.000 | 35.930 | 14.849 / 50.000 @ 2 | PASS/PASS | 20 |
| explicit dry wade handoff | Find | 13.969 | 0.531 | 50.000 | 36.031 | 14.476 / 50.000 @ 2 | PASS/PASS | 20 |
| final clamp | Find | 121.891 | 6.060 | 50.000 | -71.891 | 163.225 / 50.000 @ 18 | FAIL T/FAIL | 1 |

### Actual ground substeps

| Scene | Operation | Median C | Median L | Limit | Margin | Worst C / limit @ run | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- | ---: |
| flat box | B0 | 120.757 | 23.916 | 200.000 | 79.243 | 157.793 / 200.000 @ 12 | PASS/PASS | 20 |
| flat box | B1 | 119.346 | 23.916 | 200.000 | 80.654 | 168.336 / 200.000 @ 6 | PASS/PASS | 20 |
| flat box | D0 | 120.688 | 23.916 | 200.000 | 79.312 | 269.237 / 200.000 @ 16 | PASS/FAIL | 17 |
| flat box | D1 | 119.824 | 23.916 | 200.000 | 80.176 | 268.546 / 200.000 @ 19 | PASS/FAIL | 17 |
| stairs box | B0 | 523.831 | 44.271 | 200.000 | -323.831 | 573.046 / 200.000 @ 1 | FAIL T/FAIL | 0 |
| stairs box | B1 | 521.607 | 44.271 | 200.000 | -321.607 | 596.561 / 200.000 @ 19 | FAIL T/FAIL | 0 |
| stairs box | D0 | 530.298 | 44.271 | 200.000 | -330.298 | 705.307 / 200.000 @ 3 | FAIL T/FAIL | 0 |
| stairs box | D1 | 522.549 | 44.271 | 200.000 | -322.549 | 729.736 / 200.000 @ 3 | FAIL T/FAIL | 0 |
| flat hull | B0 | 232.807 | 32.629 | 200.000 | -32.807 | 335.159 / 200.000 @ 13 | FAIL T/FAIL | 3 |
| flat hull | B1 | 234.785 | 32.629 | 200.000 | -34.785 | 399.011 / 200.000 @ 13 | FAIL T/FAIL | 3 |
| flat hull | D0 | 236.555 | 32.629 | 200.000 | -36.555 | 240.292 / 200.000 @ 2 | FAIL T/FAIL | 2 |
| flat hull | D1 | 232.541 | 32.629 | 200.000 | -32.541 | 277.729 / 200.000 @ 19 | FAIL T/FAIL | 3 |
| stairs hull | B0 | 619.218 | 50.498 | 200.000 | -419.218 | 705.076 / 200.000 @ 6 | FAIL T/FAIL | 0 |
| stairs hull | B1 | 607.886 | 50.498 | 200.000 | -407.886 | 690.461 / 200.000 @ 7 | FAIL T/FAIL | 0 |
| stairs hull | D0 | 616.167 | 50.498 | 200.000 | -416.167 | 739.544 / 200.000 @ 2 | FAIL T/FAIL | 0 |
| stairs hull | D1 | 606.427 | 50.498 | 200.000 | -406.427 | 644.165 / 200.000 @ 1 | FAIL T/FAIL | 0 |
| flat mesh | B0 | 148.427 | 39.954 | 200.000 | 51.573 | 191.934 / 200.000 @ 4 | PASS/PASS | 20 |
| flat mesh | B1 | 149.760 | 39.954 | 200.000 | 50.240 | 197.666 / 200.000 @ 1 | PASS/PASS | 20 |
| flat mesh | D0 | 149.534 | 39.954 | 200.000 | 50.466 | 156.238 / 200.000 @ 10 | PASS/PASS | 20 |
| flat mesh | D1 | 148.610 | 39.954 | 200.000 | 51.390 | 180.035 / 200.000 @ 7 | PASS/PASS | 20 |
| stairs mesh | B0 | 297.551 | 60.027 | 200.000 | -97.551 | 372.335 / 200.000 @ 18 | FAIL T/FAIL | 2 |
| stairs mesh | B1 | 324.132 | 60.027 | 200.000 | -124.132 | 371.335 / 200.000 @ 2 | FAIL T/FAIL | 1 |
| stairs mesh | D0 | 305.872 | 60.027 | 200.000 | -105.872 | 370.895 / 200.000 @ 1 | FAIL T/FAIL | 2 |
| stairs mesh | D1 | 327.674 | 60.027 | 200.000 | -127.674 | 364.837 / 200.000 @ 2 | FAIL T/FAIL | 1 |
| analytic only | B0 | 14.933 | 2.928 | 200.000 | 185.067 | 15.380 / 200.000 @ 17 | PASS/PASS | 20 |
| analytic only | B1 | 14.928 | 2.928 | 200.000 | 185.072 | 15.386 / 200.000 @ 2 | PASS/PASS | 20 |
| analytic only | D0 | 14.895 | 2.928 | 200.000 | 185.105 | 15.338 / 200.000 @ 2 | PASS/PASS | 20 |
| analytic only | D1 | 14.972 | 2.928 | 200.000 | 185.028 | 76.184 / 200.000 @ 4 | PASS/PASS | 20 |
| analytic plus props | B0 | 118.840 | 18.427 | 200.000 | 81.160 | 170.986 / 200.000 @ 1 | PASS/PASS | 20 |
| analytic plus props | B1 | 118.214 | 18.427 | 200.000 | 81.786 | 159.880 / 200.000 @ 4 | PASS/PASS | 20 |
| analytic plus props | D0 | 118.616 | 18.427 | 200.000 | 81.384 | 204.347 / 200.000 @ 6 | PASS/FAIL | 19 |
| analytic plus props | D1 | 117.689 | 18.427 | 200.000 | 82.311 | 157.074 / 200.000 @ 6 | PASS/PASS | 20 |
| hidden crate | B0 | 1403.419 | 44.313 | 200.000 | -1203.419 | 1508.211 / 200.000 @ 10 | FAIL T,G/FAIL | 0 |
| hidden crate | B1 | 1776.164 | 44.313 | 200.000 | -1576.164 | 1835.373 / 200.000 @ 18 | FAIL T,G/FAIL | 0 |
| hidden crate | D0 | 1405.527 | 44.313 | 200.000 | -1205.527 | 1698.680 / 200.000 @ 2 | FAIL T,G/FAIL | 0 |
| hidden crate | D1 | 1777.986 | 44.313 | 200.000 | -1577.986 | 2180.803 / 200.000 @ 2 | FAIL T,G/FAIL | 0 |
| sphere | B0 | 133.036 | 10.337 | 200.000 | 66.964 | 162.082 / 200.000 @ 19 | PASS/PASS | 20 |
| sphere | B1 | 134.392 | 10.337 | 200.000 | 65.608 | 138.775 / 200.000 @ 19 | PASS/PASS | 20 |
| sphere | D0 | 134.709 | 10.337 | 200.000 | 65.291 | 160.831 / 200.000 @ 7 | PASS/PASS | 20 |
| sphere | D1 | 136.115 | 10.337 | 200.000 | 63.885 | 174.726 / 200.000 @ 15 | PASS/PASS | 20 |
| capsule | B0 | 234.653 | 11.224 | 200.000 | -34.653 | 267.988 / 200.000 @ 8 | FAIL T/FAIL | 3 |
| capsule | B1 | 237.351 | 11.224 | 200.000 | -37.351 | 263.587 / 200.000 @ 16 | FAIL T/FAIL | 3 |
| capsule | D0 | 236.411 | 11.224 | 200.000 | -36.411 | 295.245 / 200.000 @ 12 | FAIL T/FAIL | 3 |
| capsule | D1 | 239.327 | 11.224 | 200.000 | -39.327 | 296.644 / 200.000 @ 6 | FAIL T/FAIL | 3 |
| cylinder | B0 | 290.398 | 18.932 | 200.000 | -90.398 | 386.000 / 200.000 @ 10 | FAIL T/FAIL | 2 |
| cylinder | B1 | 291.484 | 18.932 | 200.000 | -91.484 | 320.617 / 200.000 @ 19 | FAIL T/FAIL | 2 |
| cylinder | D0 | 290.904 | 18.932 | 200.000 | -90.904 | 352.035 / 200.000 @ 11 | FAIL T/FAIL | 2 |
| cylinder | D1 | 290.315 | 18.932 | 200.000 | -90.315 | 326.392 / 200.000 @ 4 | FAIL T/FAIL | 1 |
| fan 96 | B0 | 8567.764 | 772.719 | 1000.000 | -7567.764 | 8902.708 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| fan 96 | B1 | 8336.741 | 772.719 | 1000.000 | -7336.741 | 8583.017 / 1000.000 @ 17 | FAIL T/FAIL | 0 |
| fan 96 | D0 | 8621.252 | 772.719 | 1000.000 | -7621.252 | 8933.796 / 1000.000 @ 12 | FAIL T/FAIL | 0 |
| fan 96 | D1 | 8387.427 | 772.719 | 1000.000 | -7387.427 | 8680.193 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| fan 256 | B0 | 26419.266 | 3137.975 | 1000.000 | -25419.266 | 27054.604 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| fan 256 | B1 | 26017.519 | 3137.975 | 1000.000 | -25017.519 | 27166.965 / 1000.000 @ 3 | FAIL T/FAIL | 0 |
| fan 256 | D0 | 26481.117 | 3137.975 | 1000.000 | -25481.117 | 27318.207 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| fan 256 | D1 | 26053.311 | 3137.975 | 1000.000 | -25053.311 | 27213.670 / 1000.000 @ 19 | FAIL T/FAIL | 0 |
| cluster 8 mesh | B0 | 354.697 | 123.840 | 1000.000 | 645.303 | 409.270 / 1000.000 @ 17 | PASS/PASS | 20 |
| cluster 8 mesh | B1 | 348.183 | 123.840 | 1000.000 | 651.817 | 380.387 / 1000.000 @ 2 | PASS/PASS | 20 |
| cluster 8 mesh | D0 | 357.791 | 123.840 | 1000.000 | 642.209 | 383.150 / 1000.000 @ 3 | PASS/PASS | 20 |
| cluster 8 mesh | D1 | 352.882 | 123.840 | 1000.000 | 647.118 | 391.057 / 1000.000 @ 15 | PASS/PASS | 20 |
| cluster 8 compound | B0 | 1057.505 | 120.721 | 1000.000 | -57.505 | 1088.396 / 1000.000 @ 18 | FAIL T/FAIL | 3 |
| cluster 8 compound | B1 | 1045.092 | 120.721 | 1000.000 | -45.092 | 1091.507 / 1000.000 @ 2 | FAIL T/FAIL | 3 |
| cluster 8 compound | D0 | 1070.039 | 120.721 | 1000.000 | -70.039 | 1110.942 / 1000.000 @ 19 | FAIL T/FAIL | 3 |
| cluster 8 compound | D1 | 1053.017 | 120.721 | 1000.000 | -53.017 | 1482.528 / 1000.000 @ 7 | FAIL T/FAIL | 3 |
| cluster 8 split | B0 | 841.769 | 113.532 | 1000.000 | 158.231 | 1170.478 / 1000.000 @ 18 | PASS/FAIL | 19 |
| cluster 8 split | B1 | 848.004 | 113.532 | 1000.000 | 151.996 | 1127.438 / 1000.000 @ 18 | PASS/FAIL | 19 |
| cluster 8 split | D0 | 859.424 | 113.532 | 1000.000 | 140.576 | 891.066 / 1000.000 @ 11 | PASS/PASS | 20 |
| cluster 8 split | D1 | 830.046 | 113.532 | 1000.000 | 169.954 | 884.913 / 1000.000 @ 2 | PASS/PASS | 20 |
| cluster 32 mesh | B0 | 1446.756 | 458.679 | 1000.000 | -446.756 | 1479.545 / 1000.000 @ 2 | FAIL T/FAIL | 2 |
| cluster 32 mesh | B1 | 1439.535 | 458.679 | 1000.000 | -439.535 | 1590.135 / 1000.000 @ 4 | FAIL T/FAIL | 2 |
| cluster 32 mesh | D0 | 1456.255 | 458.679 | 1000.000 | -456.255 | 1522.309 / 1000.000 @ 19 | FAIL T/FAIL | 2 |
| cluster 32 mesh | D1 | 1454.910 | 458.679 | 1000.000 | -454.910 | 1483.916 / 1000.000 @ 2 | FAIL T/FAIL | 1 |
| cluster 32 compound | B0 | 4246.230 | 457.057 | 1000.000 | -3246.230 | 4466.726 / 1000.000 @ 12 | FAIL T/FAIL | 0 |
| cluster 32 compound | B1 | 4250.085 | 457.057 | 1000.000 | -3250.085 | 4410.784 / 1000.000 @ 12 | FAIL T/FAIL | 0 |
| cluster 32 compound | D0 | 4269.463 | 457.057 | 1000.000 | -3269.463 | 7434.204 / 1000.000 @ 17 | FAIL T/FAIL | 0 |
| cluster 32 compound | D1 | 4260.928 | 457.057 | 1000.000 | -3260.928 | 5373.945 / 1000.000 @ 17 | FAIL T/FAIL | 0 |
| cluster 32 split | B0 | 3417.762 | 436.048 | 1000.000 | -2417.762 | 3522.976 / 1000.000 @ 15 | FAIL T/FAIL | 0 |
| cluster 32 split | B1 | 3410.641 | 436.048 | 1000.000 | -2410.641 | 3582.928 / 1000.000 @ 5 | FAIL T/FAIL | 0 |
| cluster 32 split | D0 | 3446.464 | 436.048 | 1000.000 | -2446.464 | 3686.851 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 32 split | D1 | 3414.278 | 436.048 | 1000.000 | -2414.278 | 3542.398 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 mesh | B0 | 7347.945 | 1678.336 | 1000.000 | -6347.945 | 7601.999 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 mesh | B1 | 7292.534 | 1678.336 | 1000.000 | -6292.534 | 7477.666 / 1000.000 @ 3 | FAIL T/FAIL | 0 |
| cluster 128 mesh | D0 | 7415.046 | 1678.336 | 1000.000 | -6415.046 | 7595.496 / 1000.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 128 mesh | D1 | 7376.148 | 1678.336 | 1000.000 | -6376.148 | 7691.877 / 1000.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 128 compound | B0 | 18971.497 | 1806.914 | 1000.000 | -17971.497 | 19735.404 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 compound | B1 | 18992.593 | 1806.914 | 1000.000 | -17992.593 | 19479.065 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 compound | D0 | 19068.398 | 1806.914 | 1000.000 | -18068.398 | 19653.148 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 compound | D1 | 19092.863 | 1806.914 | 1000.000 | -18092.863 | 19594.999 / 1000.000 @ 3 | FAIL T/FAIL | 0 |
| cluster 128 split | B0 | 15580.081 | 1743.035 | 1000.000 | -14580.081 | 16322.231 / 1000.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 128 split | B1 | 15562.942 | 1743.035 | 1000.000 | -14562.942 | 16454.505 / 1000.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 128 split | D0 | 15684.765 | 1743.035 | 1000.000 | -14684.765 | 16190.049 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 split | D1 | 15674.069 | 1743.035 | 1000.000 | -14674.069 | 16265.238 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 mesh | B0 | 19148.816 | 3359.903 | 1000.000 | -18148.816 | 19519.208 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 mesh | B1 | 19052.774 | 3359.903 | 1000.000 | -18052.774 | 19419.443 / 1000.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 256 mesh | D0 | 19211.580 | 3359.903 | 1000.000 | -18211.580 | 21543.238 / 1000.000 @ 4 | FAIL T/FAIL | 0 |
| cluster 256 mesh | D1 | 19126.438 | 3359.903 | 1000.000 | -18126.438 | 19940.667 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 compound | B0 | 43960.166 | 3607.068 | 1000.000 | -42960.166 | 45264.440 / 1000.000 @ 15 | FAIL T/FAIL | 0 |
| cluster 256 compound | B1 | 43818.134 | 3607.068 | 1000.000 | -42818.134 | 45201.927 / 1000.000 @ 5 | FAIL T/FAIL | 0 |
| cluster 256 compound | D0 | 43942.194 | 3607.068 | 1000.000 | -42942.194 | 45137.265 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 compound | D1 | 43874.958 | 3607.068 | 1000.000 | -42874.958 | 45091.126 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 split | B0 | 37050.591 | 3544.408 | 1000.000 | -36050.591 | 38122.506 / 1000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 split | B1 | 37109.606 | 3544.408 | 1000.000 | -36109.606 | 38268.539 / 1000.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 256 split | D0 | 36906.212 | 3544.408 | 1000.000 | -35906.212 | 38651.420 / 1000.000 @ 16 | FAIL T/FAIL | 0 |
| cluster 256 split | D1 | 36832.183 | 3544.408 | 1000.000 | -35832.183 | 38716.075 / 1000.000 @ 16 | FAIL T/FAIL | 0 |
| stacked irrelevant layers | B0 | 118.116 | 35.325 | 200.000 | 81.884 | 158.333 / 200.000 @ 16 | PASS/PASS | 20 |
| stacked irrelevant layers | B1 | 117.672 | 35.325 | 200.000 | 82.328 | 147.821 / 200.000 @ 4 | PASS/PASS | 20 |
| stacked irrelevant layers | D0 | 118.561 | 35.325 | 200.000 | 81.439 | 161.178 / 200.000 @ 5 | PASS/PASS | 20 |
| stacked irrelevant layers | D1 | 118.054 | 35.325 | 200.000 | 81.946 | 154.308 / 200.000 @ 13 | PASS/PASS | 20 |
| initial | B0 | 118.707 | 22.986 | 200.000 | 81.293 | 200.404 / 200.000 @ 13 | PASS/FAIL | 19 |
| initial | B1 | 117.600 | 22.986 | 200.000 | 82.400 | 130.211 / 200.000 @ 18 | PASS/PASS | 20 |
| initial | D0 | 119.147 | 22.986 | 200.000 | 80.853 | 175.454 / 200.000 @ 6 | PASS/PASS | 20 |
| initial | D1 | 118.618 | 22.986 | 200.000 | 81.382 | 143.751 / 200.000 @ 5 | PASS/PASS | 20 |
| wall retry | B0 | 29.550 | 3.136 | 200.000 | 170.450 | 30.479 / 200.000 @ 2 | PASS/PASS | 20 |
| wall retry | B1 | 29.328 | 3.136 | 200.000 | 170.672 | 30.706 / 200.000 @ 2 | PASS/PASS | 20 |
| wall retry | D0 | 29.395 | 3.136 | 200.000 | 170.605 | 30.333 / 200.000 @ 2 | PASS/PASS | 20 |
| wall retry | D1 | 29.474 | 3.136 | 200.000 | 170.526 | 30.544 / 200.000 @ 2 | PASS/PASS | 20 |
| pushed seat | B0 | 760.794 | 96.373 | 200.000 | -560.794 | 821.533 / 200.000 @ 13 | FAIL T/FAIL | 0 |
| pushed seat | B1 | 383.987 | 96.373 | 200.000 | -183.987 | 453.391 / 200.000 @ 12 | FAIL T/FAIL | 1 |
| pushed seat | D0 | 781.333 | 96.373 | 200.000 | -581.333 | 853.415 / 200.000 @ 17 | FAIL T,G/FAIL | 0 |
| pushed seat | D1 | 384.115 | 96.373 | 200.000 | -184.115 | 464.626 / 200.000 @ 12 | FAIL T,G/FAIL | 0 |
| descending landing | B0 | 119.859 | 22.968 | 200.000 | 80.141 | 152.464 / 200.000 @ 7 | PASS/PASS | 20 |
| descending landing | B1 | 119.267 | 22.968 | 200.000 | 80.733 | 172.756 / 200.000 @ 17 | PASS/PASS | 20 |
| descending landing | D0 | 117.949 | 22.968 | 200.000 | 82.051 | 162.721 / 200.000 @ 16 | PASS/PASS | 20 |
| slide opposing face | D0 | 14.833 | 2.618 | 200.000 | 185.167 | 25.816 / 200.000 @ 6 | FAIL G/FAIL | 0 |
| explicit entry | B0 | 14.814 | 2.955 | 200.000 | 185.186 | 15.532 / 200.000 @ 2 | PASS/PASS | 20 |
| explicit entry | B1 | 14.954 | 2.955 | 200.000 | 185.046 | 15.598 / 200.000 @ 18 | PASS/PASS | 20 |
| explicit entry | E0 | 42.439 | 2.955 | 200.000 | 157.561 | 58.152 / 200.000 @ 20 | PASS/PASS | 20 |
| explicit entry | E1 | 41.641 | 2.955 | 200.000 | 158.359 | 50.358 / 200.000 @ 8 | PASS/PASS | 20 |
| explicit rounding handoff | E0 | 43.356 | 4.586 | 200.000 | 156.644 | 47.441 / 200.000 @ 2 | PASS/PASS | 20 |
| explicit wade | B0 | 14.890 | 3.002 | 200.000 | 185.110 | 15.305 / 200.000 @ 2 | PASS/PASS | 20 |
| explicit wade | B1 | 14.950 | 3.002 | 200.000 | 185.050 | 15.467 / 200.000 @ 2 | PASS/PASS | 20 |
| explicit wade | E0 | 43.340 | 3.002 | 200.000 | 156.660 | 104.672 / 200.000 @ 12 | PASS/PASS | 20 |
| explicit medium descent retry | B0 | 14.888 | 6.865 | 200.000 | 185.112 | 15.466 / 200.000 @ 2 | PASS/PASS | 20 |
| explicit medium descent retry | B1 | 15.015 | 6.865 | 200.000 | 184.985 | 16.243 / 200.000 @ 10 | PASS/PASS | 20 |
| explicit medium descent retry | E0 | 42.293 | 6.865 | 200.000 | 157.707 | 73.366 / 200.000 @ 12 | PASS/PASS | 20 |
| explicit medium descent retry | E1 | 126.134 | 6.865 | 200.000 | 73.866 | 169.646 / 200.000 @ 6 | PASS/PASS | 20 |
| explicit dry swim handoff | B0 | 14.876 | 2.966 | 200.000 | 185.124 | 15.433 / 200.000 @ 2 | PASS/PASS | 20 |
| explicit dry swim handoff | B1 | 15.012 | 2.966 | 200.000 | 184.988 | 54.107 / 200.000 @ 16 | PASS/PASS | 20 |
| explicit dry swim handoff | E0 | 43.302 | 2.966 | 200.000 | 156.698 | 76.205 / 200.000 @ 20 | PASS/PASS | 20 |
| explicit dry wade handoff | B0 | 14.901 | 2.948 | 200.000 | 185.099 | 15.191 / 200.000 @ 18 | PASS/PASS | 20 |
| explicit dry wade handoff | B1 | 14.957 | 2.948 | 200.000 | 185.043 | 15.321 / 200.000 @ 18 | PASS/PASS | 20 |
| explicit dry wade handoff | E0 | 43.541 | 2.948 | 200.000 | 156.459 | 104.564 / 200.000 @ 16 | PASS/PASS | 20 |
| explicit dry wade handoff | E1 | 43.229 | 2.948 | 200.000 | 156.771 | 89.659 / 200.000 @ 13 | PASS/PASS | 20 |
| final clamp | D0 | 121.605 | 19.681 | 200.000 | 78.395 | 177.058 / 200.000 @ 7 | PASS/PASS | 20 |

### Acquired whole ticks

| Scene | Operation | Median C | Median L | Limit | Margin | Worst C / limit @ run | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- | ---: |
| flat box | Dry | 371.882 | 23.916 | 123.916 | -247.966 | 767.998 / 123.916 @ 16 | FAIL T/FAIL | 0 |
| stairs box | Dry | 3899.023 | 44.271 | 144.271 | -3754.752 | 5252.333 / 144.745 @ 3 | FAIL T,L/FAIL | 0 |
| flat hull | Dry | 713.741 | 32.629 | 132.629 | -581.111 | 755.209 / 133.626 @ 19 | FAIL T/FAIL | 0 |
| stairs hull | Dry | 4396.139 | 50.498 | 150.498 | -4245.641 | 4642.928 / 152.364 @ 2 | FAIL T,L/FAIL | 0 |
| flat mesh | Dry | 414.757 | 39.954 | 139.954 | -274.803 | 445.261 / 140.004 @ 7 | FAIL T/FAIL | 0 |
| stairs mesh | Dry | 1918.511 | 60.027 | 160.027 | -1758.484 | 1980.812 / 160.976 @ 12 | FAIL T,L/FAIL | 0 |
| analytic only | Dry | 47.775 | 2.928 | 102.928 | 55.153 | 113.258 / 102.980 @ 4 | PASS/FAIL | 19 |
| analytic plus props | Dry | 361.158 | 18.427 | 118.427 | -242.731 | 491.068 / 118.651 @ 6 | FAIL T/FAIL | 0 |
| hidden crate | Dry | 5414.305 | 44.313 | 144.313 | -5269.992 | 6169.318 / 146.222 @ 2 | FAIL T,G,L/FAIL | 0 |
| sphere | Dry | 417.582 | 10.337 | 110.337 | -307.245 | 471.565 / 110.559 @ 3 | FAIL T,L/FAIL | 0 |
| capsule | Dry | 752.969 | 11.224 | 111.224 | -641.745 | 884.750 / 111.264 @ 6 | FAIL T,L/FAIL | 0 |
| cylinder | Dry | 900.360 | 18.932 | 118.932 | -781.428 | 965.050 / 119.055 @ 11 | FAIL T/FAIL | 0 |
| grid 100 | Dry | 317902.781 | 88060.333 | 4000.000 | -313902.781 | 325204.105 / 4000.000 @ 2 | FAIL T,G,E/FAIL | 0 |
| fan 96 | Dry | 21631.173 | 772.719 | 4000.000 | -17631.173 | 22157.851 / 4000.000 @ 2 | FAIL T/FAIL | 0 |
| fan 256 | Dry | 68500.599 | 3137.975 | 4000.000 | -64500.599 | 70320.569 / 4000.000 @ 2 | FAIL T/FAIL | 0 |
| fan 257 | Dry | 9665.167 | 2633.319 | 4000.000 | -5665.167 | 10697.505 / 4000.000 @ 1 | FAIL T,G,E/FAIL | 0 |
| fan 300 | Dry | 10446.105 | 3178.466 | 4000.000 | -6446.105 | 10787.061 / 4000.000 @ 18 | FAIL T,G,E/FAIL | 0 |
| cluster 8 mesh | Dry | 1092.668 | 123.840 | 4000.000 | 2907.332 | 1146.416 / 4000.000 @ 19 | PASS/PASS | 20 |
| cluster 8 compound | Dry | 3201.639 | 120.721 | 4000.000 | 798.361 | 3649.406 / 4000.000 @ 7 | PASS/PASS | 20 |
| cluster 8 split | Dry | 2537.303 | 113.532 | 4000.000 | 1462.697 | 2638.283 / 4000.000 @ 2 | PASS/PASS | 20 |
| cluster 32 mesh | Dry | 4362.348 | 458.679 | 4000.000 | -362.348 | 4473.487 / 4000.000 @ 18 | FAIL T/FAIL | 3 |
| cluster 32 compound | Dry | 12756.206 | 457.057 | 4000.000 | -8756.206 | 20086.648 / 4000.000 @ 17 | FAIL T/FAIL | 0 |
| cluster 32 split | Dry | 10274.791 | 436.048 | 4000.000 | -6274.791 | 10805.605 / 4000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 128 mesh | Dry | 22271.892 | 1678.336 | 4000.000 | -18271.892 | 22955.171 / 4000.000 @ 18 | FAIL T/FAIL | 0 |
| cluster 128 compound | Dry | 57107.249 | 1806.914 | 4000.000 | -53107.249 | 58709.102 / 4000.000 @ 3 | FAIL T/FAIL | 0 |
| cluster 128 split | Dry | 46979.713 | 1743.035 | 4000.000 | -42979.713 | 48573.557 / 4000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 mesh | Dry | 57798.873 | 3359.903 | 4000.000 | -53798.873 | 59582.861 / 4000.000 @ 4 | FAIL T/FAIL | 0 |
| cluster 256 compound | Dry | 131553.413 | 3607.068 | 4000.000 | -127553.413 | 135784.627 / 4000.000 @ 2 | FAIL T/FAIL | 0 |
| cluster 256 split | Dry | 110511.416 | 3544.408 | 4000.000 | -106511.416 | 114520.726 / 4000.000 @ 16 | FAIL T/FAIL | 0 |
| stacked irrelevant layers | Dry | 361.510 | 35.325 | 135.325 | -226.186 | 443.228 / 136.377 @ 2 | FAIL T/FAIL | 0 |
| stationary | Dry | 125.140 | 15.756 | 115.756 | -9.385 | 163.662 / 115.748 @ 6 | FAIL T,E/FAIL | 0 |
| initial | Dry | 363.086 | 22.986 | 122.986 | -240.100 | 443.229 / 123.135 @ 5 | FAIL T/FAIL | 0 |
| wall retry | Dry | 76.792 | 3.136 | 103.136 | 26.344 | 112.037 / 103.184 @ 15 | PASS/FAIL | 19 |
| pushed seat | Dry | 2732.833 | 96.373 | 196.373 | -2536.460 | 2874.375 / 198.006 @ 18 | FAIL T,G,L/FAIL | 0 |
| descending landing | Dry | 574.935 | 22.968 | 122.968 | -451.967 | 784.832 / 123.201 @ 2 | FAIL T,E/FAIL | 0 |
| slide opposing face | Dry | 60.972 | 2.618 | 102.618 | 41.646 | 96.685 / 102.616 @ 17 | FAIL G,E,L/FAIL | 0 |
| explicit entry | Explicit | 166.862 | 2.955 | 102.955 | -63.907 | 176.326 / 103.078 @ 5 | FAIL T,E,L/FAIL | 0 |
| explicit rounding handoff | Explicit | 225.411 | 4.586 | 104.586 | -120.825 | 272.415 / 104.543 @ 11 | FAIL T,E/FAIL | 0 |
| explicit changed-axis rounding | Explicit | 238.622 | 4.580 | 104.580 | -134.042 | 288.731 / 104.499 @ 6 | FAIL T,E,L/FAIL | 0 |
| explicit wade | Explicit | 120.891 | 3.002 | 103.002 | -17.889 | 190.765 / 103.013 @ 12 | FAIL T,E,L/FAIL | 0 |
| explicit swim | Explicit | 102.650 | 4.045 | 104.045 | 1.396 | 152.495 / 104.085 @ 6 | FAIL E/FAIL | 0 |
| explicit medium descent retry | Explicit | 241.522 | 6.865 | 106.865 | -134.657 | 413.861 / 106.957 @ 12 | FAIL T,E,L/FAIL | 0 |
| explicit dry swim handoff | Explicit | 142.626 | 2.966 | 102.966 | -39.660 | 160.063 / 103.044 @ 5 | FAIL T,E,L/FAIL | 0 |
| explicit dry wade handoff | Explicit | 166.975 | 2.948 | 102.948 | -64.027 | 239.267 / 103.315 @ 18 | FAIL T,E,L/FAIL | 0 |
| final clamp | Dry | 249.502 | 19.681 | 119.681 | -129.820 | 349.885 / 119.746 @ 1 | FAIL T,E/FAIL | 0 |

## Allocation rows

All warm bytes below are identical across the 20 iterations. Allocation medians and worst samples
are therefore the same. There are no allocation rejections. Times do not scale these limits.

| Operation group | Rows per iteration | Median bytes | Worst bytes | Byte ceiling | Result |
| --- | ---: | ---: | ---: | --- | --- |
| Find | 45 | 0 | 0 | 0 | PASS |
| Actual ground substeps, every parent route | 144 | 0 | 0 | 0 | PASS |
| GroundTickBorrowed, every scene | 45 | 0 | 0 | 0 support-path bytes | PASS |
| DryTick, acquisition and disposal included | 37 | 64 | 64 | 208 | PASS |
| ExplicitTick, acquisition and disposal included | 8 | 312 | 312 | 312 named lifetime bytes | PASS |
| Physics lease acquisition and disposal | 45 | 64 | 64 | Attribution only | Recorded |
| LegacyDownPass | 45 | 0 | 0 | Baseline only | Recorded |
| LegacyTick | 45 | 64 | 64 | Baseline only | Recorded |
| LegacyTreadDownPass | 3 | 0 | 0 | Baseline only | Recorded |

The explicit lifetime inventory is 192 bytes for `MovementQueryLease` and its release binding,
64 bytes for the physics `ReadLease` and 56 bytes for the producer pin, totaling 312 bytes.
Every explicit whole tick matches that ceiling, independent of Find, shell and sample counts.
Matching the permitted fixed token inventory is not a noisy timing borderline case.

## Borderline timing rows

A borderline timing row lies inclusively within 10 percent on either side of its binding ceiling.
Passing and failing sides are both shown. Seven median rows are borderline. One, Explicit swim,
is below its time ceiling but remains ineligible as a comparable ground whole-tick gate.

| Scene | Operation | Median C | Median L | Limit | Margin | Median result |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| cluster 8 compound | B0 | 1057.505 | 120.721 | 1000.000 | -57.505 | FAIL T |
| cluster 8 compound | B1 | 1045.092 | 120.721 | 1000.000 | -45.092 | FAIL T |
| cluster 8 compound | D0 | 1070.039 | 120.721 | 1000.000 | -70.039 | FAIL T |
| cluster 8 compound | D1 | 1053.017 | 120.721 | 1000.000 | -53.017 | FAIL T |
| cluster 32 mesh | Dry | 4362.348 | 458.679 | 4000.000 | -362.348 | FAIL T |
| stationary | Dry | 125.140 | 15.756 | 115.756 | -9.385 | FAIL T,E |
| explicit swim | Explicit | 102.650 | 4.045 | 104.045 | 1.396 | FAIL E |

Across individual iterations, 48 distinct rows enter the borderline band at least once.
The next table reports the closest observation to the ceiling for each row in actual hosted us.
The iteration list retains every observation within the band. This includes calibration outliers,
and does not silently broaden the median aggregation rule.

| Scene | Operation | Example run | Hosted C | Hosted L | Hosted ceiling | Margin | All borderline runs |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| flat box | D1 | 6 | 120.995 | 15.018 | 125.590 | 4.595 | 6 |
| stairs box | B0 | 9 | 330.977 | 28.042 | 322.822 | -8.155 | 9 |
| stairs box | B1 | 9 | 325.508 | 28.042 | 322.822 | -2.686 | 9 |
| stairs box | D0 | 9 | 333.332 | 28.042 | 322.822 | -10.510 | 9 |
| stairs box | D1 | 9 | 333.983 | 28.042 | 322.822 | -11.161 | 9 |
| flat mesh | B0 | 4 | 122.619 | 25.378 | 127.772 | 5.153 | 4, 5 |
| flat mesh | B1 | 1 | 125.364 | 25.127 | 126.844 | 1.480 | 1 |
| flat mesh | D1 | 7 | 114.483 | 25.438 | 127.178 | 12.695 | 7 |
| stairs mesh | B0 | 20 | 186.157 | 37.429 | 196.889 | 10.732 | 14, 20 |
| stairs mesh | B1 | 20 | 201.166 | 37.429 | 196.889 | -4.277 | 20 |
| stairs mesh | D0 | 20 | 188.662 | 37.429 | 196.889 | 8.227 | 20 |
| stairs mesh | D1 | 20 | 204.161 | 37.429 | 196.889 | -7.272 | 20 |
| analytic only | Dry | 4 | 72.356 | 1.904 | 65.790 | -6.566 | 4 |
| analytic plus props | Find | 9 | 75.280 | 1.914 | 80.705 | 5.425 | 9 |
| analytic plus props | D0 | 6 | 128.319 | 11.712 | 125.590 | -2.729 | 6 |
| capsule | D1 | 14 | 173.173 | 7.263 | 176.953 | 3.780 | 14 |
| cylinder | B0 | 20 | 180.367 | 11.652 | 196.889 | 16.522 | 20 |
| cylinder | B1 | 14 | 188.742 | 12.212 | 176.953 | -11.789 | 14, 20 |
| cylinder | D0 | 20 | 181.760 | 11.652 | 196.889 | 15.129 | 20 |
| cylinder | D1 | 14 | 193.622 | 12.212 | 176.953 | -16.669 | 14 |
| fan 257 | Dry | 9 | 6060.176 | 1679.991 | 6456.431 | 396.255 | 9 |
| fan 300 | Dry | 9 | 6530.848 | 2003.886 | 6456.431 | -74.417 | 9 |
| cluster 8 compound | B0 | 1 | 652.306 | 77.004 | 634.220 | -18.086 | 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 15, 16, 17, 18, 19 |
| cluster 8 compound | B1 | 13 | 654.852 | 76.092 | 640.952 | -13.900 | 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 15, 16, 17, 18, 19 |
| cluster 8 compound | Dry | 7 | 2320.629 | 76.162 | 2543.569 | 222.940 | 7 |
| cluster 8 compound | D0 | 5 | 658.419 | 75.972 | 628.366 | -30.053 | 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 15, 16, 17, 18 |
| cluster 8 compound | D1 | 13 | 657.708 | 76.092 | 640.952 | -16.756 | 1, 2, 3, 4, 5, 6, 8, 10, 11, 12, 13, 15, 16, 17, 18, 19 |
| cluster 8 split | Find | 20 | 517.856 | 29.495 | 492.223 | -25.633 | 20 |
| cluster 8 split | B0 | 7 | 573.419 | 71.483 | 635.892 | 62.473 | 7, 19 |
| cluster 32 mesh | B0 | 14 | 910.748 | 292.916 | 884.763 | -25.985 | 14, 20 |
| cluster 32 mesh | B1 | 14 | 890.630 | 292.916 | 884.763 | -5.867 | 14, 20 |
| cluster 32 mesh | Dry | 8 | 2745.381 | 334.975 | 2585.549 | -159.832 | 1, 4, 5, 6, 7, 8, 10, 11, 13, 15, 16, 17 |
| cluster 32 mesh | D0 | 14 | 930.875 | 292.916 | 884.763 | -46.112 | 14, 20 |
| cluster 32 mesh | D1 | 20 | 994.315 | 285.753 | 984.446 | -9.869 | 14, 20 |
| cluster 32 split | Dry | 9 | 6466.619 | 293.938 | 6456.431 | -10.188 | 9 |
| stationary | Find | 9 | 75.321 | 3.717 | 80.705 | 5.384 | 9 |
| stationary | Dry | 17 | 77.965 | 14.567 | 77.362 | -0.603 | 2, 4, 5, 11, 12, 13, 15, 16, 17, 18, 19 |
| initial | Find | 9 | 75.140 | 3.717 | 80.705 | 5.565 | 9 |
| initial | B0 | 13 | 128.449 | 14.507 | 128.190 | -0.259 | 13 |
| wall retry | Dry | 15 | 70.171 | 1.994 | 64.626 | -5.545 | 15 |
| pushed seat | Find | 9 | 79.178 | 1.883 | 80.705 | 1.527 | 9 |
| slide opposing face | Dry | 17 | 60.713 | 1.643 | 64.438 | 3.725 | 17 |
| explicit changed-axis rounding | Explicit | 9 | 148.517 | 2.915 | 164.326 | 15.809 | 9 |
| explicit swim | Explicit | 19 | 65.062 | 2.515 | 64.933 | -0.129 | 1, 2, 3, 4, 5, 7, 8, 11, 12, 13, 15, 16, 17, 18, 19 |
| explicit medium descent retry | Explicit | 9 | 151.943 | 4.339 | 165.750 | 13.807 | 9 |
| explicit dry swim handoff | Explicit | 14 | 91.380 | 1.883 | 90.359 | -1.021 | 14 |
| explicit dry wade handoff | Explicit | 20 | 101.900 | 1.833 | 100.278 | -1.622 | 20 |
| final clamp | Find | 9 | 77.956 | 3.717 | 80.705 | 2.749 | 9 |

SG-24 requires a second owner-approved run before relying on a borderline row for acceptance.
This run fails independently through large overruns. No second run was launched by this evaluation.
Algorithm work is required before a future acceptance proof. Budget changes remain an owner decision.

## Other recorded operation rows

These 183 rows per iteration were also evaluated. The existing method returns false for operations
outside its timed-gate branches. They are baseline, borrowed whole-tick or lease attribution records,
not additional SG-12 timed rejections. `GroundTickBorrowed` has no whole-tick time ceiling in this
code path. Its zero-allocation support path and actual substeps are accounted for above.
All times below are equivalent us. Worst here means maximum normalized time because no time ceiling applies.

| Scene | Operation | Median | Worst @ iteration | Time ceiling |
| --- | --- | ---: | --- | --- |
| flat box | Legacy down | 6.421 | 6.839 @ 18 | N/A |
| flat box | Borrowed | 362.250 | 413.012 @ 2 | N/A |
| flat box | Legacy tick | 23.916 | 23.916 @ 15 | N/A |
| flat box | Lease | 0.592 | 0.829 @ 5 | N/A |
| stairs box | Legacy down | 10.060 | 12.624 @ 4 | N/A |
| stairs box | Borrowed | 1912.391 | 2003.305 @ 19 | N/A |
| stairs box | Legacy tick | 44.271 | 48.890 @ 20 | N/A |
| stairs box | Lease | 0.513 | 0.837 @ 1 | N/A |
| stairs box | Legacy tread | 17.928 | 25.778 @ 19 | N/A |
| flat hull | Legacy down | 6.466 | 9.394 @ 13 | N/A |
| flat hull | Borrowed | 703.682 | 968.697 @ 13 | N/A |
| flat hull | Legacy tick | 32.629 | 42.327 @ 6 | N/A |
| flat hull | Lease | 0.481 | 0.737 @ 4 | N/A |
| stairs hull | Legacy down | 15.208 | 15.772 @ 13 | N/A |
| stairs hull | Borrowed | 2214.339 | 2343.940 @ 1 | N/A |
| stairs hull | Legacy tick | 50.498 | 54.171 @ 19 | N/A |
| stairs hull | Lease | 0.479 | 0.878 @ 4 | N/A |
| stairs hull | Legacy tread | 21.551 | 34.643 @ 1 | N/A |
| flat mesh | Legacy down | 8.328 | 11.863 @ 1 | N/A |
| flat mesh | Borrowed | 417.569 | 457.981 @ 4 | N/A |
| flat mesh | Legacy tick | 39.954 | 45.792 @ 5 | N/A |
| flat mesh | Lease | 0.469 | 0.836 @ 3 | N/A |
| stairs mesh | Legacy down | 20.861 | 21.300 @ 18 | N/A |
| stairs mesh | Borrowed | 1066.967 | 1179.155 @ 8 | N/A |
| stairs mesh | Legacy tick | 60.027 | 88.286 @ 2 | N/A |
| stairs mesh | Lease | 0.464 | 0.744 @ 8 | N/A |
| stairs mesh | Legacy tread | 28.514 | 29.332 @ 3 | N/A |
| analytic only | Legacy down | 0.659 | 0.734 @ 6 | N/A |
| analytic only | Borrowed | 45.420 | 46.660 @ 2 | N/A |
| analytic only | Legacy tick | 2.928 | 3.394 @ 18 | N/A |
| analytic only | Lease | 0.444 | 0.784 @ 4 | N/A |
| analytic plus props | Legacy down | 3.086 | 4.171 @ 4 | N/A |
| analytic plus props | Borrowed | 360.352 | 422.488 @ 1 | N/A |
| analytic plus props | Legacy tick | 18.427 | 19.088 @ 2 | N/A |
| analytic plus props | Lease | 0.505 | 1.366 @ 11 | N/A |
| hidden crate | Legacy down | 14.853 | 18.814 @ 1 | N/A |
| hidden crate | Borrowed | 4476.115 | 4647.210 @ 10 | N/A |
| hidden crate | Legacy tick | 44.313 | 65.810 @ 7 | N/A |
| hidden crate | Lease | 0.478 | 0.837 @ 8 | N/A |
| sphere | Legacy down | 1.188 | 1.804 @ 10 | N/A |
| sphere | Borrowed | 410.409 | 443.871 @ 10 | N/A |
| sphere | Legacy tick | 10.337 | 10.674 @ 18 | N/A |
| sphere | Lease | 0.444 | 0.844 @ 10 | N/A |
| capsule | Legacy down | 1.223 | 1.911 @ 1 | N/A |
| capsule | Borrowed | 730.964 | 780.416 @ 8 | N/A |
| capsule | Legacy tick | 11.224 | 25.103 @ 13 | N/A |
| capsule | Lease | 0.465 | 8.388 @ 1 | N/A |
| cylinder | Legacy down | 3.664 | 5.037 @ 8 | N/A |
| cylinder | Borrowed | 895.074 | 1372.366 @ 10 | N/A |
| cylinder | Legacy tick | 18.932 | 19.675 @ 15 | N/A |
| cylinder | Lease | 0.454 | 0.854 @ 8 | N/A |
| grid 100 | Legacy down | 55114.573 | 56598.845 @ 2 | N/A |
| grid 100 | Borrowed | 326858.442 | 335607.174 @ 18 | N/A |
| grid 100 | Legacy tick | 88060.333 | 90425.217 @ 18 | N/A |
| grid 100 | Lease | 0.497 | 0.553 @ 2 | N/A |
| fan 96 | Legacy down | 186.034 | 231.059 @ 16 | N/A |
| fan 96 | Borrowed | 21525.083 | 22241.912 @ 2 | N/A |
| fan 96 | Legacy tick | 772.719 | 834.761 @ 2 | N/A |
| fan 96 | Lease | 0.504 | 0.877 @ 6 | N/A |
| fan 256 | Legacy down | 478.471 | 527.647 @ 1 | N/A |
| fan 256 | Borrowed | 68427.967 | 70264.586 @ 3 | N/A |
| fan 256 | Legacy tick | 3137.975 | 3286.680 @ 18 | N/A |
| fan 256 | Lease | 0.480 | 0.642 @ 12 | N/A |
| fan 257 | Legacy down | 505.359 | 553.642 @ 11 | N/A |
| fan 257 | Borrowed | 9733.880 | 9974.862 @ 6 | N/A |
| fan 257 | Legacy tick | 2633.319 | 2738.331 @ 16 | N/A |
| fan 257 | Lease | 0.488 | 1.481 @ 7 | N/A |
| fan 300 | Legacy down | 585.270 | 635.111 @ 18 | N/A |
| fan 300 | Borrowed | 10538.847 | 10762.487 @ 17 | N/A |
| fan 300 | Legacy tick | 3178.466 | 3276.703 @ 18 | N/A |
| fan 300 | Lease | 0.485 | 0.753 @ 4 | N/A |
| cluster 8 mesh | Legacy down | 39.719 | 44.472 @ 18 | N/A |
| cluster 8 mesh | Borrowed | 1090.594 | 1225.573 @ 17 | N/A |
| cluster 8 mesh | Legacy tick | 123.840 | 164.329 @ 15 | N/A |
| cluster 8 mesh | Lease | 0.472 | 1.659 @ 10 | N/A |
| cluster 8 compound | Legacy down | 51.401 | 52.606 @ 2 | N/A |
| cluster 8 compound | Borrowed | 3183.685 | 3259.121 @ 2 | N/A |
| cluster 8 compound | Legacy tick | 120.721 | 173.708 @ 18 | N/A |
| cluster 8 compound | Lease | 0.463 | 1.688 @ 2 | N/A |
| cluster 8 split | Legacy down | 46.967 | 67.190 @ 8 | N/A |
| cluster 8 split | Borrowed | 2540.271 | 3209.275 @ 18 | N/A |
| cluster 8 split | Legacy tick | 113.532 | 145.585 @ 6 | N/A |
| cluster 8 split | Lease | 0.464 | 0.511 @ 15 | N/A |
| cluster 32 mesh | Legacy down | 154.031 | 158.677 @ 18 | N/A |
| cluster 32 mesh | Borrowed | 4394.253 | 4498.243 @ 4 | N/A |
| cluster 32 mesh | Legacy tick | 458.679 | 518.226 @ 8 | N/A |
| cluster 32 mesh | Lease | 0.464 | 0.557 @ 17 | N/A |
| cluster 32 compound | Legacy down | 203.067 | 235.059 @ 10 | N/A |
| cluster 32 compound | Borrowed | 12752.620 | 13166.982 @ 12 | N/A |
| cluster 32 compound | Legacy tick | 457.057 | 530.369 @ 19 | N/A |
| cluster 32 compound | Lease | 0.475 | 0.905 @ 11 | N/A |
| cluster 32 split | Legacy down | 185.275 | 267.900 @ 12 | N/A |
| cluster 32 split | Borrowed | 10223.401 | 10451.322 @ 15 | N/A |
| cluster 32 split | Legacy tick | 436.048 | 492.231 @ 19 | N/A |
| cluster 32 split | Lease | 0.473 | 0.594 @ 19 | N/A |
| cluster 128 mesh | Legacy down | 619.480 | 652.556 @ 5 | N/A |
| cluster 128 mesh | Borrowed | 22306.477 | 22934.184 @ 2 | N/A |
| cluster 128 mesh | Legacy tick | 1678.336 | 1728.332 @ 18 | N/A |
| cluster 128 mesh | Lease | 0.486 | 0.877 @ 2 | N/A |
| cluster 128 compound | Legacy down | 813.296 | 850.335 @ 6 | N/A |
| cluster 128 compound | Borrowed | 56781.259 | 58599.381 @ 2 | N/A |
| cluster 128 compound | Legacy tick | 1806.914 | 1846.606 @ 3 | N/A |
| cluster 128 compound | Lease | 0.480 | 0.712 @ 8 | N/A |
| cluster 128 split | Legacy down | 750.288 | 789.277 @ 11 | N/A |
| cluster 128 split | Borrowed | 46702.713 | 48883.388 @ 18 | N/A |
| cluster 128 split | Legacy tick | 1743.035 | 1801.004 @ 2 | N/A |
| cluster 128 split | Lease | 0.480 | 1.632 @ 15 | N/A |
| cluster 256 mesh | Legacy down | 1240.627 | 1265.540 @ 17 | N/A |
| cluster 256 mesh | Borrowed | 58068.215 | 59290.883 @ 18 | N/A |
| cluster 256 mesh | Legacy tick | 3359.903 | 3446.251 @ 18 | N/A |
| cluster 256 mesh | Lease | 0.492 | 1.028 @ 3 | N/A |
| cluster 256 compound | Legacy down | 1490.212 | 1520.962 @ 18 | N/A |
| cluster 256 compound | Borrowed | 131537.329 | 134887.661 @ 18 | N/A |
| cluster 256 compound | Legacy tick | 3607.068 | 4192.057 @ 13 | N/A |
| cluster 256 compound | Lease | 0.516 | 1.025 @ 18 | N/A |
| cluster 256 split | Legacy down | 1488.123 | 1520.114 @ 2 | N/A |
| cluster 256 split | Borrowed | 110973.214 | 113871.469 @ 2 | N/A |
| cluster 256 split | Legacy tick | 3544.408 | 3666.087 @ 2 | N/A |
| cluster 256 split | Lease | 0.510 | 0.819 @ 19 | N/A |
| stacked irrelevant layers | Legacy down | 26.446 | 27.767 @ 2 | N/A |
| stacked irrelevant layers | Borrowed | 355.125 | 445.038 @ 16 | N/A |
| stacked irrelevant layers | Legacy tick | 35.325 | 36.541 @ 18 | N/A |
| stacked irrelevant layers | Lease | 0.463 | 0.639 @ 10 | N/A |
| stationary | Legacy down | 5.981 | 6.208 @ 18 | N/A |
| stationary | Borrowed | 121.277 | 153.334 @ 5 | N/A |
| stationary | Legacy tick | 15.756 | 23.198 @ 17 | N/A |
| stationary | Lease | 0.461 | 0.543 @ 10 | N/A |
| initial | Legacy down | 6.001 | 8.728 @ 7 | N/A |
| initial | Borrowed | 357.117 | 442.060 @ 13 | N/A |
| initial | Legacy tick | 22.986 | 23.428 @ 2 | N/A |
| initial | Lease | 0.464 | 0.527 @ 6 | N/A |
| wall retry | Legacy down | 0.544 | 0.976 @ 15 | N/A |
| wall retry | Borrowed | 74.550 | 77.318 @ 2 | N/A |
| wall retry | Legacy tick | 3.136 | 3.298 @ 16 | N/A |
| wall retry | Lease | 0.424 | 0.518 @ 4 | N/A |
| pushed seat | Legacy down | 2.955 | 3.444 @ 5 | N/A |
| pushed seat | Borrowed | 1301.976 | 1370.252 @ 2 | N/A |
| pushed seat | Legacy tick | 96.373 | 102.849 @ 7 | N/A |
| pushed seat | Lease | 0.452 | 1.251 @ 19 | N/A |
| descending landing | Legacy down | 3.103 | 3.248 @ 2 | N/A |
| descending landing | Borrowed | 360.840 | 411.407 @ 17 | N/A |
| descending landing | Legacy tick | 22.968 | 23.534 @ 5 | N/A |
| descending landing | Lease | 0.452 | 0.509 @ 11 | N/A |
| slide opposing face | Legacy down | 3.323 | 5.728 @ 6 | N/A |
| slide opposing face | Borrowed | 14.483 | 19.606 @ 8 | N/A |
| slide opposing face | Legacy tick | 2.618 | 2.820 @ 7 | N/A |
| slide opposing face | Lease | 0.420 | 0.507 @ 11 | N/A |
| explicit entry | Legacy down | 0.545 | 0.608 @ 15 | N/A |
| explicit entry | Borrowed | 45.168 | 46.807 @ 2 | N/A |
| explicit entry | Legacy tick | 2.955 | 3.207 @ 17 | N/A |
| explicit entry | Lease | 0.433 | 0.719 @ 13 | N/A |
| explicit rounding handoff | Legacy down | 0.513 | 0.557 @ 17 | N/A |
| explicit rounding handoff | Borrowed | 14.373 | 14.817 @ 2 | N/A |
| explicit rounding handoff | Legacy tick | 4.586 | 5.933 @ 15 | N/A |
| explicit rounding handoff | Lease | 0.431 | 0.493 @ 5 | N/A |
| explicit changed-axis rounding | Legacy down | 0.524 | 1.123 @ 12 | N/A |
| explicit changed-axis rounding | Borrowed | 14.493 | 15.810 @ 6 | N/A |
| explicit changed-axis rounding | Legacy tick | 4.580 | 4.843 @ 18 | N/A |
| explicit changed-axis rounding | Lease | 0.429 | 0.481 @ 3 | N/A |
| explicit wade | Legacy down | 0.548 | 0.639 @ 17 | N/A |
| explicit wade | Borrowed | 45.313 | 75.936 @ 13 | N/A |
| explicit wade | Legacy tick | 3.002 | 3.185 @ 18 | N/A |
| explicit wade | Lease | 0.427 | 0.464 @ 8 | N/A |
| explicit swim | Legacy down | 0.539 | 0.576 @ 12 | N/A |
| explicit swim | Borrowed | 14.360 | 14.801 @ 2 | N/A |
| explicit swim | Legacy tick | 4.045 | 4.590 @ 15 | N/A |
| explicit swim | Lease | 0.417 | 0.550 @ 20 | N/A |
| explicit medium descent retry | Legacy down | 0.522 | 0.591 @ 10 | N/A |
| explicit medium descent retry | Borrowed | 45.153 | 46.418 @ 2 | N/A |
| explicit medium descent retry | Legacy tick | 6.865 | 8.074 @ 6 | N/A |
| explicit medium descent retry | Lease | 0.426 | 0.893 @ 17 | N/A |
| explicit dry swim handoff | Legacy down | 0.532 | 0.619 @ 8 | N/A |
| explicit dry swim handoff | Borrowed | 45.313 | 84.858 @ 16 | N/A |
| explicit dry swim handoff | Legacy tick | 2.966 | 3.615 @ 15 | N/A |
| explicit dry swim handoff | Lease | 0.434 | 0.532 @ 4 | N/A |
| explicit dry wade handoff | Legacy down | 0.531 | 0.698 @ 8 | N/A |
| explicit dry wade handoff | Borrowed | 45.336 | 46.226 @ 18 | N/A |
| explicit dry wade handoff | Legacy tick | 2.948 | 3.315 @ 18 | N/A |
| explicit dry wade handoff | Lease | 0.435 | 0.511 @ 5 | N/A |
| final clamp | Legacy down | 6.060 | 6.239 @ 17 | N/A |
| final clamp | Borrowed | 121.349 | 166.501 @ 13 | N/A |
| final clamp | Legacy tick | 19.681 | 20.230 @ 18 | N/A |
| final clamp | Lease | 0.463 | 0.543 @ 8 | N/A |

## Disposition and limits

The approved hosted capture is valid evidence but **does not pass #1334**. The median results reject
36 Find entries, 81 substep entries, 32 dry whole ticks and all 8 explicit whole ticks.
157 total entries reject, with overlapping causes. 151 exceed a time ceiling and 19 fail a geometry
precondition. 14 ordinary whole-tick comparisons have unsuccessful legacy geometry, and 15 whole
ticks are non-comparable. The table preserves the full envelope instead of treating these as passing
ordinary ticks. Expected capacity refusals remain refusals. No production-code defect is inferred
from these classifications alone.

Even successful geometric cases materially exceed the time ceilings. For example, the dense
cluster 256 compound Find has median 44,772.829 us against 500 us, margin -44,272.829 us.
Its paired legacy down pass has median 1,490.212 us. The four ordinary stairs-box substeps are
also time failures. These failures are not explained away by exceptional swimming or refusal rows.

Run success means the test process captured and finalized its artifacts. The recording test does
not assert that every time row passes. The reevaluation found no disagreement with the committed
verdict path. No budget, ceiling, test or production code was changed.

The median choice is explicitly report policy because the specification leaves aggregation open.
The wide calibration spread and borderline observations require caution in a later acceptance run.
SG-21 still requires a separate game-server actor-count and aggregate CPU budget at phase 6.
A per-operation pass would not settle that adoption gate.

## Verification

One focused Release test invocation through `build-slot --label sg-1334` passed **56 tests**,
with zero failures and zero skips. Its filter selected verdict scaling, hosted normalization and
invalid baseline rejection tests. `OneShotCostArtifact` was not selected and no timing proof ran locally.
An ignored temporary reader called the existing verdict methods on saved JSON. Median evaluation
used the same reader and code path. The temporary reader is not a repository code change.

The whole-tree dash, prose, file-size and contributor-instruction guards and the doc-version guard
passed. This is a documentation-only verdict record. No full solution test suite or release was required.

## Corrected calibration (SC-2, SC-3)

This section re-evaluates the same 20 artifacts under rulings SC-2 and SC-3 of
[CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md](../design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md).
The sections above are unchanged and remain the SG-24 verdict as first evaluated.

**Verdict: still FAIL.** Under the corrected scale the ordinary median rows pass 94 of 139 and reject 45.
Five rows are envelope rows, outside both counts. The 90 dense rows carry no time verdict (SC-1) and
pass their allocation limits. #1334 remains open.

### Reference and evaluation

- Reference: section `reference-376971dda` of [2026-10-11-support-gates-cost.json](2026-10-11-support-gates-cost.json),
  recorded at the measured candidate `376971dda8964849cac1a13fa7cad17a26bd45a5` in one SG-20 invocation of
  `SupportGateCostTests.OneShotCostArtifact` on an Apple M2 Max, Arm64, macOS 27.0.1, .NET 10.0.13,
  Release, `DOTNET_TieredCompilation=0`. Captured `2026-10-16T20:39:10.101774+00:00`, finalized with exit 0.
- Its flat box legacy tick is 6.333 us. Against it, the retired single-row `k` would range 2.329 to 6.096
  (median 2.372). The corrected `k` ranges 1.649 to 1.908.
- `k` is the median of hosted/M2 warm time over every legacy row present and successful in both sections,
  matched by scene and operation, with M2 warm time above 0.5 us. Each iteration has 56 such rows.
  Quartiles interpolate at rank (n + 1) p.
- Evaluator: `SupportCostHostedRecordTests.HostedRecordWithCorrectedCalibration`, run with
  `KHAOZ_SUPPORT_COST_HOSTED_RUNS` naming the artifact directory, `KHAOZ_SUPPORT_COST_REFERENCE_COMMIT=376971dda`
  and `KHAOZ_SUPPORT_COST_HOSTED_REPORT` naming the table output. It reads the artifacts in place, selects the
  reference by commit, and calls `SupportCostVerdict.TryHostedScale` and `SupportCostVerdict.Evaluate` per
  iteration with that run's own lifetime inventory and the hosted preconditions of `SupportCostArtifact.Write`.
  It measures nothing.
- Aggregation is unchanged from the section above: each candidate and paired legacy time is divided by its
  own iteration's `k`, the per-row median is taken, and the median row is judged at `k = 1`.
  Warm bytes and envelope classification are identical across all 20 iterations.

### Calibration

| Iteration | k | Rows | Lower quartile | Upper quartile | Ordinary pass | Ordinary reject | Envelope |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1.785998 | 56 | 0.840636 | 2.269917 | 91 | 48 | 5 |
| 2 | 1.657313 | 56 | 0.817132 | 2.191561 | 91 | 48 | 5 |
| 3 | 1.663902 | 56 | 0.822048 | 2.218743 | 91 | 48 | 5 |
| 4 | 1.804506 | 56 | 0.824738 | 2.218031 | 93 | 46 | 5 |
| 5 | 1.653244 | 56 | 0.840236 | 2.246575 | 92 | 47 | 5 |
| 6 | 1.738414 | 56 | 0.823782 | 2.218067 | 93 | 46 | 5 |
| 7 | 1.667620 | 56 | 0.811424 | 2.218471 | 92 | 47 | 5 |
| 8 | 1.908185 | 56 | 0.834330 | 2.306601 | 97 | 42 | 5 |
| 9 | 1.691086 | 56 | 0.829333 | 2.251751 | 91 | 48 | 5 |
| 10 | 1.662579 | 56 | 0.806821 | 2.326501 | 94 | 45 | 5 |
| 11 | 1.666606 | 56 | 0.803184 | 2.165530 | 92 | 47 | 5 |
| 12 | 1.649738 | 56 | 0.811755 | 2.185597 | 90 | 49 | 5 |
| 13 | 1.656057 | 56 | 0.854851 | 2.273178 | 91 | 48 | 5 |
| 14 | 1.740778 | 56 | 0.815293 | 2.185157 | 91 | 48 | 5 |
| 15 | 1.667721 | 56 | 0.811697 | 2.180985 | 92 | 47 | 5 |
| 16 | 1.648725 | 56 | 0.839580 | 2.127818 | 92 | 47 | 5 |
| 17 | 1.651163 | 56 | 0.809286 | 2.168936 | 94 | 45 | 5 |
| 18 | 1.686582 | 56 | 0.847765 | 2.230200 | 92 | 47 | 5 |
| 19 | 1.691741 | 56 | 0.816627 | 2.235761 | 90 | 49 | 5 |
| 20 | 1.759028 | 56 | 0.840616 | 2.185938 | 93 | 46 | 5 |

The interquartile range spans about 0.80 to 2.33 in every iteration. Legacy rows do not move by one
common factor between the M2 Max and the hosted runner. SC-2 names this spread as the cost-if-wrong signal.

### Ordinary rows

Median rows: 94 ordinary pass, 45 ordinary reject, 5 envelope, 90 dense without a time verdict.

| Family | Rows | Pass | Reject | Median C / limit |
| --- | ---: | ---: | ---: | ---: |
| Find | 25 | 16 | 9 | 0.923 |
| Substep | 87 | 76 | 11 | 0.227 |
| Whole tick | 27 | 2 | 25 | 1.267 |

All table times are M2 Max equivalent us. Limits, pair rules, operation abbreviations and failure codes
are those of the per-row section above. `Pass/20` counts the iterations whose own row passed.

| Scene | Operation | Median C | Median L | Limit | C / limit | Verdict | Pass/20 |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| flat box | Find | 56.031 | 2.433 | 50.000 | 1.121 | FAIL T | 0 |
| flat box | B0 | 45.215 | 9.043 | 200.000 | 0.226 | PASS | 20 |
| flat box | B1 | 45.041 | 9.043 | 200.000 | 0.225 | PASS | 20 |
| flat box | Dry | 141.547 | 9.043 | 109.043 | 1.298 | FAIL T | 0 |
| flat box | D0 | 45.456 | 9.043 | 200.000 | 0.227 | PASS | 20 |
| flat box | D1 | 45.822 | 9.043 | 200.000 | 0.229 | PASS | 20 |
| stairs box | Find | 327.139 | 3.792 | 50.000 | 6.543 | FAIL T | 0 |
| stairs box | B0 | 196.743 | 16.731 | 200.000 | 0.984 | PASS | 16 |
| stairs box | B1 | 196.851 | 16.731 | 200.000 | 0.984 | PASS | 13 |
| stairs box | Dry | 1465.532 | 16.731 | 116.731 | 12.555 | FAIL T,L | 0 |
| stairs box | D0 | 198.851 | 16.731 | 200.000 | 0.994 | PASS | 13 |
| stairs box | D1 | 197.290 | 16.731 | 200.000 | 0.986 | PASS | 15 |
| flat hull | Find | 88.094 | 2.436 | 50.000 | 1.762 | FAIL T | 0 |
| flat hull | B0 | 88.502 | 12.247 | 200.000 | 0.443 | PASS | 20 |
| flat hull | B1 | 87.957 | 12.247 | 200.000 | 0.440 | PASS | 20 |
| flat hull | Dry | 269.004 | 12.247 | 112.247 | 2.397 | FAIL T | 0 |
| flat hull | D0 | 88.708 | 12.247 | 200.000 | 0.444 | PASS | 20 |
| flat hull | D1 | 88.317 | 12.247 | 200.000 | 0.442 | PASS | 20 |
| stairs hull | Find | 365.086 | 5.716 | 50.000 | 7.302 | FAIL T | 0 |
| stairs hull | B0 | 233.797 | 19.001 | 200.000 | 1.169 | FAIL T | 0 |
| stairs hull | B1 | 229.459 | 19.001 | 200.000 | 1.147 | FAIL T | 1 |
| stairs hull | Dry | 1656.780 | 19.001 | 119.001 | 13.922 | FAIL T,L | 0 |
| stairs hull | D0 | 227.779 | 19.001 | 200.000 | 1.139 | FAIL T | 1 |
| stairs hull | D1 | 229.242 | 19.001 | 200.000 | 1.146 | FAIL T | 1 |
| flat mesh | Find | 43.110 | 3.109 | 50.000 | 0.862 | PASS | 18 |
| flat mesh | B0 | 56.474 | 15.075 | 200.000 | 0.282 | PASS | 20 |
| flat mesh | B1 | 56.335 | 15.075 | 200.000 | 0.282 | PASS | 20 |
| flat mesh | Dry | 156.204 | 15.075 | 115.075 | 1.357 | FAIL T | 0 |
| flat mesh | D0 | 56.311 | 15.075 | 200.000 | 0.282 | PASS | 20 |
| flat mesh | D1 | 56.112 | 15.075 | 200.000 | 0.281 | PASS | 20 |
| stairs mesh | Find | 157.214 | 7.857 | 50.000 | 3.144 | FAIL T | 0 |
| stairs mesh | B0 | 112.687 | 22.489 | 200.000 | 0.563 | PASS | 20 |
| stairs mesh | B1 | 123.190 | 22.489 | 200.000 | 0.616 | PASS | 20 |
| stairs mesh | Dry | 718.666 | 22.489 | 122.489 | 5.867 | FAIL T,L | 0 |
| stairs mesh | D0 | 115.862 | 22.489 | 200.000 | 0.579 | PASS | 20 |
| stairs mesh | D1 | 123.027 | 22.489 | 200.000 | 0.615 | PASS | 20 |
| analytic only | Find | 5.299 | 0.244 | 50.000 | 0.106 | PASS | 20 |
| analytic only | B0 | 5.615 | 1.101 | 200.000 | 0.028 | PASS | 20 |
| analytic only | B1 | 5.598 | 1.101 | 200.000 | 0.028 | PASS | 20 |
| analytic only | Dry | 17.941 | 1.101 | 101.101 | 0.177 | PASS | 20 |
| analytic only | D0 | 5.596 | 1.101 | 200.000 | 0.028 | PASS | 20 |
| analytic only | D1 | 5.652 | 1.101 | 200.000 | 0.028 | PASS | 20 |
| analytic plus props | Find | 46.700 | 1.160 | 50.000 | 0.934 | PASS | 17 |
| analytic plus props | B0 | 44.894 | 6.911 | 200.000 | 0.224 | PASS | 20 |
| analytic plus props | B1 | 44.614 | 6.911 | 200.000 | 0.223 | PASS | 20 |
| analytic plus props | Dry | 136.080 | 6.911 | 106.911 | 1.273 | FAIL T | 0 |
| analytic plus props | D0 | 44.865 | 6.911 | 200.000 | 0.224 | PASS | 20 |
| analytic plus props | D1 | 44.456 | 6.911 | 200.000 | 0.222 | PASS | 20 |
| hidden crate | Find | 492.239 | 5.576 | 50.000 | 9.845 | FAIL T | 0 |
| hidden crate | B0 | 529.767 | 16.803 | 200.000 | 2.649 | FAIL T,G | 0 |
| hidden crate | B1 | 665.085 | 16.803 | 200.000 | 3.325 | FAIL T,G | 0 |
| hidden crate | Dry | 2033.278 | 16.803 | 116.803 | 17.408 | FAIL T,G,L | 0 |
| hidden crate | D0 | 527.500 | 16.803 | 200.000 | 2.637 | FAIL T,G | 0 |
| hidden crate | D1 | 666.794 | 16.803 | 200.000 | 3.334 | FAIL T,G | 0 |
| sphere | Find | 54.347 | 0.447 | 50.000 | 1.087 | FAIL T | 1 |
| sphere | B0 | 50.326 | 3.901 | 200.000 | 0.252 | PASS | 20 |
| sphere | B1 | 50.721 | 3.901 | 200.000 | 0.254 | PASS | 20 |
| sphere | Dry | 158.258 | 3.901 | 103.901 | 1.523 | FAIL T,L | 0 |
| sphere | D0 | 50.575 | 3.901 | 200.000 | 0.253 | PASS | 20 |
| sphere | D1 | 51.566 | 3.901 | 200.000 | 0.258 | PASS | 20 |
| capsule | Find | 93.953 | 0.461 | 50.000 | 1.879 | FAIL T | 0 |
| capsule | B0 | 88.114 | 4.188 | 200.000 | 0.441 | PASS | 20 |
| capsule | B1 | 89.284 | 4.188 | 200.000 | 0.446 | PASS | 20 |
| capsule | Dry | 280.804 | 4.188 | 104.188 | 2.695 | FAIL T,L | 0 |
| capsule | D0 | 89.315 | 4.188 | 200.000 | 0.447 | PASS | 20 |
| capsule | D1 | 89.815 | 4.188 | 200.000 | 0.449 | PASS | 20 |
| cylinder | Find | 112.065 | 1.386 | 50.000 | 2.241 | FAIL T | 0 |
| cylinder | B0 | 108.597 | 7.133 | 200.000 | 0.543 | PASS | 20 |
| cylinder | B1 | 110.023 | 7.133 | 200.000 | 0.550 | PASS | 20 |
| cylinder | Dry | 340.511 | 7.133 | 107.133 | 3.178 | FAIL T | 0 |
| cylinder | D0 | 109.898 | 7.133 | 200.000 | 0.549 | PASS | 20 |
| cylinder | D1 | 110.509 | 7.133 | 200.000 | 0.553 | PASS | 20 |
| stacked irrelevant layers | Find | 46.697 | 9.957 | 50.000 | 0.934 | PASS | 18 |
| stacked irrelevant layers | B0 | 44.618 | 13.344 | 200.000 | 0.223 | PASS | 20 |
| stacked irrelevant layers | B1 | 44.458 | 13.344 | 200.000 | 0.222 | PASS | 20 |
| stacked irrelevant layers | Dry | 137.141 | 13.344 | 113.344 | 1.210 | FAIL T | 0 |
| stacked irrelevant layers | D0 | 45.154 | 13.344 | 200.000 | 0.226 | PASS | 20 |
| stacked irrelevant layers | D1 | 44.649 | 13.344 | 200.000 | 0.223 | PASS | 20 |
| stationary | Find | 45.267 | 2.250 | 50.000 | 0.905 | PASS | 18 |
| stationary | Dry | 46.930 | 5.903 | 105.903 | 0.443 | FAIL E | 0 |
| initial | Find | 45.287 | 2.255 | 50.000 | 0.906 | PASS | 19 |
| initial | B0 | 44.925 | 8.615 | 200.000 | 0.225 | PASS | 20 |
| initial | B1 | 44.617 | 8.615 | 200.000 | 0.223 | PASS | 20 |
| initial | Dry | 137.583 | 8.615 | 108.615 | 1.267 | FAIL T | 0 |
| initial | D0 | 44.981 | 8.615 | 200.000 | 0.225 | PASS | 20 |
| initial | D1 | 44.695 | 8.615 | 200.000 | 0.223 | PASS | 20 |
| wall retry | Find | 5.320 | 0.201 | 50.000 | 0.106 | PASS | 20 |
| wall retry | B0 | 11.103 | 1.172 | 200.000 | 0.056 | PASS | 20 |
| wall retry | B1 | 11.081 | 1.172 | 200.000 | 0.055 | PASS | 20 |
| wall retry | Dry | 29.004 | 1.172 | 101.172 | 0.287 | PASS | 20 |
| wall retry | D0 | 11.128 | 1.172 | 200.000 | 0.056 | PASS | 20 |
| wall retry | D1 | 11.123 | 1.172 | 200.000 | 0.056 | PASS | 20 |
| pushed seat | Find | 46.122 | 1.110 | 50.000 | 0.922 | PASS | 16 |
| pushed seat | B0 | 288.444 | 36.163 | 200.000 | 1.442 | FAIL T | 0 |
| pushed seat | B1 | 145.989 | 36.163 | 200.000 | 0.730 | PASS | 20 |
| pushed seat | Dry | 1027.824 | 36.163 | 136.163 | 7.548 | FAIL T,G,L | 0 |
| pushed seat | D0 | 294.174 | 36.163 | 200.000 | 1.471 | FAIL T,G | 0 |
| pushed seat | D1 | 144.708 | 36.163 | 200.000 | 0.724 | FAIL G | 0 |
| descending landing | Find | 47.221 | 1.177 | 50.000 | 0.944 | PASS | 16 |
| descending landing | B0 | 45.431 | 8.602 | 200.000 | 0.227 | PASS | 20 |
| descending landing | B1 | 45.133 | 8.602 | 200.000 | 0.226 | PASS | 20 |
| descending landing | Dry | 216.808 | 8.602 | 108.602 | 1.996 | FAIL T,E | 0 |
| descending landing | D0 | 44.504 | 8.602 | 200.000 | 0.223 | PASS | 20 |
| slide opposing face | Find | 5.376 | 1.251 | 50.000 | 0.108 | PASS | 20 |
| explicit entry | Find | 5.363 | 0.204 | 50.000 | 0.107 | PASS | 20 |
| explicit entry | B0 | 5.569 | 1.124 | 200.000 | 0.028 | PASS | 20 |
| explicit entry | B1 | 5.625 | 1.124 | 200.000 | 0.028 | PASS | 20 |
| explicit entry | Explicit | 62.687 | 1.124 | 101.124 | 0.620 | FAIL E,L | 0 |
| explicit entry | E0 | 15.990 | 1.124 | 200.000 | 0.080 | PASS | 20 |
| explicit entry | E1 | 15.681 | 1.124 | 200.000 | 0.078 | PASS | 20 |
| explicit rounding handoff | Explicit | 85.864 | 1.726 | 101.726 | 0.844 | FAIL E | 0 |
| explicit rounding handoff | E0 | 16.327 | 1.726 | 200.000 | 0.082 | PASS | 20 |
| explicit changed-axis rounding | Explicit | 90.023 | 1.723 | 101.723 | 0.885 | FAIL E,L | 0 |
| explicit wade | Find | 5.274 | 0.203 | 50.000 | 0.105 | PASS | 20 |
| explicit wade | B0 | 5.630 | 1.133 | 200.000 | 0.028 | PASS | 20 |
| explicit wade | B1 | 5.629 | 1.133 | 200.000 | 0.028 | PASS | 20 |
| explicit wade | Explicit | 45.766 | 1.133 | 101.133 | 0.453 | FAIL E,L | 0 |
| explicit wade | E0 | 16.272 | 1.133 | 200.000 | 0.081 | PASS | 20 |
| explicit swim | Explicit | 38.660 | 1.521 | 101.521 | 0.381 | FAIL E | 0 |
| explicit medium descent retry | Find | 5.225 | 0.199 | 50.000 | 0.105 | PASS | 20 |
| explicit medium descent retry | B0 | 5.577 | 2.601 | 200.000 | 0.028 | PASS | 20 |
| explicit medium descent retry | B1 | 5.628 | 2.601 | 200.000 | 0.028 | PASS | 20 |
| explicit medium descent retry | Explicit | 91.105 | 2.601 | 102.601 | 0.888 | FAIL E,L | 0 |
| explicit medium descent retry | E0 | 15.857 | 2.601 | 200.000 | 0.079 | PASS | 20 |
| explicit medium descent retry | E1 | 47.282 | 2.601 | 200.000 | 0.236 | PASS | 20 |
| explicit dry swim handoff | Find | 5.281 | 0.207 | 50.000 | 0.106 | PASS | 20 |
| explicit dry swim handoff | B0 | 5.628 | 1.122 | 200.000 | 0.028 | PASS | 20 |
| explicit dry swim handoff | B1 | 5.623 | 1.122 | 200.000 | 0.028 | PASS | 20 |
| explicit dry swim handoff | Explicit | 53.860 | 1.122 | 101.122 | 0.533 | FAIL E,L | 0 |
| explicit dry swim handoff | E0 | 16.226 | 1.122 | 200.000 | 0.081 | PASS | 20 |
| explicit dry wade handoff | Find | 5.256 | 0.202 | 50.000 | 0.105 | PASS | 20 |
| explicit dry wade handoff | B0 | 5.600 | 1.101 | 200.000 | 0.028 | PASS | 20 |
| explicit dry wade handoff | B1 | 5.638 | 1.101 | 200.000 | 0.028 | PASS | 20 |
| explicit dry wade handoff | Explicit | 62.327 | 1.101 | 101.101 | 0.616 | FAIL E,L | 0 |
| explicit dry wade handoff | E0 | 16.195 | 1.101 | 200.000 | 0.081 | PASS | 20 |
| explicit dry wade handoff | E1 | 16.095 | 1.101 | 200.000 | 0.080 | PASS | 20 |
| final clamp | Find | 46.160 | 2.271 | 50.000 | 0.923 | PASS | 19 |
| final clamp | Dry | 93.607 | 7.383 | 107.383 | 0.872 | FAIL E | 0 |
| final clamp | D0 | 45.330 | 7.383 | 200.000 | 0.227 | PASS | 20 |

### Envelope rows

An envelope row is a Find whose outcome and saved oracle outcome are both `None` with the oracle in
agreement, or a stationary substep or whole tick that was not refused, whose every support read matched
its oracle, and whose reads are all steep or absent. `Oracle` shows the saved outcome, or `reads` for a
substep, whose oracle is checked per read.

| Envelope scene | Operation | Outcome | Oracle | Reads | Median C |
| --- | --- | --- | --- | --- | ---: |
| slide opposing face | Dry | Grounded | Steep | Steep Steep Steep Steep | 23.055 |
| slide opposing face | D0 | SupportedSubstep | reads | Steep | 5.583 |
| explicit rounding handoff | Find | None | None | None | 5.349 |
| explicit changed-axis rounding | Find | None | None | None | 5.326 |
| explicit swim | Find | None | None | None | 5.236 |

### Dense rows

Dense rows have no per-operation time verdict (SC-1). Phase 6 judges their time against the SG-21 aggregate
budget. Their allocation limits still gate: zero warm bytes for Find and substeps, 208 bytes for a dry tick.

| Dense scene | Operation | Median C | Median L | Warm bytes | Allocation |
| --- | --- | ---: | ---: | ---: | --- |
| grid 100 | Find | 118854.418 | 20823.553 | 0 | PASS |
| grid 100 | Dry | 119827.464 | 33247.800 | 64 | PASS |
| fan 96 | Find | 1721.303 | 69.974 | 0 | PASS |
| fan 96 | B0 | 3225.756 | 289.581 | 0 | PASS |
| fan 96 | B1 | 3144.572 | 289.581 | 0 | PASS |
| fan 96 | Dry | 8150.617 | 289.581 | 64 | PASS |
| fan 96 | D0 | 3237.371 | 289.581 | 0 | PASS |
| fan 96 | D1 | 3162.544 | 289.581 | 0 | PASS |
| fan 256 | Find | 5993.924 | 181.166 | 0 | PASS |
| fan 256 | B0 | 9964.128 | 1181.170 | 0 | PASS |
| fan 256 | B1 | 9837.651 | 1181.170 | 0 | PASS |
| fan 256 | Dry | 25860.921 | 1181.170 | 64 | PASS |
| fan 256 | D0 | 9950.346 | 1181.170 | 0 | PASS |
| fan 256 | D1 | 9850.685 | 1181.170 | 0 | PASS |
| fan 257 | Find | 3589.134 | 189.854 | 0 | PASS |
| fan 257 | Dry | 3642.749 | 994.140 | 64 | PASS |
| fan 300 | Find | 3915.111 | 220.585 | 0 | PASS |
| fan 300 | Dry | 3936.841 | 1197.961 | 64 | PASS |
| cluster 8 mesh | Find | 132.643 | 14.959 | 0 | PASS |
| cluster 8 mesh | B0 | 133.325 | 46.770 | 0 | PASS |
| cluster 8 mesh | B1 | 131.317 | 46.770 | 0 | PASS |
| cluster 8 mesh | Dry | 405.602 | 46.770 | 64 | PASS |
| cluster 8 mesh | D0 | 135.631 | 46.770 | 0 | PASS |
| cluster 8 mesh | D1 | 132.662 | 46.770 | 0 | PASS |
| cluster 8 compound | Find | 398.190 | 19.278 | 0 | PASS |
| cluster 8 compound | B0 | 398.459 | 45.718 | 0 | PASS |
| cluster 8 compound | B1 | 396.196 | 45.718 | 0 | PASS |
| cluster 8 compound | Dry | 1208.036 | 45.718 | 64 | PASS |
| cluster 8 compound | D0 | 399.706 | 45.718 | 0 | PASS |
| cluster 8 compound | D1 | 396.301 | 45.718 | 0 | PASS |
| cluster 8 split | Find | 312.413 | 17.527 | 0 | PASS |
| cluster 8 split | B0 | 319.940 | 42.882 | 0 | PASS |
| cluster 8 split | B1 | 318.127 | 42.882 | 0 | PASS |
| cluster 8 split | Dry | 955.038 | 42.882 | 64 | PASS |
| cluster 8 split | D0 | 319.342 | 42.882 | 0 | PASS |
| cluster 8 split | D1 | 313.149 | 42.882 | 0 | PASS |
| cluster 32 mesh | Find | 547.679 | 57.994 | 0 | PASS |
| cluster 32 mesh | B0 | 545.156 | 173.104 | 0 | PASS |
| cluster 32 mesh | B1 | 537.001 | 173.104 | 0 | PASS |
| cluster 32 mesh | Dry | 1643.985 | 173.104 | 64 | PASS |
| cluster 32 mesh | D0 | 550.494 | 173.104 | 0 | PASS |
| cluster 32 mesh | D1 | 547.319 | 173.104 | 0 | PASS |
| cluster 32 compound | Find | 1599.180 | 76.312 | 0 | PASS |
| cluster 32 compound | B0 | 1591.164 | 171.627 | 0 | PASS |
| cluster 32 compound | B1 | 1593.562 | 171.627 | 0 | PASS |
| cluster 32 compound | Dry | 4797.387 | 171.627 | 64 | PASS |
| cluster 32 compound | D0 | 1599.564 | 171.627 | 0 | PASS |
| cluster 32 compound | D1 | 1599.453 | 171.627 | 0 | PASS |
| cluster 32 split | Find | 1294.834 | 70.027 | 0 | PASS |
| cluster 32 split | B0 | 1291.829 | 165.309 | 0 | PASS |
| cluster 32 split | B1 | 1283.062 | 165.309 | 0 | PASS |
| cluster 32 split | Dry | 3874.037 | 165.309 | 64 | PASS |
| cluster 32 split | D0 | 1296.650 | 165.309 | 0 | PASS |
| cluster 32 split | D1 | 1287.225 | 165.309 | 0 | PASS |
| cluster 128 mesh | Find | 2800.782 | 232.688 | 0 | PASS |
| cluster 128 mesh | B0 | 2770.704 | 631.454 | 0 | PASS |
| cluster 128 mesh | B1 | 2748.802 | 631.454 | 0 | PASS |
| cluster 128 mesh | Dry | 8412.266 | 631.454 | 64 | PASS |
| cluster 128 mesh | D0 | 2796.508 | 631.454 | 0 | PASS |
| cluster 128 mesh | D1 | 2796.951 | 631.454 | 0 | PASS |
| cluster 128 compound | Find | 7211.105 | 307.217 | 0 | PASS |
| cluster 128 compound | B0 | 7163.865 | 678.382 | 0 | PASS |
| cluster 128 compound | B1 | 7171.713 | 678.382 | 0 | PASS |
| cluster 128 compound | Dry | 21499.275 | 678.382 | 64 | PASS |
| cluster 128 compound | D0 | 7191.378 | 678.382 | 0 | PASS |
| cluster 128 compound | D1 | 7179.649 | 678.382 | 0 | PASS |
| cluster 128 split | Find | 6026.744 | 282.014 | 0 | PASS |
| cluster 128 split | B0 | 5923.237 | 658.080 | 0 | PASS |
| cluster 128 split | B1 | 5898.156 | 658.080 | 0 | PASS |
| cluster 128 split | Dry | 17728.981 | 658.080 | 64 | PASS |
| cluster 128 split | D0 | 5910.839 | 658.080 | 0 | PASS |
| cluster 128 split | D1 | 5898.278 | 658.080 | 0 | PASS |
| cluster 256 mesh | Find | 7414.475 | 467.021 | 0 | PASS |
| cluster 256 mesh | B0 | 7199.881 | 1262.590 | 0 | PASS |
| cluster 256 mesh | B1 | 7169.713 | 1262.590 | 0 | PASS |
| cluster 256 mesh | Dry | 21772.398 | 1262.590 | 64 | PASS |
| cluster 256 mesh | D0 | 7262.728 | 1262.590 | 0 | PASS |
| cluster 256 mesh | D1 | 7206.858 | 1262.590 | 0 | PASS |
| cluster 256 compound | Find | 16872.275 | 560.574 | 0 | PASS |
| cluster 256 compound | B0 | 16598.827 | 1355.039 | 0 | PASS |
| cluster 256 compound | B1 | 16533.753 | 1355.039 | 0 | PASS |
| cluster 256 compound | Dry | 49523.983 | 1355.039 | 64 | PASS |
| cluster 256 compound | D0 | 16546.333 | 1355.039 | 0 | PASS |
| cluster 256 compound | D1 | 16518.493 | 1355.039 | 0 | PASS |
| cluster 256 split | Find | 14276.474 | 561.475 | 0 | PASS |
| cluster 256 split | B0 | 13978.656 | 1329.893 | 0 | PASS |
| cluster 256 split | B1 | 13970.610 | 1329.893 | 0 | PASS |
| cluster 256 split | Dry | 41572.436 | 1329.893 | 64 | PASS |
| cluster 256 split | D0 | 13902.130 | 1329.893 | 0 | PASS |
| cluster 256 split | D1 | 13852.021 | 1329.893 | 0 | PASS |
