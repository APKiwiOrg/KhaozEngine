# Bug report revoke: design

- **Date:** 2026-10-19
- **Status:** design approved and implemented for 21.8.0
- **Base design:** [In-game bug reports](2026-10-17-bug-reports-design.md), section Media access
- **Related:** https://github.com/APKiwiOrg/KhaozEngine/issues/1510 (this change), Grimhollow
  https://github.com/APKiwiOrg/Grimhollow/issues/588 (the consumer gap)

## Goal

A player asks for a report to be deleted, usually by quoting the reference code from the toast. The operator runs
one command. The report then disappears from every public link, its private record goes, its issue is redacted and
deleted, and no late relay work can publish it again.

Before 21.8.0, `BugReportRelay.RevokeAsync` only wrote the private tombstone. Public folder deletion and issue
cleanup were manual, and no reference index or host operator path existed.

## Owner rulings

| Id | Ruling |
| --- | --- |
| RV1 | The reusable operation lives in the engine relay. Hosts add one thin operator trigger. |
| RV2 | A completed revoke removes everything personal: public files, private record files and the issue. Only non-personal relay state remains (listed below). |
| RV3 | The operator reaches the relay through an HTTP trigger at Functions `AuthorizationLevel.Admin`. The operator script fetches the master key with the operator's own Azure login at call time. No new secret. |
| RV4 | The call does the urgent work and answers at once. One delayed purge job finishes after the claim lease. |
| RV5 | No new Azure resource, SKU or always-on component. Implementation stops and asks if that changes. |
| RV6 | The relay redacts and closes the issue. The operator script then deletes it with the operator's own `gh` login, because an edit leaves the original title and body in the issue's history. The GitHub App keeps Issues write only. |

## State before 21.8.0

- One public folder per report, `<gameId>/<yyyy-mm>/<reportId>-<token>/`, with a 128-bit token. Its path is held
  only in the private `accepted/<reportId>.json` and `records/<reportId>.json` and in the issue's links.
- `ReportIds.ReferenceCode` encodes 30 bits of the id as `R-` plus six Crockford base32 characters. It is not
  invertible and nothing indexes it, so two reports can share a code.
- Intake, server snapshot intake, viewer writes and the worker all honour the tombstone. A holder that passed its
  check can still finish a write inside the 10-minute claim lease (`WorkClaim.LeaseMinutes`).
- `IReportMediaStore` can put, get and check existence. It cannot list or delete.
- `IReportWorkQueue` cannot delay an item. `IGitHubIssueClient` can create an issue and add a comment.
- The issue title carries the player's description. The body carries the description, the reporter's display name,
  handle and account subject, and links into the public folder. The server snapshot comment carries the reference,
  server facts and links, with no reporter identity.

## Design

### Operation

`BugReportRelay.RevokeReportAsync(string idOrReference, CancellationToken)` returns a `ReportRevocationResult`. The
existing `RevokeAsync(reportId)` stays as the tombstone-only primitive and its documentation points at the new
operation.

The call runs these steps in order. Each is idempotent.

1. **Resolve.** A value that starts with `R-` is a reference code. It is validated and looked up through the
   reference index. Candidates that have no accepted index, record or tombstone are discarded. No candidate gives
   `NotFound`. More than one gives `Ambiguous` with the candidate ids. Any other value must pass the existing
   report id validation, otherwise the call returns `Invalid`. A valid id with no accepted index, record or
   tombstone gives `NotFound`.
2. **Tombstone.** Write `revoked/<reportId>.json` through the existing primitive. A report whose tombstone already
   exists and whose private record and accepted index are gone answers `AlreadyRevoked` and does nothing more.
3. **Delete the public folder.** The folder comes from the record, or from the accepted index when the record does
   not exist yet. Every blob under it is listed and deleted.
4. **Redact the issue.** Use the record's `IssueNumber`, or else the `CreateIssue` claim result marker. When a
   positive number exists, update the issue: title
   `Revoked bug report R-XXXXXX`, body `This report was revoked at the reporter's request. Its files and private
   record are being deleted.`, state closed with reason not planned. Labels are kept.
5. **Schedule the purge.** Enqueue `ReportWorkItem(ReportWorkKind.PurgeRevoked, reportId)` visible after the claim
   lease plus a two-minute margin.
