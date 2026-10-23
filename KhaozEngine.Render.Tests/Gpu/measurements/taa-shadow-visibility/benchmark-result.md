# TAA shadow visibility cost benchmark result

Proposal A of [benchmark-request.md](benchmark-request.md), run once at each ref after owner approval. This file records printed numbers only. No workflow was rerun to produce it.

Headline: on all three T4 legs, head adds about 2 to 9 ms a frame to every temporal mode that records the fused resolve or the split's second pass. The resolve fixture's `Off` drift control moved by -0.011 to -0.256 ms, and the split's pass one alone moved by -0.233 to +0.295 ms in resolve and sharpen at most.

## 1. Provenance

| Item | Base | Head |
| --- | --- | --- |
| Run | [39317665751](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39317665751) | [39317684399](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39317684399) |
| Input `ref`, checked out | `6c6529589f0e8c22234abd6973be2d57b60f773e` (`v21.7.1`), log `HEAD is now at 6c65295` | `058eaea98295a529b9f98cafdff92dbb51f40886`, log `HEAD is now at 058eaea` |
| Workflow | `gpu-hosted-bench.yml`, `workflow_dispatch` from `main` at `7fb5f88250d5820ba89d8da33a4b2c76c5ff56ee` | same |
| Filter | `FullyQualifiedName~TemporalResolveCostPerfGpuTests\|FullyQualifiedName~TemporalOutlineCostPerfGpuTests` | same |
| Conclusion | success, every job success | success, every job success |
| Created | 2026-10-19T20:14:34Z | 2026-10-19T20:14:42Z |

The workflow file at `7fb5f8825` is identical to the one at base. `TemporalResolveCostPerfGpuTests.cs`, `TemporalOutlineCostPerfGpuTests.cs`, `TemporalFixture.cs` and `TemporalAcceptanceScenes.cs` are identical at base and head (`git diff --stat` empty).

| Leg | Backend, device line printed by every case | Driver | Base job, duration | Head job, duration |
| --- | --- | --- | --- | --- |
| `windows-d3d11` | `Direct3D11Native on NVIDIA Tesla T4` | GRID 596.86 installed by the job, `nvidia-smi` Driver Version 596.86, CUDA 13.2, WDDM. Before the install the image reports 471.11 TCC, `Win32_VideoController` DriverVersion 30.0.14.7111. | 118283300651, 20:22:12 to 20:52:29, 30 min 17 s | 118283377685, 20:57:33 to 21:09:32, 11 min 59 s |
| `windows-vulkan` | `VulkanNative on Tesla T4` | Same GRID 596.86 install and readings as `windows-d3d11`. The Windows jobs print no `vulkaninfo`, so the Vulkan driver version string is not recorded. | 118283300442, 20:14:48 to 20:22:06, 7 min 18 s | 118283377660, 21:09:35 to 21:17:02, 7 min 27 s |
| `linux-vulkan` | `VulkanNative on Tesla T4` | `nvidia-smi` Driver Version 590.48.01, CUDA 13.1. `vulkaninfo` driverName NVIDIA, driverInfo 590.48.01, driverVersion 590.48.1.0, DRIVER_ID_NVIDIA_PROPRIETARY. | 118283300328, 20:14:45 to 20:17:53, 3 min 8 s | 118283377522, 20:17:56 to 20:21:27, 3 min 31 s |

Device report before the filter, at both refs: `backend=Direct3D11Native device='NVIDIA Tesla T4' aniso=True lodBias=True` on `windows-d3d11`, and `backend=VulkanNative device='Tesla T4' aniso=True lodBias=True` on both Vulkan legs. Driver strings were identical at both refs on each leg.

All times are 2026-10-19 UTC. The two Windows legs share one capped runner and ran in the order base `windows-vulkan`, base `windows-d3d11`, head `windows-d3d11`, head `windows-vulkan`. The Linux legs ran base then head.

Cases: each of the six jobs reported `Total tests: 10, Passed: 10`, 8 `TemporalResolveCostPerfGpuTests` and 2 `TemporalOutlineCostPerfGpuTests`. No case failed or skipped, and none printed `not measured`. Every printed mode has 15 blocks. Bench test run total time from the logs: `windows-d3d11` base 22.8984 min and head 6.2060 min, `windows-vulkan` 1.2141 and 1.5316 min, `linux-vulkan` 1.0835 and 1.4364 min. The policy line (`Split` on every case) and the internal sizes match at both refs.

One non-timing line differs. On `windows-d3d11` the resolve's frame 9 clipped count differs between refs in two cases: moving field 2560x1440 Quality 990800 at base against 992800 at head, and moving field 3456x2234 Native 1600940 against 1600102. Disoccluded counts match. Every other counts line on every leg is identical at both refs.

## 2. Results by leg

What each number is:

- Every figure is CPU wall clock in ms a frame, printed to three decimals. A block is 12 timed frames after 2 warm frames (resolve) or 3 (outline), divided by 12. Each frame is Begin, draws, PrepareFrame, recording, Submit, `WaitForIdle` and Present. Each mode gets one block in each of 15 rounds, in a rotating (resolve) or alternating (outline) order.
- Mode median: the median of the mode's 15 blocks. `Off` renders anti-aliasing off at the internal size.
- Resolve and sharpen at most: the mode median minus the `Off` median of the same run.
- Paired round differences: per round, the mode's block minus that round's `Off` block, reported as median, min and max over 15 rounds.
- Submit and drain difference: each block's mean per-frame Submit plus `WaitForIdle` wall time, median over the mode's 15 blocks, minus the same median for `Off`.
- Pass pairs: the difference of two mode medians. Pass one is split minus pass two alone, pass two is split minus pass one alone.
- Outline: medians of 15 blocks with the outline off and on. The cost is on minus off, with the per-round paired differences.

Head minus base is computed from the printed values, so it is exact to three decimals. Relative is head minus base over the base value, to one decimal. Block ranges are the min and max of the 15 printed blocks.

### windows-d3d11

Device line: `Direct3D11Native on NVIDIA Tesla T4` at both refs.

Mode medians, ms a frame (`off` is the drift control):

