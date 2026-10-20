# Equipment coverage GPU-era performance harness implementation plan

> **For agentic workers:** Use the approved program's execution method, with `subagent-driven-development` or `executing-plans`, one task and one writer at a time. Each task requires review before the next writer starts. This document authorizes no implementation or measurement.

**Goal:** Prove the added CPU preparation and native GPU performance gates before the equipment coverage branch merges to engine main, and measure the shared submission cost without covered actors.

**Architecture:** One opt-in measurement project drives a retained typical scene through one manifest, session, sample and reporting protocol. Headless fake sessions prove orchestration without devices or real clocks. Native sessions run only on the specifically approved hosted route. The retired CPU harness remains rejected.

**Tech stack:** .NET 10, xUnit, Scene3D, GpuFrameSubmission, Part A timestamp pools, the hosted stress workflow and gpu-hosted-bench.

**Spec:** `docs/design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md`, especially owner rulings R3/R4, evidence items 5 to 7 and Gates and delivery order. Original design section 11 supplies the typical geometry and completed-frame GPU definition. The planner brief and the owner's 2026-10-20 decision narrow the initial performance proof to 1 and 32 actors. This does not claim completion of the original 128-actor, limit and every-pass-combination characterization.

## Global constraints

- CPU acceptance is at most 2 ms added preparation for 32 typical actors. The 62.5 microsecond per-actor figure is a division of that budget, not a one-actor acceptance gate.
- Native GPU acceptance is at most 15 percent added completed-frame GPU execution for the same typical workload on each approved T4 backend.
- Covered actors execute GPU construction and vertex pulling. Uncovered controls use ordinary vertex-shader skinning. No CPU-construction control or `gpuSkinning=false` equivalence claim is allowed.
- Every measurement run needs a fresh owner approval naming the exact committed workload, runner, frame counts, iterations, duration and cost. Approval of this plan is not run approval.
- CPU measurement uses the hosted ubuntu-26.04 stress route, Release, iterations=1, load=0. GPU measurement uses gpu-hosted-bench on real NVIDIA T4 hardware. Nothing is measured locally.
- No retries, automatic sample extension, test loops, fallback adapters, extra actor counts, stress burners or automatic reruns. A valid budget miss returns to design review.
- Keep existing zero-allocation correctness proofs. Performance samples do not replace or silently change their isolation contract.
- Local implementation builds and bounded headless tests use build-slot. Full suites run once at the finish in the heavy lane. Root owns native correctness verification and its existing approval rules.
- No engine release, game pin, consumer work, integration or push by an implementation worker unless its dispatch brief explicitly assigns it.
- No em or en dashes and no prose semicolons.

## Review focus

1. A renamed historical authorization, a changed checkout or the wrong workflow leg must refuse before device creation or any real clock read. Task 1 pins this.
2. A pending timestamp or exhausted completion ring must retain its fence. It must never produce zero time or silently reduce the sample denominator. Task 6 pins this.
3. Fast fallback, culled actors or a different point-shadow schedule must not pass as the covered workload. Tasks 3 and 7 pin this.
4. A final query before command-list End must not be labelled completed-frame GPU time. Tasks 4, 5 and 7 pin this.
5. A failed final publication, collection-interrupted allocation counter or synthetic identity must not leave a passing unit. Tasks 2 and 7 pin this.

## Source boundary and mandatory post-merge checkpoint

The planner read only the idle source checkout `/Users/antonio/KhaozEngine/.worktrees/ec-gpu-path`, branch `feature/equipment-coverage-gpu-path`, HEAD `480357644b55c242923ceca98bf7326e2570b506`, and main through `git show origin/main:<path>`. The observed origin/main ref was `64426c24642ed171cde3ffc5f76e1ed70e2c7d34`. Neither is a promise about the reconciled branch.

The only planner write is this file. The planner did not inspect the equipment-coverage working tree, other than reading the expressly supplied brief, and did not build, test, time, render, dispatch, download artifacts or commit.

Every starting repository fact below is provisional, marked R, and must be checked against the completed merge before Task 1. Root records the reconciled SHA and dispositions in this plan. A changed fact invalidates dependent proposed manifests until their identities are regenerated.

| ID | Starting fact and source | Required re-validation |
| --- | --- | --- |
| R1 | The source branch is clean at 480357644. The intended destination is the equipment-coverage worktree. | Confirm merge completion, actual worktree root, task branch, clean starting state, merged main SHA and final workload SHA. Preserve the other writer's changes. |
| R2 | Cost entrypoints throw in `CoverageCostHarness.cs`, and `CoverageCostTests.Measure_authorized_case` immediately rejects. Only fake sessions remain. | Preserve every retired entrypoint and rejection test. Check the merge has not restored an executable historical route. |
| R3 | `CoverageCostGeometry` owns the 50x20 grid, two materials, two bones, typical regions and pose recipe. Historical full-scene setup exists only as report descriptions. | Check geometry and commands, actual topology fingerprint, asset import API, supported GPU construction and full-pass setup. Do not copy old topology counts as observed new results. |
| R4 | `UseCoverageVertexSkinning` forces the staged route regardless of UseGpuSkinning, and `ResolveCoverageGpu` packs/uploads/records before ordinary model drawing. | Re-trace admission, command frames, palettes, history, packing, upload, compute and all covered draws after the merge. |
| R5 | `CoverageBatchNativeFixture.Record` has separate staging, Prepare, RenderTotal, readback-record and Submit brackets. Resolution and surface are nested in RenderTotal. Completion enumeration follows an explicit assertion drain. | Re-check placement, production work and any new observers. Never sum nested phases into the aggregate. The 32x24 panel fixture is not the typical/full workload. |
| R6 | GpuFrameSubmission has three retained frames by default, independent participant/consumer capacities, polling Begin, shared Submit callbacks and teardown-only release. Scene readbacks register their participant even for zero rows. | Check fence ownership, capacity, Begin/Abandon/failure behavior and exactly where history commits. Include actual zero-covered scene registration costs. |
| R7 | Idle `FramePhases` has shared submission. origin/main has plain and capture-tap paths, a tap-supplied fence, AfterSubmit and Present. | Inspect the merge's Frame, FramePhases, AppWindow and FrameCaptureTaps together. Preserve capture behavior and one submit. Identify the actual no-tap fast path and tap fence consumer path. |
| R8 | Task 17 uses shared `test-shared/AllocationRegion.cs`, DisableParallelization collections and no-GC windows. Test capture must stay outside allocation windows. | Read the reconciled isolation contract, collection membership and capacity limits. Do not impose a no-GC region on timing samples or move production work outside a measured interval. |
| R9 | Part A supports fixed pools, same-list marker ordering and non-blocking TryResolve with retained same-submission fences. Pending/Invalid preserve ticks and give zero period. | Check capabilities and lifecycle on the reconciled backends. Check whether a tail-timestamp facility has arrived, before implementing Tasks 4 and 5. |
| R10 | Vulkan End closes rendering and restores resting layouts after a caller's last ordinary marker. Metal End flushes pending clears and closes encoders. D3D11 seals/replays through its recorder. | Audit every backend's final GPU-emitting work and submit scheduling. A pre-End interval remains incomplete unless the proposed tail facility or an equivalent is present. |
| R11 | `CoverageBatchDevice.FactoryCounts` forwards capabilities but not CreateTimestampPool. Old recording helpers either drop work, lack fences or copy captured payloads. | Use a real native factory in measurements, forward timestamp creation if wrapping, and inventory recorder copying. None of those helpers alone is a native measurement session. |
| R12 | The branch stress wrapper has build-target/artifact-env and pins shared workflow 1a176109. Observed origin/main lacks those inputs and pins 14dba4a6. | Preserve the needed wrapper inputs after merging. Inspect the exact reusable workflow, ubuntu-26.04 selection, timeout, report-directory behavior, Vulkan libraries and supported preparation hooks. |
| R13 | Main gpu-hosted-bench builds/tests only Render.Tests, uploads only bench-output.log, accepts ref/filter/legs, runs main's admitted workflow and has a 60-minute timeout. | Re-check workflow contents and runner-group admission. Add the opt-in project, manifest selection, real checkout identity, JSON/TRX publication and smaller proof timeout before measurement. |
| R14 | Configured labels are gpu-t4-linux and gpu-t4-windows. Windows may need GRID/WDDM setup. Vulkan can silently select llvmpipe without the existing ICD restrictions. | Root checks current runner availability, GPU identity, driver mode, backend selection, timestamp support and approved billing SKU before asking for a run. No software adapter yields GPU evidence. |
| R15 | Original gates, historical proof semantics and the source map predate this reconciled harness. Version, docs, test lists and guard baselines may have moved. | Re-read the current spec and contributor rules. Check staged version policy, test project friendship, discovery isolation, file-size limits, documentation and workflow guards. |