6. **Answer.** `Revoked` with the report id, the reference, the count of deleted public blobs, the issue outcome,
   the issue URL when one exists and the purge time.

### Purge

`ProcessWorkAsync` handles `PurgeRevoked` without a work claim. Every purge step is idempotent, so concurrent
deliveries are harmless and no purge claim files exist.

1. If the tombstone does not exist, complete without touching anything. A purge can never remove an unrevoked
   report.
2. Delete every blob under the public folder again.
3. Reload the record. Use its `IssueNumber`, or else the `CreateIssue` claim result marker, and redact that issue
   again as in step 4 above. This covers a worker that passed its revocation check before the call and created
   the issue inside the lease. An issue the operator already deleted answers `Gone`.
4. Delete the private files for the id: `records/<id>.json`, `accepted/<id>.json`, `snapshots/<id>.json` and the
   claim files of the `CreateIssue` and `AppendServerSnapshot` kinds. `WorkClaim.DeleteAllAsync` counts the
   contiguous epochs and deletes from the highest down, with each epoch's result and release markers before its
   claim. It does not list claims because their names are not folder-shaped. Interrupted cleanup can resume.

Retained, all non-personal, and aged out by the existing 180-day lifecycle rules: the tombstone, the reference
index entry (so a repeat by code answers `AlreadyRevoked` rather than `NotFound`).

### Reference index

Intake writes a JSON blob holding `{}` at `references/<lowercase code>/<reportId>.json` in the pending area before
it creates the accepted index. Lowercase paths and the `.json` extension satisfy the media path and header rules.
Writing it first keeps it present when acceptance loses a race or fails after the write, and a duplicate intake
of the same id rewrites the same name. `ReportLayout` owns the path and the code validation.

### Issue outcomes

`IGitHubIssueClient.UpdateIssueAsync(int issueNumber, GitHubIssueUpdate update, CancellationToken)` returns one of:

| Outcome | Meaning |
| --- | --- |
| `Updated` | The edit and close landed. |
| `Gone` | GitHub answered 404 or 410. The issue was deleted or the App cannot see it. |
| `Moved` | The issue was transferred. The client detects this from a 301 or from a final request URI that differs from the one it sent, does not edit at the new location and returns the new URL. |

Any other failure throws `GitHubApiException` as the existing calls do. The result's issue outcome is `Redacted`,
`NotCreated`, `Gone`, `Moved` or `Failed`.

### Failure rules

- A failed redaction during the call answers `Revoked` with issue outcome `Failed`. The purge retries it.
- The purge redacts before it deletes the record, so the issue number is never lost. A failed redaction throws, the
  queue redelivers, and the existing poison requeue picks up an exhausted item.
- `Gone` and `Moved` do not block the purge. The call's answer carries them so the operator can act.
- A late issue created inside the lease is redacted and closed by the purge and logged at warning level with its
  number. The operator script does not see it. This needs a revoke within seconds of the report being sent.

### Seams and compatibility

| Seam | New member | Engine implementations |
| --- | --- | --- |
| `IReportMediaStore` | `ListAsync(ReportArea, string prefix, CancellationToken)` and `DeleteAsync(ReportArea, string path, CancellationToken)` | in-memory, Azure Blob |
| `IReportWorkQueue` | `EnqueueAsync(ReportWorkItem, TimeSpan visibleAfter, CancellationToken)` | in-memory honours `TimeProvider`, Azure Queue sets the visibility timeout and a time to live of the delay plus seven days, preserving a full delivery window |
| `IGitHubIssueClient` | `UpdateIssueAsync` as above | `GitHubAppIssueClient` |
| `ReportWorkKind` | `PurgeRevoked` | the worker |

The new interface members are default-implemented and throw `NotSupportedException` naming the member, following
the `IGpuDevice` precedent in [DEPENDENCY-SEAMS.md](../../DEPENDENCY-SEAMS.md). Every engine implementation overrides
them, so only a third-party implementer can reach a default, and the release stays an additive minor.

## Host integration

These live in the consumer repositories, not in this engine change. They are recorded here so the engine API serves
them exactly.

