# In-game bug reports E2: report core, client and screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the bundle contract, the headless report core (sections, redaction, bundle, sinks, offline queue, viewer, server capture) and the client half (F10 trigger on every screen, capture in the trigger frame, `BugReportScreen`, submission and toasts) so a game can produce a validated bug report bundle.

**Architecture:** `KhaozEngine.BugReports.Contracts` is a BCL-only format package shared with the E3 relay. `KhaozEngine.BugReports` stays headless and server-safe and owns everything that turns captured state into a bundle and delivers it. `KhaozEngine.BugReports.Client` adapts it to `GameApp` through a new `IGameOverlay` seam, so one service sees raw input before every game screen, holds input while its modal screen is open and draws above the game UI. The headless `BugReportFlow` carries all client behaviour so tests never need a window.

**Tech Stack:** C# on the repository SDK (`net10.0` for new projects), System.Text.Json source generation, System.IO.Compression, System.Security.Cryptography, existing `KhaozEngine.Gui`, `KhaozEngine.Game`, `KhaozEngine.Http`, `KhaozEngine.Diagnostics`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-17-bug-reports-design.md`, bound by `docs/superpowers/plans/2026-10-17-bug-reports-contract.md` (the contract). Executors read both.

## Global Constraints

- Every public name, signature and default in the contract's "E2 produces" block is binding. Names this plan adds are listed per task and in the report back to the orchestrator.
- E1 has landed on this branch before Task 1: `KhaozEngine.BugReports` (with `ReportImage`, `ReportClip`, `ReportClipFrame`), `KhaozEngine.BugReports.Client` (with `FrameHistoryRecorder`, `FrameHistoryOptions`), `KhaozEngine.Imaging.JpegWriter`, `AppWindow.AddCaptureTap`. Task 1 Step 0 checks this.
- `KhaozEngine.BugReports.Contracts` has zero project references and zero package references.
- `KhaozEngine.BugReports` references exactly `BugReports.Contracts`, `Diagnostics`, `Http` and `Platform`, and its transitive closure never reaches `Gui`, `Windowing`, `Gpu`, `Render2D` or `Game`.
- New projects use the single repo-wide `TargetFramework` (`net10.0`). The architecture test forbids new multi-targeting.
- All three bug report packages stay outside every umbrella and are listed in `ArchitectureTests.OptInBackends`.
- Limits copied from the spec: 16 MB bundle, 128 entries, 1 MiB total section state, 4000 description characters, 70 title characters, 2560 px screenshot long edge, 5 server captures per account per hour, offline queue of at most 3 reports or 30 MB, queued reports expire after 7 days, `log` holds the last 500 lines, `performance` holds the last 300 frame times.
- Trigger: action id `ReportBug`, default `Key.F10`, rebindable, active on every screen including before sign-in. A second trigger while a report is open is ignored.
- Default categories, in order: `Gameplay`, `Visual`, `Audio`, `Performance`, `Interface`, `Other`. `Other` is the default selection.
- Player-facing text resolves through engine `StringId`s with an English fallback catalog. `LocalizedText.Raw` is used only for already resolved text and non-localizable tokens (reference codes, counters), following `BootStrings`.
- `AppWindow` stays the only raw input reader. Everything here reads `InputState`.
- KESIZE: new behaviour in new types. `GameApp.cs` (685 lines, not baselined, cap 800) may grow by at most 25 lines. No baseline edits.
- Warnings are errors. No blanket suppressions.
- Test namespaces are `KhaozEngine.Tests.*`. Tests that touch `LocalizationContext`, the ambient `Log` or the clipboard join a collection whose definition sets `DisableParallelization = true`.
- Every build or test command runs through `build-slot --label <what> -- <command>`. Full solution builds and suites add `--heavy`. Run `mkdir -p local-feed` once before the first restore.
- Commit subjects use `area(scope): summary`. Stage and commit explicit paths only.
- No version bump and no `CHANGELOG.md` entry. E3's last task stages the release.
- No em or en dash glyphs and no prose semicolons in Markdown, comments or shipped strings.

## Review Focus

1. A player typing into a focused game chat box presses F10. The report must trigger and none of the keystrokes typed into the report screen may reach the game while it is open. Pinned in Task 14 (`Held_input_carries_no_text_or_edges`) and Task 17 (`Typing_in_the_screen_never_reaches_the_game`).
2. A device without completion fences, or recording switched off in settings. The clip toggle is disabled and the report still sends with no `clip/` entries. Pinned in Task 17 (`No_clip_sends_without_clip_entries`).
3. A pasted 4000 character description with no spaces. The text area hard-wraps it, the bundle accepts exactly 4000 and rejects 4001. Pinned in Task 2 (`Description_limit_is_exactly_4000`) and Task 13 (`Long_unbroken_paste_wraps_and_caps`).
4. An unwritable state directory or a full disk. Submitting must never throw onto the frame thread. The player gets the failed toast. Pinned in Task 8 (`Unwritable_queue_never_throws`).
5. The game closes while a send is in flight. The report must survive and flush at the next sign-in without a duplicate issue. Pinned in Task 8 (`Report_is_written_ahead_before_sending`), with the relay's `Idempotency-Key` absorbing a resend.

## Planning decisions

**D-JSON. Wire shapes.** All Contracts JSON is compact (no indentation, so bytes match on every OS), camelCase property names, enum values camelCase strings (`"bug"`, `"crash"`), `DateTimeOffset` normalised to UTC and written as `2026-10-17T03:04:05.678+00:00`, SHA-256 as 64 lowercase hex characters, encoder `JavaScriptEncoder.Create(UnicodeRanges.All)` so non-ASCII text stays readable while `<`, `>`, `&`, `'` and `"` stay escaped. Reason: one golden byte string per shape that both E2 and E3 test against.

```json
{"formatVersion":1,"reportId":"0192f1c2a3b47c4d8e9f0a1b2c3d4e5f","gameId":"grimhollow","kind":"bug","category":"Gameplay","description":"Fell through\nthe floor","capturedAt":"2026-10-17T03:04:05.678+00:00","entries":[{"name":"sections/build.json","size":42,"sha256":"<64 hex>"}]}
{"frames":[{"offsetMilliseconds":0,"renderMilliseconds":16.5,"width":960,"height":540,"entry":"clip/000.jpg"}]}
```

`manifest.json` is the first zip entry and does not list itself. `entries` follows zip order: sections in registration order, the screenshot, `clip/index.json`, then frames. When a bundle carries `clip.mp4` (phase E4), every `ClipIndexFrame.Entry` is `"clip.mp4"` and the index only carries timing. Reason: lets the relay and viewer accept the E4 format from version 1 with no schema change.

**D-SEC. Section isolation and truncation.** A section writes one JSON object into a private buffer. A section that throws, leaves a container open or writes a non-object root becomes `{"error":"<exception full type name>: <message>"}` with the message redacted and cut to 512 characters. Every captured section is then re-tokenised and every string value and property name passes through `ReportRedactor` (the backstop the spec asks for, since `Utf8JsonWriter` cannot be intercepted). A redacted section over its budget is cut structurally: `"truncated":true` becomes the root's first property, then leaf values are copied in document order while the result fits, and open containers are closed. The largest fitting leaf count is found by binary search over a pure copier, so the output is always valid JSON and never exceeds the budget. The budget is `min(section.MaxBytes, 1 MiB - bytes already used)`. A section whose budget falls under 64 bytes is dropped with a warning log line. Reason: a byte cut would produce invalid JSON that the relay rejects.

**D-RED. Redactor rules.** All patterns use the backtracking engine with `RegexOptions.CultureInvariant`, a 250 ms match timeout and fail closed (a timeout returns `[redacted]` for the whole string). Applied in this order, each replacement writing `[redacted]`:

| Rule | Pattern | Replacement |
| --- | --- | --- |
| PEM private key | `-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[A-Za-z0-9+/=\s]*-----END [A-Z0-9 ]*PRIVATE KEY-----` | whole match |
| JWT | `\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}` | whole match |
| Provider tokens | `\b(?:gh[pousr]_[A-Za-z0-9]{30,}\|github_pat_[A-Za-z0-9_]{20,}\|xox[abprs]-[A-Za-z0-9-]{10,}\|AKIA[0-9A-Z]{16}\|sk-[A-Za-z0-9_-]{20,})\b` | whole match |
| Bearer and Basic | `(?i)\b(bearer\|basic)(\s+)[A-Za-z0-9._~+/=-]{8,}` | `$1$2[redacted]` |
| URL query secrets | `(?i)([?&](?:sig\|signature\|code\|token\|access_token\|id_token\|refresh_token\|key\|apikey\|api_key\|client_secret\|password)=)[^&#\s"']+` | `$1[redacted]` |
| Key and value | `(?i)((?:password\|passwd\|pwd\|secret\|token\|api[_-]?key\|apikey\|access[_-]?key\|private[_-]?key\|client[_-]?secret\|connection[_-]?string\|accountkey\|sharedaccesskey)["']?\s*[:=]\s*["']?)[^\s"',;&]+` | `$1[redacted]` |
| Home directory | the escaped home path, case-insensitive, followed by `(?=[/\\]\|$\|[^A-Za-z0-9._-])`, tried in native and alternate separator forms | `~` |

Pipes inside the table's patterns are escaped as `\|` for Markdown only. The C# literals use a bare `|`. The home rule is skipped when the home path is shorter than 3 characters. Environment variables are admitted only through `ReportRedactor.SelectEnvironment`, which keeps `KE_` names exactly as `TelemetrySessionHeader.SelectEngineVariables` does and redacts their values. Reason: the spec names these three classes and the `KE_` precedent. The table is the test oracle.

**D-QUEUE. Offline queue.** Layout under the directory the game passes (`<stateDir>/bug-reports/pending/`): `<reportId>.zip` (the bundle) and `<reportId>.json` (`{"reportId":"…","subject":null,"queuedAt":"…","bytes":123}`), each written to `.tmp` then moved into place, the `.json` last so its presence marks a complete entry. A scan deletes orphans and unreadable metadata. Submission is write-ahead: the bundle is queued before the first send and deleted on `Sent` or `Rejected`, so a crash mid-send keeps it. A null subject binds at flush: `FlushAsync(subject)` sends entries whose subject equals it or is null, rewriting a null subject to the flushing account before sending. Pruning on every enqueue and flush: delete entries older than 7 days by `queuedAt`, then delete oldest first (by `queuedAt`, then report id ordinal) until at most 3 entries and 30 MB remain. A flush stops at the first `Queued` result and skips ids already in flight. Reason: BR7 and BR12 with no extra infrastructure.

**D-HOOK. Frame loop and screen stack.** `GameApp` gains `AddOverlay(IGameOverlay)` and `RemoveOverlay`. In the pre-record phase, after the UI viewport update and before the game's pointers, `OnUpdate` and the HUD, every overlay receives the raw frame input. If any overlay returns true, the game, its pointers and the HUD see `InputState.WithoutInteraction()` for that frame. Overlays draw in their own point-space UI pass after `OnDrawUi` and before the HUD. `BugReportService` is one overlay with a private `ScreenStack` and `ToastStack`. In the frame that sees the trigger (frame N, before `OnUpdate`) the flow creates the report id, calls `FrameHistoryRecorder.Freeze()`, arms `CaptureScreenshotAsync()` so the E1 tap copies frame N, captures every section and raises `ReportCaptured`. Frame N+1 pushes `BugReportScreen`. Because frame N renders before the screen exists and the ring stays frozen until Send or Cancel, neither the screenshot nor the clip contains the report screen. `Begin()` sets a pending trigger consumed by the next overlay update, so a menu can close itself first. Reason: the game owns its own `ScreenStack`, so only `GameApp` is shared by the title, login, loading and game screens. The `AppWindow.InputFilter` seam was rejected because Automation owns it and a filter cannot draw.

