# Task 15f entrypoint checkout-root fix

## Result and identity

- Implementation commit `4b74e7506d94878c36c3e3530cb2d87578020de4`.
- Its sole parent is the requested starting HEAD `d092cc35e487ac5d684d09a44920ca3cd2a201f0`.
- Branch `fix/renderer-pilot-entry-root`.
- Worktree `/Users/antonio/KhaozEngine/.worktrees/renderer-pilot-entry-root`.
- Starting physical path, Git top level, branch and HEAD matched the brief. Initial tracked and untracked status was empty. The commit guard repeated path, branch, HEAD and unstaged-tree checks.
- Normal hooks passed. Root owns fresh review, push, integration and common finish verification after parallel reconciliation. No push, merge, pack, tag, release, workflow execution or worktree cleanup was performed.

The source fix is committed separately from this report so the implementation SHA is independently addressable.

## Path diff against the parent

| Path | Added | Deleted |
| --- | ---: | ---: |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfCheckout.cs` | 37 | 0 |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfEntrypoint.cs` | 6 | 4 |
| `KhaozEngine.Render.Tests/Gpu/RendererPerf/RendererPerfEntrypointRootTests.cs` | 165 | 0 |
| `docs/CROSS-PLATFORM.md` | 5 | 1 |
| `tools/Renderer.Perf.Tests/authorized/README.md` | 4 | 0 |

No packaged source or package API changed. No dependency, version, changelog, workflow, executable manifest, authorization branch, runtime policy, budget, recipe or shader source hash was changed.

## Fix and input audit

`RendererPerfCheckout` requires an absolute `GITHUB_WORKSPACE` matching `git rev-parse --show-toplevel`. Manifest reads use that validated root with `Path.GetFullPath(relativePath, root)`. Every authorization Git command passes `-C` with the same root. The relative manifest token used for the exact authorization diff remains unchanged.

The existing HEAD, tested ref, authorization SHA, sole workload parent, sole added manifest, independent dispatch hashes, strict manifest parsing, runner checks and runtime/environment equality remain intact. Production never changes process-wide working directory and never searches another checkout or copies a manifest into output.

The remaining inputs used by this entrypoint were audited.

- Frozen recipes already use the existing `RendererPerfRecipe.RecipePath` source-file owner. A new admission check requires those paths to belong to the validated checkout before `coldStart`. It rejects binaries whose recipe sources refer to another checkout. Recipe construction, verification, layouts and publication retain their existing owner.
- Shader identity uses in-memory literal/generated sources. Control GLSL uses embedded assembly resources. Neither depends on process cwd.
- The UI font uses `AppContext.BaseDirectory/Assets/Roboto-Regular.ttf`. Its existing project copy rule was retained. The final isolated project build produced that file in `tools/Renderer.Perf.Tests/bin/Release/net10.0/Assets/`.
- `authorization.json` and output files use the explicit `KE_RENDERER_PERF_REPORTS` directory supplied by the workflow, which is absolute. They are not repository-relative inputs.
- Harness hashing uses the actual assembly location. Linux CPU inspection uses the absolute `/proc/cpuinfo` path. Host telemetry invokes `nvidia-smi` by executable name. These inputs were not changed.

## Actual red and green evidence

The regression invokes the actual `RendererPerfEntrypoint.Run` under a synthetic repository's `tools/Renderer.Perf.Tests/bin/Release/net10.0` directory. It uses the existing bounded Git/manifest fixture. It never reads the real approved manifest.

The primary fixture deliberately approves runtime `10.0.999`, while the observed local testhost is `10.0.13`. After the fix it reads the manifest from the checkout, verifies authorization, writes runtime admission facts and throws the precise `Entrypoint` refusal `Observed runtime, GC, tiering or relevant environment differs from approval.` Only authorization and runtime-admission files exist. This is before `coldStart`, provider registration and native creation.

A second synthetic fixture matches its actual runtime and controlled environment, then reaches the precise recipe-checkout refusal before startup. Missing or relative roots, a nested non-root directory, foreign checkout, wrong HEAD, changed manifest bytes, an extra committed path and a missing root manifest with a foreign host-directory decoy all refuse before native creation.

Fake hosted/GPU flags and process cwd changes exist only inside the fixture. The tests use the existing `NativeDeviceLifecycle` collection, whose definition disables parallelization. The fixture restores environment and cwd and disposes only its temporary synthetic repository.

Actual run sequence follows.

1. Red prerequisite build passed with zero warnings and errors. Against the unchanged entrypoint from the starting HEAD, all 10 new regressions failed. The primary failure was the duplicated relative lookup below the nested host directory.
2. The affected selection ran once and returned exit 1 with 58 passed and one failed. All 49 existing related cases passed. The failing new foreign-checkout case expected the authorization identity refusal, but its unrelated Git repository lacked the requested workload object and refused earlier with `git failed. fatal: bad object`.
3. The fixture was corrected to fetch that parent object from the other synthetic fixture, making the identity refusal independently reachable. Manifest paths and the missing-file assertion also normalize platform separators.
4. Final shared Release setup passed with zero warnings and errors in both builds. The final 10-regression run passed with zero failures and skips. The 49 unchanged existing cases were not rerun. There is no claim that the earlier 59-case command exited green.
5. The actual isolated entrypoint project built with zero warnings and errors. The final unfiltered Render.Tests format check and all required whole-tree guards passed.

