# Bug report revoke Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the bug report relay one operator operation that revokes a report from its reference code or id, removes every public and private trace, and redacts its issue.

**Architecture:** Three relay seams gain default-implemented members (media list and delete, delayed enqueue, issue update) that every engine backend overrides. An internal `ReportRevoker` owns resolution, the immediate cleanup and the delayed `PurgeRevoked` work. `BugReportRelay` exposes it typed and as a `RelayRequest`/`RelayResponse` pair so a host trigger stays as thin as the intake trigger.

**Tech Stack:** .NET 10, C#, xUnit, `System.Text.Json`, Azure.Storage.Blobs and Azure.Storage.Queues in the opt-in Azure package.

**Spec:** `docs/superpowers/specs/2026-10-19-bug-report-revoke-design.md`

**Worktree:** `/Users/antonio/KhaozEngine/.worktrees/bug-report-revoke` on `feature/bug-report-revoke`. Every command runs from there.

## Global Constraints

- Every local build and test runs through `build-slot --label <what> -- <command>`. The full solution test adds `--heavy`. Never loop tests.
- Focused test command: `build-slot --label revoke-<task> -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release --filter "FullyQualifiedName~<Class>"`. The Azure project is `KhaozEngine.BugReports.Relay.Azure.Tests/KhaozEngine.BugReports.Relay.Azure.Tests.csproj`.
- Zero warnings. `TreatWarningsAsErrors` is on.
- Release ships as additive minor `21.8.0`. New interface members are default-implemented and throw `NotSupportedException` naming the member.
- Pending and public paths are lowercase, `/` separated and must pass `ReportPaths.IsValid`. Every stored blob needs an extension `ReportLayout.HeadersFor` supports.
- Purge delay is `WorkClaim.LeaseMinutes` plus two minutes, so twelve minutes.
- Redacted issue title: `Revoked bug report <reference>`. Redacted issue body, exactly: `This report was revoked at the reporter's request. Its files and private record are being deleted.` Close with `state_reason` `not_planned`. Labels are kept.
- Revoke request body: a JSON object with one string property `report`, at most 1024 bytes.
- No em dashes, en dashes or prose semicolons in docs, comments or messages. Test namespaces are `KhaozEngine.Tests.BugReports.Relay` and `KhaozEngine.Tests.BugReports.Relay.Azure`.
- Commit subjects use `area(scope): summary`, scope `bug-reports`, each ending with a `Refs #1510` body line. The last task's commit uses `Closes #1510`.

## Spec amendments this plan carries

Task 7 writes these back into the spec.

1. The purge takes no work claim. Every purge step is idempotent, so concurrent deliveries are harmless and no purge claim files exist.
2. The issue number is the record's `IssueNumber`, or else the `CreateIssue` claim result marker. A worker that created the issue and crashed before saving the record leaves the number only in that marker.
3. The index entry is `references/<lowercase code>/<reportId>.json` holding `{}`, because paths are lowercase and need a supported extension.
4. The engine also exposes `BugReportRelay.RevokeRequestAsync(RelayRequest, CancellationToken)`, which parses the body and maps the status, so hosts reuse their intake request plumbing.

## Review Focus

1. An operator types the code in lower case or with surrounding spaces (` r-abc123 `). Expect it to resolve like `R-ABC123`. Test in Task 4.
2. An operator types Crockford confusables, `O` for `0`, or `I` or `L` for `1`. Expect normalisation to the canonical code. Test in Task 4.
3. An operator pastes the report id in upper case. Expect it to resolve. Test in Task 5.
4. The worker created the issue but crashed before saving the record. Expect the call and the purge to find the number in the claim marker and redact. Test in Task 6.
5. Two operators revoke the same report at once, so two purges run. Expect both to succeed, one tombstone, and the second purge to change nothing. Test in Task 6.

---

### Task 1: Media store list and delete

