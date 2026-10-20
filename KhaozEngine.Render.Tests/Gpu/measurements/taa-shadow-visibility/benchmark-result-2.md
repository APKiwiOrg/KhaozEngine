# TAA shadow visibility cost benchmark, second bench

Owner-approved second T4 bench, Task 7f.6. Extraction only from the three completed runs below. No workflow, test, timing or GPU workload was dispatched or rerun for this record. The [request](benchmark-request.md) defines the fixtures, and [the first bench](benchmark-result.md) remains unchanged.

Status: the Task 7f head is much faster than the first head, but no T4 leg meets the owner's at most 0.5 ms resolve-growth budget in every 1440p case. Both cost runs succeeded on every leg. Both Vulkan oracle legs passed all 22 cases. Direct3D 11 passed 14 and failed 8, four colour comparisons and four state comparisons. The state differences are red confidence, not green lock. The failing frame index is not printed, so it is unknown from this evidence.

## 1. Provenance

| Item | Base cost | Head cost | Head oracles |
| --- | --- | --- | --- |
| Run | [39407663377](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407663377) | [39407679136](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407679136) | [39407694921](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407694921) |
| Input ref, checked out | `562033e740cad672a92d999557c75ee3e9721f1a`, log `HEAD is now at 562033e` | `1e4fc71a2055144fd03d0ed87515ad1900349561`, log `HEAD is now at 1e4fc71` | same head, log `HEAD is now at 1e4fc71` |
| Workflow | `gpu-hosted-bench.yml`, `workflow_dispatch` from main at `64426c24642ed171cde3ffc5f76e1ed70e2c7d34` | same | same |
| Conclusion | success on all three legs | success on all three legs | Vulkan success, D3D11 failure |
| Created (UTC) | 2026-10-20T10:24:02Z | 2026-10-20T10:24:10Z | 2026-10-20T10:24:18Z |
| Cases per leg | 12 passed, none failed or skipped | 12 passed, none failed or skipped | 22 passed on each Vulkan leg, D3D11 14 passed and 8 failed |

Base is `v21.7.1` (`6c6529589f0e8c22234abd6973be2d57b60f773e`) plus only `TemporalShadowedFrameCostPerfGpuTests.cs`. The base-to-head diff is empty for the resolve, outline and shadowed cost files, `TemporalFixture.cs` and `TemporalAcceptanceScenes.cs`. The shadowed file SHA-256 at both refs is `093f926314100526d85c6f90219d833aa12c30bc735f14e391ebd9ca3c26ff33`. The workflow ref above is distinct from each tested input ref.

Cost filter: `FullyQualifiedName~TemporalResolveCostPerfGpuTests|FullyQualifiedName~TemporalOutlineCostPerfGpuTests|FullyQualifiedName~TemporalShadowedFrameCostPerfGpuTests`. Oracle filter: `FullyQualifiedName~TemporalVisibilityEquivalenceGpuTests|FullyQualifiedName~TemporalShadowedFramePathShareGpuTests|FullyQualifiedName~TemporalVisibilityResolveAbGpuTests`. The device probe is a separate passing test before each filter. No software-rasteriser or `not measured` line appears.

| Leg | Printed device and backend | Driver in all three jobs |
| --- | --- | --- |
| windows-d3d11 | `Direct3D11Native on NVIDIA Tesla T4` | GRID 596.86 installed, `nvidia-smi` Driver Version 596.86, CUDA 13.2, WDDM. Before install 471.11 TCC and `Win32_VideoController` DriverVersion 30.0.14.7111. |
| windows-vulkan | `VulkanNative on Tesla T4` | Same GRID 596.86 and CUDA 13.2. No Windows `vulkaninfo` driver string is printed. |
| linux-vulkan | `VulkanNative on Tesla T4` | `nvidia-smi` Driver Version 590.48.01, CUDA 13.1. `vulkaninfo` driverName NVIDIA, driverInfo 590.48.01, driverVersion 590.48.1.0, DRIVER_ID_NVIDIA_PROPRIETARY. |

The capability probe prints `aniso=True lodBias=True` on every leg. Device names and driver strings match between the base, head and oracle job for each leg.

| Leg | Run kind | Job | UTC start to end, 2026-10-20 | Job duration | Filtered test run total | Conclusion |
| --- | --- | --- | --- | --- | --- | --- |
| windows-d3d11 | base | [118577469767](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407663377/job/118577469767) | 10:25:39 to 10:34:54 | 9 min 15 s | 2.1091 Minutes | success |
| windows-d3d11 | head | [118577529291](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407679136/job/118577529291) | 11:08:41 to 11:19:19 | 10 min 38 s | 2.3101 Minutes | success |
| windows-d3d11 | oracles | [118577576568](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407694921/job/118577576568) | 11:56:16 to 12:12:50 | 16 min 34 s | 1.5967 Minutes | failure |
| windows-vulkan | base | [118577469752](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407663377/job/118577469752) | 10:34:57 to 11:08:37 | 33 min 40 s | 1.4137 Minutes | success |
| windows-vulkan | head | [118577529385](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407679136/job/118577529385) | 11:19:22 to 11:42:08 | 22 min 46 s | 1.5701 Minutes | success |
| windows-vulkan | oracles | [118577576521](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407694921/job/118577576521) | 11:43:44 to 11:55:35 | 11 min 51 s | 41.6172 Seconds | success |
| linux-vulkan | base | [118577469625](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407663377/job/118577469625) | 10:24:57 to 10:28:06 | 3 min 9 s | 1.2671 Minutes | success |
| linux-vulkan | head | [118577529321](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407679136/job/118577529321) | 10:28:09 to 10:31:39 | 3 min 30 s | 1.4243 Minutes | success |
| linux-vulkan | oracles | [118577576564](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39407694921/job/118577576564) | 10:31:43 to 10:34:33 | 2 min 50 s | 40.1868 Seconds | success |

The two Windows legs share one capped runner. Their sequence was base D3D11, base Vulkan, head D3D11, head Vulkan, oracle Vulkan, oracle D3D11. Linux ran base, head, then oracles. Setup and driver time are inside job durations and outside the printed frame costs.

Each GPU job log was fetched once with `gh api --allow-escape-sequences repos/APKiwiOrg/KhaozEngine/actions/jobs/<id>/logs` under `/tmp/grimhollow-taa-bench2/`. All nine calls succeeded without a retry. Logs are deleted after extraction. First-bench comparisons below use only the checked-in tables for base [39317665751](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39317665751) and head [39317684399](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39317684399).

## 2. Resolve and outline costs by leg

All costs are CPU wall clock in ms per frame, not GPU timestamp queries. Each mode has 15 blocks of 12 frames, after 2 warm frames for resolve and 3 for outline and yard. Frames include recording, Submit, WaitForIdle and Present. Resolve order rotates, outline order alternates. `Off` draws at the internal size. Resolve and sharpen at most is the printed mode median minus that run's Off median. Paired columns are the printed within-round difference median and min to max, not a confidence interval. Submit and drain is the printed difference from Off.

Growth is head minus base from the printed three-decimal values. Relative growth divides by base. First growth is the matching first-bench head-minus-base value. Saved is first growth minus second growth, an observed reduction across two paired benches rather than an isolated timing of each Task 7f change. Display 3456x2234 Native uses internal 3342x2160 because of the render cap. The policy prints Split for every cost case at both refs.

### windows-d3d11

Mode medians, including the Off drift control:

| Case | Mode | Base | Head | Growth | Relative | First growth | Base blocks min to max | Head blocks min to max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | off | 1.212 | 1.151 | -0.061 | -5.0% | -0.135 | 1.066 to 1.314 | 1.086 to 1.218 |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 3.538 | 4.021 | +0.483 | +13.7% | +4.387 | 3.382 to 4.040 | 3.935 to 4.426 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 3.504 | 3.794 | +0.290 | +8.3% | +3.951 | 3.097 to 3.676 | 3.310 to 3.938 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 2.364 | 2.431 | +0.067 | +2.8% | -0.058 | 2.259 to 2.507 | 2.370 to 2.565 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.692 | 2.990 | +0.298 | +11.1% | +4.184 | 2.559 to 2.840 | 2.963 to 3.154 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.583 | 3.931 | +0.348 | +9.7% | +4.077 | 3.488 to 3.775 | 3.861 to 4.136 |
| boxes, 2560x1440 Quality, internal 1707x960 | off | 0.718 | 0.683 | -0.035 | -4.9% | -0.050 | 0.604 to 0.769 | 0.596 to 0.727 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.988 | 3.436 | +0.448 | +15.0% | +3.967 | 2.873 to 3.410 | 3.346 to 3.776 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 2.477 | 2.867 | +0.390 | +15.7% | +3.834 | 2.165 to 2.627 | 2.596 to 2.974 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.319 | 1.354 | +0.035 | +2.7% | -0.067 | 1.232 to 1.436 | 1.284 to 1.473 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.157 | 2.539 | +0.382 | +17.7% | +3.871 | 2.100 to 2.285 | 2.473 to 2.660 |
| field, 2560x1440 Native, internal 2560x1440 | off | 1.759 | 1.569 | -0.190 | -10.8% | -0.165 | 1.689 to 1.854 | 1.504 to 1.652 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 4.792 | 6.353 | +1.561 | +32.6% | +4.634 | 4.525 to 5.359 | 6.286 to 6.687 |
| field, 2560x1440 Native, internal 2560x1440 | split | 4.513 | 5.918 | +1.405 | +31.1% | +4.258 | 3.970 to 4.716 | 5.542 to 6.018 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 3.170 | 3.092 | -0.078 | -2.5% | -0.065 | 3.090 to 3.318 | 2.968 to 3.183 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 3.633 | 5.075 | +1.442 | +39.7% | +4.507 | 3.558 to 3.895 | 4.936 to 5.123 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 4.655 | 6.016 | +1.361 | +29.2% | +4.316 | 4.577 to 4.857 | 5.857 to 6.159 |
| field, 2560x1440 Quality, internal 1707x960 | off | 1.019 | 0.908 | -0.111 | -10.9% | -0.104 | 0.939 to 1.061 | 0.856 to 0.978 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 3.822 | 5.217 | +1.395 | +36.5% | +4.000 | 3.694 to 4.328 | 5.115 to 5.467 |
| field, 2560x1440 Quality, internal 1707x960 | split | 3.098 | 4.544 | +1.446 | +46.7% | +3.994 | 2.725 to 3.189 | 4.283 to 4.614 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.796 | 1.729 | -0.067 | -3.7% | -0.104 | 1.709 to 1.900 | 1.682 to 1.833 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.722 | 4.082 | +1.360 | +50.0% | +4.086 | 2.672 to 2.869 | 3.950 to 4.261 |
| boxes, 3456x2234 Native, internal 3342x2160 | off | 2.200 | 2.090 | -0.110 | -5.0% | -0.195 | 1.990 to 6.145 | 1.946 to 9.349 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 7.636 | 8.561 | +0.925 | +12.1% | +8.621 | 7.459 to 13.359 | 8.060 to 11.159 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 6.965 | 7.876 | +0.911 | +13.1% | +8.471 | 6.628 to 7.289 | 7.409 to 8.039 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 4.624 | 4.655 | +0.031 | +0.7% | -0.070 | 4.330 to 4.824 | 4.464 to 4.986 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 5.445 | 6.254 | +0.809 | +14.9% | +8.464 | 5.103 to 5.613 | 6.132 to 6.376 |
| boxes, 3456x2234 Quality, internal 2304x1489 | off | 1.322 | 1.234 | -0.088 | -6.7% | -0.138 | 1.257 to 1.399 | 1.211 to 1.276 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 6.420 | 7.477 | +1.057 | +16.5% | +8.179 | 6.262 to 6.922 | 7.300 to 7.843 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 5.199 | 5.995 | +0.796 | +15.3% | +7.847 | 4.698 to 5.334 | 5.367 to 6.151 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 2.768 | 2.769 | +0.001 | +0.0% | -0.196 | 2.627 to 2.936 | 2.632 to 2.879 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.442 | 5.194 | +0.752 | +16.9% | +7.958 | 4.320 to 4.631 | 5.039 to 5.282 |
| field, 3456x2234 Native, internal 3342x2160 | off | 3.082 | 2.863 | -0.219 | -7.1% | -0.256 | 2.940 to 3.240 | 2.743 to 2.984 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 10.194 | 12.967 | +2.773 | +27.2% | +8.836 | 10.014 to 10.722 | 12.755 to 13.034 |
| field, 3456x2234 Native, internal 3342x2160 | split | 9.308 | 11.608 | +2.300 | +24.7% | +8.962 | 7.126 to 9.497 | 11.468 to 11.813 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 6.091 | 6.005 | -0.086 | -1.4% | +0.039 | 5.735 to 7.159 | 5.878 to 6.084 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 7.435 | 9.808 | +2.373 | +31.9% | +9.025 | 7.323 to 7.913 | 9.656 to 9.937 |
| field, 3456x2234 Quality, internal 2304x1489 | off | 1.875 | 1.659 | -0.216 | -11.5% | -0.156 | 1.791 to 1.979 | 1.620 to 1.727 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 8.546 | 11.194 | +2.648 | +31.0% | +8.433 | 8.294 to 8.996 | 11.029 to 11.333 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 6.598 | 9.156 | +2.558 | +38.8% | +8.459 | 6.212 to 6.860 | 8.896 to 9.268 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 3.667 | 3.439 | -0.228 | -6.2% | -0.084 | 3.417 to 3.906 | 3.384 to 3.529 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 5.872 | 8.324 | +2.452 | +41.8% | +8.552 | 5.688 to 5.962 | 8.103 to 8.554 |

Resolve and sharpen at most, paired round spread, and submit and drain:

| Case | Mode | Base | Head | Growth | Relative | First growth | Saved vs first | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Growth |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 2.326 | 2.869 | +0.543 | +23.3% | +4.522 | +3.979 | 2.326, 2.152 to 2.803 | 2.854, 2.802 to 3.240 | 2.312 | 2.866 | +0.554 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 2.292 | 2.643 | +0.351 | +15.3% | +4.084 | +3.733 | 2.260, 1.866 to 2.436 | 2.614, 2.203 to 2.802 | 2.285 | 2.640 | +0.355 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.152 | 1.280 | +0.128 | +11.1% | +0.077 | -0.051 | 1.122, 1.045 to 1.441 | 1.280, 1.176 to 1.458 | 1.147 | 1.278 | +0.131 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.480 | 1.839 | +0.359 | +24.3% | +4.319 | +3.960 | 1.483, 1.347 to 1.610 | 1.873, 1.772 to 1.966 | 1.471 | 1.837 | +0.366 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.371 | 2.779 | +0.408 | +17.2% | +4.212 | +3.804 | 2.363, 2.318 to 2.461 | 2.777, 2.686 to 2.985 | 2.361 | 2.776 | +0.415 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.271 | 2.754 | +0.483 | +21.3% | +4.016 | +3.533 | 2.269, 2.181 to 2.687 | 2.753, 2.678 to 3.108 | 2.268 | 2.745 | +0.477 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 1.760 | 2.185 | +0.425 | +24.1% | +3.883 | +3.458 | 1.755, 1.447 to 1.937 | 2.193, 1.895 to 2.280 | 1.756 | 2.184 | +0.428 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.602 | 0.672 | +0.070 | +11.6% | -0.017 | -0.087 | 0.616, 0.501 to 0.734 | 0.676, 0.583 to 0.795 | 0.601 | 0.670 | +0.069 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.439 | 1.856 | +0.417 | +29.0% | +3.921 | +3.504 | 1.462, 1.369 to 1.567 | 1.839, 1.778 to 2.064 | 1.429 | 1.856 | +0.427 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 3.033 | 4.784 | +1.751 | +57.7% | +4.798 | +3.047 | 3.059, 2.780 to 3.618 | 4.780, 4.690 to 5.117 | 3.016 | 4.762 | +1.746 |
| field, 2560x1440 Native, internal 2560x1440 | split | 2.754 | 4.349 | +1.595 | +57.9% | +4.423 | +2.828 | 2.772, 2.116 to 3.006 | 4.334, 3.891 to 4.457 | 2.749 | 4.331 | +1.582 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.411 | 1.523 | +0.112 | +7.9% | +0.100 | -0.012 | 1.426, 1.345 to 1.579 | 1.505, 1.369 to 1.608 | 1.405 | 1.508 | +0.103 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.874 | 3.506 | +1.632 | +87.1% | +4.671 | +3.039 | 1.892, 1.704 to 2.171 | 3.469, 3.322 to 3.620 | 1.868 | 3.491 | +1.623 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.896 | 4.447 | +1.551 | +53.6% | +4.481 | +2.930 | 2.916, 2.800 to 3.112 | 4.433, 4.205 to 4.645 | 2.885 | 4.431 | +1.546 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 2.804 | 4.309 | +1.505 | +53.7% | +4.103 | +2.598 | 2.857, 2.688 to 3.322 | 4.317, 4.173 to 4.535 | 2.777 | 4.285 | +1.508 |
| field, 2560x1440 Quality, internal 1707x960 | split | 2.080 | 3.637 | +1.557 | +74.9% | +4.098 | +2.541 | 2.093, 1.687 to 2.185 | 3.644, 3.370 to 3.719 | 2.060 | 3.618 | +1.558 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.777 | 0.822 | +0.045 | +5.8% | 0.000 | -0.045 | 0.772, 0.659 to 0.962 | 0.823, 0.741 to 0.947 | 0.760 | 0.805 | +0.045 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.703 | 3.175 | +1.472 | +86.4% | +4.190 | +2.718 | 1.711, 1.644 to 1.892 | 3.197, 3.063 to 3.315 | 1.691 | 3.158 | +1.467 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 5.436 | 6.471 | +1.035 | +19.0% | +8.815 | +7.780 | 5.470, 5.253 to 7.214 | 6.472, 1.811 to 6.821 | 5.415 | 6.468 | +1.053 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 4.765 | 5.786 | +1.021 | +21.4% | +8.666 | +7.645 | 4.748, 0.808 to 5.102 | 5.786, -1.426 to 6.053 | 4.766 | 5.784 | +1.018 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 2.424 | 2.565 | +0.141 | +5.8% | +0.125 | -0.016 | 2.396, -1.476 to 2.601 | 2.597, -4.709 to 2.844 | 2.414 | 2.562 | +0.148 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 3.245 | 4.164 | +0.919 | +28.3% | +8.659 | +7.740 | 3.254, -0.701 to 3.492 | 4.182, -3.086 to 4.317 | 3.228 | 4.163 | +0.935 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 5.098 | 6.243 | +1.145 | +22.5% | +8.318 | +7.173 | 5.080, 4.890 to 5.629 | 6.233, 6.064 to 6.623 | 5.097 | 6.238 | +1.141 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 3.878 | 4.762 | +0.884 | +22.8% | +7.985 | +7.101 | 3.886, 3.358 to 4.031 | 4.775, 4.104 to 4.927 | 3.878 | 4.757 | +0.879 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.447 | 1.535 | +0.088 | +6.1% | -0.057 | -0.145 | 1.483, 1.306 to 1.578 | 1.550, 1.385 to 1.616 | 1.447 | 1.533 | +0.086 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 3.120 | 3.960 | +0.840 | +26.9% | +8.096 | +7.256 | 3.118, 2.960 to 3.300 | 3.965, 3.828 to 4.065 | 3.115 | 3.956 | +0.841 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 7.112 | 10.104 | +2.992 | +42.1% | +9.093 | +6.101 | 7.140, 6.902 to 7.605 | 10.072, 9.967 to 10.174 | 7.051 | 10.076 | +3.025 |
| field, 3456x2234 Native, internal 3342x2160 | split | 6.226 | 8.745 | +2.519 | +40.5% | +9.218 | +6.699 | 6.250, 3.916 to 6.554 | 8.749, 8.613 to 9.025 | 6.164 | 8.728 | +2.564 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 3.009 | 3.142 | +0.133 | +4.4% | +0.295 | +0.162 | 3.009, 2.549 to 3.949 | 3.142, 2.970 to 3.252 | 2.955 | 3.126 | +0.171 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 4.352 | 6.945 | +2.593 | +59.6% | +9.281 | +6.688 | 4.414, 4.195 to 4.702 | 6.963, 6.712 to 7.194 | 4.334 | 6.923 | +2.589 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 6.671 | 9.535 | +2.864 | +42.9% | +8.588 | +5.724 | 6.685, 6.368 to 7.106 | 9.557, 9.327 to 9.674 | 6.608 | 9.517 | +2.909 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 4.723 | 7.497 | +2.774 | +58.7% | +8.613 | +5.839 | 4.787, 4.307 to 4.933 | 7.489, 7.223 to 7.572 | 4.665 | 7.463 | +2.798 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.791 | 1.780 | -0.011 | -0.6% | +0.072 | +0.083 | 1.801, 1.527 to 2.015 | 1.800, 1.686 to 1.892 | 1.765 | 1.764 | -0.001 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 3.996 | 6.665 | +2.669 | +66.8% | +8.707 | +6.038 | 3.989, 3.797 to 4.100 | 6.672, 6.483 to 6.895 | 3.942 | 6.652 | +2.710 |

Pass pairs, differences of the printed mode medians:

| Case | Pair | Base | Head | Growth | First growth | Base paired median, min to max | Head paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | pass one | 0.812 | 0.804 | -0.008 | -0.234 | 0.819, 0.256 to 0.985 | 0.785, 0.330 to 0.900 |
| boxes, 2560x1440 Native, internal 2560x1440 | pass two | 1.140 | 1.363 | +0.223 | +4.007 | 1.131, 0.672 to 1.285 | 1.367, 0.745 to 1.561 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against fused | -0.034 | -0.227 | -0.193 | -0.438 | -0.066, -0.602 to 0.125 | -0.247, -0.661 to -0.098 |
| boxes, 2560x1440 Native, internal 2560x1440 | split at the display's size against its upscaling second pass | -0.079 | -0.137 | -0.058 | -0.127 | -0.098, -0.499 to 0.074 | -0.145, -0.574 to 0.038 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass one | 0.320 | 0.328 | +0.008 | -0.038 | 0.308, -0.086 to 0.488 | 0.305, 0.065 to 0.435 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass two | 1.158 | 1.513 | +0.355 | +3.900 | 1.127, 0.905 to 1.365 | 1.495, 1.279 to 1.580 |
| boxes, 2560x1440 Quality, internal 1707x960 | split against fused | -0.511 | -0.569 | -0.058 | -0.134 | -0.568, -0.932 to -0.284 | -0.603, -0.919 to -0.410 |
| field, 2560x1440 Native, internal 2560x1440 | pass one | 0.880 | 0.843 | -0.037 | -0.249 | 0.880, 0.407 to 1.099 | 0.838, 0.444 to 1.034 |
| field, 2560x1440 Native, internal 2560x1440 | pass two | 1.343 | 2.825 | +1.482 | +4.323 | 1.348, 0.677 to 1.616 | 2.831, 2.359 to 2.943 |
| field, 2560x1440 Native, internal 2560x1440 | split against fused | -0.279 | -0.436 | -0.157 | -0.375 | -0.348, -0.846 to -0.005 | -0.583, -0.895 to -0.348 |
| field, 2560x1440 Native, internal 2560x1440 | split at the display's size against its upscaling second pass | -0.141 | -0.098 | +0.043 | -0.058 | -0.131, -0.785 to 0.012 | -0.162, -0.345 to 0.028 |
| field, 2560x1440 Quality, internal 1707x960 | pass one | 0.376 | 0.462 | +0.086 | -0.092 | 0.354, -0.030 to 0.514 | 0.426, 0.163 to 0.635 |
| field, 2560x1440 Quality, internal 1707x960 | pass two | 1.302 | 2.815 | +1.513 | +4.098 | 1.272, 0.969 to 1.465 | 2.788, 2.528 to 2.881 |
| field, 2560x1440 Quality, internal 1707x960 | split against fused | -0.724 | -0.673 | +0.051 | -0.006 | -0.780, -1.229 to -0.533 | -0.701, -0.984 to -0.512 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass one | 1.520 | 1.622 | +0.102 | +0.007 | 1.508, 1.213 to 1.941 | 1.547, 1.186 to 1.825 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass two | 2.341 | 3.222 | +0.881 | +8.541 | 2.400, 1.927 to 2.662 | 3.275, 2.423 to 3.351 |
| boxes, 3456x2234 Native, internal 3342x2160 | split against fused | -0.671 | -0.684 | -0.013 | -0.150 | -0.730, -6.406 to -0.249 | -0.709, -3.237 to -0.423 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass one | 0.757 | 0.802 | +0.045 | -0.112 | 0.737, 0.257 to 0.955 | 0.775, 0.257 to 0.988 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass two | 2.431 | 3.227 | +0.796 | +8.042 | 2.427, 1.792 to 2.641 | 3.225, 2.488 to 3.449 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split against fused | -1.221 | -1.481 | -0.260 | -0.334 | -1.308, -1.867 to -0.972 | -1.489, -2.129 to -1.186 |
| field, 3456x2234 Native, internal 3342x2160 | pass one | 1.873 | 1.800 | -0.073 | -0.062 | 1.735, -0.786 to 2.140 | 1.773, 1.650 to 2.067 |
| field, 3456x2234 Native, internal 3342x2160 | pass two | 3.216 | 5.603 | +2.387 | +8.924 | 3.211, -0.033 to 3.576 | 5.647, 5.430 to 5.791 |
| field, 3456x2234 Native, internal 3342x2160 | split against fused | -0.887 | -1.359 | -0.472 | +0.125 | -1.079, -3.039 to -0.590 | -1.316, -1.527 to -1.120 |
| field, 3456x2234 Quality, internal 2304x1489 | pass one | 0.726 | 0.832 | +0.106 | -0.094 | 0.780, 0.350 to 0.920 | 0.801, 0.491 to 0.938 |
| field, 3456x2234 Quality, internal 2304x1489 | pass two | 2.931 | 5.717 | +2.786 | +8.542 | 2.985, 2.307 to 3.327 | 5.680, 5.389 to 5.855 |
| field, 3456x2234 Quality, internal 2304x1489 | split against fused | -1.948 | -2.038 | -0.090 | +0.025 | -1.957, -2.398 to -1.434 | -2.059, -2.400 to -1.815 |

Outline fixture, Split entry. The outline increment subtracts two temporal frames and cancels their common visibility cost:

| Case | Line | Base | Head | Growth | Relative | First growth | Base block range or paired median, min to max | Head block range or paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| outline, 2560x1440 Native, internal 2560x1440 | outline off | 3.521 | 3.768 | +0.247 | +7.0% | +3.925 | 3.396 to 5.166 | 3.731 to 5.265 |
| outline, 2560x1440 Native, internal 2560x1440 | outline on | 3.854 | 4.083 | +0.229 | +5.9% | +3.884 | 3.768 to 3.992 | 4.039 to 5.643 |
| outline, 2560x1440 Native, internal 2560x1440 | outline cost | 0.334 | 0.315 | -0.019 | -5.7% | -0.041 | 0.341, -1.398 to 0.484 | 0.332, 0.233 to 0.438 |
| outline, 2560x1440 Quality, internal 1707x960 | outline off | 2.546 | 2.807 | +0.261 | +10.3% | +3.545 | 2.468 to 2.649 | 2.769 to 2.997 |
| outline, 2560x1440 Quality, internal 1707x960 | outline on | 2.680 | 2.954 | +0.274 | +10.2% | +3.604 | 2.600 to 2.840 | 2.904 to 3.014 |
| outline, 2560x1440 Quality, internal 1707x960 | outline cost | 0.135 | 0.147 | +0.012 | +8.9% | +0.058 | 0.151, 0.059 to 0.372 | 0.137, 0.017 to 0.202 |

### windows-vulkan

Mode medians, including the Off drift control:

| Case | Mode | Base | Head | Growth | Relative | First growth | Base blocks min to max | Head blocks min to max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | off | 1.239 | 1.259 | +0.020 | +1.6% | -0.036 | 1.056 to 1.288 | 1.092 to 1.324 |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 4.189 | 4.535 | +0.346 | +8.3% | +2.437 | 4.123 to 4.859 | 4.424 to 5.001 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 3.384 | 3.395 | +0.011 | +0.3% | +2.173 | 3.037 to 3.509 | 3.176 to 3.476 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 2.155 | 2.187 | +0.032 | +1.5% | -0.067 | 1.997 to 2.193 | 1.993 to 2.257 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.850 | 2.862 | +0.012 | +0.4% | +2.189 | 2.768 to 2.909 | 2.798 to 2.999 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.620 | 4.111 | +0.491 | +13.6% | +2.131 | 3.559 to 3.655 | 4.024 to 4.146 |
| boxes, 2560x1440 Quality, internal 1707x960 | off | 0.750 | 0.764 | +0.014 | +1.9% | -0.063 | 0.617 to 0.769 | 0.626 to 0.875 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 3.719 | 4.084 | +0.365 | +9.8% | +2.191 | 3.589 to 4.191 | 3.952 to 4.460 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 2.483 | 3.040 | +0.557 | +22.4% | +2.084 | 2.295 to 2.545 | 2.859 to 3.154 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.187 | 1.198 | +0.011 | +0.9% | -0.003 | 1.114 to 1.286 | 1.149 to 1.294 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.349 | 2.846 | +0.497 | +21.2% | +1.993 | 2.206 to 2.398 | 2.772 to 2.933 |
| field, 2560x1440 Native, internal 2560x1440 | off | 1.692 | 1.638 | -0.054 | -3.2% | -0.068 | 1.502 to 1.779 | 1.404 to 1.699 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 5.171 | 6.579 | +1.408 | +27.2% | +2.665 | 5.031 to 5.828 | 6.479 to 7.176 |
| field, 2560x1440 Native, internal 2560x1440 | split | 4.634 | 5.290 | +0.656 | +14.2% | +2.330 | 4.230 to 4.843 | 5.006 to 5.528 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 3.116 | 3.095 | -0.021 | -0.7% | -0.056 | 2.983 to 3.286 | 2.918 to 3.183 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 3.679 | 4.372 | +0.693 | +18.8% | +2.302 | 3.651 to 3.728 | 4.316 to 4.444 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 4.945 | 5.780 | +0.835 | +16.9% | +2.154 | 4.870 to 5.003 | 5.661 to 5.841 |
| field, 2560x1440 Quality, internal 1707x960 | off | 1.031 | 0.981 | -0.050 | -4.8% | -0.077 | 0.976 to 1.193 | 0.948 to 1.092 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 4.234 | 5.571 | +1.337 | +31.6% | +2.372 | 4.111 to 4.816 | 5.477 to 5.933 |
| field, 2560x1440 Quality, internal 1707x960 | split | 3.221 | 4.122 | +0.901 | +28.0% | +2.013 | 2.976 to 3.362 | 3.913 to 4.298 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.754 | 1.736 | -0.018 | -1.0% | -0.015 | 1.640 to 1.810 | 1.619 to 1.782 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.852 | 3.753 | +0.901 | +31.6% | +2.044 | 2.718 to 2.995 | 3.536 to 3.974 |
| boxes, 3456x2234 Native, internal 3342x2160 | off | 2.046 | 2.029 | -0.017 | -0.8% | -0.048 | 1.839 to 2.143 | 1.936 to 3.811 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 8.818 | 9.455 | +0.637 | +7.2% | +4.764 | 8.687 to 9.224 | 9.243 to 11.558 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 6.938 | 7.723 | +0.785 | +11.3% | +4.115 | 6.641 to 7.090 | 7.134 to 7.964 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 4.075 | 3.927 | -0.148 | -3.6% | -0.280 | 3.707 to 4.194 | 3.639 to 4.139 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 5.751 | 6.545 | +0.794 | +13.8% | +4.138 | 5.643 to 5.925 | 6.356 to 6.687 |
| boxes, 3456x2234 Quality, internal 2304x1489 | off | 1.369 | 1.289 | -0.080 | -5.8% | -0.071 | 1.121 to 1.475 | 1.143 to 1.369 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 7.835 | 8.419 | +0.584 | +7.5% | +4.627 | 7.655 to 8.546 | 8.252 to 8.792 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 5.354 | 6.207 | +0.853 | +15.9% | +4.258 | 5.031 to 5.457 | 5.955 to 6.354 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 2.512 | 2.363 | -0.149 | -5.9% | -0.182 | 2.216 to 2.575 | 2.190 to 2.487 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.820 | 5.793 | +0.973 | +20.2% | +4.230 | 4.712 to 5.025 | 5.572 to 5.956 |
| field, 3456x2234 Native, internal 3342x2160 | off | 2.732 | 2.781 | +0.049 | +1.8% | -0.174 | 2.513 to 2.952 | 2.488 to 2.863 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 10.909 | 13.394 | +2.485 | +22.8% | +5.000 | 10.781 to 11.496 | 13.170 to 13.805 |
| field, 3456x2234 Native, internal 3342x2160 | split | 9.480 | 11.121 | +1.641 | +17.3% | +4.250 | 9.263 to 9.665 | 10.806 to 11.337 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 6.039 | 5.912 | -0.127 | -2.1% | -0.168 | 5.534 to 6.234 | 5.690 to 5.984 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 7.619 | 9.121 | +1.502 | +19.7% | +4.170 | 7.369 to 7.684 | 9.011 to 9.201 |
| field, 3456x2234 Quality, internal 2304x1489 | off | 1.798 | 1.734 | -0.064 | -3.6% | -0.119 | 1.587 to 1.853 | 1.518 to 1.819 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 9.303 | 11.848 | +2.545 | +27.4% | +4.831 | 9.167 to 9.813 | 11.798 to 12.159 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 6.919 | 8.741 | +1.822 | +26.3% | +4.313 | 6.602 to 7.016 | 8.092 to 8.837 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 3.573 | 3.408 | -0.165 | -4.6% | -0.141 | 3.265 to 3.692 | 3.234 to 3.508 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 6.052 | 7.792 | +1.740 | +28.8% | +4.268 | 5.962 to 6.139 | 7.731 to 7.982 |

Resolve and sharpen at most, paired round spread, and submit and drain:

| Case | Mode | Base | Head | Growth | Relative | First growth | Saved vs first | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Growth |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 2.950 | 3.276 | +0.326 | +11.1% | +2.473 | +2.147 | 2.948, 2.900 to 3.628 | 3.307, 3.205 to 3.817 | 3.132 | 3.486 | +0.354 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 2.145 | 2.137 | -0.008 | -0.4% | +2.209 | +2.217 | 2.115, 1.855 to 2.453 | 2.148, 1.875 to 2.254 | 2.314 | 2.370 | +0.056 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 0.916 | 0.929 | +0.013 | +1.4% | -0.032 | -0.045 | 0.902, 0.734 to 1.098 | 0.963, 0.718 to 1.079 | 1.106 | 1.180 | +0.074 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.612 | 1.604 | -0.008 | -0.5% | +2.225 | +2.233 | 1.603, 1.523 to 1.774 | 1.635, 1.524 to 1.745 | 1.805 | 1.844 | +0.039 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.382 | 2.853 | +0.471 | +19.8% | +2.165 | +1.694 | 2.367, 2.317 to 2.569 | 2.860, 2.762 to 3.020 | 2.561 | 3.049 | +0.488 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.970 | 3.319 | +0.349 | +11.8% | +2.254 | +1.905 | 2.969, 2.909 to 3.436 | 3.305, 3.185 to 3.677 | 3.083 | 3.363 | +0.280 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 1.733 | 2.276 | +0.543 | +31.3% | +2.147 | +1.604 | 1.735, 1.551 to 1.928 | 2.275, 2.098 to 2.401 | 1.835 | 2.371 | +0.536 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.437 | 0.434 | -0.003 | -0.7% | +0.060 | +0.063 | 0.450, 0.367 to 0.599 | 0.437, 0.345 to 0.569 | 0.563 | 0.581 | +0.018 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.599 | 2.082 | +0.483 | +30.2% | +2.055 | +1.572 | 1.601, 1.459 to 1.724 | 2.071, 2.018 to 2.232 | 1.726 | 2.191 | +0.465 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 3.479 | 4.941 | +1.462 | +42.0% | +2.732 | +1.270 | 3.549, 3.357 to 4.167 | 4.960, 4.831 to 5.542 | 3.644 | 5.096 | +1.452 |
| field, 2560x1440 Native, internal 2560x1440 | split | 2.942 | 3.652 | +0.710 | +24.1% | +2.398 | +1.688 | 2.971, 2.594 to 3.205 | 3.605, 3.356 to 3.926 | 3.101 | 3.834 | +0.733 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.424 | 1.457 | +0.033 | +2.3% | +0.012 | -0.021 | 1.453, 1.220 to 1.636 | 1.433, 1.280 to 1.672 | 1.603 | 1.661 | +0.058 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.987 | 2.734 | +0.747 | +37.6% | +2.369 | +1.622 | 2.007, 1.915 to 2.165 | 2.742, 2.673 to 2.959 | 2.169 | 2.941 | +0.772 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.253 | 4.142 | +0.889 | +27.3% | +2.220 | +1.331 | 3.243, 3.135 to 3.459 | 4.134, 4.032 to 4.433 | 3.402 | 4.298 | +0.896 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 3.203 | 4.590 | +1.387 | +43.3% | +2.450 | +1.063 | 3.191, 3.042 to 3.816 | 4.588, 4.391 to 4.946 | 3.317 | 4.648 | +1.331 |
| field, 2560x1440 Quality, internal 1707x960 | split | 2.189 | 3.141 | +0.952 | +43.5% | +2.090 | +1.138 | 2.189, 1.804 to 2.355 | 3.142, 2.822 to 3.305 | 2.297 | 3.240 | +0.943 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.722 | 0.755 | +0.033 | +4.6% | +0.062 | +0.029 | 0.715, 0.532 to 0.834 | 0.753, 0.632 to 0.817 | 0.861 | 0.881 | +0.020 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.820 | 2.772 | +0.952 | +52.3% | +2.120 | +1.168 | 1.822, 1.685 to 1.978 | 2.788, 2.550 to 3.020 | 1.943 | 2.878 | +0.935 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 6.772 | 7.426 | +0.654 | +9.7% | +4.813 | +4.159 | 6.784, 6.608 to 7.208 | 7.416, 7.211 to 7.796 | 7.004 | 7.633 | +0.629 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 4.892 | 5.694 | +0.802 | +16.4% | +4.162 | +3.360 | 4.903, 4.560 to 5.135 | 5.723, 3.941 to 5.942 | 5.160 | 5.979 | +0.819 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 2.028 | 1.898 | -0.130 | -6.4% | -0.233 | -0.103 | 2.054, 1.677 to 2.199 | 1.908, 0.107 to 2.089 | 2.316 | 2.182 | -0.134 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 3.705 | 4.516 | +0.811 | +21.9% | +4.186 | +3.375 | 3.746, 3.532 to 3.922 | 4.518, 2.578 to 4.690 | 3.944 | 4.790 | +0.846 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 6.466 | 7.131 | +0.665 | +10.3% | +4.699 | +4.034 | 6.483, 6.245 to 7.148 | 7.131, 6.954 to 7.513 | 6.676 | 7.338 | +0.662 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 3.984 | 4.919 | +0.935 | +23.5% | +4.329 | +3.394 | 3.987, 3.611 to 4.243 | 4.956, 4.661 to 5.068 | 4.152 | 5.145 | +0.993 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.142 | 1.074 | -0.068 | -6.0% | -0.110 | -0.042 | 1.131, 0.819 to 1.390 | 1.070, 0.883 to 1.238 | 1.360 | 1.315 | -0.045 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 3.450 | 4.504 | +1.054 | +30.6% | +4.301 | +3.247 | 3.432, 3.308 to 3.838 | 4.496, 4.239 to 4.733 | 3.673 | 4.685 | +1.012 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 8.176 | 10.612 | +2.436 | +29.8% | +5.174 | +2.738 | 8.212, 7.915 to 8.791 | 10.631, 10.389 to 11.048 | 8.393 | 10.750 | +2.357 |
| field, 3456x2234 Native, internal 3342x2160 | split | 6.748 | 8.339 | +1.591 | +23.6% | +4.425 | +2.834 | 6.692, 6.523 to 6.915 | 8.392, 7.953 to 8.671 | 6.955 | 8.541 | +1.586 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 3.307 | 3.131 | -0.176 | -5.3% | +0.006 | +0.182 | 3.252, 2.628 to 3.721 | 3.113, 2.909 to 3.475 | 3.548 | 3.314 | -0.234 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 4.886 | 6.340 | +1.454 | +29.8% | +4.344 | +2.890 | 4.807, 4.523 to 4.995 | 6.336, 6.209 to 6.586 | 5.123 | 6.529 | +1.406 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 7.506 | 10.114 | +2.608 | +34.7% | +4.950 | +2.342 | 7.516, 7.380 to 8.018 | 10.116, 9.980 to 10.435 | 7.715 | 10.263 | +2.548 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 5.121 | 7.007 | +1.886 | +36.8% | +4.431 | +2.545 | 5.147, 4.777 to 5.332 | 7.019, 6.341 to 7.188 | 5.330 | 7.189 | +1.859 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.775 | 1.674 | -0.101 | -5.7% | -0.021 | +0.080 | 1.788, 1.468 to 1.972 | 1.645, 1.487 to 1.841 | 2.003 | 1.903 | -0.100 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.255 | 6.058 | +1.803 | +42.4% | +4.387 | +2.584 | 4.249, 4.166 to 4.446 | 6.084, 5.923 to 6.238 | 4.474 | 6.257 | +1.783 |

Pass pairs, differences of the printed mode medians:

| Case | Pair | Base | Head | Growth | First growth | Base paired median, min to max | Head paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | pass one | 0.534 | 0.533 | -0.001 | -0.016 | 0.513, 0.128 to 0.679 | 0.511, 0.293 to 0.633 |
| boxes, 2560x1440 Native, internal 2560x1440 | pass two | 1.229 | 1.208 | -0.021 | +2.242 | 1.216, 0.910 to 1.413 | 1.175, 0.974 to 1.430 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against fused | -0.805 | -1.140 | -0.335 | -0.264 | -0.834, -1.467 to -0.649 | -1.166, -1.711 to -1.005 |
| boxes, 2560x1440 Native, internal 2560x1440 | split at the display's size against its upscaling second pass | -0.236 | -0.716 | -0.480 | +0.043 | -0.251, -0.522 to -0.115 | -0.738, -0.928 to -0.605 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass one | 0.134 | 0.195 | +0.061 | +0.090 | 0.160, -0.103 to 0.277 | 0.170, 0.015 to 0.355 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass two | 1.296 | 1.842 | +0.546 | +2.087 | 1.298, 1.120 to 1.369 | 1.830, 1.661 to 1.944 |
| boxes, 2560x1440 Quality, internal 1707x960 | split against fused | -1.237 | -1.043 | +0.194 | -0.107 | -1.285, -1.667 to -1.044 | -1.055, -1.411 to -0.873 |
| field, 2560x1440 Native, internal 2560x1440 | pass one | 0.955 | 0.918 | -0.037 | +0.028 | 0.937, 0.566 to 1.149 | 0.905, 0.594 to 1.131 |
| field, 2560x1440 Native, internal 2560x1440 | pass two | 1.518 | 2.196 | +0.678 | +2.385 | 1.506, 1.037 to 1.789 | 2.202, 1.855 to 2.372 |
| field, 2560x1440 Native, internal 2560x1440 | split against fused | -0.538 | -1.289 | -0.751 | -0.335 | -0.594, -1.275 to -0.315 | -1.290, -2.054 to -1.048 |
| field, 2560x1440 Native, internal 2560x1440 | split at the display's size against its upscaling second pass | -0.312 | -0.490 | -0.178 | +0.176 | -0.312, -0.672 to -0.070 | -0.507, -0.816 to -0.299 |
| field, 2560x1440 Quality, internal 1707x960 | pass one | 0.369 | 0.369 | 0.000 | -0.031 | 0.352, 0.079 to 0.573 | 0.349, 0.093 to 0.592 |
| field, 2560x1440 Quality, internal 1707x960 | pass two | 1.467 | 2.386 | +0.919 | +2.027 | 1.470, 1.254 to 1.594 | 2.373, 2.164 to 2.628 |
| field, 2560x1440 Quality, internal 1707x960 | split against fused | -1.013 | -1.449 | -0.436 | -0.360 | -1.013, -1.659 to -0.843 | -1.498, -1.810 to -1.283 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass one | 1.188 | 1.178 | -0.010 | -0.024 | 1.084, 0.725 to 1.370 | 1.158, 0.510 to 1.521 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass two | 2.864 | 3.796 | +0.932 | +4.395 | 2.862, 2.474 to 3.207 | 3.827, 3.091 to 4.102 |
| boxes, 3456x2234 Native, internal 3342x2160 | split against fused | -1.880 | -1.732 | +0.148 | -0.650 | -1.946, -2.252 to -1.730 | -1.691, -3.807 to -1.473 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass one | 0.534 | 0.415 | -0.119 | +0.027 | 0.477, 0.203 to 0.674 | 0.478, -0.001 to 0.730 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass two | 2.842 | 3.845 | +1.003 | +4.439 | 2.848, 2.494 to 3.240 | 3.848, 3.502 to 4.073 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split against fused | -2.481 | -2.212 | +0.269 | -0.370 | -2.534, -3.204 to -2.295 | -2.213, -2.649 to -2.008 |
| field, 3456x2234 Native, internal 3342x2160 | pass one | 1.862 | 2.000 | +0.138 | +0.079 | 1.920, 1.643 to 2.068 | 2.039, 1.605 to 2.164 |
| field, 3456x2234 Native, internal 3342x2160 | pass two | 3.441 | 5.209 | +1.768 | +4.418 | 3.480, 3.190 to 4.049 | 5.229, 4.841 to 5.647 |
| field, 3456x2234 Native, internal 3342x2160 | split against fused | -1.428 | -2.273 | -0.845 | -0.750 | -1.485, -2.016 to -1.216 | -2.235, -2.694 to -2.049 |
| field, 3456x2234 Quality, internal 2304x1489 | pass one | 0.866 | 0.949 | +0.083 | +0.044 | 0.883, 0.601 to 1.043 | 0.932, 0.238 to 1.091 |
| field, 3456x2234 Quality, internal 2304x1489 | pass two | 3.346 | 5.333 | +1.987 | +4.453 | 3.373, 2.910 to 3.678 | 5.347, 4.854 to 5.560 |
| field, 3456x2234 Quality, internal 2304x1489 | split against fused | -2.384 | -3.107 | -0.723 | -0.518 | -2.433, -2.933 to -2.240 | -3.176, -3.709 to -3.007 |