| Case | Mode | Base | Head | Head minus base | Relative | Base blocks min to max | Head blocks min to max |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | **off** | 1.208 | 1.073 | -0.135 | -11.2% | 1.160 to 2.148 | 1.052 to 1.562 |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 3.446 | 7.833 | +4.387 | +127.3% | 3.366 to 4.684 | 7.642 to 10.671 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 3.389 | 7.340 | +3.951 | +116.6% | 3.012 to 4.134 | 7.226 to 7.561 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 2.289 | 2.231 | -0.058 | -2.5% | 2.189 to 2.764 | 2.135 to 2.351 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.588 | 6.772 | +4.184 | +161.7% | 2.506 to 3.142 | 6.647 to 6.880 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.490 | 7.567 | +4.077 | +116.8% | 3.330 to 3.587 | 7.455 to 7.698 |
| boxes, 2560x1440 Quality, internal 1707x960 | **off** | 0.700 | 0.650 | -0.050 | -7.1% | 0.668 to 1.127 | 0.619 to 0.800 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.913 | 6.880 | +3.967 | +136.2% | 2.769 to 6.065 | 6.760 to 9.114 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 2.361 | 6.195 | +3.834 | +162.4% | 2.097 to 2.645 | 5.959 to 6.923 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.329 | 1.262 | -0.067 | -5.0% | 1.176 to 1.434 | 1.205 to 1.296 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.082 | 5.953 | +3.871 | +185.9% | 2.011 to 2.240 | 5.866 to 6.122 |
| field, 2560x1440 Native, internal 2560x1440 | **off** | 1.731 | 1.566 | -0.165 | -9.5% | 1.607 to 2.111 | 1.457 to 2.868 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 4.644 | 9.278 | +4.634 | +99.8% | 4.482 to 6.230 | 9.020 to 9.859 |
| field, 2560x1440 Native, internal 2560x1440 | split | 4.331 | 8.589 | +4.258 | +98.3% | 3.929 to 4.875 | 8.469 to 8.828 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 3.080 | 3.015 | -0.065 | -2.1% | 2.928 to 3.163 | 2.839 to 3.169 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 3.499 | 8.006 | +4.507 | +128.8% | 3.432 to 3.572 | 7.799 to 8.258 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 4.459 | 8.775 | +4.316 | +96.8% | 4.284 to 4.630 | 8.708 to 9.063 |
| field, 2560x1440 Quality, internal 1707x960 | **off** | 1.046 | 0.942 | -0.104 | -9.9% | 1.007 to 1.673 | 0.881 to 1.112 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 3.761 | 7.761 | +4.000 | +106.4% | 3.635 to 6.381 | 7.643 to 10.495 |
| field, 2560x1440 Quality, internal 1707x960 | split | 3.031 | 7.025 | +3.994 | +131.8% | 2.802 to 3.536 | 6.791 to 7.163 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.807 | 1.703 | -0.104 | -5.8% | 1.748 to 2.074 | 1.638 to 1.894 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.670 | 6.756 | +4.086 | +153.0% | 2.544 to 3.034 | 6.559 to 7.022 |
| boxes, 3456x2234 Native, internal 3342x2160 | **off** | 2.175 | 1.980 | -0.195 | -9.0% | 1.975 to 7.665 | 1.832 to 7.276 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 7.472 | 16.093 | +8.621 | +115.4% | 7.264 to 11.718 | 15.851 to 17.972 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 6.792 | 15.263 | +8.471 | +124.7% | 6.433 to 7.064 | 15.085 to 15.366 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 4.493 | 4.423 | -0.070 | -1.6% | 4.301 to 4.661 | 4.225 to 4.527 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 5.325 | 13.789 | +8.464 | +158.9% | 5.204 to 5.496 | 13.643 to 13.872 |
| boxes, 3456x2234 Quality, internal 2304x1489 | **off** | 1.271 | 1.133 | -0.138 | -10.9% | 1.222 to 2.019 | 1.096 to 1.319 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 6.056 | 14.235 | +8.179 | +135.1% | 5.938 to 9.594 | 14.131 to 14.863 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 4.847 | 12.694 | +7.847 | +161.9% | 4.314 to 5.111 | 12.579 to 12.838 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 2.634 | 2.438 | -0.196 | -7.4% | 2.482 to 2.813 | 2.363 to 2.911 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.228 | 12.186 | +7.958 | +188.2% | 4.083 to 4.489 | 11.882 to 12.326 |
| field, 3456x2234 Native, internal 3342x2160 | **off** | 3.031 | 2.775 | -0.256 | -8.4% | 2.810 to 3.134 | 2.663 to 2.984 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 9.916 | 18.752 | +8.836 | +89.1% | 8.613 to 10.309 | 18.644 to 19.022 |
| field, 3456x2234 Native, internal 3342x2160 | split | 8.817 | 17.779 | +8.962 | +101.6% | 8.470 to 9.061 | 17.614 to 18.011 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 5.864 | 5.903 | +0.039 | +0.7% | 5.648 to 6.359 | 5.695 to 6.000 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 7.138 | 16.163 | +9.025 | +126.4% | 7.010 to 7.263 | 15.950 to 16.281 |
| field, 3456x2234 Quality, internal 2304x1489 | **off** | 1.820 | 1.664 | -0.156 | -8.6% | 1.761 to 2.867 | 1.529 to 1.812 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 8.057 | 16.490 | +8.433 | +104.7% | 7.901 to 8.407 | 16.300 to 16.601 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 6.320 | 14.779 | +8.459 | +133.8% | 5.918 to 6.504 | 14.584 to 14.871 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 3.511 | 3.427 | -0.084 | -2.4% | 3.339 to 3.563 | 3.331 to 3.522 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 5.451 | 14.003 | +8.552 | +156.9% | 5.314 to 5.642 | 13.801 to 14.281 |

Resolve and sharpen at most (mode median minus `off` median, ms), with paired round differences and the submit and drain difference:

| Case | Mode | Base | Head | Head minus base | Relative | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Head minus base |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 2.238 | 6.760 | +4.522 | +202.1% | 2.242, 2.112 to 2.658 | 6.738, 6.581 to 9.109 | 2.222 | 6.733 | +4.511 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 2.182 | 6.266 | +4.084 | +187.2% | 2.182, 1.793 to 2.457 | 6.210, 5.817 to 6.443 | 2.182 | 6.239 | +4.057 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.081 | 1.158 | +0.077 | +7.1% | 1.082, 0.616 to 1.315 | 1.162, 0.722 to 1.240 | 1.083 | 1.150 | +0.067 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.380 | 5.699 | +4.319 | +313.0% | 1.362, 0.994 to 1.499 | 5.699, 5.216 to 5.814 | 1.367 | 5.666 | +4.299 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.282 | 6.494 | +4.212 | +184.6% | 2.284, 1.182 to 2.412 | 6.477, 6.026 to 6.589 | 2.273 | 6.472 | +4.199 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.213 | 6.229 | +4.016 | +181.5% | 2.198, 2.079 to 4.938 | 6.220, 6.106 to 8.314 | 2.196 | 6.197 | +4.001 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 1.661 | 5.544 | +3.883 | +233.8% | 1.634, 1.408 to 1.781 | 5.543, 5.306 to 6.124 | 1.654 | 5.510 | +3.856 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.629 | 0.612 | -0.017 | -2.7% | 0.607, 0.207 to 0.734 | 0.605, 0.471 to 0.664 | 0.628 | 0.610 | -0.018 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.382 | 5.303 | +3.921 | +283.7% | 1.384, 0.963 to 1.550 | 5.322, 5.075 to 5.490 | 1.375 | 5.279 | +3.904 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 2.913 | 7.711 | +4.798 | +164.7% | 2.966, 2.798 to 4.120 | 7.695, 6.992 to 7.910 | 2.877 | 7.674 | +4.797 |
| field, 2560x1440 Native, internal 2560x1440 | split | 2.600 | 7.023 | +4.423 | +170.1% | 2.573, 2.274 to 2.888 | 6.995, 5.960 to 7.171 | 2.587 | 6.985 | +4.398 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.349 | 1.449 | +0.100 | +7.4% | 1.311, 0.879 to 1.478 | 1.444, 0.282 to 1.575 | 1.325 | 1.429 | +0.104 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.769 | 6.440 | +4.671 | +264.0% | 1.735, 1.437 to 1.877 | 6.391, 5.171 to 6.660 | 1.731 | 6.401 | +4.670 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.728 | 7.209 | +4.481 | +164.3% | 2.711, 2.405 to 2.904 | 7.177, 5.841 to 7.497 | 2.704 | 7.161 | +4.457 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 2.716 | 6.819 | +4.103 | +151.1% | 2.708, 2.562 to 4.708 | 6.847, 6.693 to 9.383 | 2.664 | 6.771 | +4.107 |
| field, 2560x1440 Quality, internal 1707x960 | split | 1.985 | 6.083 | +4.098 | +206.4% | 1.985, 1.721 to 2.254 | 6.099, 5.906 to 6.214 | 1.970 | 6.044 | +4.074 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.761 | 0.761 | 0.000 | 0.0% | 0.768, 0.373 to 1.063 | 0.787, 0.680 to 0.927 | 0.754 | 0.747 | -0.007 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.624 | 5.814 | +4.190 | +258.0% | 1.618, 1.321 to 1.726 | 5.769, 5.614 to 6.064 | 1.607 | 5.753 | +4.146 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 5.297 | 14.112 | +8.815 | +166.4% | 5.284, 4.053 to 5.702 | 14.058, 10.697 to 14.241 | 5.275 | 14.089 | +8.814 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 4.617 | 13.283 | +8.666 | +187.7% | 4.727, -0.914 to 4.934 | 13.254, 7.999 to 13.450 | 4.618 | 13.256 | +8.638 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 2.318 | 2.443 | +0.125 | +5.4% | 2.289, -3.125 to 2.583 | 2.430, -2.791 to 2.616 | 2.306 | 2.439 | +0.133 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 3.150 | 11.809 | +8.659 | +274.9% | 3.199, -2.460 to 3.350 | 11.805, 6.453 to 11.940 | 3.152 | 11.775 | +8.623 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 4.785 | 13.103 | +8.318 | +173.8% | 4.789, 4.652 to 7.574 | 13.093, 12.937 to 13.544 | 4.780 | 13.081 | +8.301 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 3.576 | 11.561 | +7.985 | +223.3% | 3.580, 2.857 to 3.866 | 11.568, 11.431 to 11.684 | 3.575 | 11.549 | +7.974 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.363 | 1.306 | -0.057 | -4.2% | 1.373, 0.608 to 1.545 | 1.306, 1.129 to 1.815 | 1.363 | 1.309 | -0.054 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 2.957 | 11.053 | +8.096 | +273.8% | 2.954, 2.064 to 3.211 | 11.033, 10.708 to 11.226 | 2.955 | 11.033 | +8.078 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 6.885 | 15.978 | +9.093 | +132.1% | 6.916, 5.582 to 7.305 | 16.040, 15.768 to 16.250 | 6.847 | 15.948 | +9.101 |
| field, 3456x2234 Native, internal 3342x2160 | split | 5.786 | 15.004 | +9.218 | +159.3% | 5.788, 5.350 to 6.024 | 15.018, 14.767 to 15.260 | 5.754 | 14.971 | +9.217 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 2.833 | 3.128 | +0.295 | +10.4% | 2.848, 2.617 to 3.234 | 3.069, 2.932 to 3.293 | 2.812 | 3.110 | +0.298 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 4.107 | 13.388 | +9.281 | +226.0% | 4.096, 3.946 to 4.330 | 13.337, 13.218 to 13.521 | 4.082 | 13.351 | +9.269 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 6.238 | 14.826 | +8.588 | +137.7% | 6.229, 5.064 to 6.548 | 14.829, 14.660 to 14.914 | 6.197 | 14.755 | +8.558 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 4.501 | 13.114 | +8.613 | +191.4% | 4.434, 3.507 to 4.742 | 13.086, 12.955 to 13.262 | 4.475 | 13.063 | +8.588 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.691 | 1.763 | +0.072 | +4.3% | 1.676, 0.641 to 1.741 | 1.766, 1.605 to 1.909 | 1.655 | 1.760 | +0.105 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 3.632 | 12.339 | +8.707 | +239.7% | 3.620, 2.489 to 3.835 | 12.304, 12.138 to 12.530 | 3.599 | 12.299 | +8.700 |