## Measurement contract

### CPU preparation

`cpu.framePreparation` is one aggregate wall-clock CPU interval per frame. Start immediately before the production host begins submission and Scene.Begin. Include prior-frame non-blocking production outcome consumption, deterministic pose/palette and command-frame preparation, equipment/host staging, PrepareFrame, command-list Begin, every coverage upload and encoding operation, complete RenderInternal, production coverage readback recording, command-list End and GpuFrameSubmission.Submit including history callbacks. Stop after Submit returns and before Present or harness pacing.

The same interval exists on the uncovered control. Timestamp instrumentation is disabled in the CPU run. The measured gate is the difference of aggregate means, not a sum of observers. Optional CPU phase spans are children of this interval and remain diagnostic. Resource setup, asset import, exact/reference computation, assertion capture, report serialization and harness-only timestamp polling are outside the frame interval and charged to the unit deadline. Ordinary production polls and bookkeeping remain inside.

CPU route recommendation: execute the native Vulkan host path on hosted ubuntu-26.04 with lavapipe explicitly identified as software. This includes real engine packing, upload API calls and backend encoding, rather than timing dropped fake commands. It proves only the documented hosted CPU preparation gate. Its GPU execution is never GPU hardware evidence. T4 runs also report CPU observations as corroboration, without substituting them for the owner-approved stress-route proof.

The software GPU can consume host CPU and cause a conservative slowdown. Disclose this route and the pacing below. If root or owner instead wants a recording-only CPU pilot, label it `cpu.engineOnly`, and leave the full CPU gate Unproved. Do not silently equate the two.

At 1 actor report the observed delta and microseconds per actor. Do not extrapolate it to 32. At 32 actors use two reversed comparisons in one process, one off/on and one on/off. Both deltas must be at most 2 ms. Also report their pooled delta and the difference between the two order results.

### GPU execution and seal limitation

Gate samples use two queries, `FrameStart` and `AfterSealWork`. Start before the scene's first GPU work, including coverage compute. The last query must follow every GPU operation emitted by End, including pending clear flushes and Vulkan resting-layout restoration, while still preceding the native list seal. EndTimestamps then ends that same pool batch. The completion fence belongs to that one submitted list.

`gpu.completedFrame = (tailTicks - startTicks) * periodNanoseconds / 1_000_000`.

This includes construction, covered and ordinary draws, cascades, point shadows, outlines, temporal resolve and GPU seal work. It excludes Present, scanout, pre-query queue latency and CPU submission/scheduling. It is GPU service time for the complete recorded frame, not input-to-display latency. Record CPU End, Submit and harness drain wall spans separately, never add their milliseconds to GPU timestamps.

For attribution, a separately identified diagnostic block uses five queries: frame start, before coverage resolution, after coverage resolution, after rendering/readback recording but before End, and after seal work. The resolution pair includes coverage uploads/compute and their ordering. The next interval includes covered draws and the rest of the render graph, not just covered geometry. The final interval exposes seal work. These are subdivisions of the GPU total and are never added to it twice. Use the existing resolution observer or an equivalent internal observer after R4/R5, with the same marker layout on off and on.

**The existing pre-End Part A API alone cannot prove the original completed-frame gate.** Vulkan End can emit later barriers. The plan recommends an opt-in timestamp-tail interface in Tasks 4 and 5. Without it, publish `gpu.recordedBeforeEnd` and leave `gpu.completedFrame` and its gate Unproved. Do not waive this by renaming a render interval or by using Submit plus WaitForIdle wall time.

The two-query gate layout reduces marker perturbation compared with the five-query diagnostic layout. Keep layouts identical within every pair, report both layouts, disclose that marker overhead remains included, and never subtract an invented calibration. A near-threshold result or a baseline dominated by query resolution is inconclusive and needs an owner-approved follow-up, not more frames automatically.

### No covered actor and shared submission

Measure both an empty scene (0 actors) and a normal scene with 32 uncovered actors. Coverage metadata need not be equipped on this baseline. Compare the actual reconciled production FramePhases shared path with a small test-owned legacy host, using the pre-feature main host sequence as the reference. The legacy host performs the same prepare, open, render, End, direct submit and present policy, without GpuFrameSubmission. It is not an alternate shipping path.

Run these comparisons with GPU timestamp recording disabled, so the experiment does not itself add a completion consumer to the no-consumer path. Use identical native devices and one preallocated offscreen framebuffer exposed through a headless window-device adapter. Present forwards the headless device's documented behavior, with no real window. Report full aggregate CPU cost, Begin and Submit children, allocations as informational, submissions, fences, participants, consumers and waits. Do not time an empty GpuFrameSubmission method in isolation and call it a window-loop result.

Add headless semantic cases for no taps, an active fence-retaining tap and retired tap resources after R7. The minimal timing proposal measures no taps. An active-tap performance run is an explicit open question, not extra work hidden in the no-tap approval.

There is no approved numeric threshold for shared-submission overhead. Report absolute microseconds and, for a positive baseline, percentage. It remains an owner acceptance item and cannot pass using the covered-actor 2 ms budget.

### Fixed pacing and completion

Use fixed three-frame batches matching a three-slot timestamp/fence ring. A partial final batch is allowed only at the declared block boundary. After each batch, outside all CPU preparation intervals, perform one explicitly identified harness WaitForIdle to submit D3D11 work to the driver and make batch completion observable. Then poll each slot once non-blockingly. No spin, sleep or retry. A Pending result after that boundary invalidates the unit. Record every harness drain and its wall duration separately.

