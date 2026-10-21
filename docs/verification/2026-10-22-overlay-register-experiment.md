# Overlay register experiment report

Rejected under the brief. Numerical exactness passed locally and on all three hosted backends, but both requested performance targets missed. NVIDIA Register Count fell from 210 to 164, above 128. At 32 actors, overlay fell from 8.216757 ms to 6.564672 ms, 20.106 percent faster, below the required 25 percent. Exactly two authorized T4 units completed on their first attempts, at an estimated USD 0.364 total.

## Workspace and commits

The initial guard matched `~/KhaozEngine/.worktrees/ec-overlay-regs`, branch `feature/equipment-coverage-overlay-regs`, starting HEAD `f1a298ef107d691d9ea0341ece6f3ffa29d4fc6a`, clean. The measurement worktree is `~/KhaozEngine/.worktrees/ec-overlay-regs-measure`, branch `feature/equipment-coverage-overlay-regs-measure`, created clean at the experiment commit.

| Purpose | Commit | Parents and scope |
| --- | --- | --- |
| Experiment | `3fd0b2a28e8f71dfa92d218fe8512a9c4827fd95` | Starting HEAD as its only parent. Eight scoped files |
| Statistics workload | `fb36a3b7361c21403443d693639ee0c068105b3d` | Exactly the experiment and approved probe `dfbe3693eed16a504696fc6e8fb87222f838fba2` |
| Statistics authorization | `3701f4454578adfc80b6837f6116b427c5c1ebd1` | Workload as its only parent. Adds only `tools/EquipmentCoverage.Perf.Tests/authorized/s1-overlay-regs-20261022-a.json` |

The probe merge had one conflict over an empty line in docs/INDEX.md. Both sets of entries were retained. The measurement shader tree, generated shader records, version and changelog are byte-identical to the experiment. The engine version remains 21.11.0. Both remote branch tips were independently checked. Both worktrees are clean. Every commit has one subject line and no body or attribution.

## Peak live-state inventory

These are logical 32-bit scalar words in source, not std430 sizes or a physical register assignment. The largest source overlap is the continuation/worker pump, with another peak candidate at the crossing normalization join. The compiler capture reports total allocation, not the peak instruction or a source-variable register map.

| Loop-carried state | Before words | After words | Lifetime and contents |
| --- | ---: | ---: | --- |
| Overlay parent | 60 | 42 | Host and sheet triangles 18, area pairs 4, sample point 3, side state 21 before or 3 after, and 14 control/maximum/success words |
| Triangle-height child | 30 | 10 | Old child duplicates triangle 9, point 2, four predicate pairs 8, cached flags 2, two endpoints 6, phase/edge/height 3. Cursor keeps phase, u/v/w pairs, two endpoint z words and edge flag |
| Crossing child | 54 | 3 | Old child has four endpoints 12, two exact values 38, phase/power/x/value 4. Cursor keeps phase, power and x |
| Full overlay graph | 144 | 55 | Private graph carried around the pump |
| Saved crossing side | Included above | 19 shared words per lane | One exact value, 18 limbs plus sign. 1,216 uint words or 4,864 bytes per 64-lane group |

The caller also declares a 12-word face record, a 34-word CovExactJob, a 23-word CovExactResult and frame/work/face/command indices. The exact worker needs its 19-word accumulator, 19-word product, signed-add result, carry/borrow state, RN32 normalization and compact scalar-rounding residuals. At the normalization join, both unrounded sides and the requested round operand must coexist, although copies can coalesce. Full child graphs are reconstructed transiently inside a step. This explains why 55 graph words cannot be treated as a 55-register kernel. The capture does not establish which remaining temporary determines the 164-register peak.

CorrectFaces carries a 60-word height-query graph, including its 30-word triangle-height child, plus the host triangle, actual/nominal points and caller/worker state. It already meets 128 registers. Extending cursor compaction there would need its own C8 and native proof, with only 1,848 bytes of MSL headroom. It was kept as an unchanged control.

## Change and exactness argument

One combined candidate was tried. Its NVIDIA count is 164. There were no separately measured shader alternatives or unreported NVIDIA runs.

- Side signs use two bits each. Exact signs are -1, 0 or 1, encoded as sign+1. All 18 signs are written before intersections consume either table. Source, corner, side and edge-pair order are unchanged.
- Height cursors reconstruct the existing CovTriangleHeightGraph from the immutable parent triangle, point and area. cachedSides remains false and cachedArea remains true. Endpoint x/y are used only in the step that selects and orders the edge. Only endpoint z words survive the division job. The original covTriangleHeightStep performs every arithmetic operation.
- Crossing endpoints are reconstructed from the same unchanged i/j corners. CrossA saves ca in lane-owned shared storage. CrossB loads ca, consumes the worker's cb, chooses the same common scale and requests the same ca rounding, then saves cb. RoundA reloads cb for its original rounding. Both unrounded 18-limb sides remain available at their normalization join. The original covCrossingStep and exact worker perform the arithmetic.
- Shared address `64 * limb + localLane` gives each lane a disjoint column, including its sign word. Every load is dominated by that lane's preceding store. No peer access, atomic or barrier was introduced. Divergent exits remain valid.