Pass pairs, ms from the medians:

| Case | Pair | Base | Head | Head minus base | Base paired median, min to max | Head paired median, min to max |
| --- | --- | ---: | ---: | ---: | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | pass one | 0.802 | 0.568 | -0.234 | 0.832, 0.423 to 1.162 | 0.564, 0.394 to 0.853 |
| boxes, 2560x1440 Native, internal 2560x1440 | pass two | 1.101 | 5.108 | +4.007 | 1.077, 0.759 to 1.370 | 5.113, 4.998 to 5.256 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against fused | -0.056 | -0.494 | -0.438 | -0.219, -0.550 to 0.333 | -0.512, -3.292 to -0.208 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against its upscaling second pass | -0.100 | -0.227 | -0.127 | -0.135, -0.562 to 0.804 | -0.244, -0.378 to 0.028 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass one | 0.280 | 0.242 | -0.038 | 0.286, 0.036 to 0.555 | 0.249, -0.093 to 1.048 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass two | 1.032 | 4.932 | +3.900 | 1.011, 0.872 to 1.311 | 4.932, 4.668 to 5.652 |
| boxes, 2560x1440 Quality, internal 1707x960 | split against fused | -0.551 | -0.685 | -0.134 | -0.613, -3.420 to -0.445 | -0.702, -2.191 to -0.417 |
| field, 2560x1440 Native, internal 2560x1440 | pass one | 0.832 | 0.583 | -0.249 | 0.860, 0.400 to 1.327 | 0.664, 0.254 to 0.812 |
| field, 2560x1440 Native, internal 2560x1440 | pass two | 1.251 | 5.574 | +4.323 | 1.308, 0.795 to 1.886 | 5.578, 5.356 to 5.748 |
| field, 2560x1440 Native, internal 2560x1440 | split against fused | -0.313 | -0.688 | -0.375 | -0.456, -1.355 to 0.022 | -0.679, -1.032 to -0.336 |
| field, 2560x1440 Native, internal 2560x1440 | split against its upscaling second pass | -0.128 | -0.186 | -0.058 | -0.154, -0.631 to 0.360 | -0.193, -0.595 to 0.119 |
| field, 2560x1440 Quality, internal 1707x960 | pass one | 0.361 | 0.269 | -0.092 | 0.367, 0.146 to 0.600 | 0.258, -0.000 to 0.535 |
| field, 2560x1440 Quality, internal 1707x960 | pass two | 1.224 | 5.322 | +4.098 | 1.236, 0.922 to 1.491 | 5.304, 5.119 to 5.424 |
| field, 2560x1440 Quality, internal 1707x960 | split against fused | -0.730 | -0.736 | -0.006 | -0.743, -2.845 to -0.480 | -0.829, -3.469 to -0.480 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass one | 1.467 | 1.474 | +0.007 | 1.524, 1.066 to 1.723 | 1.451, 1.264 to 1.618 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass two | 2.299 | 10.840 | +8.541 | 2.400, 1.772 to 2.700 | 10.830, 10.640 to 11.063 |
| boxes, 3456x2234 Native, internal 3342x2160 | split against fused | -0.680 | -0.830 | -0.150 | -0.684, -4.967 to -0.282 | -0.796, -2.698 to -0.650 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass one | 0.619 | 0.507 | -0.112 | 0.619, 0.068 to 0.925 | 0.564, 0.326 to 0.808 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass two | 2.213 | 10.255 | +8.042 | 2.237, 1.520 to 2.630 | 10.302, 9.778 to 10.418 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split against fused | -1.208 | -1.542 | -0.334 | -1.235, -4.718 to -0.856 | -1.507, -2.044 to -1.378 |
| field, 3456x2234 Native, internal 3342x2160 | pass one | 1.678 | 1.616 | -0.062 | 1.665, 1.254 to 1.892 | 1.689, 1.362 to 1.828 |
| field, 3456x2234 Native, internal 3342x2160 | pass two | 2.952 | 11.876 | +8.924 | 2.927, 2.301 to 3.252 | 11.921, 11.762 to 12.202 |
| field, 3456x2234 Native, internal 3342x2160 | split against fused | -1.099 | -0.974 | +0.125 | -1.115, -1.566 to 0.288 | -0.972, -1.245 to -0.728 |
| field, 3456x2234 Quality, internal 2304x1489 | pass one | 0.869 | 0.775 | -0.094 | 0.825, 0.346 to 1.155 | 0.765, 0.580 to 1.002 |
| field, 3456x2234 Quality, internal 2304x1489 | pass two | 2.810 | 11.352 | +8.542 | 2.867, 2.407 to 3.165 | 11.320, 11.108 to 11.423 |
| field, 3456x2234 Quality, internal 2304x1489 | split against fused | -1.737 | -1.712 | +0.025 | -1.843, -2.081 to -1.410 | -1.727, -1.906 to -1.510 |

Outline fixture, ms a frame (Split entry at both refs):

| Case | Line | Base | Head | Head minus base | Relative | Base blocks min to max or paired median, min to max | Head blocks min to max or paired median, min to max |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| outline, 2560x1440 Native, internal 2560x1440 | outline off | 3.422 | 7.347 | +3.925 | +114.7% | 3.348 to 17.465 | 7.240 to 15.703 |
| outline, 2560x1440 Native, internal 2560x1440 | outline on | 3.718 | 7.602 | +3.884 | +104.5% | 3.432 to 3.852 | 7.486 to 194.594 |
| outline, 2560x1440 Native, internal 2560x1440 | outline cost | 0.296 | 0.255 | -0.041 | -13.9% | 0.305, -13.722 to 0.412 | 0.258, -1.603 to 178.891 |
| outline, 2560x1440 Quality, internal 1707x960 | outline off | 2.481 | 6.026 | +3.545 | +142.9% | 2.403 to 4.096 | 5.963 to 6.133 |
| outline, 2560x1440 Quality, internal 1707x960 | outline on | 2.598 | 6.202 | +3.604 | +138.7% | 2.524 to 3.539 | 6.117 to 6.247 |
| outline, 2560x1440 Quality, internal 1707x960 | outline cost | 0.117 | 0.175 | +0.058 | +49.6% | 0.127, -0.557 to 0.265 | 0.164, 0.067 to 0.269 |

### windows-vulkan

Device line: `VulkanNative on Tesla T4` at both refs.

Mode medians, ms a frame (`off` is the drift control):

