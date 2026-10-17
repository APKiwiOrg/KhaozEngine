# In-game bug reports E3 relay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the host-agnostic bug report relay (`KhaozEngine.BugReports.Relay`) that turns an authenticated client bundle and an optional server snapshot into a GitHub issue through a durable work queue, plus its opt-in Azure Storage backend (`KhaozEngine.BugReports.Relay.Azure`), then stage the documentation and the engine minor release for the whole E1 to E3 round.

**Architecture:** `BugReportRelay` is a thin facade over three internal operations (`RelayIntake`, `ServerSnapshotIntake`, `ReportWorker`). Each takes a body `Stream`, headers and a cancellation token and returns a `RelayResponse`, so a host adapts it in a few lines. Storage, queue, quota and GitHub sit behind seams with in-memory implementations in the core. The Azure package implements three of them over caller-built SDK clients, with the SDK confined to one internal adapter per service on the `KhaozEngine.Catalog.AzureBlob` pattern.

**Tech Stack:** C# on net10.0, xunit, `System.IO.Compression`, `System.Security.Cryptography` (RSA, SHA-256, `CryptographicOperations.FixedTimeEquals`, `RandomNumberGenerator`), `System.Text.Json`, `KhaozEngine.Http` (`HttpRetry`), `KhaozEngine.Diagnostics` (`ILogger`), E2's `KhaozEngine.BugReports.Contracts` and `KhaozEngine.BugReports`, and in the backend only `Azure.Storage.Blobs`, `Azure.Storage.Queues`, `Azure.Data.Tables`.

**Spec:** `docs/superpowers/specs/2026-10-17-bug-reports-design.md` (sections Relay, Security, Testing, Documentation and release). Binding contract: `docs/superpowers/plans/2026-10-17-bug-reports-contract.md` (E3 produces, D3, D6, D7, D8).

## Global Constraints

- Prerequisite: E1's media records and E2's Contracts and core are merged into the execution branch. Start every task with `git grep -l "class BundleValidator" && git grep -l "class BugReportViewerPage"` from the worktree root and stop if either prints nothing. The orchestrator names the worktree and branch.
- Every local build or test runs through `build-slot --label e3-<task> -- <command>`. Full-solution builds and suites add `--heavy`. Exit 75 means the slot is busy, so retry later. Never loop tests. Run `mkdir -p local-feed` once before the first build.
- Warnings are errors. Public types and members get XML doc comments. No blanket suppressions.
- KESIZE: a new file stays under 800 lines. New behaviour goes in a new type. `BugReportRelay` stays a facade.
- No em or en dashes and no prose semicolons in Markdown, comments or shipped text.
- `KhaozEngine.BugReports.Relay` has no ASP.NET Core, no Azure Functions and no third-party package. Its project references are exactly `BugReports`, `BugReports.Contracts`, `Diagnostics` and `Http`. (The spec table lists `Serialization`. Nothing here uses it, so it is not referenced. `Diagnostics` is the logger. `BugReports` is the viewer home per D3.)
- `KhaozEngine.BugReports.Relay.Azure` references exactly `BugReports.Relay` plus the three Azure SDK packages. Never `Azure.Identity` (D7). Its namespace contains an `Azure` segment, so every `using Azure...` directive sits above the file-scoped namespace and code never writes an `Azure.`-qualified name inside it (use `global::Azure` if one is unavoidable).
- Both packages are opt-in, in no umbrella, listed in `OptInBackends` in `KhaozEngine.Tests/ArchitectureTests.cs:35` and pinned by exact-reference facts in a new `KhaozEngine.Tests/ArchitectureTests.BugReportsRelay.cs`.
- Package csproj shape copies `KhaozEngine.Catalog.AzureBlob/KhaozEngine.Catalog.AzureBlob.csproj` (`PackageId`, `Version` = `$(KhaozEngineVersion)`, `Description`, `PackageReadmeFile`, packed `README.md`, `InternalsVisibleTo` the test project). Test csproj shape copies `KhaozEngine.Catalog.AzureBlob.Tests/KhaozEngine.Catalog.AzureBlob.Tests.csproj` (`IsPackable` false, `RootNamespace` `KhaozEngine.Tests`). Test namespaces are `KhaozEngine.Tests.BugReports.Relay` and `KhaozEngine.Tests.BugReports.Relay.Azure`.
- Every new project goes into `KhaozEngine.slnx`. Every packable project gets a root `README.md` catalog row (`| **<PackageId>** | ... | ... |`) in the task that creates it, or `scripts/check-doc-versions.sh` fails.
- Spec values: 5 reports per hour and 20 per day per account, 100 per day globally, 16 MB per bundle, 128-bit path token, JWT issued 60 s in the past and valid 9 minutes, title text at most 70 characters, GitHub body limit 65,536 characters, poison after 5 dequeues (host owned, D6).
- Relay status codes (contract): 202 accepted, 400 bad request, 401 no or bad token, 403 blocked subject or bad service key, 413 over the size cap, 422 bundle validation failed, 429 over quota with `Retry-After` in whole seconds.
- Logs never carry a bearer token, the service key, an installation token, the JWT or a subject. They carry the report id and the reference.
- Commit subjects use `area(scope): summary` with area `feat`, `test` or `docs` and scope `bugreports-relay`. The release commit is `release(<version>): stage in-game bug reports`. Never tag. Never push unless the orchestrator says so.

## Decisions

| Topic | Choice | Reason |
| --- | --- | --- |
| `IReportMediaStore` | `PutAsync(area, path, bytes, headers, overwrite)` answering whether it wrote, `GetAsync(area, path, maxBytes)` answering null only for absent, `ExistsAsync(area, path)` | Create-if-absent is the one primitive idempotency needs. A read fault must throw, not read as absent, or a worker would drop a real report |
| `IReportQuotaStore` | `ReadAsync(key)` returning `QuotaCounter(Count, Version)` or null, `TryWriteAsync(key, count, expectedVersion, expiresAt)` answering false on a version conflict, where a null version means create-only | The smallest optimistic-concurrency surface that maps onto Table ETags and a dictionary alike |
| `IGitHubIssueClient` | `CreateIssueAsync(GitHubIssueDraft)` returning the issue number, `AddCommentAsync(issueNumber, body)`. Failures throw `GitHubApiException` | The worker needs only these two calls and one failure shape that means redeliver |
| Public layout | Public area path `<gameId>/<yyyy-mm>/<reportId>-<token>/<entry>`. The host maps the Public area to a container named `reports`, so the URL reads `reports/<game>/<yyyy-mm>/...` as the spec says. `token` is 32 lowercase hex characters from `RandomNumberGenerator`. `yyyy-mm` is the relay's received time in UTC | Capability folder per spec with the month from a clock the client cannot set |
| Folder files | every bundle entry under its own name, `report.json`, `viewer.html`, and `server.json` once a snapshot merges | The viewer renders media by relative path, so it sits at the folder root |
| Pending area | `accepted/<reportId>.json` (idempotency index: folder, subject, received time) and `snapshots/<reportId>.json` (raw snapshot body) | A report id maps to its unguessable folder without exposing the token. The host's 1-day pending lifecycle rule must cover `snapshots/` only (see report risks) |
| Content types | `.json` `application/json; charset=utf-8` with `no-cache`, `.html` `text/html; charset=utf-8` with `no-cache`, `.png` `image/png`, `.jpg` `image/jpeg`, `.mp4` `video/mp4`, the media three with `public, max-age=86400` | Every blob gets an explicit content type. Validation guarantees image bytes match their type, and the only HTML served is the engine's viewer page. `report.json`, `viewer.html` and `server.json` change after first write |
| `report.json` | `ReportRecord` below. Identity comes from the authenticator, `receivedAt` from the relay clock, `issueNumber` starts null and is the worker's idempotency key, `serverSnapshot` is `none`, `inBody` or `commented` | One record answers every worker redelivery question |
| Quota | Three fixed UTC windows keyed `ah-<h>-<yyyyMMddHH>`, `ad-<h>-<yyyyMMdd>`, `gd-<yyyyMMdd>`, where `<h>` is the first 32 lowercase hex characters of SHA-256 of the UTF-8 subject. Check all three first, then increment in order with up to 5 optimistic attempts each. Conflict exhaustion answers 429 with `Retry-After: 5` | Checking first stops a full day window from burning the hour window. Hashing keeps subjects out of table keys and away from Table key character rules. Exhaustion fails closed with a short retry, and the client already queues a 429 |
| Intake order | auth header and authenticator, blocked subjects, quota, streamed body under the cap, `BundleValidator` plus id and game checks, idempotency index, store, create index, enqueue, 202 | Spec order, with the first three steps never touching the body |
| Idempotent hit | 202 with the same reference, nothing stored, and `CreateIssue` re-enqueued only when `report.json` has no issue number yet | A crash between index creation and enqueue would otherwise strand a report forever |
| JWT | RS256 PKCS#1 v1.5 over `{"alg":"RS256","typ":"JWT"}` and `{"iat":<now-60>,"exp":<now+540>,"iss":"<appId>"}`, base64url without padding, key from PEM through `RSA.ImportFromPem` (PKCS#1 or PKCS#8) | GitHub's documented shape. `exp` is 9 minutes after issue time as seen by the relay, which keeps `exp - iat` at the 10 minute maximum |
| Installation token | `POST /app/installations/{id}/access_tokens` through `HttpRetry`, cached until 5 minutes before `expires_at`, single flight behind a `SemaphoreSlim`, dropped on any 401 | One exchange per hour in practice |
| GitHub failures | 403 or 429 with `Retry-After`, or with `x-ratelimit-remaining: 0`, or 429 alone, throw `GitHubApiException` with `IsRateLimited` true. Any other non-success throws it with `IsRateLimited` false. Issue and comment POSTs are sent once with no `HttpRetry` | A thrown work item is redelivered by the host (D6). Retrying a POST in-process could create a duplicate issue |
| Neutralising | See Task 4. Every `@` gains U+200B after it, `#` before a digit gains U+200B, `gh-` before a digit becomes `gh` U+200B `-`, `github.com` becomes `github` U+200B `.com`, and `&`, `<`, `>` become entities | Breaks mentions, issue references and URL backlinks without hiding text |
| Title | `[<GameDisplayName> <version>] <first non-empty line>`, the line trimmed, at most 70 text elements, cut to 67 plus `...` when longer, `version` from section `build` property `version` or `unknown` | Spec rule, counted by text element so an emoji is never split |
| Body bound | `IssueLimits.MaxBodyChars` 60,000. Over it, the Files list collapses to the viewer link, then the log block is replaced by a pointer to the viewer | Leaves 5,536 characters of headroom under GitHub's limit |
| Service key | Constructor refuses a `ServiceKey` under 32 characters. Comparison is `CryptographicOperations.FixedTimeEquals` over the SHA-256 of both UTF-8 strings | Hashing first equalises length, so neither length nor content leaks through timing |
| Snapshot subject | Ordinal equality between the snapshot's `Subject` and the index's or record's reporter subject. A mismatch is dropped with a warning that names only the report id, and the server still gets 202 | Spec rule. The answer does not tell a caller which subject owns a report |
| Late snapshot | Snapshot intake enqueues `AppendServerSnapshot` whenever the report is already accepted. The worker throws `ReportNotReadyException` while the issue number is unset, no-ops when the snapshot is already `inBody` or `commented`, otherwise posts one comment | Closes the race between snapshot arrival and issue creation |
| Wire JSON | Internal `RelayJson.Options`: `JsonSerializerDefaults.Web` plus `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` | Must equal E2's wire options. See contract proposal 1 |
| Logging | `BugReportRelayOptions.Logger` (`KhaozEngine.Diagnostics.ILogger?`), default `Log.Get("BugReports.Relay")` | Engine logger, bridged by the host |