**Files:**
- Modify: `KhaozEngine.BugReports.Relay/IReportMediaStore.cs`, `KhaozEngine.BugReports.Relay/InMemoryReportMediaStore.cs`
- Modify: `KhaozEngine.BugReports.Relay.Azure/IReportBlobContainer.cs`, `BlobContainerAdapter.cs`, `BlobReportMediaStore.cs`
- Modify: `KhaozEngine.BugReports.Relay.Tests/Conformance/ReportMediaStoreConformance.cs`, `Support/WorkerMediaStore.cs`, `Support/HookedMediaStore.cs`
- Modify: `KhaozEngine.BugReports.Relay.Azure.Tests/InMemoryReportBlobContainer.cs`, `AzureLiveTests.cs` (live counterparts and `PrefixedMedia` forwarding)
- Test: `KhaozEngine.BugReports.Relay.Tests/InMemorySeamTests.cs` (default-member fact)

**Interfaces:**
- Produces: `Task<IReadOnlyList<string>> IReportMediaStore.ListAsync(ReportArea area, string prefix, CancellationToken cancellationToken)`. `prefix` must end in `/` and its text before the slash must pass `ReportPaths.IsValid`, otherwise `ArgumentException`. Answers every full path under the prefix at any depth, in ordinal order, empty when none.
- Produces: `Task<bool> IReportMediaStore.DeleteAsync(ReportArea area, string path, CancellationToken cancellationToken)`. True when a blob was deleted, false when absent.
- Produces (internal): `IReportBlobContainer.ListAsync(string prefix, CancellationToken)` and `DeleteAsync(string path, CancellationToken)`.

- [ ] **Step 1: Write the failing conformance facts** in `ReportMediaStoreConformance`:
  - `List_ReturnsNestedPathsUnderPrefixInOrdinalOrder`. Put `g/2026-10/a/x.json`, `g/2026-10/a/n/y.png`, `g/2026-10/b/z.json` in Public. `ListAsync(Public, "g/2026-10/a/")` equals `["g/2026-10/a/n/y.png", "g/2026-10/a/x.json"]`.
  - `List_IsScopedToArea`. A Pending put is absent from a Public list of the same prefix.
  - `List_Absent_ReturnsEmpty`.
  - `List_RejectsPrefixWithoutTrailingSlashOrInvalidPath`. `"g/2026-10/a"` and `"G/x/"` throw `ArgumentException`.
  - `Delete_RemovesBlobAndAnswersTrue`, then `ExistsAsync` is false.
  - `Delete_Absent_AnswersFalse`.
- [ ] **Step 2: Write the failing default-member fact** `MediaStoreWithoutListOrDelete_ThrowsNotSupportedNamingTheMember` in `InMemorySeamTests.cs`, over a test-local store implementing only the three existing members. Both new calls throw `NotSupportedException` whose message contains `ListAsync` or `DeleteAsync`.
- [ ] **Step 3: Add the live counterparts** `Live_Media_<FactName>` for every new fact to `AzureLiveTests` under `[BugReportsAzureFact]`, and make `PrefixedMedia` forward both calls through its prefix (strip the prefix from listed paths, record nothing for delete).
- [ ] **Step 4: Run** the Relay.Tests filter `ReportMediaStoreConformance|InMemorySeamTests` and the Azure.Tests filter `BlobReportMediaStoreConformanceTests|LiveConformanceCoverageTests`. Expected: FAIL to compile or FAIL on the new facts.
- [ ] **Step 5: Implement** the two default members, the in-memory store (filter keys by area and ordinal `StartsWith`), and the Azure adapter (`GetBlobsAsync(prefix: prefix)` enumerated to the end of every page, `DeleteIfExistsAsync` answering its `Value`). `BlobReportMediaStore` validates the same way the in-memory store does. `WorkerMediaStore` and `HookedMediaStore` forward both calls to `inner`. `InMemoryReportBlobContainer` implements both.
- [ ] **Step 6: Run** the same filters. Expected: PASS, live facts skipped without `KE_BUGREPORTS_AZURE`.
- [ ] **Step 7: Commit** `feat(bug-reports): list and delete report media`.

### Task 2: Delayed enqueue and the purge work kind

**Files:**
- Modify: `KhaozEngine.BugReports.Relay/IReportWorkQueue.cs`, `InMemoryReportWorkQueue.cs`, `WorkClaim.cs` (make `Prefix` internal static)
- Modify: `KhaozEngine.BugReports.Relay.Azure/IReportQueueSender.cs`, `QueueSenderAdapter.cs`, `StorageQueueReportWorkQueue.cs`
- Modify: `KhaozEngine.BugReports.Relay.Tests/Support/RelayHarness.cs` (queue built with `Clock`)
- Modify: `KhaozEngine.BugReports.Relay.Azure.Tests/RecordingQueueSender.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/InMemorySeamTests.cs`, `ReportWorkItemJsonTests.cs`, `KhaozEngine.BugReports.Relay.Azure.Tests/StorageQueueReportWorkQueueTests.cs`