Outline fixture, Split entry. The outline increment subtracts two temporal frames and cancels their common visibility cost:

| Case | Line | Base | Head | Growth | Relative | First growth | Base block range or paired median, min to max | Head block range or paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| outline, 2560x1440 Native, internal 2560x1440 | outline off | 3.536 | 3.542 | +0.006 | +0.2% | +1.994 | 3.489 to 4.800 | 3.481 to 4.305 |
| outline, 2560x1440 Native, internal 2560x1440 | outline on | 3.793 | 3.843 | +0.050 | +1.3% | +2.045 | 3.704 to 5.164 | 3.742 to 3.929 |
| outline, 2560x1440 Native, internal 2560x1440 | outline cost | 0.256 | 0.301 | +0.045 | +17.6% | +0.051 | 0.270, 0.181 to 0.965 | 0.288, -0.422 to 0.405 |
| outline, 2560x1440 Quality, internal 1707x960 | outline off | 2.632 | 3.019 | +0.387 | +14.7% | +1.930 | 2.593 to 2.680 | 2.988 to 3.088 |
| outline, 2560x1440 Quality, internal 1707x960 | outline on | 2.760 | 3.163 | +0.403 | +14.6% | +1.899 | 2.729 to 2.823 | 3.127 to 3.196 |
| outline, 2560x1440 Quality, internal 1707x960 | outline cost | 0.127 | 0.144 | +0.017 | +13.4% | -0.030 | 0.141, 0.078 to 0.183 | 0.137, 0.074 to 0.208 |

### linux-vulkan

Mode medians, including the Off drift control:

| Case | Mode | Base | Head | Growth | Relative | First growth | Base blocks min to max | Head blocks min to max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | off | 1.073 | 1.106 | +0.033 | +3.1% | -0.021 | 1.025 to 1.113 | 1.043 to 1.258 |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 3.773 | 4.352 | +0.579 | +15.3% | +2.375 | 3.630 to 4.197 | 4.270 to 4.677 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 3.541 | 3.652 | +0.111 | +3.1% | +2.281 | 3.248 to 3.614 | 3.359 to 3.759 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 2.131 | 2.221 | +0.090 | +4.2% | -0.038 | 2.006 to 2.164 | 2.168 to 2.251 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.777 | 2.918 | +0.141 | +5.1% | +2.374 | 2.721 to 2.823 | 2.868 to 2.978 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.575 | 4.122 | +0.547 | +15.3% | +2.314 | 3.528 to 3.668 | 3.978 to 4.194 |
| boxes, 2560x1440 Quality, internal 1707x960 | off | 0.625 | 0.623 | -0.002 | -0.3% | -0.024 | 0.605 to 0.747 | 0.602 to 0.759 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 3.311 | 3.734 | +0.423 | +12.8% | +2.093 | 3.065 to 3.621 | 3.604 to 4.083 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 2.630 | 3.056 | +0.426 | +16.2% | +2.192 | 2.389 to 2.739 | 2.796 to 3.143 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.164 | 1.205 | +0.041 | +3.5% | 0.000 | 1.118 to 1.212 | 1.159 to 1.433 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.325 | 2.731 | +0.406 | +17.5% | +2.168 | 2.270 to 2.359 | 2.656 to 2.772 |
| field, 2560x1440 Native, internal 2560x1440 | off | 1.544 | 1.562 | +0.018 | +1.2% | -0.045 | 1.510 to 1.778 | 1.519 to 1.643 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 5.025 | 6.528 | +1.503 | +29.9% | +2.807 | 4.908 to 5.395 | 6.372 to 6.815 |
| field, 2560x1440 Native, internal 2560x1440 | split | 4.755 | 5.651 | +0.896 | +18.8% | +2.391 | 4.328 to 4.910 | 5.301 to 5.736 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 3.120 | 3.215 | +0.095 | +3.0% | +0.029 | 3.066 to 3.243 | 3.104 to 3.313 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 3.619 | 4.622 | +1.003 | +27.7% | +2.486 | 3.572 to 3.738 | 4.505 to 4.703 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 4.831 | 5.922 | +1.091 | +22.6% | +2.399 | 4.721 to 4.918 | 5.838 to 6.030 |
| field, 2560x1440 Quality, internal 1707x960 | off | 0.892 | 0.857 | -0.035 | -3.9% | -0.056 | 0.832 to 1.038 | 0.835 to 1.027 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 4.007 | 5.365 | +1.358 | +33.9% | +2.409 | 3.907 to 4.430 | 5.260 to 5.670 |
| field, 2560x1440 Quality, internal 1707x960 | split | 3.330 | 4.375 | +1.045 | +31.4% | +2.215 | 2.971 to 3.398 | 4.065 to 4.512 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 1.712 | 1.793 | +0.081 | +4.7% | +0.036 | 1.607 to 1.750 | 1.753 to 1.820 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 2.818 | 3.875 | +1.057 | +37.5% | +2.258 | 2.593 to 2.873 | 3.749 to 3.937 |
| boxes, 3456x2234 Native, internal 3342x2160 | off | 1.959 | 1.957 | -0.002 | -0.1% | -0.027 | 1.874 to 2.065 | 1.819 to 2.057 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 8.182 | 9.206 | +1.024 | +12.5% | +4.451 | 8.041 to 8.469 | 9.048 to 9.373 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 7.110 | 7.954 | +0.844 | +11.9% | +4.491 | 6.985 to 7.247 | 7.653 to 8.064 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 4.121 | 4.189 | +0.068 | +1.7% | -0.055 | 3.993 to 4.212 | 4.032 to 4.277 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 5.715 | 6.588 | +0.873 | +15.3% | +4.540 | 5.571 to 5.860 | 6.364 to 6.747 |
| boxes, 3456x2234 Quality, internal 2304x1489 | off | 1.156 | 1.132 | -0.024 | -2.1% | -0.011 | 1.115 to 1.411 | 1.078 to 1.377 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 7.173 | 8.136 | +0.963 | +13.4% | +4.451 | 7.053 to 7.494 | 7.942 to 8.360 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 5.480 | 6.298 | +0.818 | +14.9% | +4.510 | 5.061 to 5.545 | 5.800 to 6.440 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 2.395 | 2.468 | +0.073 | +3.0% | -0.076 | 2.343 to 2.459 | 2.412 to 2.526 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.778 | 5.710 | +0.932 | +19.5% | +4.561 | 4.679 to 4.933 | 5.534 to 5.799 |
| field, 3456x2234 Native, internal 3342x2160 | off | 2.860 | 2.895 | +0.035 | +1.2% | -0.124 | 2.812 to 2.996 | 2.821 to 3.036 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 10.638 | 13.381 | +2.743 | +25.8% | +5.151 | 10.481 to 10.915 | 13.247 to 13.531 |
| field, 3456x2234 Native, internal 3342x2160 | split | 9.561 | 11.736 | +2.175 | +22.7% | +4.641 | 9.272 to 9.736 | 11.563 to 11.779 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 5.988 | 6.245 | +0.257 | +4.3% | +0.029 | 5.956 to 6.058 | 6.149 to 6.392 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 7.487 | 9.505 | +2.018 | +27.0% | +4.532 | 7.335 to 7.610 | 9.442 to 9.677 |
| field, 3456x2234 Quality, internal 2304x1489 | off | 1.653 | 1.649 | -0.004 | -0.2% | -0.072 | 1.608 to 1.815 | 1.595 to 1.822 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 8.988 | 11.689 | +2.701 | +30.1% | +4.999 | 8.847 to 9.373 | 11.571 to 11.850 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 6.984 | 9.168 | +2.184 | +31.3% | +4.518 | 6.736 to 7.045 | 9.020 to 9.271 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 3.550 | 3.680 | +0.130 | +3.7% | -0.042 | 3.431 to 3.593 | 3.648 to 3.750 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 5.946 | 8.097 | +2.151 | +36.2% | +4.577 | 5.874 to 6.007 | 8.009 to 8.200 |

Resolve and sharpen at most, paired round spread, and submit and drain:

| Case | Mode | Base | Head | Growth | Relative | First growth | Saved vs first | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Growth |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | fused | 2.700 | 3.247 | +0.547 | +20.3% | +2.396 | +1.849 | 2.713, 2.546 to 3.140 | 3.241, 3.063 to 3.613 | 2.759 | 3.309 | +0.550 |
| boxes, 2560x1440 Native, internal 2560x1440 | split | 2.469 | 2.547 | +0.078 | +3.2% | +2.301 | +2.223 | 2.450, 2.161 to 2.571 | 2.540, 2.223 to 2.675 | 2.524 | 2.604 | +0.080 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.058 | 1.116 | +0.058 | +5.5% | -0.017 | -0.075 | 1.049, 0.959 to 1.115 | 1.119, 0.959 to 1.179 | 1.122 | 1.185 | +0.063 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 1.704 | 1.812 | +0.108 | +6.3% | +2.396 | +2.288 | 1.689, 1.645 to 1.775 | 1.829, 1.657 to 1.917 | 1.770 | 1.882 | +0.112 |
| boxes, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 2.502 | 3.017 | +0.515 | +20.6% | +2.335 | +1.820 | 2.501, 2.432 to 2.595 | 3.003, 2.840 to 3.089 | 2.556 | 3.073 | +0.517 |
| boxes, 2560x1440 Quality, internal 1707x960 | fused | 2.686 | 3.110 | +0.424 | +15.8% | +2.117 | +1.693 | 2.688, 2.318 to 3.014 | 3.110, 2.855 to 3.451 | 2.732 | 3.150 | +0.418 |
| boxes, 2560x1440 Quality, internal 1707x960 | split | 2.006 | 2.433 | +0.427 | +21.3% | +2.216 | +1.789 | 1.992, 1.762 to 2.101 | 2.413, 2.171 to 2.529 | 2.051 | 2.469 | +0.418 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.540 | 0.581 | +0.041 | +7.6% | +0.025 | -0.016 | 0.538, 0.462 to 0.588 | 0.573, 0.462 to 0.817 | 0.593 | 0.628 | +0.035 |
| boxes, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.700 | 2.107 | +0.407 | +23.9% | +2.192 | +1.785 | 1.699, 1.577 to 1.738 | 2.106, 1.977 to 2.158 | 1.753 | 2.154 | +0.401 |
| field, 2560x1440 Native, internal 2560x1440 | fused | 3.481 | 4.966 | +1.485 | +42.7% | +2.851 | +1.366 | 3.511, 3.157 to 3.851 | 4.988, 4.832 to 5.275 | 3.505 | 5.017 | +1.512 |
| field, 2560x1440 Native, internal 2560x1440 | split | 3.211 | 4.089 | +0.878 | +27.3% | +2.437 | +1.559 | 3.201, 2.758 to 3.249 | 4.070, 3.715 to 4.208 | 3.233 | 4.133 | +0.900 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass one alone | 1.576 | 1.653 | +0.077 | +4.9% | +0.074 | -0.003 | 1.555, 1.466 to 1.656 | 1.635, 1.563 to 1.722 | 1.617 | 1.712 | +0.095 |
| field, 2560x1440 Native, internal 2560x1440 | split, pass two alone | 2.075 | 3.060 | +0.985 | +47.5% | +2.530 | +1.545 | 2.059, 1.960 to 2.118 | 3.020, 2.915 to 3.167 | 2.114 | 3.118 | +1.004 |
| field, 2560x1440 Native, internal 2560x1440 | split, upscaling second pass | 3.287 | 4.360 | +1.073 | +32.6% | +2.443 | +1.370 | 3.277, 3.053 to 3.369 | 4.351, 4.293 to 4.472 | 3.303 | 4.395 | +1.092 |
| field, 2560x1440 Quality, internal 1707x960 | fused | 3.116 | 4.507 | +1.391 | +44.6% | +2.465 | +1.074 | 3.132, 2.885 to 3.557 | 4.501, 4.283 to 4.809 | 3.154 | 4.520 | +1.366 |
| field, 2560x1440 Quality, internal 1707x960 | split | 2.439 | 3.517 | +1.078 | +44.2% | +2.271 | +1.193 | 2.391, 2.140 to 2.521 | 3.518, 3.221 to 3.667 | 2.494 | 3.540 | +1.046 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass one alone | 0.820 | 0.936 | +0.116 | +14.1% | +0.091 | -0.025 | 0.829, 0.669 to 0.872 | 0.935, 0.778 to 0.964 | 0.879 | 0.973 | +0.094 |
| field, 2560x1440 Quality, internal 1707x960 | split, pass two alone | 1.926 | 3.017 | +1.091 | +56.6% | +2.313 | +1.222 | 1.924, 1.746 to 2.004 | 3.029, 2.822 to 3.083 | 1.985 | 3.046 | +1.061 |
| boxes, 3456x2234 Native, internal 3342x2160 | fused | 6.223 | 7.249 | +1.026 | +16.5% | +4.479 | +3.453 | 6.234, 5.976 to 6.509 | 7.284, 7.107 to 7.460 | 6.286 | 7.324 | +1.038 |
| boxes, 3456x2234 Native, internal 3342x2160 | split | 5.151 | 5.998 | +0.847 | +16.4% | +4.517 | +3.670 | 5.164, 4.977 to 5.213 | 6.010, 5.658 to 6.146 | 5.198 | 6.062 | +0.864 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 2.162 | 2.232 | +0.070 | +3.2% | -0.028 | -0.098 | 2.119, 2.033 to 2.319 | 2.228, 2.075 to 2.369 | 2.224 | 2.314 | +0.090 |
| boxes, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 3.756 | 4.631 | +0.875 | +23.3% | +4.567 | +3.692 | 3.740, 3.612 to 3.862 | 4.660, 4.362 to 4.789 | 3.822 | 4.713 | +0.891 |
| boxes, 3456x2234 Quality, internal 2304x1489 | fused | 6.016 | 7.003 | +0.987 | +16.4% | +4.462 | +3.475 | 6.004, 5.683 to 6.318 | 7.007, 6.759 to 7.219 | 6.068 | 7.058 | +0.990 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split | 4.324 | 5.166 | +0.842 | +19.5% | +4.520 | +3.678 | 4.313, 3.876 to 4.400 | 5.162, 4.693 to 5.269 | 4.371 | 5.217 | +0.846 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.239 | 1.336 | +0.097 | +7.8% | -0.065 | -0.162 | 1.237, 1.009 to 1.318 | 1.316, 1.052 to 1.394 | 1.298 | 1.398 | +0.100 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 3.622 | 4.578 | +0.956 | +26.4% | +4.572 | +3.616 | 3.633, 3.322 to 3.806 | 4.598, 4.178 to 4.698 | 3.681 | 4.639 | +0.958 |
| field, 3456x2234 Native, internal 3342x2160 | fused | 7.778 | 10.486 | +2.708 | +34.8% | +5.276 | +2.568 | 7.791, 7.485 to 8.095 | 10.523, 10.212 to 10.681 | 7.817 | 10.536 | +2.719 |
| field, 3456x2234 Native, internal 3342x2160 | split | 6.701 | 8.841 | +2.140 | +31.9% | +4.766 | +2.626 | 6.715, 6.369 to 6.858 | 8.795, 8.613 to 8.948 | 6.747 | 8.886 | +2.139 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass one alone | 3.128 | 3.350 | +0.222 | +7.1% | +0.153 | -0.069 | 3.141, 2.985 to 3.238 | 3.360, 3.225 to 3.461 | 3.179 | 3.404 | +0.225 |
| field, 3456x2234 Native, internal 3342x2160 | split, pass two alone | 4.628 | 6.610 | +1.982 | +42.8% | +4.657 | +2.675 | 4.605, 4.456 to 4.798 | 6.612, 6.524 to 6.828 | 4.675 | 6.673 | +1.998 |
| field, 3456x2234 Quality, internal 2304x1489 | fused | 7.335 | 10.040 | +2.705 | +36.9% | +5.072 | +2.367 | 7.328, 7.033 to 7.721 | 10.046, 9.758 to 10.236 | 7.373 | 10.084 | +2.711 |
| field, 3456x2234 Quality, internal 2304x1489 | split | 5.332 | 7.519 | +2.187 | +41.0% | +4.590 | +2.403 | 5.332, 5.078 to 5.418 | 7.537, 7.358 to 7.653 | 5.363 | 7.561 | +2.198 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass one alone | 1.897 | 2.031 | +0.134 | +7.1% | +0.030 | -0.104 | 1.892, 1.708 to 1.964 | 2.040, 1.866 to 2.101 | 1.944 | 2.087 | +0.143 |
| field, 3456x2234 Quality, internal 2304x1489 | split, pass two alone | 4.293 | 6.448 | +2.155 | +50.2% | +4.649 | +2.494 | 4.303, 4.121 to 4.369 | 6.434, 6.280 to 6.599 | 4.330 | 6.489 | +2.159 |