**D-LOC. Localization.** `BugReportStrings` holds `bugreport.*` `StringId`s and an English fallback catalog, resolved through `LocalizationContext.Catalog` when it carries the key, as `DiagnosticsOverlayStrings` does. A game overrides any line by adding the key to its own catalog. Category labels resolve `bugreport.category.<id lowercased>` and fall back to the raw id, so a game's own category list localizes the same way. Keys and English text are fixed in Task 16.

**D-VIEW. Viewer page.** One embedded HTML template. The report data goes in `<script type="application/json" id="report-data">` as JSON produced token by token (never raw section bytes), after which every `<` in the JSON text is replaced by `\u003c`, which is valid only because `<` can appear in JSON solely inside strings. The page script is fixed template text allowed by a CSP `script-src 'sha256-…'` hash computed at render, so no injected script can run. No external URLs, `default-src 'none'`, `img-src 'self' file:`, `media-src 'self' file:`, `<meta name="referrer" content="no-referrer">` so capability tokens never leak. Frames play with a `setTimeout` chain whose delay is the next frame's `offsetMilliseconds` minus the current one, with a range scrubber, step buttons and arrow keys. When the manifest lists `clip.mp4` a `<video>` plays it and its `currentTime` drives the graph cursor through the index offsets. The graph is SVG built with `createElementNS`, one bar per frame, hitch bars marked. Hitches (render time strictly over twice the median, median of an even count is the mean of the middle two) are computed in C# and embedded so the rule is testable. All text reaches the DOM through `textContent`.

**D-TA. TextArea.** Multi-line committed text (no IME, no preedit), `MaxLength` 4000 UTF-16 units like `TextInput`, line endings normalised to `\n`, caret is a UTF-16 index on a scalar boundary. Editing reuses `TextEntry.Apply` on the text before the caret, which gives Backspace, OS text, the key-map fallback and Ctrl or Cmd+V paste for free. Enter inserts `\n` unless Ctrl or Cmd is held. Delete removes the next scalar. Left, Right, Up, Down, Home and End move the caret over visual lines. A tap places the caret. Greedy word wrap with hard breaks for long words. Vertical scroll keeps the caret line visible and the wheel scrolls three lines. No selection. Reason: the spec's minimum plus what a 4000 character field needs to be editable.

**D-THUMB. Thumbnails.** The screen needs pixels for the screenshot thumbnail and a strip of four clip frames, and E1 only exposes encoded bytes. `KhaozEngine.Imaging` gains `JpegThumbnailDecoder`, a baseline-only DC decoder that yields a one-eighth scale image without an inverse DCT (960 px frames give 120 px thumbnails). PNG screenshots decode through `ImageRgba.Decode` and a box downscale. Reason: smallest BCL route to the spec's strip. The alternative (E1 exposing RGBA thumbnails from its tap) is listed as a contract proposal.

## File map

| Path | Responsibility | Task |
| --- | --- | --- |
| `KhaozEngine.BugReports.Contracts/*` | format constants, records, entry names, ids, routes, JSON, validator | 1, 2 |
| `KhaozEngine.Diagnostics/RecentLogSink.cs`, `ReportRedactor.cs` | log ring, redaction | 3, 4 |
| `KhaozEngine.BugReports/Sections/*` | section contract, isolation, redaction pass, truncation | 5 |
| `KhaozEngine.BugReports/Bundles/*` | draft, bundle writer | 6 |
| `KhaozEngine.BugReports/Sinks/*` | submission types, HTTP sink, queue, submitter, local folder sink | 7, 8, 9 |
| `KhaozEngine.BugReports/Viewer/*` | viewer page renderer and template | 9 |
| `KhaozEngine.BugReports/Server/*` | server capture and poster | 10 |
| `KhaozEngine.Imaging/JpegThumbnailDecoder.cs` | one-eighth scale baseline JPEG decode | 11 |
| `KhaozEngine.Gui/TextAreaLayout.cs`, `TextArea.cs` | wrap and caret math, widget | 12, 13 |
| `KhaozEngine.Windowing/InputState.Hold.cs`, `KhaozEngine.Game/GameOverlay*.cs` | held input, overlay seam | 14 |
| `KhaozEngine.BugReports.Client/Sections/*` | built-in client sections and facts | 15 |
| `KhaozEngine.BugReports.Client/Screen/*` | strings, model, layout, thumbnails, screen | 16 |
| `KhaozEngine.BugReports.Client/BugReportFlow.cs` | headless client orchestration | 17 |
| `KhaozEngine.BugReports.Client/BugReportService*.cs` | `GameApp` adapter, options | 18 |
| `KhaozEngine.BugReports.Tests` (new), `KhaozEngine.BugReports.Client.Tests` (E1, extended) | headless tests | 1, 15 |

---

### Task 1: Contracts package and JSON shapes

**Files:**
- Create: `KhaozEngine.BugReports.Contracts/KhaozEngine.BugReports.Contracts.csproj`, `README.md`, `BugReportFormat.cs`, `BugReportLimits.cs`, `BugReportKind.cs`, `BugReportManifest.cs` (with `BundleEntry`), `ClipIndex.cs` (with `ClipIndexFrame`), `ReporterIdentity.cs`, `BundleEntryNames.cs`, `ReportIds.cs`, `RelayRoutes.cs` (with `RelayHeaders`), `RelayWire.cs` (`IntakeAccepted`, `RelayError`, `ServerSnapshotWire`), `BugReportJson.cs`, `BugReportJsonContext.cs`
- Create: `KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj` (E1 does not create it)
- Create: `KhaozEngine.BugReports.Tests/Contracts/ReportIdsTests.cs`, `BundleEntryNamesTests.cs`, `BugReportJsonTests.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.BugReports.cs` (E1 created it), `KhaozEngine.slnx`, root `README.md` (catalog row for `KhaozEngine.BugReports.Contracts`), `KhaozEngine.Tests/ArchitectureTests.cs` (`OptInBackends` gains `BugReports.Contracts`, `BugReports`, `BugReports.Client` where absent)

**Interfaces:**
- Consumes: nothing.
- Produces: every Contracts name in the contract except `BundleValidator` and `BundleValidationResult`. `BugReportLimits` includes `MaxPngScreenshotBytes = 4 * 1024 * 1024`. Also:

```csharp
namespace KhaozEngine.BugReports.Contracts;
public static class BugReportJson
{
    public static byte[] SerializeManifest(BugReportManifest manifest);
    public static BugReportManifest DeserializeManifest(ReadOnlySpan<byte> json);   // JsonException on bad input
    public static byte[] SerializeClipIndex(ClipIndex index);
    public static ClipIndex DeserializeClipIndex(ReadOnlySpan<byte> json);
    public static byte[] SerializeIntakeAccepted(IntakeAccepted value);
    public static IntakeAccepted DeserializeIntakeAccepted(ReadOnlySpan<byte> json);
    public static byte[] SerializeRelayError(RelayError value);
    public static RelayError DeserializeRelayError(ReadOnlySpan<byte> json);
    public static byte[] SerializeServerSnapshot(ServerSnapshotWire value);
    public static ServerSnapshotWire DeserializeServerSnapshot(ReadOnlySpan<byte> json);
}
public static class BundleEntryNames
{
    public const int MaxClipFrames = 120;
    public static bool IsValidSectionKey(string key);   // ^[a-z0-9][a-z0-9._-]{0,47}$
}
```

- [ ] **Step 0: Check the E1 precondition**

Run: `ls KhaozEngine.BugReports/*.csproj KhaozEngine.BugReports.Client/*.csproj && git grep -l "class FrameHistoryRecorder\|record ReportClip\|class JpegWriter"`
Expected: both csproj paths and three source files. Stop and report to the orchestrator if any is missing.

- [ ] **Step 1: Write the failing tests**

The test csproj copies `KhaozEngine.Gui.Tests` conventions (`IsPackable` false, `RootNamespace` `KhaozEngine.Tests`, xunit packages) and references `KhaozEngine.BugReports.Contracts` only in this task.

```csharp
// ReportIdsTests (namespace KhaozEngine.Tests.BugReports.Contracts)
[Fact] public void NewId_is_32_lowercase_hex_version_7()
{ string id = ReportIds.NewId(); Assert.Matches("^[0-9a-f]{32}$", id); Assert.Equal('7', id[12]); Assert.True(ReportIds.IsValid(id)); }
[Theory]
[InlineData("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f", "R-P3TKJZ")]
[InlineData("0192f1c2a3b47c4d8e9f0a1b00000021", "R-000011")]
[InlineData("0192f1c2a3b47c4d8e9f0a1bffffffff", "R-ZZZZZZ")]
public void ReferenceCode_uses_the_last_30_bits_in_crockford_base32(string id, string expected)
    => Assert.Equal(expected, ReportIds.ReferenceCode(id));
[Theory]
[InlineData("")] [InlineData("0192F1C2A3B47C4D8E9F0A1B2C3D4E5F")] [InlineData("0192f1c2a3b44c4d8e9f0a1b2c3d4e5f")]
[InlineData("0192f1c2-a3b4-7c4d-8e9f-0a1b2c3d4e5f")] [InlineData("../../etc/passwd000000000000000000")]
public void IsValid_rejects_non_canonical_ids(string id) => Assert.False(ReportIds.IsValid(id));
[Fact] public void ReferenceCode_and_ServerSnapshot_route_throw_on_invalid_id()
{ Assert.Throws<ArgumentException>(() => ReportIds.ReferenceCode("x")); Assert.Throws<ArgumentException>(() => RelayRoutes.ServerSnapshot("x")); }
[Fact] public void ServerSnapshot_route_is_relative()
    => Assert.Equal("bug-reports/0192f1c2a3b47c4d8e9f0a1b2c3d4e5f/server", RelayRoutes.ServerSnapshot("0192f1c2a3b47c4d8e9f0a1b2c3d4e5f"));

// BundleEntryNamesTests
[Fact] public void Section_and_frame_names()
{ Assert.Equal("sections/build.json", BundleEntryNames.Section("build")); Assert.Equal("clip/007.jpg", BundleEntryNames.ClipFrame(7)); }
[Theory] [InlineData("")] [InlineData(".hidden")] [InlineData("Build")] [InlineData("a/b")] [InlineData("a b")]
public void Section_rejects_bad_keys(string key) => Assert.Throws<ArgumentException>(() => BundleEntryNames.Section(key));
[Theory] [InlineData(-1)] [InlineData(120)]
public void ClipFrame_rejects_out_of_range(int i) => Assert.Throws<ArgumentOutOfRangeException>(() => BundleEntryNames.ClipFrame(i));
[Theory]
[InlineData("manifest.json", true)] [InlineData("screenshot.png", true)] [InlineData("screenshot.jpg", true)]
[InlineData("clip/index.json", true)] [InlineData("clip.mp4", true)] [InlineData("sections/game.state.json", true)]
[InlineData("clip/119.jpg", true)] [InlineData("clip/120.jpg", false)] [InlineData("clip/7.jpg", false)]
[InlineData("../manifest.json", false)] [InlineData("/manifest.json", false)] [InlineData("sections\\a.json", false)]
[InlineData("sections/../x.json", false)] [InlineData("sections/.json", false)] [InlineData("notes.txt", false)]
public void IsAllowed_is_an_exact_whitelist(string name, bool allowed) => Assert.Equal(allowed, BundleEntryNames.IsAllowed(name));

// BugReportJsonTests
[Fact] public void Manifest_matches_the_golden_bytes()   // the D-JSON manifest line, entry sha256 = new string('a', 64)
[Fact] public void Manifest_capturedAt_is_normalised_to_utc()   // +10:00 input serializes as the same instant with +00:00
[Fact] public void ClipIndex_matches_the_golden_bytes()
// frames (0, 16.5, 960, 540, "clip/000.jpg") and (83.25, 41, 960, 540, "clip/001.jpg") give
// {"frames":[{"offsetMilliseconds":0,"renderMilliseconds":16.5,"width":960,"height":540,"entry":"clip/000.jpg"},{"offsetMilliseconds":83.25,"renderMilliseconds":41,"width":960,"height":540,"entry":"clip/001.jpg"}]}
[Fact] public void Kind_round_trips_as_camel_case_strings()   // "crash" in, BugReportKind.Crash out, and back
[Fact] public void Non_ascii_description_stays_literal_and_html_characters_are_escaped()
// "Café <b>" serializes containing "Café" and "\u003Cb\u003E"
[Fact] public void Wire_records_round_trip()   // IntakeAccepted, RelayError, ServerSnapshotWire with one JsonElement section
```