**Interfaces:**
- Produces: `ReportWorkKind.PurgeRevoked`, serialized as `"purgeRevoked"`.
- Produces: `Task IReportWorkQueue.EnqueueAsync(ReportWorkItem item, TimeSpan visibleAfter, CancellationToken cancellationToken)`. `visibleAfter` outside zero to seven days throws `ArgumentOutOfRangeException`. Default throws `NotSupportedException`.
- Produces: `InMemoryReportWorkQueue(TimeProvider time)`. The parameterless constructor uses `TimeProvider.System`. `TryDequeue` takes the oldest item whose visible time has passed. `Count` still counts every waiting item.
- Produces (internal): `IReportQueueSender.SendAsync(string text, TimeSpan? visibilityTimeout, CancellationToken ct)`. Existing callers pass null.
- Produces (internal): `WorkClaim.Prefix(ReportWorkKind kind, string reportId)`, used by Task 6 to delete `issue` and `comment` claims.

- [ ] **Step 1: Write the failing facts.**
  - `InMemoryQueue_DelayedItem_IsHiddenUntilItsTime`. Enqueue `A` delayed twelve minutes and `B` immediate on a `ManualClock`. `TryDequeue` gives `B`, then false. Advance twelve minutes, then `A`.
  - `InMemoryQueue_RejectsNegativeOrOverSevenDayDelay`.
  - `WorkItemJson_PurgeRevoked_RoundTrips` with JSON text containing `"kind":"purgeRevoked"`.
  - `StorageQueue_DelayedEnqueue_SendsVisibilityTimeout`. `RecordingQueueSender` records `TimeSpan.FromMinutes(12)` and the immediate overload records null.
  - `QueueWithoutDelay_ThrowsNotSupportedNamingTheMember`.
- [ ] **Step 2: Run** the filters `InMemorySeamTests|ReportWorkItemJsonTests` and `StorageQueueReportWorkQueueTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** The Azure adapter passes `visibilityTimeout` to `QueueClient.SendMessageAsync`. `RelayHarness` builds `Work` in its constructor as `new InMemoryReportWorkQueue(Clock)`. `WorkerFixtures.DrainAsync` keeps its behaviour because it dequeues only visible items.
- [ ] **Step 4: Run** the same filters plus the whole Relay.Tests project. Expected: PASS.
- [ ] **Step 5: Commit** `feat(bug-reports): delay relay work and add the purge kind`.

### Task 3: GitHub issue update

**Files:**
- Modify: `KhaozEngine.BugReports.Relay/IGitHubIssueClient.cs`, `GitHubAppIssueClient.cs`
- Modify: `KhaozEngine.BugReports.Relay.Tests/Support/RecordingGitHubIssueClient.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/GitHubAppIssueClientTests.cs`

**Interfaces:**
- Produces: `public sealed record GitHubIssueUpdate(string Title, string Body)`. Documented as replacing the title and body and closing the issue as not planned.
- Produces: `public enum GitHubIssueUpdateOutcome { Updated, Gone, Moved }`.
- Produces: `public sealed record GitHubIssueUpdateResult(GitHubIssueUpdateOutcome Outcome, Uri? IssueUrl)`. `IssueUrl` is the issue's `https://github.com/<owner>/<repo>/issues/<n>` page for `Updated` and the new page for `Moved`, null for `Gone`.
- Produces: `Task<GitHubIssueUpdateResult> IGitHubIssueClient.UpdateIssueAsync(int issueNumber, GitHubIssueUpdate update, CancellationToken cancellationToken)`. Default throws `NotSupportedException`.
- Produces (test support): `RecordingGitHubIssueClient.Updates` as `List<(int IssueNumber, GitHubIssueUpdate Update)>`, `OnUpdate` as `Func<int, Task<GitHubIssueUpdateResult>>?` (null answers `Updated` with a URL built from the number).