The primitive arithmetic, exact worker, rounding, crossing and triangle-height source files are unchanged. PreciseMath remains enabled. Traversal, finite checks, canonical edge precedence, failure attribution, partial maxima, continuation words, phase order and buffer layouts are retained. No floating operation or expression tree was replaced or reassociated.

The shared declaration is included only in overlay physical modules. The initial common-source placement changed unrelated module identities and was narrowed before verification. It did not create a second overlay implementation.

## Numerical and structural evidence

| Proof | Result |
| --- | --- |
| Local Release coverage and corpus selection, MetalNative on Apple M2 Max | 2,615 passed, zero failed, zero skipped |
| New lane-isolation test | 137 reference rows across three workgroups, a partial final group, mixed valid crossings and early exits |
| Hosted Metal render-2, Apple Paravirtual device | 84 compute/golden passes, zero failed or skipped. All 72 frozen cases have zero differing words |
| Hosted D3D11 render-4, Microsoft Basic Render Driver | 84 compute/golden passes, zero failed or skipped. All 72 frozen cases have zero differing words |
| Hosted Vulkan render-5, llvmpipe LLVM 20.1.2 | 83 compute/golden passes, zero failed or skipped. All 72 frozen cases have zero differing words |
| Full [cross-platform-gpu 39641315136](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39641315136) | Success at exact experiment SHA. All 29 jobs successful, including Vulkan sync validation and shard completeness |
| Measurement harness and diagnostics selection | 151 passed, zero failed or skipped. Native Metal correctly reports Unsupported for Vulkan compiler statistics, with zero kernels and no frames |

The lane-isolation test passed on every hosted backend. The local selection includes conformation, continuation, guards, history, complete skin-through-select and frozen output comparisons. Frozen expectations, output hashes, goldens and tolerances were not edited.

C8 records were regenerated only through the documented exhaustive-inline tool and corpus writer. Twenty-five SPIR-V records changed, with four changed text-target records and no resource-layout changes. Only the two overlay modules change HLSL/MSL text. Other changed SPIR-V modules lose three unused global instructions. All bounds, single expanded worker sites, rolled loops and C5/C6 constant private accesses pass without raising a cap.

| Module | Inline before / after | HLSL bytes before / after | MSL bytes before / after |
| --- | ---: | ---: | ---: |
| CorrectFacesOverlay | 20350 / 20766 | 85926 / 90090 | 94126 / 99266 |
| SupportOverlay | 19660 / 20076 | 83296 / 87460 | 90408 / 95690 |
| CorrectFaces | 23463 / 23463 | 90000 / 90000 | 98152 / 98152 |

## T4 compiler statistics

[Run 39645167978](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39645167978), attempt 1, completed S1 unit `s1-overlay-regs-20261022-a`. Its single measurement test passed with zero skips. The returned unit is Completed with both publication flags true, no blocks or frames, and CPU/GPU budget verdicts Unproved. All 36 production pipelines report Available.

Device is VulkanNative, Tesla T4, NVIDIA 590.48.01, PCI 0x10de:0x1eb8. Local size is 64 x 1 x 1. Overlay groups are 37 x 32 x 1. Every captured source and SPIR-V hash matches the independently dumped experiment module. All identity hashes and exact UInt64 statistics were validated.

| Metric | Baseline overlay | Candidate overlay | CorrectFaces control |
| --- | ---: | ---: | ---: |
| NVIDIA Register Count | 210 | 164 | 128 |
| Binary bytes | 79232 | 68480 | 74368 |
| Shared bytes | 0 | 4864 | 1792 |
| Stack bytes | 0 | 0 | 0 |
| Raw local-memory value | 68719476736 | 68719476736 | 68719476736 |
| Spill bytes | Unknown | Unknown | Unknown |

Register allocation is 21.90 percent lower, but remains 36 registers above the 128 target. The baseline is [the earlier compiler capture](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39633636706), run 39633636706. The control retains the same register count, binary size and shared storage. The anomalous 64 GiB local-memory value remains uniform across all 36 kernels. It is not evidence of real per-thread storage, and stack zero does not prove zero spilling. The existing spill-statistic limitation remains.