`KhaozEngine.Tests/ArchitectureTests.BugReports.cs` gains:

```csharp
[Fact] public void BugReportsContracts_HasNoReferences()   // ProjectRefs empty, no non-infra PackageRefs, IsPackableLibrary
// and E1's BugReportsPackages_AreExplicitReferencesOutsideEveryUmbrella extends to KhaozEngine.BugReports.Contracts
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e2-t1-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.BugReports.Contracts"`
Expected: build FAIL with CS0246 for `ReportIds`, `BundleEntryNames` and `BugReportJson`.

- [ ] **Step 3: Implement the Contracts types**

Records and constants exactly as the contract states. `BugReportKind` carries `[JsonConverter(typeof(BugReportKindJsonConverter))]`, a `JsonStringEnumConverter<BugReportKind>` subclass constructed with `JsonNamingPolicy.CamelCase`. `BugReportJsonContext` is a `JsonSerializerContext` with `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]` and `[JsonSerializable]` for the six wire types. `BugReportJson` builds one `BugReportJsonContext` over options copied from the generated defaults with `Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)` and serializes `CapturedAt` after `ToUniversalTime()`. `ReportIds.NewId()` is `Guid.CreateVersion7().ToString("N")`. `IsValid` requires 32 lowercase hex characters, `id[12] == '7'` and `id[16]` in `89ab`. `ReferenceCode` takes `uint.Parse(id[24..], NumberStyles.HexNumber) & 0x3FFFFFFF` and writes six 5-bit groups, most significant first, over `0123456789ABCDEFGHJKMNPQRSTVWXYZ`. The csproj copies `KhaozEngine.Http`'s package shape without its multi-targeting and without references. The README covers the format version, entry names, limits, ids and the JSON goldens. The root README row says the package is BCL only and opt-in.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label e2-t1-green -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.BugReports.Contracts"`
Run: `build-slot --label e2-t1-arch -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`
Expected: both `Passed!` with `Failed: 0`. Then `bash scripts/check-doc-versions.sh` exits 0.

- [ ] **Step 5: Commit**

`git add` the new Contracts and test files, `KhaozEngine.slnx`, `README.md`, `KhaozEngine.Tests/ArchitectureTests*.cs`, then `git commit -m "feat(bugreports): add the bug report contracts package"`.

---

### Task 2: Bundle validator

**Files:**
- Create: `KhaozEngine.BugReports.Contracts/BundleValidator.cs`, `BundleValidationResult.cs`, `ImageHeaders.cs` (public PNG and JPEG header reading: `TryReadSize`, `PngMediaType` and `JpegMediaType`, shared with the bundle writer)
- Create: `KhaozEngine.BugReports.Tests/Contracts/BundleValidatorTests.cs`, `KhaozEngine.BugReports.Tests/TestBundles.cs` (builder for hand-made bundles, minimal PNG and JPEG headers)

**Interfaces:**
- Consumes: Task 1 types.
- Produces: `BundleValidator.Validate(Stream zip, long maxBytes = BugReportLimits.MaxBundleBytes)` and `BundleValidationResult` as in the contract, plus `public static class BundleErrors` holding the error code constants below. `Error` is `"<code>"` or `"<code>: <entry name>"`.

Error codes: `too_large`, `not_a_zip`, `too_many_entries`, `entry_not_allowed`, `duplicate_entry`, `compressed_entry`, `encrypted_entry`, `manifest_missing`, `manifest_invalid`, `format_version`, `report_id_invalid`, `game_id_invalid`, `description_invalid`, `category_invalid`, `entry_mismatch`, `size_mismatch`, `hash_mismatch`, `image_invalid`, `clip_index_invalid`, `section_invalid`, `sections_too_large`.

- [ ] **Step 1: Write the failing tests**

`TestBundles.Valid()` returns a `MemoryStream` with a stored zip holding a correct manifest, `sections/build.json` (`{"a":1}`), a 4x4 `screenshot.png` header, `clip/index.json` and two minimal 4x4 JPEGs. Mutators (`WithEntry`, `WithoutEntry`, `WithManifest`, `Deflated(name)`) rebuild it.

```csharp
[Fact] public void Valid_bundle_passes_and_returns_the_manifest()   // IsValid, Error null, Manifest.ReportId equals the builder's id
[Fact] public void Over_max_bytes_is_too_large()                   // Validate(stream, maxBytes: 100).Error == "too_large"
[Fact] public void Non_seekable_stream_over_the_cap_is_too_large() // a stream that yields maxBytes + 1 bytes and refuses Seek
[Fact] public void Garbage_is_not_a_zip()
[Fact] public void More_than_128_entries_is_rejected()
[Theory] [InlineData("notes.txt")] [InlineData("../manifest.json")] [InlineData("sections/../x.json")] [InlineData("clip/120.jpg")]
public void Unlisted_names_are_rejected(string name)               // Error == "entry_not_allowed: " + name
[Fact] public void Duplicate_entry_is_rejected()
[Fact] public void Deflated_entry_is_rejected_without_inflating()  // 64 MiB of zeros deflated under sections/build.json, Error == "compressed_entry: sections/build.json"
[Fact] public void Missing_manifest_is_rejected()
[Fact] public void Wrong_format_version_is_rejected()              // formatVersion 2
[Fact] public void Invalid_report_id_is_rejected()
[Fact] public void Description_limit_is_exactly_4000()             // 4000 'x' valid, 4001 'x' gives "description_invalid", "   " gives "description_invalid"
[Fact] public void Size_or_hash_mismatch_is_rejected()             // "size_mismatch: sections/build.json", "hash_mismatch: sections/build.json"
[Fact] public void Listed_but_absent_and_present_but_unlisted_are_mismatches()   // both "entry_mismatch: <name>"
[Fact] public void Png_with_bad_magic_or_2561_edge_is_rejected()   // "image_invalid: screenshot.png"
[Fact] public void Jpeg_without_a_frame_header_is_rejected()        // "image_invalid: clip/000.jpg"
[Fact] public void Section_that_is_not_a_json_object_is_rejected()  // "[1]" gives "section_invalid: sections/build.json"
[Fact] public void Clip_frames_without_index_or_with_decreasing_offsets_are_rejected()   // "clip_index_invalid"
[Fact] public void Index_dimensions_must_match_the_jpeg()           // index says 8x8, jpeg is 4x4, "clip_index_invalid"
[Fact] public void Clip_mp4_with_index_entries_naming_it_is_accepted()   // bytes 4..8 are "ftyp", every frame Entry == "clip.mp4"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `build-slot --label e2-t2-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~BundleValidatorTests"`
Expected: build FAIL with CS0103 for `BundleValidator`.

- [ ] **Step 3: Implement `BundleValidator.Validate`**

Order of checks, first failure wins: size (seekable `Length`, otherwise copy at most `maxBytes + 1` bytes into a `MemoryStream`), open `ZipArchive` (`InvalidDataException` gives `not_a_zip`), entry count, per entry name whitelist, duplicates, `IsEncrypted`, `CompressedLength != Length` gives `compressed_entry` (checked before any entry stream opens, which is the zip bomb defence), manifest presence and parse, field rules (`formatVersion == 1`, `ReportIds.IsValid`, game id `^[a-z0-9][a-z0-9-]{0,31}$`, description non-blank and at most 4000, category 1 to 64 characters, kind defined), manifest entries equal the zip entries minus the manifest as a set, then per entry read at most `Length + 1` bytes while hashing and compare size and SHA-256, then content rules: images by magic and dimensions within 1 to 2560 (`ImageHeaders.TryReadPng` reads IHDR, `TryReadJpeg` walks markers to the first SOF0 to SOF15 other than C4, C8 and CC), sections parse with `JsonDocument` (max depth 64) to an object and sum to at most 1 MiB, clip index parses, has at most 120 frames, offsets non-negative and non-decreasing, render times finite and non-negative, each entry is `ClipFrame(i)` present in the zip with matching dimensions or `clip.mp4` when that entry exists. JPEG frames with no index entry are `clip_index_invalid`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command. Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 5: Commit**

`git commit -m "feat(bugreports): validate bundles against the version 1 contract"` with the three source files and two test files.

---

### Task 3: RecentLogSink

**Files:**
- Create: `KhaozEngine.Diagnostics/RecentLogSink.cs`, `KhaozEngine.Foundation.Tests/Logging/RecentLogSinkTests.cs`
- Modify: `KhaozEngine.Diagnostics/README.md` (one section)

**Interfaces:**
- Produces:

```csharp
namespace KhaozEngine.Diagnostics;
public sealed class RecentLogSink : ILogSink
{
    public const int MaxEntryChars = 2000;
    public RecentLogSink(int capacity = 500);   // capacity < 1 throws ArgumentOutOfRangeException
    public int Capacity { get; }
    public IReadOnlyList<string> Snapshot();    // oldest first, each line LogFormatter.Format(entry)
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Keeps_the_newest_capacity_lines_oldest_first()   // capacity 3, emit m1..m5, Snapshot ends with "m3","m4","m5" in that order, Count 3
[Fact] public void Lines_use_the_log_formatter()                     // Snapshot()[0] == LogFormatter.Format(entry)
[Fact] public void Long_entry_is_cut_with_a_marker()                 // 5000 char message: line length == MaxEntryChars + " [truncated]".Length, ends with " [truncated]"
[Fact] public void Concurrent_emits_stay_bounded()                   // 4 threads x 1000 emits, capacity 500, Snapshot().Count == 500
[Fact] public void Snapshot_is_a_copy()                              // emit after snapshot does not change the earlier list
[Fact] public void Zero_capacity_throws()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t3-red -- dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter "FullyQualifiedName~RecentLogSinkTests"`. Expected: build FAIL, CS0246 `RecentLogSink`.

- [ ] **Step 3: Implement.** A fixed `string[]` ring with head and count under one lock. `Emit` formats and cuts outside the lock and never throws. `Flush` and `Dispose` do nothing. `InMemorySink` is untouched.

- [ ] **Step 4: Run to pass.** Same command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(diagnostics): add a bounded recent log ring"`.

---

### Task 4: ReportRedactor

**Files:**
- Create: `KhaozEngine.Diagnostics/ReportRedactor.cs`, `KhaozEngine.Foundation.Tests/Diagnostics/ReportRedactorTests.cs`
- Modify: `KhaozEngine.Diagnostics/README.md`

**Interfaces:**
- Consumes: `TelemetrySessionHeader.SelectEngineVariables`, `TelemetryHeaderValue`.
- Produces:

```csharp
public sealed class ReportRedactor
{
    public const string Mask = "[redacted]";
    public ReportRedactor(string? homeDirectory = null);   // null reads Environment.SpecialFolder.UserProfile
    public string Redact(string text);
    public IReadOnlyList<TelemetryHeaderValue> SelectEnvironment(IEnumerable<KeyValuePair<string, string?>> variables);
}
```

- [ ] **Step 1: Write the failing tests** (one per D-RED row plus the negatives)