Pass pairs, differences of the printed mode medians:

| Case | Pair | Base | Head | Growth | First growth | Base paired median, min to max | Head paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | pass one | 0.764 | 0.735 | -0.029 | -0.093 | 0.759, 0.496 to 0.879 | 0.738, 0.410 to 0.846 |
| boxes, 2560x1440 Native, internal 2560x1440 | pass two | 1.410 | 1.431 | +0.021 | +2.319 | 1.400, 1.156 to 1.492 | 1.452, 1.112 to 1.536 |
| boxes, 2560x1440 Native, internal 2560x1440 | split against fused | -0.232 | -0.700 | -0.468 | -0.095 | -0.340, -0.709 to -0.016 | -0.773, -1.018 to -0.525 |
| boxes, 2560x1440 Native, internal 2560x1440 | split at the display's size against its upscaling second pass | -0.034 | -0.470 | -0.436 | -0.033 | -0.065, -0.302 to 0.070 | -0.463, -0.775 to -0.323 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass one | 0.306 | 0.325 | +0.019 | +0.024 | 0.305, 0.050 to 0.414 | 0.336, 0.036 to 0.449 |
| boxes, 2560x1440 Quality, internal 1707x960 | pass two | 1.466 | 1.851 | +0.385 | +2.191 | 1.453, 1.267 to 1.543 | 1.838, 1.637 to 1.927 |
| boxes, 2560x1440 Quality, internal 1707x960 | split against fused | -0.680 | -0.678 | +0.002 | +0.098 | -0.743, -0.990 to -0.326 | -0.678, -1.038 to -0.481 |
| field, 2560x1440 Native, internal 2560x1440 | pass one | 1.136 | 1.029 | -0.107 | -0.094 | 1.139, 0.709 to 1.221 | 1.033, 0.622 to 1.202 |
| field, 2560x1440 Native, internal 2560x1440 | pass two | 1.634 | 2.436 | +0.802 | +2.362 | 1.634, 1.235 to 1.722 | 2.435, 2.048 to 2.585 |
| field, 2560x1440 Native, internal 2560x1440 | split against fused | -0.271 | -0.877 | -0.606 | -0.414 | -0.341, -0.795 to -0.024 | -0.893, -1.208 to -0.720 |
| field, 2560x1440 Native, internal 2560x1440 | split at the display's size against its upscaling second pass | -0.076 | -0.271 | -0.195 | -0.007 | -0.084, -0.567 to 0.080 | -0.287, -0.609 to -0.111 |
| field, 2560x1440 Quality, internal 1707x960 | pass one | 0.513 | 0.500 | -0.013 | -0.043 | 0.506, 0.340 to 0.608 | 0.513, 0.160 to 0.643 |
| field, 2560x1440 Quality, internal 1707x960 | pass two | 1.618 | 2.582 | +0.964 | +2.180 | 1.603, 1.365 to 1.691 | 2.592, 2.259 to 2.719 |
| field, 2560x1440 Quality, internal 1707x960 | split against fused | -0.677 | -0.990 | -0.313 | -0.193 | -0.764, -1.142 to -0.536 | -0.983, -1.314 to -0.817 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass one | 1.395 | 1.366 | -0.029 | -0.049 | 1.369, 1.234 to 1.601 | 1.398, 0.998 to 1.544 |
| boxes, 3456x2234 Native, internal 3342x2160 | pass two | 2.989 | 3.766 | +0.777 | +4.545 | 2.992, 2.821 to 3.131 | 3.755, 3.430 to 3.987 |
| boxes, 3456x2234 Native, internal 3342x2160 | split against fused | -1.072 | -1.252 | -0.180 | +0.040 | -1.120, -1.359 to -0.794 | -1.291, -1.544 to -1.116 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass one | 0.702 | 0.588 | -0.114 | -0.051 | 0.686, 0.240 to 0.820 | 0.571, 0.161 to 0.802 |
| boxes, 3456x2234 Quality, internal 2304x1489 | pass two | 3.085 | 3.830 | +0.745 | +4.586 | 3.077, 2.602 to 3.177 | 3.839, 3.332 to 3.960 |
| boxes, 3456x2234 Quality, internal 2304x1489 | split against fused | -1.693 | -1.838 | -0.145 | +0.059 | -1.670, -2.128 to -1.549 | -1.849, -2.252 to -1.679 |
| field, 3456x2234 Native, internal 3342x2160 | pass one | 2.073 | 2.231 | +0.158 | +0.109 | 2.046, 1.764 to 2.401 | 2.132, 2.033 to 2.327 |
| field, 3456x2234 Native, internal 3342x2160 | pass two | 3.573 | 5.492 | +1.919 | +4.612 | 3.557, 3.314 to 3.736 | 5.442, 5.263 to 5.612 |
| field, 3456x2234 Native, internal 3342x2160 | split against fused | -1.077 | -1.645 | -0.568 | -0.510 | -1.168, -1.387 to -0.905 | -1.708, -1.761 to -1.522 |
| field, 3456x2234 Quality, internal 2304x1489 | pass one | 1.038 | 1.071 | +0.033 | -0.060 | 1.028, 0.801 to 1.171 | 1.089, 0.923 to 1.168 |
| field, 3456x2234 Quality, internal 2304x1489 | pass two | 3.434 | 5.489 | +2.055 | +4.560 | 3.441, 3.155 to 3.614 | 5.499, 5.341 to 5.604 |
| field, 3456x2234 Quality, internal 2304x1489 | split against fused | -2.003 | -2.521 | -0.518 | -0.483 | -2.053, -2.389 to -1.872 | -2.521, -2.664 to -2.299 |

Outline fixture, Split entry. The outline increment subtracts two temporal frames and cancels their common visibility cost:

| Case | Line | Base | Head | Growth | Relative | First growth | Base block range or paired median, min to max | Head block range or paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| outline, 2560x1440 Native, internal 2560x1440 | outline off | 3.489 | 3.681 | +0.192 | +5.5% | +2.277 | 3.453 to 7.967 | 3.428 to 3.724 |
| outline, 2560x1440 Native, internal 2560x1440 | outline on | 3.794 | 4.009 | +0.215 | +5.7% | +2.268 | 3.727 to 3.895 | 3.942 to 4.179 |
| outline, 2560x1440 Native, internal 2560x1440 | outline cost | 0.305 | 0.328 | +0.023 | +7.5% | -0.010 | 0.313, -4.241 to 0.407 | 0.321, 0.285 to 0.591 |
| outline, 2560x1440 Quality, internal 1707x960 | outline off | 2.607 | 2.982 | +0.375 | +14.4% | +2.091 | 2.559 to 2.671 | 2.916 to 3.255 |
| outline, 2560x1440 Quality, internal 1707x960 | outline on | 2.750 | 3.135 | +0.385 | +14.0% | +2.092 | 2.725 to 2.857 | 3.092 to 3.179 |
| outline, 2560x1440 Quality, internal 1707x960 | outline cost | 0.143 | 0.153 | +0.010 | +7.0% | +0.001 | 0.144, 0.096 to 0.231 | 0.166, -0.115 to 0.251 |

## 3. Shadowed yard

The same 57-caster yard runs with ShadowMap. Off without shadows is its drift control. FXAA with shadows includes the PCF change without temporal rendering. Temporal modes include the PCF, visibility MRT, history copy and resolve together. Coverage is measured with anti-aliasing off on frame 90. Its darkened and edge shares are different from the check-path shares in section 5.

### windows-d3d11

| Preset | Mode | Base median | Head median | Growth | Relative | Base blocks min to max | Head blocks min to max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Native | off without shadows | 1.078 | 1.018 | -0.060 | -5.6% | 1.013 to 1.150 | 0.997 to 1.061 |
| Native | FXAA with shadows | 1.661 | 1.790 | +0.129 | +7.8% | 1.529 to 1.776 | 1.728 to 1.867 |
| Native | fused with shadows | 4.049 | 5.054 | +1.005 | +24.8% | 3.941 to 4.192 | 4.918 to 5.183 |
| Native | split with shadows | 3.986 | 4.750 | +0.764 | +19.2% | 3.626 to 4.206 | 4.406 to 4.980 |
| Native | fused without shadows | 4.108 | 4.762 | +0.654 | +15.9% | 3.992 to 4.231 | 4.630 to 4.848 |
| Native | split without shadows | 3.634 | 4.141 | +0.507 | +14.0% | 3.458 to 3.880 | 3.964 to 4.257 |
| Quality | off without shadows | 0.648 | 0.604 | -0.044 | -6.8% | 0.530 to 0.683 | 0.544 to 0.634 |
| Quality | FXAA with shadows | 0.979 | 1.001 | +0.022 | +2.2% | 0.821 to 1.074 | 0.899 to 1.040 |
| Quality | fused with shadows | 3.344 | 4.120 | +0.776 | +23.2% | 3.194 to 3.442 | 4.017 to 4.299 |
| Quality | split with shadows | 2.834 | 3.564 | +0.730 | +25.8% | 2.440 to 2.929 | 3.228 to 3.701 |
| Quality | fused without shadows | 3.499 | 4.172 | +0.673 | +19.2% | 3.350 to 3.629 | 4.012 to 4.267 |
| Quality | split without shadows | 2.569 | 3.204 | +0.635 | +24.7% | 2.493 to 2.678 | 3.160 to 3.426 |

| Preset | Mode over Off | Base | Head | Growth | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Growth |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | FXAA with shadows | 0.583 | 0.771 | +0.188 | 0.579, 0.516 to 0.655 | 0.763, 0.703 to 0.860 | 0.572 | 0.756 | +0.184 |
| Native | fused with shadows | 2.971 | 4.035 | +1.064 | 2.989, 2.868 to 3.101 | 4.028, 3.872 to 4.125 | 2.949 | 4.020 | +1.071 |
| Native | split with shadows | 2.908 | 3.731 | +0.823 | 2.903, 2.557 to 3.159 | 3.729, 3.383 to 3.983 | 2.880 | 3.714 | +0.834 |
| Native | fused without shadows | 3.030 | 3.743 | +0.713 | 3.049, 2.960 to 3.151 | 3.728, 3.585 to 3.841 | 3.018 | 3.736 | +0.718 |
| Native | split without shadows | 2.556 | 3.123 | +0.567 | 2.549, 2.380 to 2.838 | 3.115, 2.940 to 3.239 | 2.546 | 3.118 | +0.572 |
| Quality | FXAA with shadows | 0.331 | 0.397 | +0.066 | 0.329, 0.280 to 0.424 | 0.379, 0.349 to 0.421 | 0.321 | 0.390 | +0.069 |
| Quality | fused with shadows | 2.696 | 3.516 | +0.820 | 2.722, 2.547 to 2.778 | 3.524, 3.406 to 3.682 | 2.672 | 3.502 | +0.830 |
| Quality | split with shadows | 2.186 | 2.960 | +0.774 | 2.188, 1.792 to 2.304 | 2.961, 2.633 to 3.154 | 2.174 | 2.949 | +0.775 |
| Quality | fused without shadows | 2.850 | 3.567 | +0.717 | 2.880, 2.702 to 2.972 | 3.574, 3.409 to 3.671 | 2.844 | 3.563 | +0.719 |
| Quality | split without shadows | 1.921 | 2.600 | +0.679 | 1.925, 1.860 to 2.052 | 2.617, 2.534 to 2.823 | 1.916 | 2.598 | +0.682 |

| Preset | Pair | Base | Head | Growth | Base paired median, min to max | Head paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- |
| Native | shadows under the fused resolve | -0.059 | 0.292 | +0.351 | -0.052, -0.209 to 0.101 | 0.266, 0.135 to 0.451 |
| Native | shadows under the split resolve | 0.352 | 0.609 | +0.257 | 0.352, 0.042 to 0.705 | 0.631, 0.302 to 0.852 |
| Native | the fused resolve over FXAA with shadows | 2.388 | 3.264 | +0.876 | 2.386, 2.312 to 2.518 | 3.262, 3.081 to 3.394 |
| Native | the split resolve over FXAA with shadows | 2.325 | 2.960 | +0.635 | 2.307, 1.954 to 2.643 | 2.960, 2.603 to 3.148 |
| Native | split against fused with shadows | -0.063 | -0.304 | -0.241 | -0.061, -0.424 to 0.232 | -0.301, -0.659 to -0.032 |
| Quality | shadows under the fused resolve | -0.155 | -0.052 | +0.103 | -0.141, -0.317 to 0.040 | -0.032, -0.179 to 0.183 |
| Quality | shadows under the split resolve | 0.265 | 0.360 | +0.095 | 0.252, -0.237 to 0.398 | 0.328, -0.060 to 0.537 |
| Quality | the fused resolve over FXAA with shadows | 2.365 | 3.119 | +0.754 | 2.373, 2.186 to 2.457 | 3.119, 3.009 to 3.294 |
| Quality | the split resolve over FXAA with shadows | 1.855 | 2.563 | +0.708 | 1.862, 1.463 to 2.013 | 2.587, 2.246 to 2.799 |
| Quality | split against fused with shadows | -0.510 | -0.556 | -0.046 | -0.511, -0.903 to -0.342 | -0.552, -0.898 to -0.397 |

