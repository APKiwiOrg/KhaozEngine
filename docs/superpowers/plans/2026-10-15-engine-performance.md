# Ambient scopes and navigation capture implementation plan

> For agentic workers: use subagent-driven-development with the owner's explicit two-lane execution contract.

**Goal:** Remove process-global test isolation requirements and reduce ground navigation capture cost without changing bake bytes.

**Architecture:** Add async-flow overrides over existing process defaults. Investigate one representative navigation capture before selecting an optimization.

**Tech Stack:** C#, .NET, xUnit, existing physics and navigation seams.

**Spec:** The owner-supplied execution contract and engine issues [1416](https://github.com/APKiwiOrg/KhaozEngine/issues/1416) and [1417](https://github.com/APKiwiOrg/KhaozEngine/issues/1417).

## Global constraints

- Workers commit only in their assigned worktree. The orchestrator owns reconciliation, versioning, pushing and packaging.
- Every local build, test, profile and bake uses build-slot. Full suites and bakes use its heavy lane.
- No repeated test runs, load benchmarks, synthetic load, reduced coverage or self-hosted engine CI.
- Run the full engine suite once at the combined finish. Use focused Release tests during implementation and integration.
- Preserve existing process setters and process-wide crash routing.
- No private consumer assets, content, paths or descriptions enter public artifacts.
- A bake byte or format change requires owner approval before implementation.
- No release tag until the owner explicitly approves one.

## Review focus

- Nested scopes restore after exceptions and awaits, while concurrent flows remain independent.
- Disposal in an inherited child execution context cannot invalidate the parent's scope.
- Explicit null overrides are distinguishable from no override where null is legal.
- Cached category loggers follow the current scope, while crash reporting and process mutation retain their process contract.
- Capture optimizations preserve deterministic bytes, ordering, numeric results and backend safety.

### Task 1: Scoped ambient APIs

**Files:** LocalizationContext.cs in App, Log.cs and CrashHandler.cs in Diagnostics, GuiTheme.cs in Gui, one shared internal scope primitive in Diagnostics, matching headless tests and living Markdown references.

**Interfaces:** Add IDisposable-returning LocalizationContext.Use(IStringCatalog? catalog), Log.Use(LogManager manager), and GuiTheme.Use(GuiTheme theme). Reject null log managers and themes. Scope lifetime borrows values and does not shut down managers.

**Theme consumers:** Controls such as Toggle snapshot default colors at construction and retain them after disposal. Live readers such as PatchNotesTheme and PanelFrame must be read or drawn inside the desired scope, even when constructed earlier. Preserve runtime retuning through the process default.

- [x] Add failing headless contracts for nested restore, await flow, concurrent isolation, exceptions, nullable localization, cached loggers, process setters during scopes and crash routing.
- [x] Implement one shared primitive using immutable scope nodes and flow-local stack state. Keep it internal through narrowly declared friend assemblies if needed. App and Gui already reference Diagnostics, so no new package dependency is required.
- [x] Gate async-local reads behind a monotonic first-use flag for each ambient slot. No scope means the existing process read plus a predictable flag branch, with no locks, allocations or execution-context lookup.
- [x] Add finite allocation assertions for LocalizedText.Resolve and cached ambient loggers before first use, inside scopes and after disposal. Prove the never-used state without relying on test order. Do not claim timing equivalence from allocation counts.
- [x] Run focused Release builds and tests synchronously. Preserve all existing test coverage.
- [x] Sweep all Markdown for the changed APIs and behavior, update consumer and package docs, run whole-tree guards, and commit explicit paths.

### Task 2: Navigation capture research

**Files:** The existing PhysicsNavBake.Capture implementation and its physics query call graph, related headless fixtures, a reusable opt-in measurement harness if necessary, and a sanitized measurement report.

- [ ] Read issue 1417 comments first, plus issues 1283, 1401 and 1301 and the linked consumer measurement background.
- [ ] Select one representative window and separate setup cost from capture. Record exact engine commit and configuration.
- [ ] Run one capture with wall time, process CPU delta and allocations. Use dotnet-trace if already installed, attached to that same run where practical. Record profiler overhead and machine contention caveats.
- [ ] Preserve a local output baseline and hash for the eventual same-input comparison. Public fixtures and reports contain no private world content or paths.
- [ ] Rank allocation removal, capture reuse, parallel cells and tiling against the measured profile. Give an exact implementation scope and expected byte-equivalence proof.
- [ ] Commit only reusable public harness or sanitized evidence. No optimization yet. Return commands, exit codes, times, test counts and blockers.

### Task 3: Evidence-led capture optimization

- [ ] The orchestrator writes the concrete implementation brief from Task 2's evidence before dispatch.
- [ ] Add an engine-fixture regression proving byte parity against the baseline algorithm, including edge cases affected by the chosen optimization.
- [ ] Implement only the measured optimization, retaining backend contracts and bake format.
- [ ] Run one matching after capture and focused Release tests, update living docs and commit explicit paths.

### Task 4: Review and integrate

- [ ] Dispatch one fresh Codex review for each lane's complete commit range. Verify every candidate finding against reachable code before assigning fixes.
- [ ] Independently inspect diffs and commit containment. Merge current main into each lane before integrating it.
- [ ] Re-read versions and tags, ride the staged version or select the next free version, and update changelog and guarded declarations together.
- [ ] Run focused integration tests, the combined Release build, the full engine suite once and all required whole-tree guards.
- [ ] Merge and push main, then run the guarded local-feed pack and check its output.
- [ ] Post sanitized before and after evidence on both issues, close fully resolved issues from their commits and report adoption APIs, pin and rebake requirements.
- [ ] Ask once whether to release the staged version. Do not tag before the reply.