| Case | Mode | Base | Head | Head minus base | Relative | Base blocks min to max | Head blocks min to max |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | **off** | 1.204 | 1.168 | -0.036 | -3.0% | 0.968 to 1.245 | 1.020 to 1.324 |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 4.178 | 6.615 | +2.437 | +58.3% | 4.053 to 4.729 | 6.519 to 7.091 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 3.302 | 5.475 | +2.173 | +65.8% | 3.056 to 3.378 | 5.254 to 5.551 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 2.102 | 2.035 | -0.067 | -3.2% | 1.897 to 2.205 | 1.884 to 2.107 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.792 | 4.981 | +2.189 | +78.4% | 2.698 to 2.855 | 4.888 to 5.046 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.559 | 5.690 | +2.131 | +59.9% | 3.516 to 3.636 | 5.551 to 5.751 |
| boxes, 2560x1440 Quality, internal 1707x960 | **off** | 0.747 | 0.684 | -0.063 | -8.4% | 0.613 to 0.766 | 0.638 to 0.724 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 3.689 | 5.880 | +2.191 | +59.4% | 3.540 to 4.151 | 5.772 to 6.233 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 2.439 | 4.523 | +2.084 | +85.4% | 2.324 to 2.535 | 4.292 to 4.631 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.177 | 1.174 | -0.003 | -0.3% | 1.084 to 1.241 | 1.104 to 1.196 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.347 | 4.340 | +1.993 | +84.9% | 2.192 to 2.378 | 4.288 to 4.453 |
| field, 2560x1440 Native, internal 2560x1440 | **off** | 1.631 | 1.563 | -0.068 | -4.2% | 1.462 to 1.683 | 1.475 to 1.618 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 5.142 | 7.807 | +2.665 | +51.8% | 4.999 to 5.860 | 7.729 to 8.218 |
| field, 2560x1440 Native, internal 2560x1440 | split | 4.559 | 6.889 | +2.330 | +51.1% | 4.151 to 4.699 | 6.639 to 6.946 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 3.085 | 3.029 | -0.056 | -1.8% | 2.925 to 3.235 | 2.890 to 3.091 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 3.639 | 5.941 | +2.302 | +63.3% | 3.595 to 3.848 | 5.905 to 5.976 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 4.887 | 7.041 | +2.154 | +44.1% | 4.730 to 4.936 | 6.998 to 7.168 |
| field, 2560x1440 Quality, internal 1707x960 | **off** | 1.004 | 0.927 | -0.077 | -7.7% | 0.843 to 1.138 | 0.851 to 1.070 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 4.187 | 6.559 | +2.372 | +56.7% | 4.079 to 4.715 | 6.446 to 6.885 |
| field, 2560x1440 Quality, internal 1707x960 | split | 3.183 | 5.196 | +2.013 | +63.2% | 2.938 to 3.277 | 4.993 to 5.258 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.720 | 1.705 | -0.015 | -0.9% | 1.599 to 1.828 | 1.640 to 1.742 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.831 | 4.875 | +2.044 | +72.2% | 2.688 to 2.973 | 4.700 to 4.986 |
| boxes, 3456x2234 Native, internal 3342x2160 | **off** | 1.988 | 1.940 | -0.048 | -2.4% | 1.755 to 2.152 | 1.805 to 2.156 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 8.626 | 13.390 | +4.764 | +55.2% | 8.447 to 9.247 | 13.159 to 13.577 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 6.825 | 10.940 | +4.115 | +60.3% | 6.608 to 6.994 | 10.790 to 11.152 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 4.084 | 3.804 | -0.280 | -6.9% | 3.611 to 4.146 | 3.567 to 3.848 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 5.640 | 9.778 | +4.138 | +73.4% | 5.508 to 5.881 | 9.689 to 9.988 |
| boxes, 3456x2234 Quality, internal 2304x1489 | **off** | 1.314 | 1.243 | -0.071 | -5.4% | 1.133 to 1.416 | 1.100 to 1.381 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 7.736 | 12.363 | +4.627 | +59.8% | 7.644 to 8.192 | 12.292 to 12.626 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 5.237 | 9.495 | +4.258 | +81.3% | 4.913 to 5.318 | 9.253 to 9.758 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 2.454 | 2.272 | -0.182 | -7.4% | 2.165 to 2.547 | 2.142 to 2.312 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.830 | 9.060 | +4.230 | +87.6% | 4.606 to 4.920 | 8.923 to 9.166 |
| field, 3456x2234 Native, internal 3342x2160 | **off** | 2.812 | 2.638 | -0.174 | -6.2% | 2.538 to 2.938 | 2.455 to 2.780 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 10.814 | 15.814 | +5.000 | +46.2% | 10.598 to 11.316 | 15.682 to 16.150 |
| field, 3456x2234 Native, internal 3342x2160 | split | 9.388 | 13.638 | +4.250 | +45.3% | 9.070 to 9.523 | 13.501 to 13.786 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 5.968 | 5.800 | -0.168 | -2.8% | 5.408 to 6.051 | 5.583 to 5.885 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 7.524 | 11.694 | +4.170 | +55.4% | 7.269 to 7.579 | 11.618 to 11.854 |
| field, 3456x2234 Quality, internal 2304x1489 | **off** | 1.782 | 1.663 | -0.119 | -6.7% | 1.543 to 1.884 | 1.522 to 1.768 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 9.187 | 14.018 | +4.831 | +52.6% | 9.111 to 9.738 | 13.972 to 14.276 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 6.854 | 11.167 | +4.313 | +62.9% | 6.560 to 6.989 | 11.039 to 11.235 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 3.540 | 3.399 | -0.141 | -4.0% | 3.253 to 3.638 | 3.255 to 3.458 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 5.988 | 10.256 | +4.268 | +71.3% | 5.938 to 6.101 | 10.097 to 10.410 |

Resolve and sharpen at most (mode median minus `off` median, ms), with paired round differences and the submit and drain difference:

| Case | Mode | Base | Head | Head minus base | Relative | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Head minus base |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 2.974 | 5.447 | +2.473 | +83.2% | 2.979, 2.840 to 3.557 | 5.434, 5.286 to 5.936 | 3.146 | 5.626 | +2.480 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 2.098 | 4.307 | +2.209 | +105.3% | 2.074, 1.831 to 2.393 | 4.277, 4.063 to 4.507 | 2.267 | 4.489 | +2.222 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 0.898 | 0.866 | -0.032 | -3.6% | 0.907, 0.670 to 1.119 | 0.844, 0.693 to 1.015 | 1.079 | 1.059 | -0.020 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.588 | 3.813 | +2.225 | +140.1% | 1.590, 1.471 to 1.812 | 3.825, 3.585 to 3.961 | 1.770 | 4.004 | +2.234 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.356 | 4.521 | +2.165 | +91.9% | 2.356, 2.290 to 2.591 | 4.516, 4.356 to 4.579 | 2.527 | 4.703 | +2.176 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.942 | 5.196 | +2.254 | +76.6% | 2.941, 2.872 to 3.416 | 5.201, 5.111 to 5.530 | 3.066 | 5.291 | +2.225 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 1.692 | 3.839 | +2.147 | +126.9% | 1.721, 1.576 to 1.909 | 3.837, 3.589 to 3.971 | 1.809 | 3.930 | +2.121 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.430 | 0.490 | +0.060 | +14.0% | 0.427, 0.332 to 0.620 | 0.493, 0.420 to 0.555 | 0.561 | 0.592 | +0.031 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.601 | 3.656 | +2.055 | +128.4% | 1.602, 1.426 to 1.754 | 3.655, 3.604 to 3.750 | 1.728 | 3.760 | +2.032 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 3.512 | 6.244 | +2.732 | +77.8% | 3.535, 3.335 to 4.245 | 6.254, 6.161 to 6.660 | 3.648 | 6.405 | +2.757 |
| field, 2560x1440 Native, internal 2560x1440 | split | 2.928 | 5.326 | +2.398 | +81.9% | 2.928, 2.470 to 3.135 | 5.285, 5.088 to 5.417 | 3.089 | 5.489 | +2.400 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.454 | 1.466 | +0.012 | +0.8% | 1.460, 1.327 to 1.592 | 1.474, 1.327 to 1.549 | 1.623 | 1.647 | +0.024 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.009 | 4.378 | +2.369 | +117.9% | 2.026, 1.934 to 2.179 | 4.372, 4.310 to 4.487 | 2.175 | 4.559 | +2.384 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.257 | 5.477 | +2.220 | +68.2% | 3.240, 3.049 to 3.426 | 5.471, 5.394 to 5.558 | 3.416 | 5.653 | +2.237 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 3.183 | 5.633 | +2.450 | +77.0% | 3.210, 2.947 to 3.731 | 5.644, 5.489 to 5.958 | 3.298 | 5.741 | +2.443 |
| field, 2560x1440 Quality, internal 1707x960 | split | 2.179 | 4.269 | +2.090 | +95.9% | 2.182, 1.816 to 2.434 | 4.277, 3.976 to 4.367 | 2.297 | 4.376 | +2.079 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.716 | 0.778 | +0.062 | +8.7% | 0.732, 0.542 to 0.931 | 0.773, 0.643 to 0.875 | 0.838 | 0.897 | +0.059 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.828 | 3.948 | +2.120 | +116.0% | 1.841, 1.668 to 1.986 | 3.926, 3.773 to 4.043 | 1.948 | 4.062 | +2.114 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 6.638 | 11.451 | +4.813 | +72.5% | 6.672, 6.476 to 7.162 | 11.459, 11.262 to 11.645 | 6.888 | 11.702 | +4.814 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 4.838 | 9.000 | +4.162 | +86.0% | 4.839, 4.552 to 5.239 | 9.047, 8.902 to 9.186 | 5.076 | 9.250 | +4.174 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 2.097 | 1.864 | -0.233 | -11.1% | 2.061, 1.665 to 2.379 | 1.854, 1.414 to 2.030 | 2.352 | 2.131 | -0.221 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 3.653 | 7.839 | +4.186 | +114.6% | 3.665, 3.393 to 3.931 | 7.836, 7.739 to 8.084 | 3.907 | 8.099 | +4.192 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 6.421 | 11.120 | +4.699 | +73.2% | 6.437, 6.316 to 6.864 | 11.129, 10.911 to 11.430 | 6.646 | 11.342 | +4.696 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 3.922 | 8.251 | +4.329 | +110.4% | 3.914, 3.599 to 4.144 | 8.255, 8.007 to 8.534 | 4.143 | 8.471 | +4.328 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.139 | 1.029 | -0.110 | -9.7% | 1.107, 0.781 to 1.348 | 1.027, 0.913 to 1.212 | 1.371 | 1.263 | -0.108 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 3.516 | 7.817 | +4.301 | +122.3% | 3.519, 3.253 to 3.714 | 7.828, 7.562 to 7.994 | 3.748 | 8.043 | +4.295 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 8.002 | 13.176 | +5.174 | +64.7% | 8.026, 7.681 to 8.573 | 13.180, 12.975 to 13.530 | 8.225 | 13.374 | +5.149 |
| field, 3456x2234 Native, internal 3342x2160 | split | 6.575 | 11.000 | +4.425 | +67.3% | 6.558, 6.231 to 6.883 | 10.995, 10.780 to 11.148 | 6.813 | 11.203 | +4.390 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 3.156 | 3.162 | +0.006 | +0.2% | 3.141, 2.607 to 3.353 | 3.142, 2.857 to 3.353 | 3.395 | 3.382 | -0.013 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 4.712 | 9.056 | +4.344 | +92.2% | 4.673, 4.493 to 4.860 | 9.065, 8.903 to 9.242 | 4.962 | 9.270 | +4.308 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 7.405 | 12.355 | +4.950 | +66.8% | 7.430, 7.269 to 7.965 | 12.362, 12.253 to 12.612 | 7.620 | 12.554 | +4.934 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 5.072 | 9.503 | +4.431 | +87.4% | 5.095, 4.760 to 5.396 | 9.497, 9.272 to 9.572 | 5.286 | 9.701 | +4.415 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.757 | 1.736 | -0.021 | -1.2% | 1.755, 1.463 to 2.074 | 1.728, 1.579 to 1.922 | 1.978 | 1.955 | -0.023 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.206 | 8.593 | +4.387 | +104.3% | 4.215, 4.095 to 4.395 | 8.570, 8.452 to 8.734 | 4.424 | 8.806 | +4.382 |

Pass pairs, ms from the medians:

| Case | Pair | Base | Head | Head minus base | Base paired median, min to max | Head paired median, min to max |
| --- | --- | ---: | ---: | ---: | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | pass one | 0.510 | 0.494 | -0.016 | 0.520, 0.273 to 0.613 | 0.497, 0.210 to 0.627 |
| boxes, 2560x1440 Native, internal 2560x1440 | pass two | 1.199 | 3.441 | +2.242 | 1.231, 0.851 to 1.405 | 3.442, 3.218 to 3.567 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against fused | -0.876 | -1.140 | -0.264 | -0.935, -1.454 to -0.692 | -1.182, -1.604 to -0.992 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against its upscaling second pass | -0.258 | -0.215 | +0.043 | -0.265, -0.485 to -0.196 | -0.231, -0.453 to -0.020 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass one | 0.092 | 0.182 | +0.090 | 0.144, -0.047 to 0.264 | 0.192, -0.161 to 0.331 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass two | 1.262 | 3.349 | +2.087 | 1.271, 1.179 to 1.389 | 3.374, 3.112 to 3.528 |
| boxes, 2560x1440 Quality, internal 1707x960 | split against fused | -1.250 | -1.357 | -0.107 | -1.266, -1.741 to -1.018 | -1.366, -1.717 to -1.141 |
| field, 2560x1440 Native, internal 2560x1440 | pass one | 0.920 | 0.948 | +0.028 | 0.917, 0.384 to 1.093 | 0.930, 0.713 to 1.025 |
| field, 2560x1440 Native, internal 2560x1440 | pass two | 1.474 | 3.859 | +2.385 | 1.479, 0.916 to 1.637 | 3.821, 3.569 to 3.992 |
| field, 2560x1440 Native, internal 2560x1440 | split against fused | -0.583 | -0.918 | -0.335 | -0.618, -1.269 to -0.422 | -0.951, -1.328 to -0.837 |
| field, 2560x1440 Native, internal 2560x1440 | split against its upscaling second pass | -0.328 | -0.152 | +0.176 | -0.301, -0.704 to -0.152 | -0.174, -0.409 to -0.091 |
| field, 2560x1440 Quality, internal 1707x960 | pass one | 0.352 | 0.321 | -0.031 | 0.340, 0.063 to 0.542 | 0.334, 0.095 to 0.512 |
| field, 2560x1440 Quality, internal 1707x960 | pass two | 1.464 | 3.491 | +2.027 | 1.473, 1.223 to 1.579 | 3.492, 3.251 to 3.586 |
| field, 2560x1440 Quality, internal 1707x960 | split against fused | -1.004 | -1.364 | -0.360 | -1.030, -1.532 to -0.802 | -1.405, -1.670 to -1.229 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass one | 1.185 | 1.161 | -0.024 | 1.153, 0.842 to 1.428 | 1.130, 0.986 to 1.446 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass two | 2.741 | 7.136 | +4.395 | 2.809, 2.502 to 3.148 | 7.206, 7.020 to 7.581 |
| boxes, 3456x2234 Native, internal 3342x2160 | split against fused | -1.801 | -2.451 | -0.650 | -1.958, -2.358 to -1.588 | -2.454, -2.688 to -2.106 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass one | 0.407 | 0.434 | +0.027 | 0.429, 0.029 to 0.589 | 0.478, 0.179 to 0.680 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass two | 2.783 | 7.222 | +4.439 | 2.831, 2.366 to 3.120 | 7.214, 6.980 to 7.616 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split against fused | -2.499 | -2.869 | -0.370 | -2.524, -2.992 to -2.385 | -2.941, -3.142 to -2.590 |
| field, 3456x2234 Native, internal 3342x2160 | pass one | 1.864 | 1.943 | +0.079 | 1.867, 1.558 to 2.224 | 1.919, 1.853 to 2.085 |
| field, 3456x2234 Native, internal 3342x2160 | pass two | 3.419 | 7.837 | +4.418 | 3.456, 3.084 to 3.951 | 7.856, 7.616 to 8.196 |
| field, 3456x2234 Native, internal 3342x2160 | split against fused | -1.426 | -2.176 | -0.750 | -1.419, -1.904 to -1.278 | -2.189, -2.536 to -1.923 |
| field, 3456x2234 Quality, internal 2304x1489 | pass one | 0.866 | 0.910 | +0.044 | 0.786, 0.582 to 1.027 | 0.934, 0.742 to 1.091 |
| field, 3456x2234 Quality, internal 2304x1489 | pass two | 3.314 | 7.767 | +4.453 | 3.322, 3.006 to 3.684 | 7.778, 7.650 to 7.911 |
| field, 3456x2234 Quality, internal 2304x1489 | split against fused | -2.333 | -2.851 | -0.518 | -2.461, -2.826 to -2.172 | -2.897, -3.106 to -2.756 |

Outline fixture, ms a frame (Split entry at both refs):

