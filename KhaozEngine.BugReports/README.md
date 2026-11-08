# KhaozEngine.BugReports

The headless in-game bug report model. It holds what a report carries and has no GPU, window or game
dependency, so a game server can reference it as readily as a client. It is opt-in and sits in no umbrella.
Add it explicitly.

It covers sections, drafts and bundles, the sinks, the offline queue, the frame viewer page and the server
snapshot. `KhaozEngine.BugReports.Client` builds a game client on top of it. The wire format and its validator live
in `KhaozEngine.BugReports.Contracts`.

Depends on `KhaozEngine.BugReports.Contracts`, `KhaozEngine.Diagnostics`, `KhaozEngine.Http` and
`KhaozEngine.Platform`. No third-party package.

## Sections

An `IBugReportSection` names one block of report state with a `Key` (`[a-z0-9][a-z0-9._-]{0,47}`, stored at
`sections/<key>.json`) and a `MaxBytes` budget, and writes exactly one JSON object.

`BugReportSectionSet` holds at most `MaxSections`, 64 sections, so a report's entries always leave room for the
screenshot and a clip. `Add` throws `InvalidOperationException` past it. It captures in two steps:

1. `CaptureAll(context)` runs on the thread that triggers the report and only isolates. Each section writes into
   its own capped buffer. A section that throws, writes more than 4 MiB, leaves a container open or writes anything
   but one object becomes an error object. The result is a list of `RawSection`, unredacted and untruncated, with
   `IsError` set on a failed one.
2. `BugReportSectionSet.Finish(raw, redactor, budget)` runs off that thread. Every property name and string passes
   through `ReportRedactor`. A raw section that is not one object, nests deeper than 64 levels or holds a string
   that does not decode becomes an error object naming which. An error message is cut to 512 UTF-16 units, and
   each section is cut structurally to the smaller of its own budget and what is left of the 1 MiB total. A
   section left with under 64 bytes is dropped with a warning. Once the redaction budget is spent, every remaining
   string is masked whole. `Finish` has no default budget. The client passes
   `BugReportSectionSet.DefaultFinishBudget`, 2 s.

A `RawSection` must pass through `Finish` before anything leaves the machine. Only `Finish` redacts.
`CapturedSection` is public so a viewer can hold sections read back from a bundle, and the bundle writer trusts a
hand-built one to be finished output. Keep `Write` cheap and write many
short strings. One huge string costs escape scratch before the capture cap applies, and the redactor masks whole
any input or intermediate string above 4,194,304 UTF-16 code units. Matching has no wall-clock deadline.

## Drafts and bundles

