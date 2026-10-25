# Task15g renderer correction integration report

Verified source and documentation candidate `9e8b9002d2c6bcb1f1c040c1b8f47ce96138d864`. This report follows in a proof-only commit. The final proof tip and clean-state witness are retained in `/tmp/khaozengine-renderer-corrections-integration/final-state.json` and the worker return. No push or main integration is performed.

## Guard, source and version

Physical path and Git top level are `/Users/antonio/KhaozEngine/.worktrees/perf-baseline`. Branch is `feature/renderer-performance-baseline`. Initial HEAD was exactly `62cf4b977b5bb72590130438a8bfcc34acf23afb`, with clean tracked tree, index and untracked status. `worker-preflight.json` and the supplied `root-preflight.json` retain the starting state. The main checkout is read only and remains clean.

Read root AGENTS, applicable contributor engine/test, build/CI, version and documentation rules, selective CI design, the paid renderer contract, parent Task15 and the cold publication amendment and finishing contract. Accepted reports, reviews, acceptance files and root audits were read. `input-provenance.json` records their precise paths, lengths and hashes. Their already accepted unit selections were not repeated separately.

The final fetch still leaves main and origin/main at `d092cc35e487ac5d684d09a44920ca3cd2a201f0`. Latest tag is v21.13.0. Version 21.14.0 remains staged and the release lock is free. The package-bearing Metal fix rides that version. No version bump is needed, no staged version was released during this task, and the released changelog suffix remains byte-identical. Tooling and documentation do not create another bump.

## Merge ancestry and reconciliation

| Merge | First parent | Exact accepted second parent |
| --- | --- | --- |
| `e50cb197b7227fb94908f4706664eb6bd60f9c8b` | `62cf4b977b5bb72590130438a8bfcc34acf23afb` | `36103e6165e6c174fcbcb7c49db396cd47eebed6` |
| `36b003b257f47d4c029e1e1243ceaea159adfa51` | `e50cb197b7227fb94908f4706664eb6bd60f9c8b` | `1a7ad6e685cec763f2a09a35b40c3657d326a0cb` |
| `68b08598bc379c37ca0bcf4b147ef2976598af64` | `36b003b257f47d4c029e1e1243ceaea159adfa51` | `287a26ad86209ef7834beff79961e26ff5646bea` |

All three merges completed with normal hooks and no conflicts. The retained pilot-proof commit stays in first-parent ancestry. Current committed main was already an ancestor and did not advance, so no redundant main merge was made. Code, script and workflow bytes match the three accepted tips. The additional integration edit is limited to the three living documentation files and this report.

Canonical file identity, unrelated-shard selection, fail-fast setup and `ShouldUnsetParentConfigurationAndPlatform=false` remain intact. Metal keeps allocation-free warmed handle accounting, resource leases, native retention and device-lifetime policy. Entrypoint manifest/Git/recipe inputs belong to the explicit checkout, with authorization, exact runtime/environment equality, recipe ownership and native creation gates preserved. No native or runtime gate is relaxed.

No active equipment, DQ, filtered lane, private game content or sole-manifest authorization branch is imported. Authorization `a58e0d4153030f02c4af68cbfc9ea7aece5e1939` is not an ancestor. Its manifest hash remains `0c1bd0a14eaa3c58959f10c8073e5f7bb998cf3434e8776c666661393541aada`, its workload d092 is untouched, and no executable approval manifest exists in this candidate.

## Complete changed path map

The complete main-to-proof-tip map contains these 18 paths. The source/documentation candidate above contains 17 of them, with this report added next.