This is a deliberately paced preparation/service-time experiment, not a throughput or saturated multi-frame performance claim. No production wait is allowed inside a measured frame. If a native command ring waits before a batch is full, report it and leave the gate Unproved rather than deleting the sample. The fixed pacing applies equally to off/on and direct/shared blocks and is part of manifest identity. If root chooses a presenting policy instead, it requires a newly reviewed protocol and approval, not an implementation-time substitution.

## Retained workload

Use `CoverageCostGeometry.Build(Typical)` as the geometry owner. Do not call EquipmentGlb or native constructors in fake contract tests.

- Host is a 2.5 by 1 unit, 50 by 20 cell grid, 2,000 original triangles, two material slices with a shading seam and two bones.
- Two 10 by 5 regions give 200 eligible triangles, level 2 refinement and two disjoint equipment commands. Preserve collar 0.4, Z bounds [-2,2], clearance 0.02 and maximum recession 0.8. Check the current amendment's guard values separately.
- Build topology and GPU reservations through the actual public path. Report source/final counts and semantic fingerprints from that construction. Historical 1,092 source vertices, 1,258 final vertices and 2,332 final triangles are comparison leads, not manifest truth.
- Import the same host and rigid equipment for both routes. Off draws the original host with ordinary GPU skinning and the same equipment. On stages complete equipment, commands and palette, and draws the GPU-built host surface. Set UseGpuSkinning=true explicitly on both. Assert the effective route as well as the requested setting.
- Actor transforms use a centered grid with up to 8 columns, spacing 3 by 1.5 units. Actor identities, current palettes, commands and motion history remain distinct. Pose `(frameIndex + actorIndex) % 2` selects the retained lifted/bent recipe. Reset the deterministic frame sequence and history at each condition, then warm both history generations.
- Use a 1280x720 target and the historical isometric framing style with near=0.1, fitted once to the whole actor grid. Keep frustum culling enabled and prove all intended actors remain main-visible.
- Use four directional shadow cascades with direction (0.3,-0.4,-1), one dynamic shadowed point light of radius 3 near actor 0 at its position plus (0,0,2), target outlines on every actor and forced temporal resolve. Freeze all other quality settings and serialize them. Do not add background GPU load to dilute the percentage denominator.
- Freeze the point-light update and face schedule by deterministic frame index. Compare actual pass counts and schedule fingerprints between conditions. Record which point-shadow faces actually execute. A nominal full-pass setting is insufficient evidence.
- No reference construction, oracle validation, numeric readback copies, payload capture or evidence serialization executes inside the measured frame. Retain outcomes by frame/view/actor and inspect GPU completion outside the interval. Any guard fallback or incomplete result invalidates the covered block.

## Protocol, validity and report

Use a new outside-solution project, `tools/EquipmentCoverage.Perf.Tests/EquipmentCoverage.Perf.Tests.csproj`. New manifest schema is `khaozengine.coverage-perf-unit/1`, report schema is `khaozengine.coverage-perf-report/1`, and authorization identity starts `coverage-gpu-era-perf-`. Historical schemas and authorization identities are explicitly rejected. The new project never scans the old authorized directory.

Each strict manifest contains the exact unit ID, workload SHA, baseline main SHA, fixture/profile version, ordered blocks, effective routes, backend/adapter policy, timestamp layout, batch pacing, warm/measured counts, runner/workflow leg, iterations=1, load=0, cooperative unit/frame limits and the owner's exact approval reference. Hash the exact UTF-8 bytes. Reject duplicate, unknown, missing, mistyped, nonfinite or excessive fields. Initial presets are only CPU smoke, CPU proof and one T4 proof per backend. Do not add arbitrary scene/quality knobs.

No executable approval file ships as part of harness implementation. Draft shapes live in documentation only. After each separate owner approval, root commits that one manifest under the new project's `authorized/` directory and binds the dispatch to its exact ID/hash and workload SHA. A fresh run ID uses workflow run ID, run attempt and process ID. A rerun is a new approval, even with identical source. The approval block is an audit record, not a credential. No code can infer the owner's consent from a prefix.

`git rev-parse HEAD` or the built assembly's verified informational SHA supplies workload identity. GITHUB_SHA alone is insufficient when a workflow on main checks out another ref. Capture both workflow and workload SHAs, manifest bytes/hash, harness assembly identity, topology/pose/pass fingerprints, runtime, GC/tiering settings, clock frequency, host CPU, runner image, workflow/run/attempt, backend, actual adapter/vendor/device/driver, D3D recording mode, timestamp capability and period, and every completion policy.

For each block preserve raw per-frame samples with frame/pose IDs, warm-up count, measured count, CPU aggregate/children, GPU layout/ticks/period, stage and device outcomes, actual actor/pass counts, uploaded bytes, resource growth, wait/submission counts and errors. Preallocate bounded storage. Serialize JSON plus a compact Markdown verdict after measurement and retain TRX and logs. Partial conditions and first failure survive later cleanup/publication failures. Final report publication is charged to the cooperative unit deadline. A failed or missing final unit cannot inherit a condition's pass.

Validity requires every configured warm-up and measured frame, complete device outcomes, no dropped queries, no invalid/disjoint timestamps, no fallback/refusal, all actors visible where required, identical pair identity/configuration, no hot resource growth and no production waits. Synthetic sessions can exercise arithmetic but always publish Unproved. Unsupported timestamps and software devices refuse GPU proof before measurement. Equal adjacent ticks are legal API results. A zero/nonpositive or unresolved baseline total cannot supply a percentage denominator.

Use arithmetic means for the gate because the spec limits added per-frame cost and alternating poses are deliberately balanced. Report per-pose means, median, nearest-rank p95, minimum, maximum and warm-up summaries as diagnostics. Never trim outliers. Require even measured counts. For each reversed 32-actor pair compute:

```text
cpuDelta = mean(on.cpu.framePreparation) - mean(off.cpu.framePreparation)
gpuPercent = 100 * (mean(on.gpu.completedFrame) - mean(off.gpu.completedFrame))
                 / mean(off.gpu.completedFrame)
```

CPU Pass requires both pair deltas <= 2 ms. GPU Pass requires both pair percentages <= 15 percent on the exact backend. Report a valid above-threshold result as Fail. Report invalid, incomplete, under-resolved or scope-incomplete evidence as Unproved, with reasons. Do not clamp negative deltas or average one failing order into a pass. CPU identity need not match between separate jobs, but every within-process comparison must match. Never subtract separate machines' absolute times.

The existing allocation gate remains linked to its native proof SHA and reconciliation check. Timing samples use ordinary GC behavior and collect GC counts, without forcing collections or starting no-GC regions. Allocation values observed there are informational. A new zero-allocation assertion must use the reconciled AllocationRegion contract in an isolated correctness test.

## Design choice requiring root review

Scores are 1 to 10, higher is better. They evaluate complete-frame honesty, implementation scope and maintainability equally.

| Approach | Complete-frame honesty | Small scope | Maintainability | Total |
| --- | ---: | ---: | ---: | ---: |
| Existing markers before End, accurately labelled partial | 3 | 10 | 9 | 22 |
| Backend-specific test callbacks at native End | 9 | 7 | 5 | 21 |
| Optional timestamp-tail interface, consumed by the harness | 10 | 6 | 9 | 25 |