## Review Focus

1. A client whose 202 was lost resends the same bundle days later from its offline queue. Expect the same reference and no second issue. Tests: Task 5 `Intake_ResendThreeDaysLater_SameReference_NothingNewStored`, Task 7 `EndToEnd_ResendAfterIssueCreated_NoSecondIssue`.
2. The server snapshot lands while the worker is between loading the report and recording the issue number. Expect the snapshot to appear exactly once, in the body or in one comment. Test: Task 7 `SnapshotRace_ArrivesDuringIssueCreation_EndsInExactlyOneComment`.
3. A description containing `</details>`, `|`, triple backticks, `<img>`, a leading `>` and `@here`. Expect it rendered as quoted text with nothing breaking out of the blockquote. Test: Task 4 `ComposeIssue_HostileDescription_MatchesGolden`.
4. A first line whose 68th text element is an emoji or a letter with a combining mark. Expect no split surrogate or orphaned mark. Test: Task 4 `Title_EmojiAndCombiningMarksAtBoundary_NotSplit`.
5. A report at 23:59:59.9 UTC on the last day of a month, received through a non-UTC offset. Expect the old day window, the old month folder and a `Retry-After` of at least 1. Tests: Task 2 `Consume_LastInstantOfDay_CountsInOldDay_RetryAfterAtLeastOneSecond`, Task 3 `NewFolder_LastInstantOfMonthWithOffset_UsesUtcMonth`.

---

### Task 1: Relay package, seams and in-memory implementations

**Files:**
- Create: `KhaozEngine.BugReports.Relay/KhaozEngine.BugReports.Relay.csproj`, `KhaozEngine.BugReports.Relay/README.md` (opening paragraph only, Task 9 completes it)
- Create under `KhaozEngine.BugReports.Relay/`: `RelayRequest.cs`, `IReporterAuthenticator.cs`, `IReportMediaStore.cs`, `IReportWorkQueue.cs`, `ReportWorkItemJson.cs`, `IReportQuotaStore.cs`, `IGitHubIssueClient.cs`, `GitHubAppCredentials.cs`, `BugReportRelayOptions.cs`, `IssueFact.cs`, `RelayJson.cs`, `ReportPaths.cs`, `InMemoryReportMediaStore.cs`, `InMemoryReportWorkQueue.cs`, `InMemoryReportQuotaStore.cs`
- Create: `KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj`, `Conformance/ReportMediaStoreConformance.cs`, `Conformance/ReportQuotaStoreConformance.cs`, `InMemorySeamTests.cs`, `ReportWorkItemJsonTests.cs`, `Support/ManualClock.cs`
- Create: `KhaozEngine.Tests/ArchitectureTests.BugReportsRelay.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs:35` (`OptInBackends` gains `"BugReports.Relay"` with a one-line reason), `KhaozEngine.slnx`, `README.md` (catalog row after the `KhaozEngine.Http` row at line 103, layout line near line 303)

**Interfaces:**
- Consumes: `ReporterIdentity`, `BugReportLimits` (Contracts).
- Produces (namespace `KhaozEngine.BugReports.Relay`). Contract types exactly as the contract states, plus:

```csharp
public sealed record RelayRequest(IReadOnlyDictionary<string, string> Headers, Stream Body, long? ContentLength);
public sealed record RelayResponse(int StatusCode, string? JsonBody, IReadOnlyDictionary<string, string> Headers);
public interface IReporterAuthenticator { ValueTask<ReporterIdentity?> AuthenticateAsync(string bearerToken, CancellationToken cancellationToken); }

public enum ReportArea { Public, Pending }
public readonly record struct ReportBlobHeaders(string ContentType, string CacheControl);
public interface IReportMediaStore
{
    // False only when overwrite is false and the path already exists. The first write is kept.
    Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content, ReportBlobHeaders headers,
        bool overwrite, CancellationToken cancellationToken);
    // Null only when absent. Throws InvalidDataException over maxBytes. Any other fault throws.
    Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken);
}

public enum ReportWorkKind { CreateIssue, AppendServerSnapshot }
public sealed record ReportWorkItem(ReportWorkKind Kind, string ReportId, int Requeues = 0);
public interface IReportWorkQueue { Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken); }
public static class ReportWorkItemJson
{
    public static string Serialize(ReportWorkItem item);   // {"kind":"createIssue","reportId":"<32 hex>","requeues":0}
    public static ReportWorkItem Parse(string json);       // FormatException on unknown kind, bad id, negative requeues or bad JSON. Missing "requeues" reads as 0
}

public sealed record QuotaCounter(long Count, string Version);
public interface IReportQuotaStore
{
    Task<QuotaCounter?> ReadAsync(string key, CancellationToken cancellationToken);
    // expectedVersion null means create-only. False on any version conflict.
    Task<bool> TryWriteAsync(string key, long count, string? expectedVersion, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}

public sealed record GitHubIssueDraft(string Title, string Body, IReadOnlyList<string> Labels);
public interface IGitHubIssueClient
{
    Task<int> CreateIssueAsync(GitHubIssueDraft issue, CancellationToken cancellationToken);
    Task AddCommentAsync(int issueNumber, string body, CancellationToken cancellationToken);
}
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(int statusCode, bool isRateLimited, TimeSpan? retryAfter, string message);
    public int StatusCode { get; }
    public bool IsRateLimited { get; }
    public TimeSpan? RetryAfter { get; }
}
public sealed record GitHubAppCredentials(long AppId, long InstallationId, string PrivateKeyPem);

public sealed record IssueFact(string Label, string Section, string Property)   // Property is a dot path
{
    // Platform/platform.os, Game version/build.version, Engine version/build.engineVersion,
    // Backend/graphics.backend, GPU/graphics.adapter, in that order.
    public static IReadOnlyList<IssueFact> EngineDefaults { get; }
}

// BugReportRelayOptions: the contract members unchanged, plus
//   public IReadOnlyList<IssueFact> IssueFacts { get; init; } = IssueFact.EngineDefaults;
//   public ILogger? Logger { get; init; }

public sealed class InMemoryReportMediaStore : IReportMediaStore
{
    public ReportBlobHeaders? HeadersOf(ReportArea area, string path);
    public IReadOnlyList<string> Paths(ReportArea area);   // ordinal order
}
public sealed class InMemoryReportWorkQueue : IReportWorkQueue
{
    public int Count { get; }
    public bool TryDequeue([NotNullWhen(true)] out ReportWorkItem? item);
}
public sealed class InMemoryReportQuotaStore : IReportQuotaStore { }

internal static class RelayJson { public static JsonSerializerOptions Options { get; } }
internal static class ReportPaths { public static bool IsValid(string path); }  // segments [a-z0-9._-]+, '/' separated, no leading '/', no "." or ".." segment
```

The in-memory media store throws `ArgumentException` for a path `ReportPaths.IsValid` refuses, so every test catches a bad path. The in-memory quota store uses a monotonically increasing integer version rendered as a string.

- [ ] **Step 1: Write the failing tests**

`Conformance/ReportMediaStoreConformance.cs` is an abstract xunit class with `protected abstract IReportMediaStore CreateStore();` and these facts:

```csharp
[Fact] public async Task Put_ThenGet_RoundTripsBytes()                  // bytes {1,2,3} at "g/2026-10/a/x.json"
[Fact] public async Task Put_WithoutOverwrite_ExistingPath_ReturnsFalseAndKeepsFirst()   // second write {9} is ignored
[Fact] public async Task Put_WithOverwrite_ReplacesAndReturnsTrue()
[Fact] public async Task Get_Absent_ReturnsNull()
[Fact] public async Task Get_OverMaxBytes_ThrowsInvalidDataException()  // 10 bytes stored, maxBytes 9
[Fact] public async Task Areas_AreIndependent()                         // same path in Public and Pending holds different bytes
[Fact] public async Task Exists_ReflectsPut()
```

`Conformance/ReportQuotaStoreConformance.cs`, abstract with `CreateStore()`:

```csharp
[Fact] public async Task Read_Absent_ReturnsNull()
[Fact] public async Task CreateOnly_WhenAbsent_WritesCountOne()          // TryWriteAsync(key, 1, null, ...) is true, Read gives Count 1
[Fact] public async Task CreateOnly_WhenPresent_ReturnsFalse()
[Fact] public async Task Write_WithCurrentVersion_SucceedsAndChangesVersion()
[Fact] public async Task Write_WithStaleVersion_ReturnsFalseAndKeepsCount()
```

`InMemorySeamTests.cs` subclasses both suites over the in-memory stores and adds:

```csharp
[Fact] public async Task InMemoryMediaStore_RecordsHeaders()            // HeadersOf returns the exact ReportBlobHeaders written
[Fact] public async Task InMemoryMediaStore_InvalidPath_Throws()        // "../x", "/x", "a//b", "A/b" each throw ArgumentException
[Fact] public async Task InMemoryQueue_IsFifo()
```

`ReportWorkItemJsonTests.cs`:

```csharp
[Fact] public void Serialize_IsCamelCase()   // Assert.Equal("{\"kind\":\"appendServerSnapshot\",\"reportId\":\"" + id + "\",\"requeues\":2}", ...) for Requeues 2
[Fact] public void Parse_RoundTrips()
[Fact] public void Parse_MissingRequeues_IsZero()
[Theory, InlineData("{\"kind\":\"other\",\"reportId\":\"<valid id>\"}"), InlineData("{\"kind\":\"createIssue\",\"reportId\":\"xyz\"}"), InlineData("not json")]
public void Parse_Rejects(string json)       // Assert.Throws<FormatException>
```