- [ ] **Step 1: Write the failing facts** over `ScriptedHttpHandler`:
  - `UpdateIssue_SendsPatchWithTitleBodyAndNotPlannedClose`. Method PATCH, URI `https://api.github.com/repos/o/r/issues/12`, body JSON with exactly `title`, `body`, `state` `closed` and `state_reason` `not_planned`. Response 200 with `html_url` gives `Updated` and that URL.
  - `UpdateIssue_404Or410_AnswersGone` as a theory.
  - `UpdateIssue_301_AnswersMovedToTheNewIssuePage`. `Location: https://api.github.com/repos/o/other/issues/3` gives `https://github.com/o/other/issues/3`.
  - `UpdateIssue_RedirectedFinalRequest_AnswersMoved`. The scripted response's `RequestMessage.RequestUri` is a different issue's API URI, so the client reports `Moved` with that issue's page and does not classify the final status.
  - `UpdateIssue_500_ThrowsGitHubApiException`.
  - `IssueClientWithoutUpdate_ThrowsNotSupportedNamingTheMember`.
- [ ] **Step 2: Run** filter `GitHubAppIssueClientTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Give `CreateRequest` an `HttpMethod` parameter. `UpdateIssueAsync` checks for a moved issue (301 or a final request URI other than the one sent), then 404 or 410, before `GitHubResponseClassifier.Classify`. An unreadable `Location` or `html_url` throws `GitHubResponseClassifier.InvalidResponse`. The PATCH is sent once, like the existing POSTs.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(bug-reports): redact and close an issue through the GitHub App`.

### Task 4: Reference index

**Files:**
- Modify: `KhaozEngine.BugReports.Relay/ReportLayout.cs`, `RelayIntake.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/ReportLayoutTests.cs`, `RelayIntakeTests.cs`

**Interfaces:**
- Produces (internal): `static bool ReportLayout.TryNormalizeReference(string text, out string reference)`. Trims, upper-cases, maps `O` to `0` and `I` or `L` to `1`, and requires `R-` plus six characters of `ReportIds`' alphabet. `reference` is the canonical `R-XXXXXX`.
- Produces (internal): `static string ReportLayout.ReferenceIndex(string reference, string reportId)` gives `references/<lowercase reference>/<reportId>.json`, and `static string ReportLayout.ReferencePrefix(string reference)` gives `references/<lowercase reference>/`.

- [ ] **Step 1: Write the failing facts.**
  - `TryNormalizeReference_AcceptsCaseSpacesAndConfusables` as a theory over ` r-abc123 `, `R-ABCI23` (to `R-ABC123`) and `R-0O0000` (to `R-000000`).
  - `TryNormalizeReference_RoundTripsMintedCodes`. For 64 ids from `ReportIds.NewId()`, `ReportIds.ReferenceCode(id)` normalizes to itself. This pins the relay's copy of the alphabet to the minting one.
  - `TryNormalizeReference_RejectsWrongPrefixLengthOrU` over `ABC123`, `R-ABC12`, `R-ABC1234`, `R-ABCU23`.
  - `ReferenceIndex_IsLowercaseValidAndJson`. The path passes `ReportPaths.IsValid` and `HeadersFor` answers JSON.
  - `Intake_WritesReferenceIndexBeforeAcceptedIndex`. A `WorkerMediaStore.BeforeWrite` hook records write order. The index path precedes `accepted/<id>.json` and holds `{}`.
  - `Intake_LostAcceptanceRace_KeepsReferenceIndex`. The accepted index already exists from another holder. The index is still present after intake.
- [ ] **Step 2: Run** filter `ReportLayoutTests|RelayIntakeTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** `CompleteAcceptanceAsync` puts the index with `overwrite: true` before `TryCreateAcceptedAsync`.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(bug-reports): index accepted reports by reference code`.

### Task 5: The revoke call