Recommend the optional interface. It keeps ordinary End and existing implementers compatible, exposes the required backend-owned boundary once, and avoids permanent backend-specific test callbacks. It does add reviewed GPU seam work. If root or owner rejects that scope, the plan can still collect diagnostic pilots, but cannot claim the unchanged completed-frame GPU gate.

## File and interface map

- New `KhaozEngine.Render.Tests/Gpu/CoveragePerf/` contains manifest/authorization, driver/session contracts, typical scene owner, timestamp ring, legacy host control, report/arithmetic and their fake tests. Keep responsibilities cohesive and below the size ratchet.
- `CoveragePerfManifest.cs`: enums `CoveragePerfCondition { DirectOff, SharedOff, SharedOn }`, `CoveragePerfTimerLayout { CpuOnly, Total, Phases }`, immutable ordered block and manifest records, strict parser and authorization binding. `CoveragePerfBlock` carries index, actor count, condition, timer layout, warm/measured counts and fixture identity.
- `CoveragePerfSession.cs`: `CoveragePerfClock(Func<long> Now,long Frequency)`, `CoveragePerfFrameSample`, `CoveragePerfSessionInfo`, `CoveragePerfBlockResult`, `ICoveragePerfSession : IDisposable` and `CoveragePerfSessionFactory`. Session methods are `void Frame(int frameIndex,ref CoveragePerfFrameSample sample)`, `void CompleteBatch()`, and `CoveragePerfSessionInfo ReadInfo()`. Samples carry nullable GPU results until completion. All constructors and reads are outside measured intervals.
- `CoveragePerfDriver.cs`: `CoveragePerfReport Run(ReadOnlySpan<byte> manifestJson,CoveragePerfRunIdentity identity,CoveragePerfSessionFactory factory,CoveragePerfClock clock,Action<CoveragePerfBlockResult> publishBlock,Action<CoveragePerfReport> publishUnit)`. It validates before reading clock.Now or opening a session, owns one deadline and never chooses more work than the manifest.
- `CoveragePerfTypicalScene.cs` and `CoveragePerfNativeSession.cs`: retained geometry/import/equipment, deterministic actor poses, native device ownership and the aggregate bracket. Do not add a second geometry generator.
- `CoveragePerfTimestampRing.cs`: fixed three pools and preallocated samples, using `IGpuFrameCompletionConsumer` and `IGpuFrameSubmissionParticipant`. `bool TryBegin(IGpuCommandList commands,GpuFrameSubmission submission,int sampleIndex,CoveragePerfTimerLayout layout)`, `void PollCompleted()`, existing consumer/participant callbacks and Dispose. A false TryBegin invalidates a measurement block. It is not a dropped sample.
- `CoveragePerfLegacyHost.cs`: small pinned pre-feature main host sequence, headless window-device adapter and shared-path comparison. Production shared mode calls actual FramePhases, not a benchmark reimplementation.
- `CoveragePerfReport.cs`: report records, raw sample serialization, summary statistics and `CoveragePerfBudgets.Evaluate(CoveragePerfReport report)`. Define the report and identity records here before later tasks consume them.
- New `KhaozEngine.Gpu/IGpuCommandListTimestampEnd.cs`: optional public interface `IGpuCommandListTimestampEnd` with `void FinishTimestampsOnEnd(IGpuTimestampPool pool,uint finalIndex)`. This arms the current recording's final marker/end batch. The ordinary command-list End emits that marker after GPU finalization and then seals. There is no extra submission.
- New backend `*.TimestampEnd.cs` partials implement that interface. Modify existing End methods only at the finalization boundary. Reuse Part A pool validation and native marker implementations. Do not introduce another query implementation.
- New opt-in project and entry file `CoveragePerfTests.cs`, plus `KhaozEngine.Render.Tests/AssemblyInfo.CoveragePerf.cs` for the test-to-test edge. Ordinary solution tests run only fake bookkeeping and bounded semantic checks.
- Modify `.github/workflows/gpu-hosted-bench.yml`, `.github/workflows/stress-test.yml`, relevant workflow contract tests, `docs/CROSS-PLATFORM.md`, `docs/USING-KHAOZENGINE.md` and GPU README only where the final APIs/workflow need them. Root handles staged-version declarations and the package-bearing changelog under current contributor rules.

## Task 0: Reconcile and freeze the measurement target

Owner: root, after the other agent finishes merging main. No harness writer starts before this checkpoint.

- [ ] Check every R1 to R15 item, read the reconciled code at the changed boundaries, and record the final SHA and dispositions here. Reuse equivalent facilities already on main instead of duplicating them.
- [ ] Resolve the open questions below and approve the completed-frame scope, CPU native-software route and fixed pacing. If a choice changes the protocol, update the dependent tasks and run table first.
- [ ] Inspect the exact shared stress workflow. Determine whether a native Vulkan CPU session can run with current runner dependencies. If not, use the shared-workflow amendment in Task 8. No measurement is attempted to discover missing dependencies.
- [ ] Verify the normal full branch correctness packet remains valid after reconciliation, using the existing Task 10 process. This planning task supplies no replacement evidence.

## Task 1: Fresh protocol and authorization refusal

Files: create `CoveragePerfManifest.cs`, `CoveragePerfSession.cs`, protocol/identity records in `CoveragePerfReport.cs`, and `CoveragePerfManifestTests.cs` in the new CoveragePerf directory.

Interfaces: produce the records and enums in the file map, `CoveragePerfManifest Parse(ReadOnlySpan<byte> utf8)`, and `void ValidateAuthorization(CoveragePerfRunIdentity identity)`. Identity carries workload/workflow SHA, unit ID/hash, runner, leg, workflow run and attempt.

- [ ] Red: tests `RetiredSchemasAndIdentitiesNeverOpenAFactoryOrReadAClock`, `ApprovalIsBoundToShaLegCountsAndExactBytes`, `StrictFieldsRejectDuplicatesUnknownsAndCoercion`, and `OnlyApprovedBlockSequencesAreAccepted`. Assert factory/clock calls are zero on refusal. Cover 0/1/32 actor rules, even counts, max counts, missing approvals, wrong backend and old gpuSkinning fields.
- [ ] Run once: `build-slot --label ec-perf-1-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoveragePerfManifestTests`. Expect failure for the missing new types or assertions.
- [ ] Green: implement strict parsing and identity binding, with only the tabled presets. Preserve old parser/entrypoint behavior unchanged.
- [ ] Run the same focused selection once under label `ec-perf-1-green`. Require exit 0 and no skipped selected tests.
- [ ] Commit explicit paths with `test(coverage): define fresh performance authorization protocol`.

## Task 2: Fake driver, deadlines and publication

Files: create `CoveragePerfDriver.cs`, `CoveragePerfFakes.cs` and `CoveragePerfDriverTests.cs`. Extend the protocol/report records only for defined results.

Interfaces: consume Task 1 types and produce the exact Run signature in the file map. Fakes implement the same session, batch and publisher interfaces with injected numeric clocks. They never call Stopwatch, import geometry, create files/devices or sleep.