`ArchitectureTests.BugReportsRelay.cs`:

```csharp
[Fact] public void BugReportsRelay_ReferencesOnlyItsEngineEdges()
// Assert.Equal(new[] { "BugReports", "BugReports.Contracts", "Diagnostics", "Http" }, sorted ProjectRefs)
// Assert.DoesNotContain(PackageRefs, p => !IgnoredInfraPackages.Contains(p))
[Fact] public void BugReportsRelay_IsAnExplicitReferenceOutsideEveryUmbrella()
[Fact] public void BugReportsRelayTests_ReferenceOnlyWhatTheyUse()
// Assert.Equal(new[] { "BugReports", "BugReports.Contracts", "BugReports.Relay", "Diagnostics", "Imaging" }, ...)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e3-t1 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release`
Expected: build FAIL, the relay types do not exist.

- [ ] **Step 3: Implement the package**

Create the csproj (references above, `InternalsVisibleTo` `KhaozEngine.BugReports.Relay.Tests`). The test csproj carries its final reference set from this task, because Tasks 4 to 7 use `BugReports`, `Imaging` and `Diagnostics` and the architecture fact pins the set once. Then create every type in the Interfaces block, the README opening paragraph and the root README catalog row (`Depends on`: `BugReports`, `BugReports.Contracts`, `Diagnostics`, `Http`). The in-memory stores lock one object per instance. `BugReportRelayOptions` is the contract record with the two added members.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label e3-t1 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release`
Expected: PASS, 0 warnings.
Run: `build-slot --label e3-t1-arch -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS.
Run: `bash scripts/check-doc-versions.sh`
Expected: ends with `all engine-version declarations match`.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports.Relay KhaozEngine.BugReports.Relay.Tests KhaozEngine.Tests/ArchitectureTests.cs KhaozEngine.Tests/ArchitectureTests.BugReportsRelay.cs KhaozEngine.slnx README.md
git commit -m "feat(bugreports-relay): add relay seams and in-memory stores"
```

---

### Task 2: Quota gate

**Files:**
- Create: `KhaozEngine.BugReports.Relay/ReportQuotaGate.cs`, `KhaozEngine.BugReports.Relay/QuotaWindows.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/ReportQuotaGateTests.cs`, `Support/ConflictingQuotaStore.cs`

**Interfaces:**
- Consumes: `IReportQuotaStore`, `QuotaCounter`, `BugReportRelayOptions` limits, `ILogger`.
- Produces:

```csharp
internal readonly record struct QuotaWindow(string Key, int Limit, DateTimeOffset End);
internal static class QuotaWindows
{
    public static string SubjectHash(string subject);   // first 32 lowercase hex of SHA-256(UTF-8 subject)
    // [ah-<h>-<yyyyMMddHH>, ad-<h>-<yyyyMMdd>, gd-<yyyyMMdd>] in that order, all UTC
    public static IReadOnlyList<QuotaWindow> For(string subject, DateTimeOffset now, BugReportRelayOptions options);
}
internal readonly record struct QuotaDecision(bool Allowed, TimeSpan RetryAfter);
internal sealed class ReportQuotaGate
{
    public const int MaxWriteAttempts = 5;
    public static readonly TimeSpan ContentionRetryAfter = TimeSpan.FromSeconds(5);
    public ReportQuotaGate(IReportQuotaStore store, BugReportRelayOptions options, TimeProvider time, ILogger logger);
    public Task<QuotaDecision> TryConsumeAsync(string subject, CancellationToken cancellationToken);
}
```

The algorithm the signatures do not determine:

```text
now = time.GetUtcNow() in UTC
windows = QuotaWindows.For(subject, now, options)
retryFor(w) = max(1 s, ceiling to whole seconds of (w.End - now))
phase 1: read every window. If any has Count >= Limit, deny with the largest retryFor among full windows. Write nothing.
phase 2: for each window in order:
    repeat up to MaxWriteAttempts:
        c = read(w.Key)
        if c is not null and c.Count >= w.Limit: deny with retryFor(w)
        if TryWrite(w.Key, (c?.Count ?? 0) + 1, c?.Version, w.End + 1 day): next window
    attempts exhausted: log a warning naming the window kind only, deny with ContentionRetryAfter
allow with RetryAfter zero
```

Windows already incremented before a later denial stay incremented. Over-counting is safe and is noted in the README.

- [ ] **Step 1: Write the failing tests**

Options use the contract defaults (5, 20, 100). The clock starts at `2026-10-17T10:15:00Z`.

```csharp
[Fact] public async Task Consume_SixthInSameHour_IsDeniedWithRetryAfterToHourEnd()
// five Allowed, sixth: Assert.False(d.Allowed); Assert.Equal(TimeSpan.FromMinutes(45), d.RetryAfter)
[Fact] public async Task Consume_NextHour_AllowsAgain()
[Fact] public async Task Consume_TwentyFirstInDay_IsDeniedUntilMidnightUtc()
// 20 spread over four hours, 21st at 14:30Z: RetryAfter == 9.5 h
[Fact] public async Task Consume_GlobalHundred_DeniesAnyNextSubject()
// 100 consumes over 20 subjects, the next subject is denied, RetryAfter to midnight
[Fact] public async Task Consume_DeniedByDayWindow_DoesNotIncrementHourWindow()
// the hour counter is unchanged after a day-window denial
[Fact] public async Task Consume_LastInstantOfDay_CountsInOldDay_RetryAfterAtLeastOneSecond()
// clock 2026-10-31T23:59:59.900Z, global day full: RetryAfter == 1 s, keys end in 20261031
[Fact] public async Task Consume_VersionConflictTwice_RetriesAndSucceeds()
// ConflictingQuotaStore fails the first two writes: Allowed, three write attempts on the hour key
[Fact] public async Task Consume_ConflictOnEveryAttempt_DeniesWithFiveSecondRetryAfter()
// exactly 5 write attempts on the hour key, RetryAfter == 5 s, one warning logged
[Fact] public void Keys_HashTheSubject_AndNeverContainItVerbatim()
// subject "discord:42": no key contains "discord", each key matches ^(ah|ad)-[0-9a-f]{32}-\d+$ or ^gd-\d{8}$
```

`ConflictingQuotaStore` wraps `InMemoryReportQuotaStore`, returns false for the first N `TryWriteAsync` calls and counts calls per key.

- [ ] **Step 2: Run to verify failure**

Run: `build-slot --label e3-t2 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release --filter "FullyQualifiedName~ReportQuotaGateTests"`
Expected: build FAIL, `ReportQuotaGate` missing.

- [ ] **Step 3: Implement `QuotaWindows` and `ReportQuotaGate` per the signatures and algorithm above**

- [ ] **Step 4: Run to verify pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports.Relay/ReportQuotaGate.cs KhaozEngine.BugReports.Relay/QuotaWindows.cs KhaozEngine.BugReports.Relay.Tests/ReportQuotaGateTests.cs KhaozEngine.BugReports.Relay.Tests/Support/ConflictingQuotaStore.cs
git commit -m "feat(bugreports-relay): fixed-window report quotas"
```

---

### Task 3: Storage layout and report records

**Files:**
- Create: `KhaozEngine.BugReports.Relay/ReportLayout.cs`, `ReportRecord.cs`, `ReportRecordStore.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/ReportLayoutTests.cs`, `ReportRecordStoreTests.cs`

**Interfaces:**
- Consumes: `IReportMediaStore`, `ReportBlobHeaders`, `RelayJson`, `ReportPaths`, `BugReportManifest`, `ReporterIdentity`, `ReportIds`.
- Produces:

```csharp
internal static class ReportLayout
{
    public const string ReportRecordFile = "report.json";
    public const string ViewerFile = "viewer.html";
    public const string ServerSnapshotFile = "server.json";
    public const int MaxRecordBytes = 2 * 1024 * 1024;
    public static string NewFolder(string gameId, string reportId, DateTimeOffset receivedAt);  // "<game>/<yyyy-mm>/<id>-<32 hex>"
    public static string Entry(string folder, string name);           // folder + "/" + name, validated by ReportPaths
    public static string AcceptedIndex(string reportId);              // "accepted/<id>.json"
    public static string PendingSnapshot(string reportId);            // "snapshots/<id>.json"
    public static ReportBlobHeaders HeadersFor(string name);          // by extension, table in Decisions. Unknown extension throws
    public static Uri PublicUrl(Uri publicMediaBase, string path);    // base must end in '/'
}

internal enum ServerSnapshotState { None, InBody, Commented }
internal sealed record ReportRecord(int FormatVersion, string ReportId, string Reference, string Folder,
    DateTimeOffset ReceivedAt, ReporterIdentity Reporter, BugReportManifest Manifest, int? IssueNumber,
    ServerSnapshotState ServerSnapshot);
internal sealed record AcceptedReport(string Folder, string Subject, DateTimeOffset ReceivedAt);

internal sealed class ReportRecordStore
{
    public ReportRecordStore(IReportMediaStore media);
    public Task<bool> TryCreateAcceptedAsync(string reportId, AcceptedReport accepted, CancellationToken ct);  // Pending, overwrite false
    public Task<AcceptedReport?> LoadAcceptedAsync(string reportId, CancellationToken ct);
    public Task SaveReportAsync(ReportRecord record, CancellationToken ct);                                   // Public, overwrite true
    public Task<ReportRecord?> LoadReportAsync(string folder, CancellationToken ct);
    public Task<bool> PutPendingSnapshotAsync(string reportId, ReadOnlyMemory<byte> body, CancellationToken ct); // overwrite false
    public Task<ReadOnlyMemory<byte>?> LoadPendingSnapshotAsync(string reportId, CancellationToken ct);
}
```

`report.json` serializes through `RelayJson.Options`, so its shape is:

```json
{"formatVersion":1,"reportId":"<32 hex>","reference":"R-XXXXXX","folder":"<game>/<yyyy-mm>/<id>-<token>",
 "receivedAt":"2026-10-17T01:02:03+00:00","reporter":{"subject":"...","displayName":"...","handle":null},
 "manifest":{...},"issueNumber":null,"serverSnapshot":"none"}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// ReportLayoutTests
[Fact] public void NewFolder_HasGameMonthIdAndHexToken()
// matches ^grimhollow/2026-10/<id>-[0-9a-f]{32}$ for receivedAt 2026-10-17T01:02:03Z
[Fact] public void NewFolder_TwoCalls_DifferInToken()
[Fact] public void NewFolder_LastInstantOfMonthWithOffset_UsesUtcMonth()
// receivedAt 2026-11-01T09:59:59.9+10:00 is 2026-10-31T23:59:59.9Z, so the folder month is 2026-10
[Theory]
[InlineData("manifest.json", "application/json; charset=utf-8", "no-cache")]
[InlineData("viewer.html", "text/html; charset=utf-8", "no-cache")]
[InlineData("screenshot.png", "image/png", "public, max-age=86400")]
[InlineData("clip/007.jpg", "image/jpeg", "public, max-age=86400")]
[InlineData("clip.mp4", "video/mp4", "public, max-age=86400")]
public void HeadersFor_ByExtension(string name, string contentType, string cacheControl)
[Fact] public void HeadersFor_UnknownExtension_Throws()
[Fact] public void PublicUrl_JoinsBaseAndPath()
// "https://media.example.test/reports/" + "g/2026-10/a/viewer.html" gives ".../reports/g/2026-10/a/viewer.html"
[Fact] public void PublicUrl_BaseWithoutTrailingSlash_Throws()

// ReportRecordStoreTests (over InMemoryReportMediaStore)
[Fact] public async Task Report_RoundTripsEveryField()
[Fact] public async Task Report_JsonIsCamelCaseWithStringEnums()   // raw bytes contain "\"serverSnapshot\":\"none\"" and "\"issueNumber\":null"
[Fact] public async Task Accepted_CreateTwice_SecondReturnsFalseAndKeepsFirst()
[Fact] public async Task PendingSnapshot_FirstWriteWins()
[Fact] public async Task Load_Absent_ReturnsNull()
```

- [ ] **Step 2: Run to verify failure**

Run: `build-slot --label e3-t3 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release --filter "FullyQualifiedName~ReportLayoutTests|FullyQualifiedName~ReportRecordStoreTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement `ReportLayout`, the records and `ReportRecordStore`**

The token is `RandomNumberGenerator.GetHexString(32, lowercase: true)`. Month and window formatting use `CultureInfo.InvariantCulture` on the UTC value.

- [ ] **Step 4: Run to verify pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports.Relay/ReportLayout.cs KhaozEngine.BugReports.Relay/ReportRecord.cs KhaozEngine.BugReports.Relay/ReportRecordStore.cs KhaozEngine.BugReports.Relay.Tests/ReportLayoutTests.cs KhaozEngine.BugReports.Relay.Tests/ReportRecordStoreTests.cs
git commit -m "feat(bugreports-relay): report folder layout and records"
```

---

### Task 4: Issue composer

**Files:**
- Create: `KhaozEngine.BugReports.Relay/IssueMarkdown.cs`, `IssueTitle.cs`, `IssueLimits.cs`, `IssueInput.cs`, `IssueFactReader.cs`, `ReportIssueComposer.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/IssueMarkdownTests.cs`, `IssueTitleTests.cs`, `ReportIssueComposerTests.cs`, `Support/IssueFixtures.cs`, `Golden/issue-minimal.md`, `Golden/issue-full.md`, `Golden/issue-hostile.md`, `Golden/comment-snapshot.md` (copied to output with `CopyToOutputDirectory` `PreserveNewest`)

**Interfaces:**
- Consumes: `ReportRecord`, `ServerSnapshotState`, `ReportLayout`, `IssueFact`, `GitHubIssueDraft`, `CapturedSection`, `ClipIndex`, `ServerSnapshotWire`, `BundleEntryNames`, `BugReportRelayOptions`.
- Produces:

```csharp
internal static class IssueLimits
{
    public const int MaxBodyChars = 60_000;
    public const int LogTailLines = 50;
    public const int MaxLogLineChars = 400;
    public const int MaxFactChars = 120;
    public const int ClipThumbnails = 4;
    public const int ThumbnailWidth = 240;
}
internal static class IssueMarkdown
{
    public static string Neutralise(string text);                       // rules below
    public static string Quote(string text);                            // normalise newlines, Neutralise, prefix "> ", blank line becomes ">"
    public static string TableCell(string text);                        // Neutralise, "|" to "\|", any newline run to one space
    public static string InlineCode(string text);                       // backtick span for the handle and subject cells, a longer fence when the value contains a backtick, "|" escaped, newlines to a space, never neutralised
    public static string Fenced(IReadOnlyList<string> lines);           // fence of max(3, longest backtick run + 1) backticks, info string "text"
}
internal static class IssueTitle
{
    public static string Compose(string gameDisplayName, string? version, string description);
}
internal sealed record IssueInput(ReportRecord Report, IReadOnlyList<CapturedSection> Sections, ClipIndex? Clip,
    IReadOnlyList<string> LogTail, ServerSnapshotWire? Snapshot);
internal static class IssueFactReader
{
    // Section looked up in input.Sections first, then input.Snapshot.Sections. Null when missing.
    // String as is, number and bool as raw text, object and array as compact JSON, cut to MaxFactChars text elements with "...".
    public static string? Read(IssueInput input, IssueFact fact);
}
internal sealed class ReportIssueComposer
{
    public ReportIssueComposer(BugReportRelayOptions options);
    public GitHubIssueDraft ComposeIssue(IssueInput input);
    public string ComposeSnapshotComment(IssueInput input);
}
```

`Neutralise` applies, in this order, to the whole string:

1. `&` to `&amp;`, then `<` to `&lt;`, then `>` to `&gt;`.
2. Every `@` to `@` followed by U+200B.
3. Every `#` immediately followed by an ASCII digit to `#` followed by U+200B.
4. Every case-insensitive `gh-` immediately followed by an ASCII digit to the original `gh` letters, U+200B, then `-`.
5. Every case-insensitive `github.com` to the original `github` letters, U+200B, then `.com`.
6. Every control character other than `\n` and `\t` is removed.

`IssueTitle.Compose`: neutralise steps 2 to 6 only (titles are not Markdown), take the first line that is not empty after `Trim()`, cut by `StringInfo` text elements to 67 plus `...` when it has more than 70, and prefix `[<GameDisplayName> <version ?? "unknown">] `. The version is read by the composer from section `build` property `version`.

Issue body layout. Lines end in `\n`, sections are separated by exactly one blank line, the body ends with one `\n`, and `{x}` marks a value:

````text
### What happened

{IssueMarkdown.Quote(manifest.Description)}

### Report

| Field | Value |
| --- | --- |
| Reporter | {TableCell(reporter.DisplayName)} |
| Handle | {InlineCode(reporter.Handle)} |                 (only when Handle is not null)
| Account | {InlineCode(reporter.Subject)} |
| Reference | {reference} |
| Category | {TableCell(manifest.Category)} |
| Captured | {capturedAt UTC as yyyy-MM-dd HH:mm:ss} UTC |
| {fact.Label} | {TableCell(value)} |                       (per options.IssueFacts, omitted when null)

### Screenshot                                                (only when the manifest has a screenshot entry)

![Screenshot]({url of screenshot.png or screenshot.jpg})

### Clip                                                      (only when clip frames exist or clip.mp4 exists)

<a href="{viewer url}"><img src="{frame url}" width="240"></a> ...   (one line, up to 4 frames, one space between)

[Play the clip]({viewer url})

### Files

