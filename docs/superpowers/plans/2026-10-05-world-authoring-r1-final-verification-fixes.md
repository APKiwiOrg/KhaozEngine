# R1 final verification fixes

This is the bounded finishing work required by the approved R1 plan and recorded review dispositions.
It does not start another round or add a capability. All six implementation tasks are accepted.
Worktree /Users/antonio/KhaozEngine/.worktrees/wa-r1-document-identity, branch
feature/wa-r1-document-identity. Reconciled main de78df336571d9a5db97b7b9b4f07c842f11556b was
merged without conflicts at be72f8405b45866042255ef3878f2b3fcd18394a. Root supplies the exact
post-planning BASE at dispatch. Version 20.26.0 belongs to SpaceGame's prediction reset change, not
R1 or Grimhollow 0.11.0. Do not pack, refresh a shared feed, change versions or tag.

### Task 1: Resolve the four recorded R1 verification items

Use one worker, sequential issue fixes, distinct commits and a proof section per issue. Guard pwd,
branch, HEAD and clean status before edits. Preserve unrelated work. Current Gauge routing overrides
historical model notes. No helpers, other worktrees, pushes, main changes, tags, pack or release work.
No game changes. Report to the controller, who owns fresh whole-branch review and integration.

**Files**

- Formatting only for #1293: KhaozEngine.MapDoc/MapDocumentSource.cs, MapResidencyConfig.cs,
  MapResidencyGate.cs, MapShapeGeometry.cs and MapTileResidency.cs.
- #1298: KhaozEngine.MapDoc/mapdoc.schema.json and new
  KhaozEngine.MapEditor.Tests/MapDoc/NativeNumericSchemaTests.cs. Reuse actual JsonSchemaValidator
  from Content, already referenced by MapEditor.Tests. Existing NativeNumericIdTests in MapDoc.Tests
  are runtime regression coverage. No production dependency addition is needed.
- #1303: KhaozEngine.MapEditor/EditorTool.Gestures.cs and
  KhaozEngine.MapEditor.Tests/MapEditor/MapEditorSceneTests.NativeRejections.cs. Remove only the
  recorded extra blank lines in EditorTool.Placement.cs and MapEditorScene.EditActions.cs.
- #1304: KhaozEngine.MapEditor.Tests/MapDoc/MapDocumentHashTests.cs and MapTiledFormatTests.cs.
  Do not change the shared TiledDocFixture globally. Production hashing is outside scope unless
  investigation proves a defect and the controller settles the smallest correction first.

**Step 1, format baseline**

- [ ] Inspect the five #1293 paths and confirm only known formatter debt is being corrected.
- [ ] Through the shared slot, run dotnet format whitespace for MapDoc with those five --include
  paths, then verify the same paths. No format-only test is needed.
- [ ] Inspect the diff for unchanged C# tokens and commit separately with an issue reference.

**Step 2, canonical Int64 schema endpoint**

- [ ] Add failing schema/runtime parity assertions for both canonical-string fields at
  9223372036854775807, 9223372036854775808 and 9999999999999999999. Also test shorter valid values,
  high-water zero, placement zero refusal, allowed placement null and malformed canonical syntax.
  Ensure the complete fixture is otherwise valid. Include a trailing newline to check whole-string
  matching, not only digit prefixes.
- [ ] Run the focused NativeNumericSchemaTests red test through the slot.
- [ ] Correct the schema using constraints supported by the existing validator. Preserve exact string
  transport, ASCII syntax, leading-zero refusal and optional/null distinctions. Do not weaken runtime
  validation, add a global converter or use description text as enforcement.
- [ ] Run the focused schema tests green and existing NativeNumericIdTests, then commit with #1298.

**Step 3, rejected GUI gesture boundary**

- [ ] Add a headless regression to the existing native rejection tests. Accept an Add or Move,
  reject a later drag frame, restore valid input, then edit the same placement through the inspector.
  The later edit must occupy its own undo step. Assert one Undo preserves the earlier accepted work.