| Preset | Ref | Coverage internal size | Casters | Darkened display share | Within 2 px of shadow edge | Depth pass renders | Disoccluded frame 9 | Clipped frame 9 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | base | 2560x1440 | 57 | 4.99% | 2.39% | 540 of 540 | 2400 | 273600 |
| Native | head | 2560x1440 | 57 | 5.03% | 2.35% | 540 of 540 | 2400 | 279600 |
| Quality | base | 1707x960 | 57 | 5.12% | 2.39% | 540 of 540 | 2800 | 298000 |
| Quality | head | 1707x960 | 57 | 5.14% | 2.37% | 540 of 540 | 2800 | 301600 |

### windows-vulkan

| Preset | Mode | Base median | Head median | Growth | Relative | Base blocks min to max | Head blocks min to max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Native | off without shadows | 1.108 | 1.143 | +0.035 | +3.2% | 0.947 to 1.163 | 0.930 to 1.220 |
| Native | FXAA with shadows | 1.376 | 1.611 | +0.235 | +17.1% | 1.234 to 1.696 | 1.359 to 1.966 |
| Native | fused with shadows | 4.781 | 5.519 | +0.738 | +15.4% | 4.738 to 4.926 | 5.425 to 5.621 |
| Native | split with shadows | 3.976 | 4.408 | +0.432 | +10.9% | 3.702 to 4.092 | 4.109 to 4.537 |
| Native | fused without shadows | 4.969 | 5.364 | +0.395 | +7.9% | 4.699 to 5.040 | 5.118 to 5.458 |
| Native | split without shadows | 3.523 | 3.646 | +0.123 | +3.5% | 3.405 to 3.887 | 3.616 to 3.918 |
| Quality | off without shadows | 0.660 | 0.650 | -0.010 | -1.5% | 0.576 to 0.699 | 0.572 to 0.716 |
| Quality | FXAA with shadows | 0.828 | 0.889 | +0.061 | +7.4% | 0.746 to 0.976 | 0.829 to 1.109 |
| Quality | fused with shadows | 4.001 | 4.635 | +0.634 | +15.8% | 3.926 to 4.135 | 4.575 to 4.723 |
| Quality | split with shadows | 2.897 | 3.568 | +0.671 | +23.2% | 2.674 to 2.990 | 3.407 to 3.647 |
| Quality | fused without shadows | 4.372 | 4.700 | +0.328 | +7.5% | 3.968 to 4.404 | 4.513 to 4.849 |
| Quality | split without shadows | 2.594 | 3.172 | +0.578 | +22.3% | 2.560 to 2.909 | 3.104 to 3.346 |

| Preset | Mode over Off | Base | Head | Growth | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Growth |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | FXAA with shadows | 0.268 | 0.469 | +0.201 | 0.287, 0.203 to 0.594 | 0.486, 0.348 to 0.857 | 0.461 | 0.686 | +0.225 |
| Native | fused with shadows | 3.673 | 4.376 | +0.703 | 3.674, 3.586 to 3.979 | 4.392, 4.256 to 4.658 | 3.838 | 4.545 | +0.707 |
| Native | split with shadows | 2.867 | 3.265 | +0.398 | 2.847, 2.553 to 3.095 | 3.303, 2.981 to 3.562 | 3.019 | 3.425 | +0.406 |
| Native | fused without shadows | 3.860 | 4.221 | +0.361 | 3.861, 3.539 to 4.056 | 4.236, 3.975 to 4.404 | 3.978 | 4.327 | +0.349 |
| Native | split without shadows | 2.415 | 2.503 | +0.088 | 2.407, 2.256 to 2.801 | 2.505, 2.445 to 2.798 | 2.585 | 2.695 | +0.110 |
| Quality | FXAA with shadows | 0.168 | 0.239 | +0.071 | 0.164, 0.128 to 0.310 | 0.234, 0.155 to 0.456 | 0.265 | 0.343 | +0.078 |
| Quality | fused with shadows | 3.342 | 3.985 | +0.643 | 3.358, 3.234 to 3.471 | 4.005, 3.916 to 4.063 | 3.408 | 4.037 | +0.629 |
| Quality | split with shadows | 2.238 | 2.918 | +0.680 | 2.226, 2.028 to 2.378 | 2.894, 2.768 to 3.069 | 2.304 | 2.964 | +0.660 |
| Quality | fused without shadows | 3.713 | 4.050 | +0.337 | 3.711, 3.285 to 3.803 | 4.062, 3.836 to 4.203 | 3.740 | 4.051 | +0.311 |
| Quality | split without shadows | 1.934 | 2.522 | +0.588 | 1.956, 1.900 to 2.216 | 2.553, 2.424 to 2.661 | 2.017 | 2.595 | +0.578 |

| Preset | Pair | Base | Head | Growth | Base paired median, min to max | Head paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- |
| Native | shadows under the fused resolve | -0.187 | 0.155 | +0.342 | -0.173, -0.298 to 0.139 | 0.117, 0.018 to 0.476 |
| Native | shadows under the split resolve | 0.452 | 0.762 | +0.310 | 0.447, 0.155 to 0.549 | 0.688, 0.483 to 0.866 |
| Native | the fused resolve over FXAA with shadows | 3.406 | 3.907 | +0.501 | 3.409, 3.072 to 3.692 | 3.906, 3.494 to 4.187 |
| Native | the split resolve over FXAA with shadows | 2.600 | 2.796 | +0.196 | 2.576, 2.281 to 2.809 | 2.801, 2.474 to 2.973 |
| Native | split against fused with shadows | -0.806 | -1.111 | -0.305 | -0.825, -1.121 to -0.676 | -1.105, -1.435 to -1.000 |
| Quality | shadows under the fused resolve | -0.371 | -0.065 | +0.306 | -0.340, -0.424 to -0.026 | -0.068, -0.231 to 0.182 |
| Quality | shadows under the split resolve | 0.303 | 0.396 | +0.093 | 0.300, -0.031 to 0.396 | 0.385, 0.211 to 0.482 |
| Quality | the fused resolve over FXAA with shadows | 3.174 | 3.747 | +0.573 | 3.180, 3.009 to 3.269 | 3.746, 3.517 to 3.872 |
| Quality | the split resolve over FXAA with shadows | 2.069 | 2.679 | +0.610 | 2.051, 1.824 to 2.214 | 2.644, 2.459 to 2.802 |
| Quality | split against fused with shadows | -1.104 | -1.068 | +0.036 | -1.129, -1.336 to -0.963 | -1.087, -1.214 to -0.945 |

| Preset | Ref | Coverage internal size | Casters | Darkened display share | Within 2 px of shadow edge | Depth pass renders | Disoccluded frame 9 | Clipped frame 9 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | base | 2560x1440 | 57 | 4.99% | 2.39% | 540 of 540 | 2400 | 273600 |
| Native | head | 2560x1440 | 57 | 5.03% | 2.35% | 540 of 540 | 2400 | 278400 |
| Quality | base | 1707x960 | 57 | 5.12% | 2.39% | 540 of 540 | 2800 | 298000 |
| Quality | head | 1707x960 | 57 | 5.14% | 2.37% | 540 of 540 | 2800 | 302000 |

### linux-vulkan

| Preset | Mode | Base median | Head median | Growth | Relative | Base blocks min to max | Head blocks min to max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Native | off without shadows | 0.953 | 0.985 | +0.032 | +3.4% | 0.888 to 0.992 | 0.888 to 1.071 |
| Native | FXAA with shadows | 1.399 | 1.708 | +0.309 | +22.1% | 1.163 to 1.523 | 1.390 to 1.870 |
| Native | fused with shadows | 4.406 | 5.480 | +1.074 | +24.4% | 4.278 to 4.494 | 5.316 to 5.580 |
| Native | split with shadows | 4.152 | 4.698 | +0.546 | +13.2% | 3.856 to 4.225 | 4.528 to 4.801 |
| Native | fused without shadows | 4.365 | 5.092 | +0.727 | +16.7% | 4.265 to 4.437 | 4.853 to 5.183 |
| Native | split without shadows | 3.651 | 3.964 | +0.313 | +8.6% | 3.612 to 3.793 | 3.913 to 4.140 |
| Quality | off without shadows | 0.560 | 0.560 | 0.000 | +0.0% | 0.501 to 0.579 | 0.525 to 0.571 |
| Quality | FXAA with shadows | 0.801 | 0.899 | +0.098 | +12.2% | 0.708 to 0.947 | 0.785 to 1.021 |
| Quality | fused with shadows | 3.592 | 4.421 | +0.829 | +23.1% | 3.491 to 3.745 | 4.289 to 4.604 |
| Quality | split with shadows | 2.993 | 3.666 | +0.673 | +22.5% | 2.647 to 3.040 | 3.333 to 3.821 |
| Quality | fused without shadows | 3.740 | 4.356 | +0.616 | +16.5% | 3.691 to 3.836 | 4.279 to 4.492 |
| Quality | split without shadows | 2.746 | 3.312 | +0.566 | +20.6% | 2.668 to 2.790 | 3.192 to 3.364 |

| Preset | Mode over Off | Base | Head | Growth | Base paired median, min to max | Head paired median, min to max | Base submit and drain | Head submit and drain | Growth |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | FXAA with shadows | 0.446 | 0.723 | +0.277 | 0.452, 0.275 to 0.571 | 0.706, 0.489 to 0.894 | 0.501 | 0.770 | +0.269 |
| Native | fused with shadows | 3.453 | 4.495 | +1.042 | 3.465, 3.297 to 3.548 | 4.504, 4.270 to 4.621 | 3.468 | 4.524 | +1.056 |
| Native | split with shadows | 3.199 | 3.714 | +0.515 | 3.196, 2.903 to 3.337 | 3.691, 3.548 to 3.827 | 3.225 | 3.721 | +0.496 |
| Native | fused without shadows | 3.412 | 4.107 | +0.695 | 3.416, 3.330 to 3.480 | 4.137, 3.804 to 4.208 | 3.416 | 4.126 | +0.710 |
| Native | split without shadows | 2.698 | 2.979 | +0.281 | 2.697, 2.658 to 2.858 | 2.984, 2.870 to 3.181 | 2.737 | 3.029 | +0.292 |
| Quality | FXAA with shadows | 0.242 | 0.339 | +0.097 | 0.239, 0.128 to 0.391 | 0.340, 0.231 to 0.463 | 0.275 | 0.384 | +0.109 |
| Quality | fused with shadows | 3.033 | 3.861 | +0.828 | 3.025, 2.912 to 3.177 | 3.861, 3.731 to 4.050 | 3.050 | 3.877 | +0.827 |
| Quality | split with shadows | 2.433 | 3.106 | +0.673 | 2.425, 2.091 to 2.503 | 3.110, 2.773 to 3.250 | 2.451 | 3.118 | +0.667 |
| Quality | fused without shadows | 3.180 | 3.796 | +0.616 | 3.180, 3.127 to 3.262 | 3.789, 3.741 to 3.932 | 3.203 | 3.803 | +0.600 |
| Quality | split without shadows | 2.186 | 2.752 | +0.566 | 2.177, 2.088 to 2.258 | 2.752, 2.622 to 2.796 | 2.219 | 2.778 | +0.559 |

| Preset | Pair | Base | Head | Growth | Base paired median, min to max | Head paired median, min to max |
| --- | --- | --- | --- | --- | --- | --- |
| Native | shadows under the fused resolve | 0.041 | 0.387 | +0.346 | 0.048, -0.087 to 0.189 | 0.378, 0.281 to 0.559 |
| Native | shadows under the split resolve | 0.501 | 0.734 | +0.233 | 0.506, 0.179 to 0.599 | 0.695, 0.485 to 0.886 |
| Native | the fused resolve over FXAA with shadows | 3.007 | 3.772 | +0.765 | 3.041, 2.877 to 3.205 | 3.737, 3.608 to 4.128 |
| Native | the split resolve over FXAA with shadows | 2.753 | 2.991 | +0.238 | 2.706, 2.436 to 3.062 | 2.983, 2.767 to 3.313 |
| Native | split against fused with shadows | -0.254 | -0.781 | -0.527 | -0.252, -0.550 to -0.101 | -0.783, -0.970 to -0.515 |
| Quality | shadows under the fused resolve | -0.148 | 0.064 | +0.212 | -0.160, -0.265 to 0.013 | 0.052, -0.100 to 0.264 |
| Quality | shadows under the split resolve | 0.247 | 0.354 | +0.107 | 0.262, -0.114 to 0.336 | 0.372, 0.034 to 0.491 |
| Quality | the fused resolve over FXAA with shadows | 2.791 | 3.522 | +0.731 | 2.783, 2.671 to 2.939 | 3.510, 3.268 to 3.819 |
| Quality | the split resolve over FXAA with shadows | 2.191 | 2.767 | +0.576 | 2.209, 1.700 to 2.296 | 2.793, 2.326 to 2.922 |
| Quality | split against fused with shadows | -0.600 | -0.755 | -0.155 | -0.593, -1.002 to -0.483 | -0.790, -1.058 to -0.533 |

| Preset | Ref | Coverage internal size | Casters | Darkened display share | Within 2 px of shadow edge | Depth pass renders | Disoccluded frame 9 | Clipped frame 9 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Native | base | 2560x1440 | 57 | 4.99% | 2.39% | 540 of 540 | 2400 | 274400 |
| Native | head | 2560x1440 | 57 | 5.03% | 2.35% | 540 of 540 | 2400 | 280000 |
| Quality | base | 1707x960 | 57 | 5.12% | 2.39% | 540 of 540 | 2800 | 296000 |
| Quality | head | 1707x960 | 57 | 5.14% | 2.37% | 540 of 540 | 2800 | 297600 |

## 4. Owner budget, drift and Task 7f reduction

The owner's budget for this second bench is at most 0.500 ms resolve growth over v21.7.1 at 1440p on every T4 backend. This supersedes the first bench's historical no-budget statement. Verdicts below compare the observed printed growth, without rounding it to a tenth or subtracting the noise. The fixture's own printed 1.000 ms budget concerns total resolve and sharpen and is a different budget.

Primary budget table, growth in resolve and sharpen at most for every 1440p resolve case:

| Leg | 1440p case | Fused growth | At or under 0.500 ms | Split growth | At or under 0.500 ms |
| --- | --- | --- | --- | --- | --- |
| windows-d3d11 | boxes Native | +0.543 | OVER | +0.351 | at or under |
| windows-d3d11 | boxes Quality | +0.483 | at or under | +0.425 | at or under |
| windows-d3d11 | field Native | +1.751 | OVER | +1.595 | OVER |
| windows-d3d11 | field Quality | +1.505 | OVER | +1.557 | OVER |
| windows-vulkan | boxes Native | +0.326 | at or under | -0.008 | at or under |
| windows-vulkan | boxes Quality | +0.349 | at or under | +0.543 | OVER |
| windows-vulkan | field Native | +1.462 | OVER | +0.710 | OVER |
| windows-vulkan | field Quality | +1.387 | OVER | +0.952 | OVER |
| linux-vulkan | boxes Native | +0.547 | OVER | +0.078 | at or under |
| linux-vulkan | boxes Quality | +0.424 | at or under | +0.427 | at or under |
| linux-vulkan | field Native | +1.485 | OVER | +0.878 | OVER |
| linux-vulkan | field Quality | +1.391 | OVER | +1.078 | OVER |