```csharp
[Fact] public void Home_becomes_tilde()                    // new ReportRedactor("/Users/ann").Redact("open /Users/ann/Library/x.log") == "open ~/Library/x.log"
[Fact] public void Sibling_user_is_untouched()             // "/Users/anna/x" unchanged
[Fact] public void Windows_home_matches_both_separators()  // home @"C:\Users\Ann": @"C:\Users\Ann\AppData" gives @"~\AppData", "c:/users/ann/x" gives "~/x"
[Fact] public void Short_home_is_ignored()                 // home "/" leaves "/etc/hosts" unchanged
[Fact] public void Bearer_value_is_masked()                // "Authorization: Bearer abc.def-123456" == "Authorization: Bearer [redacted]"
[Fact] public void Jwt_is_masked()                         // "t=eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl" contains no "eyJ"
[Fact] public void Key_value_secrets_are_masked()          // "password=hunter22" gives "password=[redacted]", "\"apiKey\": \"abcd1234\"" gives "\"apiKey\": \"[redacted]\"", "AccountKey=abc==;" gives "AccountKey=[redacted];"
[Fact] public void Query_secrets_are_masked()              // "https://x/b?sv=1&sig=AbC%2B&se=2" gives "https://x/b?sv=1&sig=[redacted]&se=2"
[Fact] public void Provider_tokens_are_masked()            // "ghp_" + 36 'a' gives Mask
[Fact] public void Pem_block_is_masked()
[Fact] public void Ordinary_text_is_untouched()            // "Loaded 12 tiles in 3.2 ms", "tokenizer=fast", a 32 hex report id, "design=x"
[Fact] public void Environment_admits_only_KE_and_redacts_values()
// input KE_GPU=metal, HOME=/x, ke_trace=1, KE_TOKEN=password=abc
// output names "KE_GPU","KE_TOKEN","ke_trace" (ordinal), KE_TOKEN value "password=[redacted]"
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t4-red -- dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter "FullyQualifiedName~ReportRedactorTests"`. Expected: CS0246 `ReportRedactor`.

- [ ] **Step 3: Implement** the D-RED table as static readonly `Regex` fields (the home regex per instance), applied in table order, each guarded by the fail-closed timeout handler. Thread-safe and allocation-free when nothing matches. `Diagnostics` multi-targets `net8.0` and `net10.0`, so use only APIs present in both.

- [ ] **Step 4: Run to pass.** Same command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(diagnostics): add the bug report redactor"`.

---

### Task 5: Sections, isolation, redaction pass and truncation

**Files:**
- Modify: `KhaozEngine.BugReports/KhaozEngine.BugReports.csproj` (references `BugReports.Contracts`, `Diagnostics`, `Http`, `Platform`, `InternalsVisibleTo KhaozEngine.BugReports.Tests`), `KhaozEngine.BugReports.Tests/*.csproj` (add `BugReports`, `Diagnostics`, `Http`)
- Create: `KhaozEngine.BugReports/Sections/IBugReportSection.cs`, `BugReportContext.cs`, `CapturedSection.cs`, `BugReportSectionSet.cs`, `SectionJson.cs` (internal: `Redact`, `Truncate`, `ErrorObject`)
- Create: `KhaozEngine.BugReports.Tests/Core/BugReportSectionSetTests.cs`, `SectionJsonTests.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.BugReports.cs`

**Interfaces:**
- Consumes: `ReportRedactor` (Task 4), `BundleEntryNames.IsValidSectionKey`, `BugReportLimits` (Task 1).
- Produces: `IBugReportSection`, `BugReportContext`, `CapturedSection`, `BugReportSectionSet` as in the contract, plus `public const int MinSectionBytes = 64` and `public IReadOnlyList<string> Keys { get; }` on `BugReportSectionSet`. `Add` throws `ArgumentException` for an invalid or duplicate key and `ArgumentOutOfRangeException` for `MaxBytes < MinSectionBytes`. `BugReportContext` is `BugReportContext(string ReportId, DateTimeOffset CapturedAt, ReportRedactor Redactor, string? Subject = null)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Sections_capture_in_registration_order()
[Fact] public void Throwing_section_writes_error_and_the_rest_continue()
// section throws new InvalidOperationException("boom at /Users/ann/x"), redactor home /Users/ann
// json == "{\"error\":\"System.InvalidOperationException: boom at ~/x\"}", the next section is captured normally
[Fact] public void Unclosed_or_non_object_root_is_an_error()   // writes StartArray only, writes a bare number
[Fact] public void Every_string_and_property_name_is_redacted() // section writes {"password=x":"Bearer abcdefgh12"} without calling the redactor
[Fact] public void Oversized_section_is_truncated_to_valid_json_within_max_bytes()
// MaxBytes 200, writes {"lines":[ 100 strings of 20 chars ]}
// result length <= 200, JsonDocument parses it, root first property "truncated" is true, "lines" holds a prefix of the input in order
[Fact] public void Truncation_keeps_the_largest_fitting_prefix()   // adding one more leaf to the result would exceed MaxBytes
[Fact] public void Total_state_is_capped_at_one_mebibyte()
// three sections MaxBytes 512 KiB each writing 500 KiB: sum of Json lengths <= 1048576, third is truncated
[Fact] public void Section_with_budget_under_64_bytes_is_dropped()
[Fact] public void Add_rejects_duplicate_or_invalid_keys()
// SectionJsonTests
[Fact] public void Truncate_with_tiny_budget_yields_bare_marker()    // budget 18 gives {"truncated":true}
```

Architecture test added: `BugReports_ReferencesItsDeclaredSet` (exactly `BugReports.Contracts`, `Diagnostics`, `Http`, `Platform`). E1's `BugReports_StaysHeadless` keeps guarding the closure.

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t5-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.BugReports.Core"`. Expected: CS0246 `BugReportSectionSet`.

- [ ] **Step 3: Implement** per D-SEC. Writer options: `Indented = false`, `MaxDepth = 64`, encoder `JavaScriptEncoder.Create(UnicodeRanges.All)`. The truncation copier is the one non-obvious algorithm:

```text
Truncate(json, budget):
  leaves = number of primitive values in json (a property's value counts once, its name rides with it)
  if Copy(json, 0).Length > budget: return {"truncated":true}
  lo = 0, hi = leaves
  while lo < hi: mid = (lo + hi + 1) / 2, if Copy(json, mid).Length <= budget then lo = mid else hi = mid - 1
  return Copy(json, lo)
Copy(json, k):
  write StartObject, "truncated": true
  walk the root's members in order, writing containers as they open,
  stop before the (k+1)th leaf (its property name is not written),
  then close every open container including the root
```

- [ ] **Step 4: Run to pass.** Step 2 command plus `build-slot --label e2-t5-arch -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`. Expected: `Failed: 0` on both.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): capture isolated redacted report sections"`.

---

### Task 6: Draft and bundle writer

**Files:**
- Create: `KhaozEngine.BugReports/Bundles/BugReportDraft.cs`, `BugReportBundleWriter.cs`, `KhaozEngine.BugReports.Tests/Core/BugReportBundleWriterTests.cs`

**Interfaces:**
- Consumes: `CapturedSection` (Task 5), `ReportImage`, `ReportClip` (E1), Task 1 and 2 Contracts.
- Produces: `BugReportDraft`, `BugReportBundleWriter.Write(BugReportDraft)` exactly as the contract states, plus `public static IReadOnlyList<string> BugReportDraft.DefaultCategories` (the six ids).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Written_bundle_passes_the_validator()   // draft with 2 sections, PNG screenshot, 3 clip frames, BundleValidator.Validate(...).IsValid
[Fact] public void Manifest_is_first_and_lists_entries_in_zip_order()
[Fact] public void Screenshot_and_clip_toggles_omit_their_entries()   // IncludeScreenshot false: no screenshot.*, IncludeClip false: no clip/*
[Fact] public void Jpeg_screenshot_uses_the_jpg_name()
[Fact] public void Clip_offsets_and_render_times_reach_the_index()    // Offset 83.25 ms gives offsetMilliseconds 83.25
[Fact] public void Entries_are_stored_not_compressed()                // every entry CompressedLength == Length
[Fact] public void Blank_or_overlong_description_throws()             // ArgumentException
[Fact] public void More_than_120_clip_frames_throws()
[Fact] public void Unknown_media_type_throws()                        // "image/gif"
[Fact] public void Description_is_trimmed_at_the_end_only()           // "  a\n\n" written as "  a"
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t6-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~BugReportBundleWriterTests"`. Expected: CS0246 `BugReportDraft`.

- [ ] **Step 3: Implement.** Hash every payload first, then write a `ZipArchive` into a `MemoryStream` with `CompressionLevel.NoCompression` and each entry's `LastWriteTime` set to `CapturedAt`. `Category` must be 1 to 64 characters. A bundle over `MaxBundleBytes` throws `InvalidOperationException`. The writer never touches disk.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): write version 1 report bundles"`.

---

### Task 7: Submission types and HttpRelaySink

**Files:**
- Create: `KhaozEngine.BugReports/Sinks/BugReportSubmission.cs` (with `BugReportSubmitStatus`, `BugReportSubmitResult`), `IBugReportSink.cs`, `HttpRelaySink.cs`
- Create: `KhaozEngine.BugReports.Tests/Core/HttpRelaySinkTests.cs`, `KhaozEngine.BugReports.Tests/FakeHttpHandler.cs`

**Interfaces:**
- Consumes: `HttpRetry`, `HttpRetryPolicy`, `RelayRoutes`, `RelayHeaders`, `BugReportJson`, `ReportIds`.
- Produces: the contract's submission types, `IBugReportSink`, `HttpRelaySink`, plus `public static HttpRetryPolicy DefaultPolicy { get; }` (3 attempts, 60 s per attempt, retrying 408 and 5xx only) and `public HttpRetryPolicy RetryPolicy { get; init; } = DefaultPolicy` on `HttpRelaySink`. Result `Error` codes: `no_token`, `network`, `http_<status>`, or the relay's `RelayError.Error`.

| Outcome | Result |
| --- | --- |
| token null or blank | `Queued`, `no_token`, no request sent |
| 2xx | `Sent`, reference from `IntakeAccepted`, else `ReportIds.ReferenceCode` |
| 429 | `Queued`, `RetryAfter` from the header |
| 408 or 5xx after retries | `Queued`, `http_<status>` |
| transport fault after retries | `Queued`, `network` |
| other 4xx | `Rejected`, relay error text or `http_<status>` |
| caller cancellation | `OperationCanceledException` propagates |

- [ ] **Step 1: Write the failing tests** (zero-delay policy via `RetryPolicy = HttpRelaySink.DefaultPolicy with { Backoff = _ => TimeSpan.Zero }`)

```csharp
[Fact] public void Posts_zip_with_bearer_and_idempotency_key()
// base "https://relay.example/api" (no slash): request POST "https://relay.example/api/bug-reports",
// Authorization "Bearer tok", Idempotency-Key == report id, Content-Type "application/zip", body bytes equal
[Fact] public void Accepted_returns_the_relay_reference()
[Fact] public void Missing_token_queues_without_a_request()
[Fact] public void Too_many_requests_queues_with_retry_after()   // 429 Retry-After: 120 gives RetryAfter 120 s, one request
[Fact] public void Server_error_retries_then_queues()            // three 503s, Status Queued, Error "http_503", 3 requests
[Fact] public void Network_fault_queues()                        // handler throws HttpRequestException
[Fact] public void Validation_reject_is_not_queued()             // 422 {"error":"hash_mismatch: clip/000.jpg"}
[Fact] public void Caller_cancellation_propagates()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t7-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~HttpRelaySinkTests"`. Expected: CS0246 `HttpRelaySink`.

- [ ] **Step 3: Implement** the table through `HttpRetry.SendAsync` with a request factory that builds a fresh `ByteArrayContent` per attempt over the shared bundle memory. The constructor appends a trailing `/` to `relayBase` when missing.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): post bundles to the relay"`.

---

### Task 8: Offline queue and submitter

**Files:**
- Create: `KhaozEngine.BugReports/Sinks/BugReportOfflineQueue.cs`, `QueuedReportMetadata.cs` (internal record and its own source generated context), `BugReportSubmitter.cs`
- Create: `KhaozEngine.BugReports.Tests/Core/BugReportOfflineQueueTests.cs`, `BugReportSubmitterTests.cs`, `KhaozEngine.BugReports.Tests/ManualTimeProvider.cs`, `FakeSink.cs`

**Interfaces:**
- Consumes: Task 7 types.
- Produces: the contract's `BugReportOfflineQueue` plus:

```csharp
public int Count { get; }                                          // complete entries after orphan cleanup
internal Task<BugReportSubmitResult> SendOneAsync(string reportId, string subject, IBugReportSink sink, CancellationToken ct);
public sealed class BugReportSubmitter
{
    public BugReportSubmitter(IBugReportSink sink, BugReportOfflineQueue queue);
    public Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken);
    public Task<int> FlushAsync(string subject, CancellationToken cancellationToken);
}
```

`SubmitAsync` never throws except on caller cancellation. It enqueues first (D-QUEUE write-ahead). A null subject returns `Queued` with `signed_out`. An enqueue `IOException` or `UnauthorizedAccessException` logs a warning and sends directly, and a non-`Sent` result then carries `queue_failed`.

- [ ] **Step 1: Write the failing tests**

```csharp
// BugReportOfflineQueueTests, temp directory per test
[Fact] public void Enqueue_writes_zip_and_metadata_pair()
[Fact] public void Fourth_report_evicts_the_oldest()                 // queuedAt t0..t3, ids kept are t1..t3
[Fact] public void Bytes_over_30_MB_evict_oldest_first()             // three 12 MB bundles leave two
[Fact] public void Reports_older_than_7_days_expire()                // advance ManualTimeProvider 7 days + 1 s, Count 0 after FlushAsync
[Fact] public void Null_subject_binds_to_the_first_account_that_flushes()
// enqueue subject null, FlushAsync("a") sends it with Subject "a", a later FlushAsync("b") sends nothing
[Fact] public void Other_accounts_reports_are_left_alone()          // subject "a" queued, FlushAsync("b") returns 0, Count 1
[Fact] public void Flush_stops_at_first_queued_result()
[Fact] public void Rejected_reports_are_deleted()
[Fact] public void Orphans_and_corrupt_metadata_are_cleaned()       // lone .zip, lone .json, "{" metadata
// BugReportSubmitterTests
[Fact] public void Report_is_written_ahead_before_sending()          // sink blocks on a TaskCompletionSource, queue.Count == 1 during the send, 0 after Sent
[Fact] public void Queued_result_stays_on_disk()
[Fact] public void Signed_out_submission_never_calls_the_sink()
[Fact] public void Unwritable_queue_never_throws()                   // queue directory path is an existing file, SubmitAsync returns, Error "queue_failed" when the sink queues
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t8-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~BugReportOfflineQueueTests|FullyQualifiedName~BugReportSubmitterTests"`. Expected: CS0246 `BugReportOfflineQueue`.

- [ ] **Step 3: Implement** D-QUEUE. One `SemaphoreSlim` serialises flushes and a locked `HashSet<string>` tracks ids in flight. The constructor creates the directory lazily on first enqueue so a read-only path does not throw at startup.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): queue reports offline and bind them at sign-in"`.

---

### Task 9: Viewer page and LocalFolderSink

**Files:**
- Create: `KhaozEngine.BugReports/Viewer/viewer.html` (embedded resource, `LogicalName` `KhaozEngine.BugReports.Viewer.html`), `BugReportViewerModel.cs`, `BugReportViewerPage.cs`, `ClipHitches.cs` (internal), `KhaozEngine.BugReports/Sinks/LocalFolderSink.cs`
- Create: `KhaozEngine.BugReports.Tests/Core/BugReportViewerPageTests.cs`, `ClipHitchesTests.cs`, `LocalFolderSinkTests.cs`
- Modify: `KhaozEngine.BugReports/KhaozEngine.BugReports.csproj` (embedded resource)

**Interfaces:**
- Consumes: Contracts, `BundleValidator` (Task 2), `Browser.LaunchBrowserAsync`.
- Produces: `BugReportViewerModel`, `BugReportViewerPage.Render` and `LocalFolderSink` as in the contract, plus `internal static (double Median, IReadOnlyList<int> Hitches) ClipHitches.Find(IReadOnlyList<ClipIndexFrame> frames)`. Embedded data shape:

```json
{"manifest":{…},"clip":{…}|null,"medianRenderMilliseconds":10,"hitches":[3,5],"media":{"screenshot":"screenshot.png"|null,"video":"clip.mp4"|null},"sections":{"build":{…}},"reporter":{…}|null,"server":{…}|null,"logTail":["…"]}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// ClipHitchesTests
[Fact] public void Hitches_are_strictly_over_twice_the_median()   // [10,10,10,30,10,21,20] gives median 10, hitches [3,5]
[Fact] public void Even_count_median_is_the_mean_of_the_middle_two()   // [10,20,30,40] gives 25
[Fact] public void Empty_clip_has_no_hitches()
// BugReportViewerPageTests
[Fact] public void Script_close_in_user_text_cannot_end_the_data_block()
// description "</script><script>alert(1)</script>": occurrences of "</script>" in page == occurrences in the template,
// page contains "\u003c/script"
[Fact] public void Raw_markup_inside_section_json_is_reescaped()   // section bytes {"a":"<b>"} (unescaped), page has no "<b>"
[Fact] public void Page_loads_nothing_external()                   // no "http://", "https://", "<link", "@import", "src=\"//"
[Fact] public void Script_runs_only_by_hash()
// CSP meta script-src == "'sha256-" + base64(SHA256(app script text)) + "'", and script-src carries no 'unsafe-inline'
[Fact] public void Page_never_builds_html_from_strings()          // no "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval("
[Fact] public void Video_is_used_when_the_manifest_lists_clip_mp4()   // data media.video == "clip.mp4", else null
[Fact] public void Referrer_is_suppressed()
// LocalFolderSinkTests
[Fact] public void Writes_bundle_extracted_entries_and_viewer()
// <root>/<id>/bundle.zip, manifest.json, sections/build.json, clip/000.jpg, viewer.html, result Sent with ReportIds.ReferenceCode(id)
[Fact] public void Invalid_bundle_is_rejected_and_nothing_is_written()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t9-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~ClipHitchesTests|FullyQualifiedName~BugReportViewerPageTests|FullyQualifiedName~LocalFolderSinkTests"`. Expected: CS0246 `BugReportViewerPage`.

- [ ] **Step 3: Implement** per D-VIEW. The template has three placeholders: `{{TITLE}}` (HTML-encoded `Bug report R-XXXXXX`), `{{SCRIPT_HASH}}` and `{{REPORT_JSON}}`. Sections and the server snapshot are parsed and rewritten token by token with `Utf8JsonWriter.WriteStringValue(reader.GetString())`, never `WriteRawValue`. `LocalFolderSink` validates with `BundleValidator` before writing, extracts only validated names under `<root>/<reportId>/`, renders the page with no reporter and the last 50 `log` section lines, and calls `Browser.LaunchBrowserAsync(new Uri(viewerPath))` when `openViewer` is true.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): render the frame viewer and write local reports"`.

---

### Task 10: Server capture and snapshot poster

**Files:**
- Create: `KhaozEngine.BugReports/Server/ServerSnapshot.cs`, `BugReportServerCapture.cs`, `ServerSnapshotPoster.cs`
- Create: `KhaozEngine.BugReports.Tests/Core/BugReportServerCaptureTests.cs`, `ServerSnapshotPosterTests.cs`, `LoggingSerialCollection.cs` (collection with `DisableParallelization = true` for tests that read the ambient `Log`)

**Interfaces:**
- Consumes: `BugReportSectionSet` (Task 5), `ServerSnapshotWire`, `BugReportJson`, `RelayRoutes`, `RelayHeaders`, `HttpRetry`.
- Produces: `ServerSnapshot`, `BugReportServerCapture`, `ServerSnapshotPoster` as in the contract. `Capture` throws `ArgumentException` for an id failing `ReportIds.IsValid` (the game checks it first). `PostAsync` returns false with one `Log.Warn` line for any failure, including a body over `MaxServerSnapshotBytes`, and never throws except on caller cancellation.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void TryAccept_allows_five_per_account_per_rolling_hour()
// 5 true, 6th false, other subject true, advance 1 h then true
[Fact] public void Capture_runs_sections_with_the_report_id_and_time()
[Fact] public void Capture_passes_the_subject_into_the_context()   // a section sees context.Subject == "discord:42"
[Fact] public void Capture_rejects_invalid_ids()
[Fact] public void Poster_posts_wire_json_with_service_key()
// POST "https://relay.example/bug-reports/<id>/server", X-Report-Service-Key "k", body deserializes with subject and section "world"
[Fact] public void Poster_serializes_off_the_calling_thread()   // a section-free snapshot, handler records Environment.CurrentManagedThreadId != caller's
[Fact] public void Poster_failure_returns_false_with_a_warning()  // 500 three times, false, one Warn line captured by InMemorySink
[Fact] public void Oversized_snapshot_is_not_posted()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t10-red -- dotnet test KhaozEngine.BugReports.Tests/KhaozEngine.BugReports.Tests.csproj -c Release --filter "FullyQualifiedName~BugReportServerCaptureTests|FullyQualifiedName~ServerSnapshotPosterTests"`. Expected: CS0246 `BugReportServerCapture`.

- [ ] **Step 3: Implement.** Per-subject timestamp queues under a lock, pruned on each call, empty queues removed. The poster runs serialization inside `Task.Run` and posts through `HttpRetry` with the Task 7 default policy shape.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): capture and post server snapshots"`.

---

### Task 11: JPEG thumbnail decoder

**Files:**
- Create: `KhaozEngine.Imaging/JpegThumbnailDecoder.cs` (with `JpegThumbnail`), `KhaozEngine.Render.Tests/Imaging/JpegThumbnailDecoderTests.cs`
- Modify: `KhaozEngine.Imaging/README.md`

**Interfaces:**
- Consumes: `JpegWriter.Encode` (E1) in tests only.
- Produces:

```csharp
namespace KhaozEngine.Imaging;
public readonly record struct JpegThumbnail(int Width, int Height, byte[] Rgba);
public static class JpegThumbnailDecoder
{
    // Baseline sequential Huffman, 8-bit, 1 or 3 components, sampling factors 1 or 2, restart markers.
    // Output is ceil(width / 8) x ceil(height / 8), one pixel per 8x8 luma block from DC coefficients.
    // InvalidDataException on malformed input, NotSupportedException on progressive, arithmetic or 12-bit.
    public static JpegThumbnail DecodeEighthScale(ReadOnlySpan<byte> jpeg);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Solid_colour_decodes_within_tolerance()   // 64x48 RGB (200,40,90) at quality 90 gives 8x6, every channel within 8
[Fact] public void Halves_keep_their_colours()              // 64x64 left red, right blue: left four columns R > 200 and B < 60, right four B > 200 and R < 60
[Fact] public void Odd_sizes_round_up()                     // 61x37 gives 8x5
[Fact] public void Progressive_is_not_supported()           // FF D8 FF C2 header bytes
[Fact] public void Truncated_input_is_invalid_data()        // first half of a valid file
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t11-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~JpegThumbnailDecoderTests"`. Expected: CS0103 `JpegThumbnailDecoder`.

- [ ] **Step 3: Implement** per ITU T.81: parse DQT, DHT, SOF0 or SOF1, DRI and SOS, decode each block's DC difference and skip its AC coefficients by run and size, keep DC prediction per component (reset on RSTn), dequantise DC, value is `DC / 8 + 128` clamped, chroma blocks map to the luma block grid by sampling ratio, convert YCbCr to RGB with the JFIF matrix, alpha 255.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(imaging): decode one-eighth scale JPEG thumbnails"`.

---

### Task 12: TextArea layout

**Files:**
- Create: `KhaozEngine.Gui/TextAreaLayout.cs`, `KhaozEngine.Gui.Tests/Gui/TextAreaLayoutTests.cs`

**Interfaces:**
- Produces (internal, `KhaozEngine.Gui` already grants `InternalsVisibleTo` to `KhaozEngine.Gui.Tests`):