- [ ] Red: `OneDeadlineCoversEveryBlockSetupBatchCleanupAndFinalPublication`, `AnExpiredUnitNeverStartsAnotherFactory`, `PartialSamplesSurviveFrameAndCompletionFailure`, `FirstFailureSurvivesCleanupAndPublicationErrors`, `BlockIsPublishedBeforeNextFactory`, and `FinalPublicationFailureRevokesUnitVerdicts`. Exercise all boundaries with explicit injected tick advances, including exactly-at-limit and over-limit values.
- [ ] Run once: `build-slot --label ec-perf-2-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoveragePerfDriverTests`. Expect failing missing behavior.
- [ ] Green: implement one cooperative deadline, fixed declared frames, preallocated storage, partial result capture and terminal publication. A blocked native call can exceed the cooperative limit until the hosted process timeout. State that limit honestly.
- [ ] Run the same selection once under label `ec-perf-2-green`. Require exit 0.
- [ ] Commit `test(coverage): add bounded performance session orchestration`.

## Task 3: Retained typical scene and aggregate CPU interval

Task 0 amendments (A2, A3, A4): pin CoveragePerfLegacyHost.cs to main 5d896b333's FramePhases.RunPlain sequence and name the current FramePhases.Run as the one shared path. The aggregate CPU interval closes at the headless adapter's Present entry, after GpuFrameSubmission.Submit and its history callbacks and before Present is forwarded. Never time the whole Run call. Shared controls include the zero-row RecordCoverageReadbacks participant registration. Production submission goes through IGpuDevice.Submit(IGpuCommandList, ReadOnlySpan<IGpuFence>) and GpuFrameSubmission.Submit(commands, completions). GpuFrameSubmission defaults are three retained slots, eight consumers and eight participants, with a nine-entry combined fence buffer. The native helper must forward CreateTimestampPool and the span Submit. Captured or recording command-list wrappers are excluded from measured work unless every timestamp and tail operation and the native list identity are forwarded and their observer work removed.

Files: create `CoveragePerfTypicalScene.cs`, `CoveragePerfNativeSession.cs`, `CoveragePerfSceneTests.cs`, and `CoveragePerfFrameBracketTests.cs`. Reuse CoverageCostGeometry without activating its old driver.

Interfaces: implement ICoveragePerfSession. Native session construction accepts a manifest block, device/host factory and CoveragePerfClock. The fake factory supplies semantic recording only and marks results Synthetic.

- [ ] Red: `TypicalGeometryAndRoutesAreExplicit`, `EveryActorChangesPoseAndRemainsVisible`, `FullPassScheduleMatchesBetweenConditions`, `AggregateIncludesBeginPrepareUploadsEndAndSubmitExactlyOnce`, and `NestedResolutionAndSurfaceNeverIncreaseAggregate`. Fake event traces must place all production calls between the two aggregate ticks, with nested durations already contained.
- [ ] Run once: `build-slot --label ec-perf-3-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoveragePerfSceneTests|FullyQualifiedName~CoveragePerfFrameBracketTests'`. Expect the new assertions to fail before implementation.
- [ ] Green: implement the retained typical/full scene and deterministic pose/pass schedule. Disable test payload capture and real-clock reads in fake tests. Assert actual preparation/dispatched counts and retain device outcomes for completion validation. Track hot growth and production waits without stack-string capture.
- [ ] Run the same focused selection once under label `ec-perf-3-green`. Require exit 0. Bounded headless tests prove wiring, not native construction or performance.
- [ ] Commit `test(coverage): retain GPU workload and aggregate preparation bracket`.

## Task 4: Completed-frame timestamp contract and Vulkan tail

Task 0 amendments (A5, A8): a failed or abandoned measurement unit quarantines its Recording or Pending timestamp pools through teardown. Clearing the pending tail on the command list does not reset the pool, and no reusable cancellation API is implied. VulkanCommandList.cs is 783 lines against the 800-line physical cap, so the tail lives in the new VulkanCommandList.TimestampEnd.cs partial and End changes stay minimal. Do not grow any .filesize-baseline entry.

Files: create `KhaozEngine.Gpu/IGpuCommandListTimestampEnd.cs`, `KhaozEngine.Gpu.Vulkan/Internal/VulkanCommandList.TimestampEnd.cs`, `CoveragePerfTimestampEndTests.cs` and `VulkanTimestampEndTests.cs`. Modify `VulkanCommandList.cs` at End and its partial interface declaration. Update GPU interface docs.

Interfaces: produce the optional interface in the file map. It arms the same currently recording pool only when all earlier indices have been written and finalIndex is the batch's last index. Once armed, manual WriteTimestamp/EndTimestamps for that tail is refused. Existing implementations without the interface remain source compatible and cannot prove completed-frame timing.

- [ ] Red: `TailIsAfterClearFlushAndRestingLayoutRestoreBeforeNativeEnd`, `ArmingDoesNotSubmitOrAllocatePerFrame`, `ForeignPoolWrongIndexAndDoubleArmAreRefused`, and `AbandonedOrFailedRecordingCannotLeakTailIntoNextBegin`. Use event-ordered fake Vulkan calls and preallocated pools. Preserve ordinary End ordering when no tail is armed.
- [ ] Run once: `build-slot --label ec-perf-4-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoveragePerfTimestampEndTests|FullyQualifiedName~VulkanTimestampEndTests'`. Expect missing interface or ordering failures.
- [ ] Green: validate at arm time, store one pending pool/index in the command list, then use Part A Write/End after existing GPU finalization and before native end. Make failed End and abandonment ownership explicit. No new submit, flush or wait is permitted.
- [ ] Run the same selection once under label `ec-perf-4-green`. Require exit 0.
- [ ] Commit `feat(gpu): support timestamps after Vulkan frame finalization`.

## Task 5: D3D11 and Metal timestamp tails

Task 0 amendments (A5, A8): the same abandoned-pool quarantine rule as Task 4. Metal is 537 lines and the D3D11 recorder 346, keep new work in the TimestampEnd partials.

Files: create `D3D11CommandRecorder.TimestampEnd.cs`, `MetalCommandList.TimestampEnd.cs`, `D3D11TimestampEndTests.cs` and `MetalTimestampEndTests.cs`. Modify the recorder/Metal End methods and interface declarations. Extend `ComputeTimestampGpuTests.cs` with a bounded tail-order correctness case, with no duration threshold or performance report.

Interfaces: implement Task 4's same interface on D3D11CommandRecorder<TEmitter> and MetalCommandList. No backend-specific method names escape to the harness.

- [ ] Red: D3D11 immediate and deferred fake emitters must put the final marker/disjoint end after all draw opcodes and before stream End. Metal fakes must put it after pending-clear flush and encoder closure, before bookkeeping seal. Add missing-capability and failure/reset cases. Query resolve remains non-blocking.
- [ ] Run once: `build-slot --label ec-perf-5-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~D3D11TimestampEndTests|FullyQualifiedName~MetalTimestampEndTests'`. Expect ordering failures.
- [ ] Green: implement the same arming/lifetime contract using each backend's existing timestamp API. Keep marker/end resolve work last and close any encoder it creates before sealing.
- [ ] Run the focused headless selection once under label `ec-perf-5-green`. Require exit 0. Root schedules the bounded ComputeTimestampGpuTests correctness selection on the three standard backend routes as part of the existing native verification gate. Unsupported hosted timestamp devices must report the negative branch, never hardware performance.
- [ ] Commit `feat(gpu): complete frame timestamp tails on D3D11 and Metal`.

