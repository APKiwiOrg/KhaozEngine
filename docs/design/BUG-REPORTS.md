# In-game bug reports

Status: implemented capture, confirmed submission, headless bundle/server support and host-agnostic relay.

The engine owns evidence capture, the modal confirmation flow, bundle validation, offline queue and
relay. Consumers own bindings, credentials, hosting, game sections and server messages. Package
READMEs own exact API usage for [Contracts](../../KhaozEngine.BugReports.Contracts/README.md),
[core](../../KhaozEngine.BugReports/README.md),
[client](../../KhaozEngine.BugReports.Client/README.md) and
[relay](../../KhaozEngine.BugReports.Relay/README.md).

## Owner contracts

| Id | Current contract and reason |
| --- | --- |
| BR1 | Reports serve authenticated shipped-build players. They do not require a tester or GitHub credential. |
| BR2 | The consumer chooses private issue triage and labels. Players receive a short reference, not issue access. |
| BR3 | A shared static viewer plays the stored media. No transcoder or ffmpeg service is required. |
| BR4 | Server-backed games can capture an authoritative snapshot under the client report ID. The game supplies transport and authenticated subject. |
| BR5 | Relay core is host-agnostic. HTTP/queue hosts adapt streams, headers, results and cancellation. Azure remains an opt-in backend. |
| BR6 | Rebindable ReportBug defaults to F10 across screens. Trigger captures now, confirmation opens next frame, and Send authorizes transmission. |
| BR7 | Token identity is authoritative. Signed-out reports bind to the first flushing account, then only that account retries them. LocalFolderSink is a development/offline integration path. |
| BR8 | Account, game and system information remain included. Screenshot and clip have separate toggles, with the fixed disclosure on the screen. |
| BR9 | Rolling recording defaults on. Turning it off removes rolling readback/encoding, while the standby screenshot tap keeps copy-source usage requested. |
| BR10 | The recorder produces JPEG frames. Format 1 already accepts MP4 plus timing index so another encoder need not change the bundle format. |
| BR11 | Default quotas are 5/hour and 20/day per account and 100/day globally. Bundle cap is 16 MiB. The host owns media retention. |
| BR12 | Write-ahead local queuing and durable relay work separate player submission from issue creation. Queue delivery and poison recovery belong to the host. |
| BR13 | Screenshot long edge is at most 2560 px. Use PNG strictly below 4 MiB, otherwise JPEG quality 92. |
| BR14 | The required description is the single What happened field. Issue title comes from its first nonempty line, bounded by text elements. |

## Capture and confirmation

`FramePhases` runs taps after the final composite and before submit. The window first copies the
swapchain to an owned sampled texture, then downsamples through `GpuScaledCopy`. This common path
needs only swapchain copy-source usage, not sampled swapchain support. Sampling normalizes channel
order to RGBA and forces alpha one. No registered tap preserves the ordinary submit/present path.

Rolling readback uses a three-slot fenced ring. Only completed slots map. A full ring drops that
capture tick and never waits for idle. A bounded two-frame encode queue drops its oldest waiting
frame instead of blocking the frame thread. Defaults are 5 s, 12 fps, 960 px long edge, quality 70
and 8 MiB encoded bytes. Capture timestamps and the loop's unclamped render delta preserve hitches.
Suppressed frames leave gaps. Resize retains each frame's own dimensions and drains old ring slots
before disposal. Options must fit the 120-frame bundle capacity.

Suspend/Resume preserves the clip before a pause menu. Freeze stops the ring and waits at most 250 ms
for outstanding encoding. The standby screenshot tap remains registered until disposal. Its one-shot
request waits for a copyable rendered frame, reads back after submit and encodes off-thread. A
second pending request shares the same task. A device without completion fences can still take a
screenshot when its swapchain supports copying. Capture faults trip the affected guard once rather
than failing the game frame or leaving a screenshot task pending.

The overlay captures the ID, clip and sections and arms the screenshot before the game update.
Confirmation appears on the following frame, so its UI is absent from the evidence. It consumes
interaction, while the game owns pause policy. Another trigger while open is ignored. Cancel discards
the report. `ReportCaptured` permits a local server capture request. Only `ReportConfirmed`, raised
on Send, permits posting that snapshot. Bound and expire unconfirmed server captures in the game.

## Shared implementation decisions

These identifiers are scoped to bug reporting. They describe current contracts rather than execution order.