```csharp
internal readonly record struct TextAreaLine(int Start, int Length);   // Length excludes the '\n'
internal static class TextAreaLayout
{
    internal static IReadOnlyList<TextAreaLine> Wrap(ITextMeasurer font, string text, float width, float scale = 1f);
    internal static (int Line, float X) CaretPosition(ITextMeasurer font, string text, IReadOnlyList<TextAreaLine> lines, int caret, float scale = 1f);
    internal static int IndexAt(ITextMeasurer font, string text, IReadOnlyList<TextAreaLine> lines, int line, float x, float scale = 1f);
}
```

- [ ] **Step 1: Write the failing tests** (measurer 10 px per UTF-16 unit, line height 20)

```csharp
[Fact] public void Newline_ends_a_line()                    // "ab\ncd", width 100: (0,2),(3,2)
[Fact] public void Words_wrap_after_the_last_space()        // "hello world again", width 120: (0,12),(12,5)
[Fact] public void Long_word_hard_breaks()                  // "abcdefghij", width 40: (0,4),(4,4),(8,2)
[Fact] public void Trailing_newline_adds_an_empty_line()    // "ab\n": (0,2),(3,0)
[Fact] public void Empty_text_is_one_empty_line()
[Fact] public void Surrogate_pairs_are_never_split()        // "a\U0001F600b", width 20: no line starts at index 2
[Fact] public void Caret_at_a_soft_wrap_sits_on_the_next_line()   // "hello world again", caret 12: (1, 0)
[Fact] public void IndexAt_picks_the_nearest_boundary()     // line 0 of "abcd", x 14 gives 1, x 16 gives 2
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t12-red -- dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~TextAreaLayoutTests"`. Expected: CS0103 `TextAreaLayout`.

- [ ] **Step 3: Implement** greedy wrap per D-TA: trailing spaces stay on their line, at least one scalar per line, widths through `ITextMeasurer.Measure(ReadOnlySpan<char>)` times `scale`.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(gui): lay out wrapped multi-line text"`.

---

### Task 13: TextArea widget

**Files:**
- Create: `KhaozEngine.Gui/TextArea.cs`, `KhaozEngine.Gui.Tests/Gui/TextAreaTests.cs`
- Modify: `KhaozEngine.Gui/README.md`

**Interfaces:**
- Consumes: Task 12, `TextEntry.Apply`, `Pointer`, `InputState`.
- Produces:

```csharp
namespace KhaozEngine.Gui;
public sealed class TextArea
{
    public const int DefaultMaxLength = 4000;
    public TextArea(Rect bounds, ITextMeasurer measurer, SpriteFont? font = null);
    public Rect Bounds;
    public int MaxLength = DefaultMaxLength;
    public bool IsFocused;
    public SpriteFont? Font;
    public LocalizedText PlaceholderContent;
    public float TextScale = 1f;
    public string Text { get; }
    public int Caret { get; }
    public float ScrollY { get; }
    public bool TextChanged { get; }
    public bool CursorVisible { get; }
    public void SetText(string value);      // normalises line endings, clamps at a scalar boundary, caret to end
    public bool Update(Pointer pointer, InputState input, float dt);   // returns IsFocused
    public void Draw(SpriteBatch batch, Texture2D white);              // no-op without Font, scissors to Bounds
    public void Focus();
    public void Unfocus();
}
```

Inner padding 8 px horizontal and 6 px vertical, colours and blink rate as `TextInput`.

- [ ] **Step 1: Write the failing tests** (the `TextInputTests` frame helper and `MouseFrames`, `[Collection("AmbientLocalization")]`)

```csharp
[Fact] public void Tap_focuses_and_release_outside_unfocuses()
[Fact] public void Committed_text_inserts_at_the_caret()      // "ac", Left once, type "b": Text "abc", Caret 2
[Fact] public void Enter_inserts_a_newline_but_ctrl_enter_does_not()
[Fact] public void Backspace_and_delete_remove_one_scalar_each_side()
[Fact] public void SetText_normalises_line_endings()          // "a\r\nb\rc" gives "a\nb\nc"
[Fact] public void Long_unbroken_paste_wraps_and_caps()      // SetText(new string('x', 5000)): Length 4000, typing more changes nothing, Wrap yields several lines
[Fact] public void Up_and_down_keep_the_column()
[Fact] public void Home_and_end_use_the_visual_line()
[Fact] public void Tap_places_the_caret()
[Fact] public void Caret_line_scrolls_into_view_and_wheel_scrolls_three_lines()
// 30 lines in a 5-line area, caret at end: ScrollY == content height - inner height, wheel up 1: ScrollY drops by 60
[Fact] public void Unfocused_area_ignores_typing()
[Fact] public void TextChanged_is_true_only_on_the_changing_frame()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t13-red -- dotnet test KhaozEngine.Gui.Tests/KhaozEngine.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~TextAreaTests"`. Expected: CS0246 `TextArea`.

- [ ] **Step 3: Implement** per D-TA. Editing runs `TextEntry.Apply(Text[..Caret], input, MaxLength - (Text.Length - Caret))` then rejoins the tail, normalises `\r\n` and `\r`, and drops other control characters except `\n`. Navigation keys use `InputState.WasTyped` so they repeat.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(gui): add a multi-line TextArea"`.

---

### Task 14: Held input and the GameApp overlay seam

**Files:**
- Create: `KhaozEngine.Windowing/InputState.Hold.cs`, `KhaozEngine.Windowing.Tests/InputStateHoldTests.cs`
- Create: `KhaozEngine.Game/IGameOverlay.cs` (with `GameOverlayContext`), `KhaozEngine.Game/GameOverlayHost.cs` (internal), `KhaozEngine.Game.Tests/Game/GameOverlayHostTests.cs`
- Modify: `KhaozEngine.Game/GameApp.cs` (field, `AddOverlay`, `RemoveOverlay`, two call sites, `_ui.Update(frame)` moved before the overlay update), `KhaozEngine.Game/README.md`

**Interfaces:**
- Produces:

```csharp
namespace KhaozEngine.Windowing;
public sealed partial class InputState
{
    // Keeps Width, Height, WindowFocused, MousePosition, FramebufferScale, PointerCaptured, TextInputAvailable.
    // Clears key and button sets, deltas, scroll, gestures, touches, gamepads and TextInput.
    public InputState WithoutInteraction();
}
namespace KhaozEngine.Game;
public sealed record GameOverlayContext(AppWindow Window, UiViewport Ui, Render2DSurface Surface);
public interface IGameOverlay
{
    void Attach(GameOverlayContext context);       // once, inside AddOverlay
    bool Update(InputState input, float realDt);   // raw input, true holds input from the game this frame
    void DrawUi(SpriteBatch batch);                // batch already begun in the UiViewport
}
public abstract class GameApp
{
    public void AddOverlay(IGameOverlay overlay);       // throws InvalidOperationException when added twice
    public bool RemoveOverlay(IGameOverlay overlay);
}
internal sealed class GameOverlayHost
{
    internal void Add(IGameOverlay overlay, GameOverlayContext context);
    internal bool Remove(IGameOverlay overlay);
    internal bool IsEmpty { get; }
    internal InputState Update(InputState input, float realDt);   // input itself when nothing holds, else input.WithoutInteraction()
    internal void DrawUi(SpriteBatch batch);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// InputStateHoldTests
[Fact] public void Held_input_carries_no_text_or_edges()
// source has F10 down and pressed, left mouse pressed, scroll 2, TextInput "abc", gamepad connected, 1280x720, focused
// held: KeysDown, KeysPressed, MousePressed empty, ScrollDelta 0, TextInput "", Gamepads empty,
// Width 1280, Height 720, WindowFocused true, MousePosition and FramebufferScale preserved
// GameOverlayHostTests
[Fact] public void No_overlays_return_the_same_instance()
[Fact] public void Any_holding_overlay_holds_the_game()
[Fact] public void Every_overlay_updates_in_add_order_even_after_one_holds()
[Fact] public void Draw_runs_in_add_order()
[Fact] public void Add_attaches_once_and_rejects_a_second_add()
[Fact] public void Removed_overlay_stops_updating()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t14-red -- dotnet test KhaozEngine.Windowing.Tests/KhaozEngine.Windowing.Tests.csproj -c Release --filter "FullyQualifiedName~InputStateHoldTests"` and `build-slot --label e2-t14-red-game -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter "FullyQualifiedName~GameOverlayHostTests"`. Expected: CS1061 `WithoutInteraction` and CS0246 `GameOverlayHost`.

- [ ] **Step 3: Implement.** `WithoutInteraction` uses the private full constructor `CopyPointerGestures` already calls. In `GameApp.PrepareAfterTick` (non-suppressed branch) the order becomes: viewport update and `OnResize`, `_ui.Update(frame)`, `_input = _overlays.Update(frame.Input, frame.Dt)`, then `_pointer.Update`, `_uiPointer.Update`, resume, `OnUpdate`, HUD. In `RecordPhase`, after the `OnDrawUi` pass and before the HUD, when `!_overlays.IsEmpty` begin the batch in `_ui`, call `_overlays.DrawUi`, end. A render-suppressed frame does not update overlays. `AddOverlay` passes `new GameOverlayContext(_window, _ui, _surface2D)`.

- [ ] **Step 4: Run to pass.** Both Step 2 commands, `Failed: 0`. Then `wc -l KhaozEngine.Game/GameApp.cs` reports at most 710.

- [ ] **Step 5: Commit.** `git commit -m "feat(game): add GameApp overlays that can hold input"`.

---

### Task 15: Built-in client sections