- **Trigger.** `BugReportRevoke` is an HTTP POST at `bug-reports/revoke` with `AuthorizationLevel.Admin`. The body is
  `{"report": "<code or id>"}`, capped at 1 KB. It answers 200 for `Revoked` and `AlreadyRevoked`, 404 for
  `NotFound`, 409 for `Ambiguous` and 400 for `Invalid`. The trigger forwards the unread request through the
  host's intake request adapter to `BugReportRelay.RevokeRequestAsync(RelayRequest, CancellationToken)` and
  forwards its status, JSON and headers. The engine parses exactly one string property named `report`, returns
  400 for malformed bodies and 413 above 1024 bytes, and maps the revocation result to those status codes. The
  host must authorize the operator before calling it. It goes into the game-template seed first and Grimhollow
  mirrors it.
- **Operator script.** `scripts/revoke-bug-report.sh <code-or-id>` reads the master key into a variable with
  `az functionapp keys list`, never prints it, posts the request and prints the result. When the result carries an
  issue URL, or the new URL of a `Moved` issue, it deletes that issue with `gh issue delete --yes`, whatever the
  redaction outcome. It hard-codes no game name, so the template owns it.
  Exit codes: 0 revoked or already revoked, 2 not found, 3 ambiguous, 1 anything else.
- **Lifecycle.** The template-owned `bug-reports.bicep` gains a 180-day rule for `pending/references/`.
- **Docs.** The template and Grimhollow host READMEs and Grimhollow `docs/design/BUG-REPORTS.md` replace the manual
  steps with the script. The latter also corrects the folder path to include `<yyyy-mm>/`.

## Security

- The master key already grants every Functions admin API, so the trigger adds no new authority. The key is only
  fetched for one call by an operator who holds the Azure role to read it.
- The anonymous routes and their authentication are unchanged.
- Resolution only accepts a validated reference code or report id, so a request cannot name an arbitrary path.
- Deletion of the issue runs under the operator's own GitHub identity. The App is never granted delete rights.

## Cost

No new resource. One more function in the existing Flex Consumption app that bills only per run, one empty index
blob per accepted report, one delayed queue message and a few list and delete calls per revoke, one lifecycle rule,
and a few log lines. All of it rounds to a fraction of a cent a month at expected volume.

## Testing

Relay core in `KhaozEngine.BugReports.Relay.Tests`, on the in-memory stores and a held `TimeProvider`, with no
sleeps:

- resolution by code, by id, unknown, ambiguous with two ids forced onto one code, invalid
- the index is written before the accepted index and survives a lost acceptance race
- the call writes the tombstone, deletes every public blob, redacts an existing issue, answers with the counts and
  schedules the purge at the lease plus the margin
- a holder that passed its check before the call lands a public write and creates the issue, and the purge deletes
  the write and redacts the issue
- a failed redaction leaves the call `Revoked` with `Failed`, the purge throws before deleting private files, and a
  later delivery completes
- `Gone` and `Moved` complete the purge and are reported
- a purge with no tombstone changes nothing
- repeating the call and the purge is safe, and a repeat after the purge answers `AlreadyRevoked`
- conformance for list, delete and delayed enqueue on the in-memory stores

Azure backend in `KhaozEngine.BugReports.Relay.Azure.Tests`: list and delete paths and the queue visibility timeout.
GitHub App client: the PATCH request shape and the `Gone` and `Moved` mapping through a fake HTTP handler.

Relay core tests pin `RevokeRequestAsync` parsing, the 1024-byte cap and status mapping. Host tests in the consumer
repositories pin the Admin authorization level, route by attribute and forwarding to the engine request API.
The script passes `bash -n` and shellcheck, and a test asserts it never echoes the key.
A live revoke against a deployed relay belongs to the consumer's deployment task.

## Non-goals

- Revoking every report of one player by account. It would need a subject index, which is itself personal data.
- Editing the server snapshot comment. It carries no reporter identity and the issue is deleted.
- GitHub's image proxy may serve a cached screenshot briefly after the blob is deleted. The relay cannot clear it.
- Notifying the player. The operator answers the request through the channel it arrived on.

## Documentation and release

- The relay README revoke section, the base design's Media access section, the seams table in the base design, the
  `INDEX.md` row and the CHANGELOG.
- Ships as additive minor 21.8.0 after owner approval of the exact tag, through the engine release ritual. The
  template change and the Grimhollow adoption follow the release.