`BugReportDraft` holds what the trigger captured (id, game id, kind, capture time, finished sections, screenshot
and clip) and what the player edits (`Description`, `Category`, `IncludeScreenshot`, `IncludeClip`). Its setters
do not validate. `BugReportBundleWriter.Write(draft)` checks every field, writes a stored zip with the manifest and
its SHA-256 hashes, and throws when a field or a limit is broken (see [Refusals](#refusals)). Every bundle it writes
passes `BundleValidator.Validate`.

The viewer and relay share `BugReportLogTail.FromLogSection(json)` with a default limit of 50 lines. It reads the newest string
lines in their original order. Missing or malformed log data gives an empty tail.

## Refusals

A refused draft field, section, screenshot or clip throws `BugReportBundleArgumentException`, which derives from
`ArgumentException` and keeps `ParamName` as `draft`. A bundle that would break its entry or byte limit throws
`BugReportBundleLimitException`, which derives from `InvalidOperationException`. Both carry `Reason`, the rule, from the
one `BugReportBundleRefusal` enum. A null draft throws `ArgumentNullException`.

| `Reason` | What it refuses | Identifiers |
| --- | --- | --- |
| `ReportIdInvalid` | a report id that is not a canonical version 7 id, null included | `FieldName` |
| `GameIdInvalid` | a game id outside `[a-z0-9][a-z0-9-]{0,31}`, null included | `FieldName`, `MaximumCharacters` (32) |
| `ReportKindUndefined` | a kind `BugReportKind` does not define | `ActualKind` |
| `CaptureYearOutsideZipRange` | a capture time whose UTC year is outside the zip range | `ActualYear`, `MinimumYear` (1980), `MaximumYear` (2107) |
| `CategoryMissing` | a null category | `FieldName` |
| `CategoryLengthOutsideRange` | an empty or overlong category | `FieldName`, `ActualCharacters`, `MinimumCharacters` (1), `MaximumCharacters` (64) |
| `DescriptionBlank` | a null, empty or whitespace-only description | `FieldName` |
| `DescriptionTooLong` | a description over the limit once trailing whitespace is dropped | `ActualCharacters`, `MaximumCharacters` (4000) |
| `TextMalformed` | a category or description holding an unpaired surrogate | `FieldName` |
| `SectionListMissing` | a null section list | `FieldName` |
| `SectionMissing` | a null entry in the section list | `Index` |
| `SectionKeyInvalid` | a section key outside `[a-z0-9][a-z0-9._-]{0,47}`, null included | `Index`, `SectionKey` |
| `SectionKeyDuplicate` | a section key an earlier section uses, `Index` being the later one | `Index`, `SectionKey` |
| `SectionBytesExceeded` | sections whose running total passes the limit, `ActualBytes` being the total through the section that crossed it | `ActualBytes`, `MaximumBytes` (1048576) |
| `SectionJsonInvalid` | a section that is not one JSON object within the depth limit, with unique property names and decodable strings. `MaximumDepth` is the rule's limit, set on every such refusal whatever the cause, because the guard cannot tell which part of the section failed | `SectionKey`, `MaximumDepth` (64) |
| `ScreenshotMediaUnsupported` | a screenshot media type other than `image/png` or `image/jpeg`, null included | `MediaType` |
| `ImageDimensionsOutsideRange` | a screenshot or clip frame whose recorded width or height is outside the edge range | `EntryName`, `Width`, `Height`, `MinimumPixels` (1), `MaximumPixels` (2560) |
| `ImageHeaderUnreadable` | a screenshot or clip frame whose bytes do not start with a readable header of its media type | `EntryName`, `MediaType` |
| `ImageHeaderDimensionsMismatch` | a screenshot or clip frame whose header size differs from its record | `EntryName`, `Width`, `Height`, `HeaderWidth`, `HeaderHeight` |
| `ClipFrameListMissing` | a clip whose frame list is null | none |
| `ClipFrameCountExceeded` | a clip over the frame limit | `ActualCount`, `MaximumCount` (120) |
| `ClipFrameMissing` | a null entry in the clip frame list | `Index` |
| `ClipOffsetDecreased` | a clip frame that starts before the frame ahead of it, or the first before the clip | `Index`, `ActualMilliseconds`, `MinimumMilliseconds` |
| `ClipOffsetExceeded` | a clip frame that starts past the offset limit | `Index`, `ActualMilliseconds`, `MaximumMilliseconds` (600000) |
| `RenderTimeNonfinite` | a clip frame whose render time is NaN or infinite | `Index`, `ActualMilliseconds` |
| `RenderTimeOutsideRange` | a clip frame whose finite render time is negative or over the limit | `Index`, `ActualMilliseconds`, `MinimumMilliseconds` (0), `MaximumMilliseconds` (60000) |
| `EntryCountExceeded` | a bundle needing more zip entries than the limit, the manifest included (`BugReportBundleLimitException`) | `ActualCount`, `MaximumCount` (128) |
| `PayloadBytesExceeded` | a bundle whose entries after the manifest total more than the limit (`BugReportBundleLimitException`) | `ActualBytes`, `MaximumBytes` (16777216) |
| `ArchiveBytesExceeded` | a bundle whose written archive, zip framing included, is longer than the limit (`BugReportBundleLimitException`) | `ActualBytes`, `MaximumBytes` (16777216) |

`FieldName` is the `BugReportDraft` property: `ReportId`, `GameId`, `Category`, `Description` or `Sections`.
`EntryName` is the bundle entry the image would be written as, such as `screenshot.png` or `clip/002.jpg`. `Width` and
`Height` are the recorded size in pixels, and `HeaderWidth` and `HeaderHeight` the size the encoded header holds.
`Index` is the position in the section list for a section reason and in the clip frame list for a clip frame reason.
`MinimumMilliseconds` for `ClipOffsetDecreased` is the offset of the frame ahead, or zero for the first frame.

## Sinks

An `IBugReportSink` delivers one bundle. Every delivery outcome is a `BugReportSubmitResult` (`Sent`, `Queued` or
`Rejected`), never an exception. Only the caller's own cancellation throws. A sink that breaks this rule is
treated as a failed send, and its bundle stays queued.

| Sink | Does |
| --- | --- |
| `HttpRelaySink(http, relayBase, bearerToken)` | Posts to `{relay}/bug-reports` with the bearer token and `Idempotency-Key`, retrying through `HttpRetry`. Sent only for a 2xx whose body is an `IntakeAccepted` echoing the report id. Any other 2xx is Queued with `unconfirmed`. Rejected only for a 400, 403, 413 or 422 whose body is a `RelayError`. No token, a transport fault and every other status, 401, 404 and 5xx included, are Queued, a 408, 429 or 5xx with any `Retry-After` hint. The token delegate runs on the submitting thread, a thread pool thread under `BugReportService`, so it must be thread-safe. |
| `LocalFolderSink(root, openViewer)` | Validates the bundle and writes it, its entries and `viewer.html` to `<root>/<reportId>/`, then opens the page when asked. An invalid bundle is Rejected with the validator's error. For development and offline play. |

`BugReportSubmitter` puts a sink and the queue together and is what the client uses. Every Rejected result, from a
direct send or a flush, logs one warning with the report id and the error, because the report is deleted.

## Offline queue

`BugReportOfflineQueue(directory, time)` keeps bundles on disk. `BugReportSubmitter` writes each bundle there before
its first send and deletes it only when the sink returns Sent or Rejected, so a crash, a shutdown or an unreachable
relay keeps it. A report made while signed out is stored with no subject and binds to the first account that
flushes. `FlushAsync(subject, sink, token)` sends that account's reports and every unbound one, oldest first, and
returns a `BugReportFlushResult` with how many were sent and how many were rejected. The queue holds at most 3
reports or 30 MB, dropping the oldest, and expires reports after 7 days.

If the queue cannot write a signed-in submission, the submitter sends the bundle directly. Sent and Rejected
remain final results. An unsent result carries `queue_failed` because no durable copy could be kept.
A signed-out submission is never sent directly.

## Viewer

`BugReportViewerPage.Render(model)` returns one self-contained HTML page with the screenshot, a clip player, a frame
time graph with hitches marked, the sections, the server snapshot and a log tail. Media load by relative path from
the folder beside the page. Every byte of report data is treated as untrusted, because a relay renders the same page
from uploaded bundles. `LocalFolderSink` and the relay share it.

## Server snapshot

A server-backed game captures its own view of the player under the same report id. The game owns the messages that
carry the id from client to server. `BugReportService.ReportCaptured` requests capture, and
`BugReportService.ReportConfirmed` permits posting only when the player presses Send.

```csharp
var capture = new BugReportServerCapture(TimeProvider.System);
capture.Add(new PlayerStateSection(world));
var poster = new ServerSnapshotPoster(http, relay, serviceKey);
var pending = new Dictionary<(string Subject, string ReportId), ServerSnapshot>();

// On the simulation thread, when a client's capture message arrives:
void OnBugSnapshotRequest(Session session, string reportId)
{
    if (!ReportIds.IsValid(reportId)) return;
    string subject = session.AuthenticatedSubject;
    if (!capture.TryAccept(subject)) return;
    pending[(subject, reportId)] = capture.Capture(reportId, subject);
}

// On the simulation thread, only after that client confirms Send:
void OnBugSnapshotConfirmed(Session session, string reportId)
{
    if (!ReportIds.IsValid(reportId)) return;
    if (!pending.Remove((session.AuthenticatedSubject, reportId), out var snapshot)) return;
    _ = Task.Run(() => poster.PostAsync(snapshot, CancellationToken.None));
}
```

- Check `ReportIds.IsValid` and call `TryAccept` before any capture work. `TryAccept` applies the per-account
  hourly limit (5 by default) and is thread-safe.
- The subject must be the account the server authenticated for that connection, never a value the client sent.
  The relay merges a snapshot only when its subject equals the subject of the reporter's token.
- `Capture` runs the sections synchronously on the simulation thread, so keep them within the tick budget.
- Bound the game's pending snapshot store and expire entries that are never confirmed. Do not post on capture.
- `PostAsync` redacts, serializes and posts off that thread with the service key. A failed post returns false with
  one warning. A report without the server view is still a report.

## Media records

| Record | Holds |
| --- | --- |
| `ReportImage` | One encoded still image: `MediaType` (`"image/png"` or `"image/jpeg"`), `Width`, `Height` and `Bytes`. |
| `ReportClipFrame` | One JPEG clip frame: `Offset` from the first frame, `RenderMilliseconds`, its own `Width` and `Height`, and `Jpeg`. |
| `ReportClip` | The frozen clip, oldest frame first. `Frames` is empty, never null, when nothing was recorded. |

Each clip frame keeps its own size, so a window resize during recording leaves frames of two sizes in one
clip. `RenderMilliseconds` is the frame loop's unclamped delta, not the averaged and clamped `FrameStats`
figure, so a hitch shows at its real length.

`KhaozEngine.BugReports.Client` produces these records from a running window.
