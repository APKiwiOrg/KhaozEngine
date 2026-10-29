# TAA shadow visibility cost benchmark request

Status: sections 1 to 7 are completed history. Their results are in `benchmark-result.md` and `benchmark-result-2.md`. Section 8 is the current request and is not dispatched. Every billed T4 run needs fresh explicit owner approval. If approval is declined, the hold is recorded here and integration and release stay with the orchestrator.

## 1. Current evidence

### Correctness

| Gate | Result | Source |
| --- | --- | --- |
| Local Metal, Apple M2 Max | 1,555 of 1,555 passed, 0 failed, 0 skipped | [verification.md](verification.md) |
| Headless, all assemblies | 36,970 passed, 0 failed, 1,475 skipped (GPU facts off) | [verification.md](verification.md) |
| Hosted `cross-platform-gpu` run [39238918090](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39238918090) on `df821a2ad` | completed, success | Actions API |
| Hosted `cross-platform-gpu` run [39265183977](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39265183977) on `058eaea98` | in progress when this request was written, not yet evidence | Actions API |

The local Metal and headless runs tested renderer commit `44a9879b7`. Head `058eaea98` adds only `local-metal-inventory.jsonl` and `verification.md`, so it carries the same renderer and tests.

### Real-scene acceptance

The owner accepted candidate D on 2026-10-19 after the Grimhollow clips ([real-scene.md](real-scene.md)). D ships lock release only, with no large-change response. The shipping-head capture is byte-identical to D. Back15 character grain and dots against the bilinear-only control are Native 0.005474607 / 364 against 0.008027197 / 851, and Quality 0.008704202 / 759 against 0.011052578 / 1166. The three synthetic costs (8 px retreating textured tail, Quality textured grain, Quality M 1.0 leading textured rim) are accepted separately for hardware, WARP and lavapipe in [owner-release-only.md](owner-release-only.md).

### Logical memory

Visibility storage is `GpuPixelFormat.R8UNorm`, one byte per texel (`ShadowVisibilityTargets.Format`). The fix adds three textures at the internal size: the lit-pass signal (`LitVisibilityTex`) and the two history textures (`TemporalVisibilityHistory`).

| Preset at 2560x1440 | Internal size | Texels | Bytes for three R8 textures | MiB |
| --- | --- | ---: | ---: | ---: |
| Native | 2560 x 1440 | 3,686,400 | 3 x 3,686,400 = 11,059,200 | 11,059,200 / 1,048,576 = 10.546875 |
| Quality | 1707 x 960 | 1,638,720 | 3 x 1,638,720 = 4,916,160 | 4,916,160 / 1,048,576 = 4.68841552734375, 4.688416 to six places |

The Quality size follows `TemporalSettings.DisplayOverInternal` (1.5 per axis) and the `MathF.Round` in `Scene3D.ScaledViewport`. 2560 x 0.6666667 = 1706.67 rounds to 1707, and 1440 x 0.6666667 = 960.00003 rounds to 960. The default 3840x2160 render cap does not bite at either preset.

These figures exclude allocation alignment and padding, retired generations held until their drain, and the per-sample MSAA signal (`MsLitVisibilityTex`).

Fallback overhead: no fallback pass was approved. A device with fewer than five colour attachments allocates no lit signal. The resolve then binds a fully lit R8 texture of the same internal size (`TemporalVisibilityHistory.FullyLit`), so the count stays at three textures and the totals above hold. The per-frame history copy still runs on that path. The T4 legs are not expected to take it. The Direct3D 11 backend reports eight attachments (`SimultaneousRenderTargets`). NVIDIA's Vulkan drivers commonly report eight, but no T4 log in this record prints the value.

Gather speed, bandwidth and frame cost remain unmeasured. No timing of this fix exists on any device, and no local timing substitute was run.

## 2. What the existing cost fixtures time

Both fixtures time CPU wall clock per frame over a block of frames, each frame being Begin, draws, PrepareFrame, recording, Submit, `WaitForIdle` and Present. The engine's GPU seam has no timestamp queries, so blocks are the measure, as in earlier T4 records. `TemporalResolveCostPerfGpuTests` also prints the submit-and-drain difference.

Neither fixture enables shadows. `FrontStage.Setup` leaves `ShadowSettings.Mode` at its default `Off`, so `sampleKeyShadow` returns 1.0 at its strength check and `pcfCascade` never runs. The bilinear PCF change (base 3x3 point PCF, 9 fetches, against head bilinear 3x3 folded into a 4x4 footprint, 16 fetches) is not timed by either fixture at either ref.

What the fixtures do reach at head, in a shadowless scene:

- Under temporal rendering the lit passes still write the R8 MRT (a constant 1.0), the resolve reads the current 3x3 and the reprojected previous footprint, and the history copy runs.
- The resolve fixture's `Off` mode allocates neither motion nor visibility at either ref, so its median is a drift control between the base and head runs.
- Head minus base on each temporal mode median is therefore the visibility MRT write, copy and resolve reads in a shadowless scene. Head minus base on "resolve and sharpen at most" is the same change measured against each run's own `Off` median.
- A constant signal may compress better than a real shadow signal, so this traffic is a lower bound for a shadowed scene. That is unverified.
- `TemporalOutlineCostPerfGpuTests` subtracts two temporal modes, which cancels the visibility cost. Its absolute medians at both refs are two more whole-frame temporal samples at 1440p.

The existing fixtures therefore do not cover the whole lit frame. They time the resolve and a shadowless frame. They do not time the bilinear PCF or MRT traffic carrying a real shadow signal. Section 4 proposes the missing case for a separate approval.

## 3. Proposal A: paired run of the existing fixtures

| Item | Value |
| --- | --- |
| Workflow | `.github/workflows/gpu-hosted-bench.yml`, `workflow_dispatch` only, dispatched from `main` (the `gpu-bench` runner group admits the file on main only) |
| Base ref | `6c6529589f0e8c22234abd6973be2d57b60f773e` |
| Head ref | `058eaea98295a529b9f98cafdff92dbb51f40886` |
| Input `ref` | each full SHA above, so a later push cannot change what runs |
| Input `filter` | the string below |
| Input `legs` | `all`, which runs `windows-d3d11` (`gpu-t4-windows`, `direct3d11-native`), `windows-vulkan` (`gpu-t4-windows`, `vulkan-native`) and `linux-vulkan` (`gpu-t4-linux`, `vulkan-native`) |
| Runs | 2 dispatches (base, then head), 3 GPU jobs each, 6 GPU jobs in total. No repeat, no loop. |

Base choice: `6c6529589` is tag `v21.7.1`, the merge-base of `fix/taa-shadow-visibility` with `main`, and the current `origin/main`, all one commit. It is the released engine a consumer runs today and the commit the branch diverges from, so head minus base is exactly this branch. `TemporalResolveCostPerfGpuTests.cs`, `TemporalOutlineCostPerfGpuTests.cs`, `TemporalFixture.cs` and `TemporalAcceptanceScenes.cs` are byte-identical at both refs (`git diff` is empty), so any difference is the renderer. The workflow file is also identical at base and on `main`.

Filter:

```text
FullyQualifiedName~TemporalResolveCostPerfGpuTests|FullyQualifiedName~TemporalOutlineCostPerfGpuTests
```

It selects the same 10 cases per leg at both refs:

| Class | Scenes | Presets | Displays |
| --- | --- | --- | --- |
| `TemporalResolveCostPerfGpuTests` | twelve keyed boxes, moving thin field | Native, Quality | 2560x1440, 3456x2234 (8 cases) |
| `TemporalOutlineCostPerfGpuTests` | twelve keyed boxes, outline on and off | Native, Quality | 2560x1440 (2 cases) |

Dispatch commands, to run only after approval:

```bash
gh workflow run gpu-hosted-bench.yml --ref main -f ref=6c6529589f0e8c22234abd6973be2d57b60f773e -f filter='FullyQualifiedName~TemporalResolveCostPerfGpuTests|FullyQualifiedName~TemporalOutlineCostPerfGpuTests' -f legs=all
gh workflow run gpu-hosted-bench.yml --ref main -f ref=058eaea98295a529b9f98cafdff92dbb51f40886 -f filter='FullyQualifiedName~TemporalResolveCostPerfGpuTests|FullyQualifiedName~TemporalOutlineCostPerfGpuTests' -f legs=all
```

Result record: per leg and case, head minus base for every mode median, for "resolve and sharpen at most", for the submit-and-drain difference and for the `Off` drift control, each beside the paired-round spread. Earlier same-fixture pairs on the T4 (runs 39000442514 and 39000447833, 39068635433 and 39071519186) moved resolve cases by about -0.15 to +0.27 ms, mostly within the paired-round spread, so one pair cannot resolve smaller differences. Only the `gpu-hosted-bench-<leg>` artifacts are fetched, under `/tmp/khaozengine-taa-visibility-bench/`, and deleted once read.

No acceptance budget: the owner has not set a cost budget for this fix, and this request proposes none. The resolve fixture prints the temporal resolve design's 1.0 ms resolve-and-sharpen figure at 1440p. That figure is not an acceptance budget for this fix.

### Duration

From the four earlier dispatches of the resolve filter: the Linux job took 2.8 to 3.1 minutes, with the test step about 1 minute. Windows jobs took 6.8 to 12.4 minutes, except one Windows job in three of the four dispatches at 33 to 47 minutes. In the two runs whose steps were read, that time went to the GRID driver step (26.6 minutes) or the test step (26.7 minutes). Both Windows legs share one capped machine, so they run one after the other. The two outline cases add an estimated minute or less per leg.

| Span | Typical | Ceiling |
| --- | --- | --- |
| One dispatch, wall clock | 40 to 55 minutes | 120 minutes (two Windows jobs at the 60 minute timeout) |
| Both dispatches, wall clock | 75 to 90 minutes (earlier pairs took 74 and 84) | 240 minutes |