| Id | Contract and rationale |
| --- | --- |
| D1 | IDs are UUID v7, canonical 32 lowercase hex digits. Reference codes retain only 30 bits, so they are not invertible or guaranteed unique. |
| D2, D3 | Report screen lives in Client, generic TextArea in Gui, shared viewer in core. Servers and relays acquire no window/GUI dependency. |
| D6, D16 | Queue seam enqueues only. Host redelivery handles worker failure, poisons after five dequeues and can run the Azure poison requeuer daily, at most three requeues per item. |
| D7 | Azure backend receives constructed storage SDK clients. It does not select an identity provider or depend on Azure.Identity. |
| D8, D9, D-JSON | Contracts owns the total untrusted-bundle validator and compact camelCase JSON, UTC times and lowercase SHA-256. Shared strict helpers keep client and relay interpretation aligned. |
| D10, D11 | Section keys use the closed 48-character grammar. An MP4 index names clip.mp4 on every row. Exact names and sizes live in the Contracts README. |
| D12 | Relay-read section fields are log.lines, build.version, build.engineVersion, platform.os, graphics.backend and graphics.adapter. Keep these stable across producers. |
| D13 | Authenticator Handle is provider display text. Issue composition renders it and Subject as inline code, separately from neutralized player description. |
| D14, D15 | Frame timing is unclamped. The screenshot standby tap survives clip stop, and clip capacity is 120 frames. Averaged timing or a removed standby tap would lose the reporting moment. |
| D-SEC | Capture isolates sections on the trigger thread. Off-thread Finish redacts and structurally truncates them, preserving valid JSON and the aggregate budget. RawSection is never transmission-ready. |
| D-RED | Redaction uses bounded non-backtracking matching without a wall-clock deadline. Secret patterns run before optional reporter terms and home replacement. Oversized or unprocessable strings fail closed. Only KE_ environment names are admitted. |
| D-QUEUE | Bundle is durable before the first send. Metadata commits last, null subject binds before flushing, and Sent/Rejected removes it. Caps are 3 reports or 30 MB and 7 days, oldest first. |
| D-HOOK | GameApp overlay input precedes game interaction. Consumed input loses text and edges. This keeps reporting available across consumer-owned screen stacks. |
| D-LOC | Engine StringIds and an English fallback own report UI. Consumers override catalog entries and category labels. |
| D-VIEW | Shared viewer embeds escaped JSON, uses fixed hash-authorized script and textContent, loads no external code and sends no referrer. Hitch means strictly over twice the median render time. |
| D-TA | TextArea edits committed scalar-boundary text with wrapping and scrolling. Maximum description is 4000 UTF-16 units. Selection and IME/preedit are not provided. |
| D-THUMB | Baseline JPEG DC thumbnails decode at one-eighth scale without a full inverse DCT. PNG thumbnails use the existing image decoder and downscale. |

The core section capture limit is 64 sections and 4 MiB raw bytes per section. Finish uses the smaller
of each section's budget and the remaining 1 MiB aggregate. A section with under 64 bytes remaining
is dropped. A failing section becomes a redacted error object. The client supplies the finish budget.
Caller-built CapturedSection values remain the caller's responsibility to finish safely.

## Relay invariants and limits

Authenticate, block-check and charge quota before reading the upload. The manifest makes no identity
claim. Quota precedes idempotency, so resends and invalid bundles consume quota. The three fixed UTC
counters are optimistic, not one transaction, and a later refusal can retain an earlier increment.
Five conflicts fail closed with a five-second retry hint.

Public media uses a random 128-bit capability folder under the configured game and received UTC month.
Pending identity, accepted index, claim markers and unmerged snapshots stay private. Every blob has
an explicit content type. Keep accepted/claim state at least as long as the retry/report policy.
A one-day snapshot lifecycle must target snapshots only, never the whole private container.

An accepted resend returns the same reference and re-enqueues unfinished issue work. Claims and
result markers reconcile issue/comment progress. They do not eliminate a duplicate after a successful
GitHub write whose result was lost, or a holder that outlives its ten-minute lease. POSTs are sent
once per attempt, with host redelivery instead of in-process issue retries. The installation-token
exchange alone uses HttpRetry. GitHub App credentials never enter a client bundle.

Server snapshots merge only for an ordinal-equal authenticated subject. A mismatch is dropped with a
report-ID-only warning and still answers accepted. A late matching snapshot becomes at most one
comment within the claim/result guarantee. Missing server evidence does not invalidate the report.

Issue composition neutralizes mentions/references and hostile Markdown, limits its body to 60,000
characters and abbreviates the title by text element. Media stays in storage. The viewer and relay
share the newest 50-line log-tail reader. Reporter identity is added by the relay only.

[Revocation](BUG-REPORT-REVOCATION.md) owns tombstones, reference lookup, immediate cleanup and delayed
purge. No media retention duration, deployed host or native capture result is established by headless
tests alone.
