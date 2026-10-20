# In-game bug reports: design

- **Date:** 2026-10-17
- **Status:** design approved in conversation, written spec awaiting owner review
- **Plans:** `docs/superpowers/plans/2026-10-17-bug-reports-contract.md` and the E1, E2 and E3 plans beside it
- **First consumer:** Grimhollow, whose game-side spec is `docs/superpowers/specs/2026-10-17-in-game-bug-reports-design.md`
  on its `feature/bug-reports-design` branch. It covers hosting, infrastructure, cost and the game and server sections.
- **Related:** https://github.com/APKiwiOrg/KhaozEngine/issues/1428 (this round), Grimhollow https://github.com/APKiwiOrg/Grimhollow/issues/559 (adoption)

## Goal

A player presses one key anywhere in a game and the engine locks in what just happened: a screenshot, the last five
seconds as a clip, engine state and game state. The player adds one line of description and confirms. The report
arrives as a GitHub issue in a private triage repository with the reporter's identity, a playable clip and every state
section a developer needs, without the player ever holding a GitHub credential.

The engine owns capture, the report model, the bundle format, the confirmation screen, submission, the offline queue,
the server snapshot API and the relay that turns a bundle into an issue. Each game owns its key choice, its game and
server sections, its sign-in token, its relay hosting and its text.

## Owner rulings this design follows

- **BR1 Reporters:** every signed-in player in shipped builds, not only testers.
- **BR2 Destination:** a private triage repository chosen by the consumer, labelled by game and by source. Players never
  see the issue. They get a short reference code.
- **BR3 Clip delivery:** a static frame viewer page stored beside the media. No server-side transcoding and no ffmpeg.
- **BR4 Server view:** server-backed games attach the server's authoritative snapshot of the reporter in the first
  version.
- **BR5 Relay:** an engine library that each game hosts. The relay core is host-agnostic so a game can move it between
  hosts without a client or bundle change.
- **BR6 Trigger:** F10 by default, rebindable, active on every screen. It locks in the evidence at once, then a
  confirmation screen opens. Nothing leaves the machine before the player confirms.
- **BR7 Identity:** the reporter is whoever the consumer's session token says. There is no anonymous path. A report made
  before sign-in is held, then sent after the next successful sign-in and attributed to that account. There is no
  save-to-file option for players.
- **BR8 Always included:** reporter identity and game and system information are always included and the screen says
  so. Game state is not personal data. The screenshot and the clip each have a toggle.
- **BR9 Recording default:** the rolling clip is on by default with a settings toggle that removes the clip's capture, readback and encode cost. A standby screenshot tap keeps swapchain copy-source usage on, so F10 still gets a screenshot.
- **BR10 Clip codec:** JPEG frames in the first version. The bundle, relay and viewer accept `clip.mp4` from the first
  version so hardware H.264 can land as the next engine phase without a format change.
- **BR11 Limits:** 5 reports per hour and 20 per day per account, 100 per day across all accounts, 16 MB per report.
  Media retention is the host's storage policy (Grimhollow uses 180 days).
- **BR12 Work queue:** a durable queue sits between intake and issue creation so a GitHub outage never fails a player's
  submit.
- **BR13 Screenshot format:** long edge at most 2560 px, PNG when it encodes under 4 MB, JPEG quality 92 otherwise.
- **BR14 One text field:** "What happened?" is the only required field. The issue title is its first line.

## Non-goals

- No anonymous submission and no player save-to-file (BR7).
- No video codec in this round. Hardware H.264 is phase E4 (BR10).
- No automatic crash reporting in this round. The report kind reserves `Crash` so a later round can send the existing
  `CrashReport` through the same pipeline.
- No IME in the description box. `TextArea` takes committed text, like `TextInput`.
- No duplicate grouping or fingerprinting.
- No media stored in GitHub. The Issues API has no attachment upload.
- No operating system screen capture. It would prompt players for Screen Recording permission on macOS and capture
  overlapping windows.