### Estimated bill

Rates are GitHub's published GPU-powered larger runner rates from [Actions runner pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing), read on 2026-10-20: Linux 4-core GPU (`linux_4_core_gpu`) $0.052 a minute and Windows 4-core GPU (`windows_4_core_gpu`) $0.102 a minute. GitHub rounds each job up to a whole minute. The org's hosted runner API lists `gpu-t4-linux` and `gpu-t4-windows` as size `gpu-t4-4-core`, each capped at one runner. The `legs` job runs on `ubuntu-latest`, a standard runner, free in this public repository.

Earlier job minutes priced at today's rates:

| Run | Windows minutes | Linux minutes | Estimate |
| --- | ---: | ---: | ---: |
| 39068635433 | 7 + 34 | 3 | $4.34 |
| 39071519186 | 9 + 34 | 3 | $4.54 |
| 39000442514 | 47 + 8 | 4 | $5.82 |
| 39000447833 | 7 + 13 | 3 | $2.20 |

Estimate for this request: $2.20 to $5.82 a dispatch, $4.40 to $11.64 for the pair, about $9 typical. Ceiling at every job's 60 minute timeout: 2 x 60 x $0.102 + 60 x $0.052 = $15.36 a dispatch, $30.72 for the pair. This is an estimate. GitHub's billing is authoritative.

## 4. Proposal B: isolated whole-frame shadowed cost case, separate later approval

Authored in plan Task 7f.4 and not run. Not part of Proposal A's approval. The billed request belongs to Task 7f.6.

As authored:

- Class `TemporalShadowedFrameCostPerfGpuTests` in `KhaozEngine.Render.Tests/Gpu/TemporalShadowedFrameCostPerfGpuTests.cs`, cases `TheShadowedYardAtQualityIsMeasuredAt1440p` and `TheShadowedYardAtNativeIsMeasuredAt1440p`, both at 2560x1440. It uses only API present at `6c6529589`.
- Base side: branch `fix/taa-vis-shadow-cost-base`, tip commit `562033e74` (a diagnostic base that is never merged, named `fix/` because the engine push hook allows no `probe/` branches), whose two commits add only that file to `6c6529589`. The timed file is byte-identical on both branches, SHA-256 `093f926314100526d85c6f90219d833aa12c30bc735f14e391ebd9ca3c26ff33` at `562033e74` and at the head. The earlier `10478d68c` carries an older text of the file and is not the base.
- Scene: the textured ground under a still follow camera at boot pitch, 49 crates and thin posts and 8 walkers with their own motion, a low key light swaying about a fixed direction, `ShadowMode.ShadowMap`.
- Modes per round: off without shadows (drift control), FXAA with shadows, fused and split each with and without shadows. 15 rotating rounds of 12 frames after 3 warm frames. Printed only, no budget asserted.
- It also prints how often the key light's depth pass rendered, the resolve's counts and, from one frame rendered with anti-aliasing off with shadows on and off, the share of display pixels the shadow darkens and the share within 2 display pixels of a shadow edge (`PrintShadowCoverage`). That coverage is not the share of pixels that hold a lock or run the visibility check.
- Lock and check share, head only: `TemporalShadowedFramePathShareGpuTests` renders the same yard through the cost case's own scene builder, outside the timed class, with the resolve built over the test-only path export. It prints the share of display pixels gated off by a zero lock (R1), gated otherwise, skipped by range (R4), computing a change on the fast path, on the slow path by reason, binade included, and released, at Native and Quality, fused and split, on frame 90, and asserts that the exporting scene stores the same locks as a production scene rendered beside it. It is a readback, not a timing, and the base has no path export to read. On Metal (Apple M2 Max) at 2560x1440, with the binade rule, fused and split alike: at Native a zero lock gates 99.00% of display pixels and the check runs on 1.00%, fast 0.99% (range skips 0.91%) and slow 0.01%, none of it at a binade. At Quality a zero lock gates 95.92% and the check runs on 4.08%, fast 3.39% (range skips 3.11%) and slow 0.69%, of which 0.63% at a binade. So the shadowed yard is lock-poor, and its timing understates the check's cost on a lock-rich frame. The binade share depends on the scene, here about 15% of the checked pixels at Quality, so the result quotes the T4 path share, binade included, beside the timing rows it explains. Its T4 numbers come from the bench run itself.
- Register pressure, a risk for the bench to read on the `windows-d3d11` leg: FXC's `dcl_temps` grows with the fast path. These are DXBC temp counts, so they speak for the Direct3D 11 leg only. The two Vulkan legs compile SPIR-V, and this record has no equivalent register count for them. The full programs as hosted run 39374926165 printed them declare 44 temps against D's 30 (`TemporalAccumulateFrag`), 45 against 31 (`TemporalAccumulateHalfFrag`), 42 against 27 (`TemporalAccumulateAtDisplaySizeFrag`), 43 against 29 (`TemporalAccumulateAtDisplaySizeHalfFrag`), 47 against 41 (fused `TemporalResolveFrag`) and 49 against 43 (`TemporalResolveHalfFrag`), and the decision helper alone 28 against 12. DXBC temps are not the hardware's registers, since the driver allocates its own, but where its allocation grows the same way, lower occupancy costs every pixel the program shades, the lock-gated ones included, and no fetch count shows it.
- Filter for this class alone: `FullyQualifiedName~TemporalShadowedFrameCostPerfGpuTests`.
- Task 7f.6 bench filters. Head: `FullyQualifiedName~TemporalResolveCostPerfGpuTests|FullyQualifiedName~TemporalOutlineCostPerfGpuTests|FullyQualifiedName~TemporalShadowedFrameCostPerfGpuTests|FullyQualifiedName~TemporalVisibilityEquivalenceGpuTests|FullyQualifiedName~TemporalShadowedFramePathShareGpuTests|FullyQualifiedName~TemporalVisibilityResolveAbGpuTests`. Base: `FullyQualifiedName~TemporalResolveCostPerfGpuTests|FullyQualifiedName~TemporalOutlineCostPerfGpuTests|FullyQualifiedName~TemporalShadowedFrameCostPerfGpuTests`. The three head-only classes are correctness readbacks outside the timed collection, which runs alone. `TemporalVisibilityResolveAbGpuTests` is there at review 7f r3's suggestion as the T4 oracle over the production programs: the production resolve and the frozen D resolve built through the test hook, fused and split, Native and Quality, must write byte-identical history every frame, its large frames across columns 512 and 1024 included. `dotnet test --list-tests` lists 18 entries under the head filter, the path share theory as one entry with four rows when run, and 12 under the base filter on `fix/taa-vis-shadow-cost-base`.