Using the earlier conditional T4 resource model, raw count 164 rounds to 168 registers per invocation and permits six 64-thread blocks, a 37.5 percent occupancy ceiling, compared with four blocks and 25 percent at baseline. Shared storage permits 13 blocks and is not the modeled limiter here. These are conditional resource ceilings, not achieved-occupancy measurements.

Target source SHA-256 is `f506697ed4956970b644b4cb96c0ae8e743801269890eb3551b632e0276e0845`. Target SPIR-V SHA-256 is `80c9ade18f48245783841f5a5578059690e8b2309fad8e3282f2b2f59ebbd67a`. Capture JSON SHA-256 is `aafcd7cb0afcdbfd58abed08fd4320e5f8cc7ffb39ef9d9920e5bc7bfbb25ad9`.

## T4 timing and gate verdict

[Run 39645942476](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39645942476), attempt 1, ran exactly one passed profile test, zero skips, on experiment commit `3fd0b2a28e8f71dfa92d218fe8512a9c4827fd95`. This uses the same ordinary filtered bench mode as [option A run 39593539869](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39593539869). The profiler and its test source are byte-identical to that baseline.

Both profiles use 12 warm-up and nine measured frames at each of 1 and 32 actors, 36 phases, 74 timestamp markers and identical group dimensions. Backend, T4 identity, driver, fixture, per-actor-count profile fingerprint and topology fingerprint all match. Timestamp periods remain 1 ns. Fixture fingerprint is `ee66a2be04dee32f60e6efa4cec22fd1bf5875cfa31a7e7d7d790191f21e9408`. Topology fingerprint is `1503853b3534a414f7fdf74b8f03fd74d13c83a96eca6a99c32448001d2bf928`.

| Interval | Baseline mean ms | Candidate mean ms | Reduction percent |
| --- | ---: | ---: | ---: |
| 1 actor overlay | 6.811492 | 6.048939 | 11.195 |
| 1 actor outer resolution | 10.642027 | 9.472292 | 10.992 |
| 32 actors overlay | 8.216757 | 6.564672 | 20.106 |
| 32 actors CorrectFaces control | 1.820594 | 1.887111 | -3.654 |
| 32 actors outer resolution | 11.634464 | 10.072505 | 13.425 |

The 25 percent bar is at most 6.162568 ms. The candidate misses by 0.402104 ms. Its 32-actor median is 6.525632 ms and its range is 6.327072 to 7.277984 ms. Even its fastest observed sample is above the threshold. No sample was trimmed or retried.

The nine 32-actor overlay samples in ms are 7.277984, 6.671072, 6.558656, 6.333600, 6.525632, 6.483968, 6.327072, 6.368800 and 6.535264. The one-actor series has a pronounced first-three-sample transient, also seen in the baseline, so its mean is supporting data rather than a stable latency claim. Runtime clocks were not collected. Per-stage timestamps perturb execution and do not establish the ordinary whole-frame coverage budget.

Correctness passes. The register goal and the timing gate both fail. Do not adopt this candidate under the brief.

## Authorized units and cost

| Unit | GPU job UTC | Seconds | Rounded minutes | Estimated USD |
| --- | --- | ---: | ---: | ---: |
| Statistics 39645167978 | 17:35:46 to 17:38:48 on 2026-10-21 | 182 | 4 | 0.208 |
| Timing 39645942476 | 17:41:02 to 17:44:00 on 2026-10-21 | 178 | 3 | 0.156 |
| Total | Two units, no retry | 360 | 7 | 0.364 |

The estimate uses GitHub's current [Linux GPU rate of USD 0.052 per minute and per-job upward rounding](https://docs.github.com/en/billing/reference/actions-runner-pricing). It is a rate-based estimate, not an invoice query. Both runs use main workflow SHA `10659a82c766d2d4ec354d9f215af3c890c7c477`. The workflow bytes were independently checked before each dispatch, SHA-256 `cc8c555f628947a869997fe082e1f262b33573ab437954afa69eea7a7a70bb35`.

S1 manifest SHA-256 is `845280f6125a404bfb5d343b7465b0fe92c6695db44bf8a614a9422da151fd17`. Its authorization commit's single parent and manifest-only diff were checked locally and by hosted admission. The timing unit uses the exact baseline profile filter and immutable experiment SHA.

Only the named `gpu-hosted-bench-linux-vulkan` artifacts were downloaded, 22,062 compressed bytes for statistics and 11,825 for timing. The three required shard TRX artifacts were selected separately. Every download was deleted after validation and derivation. Twelve generated local golden images were removed, with no per-frame images or raw buffers remaining. Logs, TRX files, shader dumps and derived summaries remain under `/tmp/khaozengine-ec-overlay-regs/`.