- No issue access for players. The reference code is their handle.

## Packages and dependency edges

| Package | Contents | Depends on |
| --- | --- | --- |
| `KhaozEngine.Gpu` | `GpuReadbackRing` | existing |
| `KhaozEngine.Windowing` | frame tap in `FramePhases`, swapchain usage flags on request | existing |
| `KhaozEngine.Imaging` | `JpegWriter`, `JpegThumbnailDecoder` | BCL only |
| `KhaozEngine.Diagnostics` | `RecentLogSink`, `ReportRedactor` | existing |
| `KhaozEngine.Gui` | `TextArea` | existing |
| `KhaozEngine.Game` | `IGameOverlay` overlay seam, `GameApp.AddOverlay` | existing |
| `KhaozEngine.BugReports.Contracts` (new) | manifest and index schema, limits, entry names, format version | BCL only |
| `KhaozEngine.BugReports` (new) | model, sections, bundle writer, sinks, offline queue, viewer page renderer, server capture, `ServerSnapshotPoster` | Contracts, Diagnostics, Http, Platform |
| `KhaozEngine.BugReports.Client` (new) | `FrameHistoryRecorder`, `BugReportService`, `BugReportScreen`, the trigger action, built-in client sections | BugReports, Gpu, Windowing, Gui, Game, Imaging |
| `KhaozEngine.BugReports.Relay` (new) | intake, quotas, validation, issue composer, GitHub App client, workers | Contracts, BugReports, Http, Diagnostics |
| `KhaozEngine.BugReports.Relay.Azure` (new, opt-in backend) | blob media store, queue, table quota store, poison requeuer | Relay, Azure.Storage.Blobs, Azure.Storage.Queues, Azure.Data.Tables |

`KhaozEngine.BugReports` must stay headless so a server can reference it without windowing or GUI. The relay core has
no ASP.NET Core or Azure Functions dependency. Its operations take a body `Stream`, request headers and a cancellation
token and return a result record, and each host adapts that in a few lines. Azure lives only in the opt-in backend, per
`docs/DEPENDENCY-SEAMS.md`.

## Capture pipeline

### Frame tap

`FramePhases.Run` records the whole frame, interface included, into the swapchain framebuffer, then submits and
presents. When a capture consumer is registered, it records one more step after `onFrame` and before `Submit`: a
downscaled copy of the swapchain image into a capture texture, then a copy of that into a staging slot. The image is the
final composite, so the clip shows the interface.

Swapchain images need copy-source usage, and sampled usage where the downscale pass samples them. The flags are set
only while a consumer is registered. Metal needs `framebufferOnly = false`, Vulkan needs `TRANSFER_SRC` (and
`SAMPLED` if sampled), D3D11 already allows the copy. The exact downscale route per backend is a plan decision. Channel
order is normalized to RGB before encoding.

The tap runs on a wall-clock interval (default 83 ms for 12 fps), not every Nth frame. A frame with
`RenderSuppressed` captures nothing, and the clip's timestamps show the gap. A resize recreates the capture targets and
recording continues. Frames in one clip can therefore differ in size, and the index records each frame's size.

### Non-blocking readback

`GpuReadbackRing` owns three staging textures at capture size. Each copy is submitted with an `IGpuFence`. Later frames
poll the fences and map only completed slots. It never calls `WaitForIdle`. When no slot is free, that capture tick is
dropped. The tap never stalls a frame.

A device whose `GpuCapabilities.SupportsCompletionFences` is false gets no rolling clip. The screenshot still works on
it through the existing blocking `GpuReadback`.

### Encoder

`JpegWriter` is a BCL-only baseline JPEG encoder beside `PngWriter`. One background worker encodes from a bounded queue
of two mapped frames and drops the oldest when it falls behind. Encoding never runs on the frame thread. The plan
measures the per-frame encode cost on each platform rather than assuming one.

### Frame history recorder