**Files:**
- Modify: `KhaozEngine.BugReports.Client/KhaozEngine.BugReports.Client.csproj` (add references `BugReports`, `BugReports.Contracts`, `Diagnostics`, `Gui`, `Game`, `Imaging`, `App` where E1 has not, and `InternalsVisibleTo KhaozEngine.BugReports.Client.Tests`)
- Modify: `KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj` (E1 created it with `RootNamespace` `KhaozEngine.Tests.BugReports.Client`) so it references `BugReports.Client`, `BugReports`, `BugReports.Contracts`, `Diagnostics`, `App`, `Gui`, `Game`, `Windowing`, `Render2D`, `Imaging`, `Primitives`, `TestSupport.Windowing`
- Create: `KhaozEngine.BugReports.Client/Sections/ClientFacts.cs` (the five facts records and their gatherers), `BuildSection.cs`, `PlatformSection.cs`, `GraphicsSection.cs`, `PerformanceSection.cs`, `LogSection.cs`, `SessionSection.cs`, `InputSection.cs`, `FrameTimeRing.cs`
- Create: `KhaozEngine.BugReports.Client.Tests/Sections/ClientSectionsTests.cs`, `FrameTimeRingTests.cs`, `ClientFactsTests.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.BugReports.cs` (E1's `BugReportsClientTests_ReferenceOnlyTheClient` becomes `BugReportsClientTests_ReferenceTheirDeclaredSet`, asserting the exact reference list above)

**Interfaces:**
- Consumes: `IBugReportSection` (Task 5), `RecentLogSink`, `ReportRedactor`, `FrameStats`, `PassTimings`, `TelemetrySessionHeader.EngineVersion`, `ActionMap`.
- Produces (public, namespace `KhaozEngine.BugReports.Client`):

```csharp
public sealed record BuildFacts(string GameId, string GameName, string Version, string? BuildName, string EngineVersion, string? Configuration, string? Commit);
public sealed record PlatformFacts(string Os, string OsArchitecture, string ProcessArchitecture, string Runtime, int CpuCount,
    long TotalMemoryBytes, string Locale, int WindowWidth, int WindowHeight, int FramebufferWidth, int FramebufferHeight,
    float ContentScale, IReadOnlyList<TelemetryHeaderValue> Environment);
public sealed record GraphicsFacts(string Backend, string Adapter, int SwapchainWidth, int SwapchainHeight, string PresentMode,
    bool? SoftwareAdapter, string? DeviceLossReason, IReadOnlyList<string>? InjectedModules, IReadOnlyDictionary<string, string> Capabilities);
public sealed record SessionFacts(TimeSpan Uptime, string? SessionLogName, string? PreviousCrashFile, DateTimeOffset? PreviousCrashAt);
public sealed record InputFacts(IReadOnlyList<string> Devices, IReadOnlyList<string> TriggerBindings);
public sealed class BuildSection(Func<BuildFacts> facts) : IBugReportSection;          // key "build", 4 KiB
public sealed class PlatformSection(Func<PlatformFacts> facts) : IBugReportSection;    // "platform", 8 KiB
public sealed class GraphicsSection(Func<GraphicsFacts> facts) : IBugReportSection;    // "graphics", 8 KiB
public sealed class PerformanceSection(FrameStats stats, FrameTimeRing frames, Func<PassTimings?> passes) : IBugReportSection;  // "performance", 32 KiB
public sealed class LogSection(RecentLogSink log) : IBugReportSection;                 // "log", 256 KiB
public sealed class SessionSection(Func<SessionFacts> facts) : IBugReportSection;      // "session", 4 KiB
public sealed class InputSection(Func<InputFacts> facts) : IBugReportSection;          // "input", 4 KiB
public sealed class FrameTimeRing { public const int DefaultCapacity = 300; public FrameTimeRing(int capacity = DefaultCapacity); public void Add(float milliseconds); public float[] Snapshot(); }
internal static class ClientFacts
{
    internal static BuildFacts Build(Assembly? entry, string gameId, string gameName);
    internal static (string? File, DateTimeOffset? At) FindPreviousCrash(string directory, DateTimeOffset processStart);
    internal static IReadOnlyList<string> DescribeBindings(ActionMap map, string actionId);   // e.g. "Key:F10", "GamepadButton:Back"
    internal static PlatformFacts Platform(AppWindow window, ReportRedactor redactor);
    internal static GraphicsFacts Graphics(AppWindow window);
    internal static IReadOnlyList<string> Devices(InputState input);   // "keyboard", "mouse", "gamepad <index>" per connected pad
}
```

Section JSON property names (camelCase): `build` `{gameId, gameName, version, buildName, engineVersion, configuration, commit}`. `platform` `{os, osArchitecture, processArchitecture, runtime, cpuCount, totalMemoryBytes, locale, window:{width,height}, framebuffer:{width,height}, contentScale, env:{NAME:value}}`. `graphics` `{backend, adapter, swapchain:{width,height}, presentMode, softwareAdapter, deviceLossReason, injectedModules, capabilities:{…}}`. `performance` `{fps, frameMsAvg, frameMsMin, frameMsMax, managedBytes, passes:[{name, avgMs, minMs, maxMs}], frameTimesMs:[…]}`. `log` `{lines:[…]}`. `session` `{uptimeSeconds, sessionLog, previousCrash:{file, writtenAt}|null}`. `input` `{devices:[…], trigger:[…]}`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Built_in_keys_and_budgets()   // keys and MaxBytes exactly as listed, sum <= 400 KiB
[Fact] public void Build_section_writes_its_facts()   // parse JSON, gameId "grimhollow", commit "3f2a9c1", null buildName as JSON null
[Fact] public void Performance_section_writes_the_last_300_frame_times()   // 400 samples added, frameTimesMs length 300, first value is sample 101
[Fact] public void Log_section_writes_the_ring_lines()
[Fact] public void Platform_section_env_holds_only_what_it_was_given()
[Fact] public void Session_section_writes_file_name_not_path()   // "/Users/ann/logs/session-1.log" gives "session-1.log"
[Fact] public void Graphics_capabilities_are_an_object()
// ClientFactsTests
[Fact] public void Build_facts_read_product_version_and_commit()   // FromAssembly of the test assembly, Commit is the text after '+' or null
[Fact] public void Previous_crash_is_the_newest_file_before_process_start()   // temp dir, three *-crash-*.txt with set write times
[Fact] public void Bindings_describe_key_and_gamepad_sources()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t15-red -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.BugReports.Client.Sections"`. Expected: CS0246 `BuildSection`.

- [ ] **Step 3: Implement.** Sections only write facts. `Capabilities` is every public `bool`, `int` and `string` property of `GpuCapabilities` by reflection, ordinal by name, so it needs no upkeep. Crash files are the `CrashReport.DefaultDirectory` files whose name contains `-crash`.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): add the built-in client report sections"`.

---

### Task 16: Strings, screen model, thumbnails and BugReportScreen

**Files:**
- Create: `KhaozEngine.BugReports.Client/Screen/BugReportStrings.cs`, `BugReportScreenModel.cs`, `BugReportScreenLayout.cs` (internal), `ReportThumbnails.cs`, `BugReportScreen.cs` (with `BugReportScreenResources`, `BugReportScreenResult`)
- Create: `KhaozEngine.BugReports.Client.Tests/Screen/BugReportStringsTests.cs`, `BugReportScreenModelTests.cs`, `ReportThumbnailsTests.cs`, `BugReportScreenTests.cs`, `KhaozEngine.BugReports.Client.Tests/AmbientLocalizationCollection.cs` (`DisableParallelization = true`)

**Interfaces:**
- Consumes: `TextArea` (Task 13), `Dropdown`, `Toggle`, `Button`, `Screen`, `JpegThumbnailDecoder` (Task 11), `BugReportDraft` (Task 6).
- Produces:

```csharp
public static class BugReportStrings
{
    public static readonly StringId Title, DescriptionLabel, DescriptionPlaceholder, DescriptionCounter, CategoryFieldLabel,
        ScreenshotToggle, ClipToggle, ClipUnavailable, Included, SignedOut, Send, Cancel, Capturing,
        ToastSent, ToastQueued, ToastQueuedSignedOut, ToastRejected, ToastFailed, ToastFlushed;
    public static IStringCatalog EnglishDefaults { get; }
    public static string Resolve(StringId id);
    public static string Format(StringId id, params object?[] args);
    public static string CategoryLabel(string categoryId);   // "bugreport.category.<id lowercased>", else the raw id
}
public sealed class BugReportScreenModel
{
    public BugReportScreenModel(IReadOnlyList<string> categories, bool signedIn, bool screenshotAvailable, bool clipAvailable);
    public string Description { get; set; }        // clamped to BugReportLimits.MaxDescriptionChars
    public int CategoryIndex { get; set; }         // defaults to "Other" when listed, else 0
    public string Category { get; }
    public bool IncludeScreenshot { get; set; }    // always false while unavailable
    public bool IncludeClip { get; set; }
    public bool SignedIn { get; }
    public bool ScreenshotAvailable { get; set; }  // the flow clears it when the capture faults
    public bool ClipAvailable { get; }
    public bool CanSend { get; }                   // description has a non-whitespace character
    public void ApplyTo(BugReportDraft draft);
}
public static class ReportThumbnails
{
    public const int ScreenshotLongEdge = 320;
    public static ImageRgba FromScreenshot(ReportImage image);        // PNG via ImageRgba.Decode then box downscale, JPEG via the decoder
    public static ImageRgba FromClipFrame(ReportClipFrame frame);
    public static IReadOnlyList<int> StripIndices(int frameCount, int strip = 4);   // evenly spaced, first and last included
}
public sealed record BugReportScreenResources(ITextMeasurer Measurer, SpriteFont? Font, Texture2D? White, IDesignViewport Viewport);
public enum BugReportScreenResult { Open, Send, Cancel }
public sealed class BugReportScreen : Screen
{
    public BugReportScreen(BugReportScreenModel model, BugReportScreenResources resources);
    public BugReportScreenModel Model { get; }
    public BugReportScreenResult Result { get; }
    public event Action<BugReportScreenResult>? Closed;
    public void SetThumbnails(Texture2D? screenshot, IReadOnlyList<Texture2D> strip);
    internal IReadOnlyList<string> InfoLines();     // resolved Included line, plus SignedOut when not signed in
    internal BugReportScreenLayout Layout { get; }
}
internal readonly record struct BugReportScreenLayout(Rect Panel, Rect Thumbnail, Rect Strip, Rect Description,
    Rect Category, Rect ScreenshotToggle, Rect ClipToggle, Rect Send, Rect Cancel)
{
    internal static BugReportScreenLayout Compute(Rect viewportBounds);
}
```

Keys and English defaults (fixed copy):

| Key | English |
| --- | --- |
| `bugreport.screen.title` | Report a bug |
| `bugreport.screen.description.label` | What happened? |
| `bugreport.screen.description.placeholder` | Describe what you saw and what you expected to happen. |
| `bugreport.screen.description.counter` | {0}/{1} |
| `bugreport.screen.category.label` | Category |
| `bugreport.screen.screenshot.toggle` | Include screenshot |
| `bugreport.screen.clip.toggle` | Include the last few seconds of video |
| `bugreport.screen.clip.unavailable` | Video is not available on this device |
| `bugreport.screen.included` | Your account and your game and system info are included. |
| `bugreport.screen.signed-out` | You are not signed in. This report will be sent after you sign in. |
| `bugreport.screen.send` | Send |
| `bugreport.screen.cancel` | Cancel |
| `bugreport.screen.capturing` | Capturing... |
| `bugreport.toast.sent` | Bug report sent. Reference {0} |
| `bugreport.toast.queued` | Bug report saved. It will be sent the next time you sign in. |
| `bugreport.toast.queued-signed-out` | Bug report saved. It will be sent after you sign in. |
| `bugreport.toast.rejected` | Bug report not accepted ({0}). |
| `bugreport.toast.failed` | The bug report could not be prepared. |
| `bugreport.toast.flushed` | {0} saved bug reports sent. |
| `bugreport.category.gameplay` and the other five | Gameplay, Visual, Audio, Performance, Interface, Other |

Screen behaviour: modal (`PassUpdateThrough` false), `DrawOrder` 20000, `TextArea` focused on open, Escape cancels unless the dropdown is open (then it closes the dropdown), Ctrl or Cmd plus Enter sends when `CanSend`, the Send button is disabled while `!CanSend`, the clip toggle is disabled with the unavailable line when `!ClipAvailable`. Panel 640 by 560 points centred, scaled to fit 90 percent of the viewport.

- [ ] **Step 1: Write the failing tests**

```csharp
// BugReportStringsTests ([Collection("AmbientLocalization")])
[Fact] public void English_defaults_cover_every_key()       // every StringId field resolves to the table text
[Fact] public void Wired_catalog_overrides_a_line()         // catalog with "bugreport.screen.send" = "Envoyer"
[Fact] public void English_templates_take_the_documented_arguments()   // counter 2, sent 1, rejected 1, flushed 1, others 0
[Fact] public void Unknown_category_falls_back_to_its_id()  // CategoryLabel("Crafting") == "Crafting"
// BugReportScreenModelTests
[Fact] public void Defaults_to_Other_and_cannot_send_blank()
[Fact] public void Unavailable_media_cannot_be_included()
[Fact] public void Description_is_clamped_to_4000()
// ReportThumbnailsTests
[Fact] public void Strip_indices_are_evenly_spaced()        // (60,4) gives [0,20,39,59], (3,4) gives [0,1,2], (0,4) gives []
[Fact] public void Png_screenshot_downscales_to_320()       // 1280x720 PNG gives 320x180
// BugReportScreenTests (fake measurer, null font and white, DesignViewport 1280x720, driven through a ScreenStack)
[Fact] public void Opens_modal_with_the_description_focused()
[Fact] public void Typing_fills_the_description()            // committed text "Fell through"
[Fact] public void Send_is_ignored_until_there_is_text()     // tap Layout.Send with blank text, Result Open
[Fact] public void Ctrl_enter_sends_when_text_is_present()   // Result Send, Closed raised once
[Fact] public void Escape_cancels()
[Fact] public void Toggles_flip_screenshot_and_clip()
[Fact] public void Clip_toggle_is_inert_when_clip_is_unavailable()
[Fact] public void Signed_out_variant_adds_the_pending_line()   // InfoLines() == [Included, SignedOut] versus [Included]
[Fact] public void Category_dropdown_lists_localized_labels()
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t16-red -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release --filter "FullyQualifiedName~KhaozEngine.Tests.BugReports.Client.Screen"`. Expected: CS0246 `BugReportStrings`.

