# Ten bounded backlog fixes implementation plan

> For agentic workers, use subagent-driven-development. Each issue is a separate deliverable and review gate.

**Goal:** Implement ten unclaimed backlog items without duplicating the other active batch.

**Architecture:** Repair existing flows and their contracts. Keep each issue scoped, add headless regression coverage for behavior changes, and integrate one version and changelog update after all issue commits.

**Tech Stack:** C#, .NET 10, xUnit, Markdown.

**Spec:** The ten linked GitHub issues and their current comments are the requirements.

## Global Constraints

- Use the batch worktree on fix/backlog-ten-headless. The dispatch supplies its absolute path. Verify the root and branch before writing.
- Follow AGENTS.md and docs/CONTRIBUTOR-RULES.md. Preserve unrelated edits. Stage and commit explicit paths.
- Root owns integration, version files, CHANGELOG.md, publishing, pushes and issue closure. Workers do none of these.
- Workers do not spawn agents. One implementation worker at a time. Read-only review may overlap preparation.
- Local build and test commands need a root-issued turn. No concurrent builds, repeated test loops, CPU load or stress runs.
- Confirm regression failure before behavior changes, run focused Release verification after them, and report exact exit codes.
- New behavior gets matching area tests. Process-global state uses DisableParallelization = true collections.
- No baseline growth, warning suppression, dependency additions, raw input access or localization bypass.
- Sweep Markdown for changed names and old behavior. Run whole-tree dash, prose, file-size, instruction and doc-version guards.
- No em or en dash glyphs, middle-dot separators or prose semicolons. No attribution in commits.
- Package-bearing finish gets one batch version, full Release build and suite once at finish, merge current main into this branch, verify and push main, then guarded local-feed pack. No release tag.

## Review Focus

- Invalid durable bytes stay preserved and quarantined rather than silently narrowed.
- Live slots retain strict payload validation when quarantine wrappers are exempted.
- Fragment boundary values honor the existing payload cap and round trip.
- Spatial walks retain plane filtering and nearest-hit selection while eliminating allocations.
- Documentation claims follow shipped code and preserve other batch ownership.

### Task 1: Catalog schema parity

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/1198

**Files:** KhaozEngine.Catalog.Authoring/InMemoryContentAuthoringStore.cs and KhaozEngine.Catalog.Tests/Authoring/InMemoryContentAuthoringStoreTests.cs.

**Requirements:** Report schema version 3 from the in-memory store, matching both providers. Document the build target consumers should read. Preserve the existing public constant.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter FullyQualifiedName~InMemoryContentAuthoringStoreTests`.
- [ ] Commit explicit owned paths with `Closes #1198` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 2: Allocation-free object picking

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/1183

**Files:** KhaozEngine.TileWorld/TileWorldDocument.cs, its object-query partials, TileObjectRaycast and matching area tests.

**Requirements:** Replace the iterator on the picking hot path with a non-allocating region walk. Preserve rectangle, plane, ray ordering and nearest-hit behavior. Pin allocations through the camera probe and retain ordinary picking tests.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `Locate the matching TileWorldCameraProbe and TileObjectRaycast test projects, then run their focused Release classes`.
- [ ] Commit explicit owned paths with `Closes #1183` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 3: Skinned grounding documentation and refusal coverage

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/1201

**Files:** KhaozEngine.Render3D/Animation/SkinnedGrounding.cs and KhaozEngine.Render.Tests/Render3D/Animation/ClipRefusalsTests.cs.

**Requirements:** State the existing unweighted threshold of 1e-8. Add the missing moved-joint refusal test with ParamName moved, alongside the turned-joint test. No rendering behavior change.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~ClipRefusalsTests`.
- [ ] Commit explicit owned paths with `Closes #1201` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 4: Catalog key validation and retired family bundles

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/914

**Files:** KhaozEngine.Catalog/ContentKey.cs, Validation/ContentKeyChecks.cs, KhaozEngine.Catalog.Authoring/Publish/ContentForkChecks.cs, InMemoryContentAuthoringStore family and bundle partials, matching Catalog tests.