`FrameHistoryRecorder` keeps a ring of encoded frames, each with its capture time and that frame's unclamped render time from
the frame loop, so the viewer can show hitches.

| Option | Default |
| --- | --- |
| Duration | 5 s |
| Rate | 12 fps |
| Long edge | 960 px |
| JPEG quality | 70 |
| Hard byte cap | 8 MB |

Frames live in memory only. Nothing is written to disk unless the player confirms a report that then has to be queued.

- `Suspend()` and `Resume()` let a game pause the ring while its pause menu is open, so choosing Report bug from that
  menu still gets the five seconds before the menu opened.
- `Freeze()` returns an immutable clip and keeps the ring stopped until the confirmation screen closes, so the screen
  itself is never recorded.

### Screenshot

On trigger, the tap of that same frame also records a full-resolution copy of the swapchain image, and the engine reads
it back with one blocking readback after submit. One hitch at the moment of reporting is accepted. The image is
downscaled to a 2560 px long edge when larger and encoded per BR13 on the background worker.

## Report model and sections

`BugReportDraft` holds:

- `ReportId`, a time-ordered UUID v7 (`Guid.CreateVersion7()`) generated at trigger, so the server snapshot and the client bundle share it before the player has
  typed anything
- game id, kind (`Bug` now, `Crash` reserved), captured-at time
- description, category
- the screenshot, the frozen clip and the captured sections

The engine ships a default category list (Gameplay, Visual, Audio, Performance, Interface, Other). A game may replace it.

### Sections

`IBugReportSection` has a `Key`, a `MaxBytes` and a write method that receives a `Utf8JsonWriter` and a
`BugReportContext`. Sections register on `BugReportService` and are captured at trigger time on the main thread, so
they describe the moment of the bug.

- A section that throws writes `{ "error": "<type>: <message>" }` and the report continues.
- A section over its `MaxBytes` is truncated with a `"truncated": true` marker.
- Total section state is capped at 1 MiB.

Built-in client sections:

| Key | Contents |
| --- | --- |
| `build` | game name, version, build name, engine version, configuration, commit when `BuildMetadata` has one |
| `platform` | OS, architecture, runtime, CPU count, RAM, locale, display, DPI |
| `graphics` | backend, adapter, driver, swapchain size, vsync, capabilities |
| `performance` | `FrameStats`, `PassTimings`, the last 300 frame times |
| `log` | the last 500 lines from `RecentLogSink` |
| `session` | uptime, session log name, whether a crash report from the last run exists |
| `input` | connected devices and the current trigger binding |

`RecentLogSink` is a new bounded ring in `KhaozEngine.Diagnostics`. `InMemorySink` stays test-only.

### Redaction

`ReportRedactor` runs over every string a section writes and every log line. It rewrites the home directory to `~`,
masks bearer-shaped and secret-shaped values and admits only `KE_` environment variables, matching
`TelemetrySessionHeader`. Sections own not writing secrets. The redactor is the backstop.

## Bundle format

`KhaozEngine.BugReports.Contracts` defines format version 1, used by both the client and the relay.

The bundle is a zip with stored entries, since the media is already compressed:

| Entry | Contents |
| --- | --- |
| `manifest.json` | `formatVersion`, report id, game id, kind, category, description, captured-at, entry list with sizes and SHA-256 |
| `sections/<key>.json` | one per section |
| `screenshot.png` or `screenshot.jpg` | optional, per the toggle |
| `clip/index.json` | per frame: capture time, render milliseconds, width, height, entry name |
| `clip/NNN.jpg` | JPEG frames (first version) |
| `clip.mp4` | accepted from version 1, produced from phase E4 |

The manifest makes no identity claim. The relay takes identity from the token only.

Limits: 16 MB total, at most 128 entries, entry names from the whitelist above, no directory traversal, no compressed
entries, declared sizes and hashes must match.

## Client flow