- [ ] **Step 3: Implement.** `BugReportStrings` mirrors `DiagnosticsOverlayStrings` (`Resolve` checks `LocalizationContext.Catalog.TryGet` and falls back to `EnglishDefaults`, formats with the current culture for a wired catalog and invariant culture for English). Widgets receive `LocalizedText.Raw(BugReportStrings.Resolve(...))` each frame, following `BootScreenRenderer`. Widget state lives in the screen, rules live in the model. `Draw` draws a scrim over `Viewport.WindowBounds`, the panel, thumbnails (or the Capturing line until `SetThumbnails`), and every widget.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): add the bug report confirmation screen"`.

---

### Task 17: BugReportFlow

**Files:**
- Create: `KhaozEngine.BugReports.Client/BugReportServiceOptions.cs`, `IBugReportCapture.cs` (internal, plus `RecorderCapture` adapter over `FrameHistoryRecorder`), `BugReportFlow.cs` (internal), `BugReportFlowState.cs` (internal)
- Create: `KhaozEngine.BugReports.Client.Tests/BugReportFlowTests.cs`, `BugReportServiceOptionsTests.cs`, `FakeCapture.cs`, `FakeSink.cs` (E1's `ManualTimeProvider.cs` in this project is reused and gains a `GetUtcNow` override that advances with its timestamp, if it lacks one)

**Interfaces:**
- Consumes: Tasks 5 to 8, 15, 16, `FrameHistoryRecorder` (E1), `ActionMapController`.
- Produces: the contract's `BugReportServiceOptions` with these additions, then the internal flow types:

```csharp
public sealed record BugReportServiceOptions
{
    // contract members, plus
    public string? TriggerBindingsJson { get; init; }      // persisted rebinding, applied over DefaultKey
    public Action<string>? SaveTriggerBindings { get; init; }
    public string? SessionLogPath { get; init; }           // the value SessionLog.Configure returned
    // GameId ^[a-z0-9][a-z0-9-]{0,31}$, non-blank display name, 1 to 16 category ids of 1 to 64 chars,
    // Recording.Duration x Recording.FramesPerSecond <= 120. ArgumentException or ArgumentOutOfRangeException.
    internal static void Validate(BugReportServiceOptions options);
}
```

```csharp
internal interface IBugReportCapture
{
    bool ClipAvailable { get; }                    // recorder available and recording
    ReportClip? FreezeClip();                      // null when not recording
    void Thaw();
    Task<ReportImage> CaptureScreenshotAsync();
}
internal enum BugReportFlowState { Idle, Captured, Open }
internal sealed class BugReportFlow
{
    internal BugReportFlow(BugReportServiceOptions options, IBugReportCapture capture, BugReportSectionSet sections,
        BugReportSubmitter submitter, Func<string?> currentSubject, Func<BugReportScreenModel, BugReportScreen> screenFactory,
        Func<ImageRgba, Texture2D?> textureFactory, TimeProvider time);
    internal ActionMapController TriggerControls { get; }
    internal BugReportFlowState State { get; }
    internal ScreenStack Screens { get; }
    internal ToastStack Toasts { get; }
    internal Task? PendingWork { get; }            // the latest send, flush or thumbnail task, for tests to await
    internal event Action<string>? ReportCaptured;
    internal event Action<BugReportSubmitResult>? ReportSubmitted;
    internal bool Begin();                         // false unless Idle
    internal bool Update(InputState input, float realDt, IDesignViewport? viewport);   // true while not Idle
    internal Task<int> RetryQueuedAsync(CancellationToken cancellationToken);
}
```

`Update` order: drain completed work posted to a `ConcurrentQueue<Action>` (toasts, events, textures), poll `currentSubject()` and start a flush when it changes to a non-null value, update the trigger map with the raw input, then Idle plus trigger or pending `Begin` captures per D-HOOK (state `Captured`, returns true), `Captured` pushes the screen (state `Open`) and starts thumbnail decoding on the thread pool, `Open` updates `Screens` with the input and viewport and handles `Result`, then `Toasts.Update(realDt)`. Send copies the model into the draft, waits for the screenshot task at most 5 s when it is included, builds the bundle inside `Task.Run`, submits through the submitter and posts the toast. A build failure posts `ToastFailed`. Send and Cancel both `Thaw()` and pop the screen in the same frame. The constructor calls `BugReportServiceOptions.Validate`. The screen model uses `options.Categories ?? BugReportDraft.DefaultCategories`. The trigger map declares `InputAction.Button(BugReportFlow.TriggerActionId)` (an internal const `"ReportBug"`, which Task 18's public `BugReportService.TriggerActionId` is defined as) bound to `options.DefaultKey`, wrapped in an `ActionMapController` over `TriggerBindingsJson` and `SaveTriggerBindings`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Trigger_captures_in_the_same_frame_and_opens_next_frame()
// frame 1 F10 pressed: ReportCaptured once with ReportIds.IsValid id, FreezeClip 1, screenshot 1, Screens empty, Update true
// frame 2: one BugReportScreen in Screens, State Open
[Fact] public void Sections_are_captured_in_the_trigger_frame()   // counting section Write count 1 after frame 1
[Fact] public void Context_carries_the_signed_in_subject()   // subject "discord:42" reaches the section; null before sign-in
[Fact] public void Recorder_screenshot_limits_come_from_contracts()
[Fact] public void Second_trigger_while_open_is_ignored()
[Fact] public void Begin_captures_on_the_next_update_and_fails_while_open()
[Fact] public void Cancel_thaws_and_sends_nothing()               // Thaw 1, sink calls 0, next Update returns false
[Fact] public void Send_submits_a_valid_bundle_and_toasts_the_reference()
// sink receives one submission, BundleValidator valid, manifest description "Fell through", toast "Bug report sent. Reference R-…"
[Fact] public void Signed_out_send_is_queued_without_the_sink()   // toast is the queued-signed-out line
[Fact] public void Sign_in_flushes_the_queue_once()               // subject null, send, subject "acct-1": sink gets it with Subject "acct-1", flushed toast
[Fact] public void Screenshot_failure_disables_the_toggle_and_still_sends()
[Fact] public void No_clip_sends_without_clip_entries()          // ClipAvailable false, toggle off, no "clip/" entry
[Fact] public void Rejected_send_toasts_the_reason()
[Fact] public void Typing_in_the_screen_never_reaches_the_game() // Update returns true on every frame the screen is open
[Fact] public void Trigger_rebind_saves_through_the_callback()   // SaveTriggerBindings receives JSON naming F9 after a rebind
// BugReportServiceOptionsTests
[Fact] public void Defaults_match_the_contract()                 // DefaultKey F10, RecordingEnabled true, Categories null, Recording default
[Theory] [InlineData("")] [InlineData("Grim Hollow")] [InlineData("-grim")]
public void Invalid_game_id_is_rejected(string id)
[Fact] public void Too_many_or_blank_categories_are_rejected()
[Fact] public void Recording_longer_than_120_frames_is_rejected() // Duration 10 s at 30 fps
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t17-red -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release --filter "FullyQualifiedName~BugReportFlowTests|FullyQualifiedName~BugReportServiceOptionsTests"`. Expected: CS0246 `BugReportFlow` and `BugReportServiceOptions`.

- [ ] **Step 3: Implement** as specified above. `BugReportFlow` passes the current subject (`currentSubject()`) into the `BugReportContext` it hands to `CaptureAll`. Replace E1's internal screenshot constants with `BugReportLimits.MaxScreenshotLongEdge` and `BugReportLimits.MaxPngScreenshotBytes`. All widget and screen work stays on the caller's thread. Background tasks only post closures.

- [ ] **Step 4: Run to pass.** Step 2 command, `Failed: 0`.

- [ ] **Step 5: Commit.** `git commit -m "feat(bugreports): drive capture, confirmation and submission headlessly"`.

---

### Task 18: BugReportService, documentation and full verification

**Files:**
- Create: `KhaozEngine.BugReports.Client/BugReportService.cs`, `KhaozEngine.BugReports.Client.Tests/BugReportServiceTests.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.BugReports.cs`, `KhaozEngine.BugReports.Client/README.md`, `KhaozEngine.BugReports/README.md`, root `README.md` (the two rows E1 wrote now describe the full surface), `docs/USING-KHAOZENGINE.md` (an "In-game bug reports" section), `docs/DEPENDENCY-SEAMS.md` (a row for the `IBugReportSink` seam with its in-package `HttpRelaySink` and `LocalFolderSink`, no third-party dependency)

**Interfaces:**
- Consumes: everything above, including `BugReportServiceOptions` from Task 17.
- Produces: the contract's `BugReportService`, plus:

```csharp
public sealed class BugReportService : IDisposable
{
    // contract members (TriggerActionId is defined as BugReportFlow.TriggerActionId), plus
    public ActionMapController TriggerControls { get; }
    public RecentLogSink RecentLog { get; }
    public bool IsOpen { get; }
    public event Action<BugReportSubmitResult>? ReportSubmitted;
    public void SetPassTimingsSource(Func<PassTimings?> source);
}
```

The constructor validates options, calls `app.AddOverlay(overlay)` (whose `Attach` creates `Recorder` from the context window, starts it when `RecordingEnabled` and `Recorder.IsAvailable`, builds the font lazily from `Surface.LoadDefaultDpiFont(18f)` on first open, registers the seven built-in sections ahead of game sections, attaches `RecentLog` to `Log.Manager` when one is configured) and builds the flow. `SetRecordingEnabled` calls `Recorder.Start()` or `Recorder.Stop()`. `Dispose` removes the overlay and disposes the recorder. A `GameApp` needs a window, so the adapter itself is proved by the first consumer's live check and its headless tests pin the public constants and the package boundary.

- [ ] **Step 1: Write the failing tests**

```csharp
// BugReportServiceTests
[Fact] public void Trigger_action_id_is_ReportBug()   // BugReportService.TriggerActionId == "ReportBug"
[Fact] public void IsOpen_is_true_from_capture_until_the_screen_closes()
// ArchitectureTests.BugReports
[Fact] public void BugReportsClient_ReferencesItsDeclaredSet()
// exact ProjectRefs: App, BugReports, BugReports.Contracts, Diagnostics, Game, Gpu, Gui, Imaging, Windowing (plus any E1 added), no package refs
```

- [ ] **Step 2: Run to fail.** `build-slot --label e2-t18-red -- dotnet test KhaozEngine.BugReports.Client.Tests/KhaozEngine.BugReports.Client.Tests.csproj -c Release --filter "FullyQualifiedName~BugReportServiceTests"`. Expected: CS0117 `TriggerActionId` on `BugReportService` (or CS0246 when E1 did not create the type).

- [ ] **Step 3: Implement** the adapter as described. The client README shows the five-line setup (`new BugReportService(app, options, new HttpRelaySink(http, relay, () => token), new BugReportOfflineQueue(dir, TimeProvider.System), () => subject)`), game sections, the `ReportCaptured` server hook, rebinding, `Suspend` and `Resume` for pause menus, the settings toggle and string overrides. The core README covers sections, bundles, sinks, the queue, the viewer and server capture with a server-side example that checks `ReportIds.IsValid` and `TryAccept` before `Capture`.

- [ ] **Step 4: Run the full verification**

```sh
mkdir -p local-feed
build-slot --heavy --label e2-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label e2-test -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: build `0 Warning(s)`, `0 Error(s)`, every test assembly `Failed: 0`, every guard exits 0. Then sweep Markdown with `git grep -w -n -e BugReportService -e IBugReportSink -e TextArea -e AddOverlay -- "*.md"` and fix stale text.

- [ ] **Step 5: Commit**

`git commit -m "feat(bugreports): wire the bug report service into GameApp"` with the service files, tests and the five documents.

The windowed path (F10 on the title, login and in-game screens, a frame N screenshot without the screen, a clip frozen before the screen) is proved by the first consumer's live check, as the spec states. This plan does not launch a client.