Plainly per leg: windows-d3d11 fails the all-case budget, with fused boxes Native and both field presets over it, and split field Native and Quality over it. windows-vulkan fails it, with fused field Native and Quality over it, and split boxes Quality and both field presets over it. linux-vulkan fails it, with fused boxes Native and both field presets over it, and split both field presets over it.

Other 1440p fixtures, kept separate because their subtractions differ. Yard proxy is the printed temporal-with-shadows median minus FXAA-with-shadows median, which removes a shared shadowed frame but is not a timestamped resolve. Outline prints only Split and has no Off or fused control, so its two absolute frame-growth rows are reported rather than inventing fused measurements:

| Leg | 1440p case or proxy | Fused growth | At or under 0.500 ms | Split growth | At or under 0.500 ms |
| --- | --- | --- | --- | --- | --- |
| windows-d3d11 | Native outline off frame | not measured | n/a | +0.247 | at or under (frame proxy) |
| windows-d3d11 | Native outline on frame | not measured | n/a | +0.229 | at or under (frame proxy) |
| windows-d3d11 | yard Native over FXAA | +0.876 | OVER | +0.635 | OVER |
| windows-d3d11 | Quality outline off frame | not measured | n/a | +0.261 | at or under (frame proxy) |
| windows-d3d11 | Quality outline on frame | not measured | n/a | +0.274 | at or under (frame proxy) |
| windows-d3d11 | yard Quality over FXAA | +0.754 | OVER | +0.708 | OVER |
| windows-vulkan | Native outline off frame | not measured | n/a | +0.006 | at or under (frame proxy) |
| windows-vulkan | Native outline on frame | not measured | n/a | +0.050 | at or under (frame proxy) |
| windows-vulkan | yard Native over FXAA | +0.501 | OVER | +0.196 | at or under |
| windows-vulkan | Quality outline off frame | not measured | n/a | +0.387 | at or under (frame proxy) |
| windows-vulkan | Quality outline on frame | not measured | n/a | +0.403 | at or under (frame proxy) |
| windows-vulkan | yard Quality over FXAA | +0.573 | OVER | +0.610 | OVER |
| linux-vulkan | Native outline off frame | not measured | n/a | +0.192 | at or under (frame proxy) |
| linux-vulkan | Native outline on frame | not measured | n/a | +0.215 | at or under (frame proxy) |
| linux-vulkan | yard Native over FXAA | +0.765 | OVER | +0.238 | at or under |
| linux-vulkan | Quality outline off frame | not measured | n/a | +0.375 | at or under (frame proxy) |
| linux-vulkan | Quality outline on frame | not measured | n/a | +0.385 | at or under (frame proxy) |
| linux-vulkan | yard Quality over FXAA | +0.731 | OVER | +0.576 | OVER |

Observed Task 7f speed-up, using only the primary fused and split resolve rows. Ranges below span matching cases, not uncertainty bounds. A reduction above 100% means the second printed growth is slightly negative, not a proved negative overhead:

| Leg | Display width | First growth | Second growth | Growth removed, ms | Growth reduction |
| --- | --- | --- | --- | --- | --- |
| windows-d3d11 | 2560 display | +3.883 to +4.798 | +0.351 to +1.751 | 2.541 to 3.979 | 62.0% to 91.4% |
| windows-d3d11 | 3456 display | +7.985 to +9.218 | +0.884 to +2.992 | 5.724 to 7.780 | 66.7% to 88.9% |
| windows-vulkan | 2560 display | +2.090 to +2.732 | -0.008 to +1.462 | 1.063 to 2.217 | 43.4% to 100.4% |
| windows-vulkan | 3456 display | +4.162 to +5.174 | +0.654 to +2.608 | 2.342 to 4.159 | 47.3% to 86.4% |
| linux-vulkan | 2560 display | +2.117 to +2.851 | +0.078 to +1.485 | 1.074 to 2.223 | 43.6% to 96.6% |
| linux-vulkan | 3456 display | +4.462 to +5.276 | +0.842 to +2.708 | 2.367 to 3.678 | 46.7% to 81.4% |

Off drift control per resolve case, head median minus base median:

| Case | windows-d3d11 | windows-vulkan | linux-vulkan |
| --- | --- | --- | --- |
| boxes, 2560x1440 Native, internal 2560x1440 | -0.061 | +0.020 | +0.033 |
| boxes, 2560x1440 Quality, internal 1707x960 | -0.035 | +0.014 | -0.002 |
| field, 2560x1440 Native, internal 2560x1440 | -0.190 | -0.054 | +0.018 |
| field, 2560x1440 Quality, internal 1707x960 | -0.111 | -0.050 | -0.035 |
| boxes, 3456x2234 Native, internal 3342x2160 | -0.110 | -0.017 | -0.002 |
| boxes, 3456x2234 Quality, internal 2304x1489 | -0.088 | -0.080 | -0.024 |
| field, 3456x2234 Native, internal 3342x2160 | -0.219 | +0.049 | +0.035 |
| field, 3456x2234 Quality, internal 2304x1489 | -0.216 | -0.064 | -0.004 |

The resolve Off median drift spans -0.219 to -0.035 ms on D3D11, -0.080 to +0.049 ms on Windows Vulkan, and -0.035 to +0.035 ms on Linux Vulkan. Yard Off drift is Native -0.060, Quality -0.044 ms on D3D11, +0.035 and -0.010 on Windows Vulkan, and +0.032 and 0.000 on Linux Vulkan. Subtracting Off matters, especially on D3D11.

Noise assessment from the printed blocks and paired ranges only:

- The primary field fused and split rows have disjoint base and head paired ranges at both display sizes on every leg. Their growth is larger than the measured Off drift. The field budget misses are not plausibly explained by that drift alone.
- For boxes 1440p, both Native paths and Quality fused have overlapping paired ranges on all three legs. Quality split overlaps on D3D11, but not either Vulkan leg. Noise can plausibly be as large as those overlapping-row differences. In particular D3D11 Native fused +0.543, Linux Native fused +0.547 and Windows Vulkan Quality split +0.543 are close to the 0.500 limit, so the printed verdict is not a confidence claim. Windows Vulkan Native split -0.008 is not evidence of a real saving.
- At 3456x2234, boxes Native fused and split also have overlapping paired ranges on D3D11. Windows Vulkan overlaps for boxes Native split and Quality fused. Linux Vulkan has no such overlap in the primary fused or split rows at this display size. The overlapping cases carry the same noise caveat.
- Small pass-one, split-versus-fused, outline-increment and shadow-cost differences must be read beside their individual ranges above. An overlap does not prove equality and disjoint min-to-max ranges are not a formal significance test. A single paired bench cannot assign a tight error bar or justify waiving the budget.

## 5. T4 correctness oracles

Each leg passed six `TemporalVisibilityEquivalenceGpuTests` cases and all four yard path-share cases. The equivalence helper and the production A/B history comparison are separate proofs. Passing helper decisions does not erase D3D11 production colour or confidence differences.

General equivalence, 3,145,728 cases per row. Fast includes range skips. Slow reasons sum to slow. Decision, validity, change-bit and predicted-path mismatch counts and skips meeting a D release are all zero in every row:

| Leg | Internal to display | Fast | Slow | Edge | Deviation | Border | Binade | Range skip | Decision mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| windows-d3d11 | 2560x1440 to 2560x1440 | 2417101 | 728627 | 423996 | 265584 | 39047 | 0 | 237315 | 0 |
| windows-d3d11 | 1707x960 to 2560x1440 | 2439958 | 705770 | 425119 | 237786 | 42865 | 0 | 241870 | 0 |
| windows-d3d11 | 3342x2160 to 3456x2234 | 2372951 | 772777 | 423174 | 312762 | 36841 | 0 | 235940 | 0 |
| windows-vulkan | 2560x1440 to 2560x1440 | 2416995 | 728733 | 423996 | 265688 | 39049 | 0 | 237323 | 0 |
| windows-vulkan | 1707x960 to 2560x1440 | 2439783 | 705945 | 425119 | 237991 | 42835 | 0 | 241860 | 0 |
| windows-vulkan | 3342x2160 to 3456x2234 | 2372863 | 772865 | 423174 | 312876 | 36815 | 0 | 235848 | 0 |
| linux-vulkan | 2560x1440 to 2560x1440 | 2416995 | 728733 | 423996 | 265688 | 39049 | 0 | 237323 | 0 |
| linux-vulkan | 1707x960 to 2560x1440 | 2439783 | 705945 | 425119 | 237991 | 42835 | 0 | 241860 | 0 |
| linux-vulkan | 3342x2160 to 3456x2234 | 2372863 | 772865 | 423174 | 312876 | 36815 | 0 | 235848 | 0 |

Binade equivalence, six draws of 1,048,576 cases per size. Each full 6,291,456-case size reports zero differences from D and zero predicted-path mismatches. The diagnostic without the binade rule also prints zero decision and change-bit differences on these T4 devices, which does not prove the rule unnecessary. The helper has explicit binade coverage:

### windows-d3d11, binade draws

| Internal to display | Draw and jitter | Fast | Slow edge | Slow deviation | Slow border | Slow binade | Range skip | D releases | Decision mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2560x1440 to 2560x1440 | (+, +) (0.3125, 0.2109375) | 0 | 48052 | 86761 | 126417 | 787346 | 0 | 463845 | 0 |
| 2560x1440 to 2560x1440 | (+, -) (0.1875, -0.4375) | 192874 | 48084 | 90715 | 123649 | 593254 | 20196 | 463288 | 0 |
| 2560x1440 to 2560x1440 | (-, +) (-0.375, 0.0625) | 192623 | 48705 | 91023 | 125745 | 590480 | 21573 | 463553 | 0 |
| 2560x1440 to 2560x1440 | (-, -) (-0.0625, -0.2890625) | 786753 | 48432 | 88860 | 124531 | 0 | 69623 | 462176 | 0 |
| 2560x1440 to 2560x1440 | zero (0, 0) | 779180 | 48300 | 97532 | 123564 | 0 | 69277 | 462769 | 0 |
| 2560x1440 to 2560x1440 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 47867 | 95780 | 124010 | 780919 | 0 | 463101 | 0 |
| 3342x2160 to 3456x2234 | (+, +) (0.3125, 0.2109375) | 0 | 47990 | 108950 | 122084 | 769552 | 0 | 483241 | 0 |
| 3342x2160 to 3456x2234 | (+, -) (0.1875, -0.4375) | 190213 | 48020 | 112545 | 120385 | 577413 | 19529 | 482635 | 0 |
| 3342x2160 to 3456x2234 | (-, +) (-0.375, 0.0625) | 185625 | 48615 | 112013 | 121326 | 580997 | 18455 | 483386 | 0 |
| 3342x2160 to 3456x2234 | (-, -) (-0.0625, -0.2890625) | 767509 | 48349 | 109678 | 123040 | 0 | 66613 | 482854 | 0 |
| 3342x2160 to 3456x2234 | zero (0, 0) | 761603 | 48207 | 117765 | 121001 | 0 | 66428 | 483072 | 0 |
| 3342x2160 to 3456x2234 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 47801 | 114712 | 120679 | 765384 | 0 | 482962 | 0 |
| 1707x960 to 2560x1440 | (+, +) (0.3125, 0.2109375) | 0 | 48184 | 94746 | 133438 | 772208 | 0 | 454017 | 0 |
| 1707x960 to 2560x1440 | (+, -) (0.1875, -0.4375) | 194662 | 48204 | 85539 | 134274 | 585897 | 20022 | 452021 | 0 |
| 1707x960 to 2560x1440 | (-, +) (-0.375, 0.0625) | 190336 | 48814 | 90053 | 134618 | 584755 | 20809 | 453579 | 0 |
| 1707x960 to 2560x1440 | (-, -) (-0.0625, -0.2890625) | 784863 | 48525 | 80035 | 135153 | 0 | 94483 | 451851 | 0 |
| 1707x960 to 2560x1440 | zero (0, 0) | 770463 | 48430 | 95739 | 133944 | 0 | 92402 | 452259 | 0 |
| 1707x960 to 2560x1440 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 48000 | 93657 | 134310 | 772609 | 0 | 453050 | 0 |

### windows-vulkan, binade draws

| Internal to display | Draw and jitter | Fast | Slow edge | Slow deviation | Slow border | Slow binade | Range skip | D releases | Decision mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2560x1440 to 2560x1440 | (+, +) (0.3125, 0.2109375) | 0 | 48052 | 86360 | 126460 | 787704 | 0 | 463849 | 0 |
| 2560x1440 to 2560x1440 | (+, -) (0.1875, -0.4375) | 193016 | 48084 | 90550 | 123556 | 593370 | 20203 | 463257 | 0 |
| 2560x1440 to 2560x1440 | (-, +) (-0.375, 0.0625) | 192605 | 48705 | 90963 | 125741 | 590562 | 21582 | 463516 | 0 |
| 2560x1440 to 2560x1440 | (-, -) (-0.0625, -0.2890625) | 786786 | 48432 | 88911 | 124447 | 0 | 69618 | 462140 | 0 |
| 2560x1440 to 2560x1440 | zero (0, 0) | 779044 | 48300 | 97716 | 123516 | 0 | 69264 | 462759 | 0 |
| 2560x1440 to 2560x1440 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 47867 | 95365 | 123999 | 781345 | 0 | 463065 | 0 |
| 3342x2160 to 3456x2234 | (+, +) (0.3125, 0.2109375) | 0 | 47990 | 108671 | 122191 | 769724 | 0 | 483291 | 0 |
| 3342x2160 to 3456x2234 | (+, -) (0.1875, -0.4375) | 190274 | 48020 | 112551 | 120407 | 577324 | 19534 | 482652 | 0 |
| 3342x2160 to 3456x2234 | (-, +) (-0.375, 0.0625) | 185597 | 48615 | 111666 | 121351 | 581347 | 18453 | 483416 | 0 |
| 3342x2160 to 3456x2234 | (-, -) (-0.0625, -0.2890625) | 767390 | 48349 | 109760 | 123077 | 0 | 66554 | 482876 | 0 |
| 3342x2160 to 3456x2234 | zero (0, 0) | 761630 | 48207 | 117712 | 121027 | 0 | 66386 | 483122 | 0 |
| 3342x2160 to 3456x2234 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 47801 | 114672 | 120747 | 765356 | 0 | 482984 | 0 |
| 1707x960 to 2560x1440 | (+, +) (0.3125, 0.2109375) | 0 | 48184 | 94465 | 133484 | 772443 | 0 | 454025 | 0 |
| 1707x960 to 2560x1440 | (+, -) (0.1875, -0.4375) | 194742 | 48204 | 85317 | 134267 | 586046 | 20008 | 452008 | 0 |
| 1707x960 to 2560x1440 | (-, +) (-0.375, 0.0625) | 190266 | 48814 | 90242 | 134524 | 584730 | 20800 | 453552 | 0 |
| 1707x960 to 2560x1440 | (-, -) (-0.0625, -0.2890625) | 784699 | 48525 | 80261 | 135091 | 0 | 94522 | 451852 | 0 |
| 1707x960 to 2560x1440 | zero (0, 0) | 770507 | 48430 | 95729 | 133910 | 0 | 92379 | 452318 | 0 |
| 1707x960 to 2560x1440 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 48000 | 93641 | 134316 | 772619 | 0 | 452993 | 0 |