`BugReportService` lives at the `GameApp` level, not inside a game screen, so the trigger works on the title screen,
the login screen, while loading and in game. The trigger is a rebindable `ActionMap` action with F10 as the engine
default. `BugReportService.Begin()` serves menu entries.

1. **Capture.** In the frame that sees the trigger, the service freezes the clip, records the screenshot copy and
   captures every section. It raises `ReportCaptured(reportId)` so a connected game can ask its server for a snapshot.
2. **Confirm.** `BugReportScreen` opens the next frame. It shows the screenshot thumbnail and a strip of clip frames,
   the required "What happened?" `TextArea` (4000 characters), an optional category `Dropdown`, the screenshot and clip
   toggles and the fixed line "Your account and your game and system info are included". Before sign-in it says the
   report will be sent after the player signs in.
3. **Send or cancel.** Send builds the bundle on a background thread and hands it to the sink. Cancel discards
   everything. A second trigger while the screen is open is ignored.

The screen is modal for input. The game decides whether its world pauses. All text resolves through engine `StringId`s
that a game can override.

### Sinks

`IBugReportSink` has two implementations:

- `HttpRelaySink` posts the bundle to `{relay}/bug-reports` with `Authorization: Bearer <token>` and
  `Idempotency-Key: <reportId>`, retrying through `HttpRetry`. The game supplies the token through a credentials
  delegate, so the engine stays auth-agnostic. The response carries a reference code that the screen shows in a toast.
- `LocalFolderSink` is for development builds. It writes the bundle and the rendered viewer page to a folder and opens
  the page, so the whole pipeline can be exercised without a relay.

### Offline queue

A send that fails on the network, a 5xx or a 429 writes the bundle to `<stateDir>/bug-reports/pending/` tagged with
the account subject. A report made before sign-in goes straight there with the subject left open and binds to the
first account that signs in.

- The queue retries after the next successful sign-in by the same account.
- It holds at most 3 reports or 30 MB, dropping the oldest, and expires reports after 7 days.
- Validation rejects (4xx other than 429) are not queued. The player is told why.

## Server snapshot

For server-backed games the engine supplies the server half. The game supplies the message that carries the report id
from client to server, because protocols are game-owned.

- The server registers `IBugReportSection`s on a `BugReportServerCapture`, the same contract the client uses.
- On the game's report message, the server applies a per-account limit first, then captures its sections on the
  simulation thread into immutable records. Capture must be cheap enough for the tick budget.
- `ServerSnapshotPoster` serializes off the simulation thread and posts to `{relay}/bug-reports/{reportId}/server` with
  the account subject and a service key, through `HttpRetry`.
- A failed post is dropped with a warning. A report without the server view is still a report.

## Relay

### Operations

| Operation | Trigger | Does |
| --- | --- | --- |
| `IntakeAsync` | client upload | authenticate, quota, validate, store, enqueue `CreateIssue`, return the reference |
| `ServerSnapshotIntakeAsync` | server post | check the service key, store the snapshot in the private pending area keyed by report id, enqueue `AppendServerSnapshot` if the issue already exists |
| `RevokeReportAsync` | operator call | resolve the id or reference, tombstone, delete public media, redact and close the issue, schedule the private purge |
| `RevokeRequestAsync` | authorized operator HTTP request | parse the capped body and map the revocation status to a relay response |
| `ProcessWorkAsync` | queue message | create the issue, append the snapshot comment or purge a revoked report |

### Intake

1. Authenticate through the game's `IReporterAuthenticator`, which returns subject, display name and an optional
   handle. A missing or bad token is rejected before the body is read.
2. Reject a subject on the host's blocked-subjects list.
3. Check quotas through `IReportQuotaStore`: per-account hour and day windows and the global day window. Over quota
   answers 429 with `Retry-After`.
4. Stream the body under the 16 MB cap and validate it against Contracts: entry names, sizes, hashes, PNG and JPEG
   magic bytes and dimension bounds, and JSON that parses.