**Files:**
- Create: `KhaozEngine.BugReports.Relay/ReportRevocationResult.cs` (public result types)
- Create: `KhaozEngine.BugReports.Relay/ReportRevoker.cs` (internal operation, shared with Task 6)
- Modify: `KhaozEngine.BugReports.Relay/BugReportRelay.cs`, `RelayResponses.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/ReportRevokeTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 4.
- Produces: `public enum ReportRevocationStatus { Revoked, AlreadyRevoked, NotFound, Ambiguous, Invalid }`.
- Produces: `public enum ReportIssueRevocation { NotCreated, Redacted, Gone, Moved, Failed }`.
- Produces: `public sealed record ReportRevocationResult(ReportRevocationStatus Status, string? ReportId, string? Reference, IReadOnlyList<string> CandidateIds, int DeletedPublicBlobs, ReportIssueRevocation? Issue, int? IssueNumber, Uri? IssueUrl, DateTimeOffset? PurgeAt)`.
- Produces: `Task<ReportRevocationResult> BugReportRelay.RevokeReportAsync(string idOrReference, CancellationToken cancellationToken)`.
- Produces: `Task<RelayResponse> BugReportRelay.RevokeRequestAsync(RelayRequest request, CancellationToken cancellationToken)`. 200 for `Revoked` and `AlreadyRevoked`, 404 `NotFound`, 409 `Ambiguous`, 400 `Invalid`, each with the result serialized through `RelayJson.Options`. A body over 1024 bytes answers 413 `too_large`. Malformed JSON, a non-object, a missing or non-string `report`, or any other property answers 400 `bad_request`.
- Produces (internal): `ReportRevoker(IReportMediaStore media, ReportRecordStore records, ReportRevocation revocation, IReportWorkQueue work, IGitHubIssueClient github, TimeProvider time, ILogger logger)`, building its own stateless `WorkClaim(media, time)`, with `RevokeAsync(string idOrReference, CancellationToken)` and `static TimeSpan PurgeDelay` of twelve minutes. Task 6 adds `PurgeAsync`.

- [ ] **Step 1: Write the failing facts** on `RelayHarness`, accepting a report through `WorkerFixtures.AcceptAsync` and creating its issue with `WorkerFixtures.ProcessAsync`:
  - `Revoke_ByReference_DeletesPublicFolderRedactsIssueAndSchedulesPurge`. Status `Revoked`, `DeletedPublicBlobs` equals the folder's blob count, Public has no path under the folder, the tombstone exists, `GitHub.Updates` holds one update with the exact title and body, `Issue` is `Redacted`, `IssueUrl` is set, and `PurgeAt` is the clock plus twelve minutes. The queue holds a `PurgeRevoked` item that `TryDequeue` refuses until twelve minutes pass.
  - `Revoke_ById_AndUppercaseId_Resolve`.
  - `Revoke_BeforeIssueCreation_AnswersNotCreated`. The folder comes from the accepted index when only it and the record exist.
  - `Revoke_Unknown_AnswersNotFound` for an unindexed code and for a valid but unknown id.
  - `Revoke_SharedCode_AnswersAmbiguousWithBothIds`. Seed a second index entry and record for an id with the same code. Nothing is written.
  - `Revoke_Garbage_AnswersInvalid` for `../x` and `R-??????`.
  - `Revoke_GitHubFails_StillRevokedWithFailed`. `OnUpdate` throws `GitHubApiException(500, ...)`. Tombstone written, folder empty, purge queued.
  - `Revoke_Repeat_BeforePurge_IsSafe`. A second call answers `Revoked`, deletes zero blobs and queues a second purge.
  - `RevokeRequest_MapsStatusesAndRejectsBadBodies`. Theory over the five statuses and over a 1025-byte body, invalid JSON, `{"report":1}`, `{"report":"x","y":1}`.
- [ ] **Step 2: Run** filter `ReportRevokeTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `ReportRevoker.RevokeAsync` in the spec's order (resolve, tombstone, delete the folder, redact, schedule, answer). Resolution discards index candidates with no accepted index, record or tombstone. `AlreadyRevoked` is a tombstone with neither record nor accepted index, and it schedules nothing. The issue number comes from the record, else `claims.FindResultAsync(CreateIssue, id)`. A thrown `GitHubApiException` or `HttpRequestException` gives `Failed`. `BugReportRelay` constructs the revoker beside the worker and documents that `RevokeAsync` is the tombstone-only primitive.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(bug-reports): revoke a report from its code or id`.

### Task 6: The purge

**Files:**
- Modify: `KhaozEngine.BugReports.Relay/ReportRevoker.cs`, `BugReportRelay.cs` (route `PurgeRevoked` in `ProcessWorkAsync`)
- Test: `KhaozEngine.BugReports.Relay.Tests/ReportPurgeTests.cs`