## Task 6: Fixed timestamp ring and retained native completion

Task 0 amendments (A3, A4, A5): the ring stays inside the already begun production GpuFrameSubmission, consuming slot fences through TryRetain (ruling in the SDD ledger). Semantic tests cover paired slot and caller fences, independent resets, callback errors and zero-row participants. If CoverageBatchDevice is reused, implement and count FactoryCounts.CreateTimestampPool(uint). Abandoned or failed units quarantine pools through teardown.

Files: create `CoveragePerfTimestampRing.cs`, `CoveragePerfTimestampRingTests.cs`, and a native forwarding device helper in the CoveragePerf directory. Modify the new native session to consume them. If reusing CoverageBatchDevice, explicitly forward CreateTimestampPool and count its cold creation.

Interfaces: consume Parts A and Tasks 4/5. Implement the exact ring methods and completion/participant callbacks from the file map. Pools have capacity 5, active count 2 or 5, one unresolved batch each. Pool/arrays/delegates are cold-owned. Keep per-slot sample index and frame identity immutable until consumed.

- [ ] Red: `PendingNeverMutatesPublishedSamplesOrResetsFence`, `InvalidAndDisjointInvalidateUnit`, `FullRingDoesNotAllocateWaitOrDrop`, `ResolveUsesExactSubmittedFence`, `SubmitFailureQuarantinesUntilTeardown`, `UnsupportedOrSoftwareDeviceCannotProveGpu`, and `FactoryWrapperForwardsTimestampCreation`. Test out-of-order completion, abandonment and multiple consumers on one fence with fakes.
- [ ] Run once: `build-slot --label ec-perf-6-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoveragePerfTimestampRingTests`. Expect lifecycle assertions to fail.
- [ ] Green: retain through GpuFrameSubmission before recording, arm the tail before GpuRecording scope disposal, and resolve using the retained same-submission fence. Polling cannot flush, wait, submit or spin. Only the explicitly separate CompleteBatch harness boundary drains. Do not call teardown-only ReleaseCompleted during ordinary frame polling.
- [ ] Run the same selection once under label `ec-perf-6-green`. Require exit 0. Bounded native correctness checks validate sample identity and finite periods without publishing timing thresholds.
- [ ] Commit `test(coverage): retain asynchronous performance timestamp batches`.

## Task 7: Shared-host control, validity and gate arithmetic

Task 0 amendments (A2, A3): the legacy host is pinned to main 5d896b333 RunPlain, the shared host is the current FramePhases.Run, and the no-tap interval closes at Present entry as in Task 3. Include zero-row scene participant registration in the shared control.

Files: create `CoveragePerfLegacyHost.cs`, `CoveragePerfHostTests.cs`, `CoveragePerfBudgetTests.cs` and `CoveragePerfReportTests.cs`. Complete `CoveragePerfReport.cs` and serialization.

Interfaces: produce CoveragePerfBudgets.Evaluate and the report contract above. The host control consumes the reconciled FramePhases API after R7. Keep legacy baseline SHA and semantics in report identity.

- [ ] Red: `ZeroCoveredActorMeasuresRealSharedHostWithoutQueryConsumers`, `LegacyAndSharedHaveIdenticalDrawAndPresentPolicy`, `CaptureTapUsesOneSubmissionAndRunsAfterSubmit`, and render-suppressed/abandoned-frame cases. Test actual production FramePhases with fakes, no clock timing.
- [ ] Red: numeric tests pin CPU 2.0 Pass and 2.000001 Fail, GPU 15.0 Pass and 15.000001 Fail, both reversed pairs required, ratio-of-means, negative deltas preserved, nonpositive baselines Unproved, and no child double counting. Pin median/nearest-rank p95 and balanced-pose counts using fixed vectors.
- [ ] Red: refuse synthetic, wrong commit/device/driver/route/schedule, dropped frames, hot growth, production waits, unsupported or invalid queries, GPU fallback, partial publication and a pre-End-only sample claiming a full-frame verdict. Timing allocation observations cannot upgrade the separate allocation proof.
- [ ] Run once: `build-slot --label ec-perf-7-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoveragePerfHostTests|FullyQualifiedName~CoveragePerfBudgetTests|FullyQualifiedName~CoveragePerfReportTests'`. Expect missing or failing assertions.
- [ ] Green: implement the small legacy host and production shared host, strict comparability, report serialization and arithmetic. One over-budget reversed pair fails the gate. Publish no invented shared-cost threshold.
- [ ] Run the same selection once under label `ec-perf-7-green`. Require exit 0. Inspect representative fake JSON for raw samples, identities and explicit Unproved reasons.
- [ ] Commit `test(coverage): report complete performance gates and shared host cost`.

## Task 8: Isolated entrypoint and hosted wiring

Task 0 amendments (A6, A7, A8): the shared-workflow amendment in APKiwiOrg/ci-workflows is required before C0, not conditional. It provisions libvulkan1 and mesa-vulkan-drivers, discovers and records the lavapipe ICD from the package file list (as cross-platform-gpu.yml does), selects the native Vulkan backend and software adapter explicitly with KE_VULKAN_REQUIRED=1, and forwards the fresh unit, hash and workload SHA through narrow structured inputs. It sets validated hard job limits of 20 minutes for smoke and 30 for proof, and fails on missing or unfinalized JSON, Markdown or TRX, on zero or more than one executed unit, and on any skipped unit. Existing artifact-env restrictions stay. The engine caller pins the reviewed shared commit. The admitted GPU workflow bootstrap on main stays a separately reviewed commit that keeps GRID, NVIDIA ICD and adapter checks and adds the Perf build, test, report path and 30-minute proof cap. AssemblyInfo.CoveragePerf.cs grants InternalsVisibleTo("EquipmentCoverage.Perf.Tests"). The Perf project follows the Cost project's SDK, xUnit, IsPackable=false and one-way ProjectReference pattern. The new discovery guard allows only the explicit manual measurement branches, and the old Cost guard stays unchanged. Run scripts/tests/gpu-test-shards.test.sh if shard contracts change, and compare actionlint diagnostics against main's set.

Files: new opt-in project/entrypoint and AssemblyInfo.CoveragePerf, new `CoveragePerfDiscoveryTests.cs` and `CoveragePerfWorkflowTests.cs`, modified GPU/stress workflows and living documentation. The existing Cost project and its isolation guard remain unchanged.

Interfaces: `CoveragePerfTests.Measure_authorized_unit(string unitId)` resolves only the exact selected fresh manifest. Missing selection or authorization is a refusal or explicit skip outside a measurement dispatch. A workflow intended to measure must require exactly one executed unit and zero skipped units. Add trait `Category=CoveragePerformance` and explicitly exclude that project/entrypoint from normal solution, CI and GPU correctness discovery.