- [Viewer]({viewer url})
- [{entry name}]({entry url})                               (each sections/*.json in manifest order)
- [Server snapshot]({server.json url})                      (only when ServerSnapshot is InBody)

<details>                                                     (only when LogTail is not empty)
<summary>Last 50 log lines</summary>

{IssueMarkdown.Fenced(last 50 lines, each cut to 400 chars)}

</details>
````

Thumbnail frame indices for `n` frames: all when `n <= 4`, otherwise `round(i * (n - 1) / 3.0, MidpointRounding.AwayFromZero)` for `i` 0 to 3. When the body exceeds `MaxBodyChars`, the Files list becomes `- [Viewer]({viewer url})` and `- {k} more files in the report folder`. If it still exceeds, the details block becomes `Log tail omitted for size. Open the viewer.`

Snapshot comment layout:

```text
### Server snapshot

Received for report {reference}.

| Field | Value |
| --- | --- |
| {fact.Label} | {TableCell(value)} |          (only facts whose Section is a key of Snapshot.Sections, table omitted when none)

- [Server snapshot]({server.json url})
- [Viewer]({viewer url})
```

- [ ] **Step 1: Write the failing tests**

`IssueFixtures` builds inputs with game `grimhollow`, display name `Grimhollow`, base `https://media.example.test/reports/`, report id `0192f3a4b5c67d8e9f0a1b2c3d4e5f60`, folder `grimhollow/2026-10/0192f3a4b5c67d8e9f0a1b2c3d4e5f60-00112233445566778899aabbccddeeff`, reporter `("discord:42", "Ash", null)`, captured `2026-10-17T01:02:03Z`, category `Gameplay`, sections `build` `{"version":"0.29.3","engineVersion":"21.2.0"}` and `platform` `{"os":"macOS 26.0"}`. Goldens carry the literal token `{reference}`, which the test replaces with `ReportIds.ReferenceCode(id)` before comparing. Write `Golden/issue-minimal.md` exactly as:

```text
### What happened

> Fell through the floor
> near the mill

### Report

| Field | Value |
| --- | --- |
| Reporter | Ash |
| Account | discord:42 |
| Reference | {reference} |
| Category | Gameplay |
| Captured | 2026-10-17 01:02:03 UTC |
| Platform | macOS 26.0 |
| Game version | 0.29.3 |
| Engine version | 21.2.0 |

### Files

- [Viewer](https://media.example.test/reports/grimhollow/2026-10/0192f3a4b5c67d8e9f0a1b2c3d4e5f60-00112233445566778899aabbccddeeff/viewer.html)
- [sections/build.json](https://media.example.test/reports/grimhollow/2026-10/0192f3a4b5c67d8e9f0a1b2c3d4e5f60-00112233445566778899aabbccddeeff/sections/build.json)
- [sections/platform.json](https://media.example.test/reports/grimhollow/2026-10/0192f3a4b5c67d8e9f0a1b2c3d4e5f60-00112233445566778899aabbccddeeff/sections/platform.json)
```

`issue-full.md` adds handle `ash#0001`, a `graphics` section `{"backend":"Metal","adapter":"Apple M3"}`, `screenshot.png`, 60 clip frames, 3 log lines, and an `InBody` snapshot, written by hand from the layout. `issue-hostile.md` uses the description in the Review Focus.

```csharp
// IssueMarkdownTests
[Fact] public void Neutralise_At_GetsZeroWidthSpace()             // "@octocat" -> "@​octocat"
[Fact] public void Neutralise_HashDigit_Only()                    // "#123 #abc owner/repo#12" -> "#​123 #abc owner/repo#​12"
[Fact] public void Neutralise_GhReference_KeepsCase()             // "GH-7" -> "GH​-7"
[Fact] public void Neutralise_GithubDotCom()                      // "https://GitHub.com/a/b/issues/1" -> "https://GitHub​.com/a/b/issues/1"
[Fact] public void Neutralise_HtmlBecomesEntities()               // "<img src=x> & >" -> "&lt;img src=x&gt; &amp; &gt;"
[Fact] public void Neutralise_ControlCharactersRemoved()          // "a\u0007b" -> "ab"
[Fact] public void Quote_EveryLineQuoted_BlankLinesStayInside()   // "a\r\n\r\nb\rc" -> "> a\n>\n> b\n> c"
[Fact] public void TableCell_PipeAndNewlines()                    // "a|b\n\nc" -> "a\\|b c"
[Fact] public void Handle_and_subject_render_as_inline_code()   // "<@42>" appears as `<@42>`, no zero-width space
[Fact] public void Fenced_LongerThanLongestBacktickRun()          // a line with "````" gives a 5-backtick fence

// IssueTitleTests
[Fact] public void Title_FirstLineTrimmed()                       // ("Grimhollow","0.29.3","  Stuck  \nmore") -> "[Grimhollow 0.29.3] Stuck"
[Fact] public void Title_LeadingBlankLines_UseFirstNonEmpty()
[Fact] public void Title_Over70_CutTo67PlusEllipsis()             // 80 'a' -> text part is 67 'a' + "...", 70 text elements
[Fact] public void Title_Exactly70_Unchanged()
[Fact] public void Title_EmojiAndCombiningMarksAtBoundary_NotSplit()
// 66 'a' + "\U0001F600" + "é" + 10 'b': the text part ends with "\U0001F600...", no lone surrogate
[Fact] public void Title_MissingVersion_UsesUnknown()             // "[Grimhollow unknown] ..."
[Fact] public void Title_NeutralisesMentions()                    // "@dev #12" -> "@​dev #​12", no "&amp;" escaping

// ReportIssueComposerTests
[Fact] public void ComposeIssue_Minimal_MatchesGolden()
[Fact] public void ComposeIssue_Full_MatchesGolden()
[Fact] public void ComposeIssue_HostileDescription_MatchesGolden()
// description "</details>\n| a | b |\n```\n<img src=x>\n> quoted\n@here #1"
[Fact] public void ComposeIssue_SixtyFrames_ThumbnailsAre0_20_39_59()
[Fact] public void ComposeIssue_TwoFrames_ShowsBoth()
[Fact] public void ComposeIssue_Mp4Only_LinksClipWithoutThumbnails()
[Fact] public void ComposeIssue_LabelsAndTitle_FromOptionsAndBuildSection()
[Fact] public void ComposeIssue_LogTail_LastFiftyLines_EachCutTo400()
[Fact] public void ComposeIssue_MissingFactsOmitted()             // no graphics section: no Backend or GPU rows
[Fact] public void ComposeIssue_WorstCase_BodyAtMost60000()
// 4000 '&' description, 128 sections, 50 lines of 10,000 chars, five 1,000-char facts: Body.Length <= 60_000
[Fact] public void MaxBodyChars_IsBelowGitHubLimit()               // Assert.True(IssueLimits.MaxBodyChars < 65_536)
[Fact] public void ComposeSnapshotComment_MatchesGolden()
```

- [ ] **Step 2: Run to verify failure**

Run: `build-slot --label e3-t4 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release --filter "FullyQualifiedName~IssueMarkdownTests|FullyQualifiedName~IssueTitleTests|FullyQualifiedName~ReportIssueComposerTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement the six types per the rules and layouts above**

Build with a `StringBuilder` and `\n` only. URLs come from `ReportLayout.PublicUrl`.

- [ ] **Step 4: Run to verify pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports.Relay/Issue*.cs KhaozEngine.BugReports.Relay/ReportIssueComposer.cs KhaozEngine.BugReports.Relay.Tests/Issue*Tests.cs KhaozEngine.BugReports.Relay.Tests/ReportIssueComposerTests.cs KhaozEngine.BugReports.Relay.Tests/Support/IssueFixtures.cs KhaozEngine.BugReports.Relay.Tests/Golden KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj
git commit -m "feat(bugreports-relay): compose neutralised issue bodies"
```

---

### Task 5: Relay facade and client intake

**Files:**
- Create: `KhaozEngine.BugReports.Relay/BugReportRelay.cs`, `RelayIntake.cs`, `RequestHeaderReader.cs`, `RelayResponses.cs`, `CappedBody.cs`, `ReportBundleStorer.cs`, `ReportViewerWriter.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/RelayIntakeTests.cs`, `Support/RelayHarness.cs`, `Support/TestBundles.cs`, `Support/ThrowingStream.cs`, `Support/FakeAuthenticator.cs`, `Support/RecordingLogger.cs`, `Support/RecordingGitHubIssueClient.cs`, `Support/HookedMediaStore.cs` (wraps the in-memory store and runs a callback before the first write to a path prefix)

**Interfaces:**
- Consumes: Tasks 1 to 3, `BundleValidator`, `BundleEntryNames`, `ReportIds`, `RelayHeaders`, `IntakeAccepted`, `RelayError`, `ClipIndex`, `CapturedSection`, `BugReportViewerModel`, `BugReportViewerPage`, E2's `BugReportBundleWriter` and `BugReportDraft` (tests), E1's `ReportImage`, `ReportClip`, `JpegWriter`, `PngWriter` (tests).
- Produces:

```csharp
public sealed class BugReportRelay
{
    // Validates options: GameId matches ^[a-z0-9-]{1,32}$, ServiceKey has at least 32 characters, PublicMediaBase is
    // absolute, ends in '/', and is https unless the host is loopback, every limit is positive, and
    // MaxBundleBytes <= BugReportLimits.MaxBundleBytes. ArgumentException otherwise.
    public BugReportRelay(BugReportRelayOptions options, IReporterAuthenticator authenticator, IReportMediaStore media,
        IReportWorkQueue work, IReportQuotaStore quotas, IGitHubIssueClient github, TimeProvider time);
    public Task<RelayResponse> IntakeAsync(RelayRequest request, CancellationToken cancellationToken);
    public Task<RelayResponse> ServerSnapshotIntakeAsync(string reportId, RelayRequest request, CancellationToken cancellationToken); // Task 7
    public Task ProcessWorkAsync(ReportWorkItem item, CancellationToken cancellationToken);                                         // Task 7
}
internal sealed class RequestHeaderReader
{
    public RequestHeaderReader(IReadOnlyDictionary<string, string> headers);   // OrdinalIgnoreCase copy
    public string? Get(string name);
    public string? BearerToken();     // "Bearer <token>", scheme case-insensitive, token trimmed, null when absent or empty
}
internal static class RelayResponses
{
    public static RelayResponse Accepted(string reportId);                                    // 202, IntakeAccepted body, Cache-Control no-store
    public static RelayResponse Error(int status, string error);                              // RelayError body, Cache-Control no-store
    public static RelayResponse Unauthorized();                                               // 401 "unauthorized", WWW-Authenticate: Bearer
    public static RelayResponse TooManyRequests(TimeSpan retryAfter);                         // 429 "quota", Retry-After whole seconds
}
internal static class CappedBody
{
    // Null when the declared length or the streamed bytes exceed maxBytes. Reads at most maxBytes + 1 bytes.
    public static Task<MemoryStream?> ReadAsync(Stream body, long? declaredLength, long maxBytes, CancellationToken ct);
}
internal sealed class ReportBundleStorer
{
    // Writes every zip entry, report.json and viewer.html under a new folder and returns the record.
    public Task<ReportRecord> StoreAsync(MemoryStream bundle, BugReportManifest manifest, ReporterIdentity reporter,
        DateTimeOffset receivedAt, CancellationToken ct);
}
internal sealed class ReportViewerWriter
{
    // Reads sections, clip index and log tail from the folder, renders BugReportViewerPage, writes viewer.html.
    public Task WriteAsync(ReportRecord record, ReadOnlyMemory<byte>? serverSnapshotJson, CancellationToken ct);
}
```

Intake algorithm (the order is binding):

```text
1. token = BearerToken(); null -> 401. identity = authenticator(token); null -> 401. Exceptions propagate.
2. identity.Subject in options.BlockedSubjects (ordinal) -> 403 "blocked".
3. quota = gate.TryConsumeAsync(subject); denied -> 429 with Retry-After.
4. key = header Idempotency-Key; missing or !ReportIds.IsValid -> 400 "bad-request".
   body = CappedBody(request.Body, request.ContentLength, options.MaxBundleBytes); null -> 413 "too-large".
   result = BundleValidator.Validate(body, options.MaxBundleBytes); invalid -> 422 "invalid-bundle: " + result.Error.
   manifest.ReportId != key -> 400 "id-mismatch". manifest.GameId != options.GameId -> 422 "game-mismatch".
5. accepted = LoadAccepted(key). Present -> if LoadReport(accepted.Folder).IssueNumber is null, enqueue CreateIssue.
   Return 202 with the same reference.
6. record = storer.StoreAsync(...). TryCreateAccepted(key, new AcceptedReport(record.Folder, subject, receivedAt)).
   Lost the race -> load the winner and continue as step 5 for the winner. The new folder stays orphaned.
7. Enqueue CreateIssue(key). 202 Accepted(key).
```

Section blobs come from `sections/<key>.json` entries. The log tail is the string array at property `lines` of section `log` (see contract proposal 2), empty when absent. The clip index parses through `RelayJson.Options`.

- [ ] **Step 1: Write the failing tests**

`RelayHarness` builds a relay over the three in-memory stores, a `FakeAuthenticator` mapping `"good"` to `("discord:42", "Ash", null)` and `"other"` to `("discord:7", "Bo", "bo#2")`, a `RecordingGitHubIssueClient`, a `RecordingLogger` and a `ManualClock` at `2026-10-17T01:02:03Z`. `TestBundles.Valid(reportId, gameId = "grimhollow")` writes a bundle through `BugReportBundleWriter` with a 16 by 16 PNG screenshot, three 16 by 16 JPEG frames and sections `build`, `platform` and `log`. `TestBundles.WithEntryBytes(bundle, name, bytes)` replaces one entry and rewrites its manifest `size` and `sha256` through `JsonNode`.

```csharp
[Fact] public async Task Intake_MissingAuthorization_401_BodyNeverRead()        // ThrowingStream body, WWW-Authenticate: Bearer
[Fact] public async Task Intake_NonBearerScheme_401()                           // "Basic abc"
[Fact] public async Task Intake_UnknownToken_401_BodyNeverRead()
[Fact] public async Task Intake_AuthenticatorThrows_Propagates_BodyNeverRead()
[Fact] public async Task Intake_HeaderNamesInAnyCase_Accepted()                // "authorization", "IDEMPOTENCY-KEY"
[Fact] public async Task Intake_BlockedSubject_403_BodyNeverRead_QuotaUntouched()
[Fact] public async Task Intake_OverHourlyQuota_429WithRetryAfter_BodyNeverRead()  // sixth intake: Headers["Retry-After"] == "3477"
[Fact] public async Task Intake_ContentLengthOverCap_413_BodyNeverRead()
[Fact] public async Task Intake_StreamOverCapWithoutLength_413_ReadsAtMostCapPlusOne()
[Fact] public async Task Intake_MissingIdempotencyKey_400()
[Fact] public async Task Intake_IdempotencyKeyDiffersFromManifest_400()
[Fact] public async Task Intake_TraversalEntry_422()                            // body "invalid-bundle: ..." 
[Fact] public async Task Intake_ScreenshotWithBadMagic_422()                   // WithEntryBytes("screenshot.png", "GIF89a...")
[Fact] public async Task Intake_GameMismatch_422()
[Fact] public async Task Intake_Valid_202_StoresAndEnqueues()
// JsonBody parses to IntakeAccepted(id, ReportIds.ReferenceCode(id)), every zip entry plus report.json and viewer.html under
// one folder, accepted/<id>.json in Pending, queue holds exactly CreateIssue(id)
[Fact] public async Task Intake_StoredBlobs_HaveExplicitContentTypes()        // every Public path has HeadersOf matching HeadersFor
[Fact] public async Task Intake_RecordIdentityComesFromTokenNotBundle()      // report.json reporter == ("discord:42","Ash",null), receivedAt == clock
[Fact] public async Task Intake_SameIdTwice_SameReference_NoSecondFolder()
[Fact] public async Task Intake_DuplicateBeforeIssueCreated_ReEnqueues()
[Fact] public async Task Intake_DuplicateAfterIssueCreated_DoesNotEnqueue()  // set IssueNumber 7 in report.json first
[Fact] public async Task Intake_ResendThreeDaysLater_SameReference_NothingNewStored()
[Fact] public async Task Intake_AcceptedIndexLostRace_ReturnsWinnersReference()
// HookedMediaStore writes a rival accepted/<id>.json (with a stored report.json) just before the relay's own create
[Fact] public void Constructor_ShortServiceKey_Throws()
[Fact] public void Constructor_HttpBaseOffLoopback_Throws()
```

- [ ] **Step 2: Run to verify failure**

Run: `build-slot --label e3-t5 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release --filter "FullyQualifiedName~RelayIntakeTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement the facade and intake types**

`ServerSnapshotIntakeAsync` and `ProcessWorkAsync` throw `NotImplementedException` until Task 7. `BugReportRelay` only validates options and delegates.

- [ ] **Step 4: Run to verify pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports.Relay KhaozEngine.BugReports.Relay.Tests
git commit -m "feat(bugreports-relay): authenticated, quota-checked bundle intake"
```

---

### Task 6: GitHub App issue client

**Files:**
- Create: `KhaozEngine.BugReports.Relay/GitHubAppJwt.cs`, `InstallationTokenCache.cs`, `GitHubResponseClassifier.cs`, `GitHubAppIssueClient.cs`
- Test: `KhaozEngine.BugReports.Relay.Tests/GitHubAppJwtTests.cs`, `GitHubAppIssueClientTests.cs`, `Support/ScriptedHttpHandler.cs`

**Interfaces:**
- Consumes: `IGitHubIssueClient`, `GitHubIssueDraft`, `GitHubApiException`, `GitHubAppCredentials`, `HttpRetry`, `HttpRetryPolicy`.
- Produces:

```csharp
internal static class GitHubAppJwt
{
    public static string Create(long appId, RSA key, DateTimeOffset now);   // iat = unix(now) - 60, exp = unix(now) + 540
}
internal sealed class InstallationTokenCache
{
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    public Task<string> GetAsync(CancellationToken ct);
    public void Invalidate();
}
internal static class GitHubResponseClassifier
{
    public static GitHubApiException? Classify(HttpResponseMessage response, DateTimeOffset now);  // null for 2xx
}
public sealed class GitHubAppIssueClient : IGitHubIssueClient, IDisposable
{
    public GitHubAppIssueClient(HttpClient http, GitHubAppCredentials credentials, string owner, string repository, TimeProvider time);
    internal GitHubAppIssueClient(HttpClient http, GitHubAppCredentials credentials, string owner, string repository,
        TimeProvider time, HttpRetryPolicy tokenPolicy);
}
```

Every request goes to `https://api.github.com/` with `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28` and `User-Agent: KhaozEngine-BugReports`. The token exchange is `POST app/installations/{InstallationId}/access_tokens` with `Authorization: Bearer <jwt>` through `HttpRetry.SendAsync`, reading `token` and `expires_at`. Issues are `POST repos/{owner}/{repo}/issues` with `{"title","body","labels"}` returning `number`. Comments are `POST repos/{owner}/{repo}/issues/{n}/comments` with `{"body"}`. Both use `Authorization: Bearer <installation token>`, are sent once, and on 401 invalidate the cache before throwing. Classification: 403 or 429 with `Retry-After` gives `IsRateLimited` and that delay. 403 or 429 with `x-ratelimit-remaining: 0` gives `IsRateLimited` and `x-ratelimit-reset` minus now, at least 1 s. 429 alone gives `IsRateLimited` and 60 s. Every other non-success gives `IsRateLimited` false and a message holding the status and at most 200 characters of GitHub's `message` field. The constructor throws `ArgumentException` when the PEM does not import as RSA.

- [ ] **Step 1: Generate the key at test time**

Tests create `using var rsa = RSA.Create(2048)` and pass `rsa.ExportPkcs8PrivateKeyPem()` as `PrivateKeyPem`. The fixed clock is `2026-10-17T00:00:00Z` (unix 1792195200) and the app id is 123456. The test asserts the JWT header is exactly `{"alg":"RS256","typ":"JWT"}`, the claims are exactly `iat` = clock minus 60 s, `exp` = clock plus 540 s and `iss` = the app id as a string at the fixed clock, and the signature verifies with `rsa.VerifyData(..., HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)`. The engine repository is public, so no private key is committed, even for tests.

- [ ] **Step 2: Write the failing tests**

```csharp
// GitHubAppJwtTests
[Fact] public void Jwt_header_claims_and_signature_verify()
// key from RSA.Create(2048) at test time, segment 0 decodes to {"alg":"RS256","typ":"JWT"}, claims are iat 1792195140,
// exp 1792195740 and iss "123456" at the fixed clock, signature verifies with RSA.VerifyData, SHA256, Pkcs1
[Fact] public void Create_NoPaddingCharacters()                            // Assert.DoesNotContain('=', token)

// GitHubAppIssueClientTests (ScriptedHttpHandler queues responses and records requests)
[Fact] public async Task CreateIssue_ExchangesTokenThenPostsIssue_ReturnsNumber()   // number 42, two requests in order
[Fact] public async Task CreateIssue_SendsTitleBodyLabelsAndHeaders()
[Fact] public async Task Token_ReusedUntilFiveMinutesBeforeExpiry()
// expires_at clock + 1 h: second CreateIssue at +54 min makes no exchange, third at +56 min exchanges again
[Fact] public async Task CreateIssue_403WithRetryAfter_ThrowsRateLimited()       // RetryAfter == 30 s
[Fact] public async Task CreateIssue_403RemainingZero_ThrowsRateLimitedUntilReset()
[Fact] public async Task CreateIssue_429Alone_ThrowsRateLimitedSixtySeconds()
[Fact] public async Task CreateIssue_422_ThrowsNotRateLimitedWithGitHubMessage()
[Fact] public async Task CreateIssue_401_InvalidatesTokenAndThrows()             // the next call exchanges a new token
[Fact] public async Task CreateIssue_TransportFault_SentOnceAndPropagates()     // one issue request recorded, HttpRequestException
[Fact] public async Task AddComment_PostsToCommentsRoute()
[Fact] public void Constructor_NonRsaPem_Throws()
[Fact] public async Task Exceptions_NeverContainTokenOrJwt()                     // messages of every thrown case exclude "ghs_" and the JWT
```

- [ ] **Step 3: Run to verify failure**

Run: `build-slot --label e3-t6 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release --filter "FullyQualifiedName~GitHubAppJwtTests|FullyQualifiedName~GitHubAppIssueClientTests"`
Expected: build FAIL.

- [ ] **Step 4: Implement the four types**

Use `RSA.ImportFromPem`, `RSA.SignData(..., HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)` and `System.Buffers.Text.Base64Url`. JSON goes through `Utf8JsonWriter` with the property order shown. Tests pass a `tokenPolicy` with zero backoff.

- [ ] **Step 5: Run to verify pass**

Same command. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.BugReports.Relay/GitHub*.cs KhaozEngine.BugReports.Relay/InstallationTokenCache.cs KhaozEngine.BugReports.Relay.Tests/GitHub*Tests.cs KhaozEngine.BugReports.Relay.Tests/Support/ScriptedHttpHandler.cs KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj
git commit -m "feat(bugreports-relay): GitHub App issue client"
```

---

### Task 7: Server snapshot intake and work processing

**Files:**
- Create: `KhaozEngine.BugReports.Relay/ServiceKeyCheck.cs`, `ServerSnapshotIntake.cs`, `ReportWorker.cs`, `ReportIssueInputLoader.cs`, `ReportNotReadyException.cs`
- Modify: `KhaozEngine.BugReports.Relay/BugReportRelay.cs` (delegate the two operations)
- Test: `KhaozEngine.BugReports.Relay.Tests/ServerSnapshotIntakeTests.cs`, `ReportWorkerTests.cs`, `RelayEndToEndTests.cs`, `ServiceKeyCheckTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 6, `ServerSnapshotWire`, `BugReportLimits.MaxServerSnapshotBytes`, `RelayHeaders.ServiceKey`, `RelayRoutes`.
- Produces:

```csharp
internal static class ServiceKeyCheck
{
    public static bool Matches(string expected, string? provided);   // FixedTimeEquals(SHA256(expected), SHA256(provided)), false for null
}
public sealed class ReportNotReadyException : Exception
{
    public ReportNotReadyException(string reportId);
    public string ReportId { get; }
}
internal sealed class ReportIssueInputLoader
{
    public Task<IssueInput> LoadAsync(ReportRecord record, ReadOnlyMemory<byte>? snapshotJson, CancellationToken ct);
}
internal sealed class ServerSnapshotIntake { public Task<RelayResponse> RunAsync(string reportId, RelayRequest request, CancellationToken ct); }
internal sealed class ReportWorker { public Task ProcessAsync(ReportWorkItem item, CancellationToken ct); }
```

Snapshot intake order:

```text
1. !ServiceKeyCheck.Matches(options.ServiceKey, header X-Report-Service-Key) -> 403 "forbidden". Body not read.
2. !ReportIds.IsValid(reportId) -> 400.
3. body = CappedBody(..., BugReportLimits.MaxServerSnapshotBytes); null -> 413.
4. wire = ServerSnapshotWire parse; bad JSON, FormatVersion != 1, wire.ReportId != reportId or empty Subject -> 400.
5. accepted = LoadAccepted(reportId)
   present and accepted.Subject != wire.Subject -> warn "server snapshot subject mismatch for report <id>", store nothing, 202
   PutPendingSnapshot(reportId, raw body)   (first write wins)
   present -> enqueue AppendServerSnapshot(reportId)
6. 202 Accepted(reportId)
```

Worker:

```text
CreateIssue:
  accepted = LoadAccepted(id); null -> log error "unknown report <id>", return
  record = LoadReport(accepted.Folder); record.IssueNumber set -> return
  snap = LoadPendingSnapshot(id); parse
  snap present and snap.Subject == record.Reporter.Subject -> put server.json (raw bytes), rewrite viewer with it,
      record = record with ServerSnapshot = InBody, SaveReport(record)
  snap present and subjects differ -> warn and ignore
  number = github.CreateIssueAsync(composer.ComposeIssue(loader.LoadAsync(record, snapIfMerged)))
  SaveReport(record with IssueNumber = number)
AppendServerSnapshot:
  accepted null -> return. record = LoadReport.
  record.IssueNumber null -> throw ReportNotReadyException(id)
  record.ServerSnapshot is InBody or Commented -> return
  snap = LoadPendingSnapshot; null -> warn, return. Subject mismatch -> warn, return.
  put server.json, rewrite viewer, github.AddCommentAsync(number, composer.ComposeSnapshotComment(...)),
  SaveReport(record with ServerSnapshot = Commented)
Any GitHubApiException or store fault propagates, so the host redelivers (D6).
```

- [ ] **Step 1: Write the failing tests**

```csharp
// ServiceKeyCheckTests
[Fact] public void Matches_SameKey_True()
[Fact] public void Matches_DifferentKeySameLength_False()
[Fact] public void Matches_DifferentLengthOrNull_FalseWithoutThrowing()

// ServerSnapshotIntakeTests
[Fact] public async Task Snapshot_MissingServiceKey_403_BodyNeverRead()
[Fact] public async Task Snapshot_WrongServiceKey_403_BodyNeverRead()
[Fact] public async Task Snapshot_ServiceKeyHeaderAnyCase_Accepted()
[Fact] public async Task Snapshot_InvalidReportId_400()
[Fact] public async Task Snapshot_OverOneMiB_413()
[Fact] public async Task Snapshot_RouteAndBodyIdsDiffer_400()
[Fact] public async Task Snapshot_BeforeBundle_StoredPending_NothingEnqueued_202()
[Fact] public async Task Snapshot_AfterBundle_SubjectMatches_StoredAndAppendEnqueued()
[Fact] public async Task Snapshot_AfterBundle_SubjectMismatch_NothingStored_202_WarningWithoutSubjects()
[Fact] public async Task Snapshot_Twice_FirstWriteWins()

// ReportWorkerTests
[Fact] public async Task CreateIssue_ComposesAndRecordsIssueNumber()       // RecordingGitHubIssueClient returns 7, report.json issueNumber 7
[Fact] public async Task CreateIssue_Redelivered_IsNoOp()                   // client called once across two ProcessWorkAsync calls
[Fact] public async Task CreateIssue_UnknownReport_ReturnsAndLogsError()
[Fact] public async Task CreateIssue_PendingSnapshotMatches_InBodyAndServerJsonAndViewerRerendered()
[Fact] public async Task CreateIssue_PendingSnapshotSubjectMismatch_IgnoredAndWarned()
[Fact] public async Task CreateIssue_RateLimited_Throws_IssueNumberStaysNull()
[Fact] public async Task AppendSnapshot_BeforeIssue_ThrowsReportNotReady()
[Fact] public async Task AppendSnapshot_AfterIssue_PostsOneComment_StateCommented()
[Fact] public async Task AppendSnapshot_AlreadyInBody_IsNoOp()
[Fact] public async Task AppendSnapshot_Redelivered_AfterCommented_IsNoOp()
[Fact] public async Task SnapshotRace_ArrivesDuringIssueCreation_EndsInExactlyOneComment()
// RecordingGitHubIssueClient runs a callback inside CreateIssueAsync that posts the snapshot through the relay.
// Drain the queue: one issue with no server row, one comment, state Commented

// RelayEndToEndTests
[Fact] public async Task EndToEnd_IntakeThenWorker_EveryBodyLinkResolvesToAStoredBlob()
// every https URL in the issue body maps back through PublicMediaBase to an existing Public path
[Fact] public async Task EndToEnd_ResendAfterIssueCreated_NoSecondIssue()
[Fact] public async Task EndToEnd_ProcessWork_UndefinedKind_Throws()       // (ReportWorkKind)99 -> ArgumentOutOfRangeException
```

- [ ] **Step 2: Run to verify failure**

Run: `build-slot --label e3-t7 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release --filter "FullyQualifiedName~ServiceKeyCheckTests|FullyQualifiedName~ServerSnapshotIntakeTests|FullyQualifiedName~ReportWorkerTests|FullyQualifiedName~RelayEndToEndTests"`
Expected: build FAIL.

- [ ] **Step 3: Implement per the two algorithms above**

- [ ] **Step 4: Run the whole relay test project**

Run: `build-slot --label e3-t7 -- dotnet test KhaozEngine.BugReports.Relay.Tests/KhaozEngine.BugReports.Relay.Tests.csproj -c Release`
Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.BugReports.Relay KhaozEngine.BugReports.Relay.Tests
git commit -m "feat(bugreports-relay): server snapshot merge and issue worker"
```

---

### Task 8: Azure Storage backend

**Files:**
- Create: `KhaozEngine.BugReports.Relay.Azure/KhaozEngine.BugReports.Relay.Azure.csproj`, `README.md` (opening paragraph), `IReportBlobContainer.cs`, `BlobContainerAdapter.cs`, `BlobReportMediaStore.cs`, `IReportQueueSender.cs`, `QueueSenderAdapter.cs`, `StorageQueueReportWorkQueue.cs`, `IQuotaTable.cs`, `QuotaTableAdapter.cs`, `TableReportQuotaStore.cs`, `PoisonQueueRequeuer.cs`
- Create: `KhaozEngine.BugReports.Relay.Azure.Tests/KhaozEngine.BugReports.Relay.Azure.Tests.csproj`, `InMemoryReportBlobContainer.cs`, `InMemoryQuotaTable.cs`, `RecordingQueueSender.cs`, `BlobReportMediaStoreTests.cs`, `TableReportQuotaStoreTests.cs`, `StorageQueueReportWorkQueueTests.cs`, `AdapterClassificationTests.cs`, `BugReportsAzureFactAttribute.cs`, `PoisonQueueRequeuerTests.cs`, `AzureLiveTests.cs`
- Modify: `Directory.Packages.props` (after line 95), `KhaozEngine.Tests/ArchitectureTests.cs` (`OptInBackends` gains `"BugReports.Relay.Azure"`, the package allowlist at line 141 becomes `["Azure.Storage.Blobs"] = { "Catalog.AzureBlob", "BugReports.Relay.Azure" }` and gains `["Azure.Storage.Queues"]` and `["Azure.Data.Tables"]` rows for `BugReports.Relay.Azure` with a comment that Azure.Identity still has no row), `KhaozEngine.Tests/ArchitectureTests.BugReportsRelay.cs`, `KhaozEngine.slnx`, `README.md` (catalog row and layout line)

**Interfaces:**
- Consumes: `IReportMediaStore`, `IReportWorkQueue`, `ReportWorkItemJson`, `IReportQuotaStore`, `QuotaCounter`, the two conformance suites from Task 1 (linked source).
- Produces (namespace `KhaozEngine.BugReports.Relay.Azure`):

```csharp
public sealed class BlobReportMediaStore : IReportMediaStore
{
    public BlobReportMediaStore(BlobContainerClient publicContainer, BlobContainerClient pendingContainer);
}
public sealed class StorageQueueReportWorkQueue : IReportWorkQueue
{
    public StorageQueueReportWorkQueue(QueueClient queue);   // sends ReportWorkItemJson.Serialize(item) as the message text
}
public sealed class TableReportQuotaStore : IReportQuotaStore
{
    public TableReportQuotaStore(TableClient table);         // PartitionKey = key, RowKey "q", properties Count (long), ExpiresAt
}
public sealed class PoisonQueueRequeuer
{
    public PoisonQueueRequeuer(QueueClient poison, QueueClient work, int maxRequeues = 3);
    public Task<int> RequeueAsync(CancellationToken cancellationToken);   // returns items moved back
}
internal interface IReportBlobContainer
{
    Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> bytes, ReportBlobHeaders headers, bool overwrite, CancellationToken ct);
    Task<ReadOnlyMemory<byte>?> DownloadAsync(string path, int maxBytes, CancellationToken ct);   // null on 404 only
    Task<bool> ExistsAsync(string path, CancellationToken ct);
}
internal interface IReportQueueSender { Task SendAsync(string text, CancellationToken ct); }
internal interface IQuotaTable
{
    Task<QuotaCounter?> GetAsync(string key, CancellationToken ct);                                   // null on 404 only
    Task<bool> AddAsync(string key, long count, DateTimeOffset expiresAt, CancellationToken ct);       // false on 409
    Task<bool> ReplaceAsync(string key, long count, string etag, DateTimeOffset expiresAt, CancellationToken ct); // false on 412
}
internal static class AzureStatus
{
    public static bool IsConflict(int status);   // 409 or 412
    public static bool IsAbsent(int status);     // 404
}
```

Each adapter is the only file naming SDK types besides the public constructors. Uploads set `BlobHttpHeaders.ContentType` and `CacheControl`, and a create-only upload uses `IfNoneMatch = ETag.All`. Downloads refuse a declared `ContentLength` over `maxBytes` with `InvalidDataException` before reading the body. Unlike the catalog adapter, read faults other than 404 propagate, because a worker must not treat a fault as an absent report. The queue sender passes the text unchanged, so the host's `QueueClientOptions.MessageEncoding` decides the wire encoding. A Functions queue trigger expects Base64 by default, and the README says so.

- [ ] **Step 1: Pin the SDK versions**

Add `Azure.Storage.Queues` and `Azure.Data.Tables` to `Directory.Packages.props`, each the newest stable 12.x release (`dotnet package search Azure.Storage.Queues --exact-match`), with a comment matching the Blobs one. They must resolve to the same `Azure.Core` as `Azure.Storage.Blobs` 12.29.2 with no NU1605 or NU190x warning.

- [ ] **Step 2: Write the failing tests**

The test csproj references `BugReports.Relay` and `BugReports.Relay.Azure`, the three SDKs and `Azure.Identity` (test only, for the live leg), and links `../KhaozEngine.BugReports.Relay.Tests/Conformance/*.cs` with `Link="Conformance/%(Filename)%(Extension)"`. The stores gain internal constructors over the internal seams for the doubles.

```csharp
// BlobReportMediaStoreTests
public sealed class BlobReportMediaStoreConformanceTests : ReportMediaStoreConformance   // over two InMemoryReportBlobContainer
[Fact] public async Task Put_CreateOnly_PassesOverwriteFalse()
[Fact] public async Task Put_PassesContentTypeAndCacheControl()
[Fact] public async Task Areas_MapToTheirContainers()          // Public writes reach only the public double

// TableReportQuotaStoreTests
public sealed class TableReportQuotaStoreConformanceTests : ReportQuotaStoreConformance
[Fact] public async Task Write_CreateOnly_UsesAdd_VersionUsesReplace()

// PoisonQueueRequeuerTests
[Fact] public async Task Requeues_poison_items_and_counts_them()
[Fact] public async Task Leaves_items_that_reached_three_requeues()

// StorageQueueReportWorkQueueTests
[Fact] public async Task Enqueue_SendsWorkItemJson()           // ReportWorkItemJson.Parse(sent) == item

// AdapterClassificationTests
[Theory, InlineData(409, true), InlineData(412, true), InlineData(404, false), InlineData(500, false)]
public void IsConflict(int status, bool expected)
[Theory, InlineData(404, true), InlineData(403, false), InlineData(500, false)]
public void IsAbsent(int status, bool expected)

[Fact] public void Constructors_NullClient_Throw()

// AzureLiveTests, each [BugReportsAzureFact]: skipped unless KE_BUGREPORTS_STORAGE names a storage account.
// Builds clients with DefaultAzureCredential, creates containers "bugreports-test-<8 hex>-public" and "-pending",
// queue "bugreports-test-<8 hex>" and table "bugreportstest<8 hex>", deletes all four in a finally.
[BugReportsAzureFact] public async Task Live_MediaStore_PassesConformance()
[BugReportsAzureFact] public async Task Live_QuotaStore_PassesConformance()
[BugReportsAzureFact] public async Task Live_Queue_RoundTripsWorkItem()
[BugReportsAzureFact] public async Task Live_Blob_StoresExplicitContentType()
```

Architecture facts added to `ArchitectureTests.BugReportsRelay.cs`:

```csharp
[Fact] public void BugReportsRelayAzure_ReferencesRelayAndTheThreeSdksOnly()
// ProjectRefs == { "BugReports.Relay" }, PackageRefs minus infra == { "Azure.Data.Tables", "Azure.Storage.Blobs", "Azure.Storage.Queues" }
[Fact] public void BugReportsRelayAzure_IsAnExplicitReferenceOutsideEveryUmbrella()
[Fact] public void BugReportsRelayAzureTests_ReferenceOnlyTheRelayAndBackend()
```

- [ ] **Step 3: Run to verify failure**

Run: `build-slot --label e3-t8 -- dotnet test KhaozEngine.BugReports.Relay.Azure.Tests/KhaozEngine.BugReports.Relay.Azure.Tests.csproj -c Release`
Expected: build FAIL.

- [ ] **Step 4: Implement the package per the Interfaces block**

`PoisonQueueRequeuer.RequeueAsync` reads up to 32 messages per call from the poison queue, re-enqueues each with `Requeues + 1` to the work queue when below the cap, then deletes it from the poison queue. Items at the cap stay in the poison queue and are logged once.

- [ ] **Step 5: Run to verify pass**

Run: `build-slot --label e3-t8 -- dotnet test KhaozEngine.BugReports.Relay.Azure.Tests/KhaozEngine.BugReports.Relay.Azure.Tests.csproj -c Release`
Expected: PASS with the four live facts skipped.
Run: `build-slot --label e3-t8-arch -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS.
Run: `bash scripts/check-doc-versions.sh`
Expected: ends with `all engine-version declarations match`.

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props KhaozEngine.BugReports.Relay.Azure KhaozEngine.BugReports.Relay.Azure.Tests KhaozEngine.Tests/ArchitectureTests.cs KhaozEngine.Tests/ArchitectureTests.BugReportsRelay.cs KhaozEngine.slnx README.md
git commit -m "feat(bugreports-relay): opt-in Azure Storage backend"
```

---

### Task 9: Documentation and release staging for E1 to E3

**Files:**
- Modify: `KhaozEngine.BugReports.Relay/README.md`, `KhaozEngine.BugReports.Relay.Azure/README.md`
- Modify: `docs/USING-KHAOZENGINE.md` (new section after "The session header" at line 16728), `docs/DEPENDENCY-SEAMS.md` (seam table rows near line 68 and a new "Bug report package edges" section after "Content catalog package edges"), `docs/SECURITY-BASELINE.md` (threat model), `docs/INDEX.md` (design table row at the top, line 47), `README.md` (catalog rows reviewed), `CHANGELOG.md`, `Directory.Build.props`, and every declaration `scripts/check-doc-versions.sh` names

**Interfaces:**
- Consumes: every public name in the contract and in Tasks 1 to 8.
- Produces: no code.

- [ ] **Step 1: Complete the two relay package READMEs**

Relay README: purpose, the three operations, a host sketch for an isolated-worker Functions app (HTTP `bug-reports` to `IntakeAsync`, HTTP `bug-reports/{reportId}/server` to `ServerSnapshotIntakeAsync`, queue trigger to `ProcessWorkAsync` through `ReportWorkItemJson.Parse`), the host's duties under D6 (`maxDequeueCount` 5, a daily `PoisonQueueRequeuer` run per D16, visibility backoff), the status codes, the folder layout, the pending prefixes with the retention rule (`snapshots/` 1 day, `accepted/` the report retention), the quota over-count note and the content type rule: every blob gets an explicit content type, validation guarantees image bytes match their type, and the only HTML served is the engine's viewer page. Azure README: constructor wiring with caller-built clients, the required RBAC roles, `QueueClientOptions.MessageEncoding = Base64` for a Functions trigger, and the `KE_BUGREPORTS_STORAGE` live leg. The new packages are opt-in and are not added to any umbrella package. No `net8.0` target: the first consumer hosts the relay on Flex Consumption with .NET 10 isolated.

- [ ] **Step 2: Write the USING section for the whole round**

Heading `## In-game bug reports (KhaozEngine.BugReports*, <version>)`. Subsections: Capture (E1 `FrameHistoryRecorder`, options table, `Suspend`, `Freeze`), Report and sections (E2 `IBugReportSection`, built-in keys, redaction), Client wiring (`BugReportService`, sinks, offline queue), Server snapshot (`BugReportServerCapture`, `ServerSnapshotPoster`), Relay host (this plan). Every snippet uses contract names only.

- [ ] **Step 3: Record the dependency edges and the threat**

`DEPENDENCY-SEAMS.md` gains seam table rows for `IReporterAuthenticator`, `IReportMediaStore`, `IReportWorkQueue`, `IReportQuotaStore` and `IGitHubIssueClient`, and an edge list for the five new packages written like the catalog list:

```text
KhaozEngine.BugReports.Relay       -> KhaozEngine.BugReports            (the viewer page, D3)
KhaozEngine.BugReports.Relay       -> KhaozEngine.BugReports.Contracts  (validation, wire records)
KhaozEngine.BugReports.Relay       -> KhaozEngine.Diagnostics           (ILogger)
KhaozEngine.BugReports.Relay       -> KhaozEngine.Http                  (HttpRetry for the token exchange)
KhaozEngine.BugReports.Relay.Azure -> KhaozEngine.BugReports.Relay      (the three seams it implements)
KhaozEngine.BugReports.Relay.Azure -> Azure.Storage.Blobs, Azure.Storage.Queues, Azure.Data.Tables (declared HERE, no Azure.Identity)
```

The E1 and E2 package edges are listed beside them from their csproj files. `SECURITY-BASELINE.md` gains a threat entry for bug report uploads (authentication before the body, quota before validation, bundle validation, capability URLs, constant-time service key, escaped viewer, identity from the token only) and its count sentence is updated.

- [ ] **Step 4: Index the round**

`docs/INDEX.md` gains a design row linking the spec and the contract and four plans, with status "Implemented for staged <version>, not released".

- [ ] **Step 5: Stage the engine minor version**

```bash
git fetch origin
git tag --sort=-v:refname | head -1
grep -n KhaozEngineVersion Directory.Build.props
```

When the version equals the newest tag (21.1.0 at planning time), set `<KhaozEngineVersion>` to the next minor (21.2.0). When an unreleased minor is already staged, ride it. When only an unreleased patch is staged, raise it to the next minor and fold its entry into the new heading. Update every PackageReference example `scripts/check-doc-versions.sh` reads in `README.md` and `docs/USING-KHAOZENGINE.md`. Add `## <version>` at the top of `CHANGELOG.md` with one-line bullets for E1 capture, E2 report core, client and screen, and E3 relay and Azure backend, keeping any bullets E1 or E2 already staged. Never run `scripts/tag-release.sh` and never create a tag.

- [ ] **Step 6: Sweep the docs**

Run: `git grep -n -w -e BugReportRelay -e BugReportService -e FrameHistoryRecorder -e BundleValidator -e GitHubAppIssueClient -e BlobReportMediaStore -- '*.md'`
Expected: every hit describes current behaviour.

- [ ] **Step 7: Run the guards and the full verification**

```bash
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
build-slot --heavy --label e3-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label e3-suite -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
build-slot --label e3-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: every guard exits 0, `check-doc-versions.sh` ends with `all engine-version declarations match <version>`, the build reports 0 warnings and 0 errors, the suite reports 0 failed, and format reports no changes.

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.BugReports.Relay/README.md KhaozEngine.BugReports.Relay.Azure/README.md docs/USING-KHAOZENGINE.md docs/DEPENDENCY-SEAMS.md docs/SECURITY-BASELINE.md docs/INDEX.md README.md CHANGELOG.md Directory.Build.props
git commit -m "release(<version>): stage in-game bug reports"
```

Stop here. Integration, the pack into `local-feed` and any tag belong to the orchestrator and the owner.