| Case | Line | Base | Head | Head minus base | Relative | Base blocks min to max or paired median, min to max | Head blocks min to max or paired median, min to max |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| outline, 2560x1440 Native, internal 2560x1440 | outline off | 3.489 | 5.483 | +1.994 | +57.2% | 3.439 to 9.712 | 5.448 to 7.734 |
| outline, 2560x1440 Native, internal 2560x1440 | outline on | 3.758 | 5.803 | +2.045 | +54.4% | 3.642 to 3.854 | 5.727 to 5.938 |
| outline, 2560x1440 Native, internal 2560x1440 | outline cost | 0.269 | 0.320 | +0.051 | +19.0% | 0.290, -6.070 to 0.321 | 0.286, -2.007 to 0.400 |
| outline, 2560x1440 Quality, internal 1707x960 | outline off | 2.591 | 4.521 | +1.930 | +74.5% | 2.536 to 2.673 | 4.484 to 4.570 |
| outline, 2560x1440 Quality, internal 1707x960 | outline on | 2.741 | 4.640 | +1.899 | +69.3% | 2.701 to 2.782 | 4.584 to 4.668 |
| outline, 2560x1440 Quality, internal 1707x960 | outline cost | 0.149 | 0.119 | -0.030 | -20.1% | 0.141, 0.090 to 0.239 | 0.114, 0.037 to 0.177 |

### linux-vulkan

Device line: `VulkanNative on Tesla T4` at both refs.

Mode medians, ms a frame (`off` is the drift control):

| Case | Mode | Base | Head | Head minus base | Relative | Base blocks min to max | Head blocks min to max |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | **off** | 1.037 | 1.016 | -0.021 | -2.0% | 1.003 to 1.148 | 0.977 to 1.222 |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 3.641 | 6.016 | +2.375 | +65.2% | 3.551 to 4.060 | 5.901 to 6.272 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 3.406 | 5.687 | +2.281 | +67.0% | 3.066 to 3.450 | 5.383 to 5.764 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 2.043 | 2.005 | -0.038 | -1.9% | 2.008 to 2.068 | 1.955 to 2.058 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.679 | 5.053 | +2.374 | +88.6% | 2.618 to 2.720 | 4.958 to 5.098 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.470 | 5.784 | +2.314 | +66.7% | 3.426 to 3.508 | 5.673 to 5.963 |
| boxes, 2560x1440 Quality, internal 1707x960 | **off** | 0.612 | 0.588 | -0.024 | -3.9% | 0.600 to 0.703 | 0.575 to 0.787 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 3.167 | 5.260 | +2.093 | +66.1% | 2.944 to 3.484 | 5.192 to 5.491 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 2.565 | 4.757 | +2.192 | +85.5% | 2.302 to 2.633 | 4.401 to 4.817 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.136 | 1.136 | 0.000 | 0.0% | 1.074 to 1.175 | 1.108 to 1.169 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.267 | 4.435 | +2.168 | +95.6% | 2.222 to 2.277 | 4.359 to 4.502 |
| field, 2560x1440 Native, internal 2560x1440 | **off** | 1.506 | 1.461 | -0.045 | -3.0% | 1.469 to 1.689 | 1.404 to 1.642 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 4.866 | 7.673 | +2.807 | +57.7% | 4.686 to 5.214 | 7.556 to 7.894 |
| field, 2560x1440 Native, internal 2560x1440 | split | 4.647 | 7.038 | +2.391 | +51.5% | 4.224 to 4.715 | 6.853 to 7.130 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 3.010 | 3.039 | +0.029 | +1.0% | 2.994 to 3.048 | 2.999 to 3.074 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 3.501 | 5.987 | +2.486 | +71.0% | 3.477 to 3.538 | 5.937 to 6.072 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 4.700 | 7.099 | +2.399 | +51.0% | 4.598 to 4.768 | 7.038 to 7.169 |
| field, 2560x1440 Quality, internal 1707x960 | **off** | 0.867 | 0.811 | -0.056 | -6.5% | 0.810 to 0.927 | 0.787 to 1.037 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 3.913 | 6.322 | +2.409 | +61.6% | 3.736 to 4.317 | 6.110 to 6.579 |
| field, 2560x1440 Quality, internal 1707x960 | split | 3.211 | 5.426 | +2.215 | +69.0% | 2.891 to 3.351 | 5.035 to 5.522 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.654 | 1.690 | +0.036 | +2.2% | 1.592 to 1.715 | 1.659 to 1.712 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.683 | 4.941 | +2.258 | +84.2% | 2.625 to 2.840 | 4.848 to 5.017 |
| boxes, 3456x2234 Native, internal 3342x2160 | **off** | 1.869 | 1.842 | -0.027 | -1.4% | 1.816 to 1.957 | 1.780 to 1.981 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 7.822 | 12.273 | +4.451 | +56.9% | 7.714 to 8.131 | 12.146 to 12.416 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 6.906 | 11.397 | +4.491 | +65.0% | 6.603 to 7.050 | 11.236 to 11.492 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 3.942 | 3.887 | -0.055 | -1.4% | 3.864 to 4.048 | 3.796 to 3.943 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 5.503 | 10.043 | +4.540 | +82.5% | 5.416 to 5.556 | 9.880 to 10.097 |
| boxes, 3456x2234 Quality, internal 2304x1489 | **off** | 1.095 | 1.084 | -0.011 | -1.0% | 1.074 to 1.205 | 1.028 to 1.308 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 6.881 | 11.332 | +4.451 | +64.7% | 6.726 to 7.212 | 11.156 to 11.480 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 5.300 | 9.810 | +4.510 | +85.1% | 4.909 to 5.378 | 9.591 to 9.942 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 2.345 | 2.269 | -0.076 | -3.2% | 2.322 to 2.448 | 2.246 to 2.323 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.634 | 9.195 | +4.561 | +98.4% | 4.565 to 4.803 | 9.000 to 9.329 |
| field, 3456x2234 Native, internal 3342x2160 | **off** | 2.807 | 2.683 | -0.124 | -4.4% | 2.613 to 2.907 | 2.648 to 2.766 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 10.321 | 15.472 | +5.151 | +49.9% | 10.181 to 10.739 | 15.226 to 15.768 |
| field, 3456x2234 Native, internal 3342x2160 | split | 9.265 | 13.906 | +4.641 | +50.1% | 9.026 to 9.439 | 13.743 to 13.954 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 5.840 | 5.869 | +0.029 | +0.5% | 5.699 to 5.933 | 5.803 to 6.103 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 7.304 | 11.836 | +4.532 | +62.0% | 7.200 to 7.341 | 11.778 to 11.914 |
| field, 3456x2234 Quality, internal 2304x1489 | **off** | 1.584 | 1.512 | -0.072 | -4.5% | 1.554 to 1.789 | 1.491 to 1.736 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 8.744 | 13.743 | +4.999 | +57.2% | 8.595 to 9.053 | 13.647 to 13.902 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 6.793 | 11.311 | +4.518 | +66.5% | 6.530 to 6.892 | 11.111 to 11.501 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 3.435 | 3.393 | -0.042 | -1.2% | 3.384 to 3.481 | 3.303 to 3.481 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 5.790 | 10.367 | +4.577 | +79.1% | 5.673 to 5.880 | 10.230 to 10.575 |

Resolve and sharpen at most (mode median minus `off` median, ms), with paired round differences and the submit and drain difference:

| Case | Mode | Base | Head | Head minus base | Relative | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Head minus base |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 2.604 | 5.000 | +2.396 | +92.0% | 2.606, 2.403 to 3.010 | 4.988, 4.719 to 5.242 | 2.660 | 5.053 | +2.393 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 2.370 | 4.671 | +2.301 | +97.1% | 2.366, 2.064 to 2.404 | 4.653, 4.395 to 4.787 | 2.427 | 4.713 | +2.286 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.006 | 0.989 | -0.017 | -1.7% | 1.004, 0.911 to 1.038 | 0.983, 0.756 to 1.029 | 1.070 | 1.054 | -0.016 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.642 | 4.038 | +2.396 | +145.9% | 1.641, 1.540 to 1.675 | 4.035, 3.787 to 4.067 | 1.707 | 4.097 | +2.390 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.433 | 4.768 | +2.335 | +96.0% | 2.431, 2.307 to 2.477 | 4.735, 4.579 to 4.948 | 2.489 | 4.814 | +2.325 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.555 | 4.672 | +2.117 | +82.9% | 2.553, 2.241 to 2.884 | 4.672, 4.411 to 4.903 | 2.601 | 4.698 | +2.097 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 1.953 | 4.169 | +2.216 | +113.5% | 1.953, 1.701 to 2.017 | 4.158, 3.823 to 4.228 | 2.003 | 4.202 | +2.199 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.524 | 0.549 | +0.025 | +4.8% | 0.524, 0.473 to 0.560 | 0.549, 0.354 to 0.587 | 0.576 | 0.596 | +0.020 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.655 | 3.847 | +2.192 | +132.4% | 1.651, 1.564 to 1.671 | 3.854, 3.628 to 3.920 | 1.711 | 3.887 | +2.176 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 3.361 | 6.212 | +2.851 | +84.8% | 3.364, 2.998 to 3.728 | 6.214, 5.957 to 6.469 | 3.390 | 6.231 | +2.841 |
| field, 2560x1440 Native, internal 2560x1440 | split | 3.141 | 5.578 | +2.437 | +77.6% | 3.118, 2.731 to 3.202 | 5.576, 5.386 to 5.667 | 3.174 | 5.609 | +2.435 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.504 | 1.578 | +0.074 | +4.9% | 1.506, 1.325 to 1.552 | 1.573, 1.369 to 1.663 | 1.551 | 1.627 | +0.076 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.996 | 4.526 | +2.530 | +126.8% | 1.999, 1.811 to 2.055 | 4.517, 4.344 to 4.598 | 2.043 | 4.553 | +2.510 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.195 | 5.638 | +2.443 | +76.5% | 3.185, 3.031 to 3.275 | 5.655, 5.508 to 5.724 | 3.227 | 5.668 | +2.441 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 3.046 | 5.511 | +2.465 | +80.9% | 3.028, 2.858 to 3.469 | 5.494, 5.260 to 5.760 | 3.084 | 5.536 | +2.452 |
| field, 2560x1440 Quality, internal 1707x960 | split | 2.344 | 4.615 | +2.271 | +96.9% | 2.348, 2.024 to 2.466 | 4.597, 4.248 to 4.733 | 2.374 | 4.625 | +2.251 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.788 | 0.879 | +0.091 | +11.5% | 0.789, 0.731 to 0.905 | 0.875, 0.655 to 0.925 | 0.832 | 0.912 | +0.080 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.817 | 4.130 | +2.313 | +127.3% | 1.819, 1.763 to 1.968 | 4.120, 3.861 to 4.199 | 1.866 | 4.159 | +2.293 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 5.953 | 10.432 | +4.479 | +75.2% | 5.953, 5.843 to 6.272 | 10.440, 10.261 to 10.561 | 6.022 | 10.508 | +4.486 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 5.038 | 9.555 | +4.517 | +89.7% | 5.046, 4.677 to 5.145 | 9.538, 9.387 to 9.668 | 5.101 | 9.635 | +4.534 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 2.073 | 2.045 | -0.028 | -1.4% | 2.080, 1.954 to 2.219 | 2.045, 1.943 to 2.115 | 2.145 | 2.133 | -0.012 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 3.634 | 8.201 | +4.567 | +125.7% | 3.641, 3.522 to 3.702 | 8.191, 8.074 to 8.278 | 3.709 | 8.283 | +4.574 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 5.786 | 10.248 | +4.462 | +77.1% | 5.776, 5.572 to 6.096 | 10.224, 9.874 to 10.439 | 5.843 | 10.311 | +4.468 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 4.205 | 8.725 | +4.520 | +107.5% | 4.189, 3.781 to 4.279 | 8.731, 8.522 to 8.855 | 4.260 | 8.779 | +4.519 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.250 | 1.185 | -0.065 | -5.2% | 1.243, 1.140 to 1.320 | 1.196, 0.957 to 1.252 | 1.312 | 1.255 | -0.057 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 3.539 | 8.111 | +4.572 | +129.2% | 3.518, 3.440 to 3.676 | 8.107, 7.916 to 8.260 | 3.601 | 8.169 | +4.568 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 7.513 | 12.789 | +5.276 | +70.2% | 7.567, 7.274 to 7.929 | 12.781, 12.474 to 13.062 | 7.560 | 12.846 | +5.286 |
| field, 3456x2234 Native, internal 3342x2160 | split | 6.458 | 11.224 | +4.766 | +73.8% | 6.470, 6.185 to 6.652 | 11.213, 11.032 to 11.298 | 6.504 | 11.274 | +4.770 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 3.033 | 3.186 | +0.153 | +5.0% | 3.032, 2.902 to 3.265 | 3.165, 3.079 to 3.440 | 3.086 | 3.232 | +0.146 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 4.497 | 9.154 | +4.657 | +103.6% | 4.499, 4.318 to 4.702 | 9.148, 9.056 to 9.230 | 4.549 | 9.217 | +4.668 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 7.160 | 12.232 | +5.072 | +70.8% | 7.177, 6.867 to 7.488 | 12.216, 12.026 to 12.411 | 7.190 | 12.253 | +5.063 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 5.209 | 9.799 | +4.590 | +88.1% | 5.209, 4.917 to 5.290 | 9.793, 9.572 to 9.971 | 5.243 | 9.837 | +4.594 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.851 | 1.881 | +0.030 | +1.6% | 1.839, 1.651 to 1.904 | 1.890, 1.631 to 1.969 | 1.896 | 1.917 | +0.021 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.206 | 8.855 | +4.649 | +110.5% | 4.177, 3.912 to 4.311 | 8.819, 8.700 to 9.071 | 4.240 | 8.900 | +4.660 |

Pass pairs, ms from the medians:

| Case | Pair | Base | Head | Head minus base | Base paired median, min to max | Head paired median, min to max |
| --- | --- | ---: | ---: | ---: | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | pass one | 0.727 | 0.634 | -0.093 | 0.740, 0.388 to 0.773 | 0.661, 0.347 to 0.731 |
| boxes, 2560x1440 Native, internal 2560x1440 | pass two | 1.363 | 3.682 | +2.319 | 1.369, 1.059 to 1.400 | 3.708, 3.379 to 3.780 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against fused | -0.234 | -0.329 | -0.095 | -0.387, -0.652 to -0.102 | -0.397, -0.621 to -0.246 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against its upscaling second pass | -0.064 | -0.097 | -0.033 | -0.063, -0.413 to -0.005 | -0.115, -0.369 to 0.079 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass one | 0.298 | 0.322 | +0.024 | 0.308, 0.047 to 0.366 | 0.306, -0.046 to 0.421 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass two | 1.429 | 3.620 | +2.191 | 1.429, 1.227 to 1.476 | 3.596, 3.254 to 3.662 |
| boxes, 2560x1440 Quality, internal 1707x960 | split against fused | -0.602 | -0.504 | +0.098 | -0.629, -0.924 to -0.311 | -0.514, -0.818 to -0.397 |
| field, 2560x1440 Native, internal 2560x1440 | pass one | 1.145 | 1.051 | -0.094 | 1.126, 0.731 to 1.217 | 1.051, 0.851 to 1.162 |
| field, 2560x1440 Native, internal 2560x1440 | pass two | 1.637 | 3.999 | +2.362 | 1.644, 1.223 to 1.702 | 3.990, 3.812 to 4.123 |
| field, 2560x1440 Native, internal 2560x1440 | split against fused | -0.220 | -0.634 | -0.414 | -0.262, -0.623 to 0.029 | -0.638, -0.870 to -0.452 |
| field, 2560x1440 Native, internal 2560x1440 | split against its upscaling second pass | -0.054 | -0.061 | -0.007 | -0.086, -0.480 to 0.045 | -0.075, -0.219 to 0.063 |
| field, 2560x1440 Quality, internal 1707x960 | pass one | 0.528 | 0.485 | -0.043 | 0.471, 0.216 to 0.617 | 0.474, 0.128 to 0.619 |
| field, 2560x1440 Quality, internal 1707x960 | pass two | 1.557 | 3.737 | +2.180 | 1.545, 1.257 to 1.671 | 3.737, 3.323 to 3.821 |
| field, 2560x1440 Quality, internal 1707x960 | split against fused | -0.702 | -0.895 | -0.193 | -0.744, -1.105 to -0.434 | -0.971, -1.200 to -0.588 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass one | 1.403 | 1.354 | -0.049 | 1.408, 1.100 to 1.505 | 1.367, 1.182 to 1.455 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass two | 2.965 | 7.510 | +4.545 | 2.962, 2.598 to 3.141 | 7.493, 7.320 to 7.614 |
| boxes, 3456x2234 Native, internal 3342x2160 | split against fused | -0.916 | -0.876 | +0.040 | -0.919, -1.274 to -0.783 | -0.923, -1.111 to -0.654 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass one | 0.666 | 0.615 | -0.051 | 0.664, 0.106 to 0.784 | 0.598, 0.262 to 0.939 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass two | 2.954 | 7.540 | +4.586 | 2.957, 2.461 to 3.053 | 7.536, 7.325 to 7.687 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split against fused | -1.582 | -1.523 | +0.059 | -1.587, -2.000 to -1.376 | -1.483, -1.784 to -1.275 |
| field, 3456x2234 Native, internal 3342x2160 | pass one | 1.961 | 2.070 | +0.109 | 1.967, 1.799 to 2.153 | 2.040, 1.912 to 2.174 |
| field, 3456x2234 Native, internal 3342x2160 | pass two | 3.425 | 8.037 | +4.612 | 3.433, 3.186 to 3.595 | 8.038, 7.763 to 8.118 |
| field, 3456x2234 Native, internal 3342x2160 | split against fused | -1.056 | -1.566 | -0.510 | -1.085, -1.372 to -0.890 | -1.563, -1.849 to -1.426 |
| field, 3456x2234 Quality, internal 2304x1489 | pass one | 1.004 | 0.944 | -0.060 | 1.000, 0.740 to 1.108 | 0.925, 0.728 to 1.271 |
| field, 3456x2234 Quality, internal 2304x1489 | pass two | 3.358 | 7.918 | +4.560 | 3.374, 3.087 to 3.453 | 7.935, 7.655 to 8.162 |
| field, 3456x2234 Quality, internal 2304x1489 | split against fused | -1.950 | -2.433 | -0.483 | -1.997, -2.241 to -1.849 | -2.421, -2.653 to -2.245 |