### linux-vulkan, binade draws

| Internal to display | Draw and jitter | Fast | Slow edge | Slow deviation | Slow border | Slow binade | Range skip | D releases | Decision mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2560x1440 to 2560x1440 | (+, +) (0.3125, 0.2109375) | 0 | 48052 | 86360 | 126460 | 787704 | 0 | 463849 | 0 |
| 2560x1440 to 2560x1440 | (+, -) (0.1875, -0.4375) | 193016 | 48084 | 90550 | 123556 | 593370 | 20203 | 463257 | 0 |
| 2560x1440 to 2560x1440 | (-, +) (-0.375, 0.0625) | 192605 | 48705 | 90963 | 125741 | 590562 | 21582 | 463516 | 0 |
| 2560x1440 to 2560x1440 | (-, -) (-0.0625, -0.2890625) | 786786 | 48432 | 88911 | 124447 | 0 | 69618 | 462140 | 0 |
| 2560x1440 to 2560x1440 | zero (0, 0) | 779044 | 48300 | 97716 | 123516 | 0 | 69264 | 462759 | 0 |
| 2560x1440 to 2560x1440 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 47867 | 95365 | 123999 | 781345 | 0 | 463065 | 0 |
| 3342x2160 to 3456x2234 | (+, +) (0.3125, 0.2109375) | 0 | 47990 | 108671 | 122191 | 769724 | 0 | 483291 | 0 |
| 3342x2160 to 3456x2234 | (+, -) (0.1875, -0.4375) | 190274 | 48020 | 112551 | 120407 | 577324 | 19534 | 482652 | 0 |
| 3342x2160 to 3456x2234 | (-, +) (-0.375, 0.0625) | 185597 | 48615 | 111666 | 121351 | 581347 | 18453 | 483416 | 0 |
| 3342x2160 to 3456x2234 | (-, -) (-0.0625, -0.2890625) | 767390 | 48349 | 109760 | 123077 | 0 | 66554 | 482876 | 0 |
| 3342x2160 to 3456x2234 | zero (0, 0) | 761630 | 48207 | 117712 | 121027 | 0 | 66386 | 483122 | 0 |
| 3342x2160 to 3456x2234 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 47801 | 114672 | 120747 | 765356 | 0 | 482984 | 0 |
| 1707x960 to 2560x1440 | (+, +) (0.3125, 0.2109375) | 0 | 48184 | 94465 | 133484 | 772443 | 0 | 454025 | 0 |
| 1707x960 to 2560x1440 | (+, -) (0.1875, -0.4375) | 194742 | 48204 | 85317 | 134267 | 586046 | 20008 | 452008 | 0 |
| 1707x960 to 2560x1440 | (-, +) (-0.375, 0.0625) | 190266 | 48814 | 90242 | 134524 | 584730 | 20800 | 453552 | 0 |
| 1707x960 to 2560x1440 | (-, -) (-0.0625, -0.2890625) | 784699 | 48525 | 80261 | 135091 | 0 | 94522 | 451852 | 0 |
| 1707x960 to 2560x1440 | zero (0, 0) | 770507 | 48430 | 95729 | 133910 | 0 | 92379 | 452318 | 0 |
| 1707x960 to 2560x1440 | smallest positive x, largest y (1E-45, 0.49999997) | 0 | 48000 | 93641 | 134316 | 772619 | 0 | 452993 | 0 |

### Yard path shares

Frame 90, 3,686,400 display pixels per row. Every row prints 0.00% not reaching the call site, 0.00% gated otherwise, 0.00% slow base deviation, and zero stored lock differences from the production scene beside it. Shares below are the printed percentages, rounded to 0.01%. Small rounded shares need not sum exactly. Slow change computed equals slow in every row.

| Leg | Entry and preset | Zero lock R1 | Check runs | Fast | Range skip | Fast computed | Fast no acceptance | Slow | Slow edge | Slow border | Slow binade | Released | Fast released |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| windows-d3d11 | Split Quality | 95.93% | 4.07% | 3.38% | 3.11% | 0.27% | 0.01% | 0.69% | 0.01% | 0.05% | 0.63% | 0.04% | 0.04% |
| windows-d3d11 | Split Native | 99.00% | 1.00% | 0.99% | 0.91% | 0.08% | 0.00% | 0.01% | 0.00% | 0.01% | 0.00% | 0.02% | 0.02% |
| windows-d3d11 | Fused Quality | 95.93% | 4.07% | 3.38% | 3.11% | 0.27% | 0.01% | 0.69% | 0.01% | 0.05% | 0.63% | 0.04% | 0.04% |
| windows-d3d11 | Fused Native | 99.00% | 1.00% | 0.99% | 0.91% | 0.08% | 0.00% | 0.01% | 0.00% | 0.01% | 0.00% | 0.02% | 0.02% |
| windows-vulkan | Split Quality | 95.93% | 4.07% | 3.38% | 3.11% | 0.27% | 0.01% | 0.69% | 0.01% | 0.05% | 0.63% | 0.04% | 0.04% |
| windows-vulkan | Split Native | 99.00% | 1.00% | 0.99% | 0.91% | 0.08% | 0.00% | 0.01% | 0.00% | 0.01% | 0.00% | 0.02% | 0.02% |
| windows-vulkan | Fused Quality | 95.93% | 4.07% | 3.38% | 3.11% | 0.27% | 0.01% | 0.69% | 0.01% | 0.05% | 0.63% | 0.04% | 0.04% |
| windows-vulkan | Fused Native | 99.00% | 1.00% | 0.99% | 0.91% | 0.08% | 0.00% | 0.01% | 0.00% | 0.01% | 0.00% | 0.02% | 0.02% |
| linux-vulkan | Split Quality | 95.93% | 4.07% | 3.38% | 3.11% | 0.27% | 0.01% | 0.69% | 0.01% | 0.05% | 0.63% | 0.04% | 0.04% |
| linux-vulkan | Split Native | 99.00% | 1.00% | 0.99% | 0.91% | 0.08% | 0.00% | 0.01% | 0.00% | 0.01% | 0.00% | 0.02% | 0.02% |
| linux-vulkan | Fused Quality | 95.93% | 4.07% | 3.38% | 3.11% | 0.27% | 0.01% | 0.69% | 0.01% | 0.05% | 0.63% | 0.04% | 0.04% |
| linux-vulkan | Fused Native | 99.00% | 1.00% | 0.99% | 0.91% | 0.08% | 0.00% | 0.01% | 0.00% | 0.01% | 0.00% | 0.02% | 0.02% |

### Production A/B histories and D3D11 failures

Windows Vulkan and Linux Vulkan each passed all 12 A/B cases: random frames, threshold tiles and large threshold frames, fused and split, Native and Quality, six frames per case. They compare colour, state, stored R8 and stored depth every frame. D3D11 passed all four 96x60 threshold-tile cases, but failed the four random and four large-threshold cases below.

Comparison order at this tested head is [colour at line 284, state at 285, stored R8 at 286, stored depth at 289](../../TemporalVisibilityResolveAbGpuTests.cs#L283). `Assert.Equal` takes production as expected and frozen D as actual. Four failures are colour at 284, four state at 285. No stored R8 or stored depth failure is reported. Those later assertions are not reached on the failing frame. The state-failing cases passed colour on that frame, but the colour-failing cases do not reach state. This is not an all-state failure.

Decode: [TemporalFormats](../../../../KhaozEngine.Render3D/Internal/TemporalFormats.cs) defines colour as R16G16B16A16Float and history state as R16G16Float. State red is accumulated sample weight over its cap (confidence), green is the thin-feature lock and motion encoding. [TemporalResolveRig.ReadState](../../TemporalResolveRig.cs#L139) reads the history confidence target. [TemporalTextureIo.Read](../../TemporalTextureIo.cs#L36) expands each half channel into one float32 in top-to-bottom row order. `Bytes` at line 606 then compares the float32 byte arrays, not the packed half targets. Thus compared colour has 16 bytes per texel and state 8, with each channel four little-endian bytes. For byte position p, texel is floor(p / bytes-per-texel), channel is floor(p / 4) modulo channels. Coordinates below are zero-based display texels.

| Frame set | Entry and preset | Line and comparison | First differing byte | Display texel | Channel | Expected (production) | Actual (frozen D) | Float32 bytes expected / actual | First failing frame |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| large threshold | Fused Quality | 285, state | 7377 | (922, 0) | R confidence | 0.00899505615234375 | 0.0089874267578125 | 00 60 13 3c / 00 40 13 3c | not printed |
| large threshold | Split Quality | 285, state | 7377 | (922, 0) | R confidence | 0.00899505615234375 | 0.0089874267578125 | 00 60 13 3c / 00 40 13 3c | not printed |
| large threshold | Split Native | 285, state | 1105 | (138, 0) | R confidence | 0.06292724609375 | 0.06219482421875 | 00 e0 80 3d / 00 c0 7e 3d | not printed |
| large threshold | Fused Native | 285, state | 1105 | (138, 0) | R confidence | 0.06219482421875 | 0.06292724609375 | 00 c0 7e 3d / 00 e0 80 3d | not printed |
| random | Fused Quality | 284, colour | 5409 | (38, 5) | R colour | 0.1341552734375 | 0.13427734375 | 00 60 09 3e / 00 80 09 3e | not printed |
| random | Split Quality | 284, colour | 23073 | (2, 24) | R colour | 0.50390625 | 0.50341796875 | 00 00 01 3f / 00 e0 00 3f | not printed |
| random | Split Native | 284, colour | 11889 | (23, 18) | R colour | 0.56884765625 | 0.568359375 | 00 a0 11 3f / 00 80 11 3f | not printed |
| random | Fused Native | 284, colour | 6081 | (20, 9) | R colour | 0.52880859375 | 0.529296875 | 00 60 07 3f / 00 80 07 3f | not printed |

Every reported byte position is the second byte of the expanded red float. At Native large-threshold position 1105, byte 1106 also differs, 126 versus 128 or the reverse, as the full assertion snippets show. The green state channel is not the reported first mismatch. Only the first mismatch and its neighbouring bytes are printed, so this does not assert the rest of a buffer is equal.

First failing frame: unknown for all eight cases. `Run` loops over zero-based frames 0 to 5, emits its device and size line before the loop, and emits the six-frame success and path totals only after the loop. Its equality assertions have no frame argument or message. The failure log therefore cannot identify which frame first differed. No rerun or instrumentation was authorised or performed to fill that gap.

Full assertion messages, verbatim from the final failure reports. The case names and Run lines preserve the mapping above:

`TemporalVisibilityResolveAbGpuTests.LargeThresholdFramesWriteTheFrozenDHistoryAcrossPowerOfTwoColumns(choice: 0, preset: Quality)`, `Run` line 285.

```text
Assert.Equal() Failure: Collections differ
                      ↓ (pos 7377)
Expected: [···, 0, 0, 96, 19, 60, ···]
Actual:   [···, 0, 0, 64, 19, 60, ···]
                      ↑ (pos 7377)
```

`TemporalVisibilityResolveAbGpuTests.LargeThresholdFramesWriteTheFrozenDHistoryAcrossPowerOfTwoColumns(choice: 1, preset: Quality)`, `Run` line 285.

```text
Assert.Equal() Failure: Collections differ
                      ↓ (pos 7377)
Expected: [···, 0, 0, 96, 19, 60, ···]
Actual:   [···, 0, 0, 64, 19, 60, ···]
                      ↑ (pos 7377)
```

`TemporalVisibilityResolveAbGpuTests.LargeThresholdFramesWriteTheFrozenDHistoryAcrossPowerOfTwoColumns(choice: 1, preset: Native)`, `Run` line 285.

```text
Assert.Equal() Failure: Collections differ
                      ↓ (pos 1105)
Expected: [···, 0, 0, 224, 128, 61, ···]
Actual:   [···, 0, 0, 192, 126, 61, ···]
                      ↑ (pos 1105)
```

`TemporalVisibilityResolveAbGpuTests.LargeThresholdFramesWriteTheFrozenDHistoryAcrossPowerOfTwoColumns(choice: 0, preset: Native)`, `Run` line 285.

```text
Assert.Equal() Failure: Collections differ
                      ↓ (pos 1105)
Expected: [···, 0, 0, 192, 126, 61, ···]
Actual:   [···, 0, 0, 224, 128, 61, ···]
                      ↑ (pos 1105)
```

`TemporalVisibilityResolveAbGpuTests.ProductionResolveWritesTheFrozenDHistoryEveryFrame(choice: 0, preset: Quality)`, `Run` line 284.

```text
Assert.Equal() Failure: Collections differ
                       ↓ (pos 5409)
Expected: [···, 63, 0, 96, 9, 62, ···]
Actual:   [···, 63, 0, 128, 9, 62, ···]
                       ↑ (pos 5409)
```

`TemporalVisibilityResolveAbGpuTests.ProductionResolveWritesTheFrozenDHistoryEveryFrame(choice: 1, preset: Quality)`, `Run` line 284.

```text
Assert.Equal() Failure: Collections differ
                       ↓ (pos 23073)
Expected: [···, 63, 0, 0, 1, 63, ···]
Actual:   [···, 63, 0, 224, 0, 63, ···]
                       ↑ (pos 23073)
```

`TemporalVisibilityResolveAbGpuTests.ProductionResolveWritesTheFrozenDHistoryEveryFrame(choice: 1, preset: Native)`, `Run` line 284.

```text
Assert.Equal() Failure: Collections differ
                       ↓ (pos 11889)
Expected: [···, 63, 0, 160, 17, 63, ···]
Actual:   [···, 63, 0, 128, 17, 63, ···]
                       ↑ (pos 11889)
```

`TemporalVisibilityResolveAbGpuTests.ProductionResolveWritesTheFrozenDHistoryEveryFrame(choice: 0, preset: Native)`, `Run` line 284.

```text
Assert.Equal() Failure: Collections differ
                       ↓ (pos 6081)
Expected: [···, 63, 0, 96, 7, 63, ···]
Actual:   [···, 63, 0, 128, 7, 63, ···]
                       ↑ (pos 6081)
```

## 6. Caveats and disposition

- The synthetic resolve and outline fixtures run without shadows. They do not time bilinear PCF. The yard includes real shadows, but the frame-90 visibility check reaches only about 1% of display pixels at Native and 4% at Quality. Roughly 99% or 96% are lock-gated before that check. A lock-dense frame is unmeasured, so these are not a bound on that workload.
- Yard darkened coverage is about 5% and the 2-pixel edge share about 2%. Those are not lock shares. Path export is a correctness readback outside the timed fixtures. It does not isolate the runtime cost of each path.
- Direct3D 11 register pressure grew. FXC DXBC `dcl_temps` for accumulate grows from 30 to 44, with the half, at-display and fused variants detailed in the request. DXBC temps are not the driver's allocated hardware registers. Vulkan compiles SPIR-V, and this record has no equivalent Vulkan register count. Gating texture work does not remove the possible occupancy cost across all shaded pixels.
- The printed CPU wall-clock blocks include driver submission and a GPU drain. They do not split MRT writes, history copy, visibility decisions and bilinear PCF into GPU timestamps. One pair per ref measures neither run-to-run variance nor a precise error bar.
- The owner's all-backend budget is not met. The D3D11 production A/B oracle is not green even though helper decisions match D. First failing frame indices are absent from the logs. Integration, diagnosis and any further run approval remain with the orchestrator. This record changes no renderer, test, workflow or release state.
