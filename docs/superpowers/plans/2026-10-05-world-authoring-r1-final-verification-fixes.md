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


### Execution outcome, 2026-10-05

Worker returned DONE with four separate commits, now pushed:

- `545e9cb1c`, #1293 formatter-only repair. Controller compared all five files' non-whitespace
  text and inspected the diff, with no semantic change.
- `38055896f`, #1298 shared positive Int64 schema range and exact end-of-string handling.
  Schema rejects overflow and trailing newline like the existing runtime reader.
- `f977d4104`, #1303 rejected GUI gestures seal the merge barrier. The new headless proof keeps
  a later inspector move separate from an earlier accepted Add.
- `a5e16744fe937f93ee2c211f7f02d93534e4e528`, #1304 fixed historical format-3 golden input and
  current-format migration assertions. The original golden, production hashing, SchemeVersion and
  shared SampleDoc fixture are unchanged. The format version was the changed hash input.

Controller verified ancestry from e3d85b807, clean tree, changed-file size bounds, diffs and actual
slot/log exits. Combined MapEditor checks passed 456, with 6 GPU skips and 0 failures. MapDoc checks
passed 213. Current-main ClientPredictionTransitionTests passed 8. All observed compiler warning
counts were zero. Full solution build/suite and whole-branch review remain pending.

Live main remains de78df336 with SpaceGame 20.26.0 staged, latest observed tag v20.25.0. Both local
and remote v20.27.0 were free. R1's review candidate stages 20.27.0, preserving SpaceGame's separate
20.26.0 entry. Version, changelog and guarded declarations changed together. This is not a release
tag or permission to tag. Recheck refs again before final integration/release actions.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t1 | Observed exit | SHA-256 |
| --- | --- | --- |
| `01-1293-verify-red.log` | 2 | `41cdb9056beef759736a9221e559d65be6384d1bd97e6b33a93c204a45abf209` |
| `03-1293-verify-green.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `04-1298-red.log` | 1 | `519e62562bf843ab2e62b410ae61e8df42ed57eaf780c9bcfae860fb90bc4e51` |
| `05-1298-green.log` | 0 | `a721b735a46c30fa1a9fb60314f653789914707b0cb322a9fc2b31ae4b52717d` |
| `06-1298-runtime.log` | 0 | `be9af4606252f84f5ec17086ff77d3cfdfd2c3b6faa4d1294139f8364215540d` |
| `08-1303-red.log` | 1 | `eaba86a20d79841736411258cedef20539ac4f9e19b8ba7eb94b081c7f557576` |
| `09-1303-green.log` | 0 | `7c5ea5f54dc00207b5fb53da32e078c31032d729283a6d31c559fbf6661e5073` |
| `12-1304-repro.log` | 1 | `030418b2b7dddf14b9c5a356ac1898720059aeed7d76c1aa2be8b7e04686f87e` |
| `13-1304-green.log` | 0 | `097edbb038d0455a128359c59d94ecb214b92043fd8a77e9736258556b782799` |
| `15-combined.log` | 0 | `bb903ba0c7016f5305fe0c534262d0f82d9120ec2170288f08f45c178275390f` |
| `16-mapdoc-tests.log` | 0 | `29c06f2e16c7597119399b28a45b6e0ae78a6ed4b4104a5ba845f0810e5fad81` |
| `17-client-prediction.log` | 0 | `587c12a67cfc5e9c72ef0934ed6defe351fcb74613699fdfdeae78356229d25b` |
| `18-guards.log` | 0 | `9eafd63548925867ce3af53a91376b2b647c780afd0334674e92e7f3d77395cd` |