No test loop, full suite, heavy publication fixture, real native provider/device, measurement fact, paid run or workflow rerun was executed. No manual validation is needed for this headless proof.

## Commands

All commands ran from the worktree root. Each build, test, format or script-check command ran alone through `build-slot --label <label> -- <command>`. Console output was retained in the scratch logs listed below.

Shared Release setup, used for red preparation and the final verified source tree.

```bash
sh scripts/build-release-tests.sh KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj
```

Actual red and final green regression command, with different labels and log destinations.

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter FullyQualifiedName~RendererPerfEntrypointRootTests --logger 'console;verbosity=normal'
```

Single affected selection, including the documented fixture failure.

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~RendererPerfEntrypointRootTests|FullyQualifiedName~RendererPerfEntrypointTests|FullyQualifiedName~RendererPerfProtocolTests.Rejects_invalid_or_reused_authorization_before_factory_or_clock|FullyQualifiedName~RendererPerfShaderIdentityTests|FullyQualifiedName~RendererPerfRecipeTests.Recipe_contains_no_consumer_asset' --logger 'console;verbosity=normal'
```

Isolated project build and final unfiltered project format verification.

```bash
dotnet build tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj -c Release
dotnet format KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj --verify-no-changes --no-restore --verbosity normal
```

The guard batch used `set -eu` and ran these commands sequentially under one build slot. Its final exit 0 establishes that each command returned 0. File-size output included only existing `Scene3D.cs` baseline slack, which was left untouched.

```bash
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
git diff --cached --check
```

The implementation commit used explicit paths and normal hooks through `build-slot`. The scratch `commit-fix.sh` first asserted physical path, top level, branch, starting HEAD and no unstaged tracked changes, then ran `git commit -m 'fix(renderer-perf): anchor pilot entrypoint to checkout root' --` with the five paths in the diff table.

## Log hashes and exits

Logs are under `/tmp/khaozengine-renderer-entry-root-fix/`. Hashes are SHA-256 of the complete log bytes, including the build-slot result line.

| Log | Exit | SHA-256 |
| --- | ---: | --- |
| `red-build.log` | 0 | `dda0a386cc268db5e147c817504bd851144c418fb5b872f2a75ff4f0b32f045c` |
| `red-tests.log` | 1 | `221be6e2e0c9b11fe7849a91af2dc55839a775326e50596085b58760cc9d8892` |
| `green-tests.log` (first affected selection) | 1 | `87bd2690ce83141ae5a06f1482b4e7b6d2cc4fb58903928cfa1d9de230db305e` |
| `verified-build.log` | 0 | `a3749e452393633c7bf62f6dc417c8d9bd921c7bb0e95ed49e1fc417362b9f7b` |
| `final-tests.log` | 0 | `0c14e3cd470e4b4c8e5c149ccdd34d93280467bbea3bc60214b2d12ed2e4eb6d` |
| `host-build.log` | 0 | `49a16c50db2c88a8291412cd2e449f01dd0396e014eacfaad9c9cd3fec2348ab` |
| `project-format.log` | 0 | `140f77201d20be606fb382a55dd87388a1b92702060c0313211263b9eb79dd68` |
| `guards.log` | 0 | `61dd30ed4fa718e72935321f548a16ff29c5dd1374384491bbee7536fad1a4b8` |
| `source-commit.log` | 0 | `cf608fe8c7e1252fa0dbd419fbddcd8040a2ec3ca42c704e027cef2343b04efc` |

## Consumed attempt and limits

The original failure remains [issue 1567](https://github.com/APKiwiOrg/KhaozEngine/issues/1567), [run 40133376096](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40133376096), GPU job `120989788923`. Workload A is `d092cc35e487ac5d684d09a44920ca3cd2a201f0`. Authorization B is `a58e0d4153030f02c4af68cbfc9ea7aece5e1939`. The original manifest hash is `0c1bd0a14eaa3c58959f10c8073e5f7bb998cf3434e8776c666661393541aada`.

Root's compact audit `/tmp/khaozengine-renderer-pilot-preparation/pilot-attempt1-audit.json` had SHA-256 `f7f0b0247b83999d38288a8c07ef65bf43955334def299a5df1e9ae5019e9fbc`. It records one failed fixed fact, zero skips, zero measured frames, matching authorization/manifest hashes, both builds with zero warnings and errors, observed testhost `10.0.13`, four billed GPU minutes and USD 0.208. It records no complete runtime environment, runtime-admission file, native admission or measurement report. No artifacts were downloaded by this task. Root alone owns the original-attempt verification document.

This local proof establishes checkout-root lookup and fail-closed admission behavior. It does not establish that the real approved manifest would pass runtime equality, native admission, compiler retention, timing or publication. Windows path normalization is implemented, but no Windows or native backend execution was performed. Root retains common finish verification after reconciling the parallel fixes. The single approved attempt is consumed. This change grants no retry authority.