The proposal as first written:

- Purpose: time the whole lit frame with the key shadow on, so the bilinear PCF, the R8 MRT carrying a real signal and the history copy are all inside the measured frame.
- Shape: a new GPU perf class with the existing block discipline (15 rounds, rotating order, printed and never asserted, software rasterisers return before measuring). `ShadowMode.ShadowMap` with the key light covering a receiver that fills most display pixels, moving casters so the signal changes, at 2560x1440 Native and Quality. Modes per round: temporal with shadows, FXAA with shadows, temporal without shadows.
- Pairing: base and head run the same test source. Head minus base in the FXAA shadowed mode is the PCF change. Head minus base in the temporal shadowed mode is the whole change. The class does not exist at `v21.7.1`, so the base side needs a branch at `6c6529589` that adds only that test file. That branch, its commit and the test are implementation work for that approval.
- Cost: one more pair of dispatches on the same three legs, expected close to Proposal A's duration and bill. Exact figures belong to that request.

## 5. Approval block

```text
What will run: gpu-hosted-bench.yml, the existing TemporalResolveCostPerfGpuTests (8 cases: boxes and moving field, Native and Quality, 2560x1440 and 3456x2234) and TemporalOutlineCostPerfGpuTests (2 cases: Native and Quality at 2560x1440). Filter FullyQualifiedName~TemporalResolveCostPerfGpuTests|FullyQualifiedName~TemporalOutlineCostPerfGpuTests, legs=all. Printed timings only, no asserted budget. The owner has set no cost budget for this fix.
Where: GitHub-hosted NVIDIA Tesla T4 larger runners. windows-d3d11 and windows-vulkan on gpu-t4-windows, linux-vulkan on gpu-t4-linux, dispatched from main.
How many runs: 2 dispatches, 6 GPU jobs in total. Base 6c6529589f0e8c22234abd6973be2d57b60f773e (v21.7.1, the merge-base with main), head 058eaea98295a529b9f98cafdff92dbb51f40886. No repeats, no loop.
Roughly how long: 40 to 55 minutes a dispatch, 75 to 90 minutes for both, ceiling 240 minutes at the 60 minute job timeouts.
Roughly what it costs: about $4.40 to $11.64 for the pair, about $9 typical, ceiling $30.72. Estimated from GitHub's published $0.102 a minute Windows 4-core GPU and $0.052 a minute Linux 4-core GPU rates applied to earlier runs of the same fixture.
Not covered: the bilinear PCF and a real shadow signal. Those need a separate shadowed whole-frame cost case, a later approval.
```

Result: [benchmark-result.md](benchmark-result.md) records the paired run.

## 6. Historical decomposition and NVIDIA Direct3D 11 diagnostic requests

These requests were approved and ran as 39497171271 and 39497191900. Section 7 supersedes their comparison
rule and variants. The historical input ref is
`c3842b762d8d381719bef00091520e1d000c1604`, pushed and verified against the remote branch before writing this
section. It contains the test-only NVIDIA comparison rule (`e639ef2d6`) and decomposition harness (`c3842b762`).
The later request-record commit changes documentation only. No production code, shader hash table, bound or
baseline changes in this work.

### A. One head-only cost decomposition dispatch