5. If the report id was already accepted, return the same reference without storing again.
6. Write the entries, `report.json` (the manifest plus the relay's identity and received time) and the rendered viewer
   page to `IReportMediaStore` under `reports/<game>/<yyyy-mm>/<reportId>-<token>/`, where the token is 128 random bits.
   Every blob gets an explicit content type. Validation guarantees image bytes match their type, and the only HTML served is the engine's viewer page. Azure Blob Storage cannot set per-blob response headers, so nosniff is not used.
7. Enqueue `CreateIssue(reportId)` on `IReportWorkQueue` and answer 202 with the reference.

The reference code is derived from the report id (for example `R-6F3A2Q`) and appears in the issue, so a player who
quotes it can be matched to their report.

### Worker

- Loads `report.json` and any stored server snapshot.
- Creates the issue through the GitHub App client and records the issue number in `report.json`. A message for a report
  that already has an issue number is a no-op, which makes redelivery safe.
- Failures leave the message for the queue to redeliver with backoff. After 5 dequeues the host moves it to a poison queue. The Azure backend's `PoisonQueueRequeuer`, run daily by the host, moves poison items back at most 3 times, which recovers from GitHub outages without an alert.
- An `AppendServerSnapshot` message posts the snapshot summary as a comment.

### Server snapshot merge

A snapshot is merged only when its subject equals the subject of the reporter's token. A mismatch is dropped and
logged. A pending snapshot lives in a private area, because the report's capability folder does not exist until the
client bundle arrives. On merge the worker copies it into the report folder. A pending snapshot with no matching report
expires by storage policy (Grimhollow uses one day).

### GitHub App client

A BCL-only client with no Octokit dependency. It signs the App JWT with RS256 from the PEM private key (issued 60 s in
the past, valid 9 minutes), exchanges it for an installation token and caches that token until shortly before expiry.
It creates issues and comments. A 403 or 429 with `Retry-After` from GitHub's secondary limits fails the work item so
the queue redelivers it later.

### Issue shape

- Labels: from host configuration (Grimhollow: `bug`, `game:grimhollow`, `source:in-game`).
- Title: `[<Game> <version>] <first line of the description, at most 70 characters>`.
- Body, in order:
  - the description in a blockquote, with `@` mentions and `#123` references neutralised so a player cannot ping
    GitHub users or cross-link issues
  - a reporter table: display name, handle when the authenticator supplies one, account id, reference, captured-at,
    platform, game and engine versions, backend and GPU, connection state, server build, position
  - the screenshot inline
  - four clip frames inline as thumbnails and a "Play the clip" link to the viewer
  - links to each section and the server snapshot
  - the last 50 log lines in a collapsed `<details>`
- Everything else stays in storage, which keeps the body far below GitHub's 65,536 character limit.

### Viewer

A single self-contained HTML page rendered by the core package's `BugReportViewerPage` with the report data embedded as escaped JSON and
no external scripts or styles.

- Plays the clip at its recorded timestamps, scrubs, and steps frame by frame.
- Plays `clip.mp4` with a `<video>` element when present, using `clip/index.json` for timing.
- Graphs frame time per frame and marks hitches over twice the median.
- Pretty-prints every section, the server snapshot and the log tail.

### Media access

GitHub's image proxy and the viewer both fetch anonymously, so media is served at capability URLs: the 128-bit path
token, per-blob read access and no container listing. To revoke a report, call `BugReportRelay.RevokeReportAsync`
with its id or reference, or forward an authorized operator request to `RevokeRequestAsync`. Delete the returned
issue with an identity that may delete issues. The relay deletes public media, redacts and closes the issue, and
schedules a purge after twelve minutes to repeat cleanup and delete private state. Retain the non-personal
tombstone and reference index until lifecycle expiry. A missing public manifest also means revoked. See the
[implemented revoke design](2026-10-19-bug-report-revoke-design.md) for status mapping and purge recovery.
A holder that passed its check before the call can still finish a write inside its lease, and a holder
running beyond the lease is outside the guarantee.

### Seams and backends

| Seam | Purpose | Azure backend |
| --- | --- | --- |
| `IReporterAuthenticator` | token to identity | none (the game supplies it) |
| `IReportMediaStore` | write and read report blobs, existence checks, recursive `ListAsync` and idempotent `DeleteAsync` | Blob Storage |
| `IReportWorkQueue` | at-least-once work items, including delayed `EnqueueAsync` | Storage Queue, with a time to live of the delay plus seven days |
| `IReportQuotaStore` | fixed-window counters with optimistic concurrency | Table Storage |
| `IGitHubIssueClient` | create issues, add comments, redact through `UpdateIssueAsync`, and construct a page URL through `IssuePageUrl` | in-package GitHub App client |

The media, queue and quota seams have in-memory implementations for tests and local runs.
`IGitHubIssueClient` creates issues, adds comments and redacts and closes issues through `UpdateIssueAsync`.
The new list, delete, delayed enqueue and issue update members have default implementations that throw
`NotSupportedException` naming the member. Engine backends override them.
`IssuePageUrl` defaults to null when a client cannot construct the issue page URL.

## Security

- Authentication happens before the body is read, and quota before validation.
- Bundle validation rejects traversal, compression, unknown entries, mismatched hashes and bad image headers.
- The service key comparison is constant-time.
- The viewer escapes all embedded data and loads nothing external.
- Identity comes from the token only. The manifest carries no identity.
- No secret ever enters a bundle. Redaction is the backstop.

## Testing

All headless, in the matching area test projects.

- `JpegWriter`: output decodes with a reference decoder referenced only by the test project, within a tolerance.
- `FrameHistoryRecorder` with a fake frame source and clock: rate, duration, byte cap, dropping under backlog,
  `Suspend`, `Resume` and `Freeze`.
- `GpuReadbackRing` on the GPU test kit for each backend CI has: correct pixels, no `WaitForIdle`, dropped ticks when all
  slots are busy.
- `RecentLogSink` bounds and `ReportRedactor` rules.
- Section isolation for throwing and oversized sections.
- Bundle round trip, manifest hashes and every Contracts rejection, including a zip bomb.
- Offline queue caps, expiry and subject binding, with a fake HTTP handler.
- Relay intake: rejection before reading the body, per-account and global quotas, idempotency, blocked subjects.
- Issue composer golden files, including mention neutralising and the body size bound.
- GitHub App JWT against a fixed test key.
- Worker idempotency and poison handling, and server snapshot merge with a subject mismatch and a late arrival.
- `BugReportScreen` states through the headless GUI harness, including the before-sign-in variant.

The windowed frame tap is proved by the first consumer's live check, not a headless test.

## Phases

| Phase | Scope |
| --- | --- |
| E1 Capture | frame tap, `GpuReadbackRing`, `JpegWriter`, `FrameHistoryRecorder`, `RecentLogSink`, screenshot |
| E2 Report and client | Contracts, model, sections, redaction, bundle, sinks, offline queue, `TextArea`, `BugReportScreen`, `BugReportService`, server capture and poster |
| E3 Relay | relay core, GitHub App client, issue composer, viewer, Azure backend |
| E4 Hardware clip (next round) | VideoToolbox and Media Foundation H.264 behind a seam, a managed MP4 muxer, JPEG frames kept as the fallback |

E1 to E3 ship together in one engine minor release, tagged only with owner approval.

## Documentation and release

- Package READMEs for the four new packages, `docs/USING-KHAOZENGINE.md` and `docs/INDEX.md`.
- `CHANGELOG.md` entry for the release.
- `docs/DEPENDENCY-SEAMS.md` records the Azure backend edge.

## Follow-ups

- E4 hardware H.264 clips.
- Automatic crash reports through the same pipeline.
- IME for `TextArea` and `TextInput`.
- None for game-template here. The relay function scaffold and its Bicep module are authored in game-template as part
  of the first consumer's adoption, so every later game inherits them.