**Requirements:** Share the actual key-shape rule between validator and fork preflight. Preserve retired family state through import, read and export. Test invalid keys and retired family round trips. Leave CreateFamilyAsync transaction changes to the other batch issue #940.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release --filter "FullyQualifiedName~BundleTests|FullyQualifiedName~Fork|FullyQualifiedName~Key"`.
- [ ] Commit explicit owned paths with `Closes #914` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 5: Truthful rollback blocker kinds

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/955

**Files:** KhaozEngine.Catalog.Authoring/ContentRollback.cs, KhaozEngine.Server.Admin/Catalog/CatalogVersionActions.cs and related payload types and tests.

**Requirements:** Carry nullable rule kind on a rollback blocker and render the actual kind. A baseline with a retired row and no matching rule must yield a null kind, not a fabricated Retired rule. Keep existing constructor callers compatible.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `Run focused Release rollback and admin catalog action tests`.
- [ ] Commit explicit owned paths with `Closes #955` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 6: Quarantine plain stacks safely

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/935

**Files:** KhaozEngine.Items/ItemContainer.Slots.cs, KhaozEngine.ItemInstances.Journal/ContainerLoad*.cs, page codecs as needed and matching Items and Server tests.

**Requirements:** Allow a verified quarantine wrapper for an instance-id-zero stack while retaining the instance-id requirement for live payloads. Load and page decoding must carry the quarantine flag, preserve original bytes and keep the page clean. A malformed payload without instance id must be quarantined at entry level when its shape can be preserved. Pin normal payload refusals and unusable quarantined slots.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `Run focused Release Items slot tests and Server ContainerLoadRescueTests`.
- [ ] Commit explicit owned paths with `Closes #935` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 7: Enforce instance scalar contracts

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/917

**Files:** KhaozEngine.ItemInstances scalar codecs and registry plus identification codec, matching ItemInstances and Server load tests.

**Requirements:** Validate v1 scalar widths, reject BoundTo subject zero and reserved Flags bits, and cap the identification revealed mask at uint.MaxValue. Keep the generic Varint shape at 64 bits. Use per-kind codecs, preserving invalid bytes through quarantine. Do not edit sections 3.8 or Varint semantics owned by other batch issues #903 and #905.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release with focused scalar, registry and identification filters`.
- [ ] Commit explicit owned paths with `Closes #917` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 8: Use the game payload budget for fragments

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/923

**Files:** KhaozEngine.TileWorld.Netcode/TileFragmentedMessage.cs, matching fragment tests and item-instance design section 7.5.

**Requirements:** Keep MaxGameMessageBytes as the payload cap. Derive a fragment chunk as payload cap minus the five-byte fragment header, giving 1019 bytes. Enforce full encoded game payload bounds, cover boundary and reassembly behavior, and correct the conservative arithmetic in section 7.5.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TileFragmentedMessageTests`.
- [ ] Commit explicit owned paths with `Closes #923` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 9: Document deterministic random draw consumption

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/995

**Files:** docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md sections 14.1 and 14.2, docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md sections 9.3 and 9.4.

**Requirements:** Read the shipped random implementations and generator. Document the fifth Skip member, its default and two overrides, and collapsed bounds on both real and discarded draws. Match the code without changing random behavior. Other batch #996 owns the Scope B validator prose.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `Run whole-tree documentation guards, no new tests for prose`.
- [ ] Commit explicit owned paths with `Closes #995` in the body and write the report with root cause, paths, commands, exit codes and concerns.

### Task 10: Document registered identification mask bits

**Issue:** https://github.com/APKiwiOrg/KhaozEngine/issues/982

**Files:** docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md section 12.7 and related living documentation.

**Requirements:** State that Identify sets state 1 and ORs registered gated bits. Reserved unassigned bits remain zero. Preserve the shipped implementation and existing tests. Other batch #925 owns section 12.5 visibility semantics.

**Interfaces:** Preserve existing callers and wire layouts except the explicitly stated fragment payload capacity.

- [ ] Read the issue body and comments, locate current production code and tests, and confirm the gap remains.
- [ ] Add a regression test and observe the expected failure before changing behavior. Human prose needs no artificial test.
- [ ] Implement only the required existing-flow correction and sweep relevant Markdown.
- [ ] Verify synchronously in Release after receiving the root validation turn. Command: `Run documentation guards and inspect the existing Identify regression coverage`.
- [ ] Commit explicit owned paths with `Closes #982` in the body and write the report with root cause, paths, commands, exit codes and concerns.