```bash
gh workflow run gpu-hosted-bench.yml --repo APKiwiOrg/KhaozEngine --ref main -f ref=c3842b762d8d381719bef00091520e1d000c1604 -f filter='FullyQualifiedName~TemporalVisibilityCostDecompositionCostPerfGpuTests' -f legs=all
```

One dispatch, three GPU jobs, `windows-d3d11`, `windows-vulkan` and `linux-vulkan`. No base dispatch, repeat or
loop. The class name contains `CostPerf` so the plan's unchanged `TAA_CORRECTNESS` selection excludes it.

All four requested cases remain, twelve keyed boxes and the moving thin field at 2560x1440 Native and Quality.
The harness calls the original cost fixture's scene builders. Each case interleaves nine modes in rotating order,
Off at the internal size and the four programs below on each of the fused and split entries. Every mode owns a
synchronized copy of the scene and history, settles for ten frames and then takes fifteen blocks of twelve
frames after two warm frames. This keeps the moving geometry, draw frame, effect clock and jitter sequence
aligned across variants in every round. Shader compilation and target creation precede the timed blocks.

| Variant | Test-only injection |
| --- | --- |
| V0 shipped | The normal scene renderer with no fragment rewrite. |
| V1 check removed | `TemporalVisibilityCostPrograms.WithoutCheck` replaces the call-site expression with `bool visibilityReleased = false`, without evaluating the check. All mask source stays shipped. |
| V2 check and mask removed | V1 plus `WithoutCheckOrMask`, which removes the unused visibility helper and uses `TemporalVisibilityFrozenDMask.Restore` to restore the pre-7f.3 prepare loop, fused loop, surface and zero surface w store. |
| V3 D | `TemporalVisibilityFrozenD.ToD` restores all frozen D programs, including the mask changes. |

Rewritten renderers are installed before the first resolve by reflection over `Scene3D._temporalResolve`, using
the existing `fragmentSourceForTests` constructor hook. No production seam changes. Every variant retains the
head's R8 MRT write and history copy, so this does not isolate those costs. In a fused V1 program the compiler
may remove the now-unused mask too. V1 minus V2 measures the compiled programs' difference, which may be zero
there, rather than forcing dead ALU to stay alive with a synthetic output.

Output follows the original median-of-blocks method, mode medians, resolve and sharpen at most over Off,
paired round median and min to max, block values and submit-and-drain difference. For each entry it also prints
V0 minus V1 (check), V1 minus V2 (mask) and V0 minus V3 (7f against D), plus the Off drift control. No budget is
asserted. This is the shadowless fixture, so current visibility is constant 1.0 and no PCF work is timed.