## Commands and exit codes

Every local build, test and shader measurement used build-slot, with the native coverage selection in the heavy lane. There was no local full-suite loop, load test, stress run or subagent.

| Operation | Exit | Evidence |
| --- | ---: | --- |
| Initial workspace/branch/HEAD/porcelain guard | 0 | Exact assigned worktree, starting commit and clean status |
| Initial and final Release solution builds | 0 | Zero warnings and errors |
| C8 dump before and after source scoping | 1, 1 | Expected stale hash bindings, 39 then 25 refusals, no cap increase |
| Documented exhaustive-inline measurement | 0 | All 47 physical module rows measured |
| Corpus writer | 0 | Three passed |
| Final Metal coverage and corpus selection below | 0 | 2,615 passed, zero skipped |
| Experiment whole-solution format and all tree guards | 0 | Complete format check and dash/prose/size/instruction/doc-version guards |
| Experiment commit, push and remote SHA verification | 0 | Exact code SHA above |
| Hosted correctness dispatch and watch | 0 | All 29 jobs successful |
| Measurement worktree creation | 0 | Clean exact experiment base |
| Probe merge | 1 | One documentation blank-line conflict, resolved without code changes |
| Measurement Perf Release build | 0 | Zero warnings and errors |
| Focused statistics/manifest/diagnostics tests | 0 | 151 passed, zero skipped |
| First measurement whole-solution format | 2 | Missing restore references outside the Perf dependency graph |
| Measurement full solution Release build | 0 | Restored remaining references, zero warnings and errors |
| Final measurement whole-solution and standalone Perf formatting | 0 | No source edits or suppressions needed |
| Measurement dash/prose/size/instruction/doc-version/diff guards | 0 | All passed |
| Workload merge commit, manifest-only commit, push and remote checks | 0 | Exact parents and one added manifest verified |
| Both T4 dispatches and watches | 0 | One successful first attempt each |
| Artifact identities, native statistics and timing comparison validation | 0 | Source/SPIR-V hashes, run IDs, manifest, publication, frames, groups and fingerprints checked |
| Final protected-file, clean-tree and remote-tip checks | 0 | Both branches retained, no integration |

The initial measurement format failure came from formatting a partly restored solution. It reported missing Localization analyzer references and consequential CA2255 diagnostics in unrelated test projects. The full solution build fixed the environment. No code, baseline or suppression was changed to resolve it.

The native verification command was run from the experiment worktree:

```bash
build-slot --heavy --label ec-overlay-regs-metal -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native KE_WRITE_SHADER_CORPUS=0 KE_WRITE_COVERAGE_REFERENCE_HASHES=0 KE_UPDATE_GOLDENS=0 KE_COVERAGE_EVIDENCE_DIR= KE_GOLDEN_EVIDENCE_DIR=/tmp/khaozengine-ec-overlay-regs/golden-evidence dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter '(FullyQualifiedName~Coverage&FullyQualifiedName!~CoverageDispatchProfile)|FullyQualifiedName~ShaderCorpus' --logger 'trx;LogFileName=metal.trx' --results-directory /tmp/khaozengine-ec-overlay-regs
```

The exact successful paid dispatches were:

```bash
gh workflow run gpu-hosted-bench.yml --repo APKiwiOrg/KhaozEngine --ref main -f ref=3701f4454578adfc80b6837f6116b427c5c1ebd1 -f legs=linux-vulkan -f coverage-perf-unit=s1-overlay-regs-20261022-a -f coverage-perf-manifest-sha256=845280f6125a404bfb5d343b7465b0fe92c6695db44bf8a614a9422da151fd17 -f coverage-perf-workload-sha=fb36a3b7361c21403443d693639ee0c068105b3d
gh workflow run gpu-hosted-bench.yml --repo APKiwiOrg/KhaozEngine --ref main -f ref=3fd0b2a28e8f71dfa92d218fe8512a9c4827fd95 -f legs=linux-vulkan -f filter=FullyQualifiedName=KhaozEngine.Tests.Gpu.CoverageDispatchProfileGpuTests.Typical_fixture_reports_each_resolution_dispatch_for_1_and_32_actors
```

No merge to coverage or main, tag, version bump, stash, extra paid run or subagent was used. Both experiment branches stay pushed for review. No manual or windowed validation is required. The two authorized T4 units are consumed.

The durable derived measurements are [compiler statistics](evidence/2026-10-22-overlay-register-stats.json) and [timing samples](evidence/2026-10-22-overlay-register-timing.json). They preserve the all-kernel statistics, identities and selected raw timing samples used by this report.