- [ ] Run the focused case red through the slot.
- [ ] Seal the merge barrier when CancelRejectedGesture ends the rejected UI gesture. Preserve
  stacks/labels, dirty state, allocator, events and document bytes at rejection. Do not change direct
  EditorHistory rejected-command semantics. Remove the two recorded blank-line nits only.
- [ ] Run native rejection/history and affected editor-tool tests green, then commit with #1303.

**Step 4, migration and historical hash proof**

- [ ] Reproduce the #1304 trio on this reconciled source through a focused slot run.
- [ ] Establish the cause before changing expectations. OfManifest hashes doc.FormatVersion, and
  both full and compact writers ignore null fields. Pure OfWorld/MapSpatialIndex do not require the
  current format. A fixed historical format-3 fixture may preserve the original scheme-1 golden.
- [ ] Test that hypothesis against the original full digest, including Swedish culture. Do not alter
  the global SampleDoc fixture. Separate the historical canonicalization oracle from the test that
  verifies v2 loads at CurrentFormatVersion with the default tile size.
- [ ] If the historical input still changes hash, stop that item and report the exact canonical-byte
  cause. Do not blindly replace the digest, change production hashing or bump SchemeVersion. Other
  independent items can finish while this one is investigated.
- [ ] Once the hypothesis is proven, give the migration test a current-format name/assertion and
  preserve meaningful format/input identity coverage. Run the hash and migration tests green and
  commit with #1304. Record why no hash-scheme change was necessary, or the controller ruling if one is.

**Step 5, affected checks and handoff**

- [ ] Run the combined MapEditor native/MapDoc/MapEditTool regression selection. All three #1304
  failures must now pass. Run MapDoc.Tests and the newly merged ClientPredictionTransitionTests
  once as focused reconciliation coverage. No full suite yet.
- [ ] Run changed-file formatting and repository guards, including the whole-tree size check,
  serially through the shared slot. Keep 0 warnings, nonempty matching tests and actual exit codes.
- [ ] Inspect clean status and commits. Write the finishing report, one issue/proof section each.

Every dotnet target and repository verification command uses /tmp/grimhollow-orch/slot-run.sh with a
unique label/log. Use a task-specific log directory under /tmp/grimhollow-orch/logs. No loops, load,
stress or live client. Exit 75 means no target ran, report slot contention without claiming a test
result. Use only the established bounded slot-retry procedure.

Focused targets (fill the unique label and log path at dispatch)

```bash
dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter FullyQualifiedName~NativeNumericSchemaTests
dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter FullyQualifiedName~NativeNumericIdTests
dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter 'FullyQualifiedName~NativeRejection_|FullyQualifiedName~NativePlacementHistoryTests|FullyQualifiedName~EditorToolTests'
dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter 'FullyQualifiedName~MapDocumentHashTests|FullyQualifiedName~MapTiledFormatTests'
dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter 'FullyQualifiedName~Native|FullyQualifiedName~MapDoc|FullyQualifiedName~MapEditTool'
dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ClientPredictionTransitionTests
```

Do not re-run a check merely for repetition. Broaden/repeat only for a new change, failure or unresolved
concern. The controller runs the full R1 build/suite after whole-branch review and final reconciliation.

**Remaining scope and report**

#1302 is R9 edit-cost work. #1305 stays an explicit alias/filesystem-case lead for whole-round risk
disposition. Do not add alias infrastructure or claim physical-file protection. The OA9 compatibility
audit remains with the controller. No version change or tag is authorized by this task.

Report DONE, DONE_WITH_CONCERNS or a concrete blocker, exact commits, issue dispositions, changed
contracts, commands/exit codes/counts/logs, guard results and limits. Do not close issues yourself.
Do not modify plan/spec/program/ledger files. Root records proofs, pushes verified commits, closes
resolved issues when integrated, and obtains a fresh whole-branch review of all R1 work.

## Outcome

Prepared after Task 6 acceptance. No finishing fixes or tests have run yet. Parent main was merged
without conflict, its changed runtime source still needs the named focused proof and full checks.
