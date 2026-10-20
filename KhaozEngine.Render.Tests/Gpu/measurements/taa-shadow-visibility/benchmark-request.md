# TAA shadow visibility cost benchmark request

Status: request only, plan Task 12. Nothing has been dispatched. The billed T4 run needs fresh explicit owner approval. If approval is declined, the hold is recorded here and integration and release stay with the orchestrator.

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
