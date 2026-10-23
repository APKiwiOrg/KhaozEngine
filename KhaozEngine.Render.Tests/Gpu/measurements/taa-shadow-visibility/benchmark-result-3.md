# T4 bench rounds 3, 4 and the Task 7h K round

Growth is head minus base in the printed resolve and sharpen at most (resolve fixtures) or the resolve over FXAA with shadows from the medians (shadowed yard), in milliseconds at 2560x1440 on the hosted Tesla T4. The owner's budget is at most 0.5 ms over v21.7.1, and on 2026-10-22 the owner accepted the shadowed yard read within the roughly 0.1 ms run-to-run drift.

## Round 3, head 1b7ba67c4

Correctness [39591902724](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39591902724) passed entry identity, A/B and equivalence on all three legs. Its only failures were the stored-depth gather oracle, where NVIDIA gathers return every NaN as 0x7fffffff. The stored-depth contract became NaN for NaN at 6c0382161.

Resolve pair, base v21.7.1 [39591919708](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39591919708), head [39591935309](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39591935309):

| Leg | Case | Fused growth | Split growth |
| --- | --- | ---: | ---: |
| windows-d3d11 | boxes Native | +0.308 | +0.375 |
| windows-d3d11 | boxes Quality | +0.193 | +0.340 |
| windows-d3d11 | field Native | +1.309 | +1.495 |
| windows-d3d11 | field Quality | +1.074 | +1.463 |
| windows-vulkan | boxes Native | +0.321 | +0.052 |
| windows-vulkan | boxes Quality | +0.304 | +0.463 |
| windows-vulkan | field Native | +1.400 | +0.802 |
| windows-vulkan | field Quality | +1.267 | +1.009 |
| linux-vulkan | boxes Native | +0.506 | +0.104 |
| linux-vulkan | boxes Quality | +0.405 | +0.552 |
| linux-vulkan | field Native | +1.308 | +0.758 |
| linux-vulkan | field Quality | +1.106 | +1.054 |

Shadowed yard pair, base 562033e74 (v21.7.1 plus the yard test) [39613796197](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39613796197), head [39613815194](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39613815194):

| Leg | Preset | Fused growth | Split growth |
| --- | --- | ---: | ---: |
| windows-d3d11 | Native | +0.428 | +0.598 |
| windows-d3d11 | Quality | +0.392 | +0.630 |
| windows-vulkan | Native | +0.501 | +0.231 |
| windows-vulkan | Quality | +0.631 | +0.629 |
| linux-vulkan | Native | +0.665 | +0.299 |
| linux-vulkan | Quality | +0.706 | +0.677 |

Decomposition [39610967582](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39610967582) put V0 shipped minus V1 check removed at 0.67 to 1.48 ms in the field and -0.23 to +0.48 ms in the boxes. Its absolute values ran up to 0.49 ms above the cost pair, so only same-run differences are used.

## Round 4, head 3e8358b09 (Task 7h Z, the exact positive-zero lock bypass)

Correctness [39654917809](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39654917809) passed on all three legs, including the lock-release probe and the NaN-for-NaN gather contract. NVIDIA Direct3D 11 writes identical fused and split histories without `precise`.

Cost pair, base 562033e74 [39654935809](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39654935809), head [39654953723](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39654953723). Resolve fixtures (shadows off):

| Leg | Case | Fused growth | Split growth |
| --- | --- | ---: | ---: |
| windows-d3d11 | boxes Native | +0.151 | +0.190 |
| windows-d3d11 | boxes Quality | +0.079 | +0.166 |
| windows-d3d11 | field Native | +1.350 | +1.532 |
| windows-d3d11 | field Quality | +1.010 | +1.326 |
| windows-vulkan | boxes Native | +0.512 | +0.040 |
| windows-vulkan | boxes Quality | +0.335 | +0.557 |
| windows-vulkan | field Native | +1.530 | +0.738 |
| windows-vulkan | field Quality | +1.408 | +1.038 |
| linux-vulkan | boxes Native | +0.249 | +0.018 |
| linux-vulkan | boxes Quality | +0.218 | +0.440 |
| linux-vulkan | field Native | +0.985 | +0.527 |
| linux-vulkan | field Quality | +1.045 | +0.957 |

Shadowed yard:

| Leg | Preset | Fused growth | Split growth |
| --- | --- | ---: | ---: |
| windows-d3d11 | Native | +0.499 | +0.513 |
| windows-d3d11 | Quality | +0.422 | +0.587 |
| windows-vulkan | Native | +0.555 | +0.312 |
| windows-vulkan | Quality | +0.573 | +0.597 |
| linux-vulkan | Native | +0.362 | +0.099 |
| linux-vulkan | Quality | +0.484 | +0.505 |

Decomposition [39654970582](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39654970582) failed its boxes Native case on every leg in the V6 sanity check, which counted stored lock bits without decoding the moved marks. Fixed at 5c2de244c. The field rows printed and are diagnostic only.

## Task 7h K round, head 779e604f5 (certified fully lit no-check variant)

Correctness [39696023737](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39696023737) passed on all three legs, including TemporalVisibilityCertificateGpuTests, TemporalVisibilityFullyLitGpuTests and the lock-release probe. Cost pair, base 562033e74 [39696035867](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39696035867), head [39696048067](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39696048067). Resolve fixtures (shadows off, certified):

| Leg | Case | Fused growth | Split growth |
| --- | --- | ---: | ---: |
| windows-d3d11 | boxes Native | +0.059 | +0.204 |
| windows-d3d11 | boxes Quality | +0.008 | +0.132 |
| windows-d3d11 | field Native | +0.231 | +0.253 |
| windows-d3d11 | field Quality | +0.045 | +0.157 |
| windows-vulkan | boxes Native | -0.061 | -0.010 |
| windows-vulkan | boxes Quality | +0.063 | +0.109 |
| windows-vulkan | field Native | +0.102 | +0.046 |
| windows-vulkan | field Quality | +0.169 | +0.079 |
| linux-vulkan | boxes Native | -0.094 | -0.018 |
| linux-vulkan | boxes Quality | -0.089 | -0.008 |
| linux-vulkan | field Native | -0.137 | -0.163 |
| linux-vulkan | field Quality | -0.097 | -0.070 |

Shadowed yard (uncertified, the check runs as in round 4):

| Leg | Preset | Fused growth | Split growth |
| --- | --- | ---: | ---: |
| windows-d3d11 | Native | +0.420 | +0.428 |
| windows-d3d11 | Quality | +0.294 | +0.498 |
| windows-vulkan | Native | +0.516 | +0.212 |
| windows-vulkan | Quality | +0.625 | +0.658 |
| linux-vulkan | Native | +0.403 | +0.072 |
| linux-vulkan | Quality | +0.516 | +0.474 |

Windows Vulkan's shadowed Quality split read +0.629, +0.597 and +0.658 across rounds 3, 4 and this round on the same checked program, the largest yard growth on any leg.