- [ ] Red: discovery tests prove the Perf project is outside the solution and ordinary CI selections. Workflow tests prove measurement is manual-only, exact unit/hash/SHA are forwarded, an empty selection uses existing behavior, and native GPU identity/capability checks occur before the measurement test. Preserve the old Cost isolation test.
- [ ] Run once: `build-slot --label ec-perf-8-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoveragePerfDiscoveryTests|FullyQualifiedName~CoveragePerfWorkflowTests|FullyQualifiedName~CoverageCostContractTests.Cost_project'`. Expect new guard failures.
- [ ] Green: GPU workflow adds optional `coverage-perf-unit` and manifest hash inputs. Nonempty selection builds/tests the new Perf project with an exact fixed filter. Reject simultaneous free-form measurement filters and use one approved leg. Keep existing driver setup and adapter checks. Set `KE_COVERAGE_PERF_REPORTS` to a per-job directory, retain JSON/Markdown/TRX/logs with always(), fail on missing required reports or zero tests, and cap a proof job at 30 minutes. Record nvidia-smi identity, actual backend capabilities and workload checkout SHA. No all-leg dispatch is used for the proposed individually approved runs.
- [ ] Green: stress wrapper preserves build-target/artifact-env across the merge and selects the new project, exact unit, iterations=1, load=0, Release and `artifact-env=KE_COVERAGE_PERF_REPORTS`. Add a measurement-specific input only where the shared workflow actually needs it. Do not turn artifact-env into a generic arbitrary environment injection feature.
- [ ] If current shared runner images lack Vulkan/mesa setup, amend `APKiwiOrg/ci-workflows/.github/workflows/stress-test.yml` first with a narrowly allowlisted `coverage-perf-cpu` boolean input, default false. Require the exact Perf project, Release, load=0 and iterations=1, install libvulkan1 plus the Mesa Vulkan driver before execution, pin/record the software ICD and selected backend, and forward the fresh unit/hash through structured inputs. Add the matching engine caller inputs and pin the reviewed shared-workflow commit. Confirm the shared repository's own instructions and tests before this cross-repository implementation. No new standalone measurement workflow replaces the shared route.
- [ ] Run the focused guard selection once under label `ec-perf-8-green`, then explicitly build the new opt-in project with `build-slot --label ec-perf-project-build -- dotnet build tools/EquipmentCoverage.Perf.Tests/EquipmentCoverage.Perf.Tests.csproj -c Release`. This builds without measuring. Require exit 0, zero warnings and no runnable approved manifests in the implementation commit.
- [ ] Root must land only the reviewed workflow-enablement change on main before using the GPU runner group's main-only admitted workflow. The measurement code stays on the unmerged feature SHA selected by ref. Do not merge the feature to main to make its own performance workflow usable.
- [ ] Commit explicit harness/wiring paths with `ci(coverage): wire opt-in hosted performance proofs`. Keep any main-only workflow bootstrap as a separately reviewed commit. Update GPU API docs and current package version/changelog only under the engine's existing staged-version rules.

## Task 9: Final verification and approval-sized measurement handoff

Task 0 amendments (A8, A9): v21.9.0 is already released. Root selects the next free additive minor at the finishing checkpoint and moves version, newest changelog entry and guarded declarations together. No tag. Runner readiness at Task 0: gpu-t4-linux and gpu-t4-windows were Ready with maximum_runners 1 each. Gpu-hosted-bench run 39407694921 on main shows the Windows T4 D3D11 runner working, with unrelated TemporalVisibilityResolveAbGpuTests failures tracked in https://github.com/APKiwiOrg/KhaozEngine/issues/1521. Before each run request, root re-checks runtime device, timestamp support, driver mode and price.

Owner: root. This task is a future execution handoff, not permission granted by the plan.

- [ ] Review the whole diff and API/doc sweep. Run one Release solution build and one full headless suite through build-slot, with `--heavy` on the full suite. Run the explicitly outside-solution Perf project build if changes since Task 8 require it. Run format and applicable dash/prose/file-size/doc-version/workflow guards. No timing entrypoint is included.
- [ ] Obtain the standard native correctness packet for the reconciled branch, including timestamp-tail ordering, coverage scene correctness and zero-allocation checks, under the program's native verification rules. Do not claim timing from those correctness tests.
- [ ] Re-check R1 to R15, runner readiness and current prices. Present run C0 only for its own exact approval. Create its executable manifest only after approval, commit it, and retain the exact approval reference. No automatic run follows from a writer finishing.
- [ ] After C0 is valid, separately present C1. After CPU proof and native timing readiness are established, separately present G1, then G2, then G3. Use a new manifest/approval for every run or rerun. Root can stop earlier on a valid miss or incomplete harness.
- [ ] Preserve report artifacts and a compact evidence summary under `docs/superpowers/plans/proofs/`, naming run URLs, manifest hashes, exact SHAs, valid counts, both order results, shared-path observations and open limitations. Root follows the normal durable commit/push rule for accepted evidence.
- [ ] Only when CPU and all approved T4 backend gates pass, allocation correctness is still valid and the owner accepts the shared-path cost may root finish Task 10 integration. A failure returns to design. No tag or game adoption is authorized here.

Future finish commands, run by the implementation owner from the reconciled worktree, include:

```bash
build-slot --label ec-perf-final-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label ec-perf-final-tests -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
build-slot --label ec-perf-final-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
```

Use the reconciled contributor instructions if these command contracts changed. This planner ran none of them.

## Proposed runs, each separately unauthorized

All counts below are per condition block. Every block starts with fresh condition history and fixed pose index 0, then the declared warm-up. Every run is one workflow iteration and has load=0. No retries or automatic repeats. Proposed frame limit is 10,000 ms, checked after return. Unit deadline includes all blocks, setup, pacing, validation, cleanup and final publication. The job timeout is the hard outer bound.

Notation: D is direct legacy host with coverage off, S is production shared host with coverage off, and C is production shared host with coverage on. All CPU rows use timestamps disabled. T is a two-query GPU total block, P is a five-query phase diagnostic block. Within a pair, the entire configuration other than coverage/host condition matches.

