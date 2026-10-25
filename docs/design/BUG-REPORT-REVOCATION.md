# Bug report revocation

Status: implemented relay operation. [Bug reports](BUG-REPORTS.md) owns intake and evidence capture.
The [relay README](../../KhaozEngine.BugReports.Relay/README.md#host-duties) owns host integration.

## Owner contracts

| Id | Contract and reason |
| --- | --- |
| RV1 | Reusable cleanup lives in BugReportRelay. A host adds only an authorized operator trigger. |
| RV2 | Completed cleanup removes public media, private personal records and the issue. Only non-personal tombstone/reference state remains for lifecycle expiry. |
| RV3 | The documented Functions adapter uses Admin authorization and an existing operator login to obtain its master key at call time. RevokeRequestAsync itself does not authenticate operators. |
| RV4 | Immediate public cleanup is followed by one delayed purge after the claim lease, so already-running holders cannot leave their within-lease writes behind. |
| RV5 | Reuse existing media and queue resources. The operation requires no new resource or always-on service. |
| RV6 | Relay redacts/closes, while an identity allowed to delete issues performs deletion. Editing leaves GitHub history, so App Issues write alone cannot complete personal-data removal. |

## Resolution and urgent cleanup

`RevokeReportAsync(idOrReference)` trims input. Reference codes normalize case and Crockford O/I/L
confusables, then resolve through the private reference index. Codes contain only 30 ID bits and may
collide. Discard candidates without a record, accepted index or tombstone. No match is NotFound and
multiple matches are Ambiguous with candidate IDs. IDs accept pasted uppercase by canonicalizing
before validation. Invalid input cannot become a storage path.

Intake writes the empty reference index entry before the accepted index. A failed acceptance may leave
an orphan entry, but lookup validates candidates. Repeat intake writes the same index path. Reports
accepted before reference indexing remain revocable by ID.

For one resolved report, write its create-only tombstone, delete every public blob in its known folder,
and redact/close a known issue. Recover the issue number from the CreateIssue result marker when a
worker saved it there but not in the record. Schedule PurgeRevoked after the ten-minute lease plus two
minutes. All these steps are idempotent. A tombstone with neither private record nor accepted index
returns AlreadyRevoked.

`RevokeAsync(reportId)` remains a tombstone-only primitive. Callers needing full cleanup use
RevokeReportAsync or the host's authorized RevokeRequestAsync adapter.

## Purge, ordering and failure

Purge takes no work claim and does nothing without a tombstone. It repeats public deletion and issue
redaction, then deletes records, accepted index, snapshot and issue/comment claim files. Claim epochs
are removed newest first, with result/release markers before their claim. An interruption can resume
without losing the saved issue number prematurely.

The tombstone and reference index remain until host lifecycle expiry, so a repeat by reference can
return AlreadyRevoked. Neither retains personal report contents. Tombstones and a missing public
manifest prevent fresh issue, comment, snapshot and viewer publication.

The urgent call reports an issue redaction failure as Revoked with issue outcome Failed and still
schedules purge. Caller cancellation propagates. An HTTP timeout with an uncancelled caller token is
a redaction failure. Purge redacts before deleting private state and throws on that failure for queue
redelivery. Gone and Moved do not block purge. A transferred issue is not edited at its new location.
Its destination URL is returned for the operator.

Known issue numbers carry a page URL when the GitHub client can construct one, including Failed and
Gone. A returned update/transfer URL takes precedence over IssuePageUrl. The host must delete the
returned issue under its own authorized identity. A late issue found only during purge is redacted
and logged for operator deletion, including a transferred destination. The operator's earlier result
cannot include an issue that did not yet exist.

A holder that passed its revocation check can still finish within its lease. Purge repeats cleanup
after that interval. Work beyond the lease and GitHub's image-proxy cache are outside this guarantee.
Neither immediate success nor issue redaction proves permanent issue deletion.

## Request and seam compatibility

The operator request is exactly one JSON string property, report, within 1024 bytes. Forward the unread
request and complete relay response through the host adapter. Status is 200 for Revoked/AlreadyRevoked,
404 for NotFound, 409 for Ambiguous, 400 for Invalid/malformed JSON and 413 above the byte cap.

List/delete, delayed enqueue and issue-update seam members have compatibility defaults that throw
NotSupportedException naming the member. Engine in-memory/Azure implementations override them.
IssuePageUrl may return null. Azure rounds a positive queue delay up to whole seconds and adds seven
days to that rounded delay for message lifetime, retaining a full delivery window.