**Interfaces:**
- Consumes: `ReportRevoker` from Task 5, `WorkClaim.Prefix` from Task 2.
- Produces (internal): `Task ReportRevoker.PurgeAsync(string reportId, CancellationToken)`. `BugReportRelay.ProcessWorkAsync` sends `PurgeRevoked` items here and every other kind to `ReportWorker`.

- [ ] **Step 1: Write the failing facts.** Each revokes first, advances the clock twelve minutes and drains the queue:
  - `Purge_DeletesPrivateFilesAndKeepsTombstoneAndIndex`. After the purge, Pending holds exactly the tombstone and the reference index entry for the id.
  - `Purge_DeletesLatePublicWrite`. After the call, put `server.json` into the folder as a late holder would. The purge removes it.
  - `Purge_RedactsIssueCreatedInsideTheLease`. Revoke before issue creation, then save an issue number into the record as a late worker would. The purge redacts it and logs a warning naming the issue number and report id.
  - `Purge_FindsIssueNumberInClaimMarker`. Write a `CreateIssue` `.done` marker with `9` and no record issue number. The call and the purge both update issue 9.
  - `Purge_RedactionFails_ThrowsAndKeepsPrivateFiles`. A later delivery with GitHub healthy completes the purge.
  - `Purge_GoneOrMoved_Completes`.
  - `Purge_WithoutTombstone_ChangesNothing`. Enqueue `PurgeRevoked` for an unrevoked report. Its files and issue are untouched.
  - `ConcurrentRevokes_TwoPurges_AreSafe`. Two calls, two purges, one tombstone with the first time, and the second purge deletes nothing.
  - `Revoke_AfterPurge_AnswersAlreadyRevoked` by code and by id.
- [ ] **Step 2: Run** filter `ReportPurgeTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `PurgeAsync` in the spec's order. It reads the record directly through `ReportRecordStore`, never through the worker's `LoadAsync`, which hides revoked reports. It redacts before any private delete. It deletes `records/`, `accepted/`, `snapshots/` and every path under `WorkClaim.Prefix` for `CreateIssue` and `AppendServerSnapshot`. An `Updated` answer during the purge logs at warning level.
- [ ] **Step 4: Run** the filter and then the whole Relay.Tests project. Expected: PASS.
- [ ] **Step 5: Commit** `feat(bug-reports): purge a revoked report after the claim lease`.

### Task 7: Docs, version and full verification

**Files:**
- Modify: `KhaozEngine.BugReports.Relay/README.md` (operations table row and the "To revoke a report" steps)
- Modify: `docs/superpowers/specs/2026-10-17-bug-reports-design.md` (Operations table, Media access, Seams table)
- Modify: `docs/superpowers/specs/2026-10-19-bug-report-revoke-design.md` (the four amendments above, status line)
- Modify: `docs/DEPENDENCY-SEAMS.md` rows 108, 109 and 111
- Modify: `docs/INDEX.md` (revoke row status), `CHANGELOG.md`, `Directory.Build.props` and every declaration `scripts/check-doc-versions.sh` flags

- [ ] **Step 1: Update the docs.** The README revoke section becomes: call `RevokeReportAsync` or `RevokeRequestAsync`, delete the returned issue with an identity that may delete issues, nothing else is manual. It keeps the lease residual for a holder that outlives its lease.
- [ ] **Step 2: Bump** `KhaozEngineVersion` to `21.8.0` and add a `## 21.8.0` CHANGELOG entry dated the commit day. Its bullets name `RevokeReportAsync`, `RevokeRequestAsync`, the reference index, the purge and the three default-implemented seam members.
- [ ] **Step 3: Run the checks.** `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `bash scripts/check-doc-versions.sh`. Expected: each exits 0.
- [ ] **Step 4: Run the full verification once.** `build-slot --label revoke-build -- dotnet build KhaozEngine.slnx -c Release` then `build-slot --heavy --label revoke-test -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`. Expected: zero warnings, zero failures.
- [ ] **Step 5: Commit** `docs(bug-reports): document the operator revoke and stage 21.8.0` with body `Closes #1510`.

## After this plan

The controller reviews the branch, merges it into engine `main` and asks the owner to approve exactly `v21.8.0` at that commit. The release then goes through `release-khaozengine`. The game-template seed trigger, operator script and lifecycle rule, and the Grimhollow adoption that closes Grimhollow #588, get their own plan against the released API.