No trimming is needed for the 60 minute per-job timeout. [Bench 2](benchmark-result-2.md#1-provenance) measured
the complete cost filter's test step at 2.11 to 2.31 minutes on D3D11, 1.41 to 1.57 minutes on Windows Vulkan
and 1.27 to 1.42 minutes on Linux Vulkan. Its complete jobs took 9:15 to 10:38, 22:46 to 33:40 and 3:09 to
3:30 respectively. This request has 36 mode/case combinations and 6,480 timed frames against bench 2's 58 and
10,440, but creates more fixture copies and compiles more programs. The estimates below allow for that extra
setup, approximately three to ten minutes of tests and compilation, while retaining bench 2's driver/setup
variability. They are planning estimates, not new measurements.

| Leg | Expected complete job duration | Rounded billed minutes | Estimated bill |
| --- | --- | --- | --- |
| windows-d3d11 | 12 to 18 minutes | 12 to 18 | $1.22 to $1.84 |
| windows-vulkan | 25 to 40 minutes | 25 to 40 | $2.55 to $4.08 |
| linux-vulkan | 4 to 6 minutes | 4 to 6 | $0.21 to $0.31 |
| Dispatch total | Windows legs serialize, about 37 to 58 minutes plus queue time | 37 to 58 Windows, 4 to 6 Linux | $3.98 to $6.23 |

Rates are the same published rates used in the earlier request, Windows GPU $0.102 per minute and Linux GPU
$0.052 per minute, with each job rounded up, from [Actions runner pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing).
All three jobs reaching the workflow's 60 minute timeout would cost at most $15.36, excluding queue time, which
is not a running GPU job. Billing is authoritative. No cost or timing test has run locally for this request.

### B. One NVIDIA Direct3D 11 correctness dispatch

```bash
gh workflow run gpu-hosted-bench.yml --repo APKiwiOrg/KhaozEngine --ref main -f ref=c3842b762d8d381719bef00091520e1d000c1604 -f filter='FullyQualifiedName~TemporalVisibilityResolveAbGpuTests|FullyQualifiedName~TemporalEntryIdentityGpuTests' -f legs=windows-d3d11
```

One dispatch, one GPU job, twenty correctness cases on the hardware NVIDIA Direct3D 11 device. This filter
selects no cost, timing, perf or bench test. Bench 2's D3D11 oracle job took 16:34, with 1.5967 minutes in its
test step. This selection replaces the helper equivalence and yard-path cases with entry identity, adds D's
opposite entry to each A/B case and D's two entries to the walk and moving-shadow cases. Allow 17 to 25 minutes
for the complete job, including the observed Windows setup variability and added controls. At $0.102 per
rounded minute that is about $1.73 to $2.55. The 60 minute timeout ceiling is $6.12. These are estimates only.

Owner ruling, 2026-10-21, implemented only when the backend is `Direct3D11Native`, the device reports hardware,
the existing device-class and software-name helpers exclude software, and the adapter name contains NVIDIA.
D's fused and split programs measure the maximum difference in representable half-float steps separately for
RGBA and confidence across the same frame set. Production versus the matching D entry, and production fused
versus split in entry identity, must stay inside that measured envelope. A zero channel envelope requires exact
bits, including signed zero. Stored lock channels, R8 and depth stay exact. No fixed epsilon is introduced.
Every NVIDIA frame prints D fused versus split and production versus D counts, maximum steps per channel and
whether any lock differs. The running and final envelopes are printed. The complete frame-set envelope is
measured before accepting arithmetic differences, so the first failing frame is judged against the same bound
as the last. A/B failures on every backend identify the zero-based frame, and aggregate coverage failures name
the final frame as an aggregate check.

Input identity was checked in the A/B rig. One `Frame` is generated once per frame and supplied to every rig,
including the new opposite-entry D control, with the same current colour, motion, NDC depth, history colour,
confidence, locks, previous depth, R8 sequence and uniforms. Each renderer's actual uniforms are asserted equal
to that shared value. The separate original fused and split theory rows use different random seeds, so their
inputs as whole frame sets were not identical. The new envelope never compares those separate rows. It
compares both D entries inside each row on the same inputs. Thus the historical 0.06293 versus 0.06219 report
alone was not proof of identical inputs across the two original rows.

Every other device retains its existing comparison, strict byte identity except the pre-existing software
Vulkan colour rule in entry identity, whose bounds remain unchanged. The single authorized local Metal run
passed all twenty cases, zero failed and zero skipped, on Apple M2 Max in 11.4337 seconds. All twelve A/B
cases printed byte-identical colour, state, R8 and depth. Every walk printed no colour, confidence, lock or mark
differences. The NVIDIA branch remains unverified on hardware until this approved T4 request runs.

### Local verification, no performance execution

- Release solution build passed with zero warnings and zero errors after correcting a missing namespace import
  in the new compile test.
- Device-free selection passed 103 of 103, including half-step distance across zero and binades, per-channel
  maxima, exact locks, exact zero envelopes, non-finite rejection, frozen D pins, existing shader contracts and
  compilation of every rewritten program through both backend emitters.
- Discovery through `build-slot` listed four decomposition cases. The unchanged `TAA_CORRECTNESS` filter listed
  1,343 entries with no cost, perf or bench case. Neither discovery executed a test.
- The one focused Metal run passed 20 of 20. No other GPU test, timing, perf or bench test ran.
- `dotnet format KhaozEngine.slnx --verify-no-changes --no-restore` returned zero. It emitted its workspace-load
  warning summary. The Release build itself emitted zero warnings.
- Whole-tree dash, prose and file-size guards and `git diff --check` passed. Production shader sources, hash
  tables and baselines are unchanged.

Integration, any billed execution, merge and release remain with the owner and orchestrator.

## 7. Historical outside-cost and T4 verification requests, completed

Completed history, not a pending action. The owner approved both requests on 2026-10-21 and both ran on
`5ce5454d6a2b719b1448dd95bf0491145a6a945c`, the head with the pass-one mask withdrawn: A as
[39519219228](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39519219228) and B as
[39519233674](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39519233674). Do not dispatch these commands
again. The text below is kept as written before dispatch, as evidence.

- A's results are in [benchmark-result-2.md section 7](benchmark-result-2.md#7-historical-outside-cost-decomposition-run-39519219228).
  Its V5 and V4 variants are retired.
- B found confidence against frozen D up to 32 half steps on 233,384 texels with and without the mask, so the
  mask never caused it. Production fused and split had no differences, and locks against D were identical.
- What B's evidence superseded: the withdrawal is reverted and R3-lite is restored
  ([current gather contract](../../../../docs/design/TEMPORAL-RESOLVE.md#plan-amendments)). The fixed two-half-step NVIDIA allowance is replaced by the
  report-only finite-value rule: on hardware NVIDIA Direct3D 11 stored locks, R8 and depth equal D prime's,
  colour and confidence keep D prime's non-finite classes, and finite differences are reported only. D prime
  (`TemporalVisibilityFrozenD.ToD`) is the A/B reference, and shipped D (`ToShippedD`) is an exact reference on
  Metal only ([current device comparison rules](../../../../docs/design/TEMPORAL-RESOLVE.md#plan-amendments)). Entry identity is exact except llvmpipe colour,
  which keeps main's amendment 23 bound.

The [withdrawal record](mask-withdrawal.md) lists source changes, every moved shader program and the local
verification. Prepare and fused entry no longer compute a current-neighbour mask, PreparedSurface.w is zero,
and the current side runs in pass two as at 7f.2. R1, R2 with the binade rule and R4 remain. Entry identity is
exact on every backend. Hardware NVIDIA D3D11 alone allows a fixed two half steps per colour/confidence channel
in frozen-D A/B, with exact stored locks, R8 and depth. D's paired entries are diagnostic only and never set the
allowance. This supersedes section 6's measured envelope and V2 mask variant.

### A. Outside-cost decomposition, one dispatch on all legs

```bash
gh workflow run gpu-hosted-bench.yml --repo APKiwiOrg/KhaozEngine --ref main -f ref=5ce5454d6a2b719b1448dd95bf0491145a6a945c -f filter='FullyQualifiedName~TemporalVisibilityCostDecompositionCostPerfGpuTests' -f legs=all
```

One dispatch, three NVIDIA T4 GPU jobs. Both original scenes, twelve keyed boxes and the moving thin field,
remain at 2560x1440 Native and Quality. Nothing is trimmed. Each case interleaves Off and five variants at both
entries, eleven modes, in the same fifteen rotating rounds of twelve timed frames after two warm frames. All
modes keep synchronized scene clocks, draw frames, jitter and independent histories. Setup, shader compilation
and target creation precede measured blocks. No asserted budget is introduced or changed.

| Variant | Test-only injection |
| --- | --- |
| V0 shipped | The normal renderer and scene, no rewrite. |
| V1 check removed | `WithoutCheck`, the check expression replaced with false. |
| V5 visibility infrastructure off, retired | `TemporalTestResolve.Install(..., disableVisibility: true)` sets the scene seam to the existing no-visibility MRT layout and constructs the renderer and split without visibility bindings. They omit visibility history allocation, uniform updates and copies. `WithoutVisibility` removes the helper, check and three shader declarations. |
| V4 V5 without precise, retired | V5 plus `WithoutVisibilityOrPrecise`, removing exactly the two shared lock-release precise markings. Device-free SPIR-V checks prove NoContraction markings disappear, and the control renders on Metal. FXC's current output is not yet measured or archived. |
| V3 frozen D | `TemporalVisibilityFrozenD.ToD`, retaining shipped visibility infrastructure. |

Task 7g.3a removed `precise` from production for the barrier-ordered lock release. Run 39519219228 measured
this V4 on 5ce5454d6, and its V5 minus V4 rows are the precise cost on that head. A later barrier-removed V4
was authored but never ran. Review 7g r1 retired V5 and V4 together, because V5's visibility-disable switch
lived in the shipped renderer and scene. Task 7h Z adds V6 no check with identity barriers beside V0, V1 and V3 D
prime. Since 7h Z round 2 every shipped program keeps the positive-zero bypass as a real branch, so V1 already
skips the ordered barriers for positive-zero locks. V1 minus V6 therefore measures the barriers that remain on the
nonzero-lock path, close to zero in the boxes, where every lock is positive zero. Round 1's flattened candidate is
history in 7h-zero-lock.md. Readbacks before and after all timed blocks report colour, confidence and lock
differences, and require the boxes to retain positive-zero locks. Run 39654970582 times V0, V1, V3 and V6 at
3e8358b09.

The output prints V0 minus V1 (check), V1 minus V5 (visibility infrastructure), V5 minus V4 (precise), and
V0 minus V3, with paired-round differences and the existing Off drift control. The scenes have shadows off, so
this isolates visibility infrastructure and arithmetic without timing PCF. No cost, perf or bench test was run
locally for this request.

The successful last decomposition [39497171271](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39497171271)
provides these job and test-step times, read from the completed job records. The failed first Windows Vulkan
restore produced no measurement. Its successful retry is the Windows Vulkan row below.

| Leg | Successful job duration | Test step | Job |
| --- | --- | --- | --- |
| windows-d3d11 | 8 min 3 s | 2 min 12 s | [118909301214](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39497171271/job/118909301214) |
| windows-vulkan | 7 min 33 s | 1 min 27 s | [118909298673](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39497171271/job/118909298673) |
| linux-vulkan | 3 min 17 s | 1 min 9 s | [118909300495](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39497171271/job/118909300495) |

The new selection has 44 mode/case combinations and 7920 timed frames, versus the last decomposition's 36 and
6480, a 22.2 percent increase. It compiles additional controls. [Bench 2](benchmark-result-2.md#1-provenance)
shows the broader setup variability, D3D11 jobs 9:15 to 10:38, Windows Vulkan 22:46 to 33:40 and Linux 3:09 to
3:30. Allow roughly three to six minutes for tests and control compilation. This remains comfortably below
the unchanged 60 minute job timeout, without trimming a scene or Native.

| Leg | Expected complete job duration | Estimated bill |
| --- | --- | --- |
| windows-d3d11 | 10 to 18 minutes | $1.02 to $1.84 |
| windows-vulkan | 10 to 40 minutes | $1.02 to $4.08 |
| linux-vulkan | 4 to 6 minutes | $0.21 to $0.31 |
| Dispatch total | About 20 to 58 minutes plus queue time, Windows legs serialize | $2.25 to $6.23 |

Rates checked on 2026-10-21 against [GitHub Actions runner pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing),
Windows 4-core GPU $0.102/minute and Linux 4-core GPU $0.052/minute. GitHub rounds each job up to a whole
minute. All three jobs hitting the 60 minute timeout would cost $15.36. These are estimates, not new timings.

### B. NVIDIA T4 D3D11 verification, one dispatch

```bash
gh workflow run gpu-hosted-bench.yml --repo APKiwiOrg/KhaozEngine --ref main -f ref=5ce5454d6a2b719b1448dd95bf0491145a6a945c -f filter='FullyQualifiedName~TemporalVisibilityResolveAbGpuTests|FullyQualifiedName~TemporalEntryIdentityGpuTests|FullyQualifiedName~TemporalVisibilityEquivalenceGpuTests' -f legs=windows-d3d11
```

One dispatch, one hardware NVIDIA T4 D3D11 GPU job, 26 correctness cases. No timing, perf or bench class is
selected. Entry identity is exact, including Quality, Performance and outlined frames. Helper equivalence stays
exact. A/B used the fixed two-half-step arithmetic rule described above, since superseded, with per-frame
counts and maxima and exact locks, R8 and depth. D's own entry comparison is printed as a diagnostic, never an acceptance envelope.

The last paired D3D11 verification [39497191900](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39497191900)
took 8 min 44 s for [job 118885229357](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39497191900/job/118885229357),
including 1 min 26 s in the test step. Bench 2's D3D11 oracle job took 16 min 34 s, with 1.5967 minutes in tests.
Allowing for the helper equivalence cases and observed Windows setup variation, expect about 10 to 22 minutes
plus queue time, approximately $1.02 to $2.24 at $0.102/minute. The 60 minute timeout ceiling is $6.12.

### Readiness and limits, as written before dispatch

Local Release build has zero warnings and errors. Shader writers pass 9/9. The device-free selection passed
236/243, with only the seven explicitly stale DXBC provenance cases failing, covering eight listings. Metal
passed 1656/1656 with zero failures or skips, including exact A/B and equivalence. Motion allocation checks
passed 5/5 with zero steady temporal-frame allocations. Format and tree guard results are in the withdrawal
record. The comparison rules changed as requested. Baselines, version, tags and releases are unchanged.

The DXBC fixture test remains red until a genuine Windows FXC dump recaptures the eight stale listings and their
source pins. Neither proposed filter performs that dump. These two approvals therefore do not complete the
DXBC recapture requirement or authorize integration, a release, another run or an automatic retry. Hardware
NVIDIA D3D11 after the withdrawal, the outside-cost figures and the stricter entry gate on other unrun backends
remain unverified until their own checks run. The former software-Vulkan colour exception was removed to
enforce exact entry identity on every backend, and that tighter gate has not run on software Vulkan here.
Leave this branch unmerged if either verification fails.

## 8. Current T4 Direct3D 11 verification filter

Not dispatched. The next T4 Direct3D 11 correctness run needs fresh owner approval, and its ref is resolved with
`git rev-parse` on the pushed candidate at dispatch time. Its filter:

```text
FullyQualifiedName~TemporalVisibilityResolveAbGpuTests|FullyQualifiedName~TemporalEntryIdentityGpuTests|FullyQualifiedName~TemporalVisibilityEquivalenceGpuTests|FullyQualifiedName~TemporalVisibilityGatherGpuTests
```

`FullyQualifiedName~TemporalEntryIdentityGpuTests` selects the large-frame entry identity case,
`Both_entry_points_write_the_same_history_across_power_of_two_columns` at Native and Quality, added after review
7g r1. It runs production fused and split on the A/B's large threshold frames, 1104x612 internal, across columns
512 and 1024 and row 512 under positive jitter, and holds their colour, state, R8 and depth byte-identical every
frame. On the same inputs D prime fused must equal D prime split. Neither comparison has an NVIDIA exception.
Entry identity is exact on Metal, on the T4 on Direct3D 11 (under the NVIDIA D3D11 rule) and Vulkan, and on WARP.
On llvmpipe the state, R8 and depth are exact and the colour of each pair is within main's amendment 23 bound,
`0.0625` on at most `0.0025` of the values compared.
The A/B applies the current contracts listed in section 7. The decomposition, if requested again, times V0
shipped, V1 check removed and V3 D prime only.

Task 7g.5 adds the exact raw gather oracle to this selection. Direct3D 11 and Vulkan use all visibility gathers,
while Metal retains current-block loads. The software-Vulkan rule is unchanged. The separately authorized free
all-leg correctness dispatch uses the filter from `7g3b-hosted.md` plus `TemporalVisibilityGatherGpuTests`, with no
timing, perf or bench cases. This does not authorize the T4 dispatch described in this section.