| Run | Workload, exact order and frames | Runner and route | Limits and estimated duration | Paid compute estimate |
| --- | --- | --- | --- | --- |
| C0 | 0 actors D,S, then 1 actor S,C. Each block 64 warm + 32 measured. 4 blocks, 384 total frames. Checks native CPU session, zero-covered empty host delta and one-actor route. No 32-actor verdict. | stress-test, hosted ubuntu-26.04, native Vulkan with explicitly selected lavapipe, one iteration | Unit 300 s, job 20 min. Estimate 5 to 15 min including build/setup, at most 5 min measured unit. | Standard hosted Linux on public engine repo, expected USD 0 runner charge. Confirm repo billing/readiness. |
| C1 | 32 actors D,S,C,C,S,D. Each block 64 warm + 128 measured. 6 blocks, 1,152 total frames. Pairs S,C and C,S prove CPU. D,S and S,D measure shared overhead for 32 ordinary actors. | Same stress route as C0, same policy, one iteration | Unit 600 s, job 30 min. Estimate 8 to 20 min including build/setup, at most 10 min measured unit. | Expected USD 0 standard runner charge on public engine repo. |
| G1 | First 1 actor S,C in P, each 64 warm + 32 measured. If valid, 32 actors S,C in P with the same counts, then 32 actors S,C,C,S in T, each 64 warm + 128 measured. 8 blocks, 1,152 total frames. The first two stages diagnose the actual compute/draw/seal route. Only the final reversed 32-actor T pairs decide the GPU gate. | gpu-hosted-bench, leg linux-vulkan, label gpu-t4-linux, actual NVIDIA T4 required, one iteration | Unit 600 s, job 30 min. Estimate 8 to 15 min including setup/build. Stop before the next stage on invalid route/capability/outcome evidence. | USD 0.416 to 0.780 expected. 30-minute timeout ceiling USD 1.560. |
| G2 | Same workload, order, counts and fixed pacing as G1. No frames are borrowed from another backend's report. | gpu-hosted-bench, leg windows-d3d11, label gpu-t4-windows, actual NVIDIA T4 and working WDDM required, one iteration | Unit 600 s, job 30 min. Estimate 12 to 25 min including GRID setup/build. | USD 1.224 to 2.550 expected. Timeout ceiling USD 3.060. |
| G3 | Same workload, order, counts and fixed pacing as G1. | gpu-hosted-bench, leg windows-vulkan, label gpu-t4-windows, actual NVIDIA T4 and NVIDIA Vulkan driver required, one iteration | Unit 600 s, job 30 min. Estimate 12 to 25 min including setup/build. | USD 1.224 to 2.550 expected. Timeout ceiling USD 3.060. |

These are unmeasured planning estimates, not observed performance. A sample deadline can expire earlier than the job timeout, including during setup. Stop and publish partial evidence. Do not enlarge a deadline or shrink the workload without a new proposal and approval.

The minimal proposal is five runs, two CPU and three paid GPU jobs. The one-actor and phase stages are inside each GPU job to avoid paying for separate builds and driver installations. GPU expected total is USD 2.864 to 5.880, with a combined 30-minute-per-job ceiling of USD 7.680. Rates are USD 0.052 per minute for Linux 4-core GPU and USD 0.102 for Windows 4-core GPU. GitHub rounds each job up to a whole minute and charges larger runners even for public repositories. Rates and billing rules come from [GitHub Actions runner pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing), read during planning. Re-check them before owner approval. Artifact storage, tax and other organization activity are not included.

Each approval request must name the final workload SHA, manifest ID/hash, exact row, effective routes, runner/leg, condition order, actor counts, warm/measured frames, one iteration, load=0, unit/job limits and price range/ceiling. G1 approval does not approve G2 or G3. A workflow routing error or zero-test run is not silently rerun.

## Open questions for root and owner

1. Approve the optional timestamp-tail interface, or accept that pre-End diagnostics leave the original completed-frame GPU gate Unproved. Recommendation is the interface, not a scope waiver.
2. Confirm the hosted native-Vulkan/lavapipe CPU host interval is the intended documented CPU gate. Recording-only CPU results cannot include real native upload/encoding. A T4 CPU report is corroboration under the current stress-route instruction.
3. Confirm three-frame batches with explicit harness drains are acceptable for preparation and GPU service-time proof. They deliberately make no saturated-throughput or input-latency claim.
4. What shared-submission overhead is acceptable, and does the owner want a separate active-capture-tap timing run? The current proposal reports no-tap empty and 32-uncovered costs only. Capture semantics remain required correctness checks.
5. Confirm these three T4 backend/OS combinations are the native reference set for this pre-merge gate. Metal capability/correctness remains required, but hosted Apple paravirtual results cannot stand in for real Metal hardware performance. If a real Metal reference is also required, it needs its own permitted hardware route and separate approval.
6. Root must resolve workflow enablement on main and any shared stress-runner preparation gap before a run request. The configured runner labels are not live availability proof.
7. The original section 11 also described 128 actors, limit fixtures, static-transient point shadows and more combinations. The initial five runs prove only the owner-requested typical gates and shared no-tap path. Confirm those wider characterization rows remain outside this merge gate rather than implying they were measured.

## Planner self-review

- The CPU gate has one aggregate interval and reversed off/on comparisons at 32 actors. The one-actor result never stands in for it.
- GPU construction, draws and seal work are covered by the proposed tail facility, with partial pre-End timing explicitly unable to pass the full gate.
- The typical geometry is retained with explicit routes, actual topology evidence, pass schedule identity and device-confirmed non-fallback outcomes.
- Fresh authorization, strict identity, injected-clock fake contracts, deadline/partial/publication handling and opt-in discovery isolation each have an owning task.
- Fixed pools, same-submission fence lifetime, capability refusal, no-blocking resolution and declared harness pacing each have tests and report fields.
- Shared-path overhead is measured without timestamp consumers, with empty and uncovered scene cases and no invented budget.
- Every proposed measurement specifies order, counts, frames, iteration, runner, duration, hard limit and paid cost. Nothing in this plan was executed.
- Every inspected starting fact that the merge can alter is in R1 to R15. Root must settle the listed questions and re-validate those facts before implementation.

## Rulings recorded before execution (2026-10-20)

Owner rulings on the open questions:

- P-1 (question 1): add the optional timestamp-tail interface. The completed-frame GPU gate is measured from FrameStart to AfterSealWork.
- P-2 (question 4): the shared frame-submission overhead is reported for an empty scene and 32 uncovered actors, against the test-owned pre-feature direct-submit host. The owner accepts or rejects it after seeing the numbers. No active capture-tap timing run.
- P-3 (question 5): the NVIDIA T4 set (Linux Vulkan, Windows Direct3D 11, Windows Vulkan) is the GPU reference. Metal correctness stays proven, and real Metal hardware performance is disclosed as unproved.
- P-4 (question 7): the merge gate covers the typical gates only. Wider characterization is https://github.com/APKiwiOrg/KhaozEngine/issues/1519.

Root resolutions:

- Question 2: the native Vulkan host interval on lavapipe, through the hosted ubuntu-26.04 stress workflow, is the documented CPU gate. T4 CPU numbers corroborate only.
- Question 3: fixed three-frame batches with explicit harness drains are accepted. No throughput or latency claim.
- Question 6: root checks runner availability and workflow enablement before each run request.

Reconciliation context: the branch merged main through 5d896b333 at 2c09e933b, with owner ruling T10-1 adding `IGpuDevice.Submit(IGpuCommandList, ReadOnlySpan<IGpuFence>)`. Task 0 records R1 to R15 dispositions against the reconciled head.

## Task 0 dispositions (2026-10-20)

Frozen identity: plan head b143c2334, reviewed code head 7391fc157, merged main 5d896b333 (merge 2c09e933b, earlier merge d0b542378 with main parent 64426c246), clean, no MERGE_HEAD. R2, R3, R4, R5, R8, R9, R10 and R13 hold. R1, R6, R7, R11 and R15 changed. R12 and R14 needed action. Root adopted all nine amendments A1 to A9 from the Task 0 revalidation report and folded them into Tasks 3 to 9 above. The correctness packet for the reconciled code head is the hosted all-leg full run https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39422002850. Executable workload identities are generated only after implementation is committed.