| Path | Ownership |
| --- | --- |
| `.github/workflows/cross-platform-gpu.yml` | Accepted native matrix and sync Release setup |
| `.superpowers/sdd/2026-10-21-renderer-performance-baseline/task-15f-entry-root-fix-report.md` | Accepted committed entrypoint proof report |
| `.superpowers/sdd/2026-10-21-renderer-performance-baseline/task-15g-corrections-integration-report.md` | This integration proof report, added in a following documentation-only commit |
| `CHANGELOG.md` | Accepted package-bearing Metal fix in staged 21.14.0 |
| `KhaozEngine.Gpu.Metal/Internal/MetalMemoryDiagnostics.cs` | Accepted inline warmed staging-capacity accounting |
| `KhaozEngine.Gpu.Metal/README.md` | Warmed accounting and lifetime contract |
| `KhaozEngine.Render.Tests/Gpu/MetalStagingMemoryTests.cs` | Eight new device-free allocation and ownership cases |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfCheckout.cs` | Explicit checkout owner for manifest, Git and recipe paths |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfEntrypoint.cs` | Accepted checkout-root entrypoint integration |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfEntrypointRootTests.cs` | Ten new actual-entrypoint refusal regressions |
| `docs/CONTRIBUTOR-RULES.md` | Living native prerequisite and canonical file identity rules |
| `docs/CROSS-PLATFORM.md` | Living native setup and checkout-root admission contract |
| `docs/RENDERER-PERFORMANCE.md` | Living shared verifier prerequisite contract |
| `docs/verification/2026-10-21-renderer-performance-baseline.md` | Retained first-pilot proof and current correction-batch evidence |
| `scripts/build-release-tests.sh` | Accepted exact canonical identity and reduced-root inspection |
| `scripts/gpu-shard-build.sh` | Accepted shared setup with preserved Release propagation and Debug refusal |
| `scripts/tests/build-release-tests.test.sh` | Accepted bounded command fixtures for native setup and selection |
| `tools/Renderer.Perf.Tests/authorized/README.md` | Explicit checkout ownership and no fallback instructions |

## Fresh common verification

Every build, test, format and script check ran alone through build-slot. The full solution setup, full suite and solution format used the heavy lane. Exact argv, cwd, start/end, integer exits, full logs, source snapshots and SHA-256 values are in `commands.jsonl`. Commands below are actual command vectors, not suggested reruns.

Shared `sh scripts/build-release-tests.sh KhaozEngine.slnx` exits 0. Both inner builds report zero warnings and zero errors. The isolated `tools/Renderer.Perf.Tests` Release build exits 0 with zero warnings and errors, without running its performance fact. Binary hashes for Render.Tests, Metal, the verifier and the isolated host are in `release-binary-identities.json`. The final source audit checks that they remain unchanged.

The ordinary full Release selection ran exactly once with `KE_GPU_TESTS` and `KE_RENDERER_PERF_UNIT` unset, `--no-build`, filter `Category!=LiveSocket`, logger `trx` and a dedicated results directory. It exits 0. All 41 assembly TRX files have distinct names, including the logger collision suffixes. Result-element totals are 39,630 Passed, zero Failed and 1,530 NotExecuted, not the adapter summary counter. Every named result and skip is retained in `full-test-outcomes.json`, `full-trx-summary.json` and the raw TRX files. No unrelated failure or flake occurred and no full-suite rerun or focused .NET rerun followed.

| Assembly | Passed | Failed | NotExecuted |
| --- | ---: | ---: | ---: |
| `KhaozEngine.Accounts.Tests` | 286 | 0 | 46 |
| `KhaozEngine.Attachments.Tests` | 261 | 0 | 0 |
| `KhaozEngine.Audio.Tests` | 206 | 0 | 3 |
| `KhaozEngine.Automation.Tests` | 110 | 0 | 0 |
| `KhaozEngine.BugReports.Client.Tests` | 205 | 0 | 0 |
| `KhaozEngine.BugReports.Relay.Azure.Tests` | 112 | 0 | 21 |
| `KhaozEngine.BugReports.Relay.Tests` | 623 | 0 | 0 |
| `KhaozEngine.BugReports.Tests` | 442 | 0 | 0 |
| `KhaozEngine.Catalog.AzureBlob.Tests` | 25 | 0 | 7 |
| `KhaozEngine.Catalog.Tests` | 1533 | 0 | 0 |
| `KhaozEngine.CodeHealth.Analyzers.Tests` | 51 | 0 | 0 |
| `KhaozEngine.Foundation.Tests` | 1624 | 0 | 0 |
| `KhaozEngine.Game.Tests` | 6329 | 0 | 1 |
| `KhaozEngine.Gui.Tests` | 1532 | 0 | 0 |
| `KhaozEngine.Identity.Exchange.Tests` | 177 | 0 | 0 |
| `KhaozEngine.ItemInstances.Tests` | 895 | 0 | 0 |
| `KhaozEngine.Localization.Analyzers.Tests` | 46 | 0 | 0 |
| `KhaozEngine.MapDoc.Compatibility.Tests` | 14 | 0 | 0 |
| `KhaozEngine.MapDoc.Physics.Tests` | 247 | 0 | 0 |
| `KhaozEngine.MapDoc.Tests` | 1777 | 0 | 0 |
| `KhaozEngine.MapEditor.Tests` | 1748 | 0 | 9 |
| `KhaozEngine.Movement.Tests` | 1491 | 0 | 3 |
| `KhaozEngine.Netcode.Abstractions.DecouplingTests` | 4 | 0 | 0 |
| `KhaozEngine.Netcode.DecouplingTests` | 4 | 0 | 0 |
| `KhaozEngine.Particles.Tests` | 106 | 0 | 0 |
| `KhaozEngine.Physics.HeadlessLoaderTests` | 5 | 0 | 0 |
| `KhaozEngine.Render.Tests` | 12220 | 0 | 1113 |
| `KhaozEngine.Server.Tests` | 4073 | 0 | 317 |
| `KhaozEngine.Showcase.Tests` | 83 | 0 | 0 |
| `KhaozEngine.Simulation.Tests` | 309 | 0 | 0 |
| `KhaozEngine.Tests` | 89 | 0 | 0 |
| `KhaozEngine.TileEdit.Tests` | 110 | 0 | 7 |
| `KhaozEngine.TileWorld.Netcode.Tests` | 1389 | 0 | 0 |
| `KhaozEngine.TileWorld.Physics.Tests` | 201 | 0 | 0 |
| `KhaozEngine.TileWorld.Tests` | 650 | 0 | 0 |
| `KhaozEngine.TileWorld.WorldMap.Tests` | 35 | 0 | 0 |
| `KhaozEngine.Windowing.Tests` | 147 | 0 | 0 |
| `KhaozEngine.WorldMap.Gui.Tests` | 73 | 0 | 3 |
| `KhaozEngine.WorldMap.Tests` | 353 | 0 | 0 |
| `KeDungeon.Tests` | 13 | 0 | 0 |
| `PixelLabSheetAssembler.Tests` | 32 | 0 | 0 |

Correction inclusion in that same full run is 71 Passed in the accepted Metal selection, including all eight new cases, ten Passed in the new entrypoint-root selection, and 49 Passed in the existing related entrypoint selection. All have zero failures and skips. The named new outcomes follow.

| New correction case | Outcome |
| --- | --- |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Capacity_overflow_rolls_back_creation_without_partial_counts_or_peak` | Passed |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Dead_arena_refuses_creation_but_disposal_releases_existing_staging_once` | Passed |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Duplicate_creation_rolls_back_native_reference_and_preserves_owner_until_release` | Passed |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Invalid_creation_and_nil_ownership_leave_the_ledger_empty` | Passed |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Setup_creation_overflow_releases_native_staging_without_registering_its_handle` | Passed |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Warmed_arena_trim_recreates_blocks_without_managed_allocation(retention: 0, freshBlocks: 24)` | Passed |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Warmed_arena_trim_recreates_blocks_without_managed_allocation(retention: 16, freshBlocks: 0)` | Passed |
| `KhaozEngine.Tests.Gpu.MetalStagingMemoryTests.Warmed_native_creation_and_release_allocate_no_managed_accounting_leases` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Invalid_checkout_or_authorization_refuses_before_runtime_admission(change: "extra-change")` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Invalid_checkout_or_authorization_refuses_before_runtime_admission(change: "foreign-checkout")` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Invalid_checkout_or_authorization_refuses_before_runtime_admission(change: "manifest-hash")` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Invalid_checkout_or_authorization_refuses_before_runtime_admission(change: "missing-root")` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Invalid_checkout_or_authorization_refuses_before_runtime_admission(change: "nested-root")` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Invalid_checkout_or_authorization_refuses_before_runtime_admission(change: "relative-root")` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Invalid_checkout_or_authorization_refuses_before_runtime_admission(change: "wrong-head")` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Missing_checkout_manifest_does_not_fall_back_to_a_foreign_manifest_under_test_host` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Nested_test_host_reads_authorized_checkout_manifest_then_refuses_runtime_before_native_startup` | Passed |
| `KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfEntrypointRootTests.Runtime_admitted_fixture_refuses_recipe_sources_from_another_checkout_before_startup` | Passed |

The outside-solution `Measure_authorized_unit` fact is absent from the full run. GPU, optional integration and other skipped cases remain NotExecuted. No skip is converted into native evidence.

| Actual full publication variant | Retained frames | Combined root/compiler bytes |
| --- | ---: | ---: |
| `complete-small` | 16944 | 199834765 |
| `complete-unsupported` | 16944 | 199829805 |
| `held-invalid` | 16944 | 200047436 |

These are synthetic publication proofs, not measured native frames. Their retained frame counts and combined byte sizes match Task15b history. `full-fixture-proofs.json` keeps actual accounting, requests, counts, scalar precision and FULL_PROOF rows. `full-fixture-file-audit.json` independently matches every copied file length and hash before cleanup. Exactly 656,561,341 generated bytes are then removed from the three fixture-copy directories. Raw TRX, complete log, named outcomes and compact proof remain. No worktree, branch, other checkout or existing Debug output is cleaned.

All three format checks exit 0 without changes. Whole-tree dash, prose, file-size, contributor-instruction and doc-version guards pass. Shell syntax, warning-level Shellcheck and whitespace checks pass. Instruction bytes are 8,320 of 16,384. File-size output retains only the existing Scene3D advisory, 2,148 lines against baseline 2,228. No baseline is changed, grown or exempted.

## Retained red checks and precise limits

The first setup-fixture command in this long-lived worktree exits 1 at the native shard Debug-output guard. The witness contains 171 Debug directories and 42 files, with directory dates predating this task. The wrapper explicitly targets a fresh CI checkout. No Debug output is removed or moved and no source guard is weakened. The exact fixture is then run once in a bounded fresh source copy under scratch, with script/workflow/solution/project bytes hashed against this candidate, and exits 0. `fresh-setup-source-identity.json`, `fresh-setup-result.json` and both full logs preserve the exact environment difference. This establishes the fresh-CI fixture result and does not relabel the original worktree refusal green. This is the #1565 setup owner and its documented fresh-checkout boundary, not a new native execution claim. The stub fixture performs no real dotnet build, test, determinism or native workload. Native Windows aliases explicitly remain unexecuted.

R-1 follow-up preserves that refusal as history. The fixture now supplies its own disposable source
layout from tracked working-tree inputs, so contributors run it directly in an existing checkout.
The corrected worktree run exits 0 with 17 route/status messages, including a real-wrapper sandbox
Debug-output refusal. The same 171 worktree Debug directory paths remain before and after. The
production guard is unchanged. Command logs and hashes are retained under
`/tmp/khaozengine-renderer-corrections-integration/fix-r1`. Native Windows aliases remain unexecuted.

Raw actionlint exits 1 for both main and this candidate. Three diagnostics match exactly in message and source location, unknown ubuntu-26.04 labels at 478:14 and 1800:14, and existing SC2012 at 928:9. No suppression or baseline growth is introduced. The first comparison script greedily parsed SC2012 embedded coordinates as the header location and exited 1. That audit failure and original parser remain. The corrected .yml-anchored parser verifies the unchanged raw logs and exits 0. Neither raw actionlint command is reported green or rerun.

The earlier local full-suite result remains 39,602 Passed, 1,530 NotExecuted and ten Failed. Its affected corrections and the later hosted 39,612-pass result remain separate historical evidence. This task supplies a new once-only common result for the merged correction source. It does not overwrite or waive either history.

First pilot run 40133376096 remains a consumed failed attempt, with one failed fixed fact, zero measured frames, four billed GPU minutes and USD 0.208. No complete original runtime environment, native admission or measurement report was observed. Workload d092 and authorization a58 are untouched. No retry, fresh pilot manifest or paid authority is created.

Native Windows identity, original native Metal recording/allocation proof, real runtime equality, compiler retention, driver behavior, GPU timing, payload/deadline/cost and baseline acceptance remain open gates. No native GPU, device, window, renderer performance fact, native timing, load/stress campaign, workflow dispatch/rerun, package/feed write, release/tag, settings, memory, agent, push, main merge or branch cleanup is performed.

## Command and evidence register

Every row uses the assigned worktree as the outer cwd. The fresh fixture explicitly records its nested cwd under scratch. Each source snapshot records its exact HEAD and full tracked-file identity map. The initial runner rehashed all tracked files. Its preserved replacement uses the full-run map plus every Git delta against that exact HEAD, avoiding repeated unchanged binary reads. Both runners and `runner-snapshot-optimization.json` remain. The final audit independently hashes source again and checks the binary identities.

| Label | Exit | Full log | SHA-256 |
| --- | ---: | --- | --- |
| `fetch` | 0 | `fetch.log` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `merge-native-ci` | 0 | `merge-native-ci.log` | `fbfdae587931b5ce4164bc3fd2c73852294f5b7468886e8a996dad8718412012` |
| `merge-metal` | 0 | `merge-metal.log` | `fcced5341cdfe2dfeebb6beb7da72b7a82cc670ac26f6ab7989143d4f4d2defa` |
| `merge-entry-root` | 0 | `merge-entry-root.log` | `835a9dab90a660694bc587c52159cd7d9b8ff4c4957a0a454474ba8487e820f3` |
| `release-lock-preflight` | 0 | `release-lock-preflight.log` | `34e1db58b9b3151e2ab31fa37a1ba57323176ce1110dcdc25659850076781fc1` |
| `release-setup` | 0 | `release-setup.log` | `15ddf64535ee19950fd0abe176ce7a881e05f0fefa67e147294ca93dafe9a79c` |
| `measurement-build` | 0 | `measurement-build.log` | `0dd9840bfdf6abe9d5281e8c5aef81ae5725dd8e16fea778a3c69c0349c55bfe` |
| `full-suite` | 0 | `full-suite.log` | `4d1f2ec8fe2644a7b2fc0ba4c81efb50549f3d0c65ad886e1d07d8667f1c4d28` |
| `trx-audit` | 0 | `trx-audit.log` | `1a4a97670610b979aab31b2c65fd09e5ca46bf3e8008bf1225d4abdf1da08996` |
| `format-solution` | 0 | `format-solution.log` | `0a162cc0dc87040d180f75e0757eb515fbb50d616d18c475a72ab8e94abbec36` |
| `format-verifier` | 0 | `format-verifier.log` | `6fa8c49538d79135ee00a6f0705cc337566e0ed6a130c644e7e97223820c3989` |
| `format-measurement` | 0 | `format-measurement.log` | `d64771287dc6e07cfb81a602492e28e0e5354909b55e0d4561ae32babe13500d` |
| `release-setup-fixture` | 1 | `release-setup-fixture.log` | `21e047ab35649a4cd982c152044643ccaa51701b5fed0228e6c3cc1ef883fb2a` |
| `release-setup-fixture-fresh` | 0 | `release-setup-fixture-fresh.log` | `bdcbef8d40227b190e21504970f5e5cf889d5f2b573ea9fe441d9f7b5b9ca01f` |
| `syntax-release-setup` | 0 | `syntax-release-setup.log` | `3d9f0927ee680032fce33f7a41f4bade6cc0c9cbbcb79686d7eeae827b6f6ba4` |
| `syntax-shard-setup` | 0 | `syntax-shard-setup.log` | `a03cd76710a9c25281d8f49a8a9fee0cd2322fd6f4bafd659ab2f2dea01b7c8d` |
| `shellcheck` | 0 | `shellcheck.log` | `f698d47015a6f5dc8c1eba51b659239fa7dd424c7cffde80cc13ea70c392a48d` |
| `dashes-tree` | 0 | `dashes-tree.log` | `46bcbfa9421de319271d5bcc32fbb0817d7e5f97511212b242efeaae7dc6cea7` |
| `prose-tree` | 0 | `prose-tree.log` | `82085359944488ee70f062def979ede22ad9b2e02b752fc4cfa3e2ba4b098b8f` |
| `size-tree` | 0 | `size-tree.log` | `c8416704eec3764de57fd4b419b16e2839af37e7fd0f0c06e070cba389fc1348` |
| `instructions-tree` | 0 | `instructions-tree.log` | `c2eb3e9b044673b1da0ad42c71aa339df020aefbde265e0671902a5748f2c961` |
| `doc-versions` | 0 | `doc-versions.log` | `69eb11004baa0dd596d2c8cdf262dc7c7f771e406dffe06bccda7ff220940570` |
| `whitespace-worktree` | 0 | `whitespace-worktree.log` | `9a0d20b8ec7f1276759add0c9aa7ea6dd343591d4883da1dda5597adaf89e0c8` |
| `whitespace-index` | 0 | `whitespace-index.log` | `501c0756ac8cc51312c5c1db6edcc75c52a6a2c0030fe801291b3c821ec90c87` |
| `actionlint-main` | 1 | `actionlint-main.log` | `c8e988cca5865ea72bfffb618e8c873c99cbba4e7b70c1d8a6213646323492f0` |
| `actionlint-candidate` | 1 | `actionlint-candidate.log` | `1ac8420f8f0cca30a551546cb86aa77fa56a1331c846259f77b170b128338748` |
| `actionlint-comparison` | 1 | `actionlint-comparison.log` | `51ea46e673a756b55b3fddc3229269f36b78d50018eb99d7941c248f56132d02` |
| `compact-fixtures` | 0 | `compact-fixtures.log` | `9f96064daabfd6f1749a1194dbcb16894639740205fe91c2f3bc381bdf4d509f` |
| `actionlint-comparison-corrected` | 0 | `actionlint-comparison-corrected.log` | `11eb8ce2980a12827608942ea4dac856b08bfd9326da66bab7ced2374eb75879` |
| `fetch-final` | 0 | `fetch-final.log` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `release-lock-final` | 0 | `release-lock-final.log` | `f1337f4a74b096b954421ee7d1747f04b977eefbb2de16425dca0a5747a814e2` |
| `candidate-source-audit` | 0 | `candidate-source-audit.log` | `3c9ddef8868f64940831ed56a4c01a31d6900a9a806c63712285a71f9fb2dd83` |
| `final-doc-dashes` | 0 | `final-doc-dashes.log` | `a8adf54ead52188b94ce1b45dc7c5d6ca99d511067adedf303d6c6e4386d8921` |
| `final-doc-prose` | 0 | `final-doc-prose.log` | `6e3db6cc23607612a39b5f1b6e4674a63eec2e15d7626187f28e511a5b366f2b` |
| `final-doc-whitespace` | 0 | `final-doc-whitespace.log` | `72b20dc787111032eca561e477ff8902fed1b6a09bde9e954b8c27ef6382ed11` |
| `commit-candidate-docs` | 0 | `commit-candidate-docs.log` | `0654488561f33c801fd63bab8b615df81141c0d46399784057e736aa157253da` |

All logs, snapshots and the complete register are under `/tmp/khaozengine-renderer-corrections-integration`. Post-report text/whitespace checks, the proof-only commit and final clean-state audit append their exact records there after this report is written. Their hashes are retained externally to avoid a self-referential report hash.

Actual command vectors follow.

### fetch

```bash
git fetch origin
```

### merge-native-ci

```bash
build-slot --label renderer-15g-merge-native -- git merge --no-ff -m 'merge(renderer): retain accepted native CI prerequisite correction' 36103e6165e6c174fcbcb7c49db396cd47eebed6
```

### merge-metal

```bash
build-slot --label renderer-15g-merge-metal -- git merge --no-ff -m 'merge(renderer): retain accepted Metal staging accounting correction' 1a7ad6e685cec763f2a09a35b40c3657d326a0cb
```

### merge-entry-root

```bash
build-slot --label renderer-15g-merge-entry -- git merge --no-ff -m 'merge(renderer): retain accepted checkout root admission correction' 287a26ad86209ef7834beff79961e26ff5646bea
```

### release-lock-preflight

```bash
build-slot --label renderer-15g-release-lock -- sh scripts/release-lock.sh status
```

### release-setup

```bash
build-slot --heavy --label renderer-15g-release-setup -- sh scripts/build-release-tests.sh KhaozEngine.slnx
```

### measurement-build

```bash
build-slot --label renderer-15g-measurement-build -- dotnet build tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj -c Release
```

### full-suite

```bash
build-slot --heavy --label renderer-15g-full-suite -- env -u KE_GPU_TESTS -u KE_RENDERER_PERF_UNIT TMPDIR=/tmp/khaozengine-renderer-corrections-integration/runtime-tmp KE_RENDERER_PERF_TASK4_EVIDENCE=/tmp/khaozengine-renderer-corrections-integration/full-fixtures dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket' --logger trx --results-directory /tmp/khaozengine-renderer-corrections-integration/trx
```

### trx-audit

```bash
build-slot --label renderer-15g-trx-audit -- python3 /tmp/khaozengine-renderer-corrections-integration/summarize-trx.py
```

### format-solution

```bash
build-slot --heavy --label renderer-15g-format-solution -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --verbosity diagnostic
```

### format-verifier

```bash
build-slot --label renderer-15g-format-verifier -- dotnet format tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj --verify-no-changes --no-restore --verbosity diagnostic
```

### format-measurement

```bash
build-slot --label renderer-15g-format-measurement -- dotnet format tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj --verify-no-changes --no-restore --verbosity diagnostic
```

### release-setup-fixture

```bash
build-slot --label renderer-15g-setup-fixture -- sh scripts/tests/build-release-tests.test.sh
```

### release-setup-fixture-fresh

```bash
build-slot --label renderer-15g-setup-fixture-fresh -- python3 /tmp/khaozengine-renderer-corrections-integration/run-fresh-setup-fixture.py
```

### syntax-release-setup

```bash
build-slot --label renderer-15g-setup-syntax -- sh -n scripts/build-release-tests.sh
```

### syntax-shard-setup

```bash
build-slot --label renderer-15g-shard-syntax -- sh -n scripts/gpu-shard-build.sh
```

### shellcheck

```bash
build-slot --label renderer-15g-shellcheck -- shellcheck -S warning scripts/build-release-tests.sh scripts/gpu-shard-build.sh scripts/tests/build-release-tests.test.sh
```

### dashes-tree

```bash
build-slot --label renderer-15g-dashes -- sh scripts/check-dashes.sh --tree
```

### prose-tree

```bash
build-slot --label renderer-15g-prose -- sh scripts/check-prose.sh --tree
```

### size-tree

```bash
build-slot --label renderer-15g-size -- sh scripts/check-file-size.sh --tree
```

### instructions-tree

```bash
build-slot --label renderer-15g-instructions -- sh scripts/check-agent-instructions.sh --tree
```

### doc-versions

```bash
build-slot --label renderer-15g-doc-versions -- bash scripts/check-doc-versions.sh
```

### whitespace-worktree

```bash
build-slot --label renderer-15g-worktree-whitespace -- git diff --check
```

### whitespace-index

```bash
build-slot --label renderer-15g-index-whitespace -- git diff --cached --check
```

### actionlint-main

```bash
build-slot --label renderer-15g-actionlint-main -- actionlint /tmp/khaozengine-renderer-corrections-integration/main-cross-platform-gpu.yml
```

### actionlint-candidate

```bash
build-slot --label renderer-15g-actionlint-candidate -- actionlint .github/workflows/cross-platform-gpu.yml
```

### actionlint-comparison

```bash
build-slot --label renderer-15g-actionlint-comparison -- python3 /tmp/khaozengine-renderer-corrections-integration/compare-actionlint.py
```

### compact-fixtures

```bash
build-slot --label renderer-15g-compact-fixtures -- python3 /tmp/khaozengine-renderer-corrections-integration/compact-fixtures.py
```

### actionlint-comparison-corrected

```bash
build-slot --label renderer-15g-actionlint-compare-corrected -- python3 /tmp/khaozengine-renderer-corrections-integration/compare-actionlint.py
```

### fetch-final

```bash
git fetch origin
```

### release-lock-final

```bash
build-slot --label renderer-15g-release-lock-final -- sh scripts/release-lock.sh status
```

### candidate-source-audit

```bash
build-slot --label renderer-15g-candidate-audit -- python3 /tmp/khaozengine-renderer-corrections-integration/final-audit.py candidate-source-audit-state
```

### final-doc-dashes

```bash
build-slot --label renderer-15g-final-doc-dashes -- sh scripts/check-dashes.sh --tree
```

### final-doc-prose

```bash
build-slot --label renderer-15g-final-doc-prose -- sh scripts/check-prose.sh --tree
```

### final-doc-whitespace

```bash
build-slot --label renderer-15g-final-doc-whitespace -- git diff --check
```

### commit-candidate-docs

```bash
build-slot --label renderer-15g-commit-candidate -- git commit -m 'docs(renderer): record correction batch verification' -- docs/CONTRIBUTOR-RULES.md docs/CROSS-PLATFORM.md docs/verification/2026-10-21-renderer-performance-baseline.md
```

## Handoff

No manual validation is needed for this preparation. Root owns the evidence audit, fresh integration review, final live-state check, main push and post-main pack. If those pass, integrate this clean proof tip through root. If rejected, preserve this worktree and return the concrete finding against the candidate. Task15 native baseline and original armour acceptance remain incomplete.