Outline fixture, ms a frame (Split entry at both refs):

| Case | Line | Base | Head | Head minus base | Relative | Base blocks min to max or paired median, min to max | Head blocks min to max or paired median, min to max |
| --- | --- | ---: | ---: | ---: | ---: | --- | --- |
| outline, 2560x1440 Native, internal 2560x1440 | outline off | 3.358 | 5.635 | +2.277 | +67.8% | 3.322 to 6.619 | 5.577 to 8.350 |
| outline, 2560x1440 Native, internal 2560x1440 | outline on | 3.680 | 5.948 | +2.268 | +61.6% | 3.611 to 3.719 | 5.847 to 6.002 |
| outline, 2560x1440 Native, internal 2560x1440 | outline cost | 0.322 | 0.312 | -0.010 | -3.1% | 0.306, -2.943 to 0.368 | 0.287, -2.504 to 0.351 |
| outline, 2560x1440 Quality, internal 1707x960 | outline off | 2.515 | 4.606 | +2.091 | +83.1% | 2.439 to 2.540 | 4.573 to 4.687 |
| outline, 2560x1440 Quality, internal 1707x960 | outline on | 2.671 | 4.763 | +2.092 | +78.3% | 2.642 to 2.753 | 4.700 to 4.788 |
| outline, 2560x1440 Quality, internal 1707x960 | outline cost | 0.156 | 0.157 | +0.001 | +0.6% | 0.143, 0.115 to 0.277 | 0.147, 0.076 to 0.200 |

## 3. Reading

What these fixtures measure. Neither fixture enables shadows. `ShadowSettings.Mode` stays `Off`, so `sampleKeyShadow` returns 1.0 and `pcfCascade` never runs at either ref. At head, every temporal frame still writes the R8 visibility MRT with a constant 1.0, runs the history copy and has the resolve read the visibility footprints. Head minus base on the temporal modes therefore covers that traffic in a shadowless scene. A constant 1.0 signal may compress better than a real shadow signal, so a shadowed scene could cost more. That is unverified. These runs do not time the bilinear PCF (base 3x3 point, 9 fetches, against head bilinear 3x3 in a 4x4 footprint, 16 fetches) or the MRT and history carrying a real shadow signal. Proposal B in the request covers that and is not run.

Drift check. The resolve fixture's `Off` mode allocates neither motion nor visibility at either ref, so it carries none of the change. Its median is lower at head than at base in all 24 resolve cases: -0.050 to -0.256 ms (-7.1% to -11.2%) on `windows-d3d11`, -0.036 to -0.174 ms (-2.4% to -8.4%) on `windows-vulkan` and -0.011 to -0.124 ms (-1.0% to -6.5%) on `linux-vulkan`. On every leg the base and head `Off` block ranges overlap. So the two runs drifted by up to about a quarter of a millisecond, and the drift made head faster. It did not make head slower.

What changed. Every mode that records the fused resolve or the split's second pass grew at head on every leg. Resolve and sharpen at most grew by:

| Leg | 2560x1440 display, 14 rows | 3456x2234 display, 12 rows | Split, pass one alone, 8 rows |
| --- | ---: | ---: | ---: |
| `windows-d3d11` | +3.883 to +4.798 | +7.985 to +9.281 | -0.057 to +0.295 |
| `windows-vulkan` | +2.055 to +2.732 | +4.162 to +5.174 | -0.233 to +0.062 |
| `linux-vulkan` | +2.117 to +2.851 | +4.462 to +5.276 | -0.065 to +0.153 |

- On every leg the submit and drain difference moved with resolve and sharpen at most, by +3.856 to +9.269 ms on `windows-d3d11`, +2.032 to +5.149 ms on `windows-vulkan` and +2.097 to +5.286 ms on `linux-vulkan`. The growth is inside Submit and `WaitForIdle`, which is GPU work and its submission, and not in recording.
- The growth sits in the second pass. The pass two pair grew by +3.900 to +8.924 ms on `windows-d3d11`, +2.027 to +4.453 on `windows-vulkan` and +2.180 to +4.612 on `linux-vulkan`. The pass one pair moved by -0.249 to +0.109 ms across the legs.
- The growth follows the display size, not the internal size. Native and Quality at one display grew by similar amounts although their internal sizes differ. For the fused, split and pass two alone modes, each 3456x2234 row grew by 1.83 to 2.12 times its 2560x1440 counterpart. The display pixel count ratio is 7,720,704 to 3,686,400, about 2.09.
- Row by row, `windows-d3d11` grew by 1.67 to 2.14 times `windows-vulkan` and 1.66 to 1.99 times `linux-vulkan`.
- In every resolve row that grew, on every leg, the lowest head block is above the highest base block.
- The outline fixture's absolute medians are two more whole temporal frames at 2560x1440. Outline off grew by +3.545 and +3.925 ms on `windows-d3d11`, +1.930 and +1.994 on `windows-vulkan` and +2.091 and +2.277 on `linux-vulkan`. Outline on grew by about the same amounts. Its subtraction cancels the change, and the outline cost moved by -0.041 to +0.058 ms.

Where noise is plausibly as large as the difference, based only on these prints:

- On every leg, for the split's pass one alone, the pass one pair, the split against its upscaling second pass pair and the outline cost. These head minus base differences run from -0.280 to +0.295 ms, comparable to the `Off` drift, and in every one of these rows the base and head paired round ranges overlap.
- The split against fused pair on `windows-d3d11` (-0.438 to +0.125 ms, mixed sign, paired ranges overlap in 8 of 8) and on `linux-vulkan` (-0.510 to +0.098 ms, mixed sign, overlap in 6 of 8). On `windows-vulkan` it moved by -0.107 to -0.750 ms, negative in all 8 cases with overlap in 7 of 8. There, fused grew more than split in every case, which the drift alone does not plainly explain.
- Not for the second-pass growth on any leg. The smallest such median difference, +1.993 ms on `windows-vulkan`, is about eight times the largest `Off` drift on any leg, -0.256 ms on `windows-d3d11`. Its block ranges do not overlap, and the pass two pair's paired round ranges do not overlap in any of the 24 cases.
- `windows-d3d11` is the noisiest leg in these prints. The base `Off` blocks reach 7.665 ms against a 2.175 ms median in the boxes 3456x2234 Native case. The head outline Native on blocks include one at 194.594 ms, which gives a paired max of 178.891. The base test step took 22.8984 min against 6.2060 min at head, and the logs do not say why. A single outlier block does not move a median of 15 blocks by its size.
- The outline Native off row on every leg has one high base block, 17.465 ms on `windows-d3d11`, 9.712 on `windows-vulkan` and 6.619 on `linux-vulkan`. That is the only grown row whose block ranges overlap.

One paired run cannot resolve differences smaller than the drift. The prints do not separate the MRT write, the history copy and the resolve reads within the second-pass growth.

## 4. Budget

The owner set no cost budget for this fix. This record states the numbers and gives no pass or fail verdict. The resolve fixture's printed 1.0 ms budget lines come from the temporal resolve design and are not a budget for this fix.
