# In-game bug reports: cross-plan interface contract

This contract binds the four implementation plans:

- E1 capture: `docs/superpowers/plans/2026-10-17-bug-reports-e1-capture.md`
- E2 report core, client and screen: `docs/superpowers/plans/2026-10-17-bug-reports-e2-report-client.md`
- E3 relay: `docs/superpowers/plans/2026-10-17-bug-reports-e3-relay.md`
- Grimhollow G1 to G3: Grimhollow `docs/superpowers/plans/2026-10-17-in-game-bug-reports.md`

Spec: `docs/superpowers/specs/2026-10-17-bug-reports-design.md`. The public names below are binding. A plan or an
executor that needs to change one records the change here and in every plan that uses it, in the same commit.
Members not listed are each plan's own choice.

## Decisions made at planning time

- **D1 Report id.** `Guid.CreateVersion7()` instead of a ULID. It is BCL, time-ordered and 16 bytes, which is
  everything the spec needed from a ULID. Text form is the 32 lowercase hex characters of `Guid.ToString("N")`. The
  reference code is `R-` plus 6 Crockford base32 characters taken from the id's last 30 random bits.
- **D2 Screen home.** `BugReportScreen` lives in `KhaozEngine.BugReports.Client`, because it shows report types.
  `KhaozEngine.Gui` gains only `TextArea`.
- **D3 Viewer home.** The viewer page renderer lives in `KhaozEngine.BugReports` (core), so `LocalFolderSink` and the
  relay share one template. `KhaozEngine.BugReports.Relay` references the core.
- **D4 Media records first.** E1 creates `KhaozEngine.BugReports` with only the media records below, so the recorder
  can return them. E2 adds the rest of the core.
- **D5 Log ring and redactor.** `RecentLogSink` and `ReportRedactor` land in E2 with the sections that use them.
- **D6 Queue seam.** The work queue seam only enqueues. Delivery, retry and poison handling belong to the host (a
  Functions queue trigger with `maxDequeueCount` 5). `ProcessWorkAsync` throws to ask for redelivery.
- **D7 Azure clients.** The Azure backend takes constructed SDK clients. The engine never references Azure.Identity.
- **D8 Shared validation.** Bundle validation lives in Contracts (BCL only, `System.IO.Compression`), used by client
  tests and the relay.
- **D9 Shared JSON.** Contracts exposes `BugReportJson.Options` (compact camelCase `JsonSerializerOptions`) for every bundle and wire document, and `BundleErrors`, a static class of stable error code strings.
- **D10 Section keys.** A section key matches `^[a-z0-9][a-z0-9._-]{0,47}$`.
- **D11 MP4 index.** When `clip.mp4` is present, every `ClipIndexFrame.Entry` is `"clip.mp4"`.
- **D12 Section fields the relay reads.** `log.lines`, `build.version`, `build.engineVersion`, `platform.os`, `graphics.backend`, `graphics.adapter`. E2's built-in sections must keep these names.
- **D13 Handle.** `ReporterIdentity.Handle` is free text that names the reporter in the identity provider. Grimhollow sets the Discord mention `<@id>`. The issue renders the handle and the subject as inline code, never as neutralised text.
- **D14 Render time.** A clip frame's render time is the frame loop's unclamped delta, not `FrameStats`, which averages and clamps at 100 ms.
- **D15 Standby screenshot tap.** The recorder holds a screenshot tap from construction to dispose, so swapchain copy-source usage stays on while the clip is stopped. `IsAvailable` is false when the device lacks completion fences or cannot copy its swapchain. A clip holds at most 120 frames.
- **D16 Poison handling.** The Azure backend ships `PoisonQueueRequeuer`, which a host runs daily to move poison messages back to the work queue, at most 3 times per item. Grimhollow has no Application Insights in production, so this replaces an alert.

## E1 produces

```csharp
namespace KhaozEngine.Imaging;
public static class JpegWriter
{
    // Baseline JPEG, 4:2:0, quality 1 to 100. rgba is 4 bytes per pixel, alpha ignored.
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality);
}

namespace KhaozEngine.Gpu;
public sealed class GpuReadbackRing : IDisposable
{
    public GpuReadbackRing(IGpuDevice device, int width, int height, int slotCount = 3);
    // Remaining members are E1's choice. Consumed only inside E1.
}

namespace KhaozEngine.Windowing;
public interface IFrameCaptureTap { /* members are E1's choice, consumed only inside E1 */ }
public sealed partial class AppWindow
{
    public void AddCaptureTap(IFrameCaptureTap tap);
    public void RemoveCaptureTap(IFrameCaptureTap tap);
}

namespace KhaozEngine.BugReports;   // created by E1 with these records only (D4)
public sealed record ReportImage(string MediaType, int Width, int Height, ReadOnlyMemory<byte> Bytes); // "image/png" or "image/jpeg"
public sealed record ReportClipFrame(TimeSpan Offset, double RenderMilliseconds, int Width, int Height, ReadOnlyMemory<byte> Jpeg);
public sealed record ReportClip(IReadOnlyList<ReportClipFrame> Frames);   // Offset is from the first frame

namespace KhaozEngine.BugReports.Client;
public sealed record FrameHistoryOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);
    public int FramesPerSecond { get; init; } = 12;
    public int LongEdge { get; init; } = 960;
    public int JpegQuality { get; init; } = 70;
    public int MaxBytes { get; init; } = 8 * 1024 * 1024;
}
public sealed class FrameHistoryRecorder : IDisposable
{
    public FrameHistoryRecorder(AppWindow window, FrameHistoryOptions options);
    public bool IsAvailable { get; }    // false without completion fences or swapchain copy (D15)
    public bool IsRecording { get; }
    public void Start();                // registers the tap
    public void Stop();                 // unregisters the tap and clears the ring (settings toggle off)
    public void Suspend();              // pause menu open
    public void Resume();
    public ReportClip Freeze();         // stops the ring until Thaw
    public void Thaw();
    // Arms the standby screenshot tap (held from construction, D15) for the current frame.
    // Long edge at most 2560. PNG when it encodes under 4 MB, JPEG quality 92 otherwise.
    public Task<ReportImage> CaptureScreenshotAsync();
}
```

## E2 produces

### Contracts (`KhaozEngine.BugReports.Contracts`, BCL only)

```csharp
namespace KhaozEngine.BugReports.Contracts;
public static class BugReportFormat { public const int Version = 1; }
public static class BugReportJson
{
    public static JsonSerializerOptions Options { get; }                       // D9
    public static JsonDocumentOptions SectionDocumentOptions { get; }          // depth MaxSectionDepth, no duplicate names
    public static bool IsValidSection(ReadOnlyMemory<byte> json);              // one object, every string decodes
    public static bool IsWellFormedText(string text);                          // false on any lone surrogate
    // One Serialize<T> returning UTF-8 bytes and one Deserialize<T> over a UTF-8 span per wire document, each
    // Deserialize throwing JsonException on a malformed, incomplete or duplicate-keyed document:
    public static byte[] SerializeManifest(BugReportManifest manifest);
    public static BugReportManifest DeserializeManifest(ReadOnlySpan<byte> json);
    public static byte[] SerializeClipIndex(ClipIndex index);
    public static ClipIndex DeserializeClipIndex(ReadOnlySpan<byte> json);
    public static byte[] SerializeIntakeAccepted(IntakeAccepted value);
    public static IntakeAccepted DeserializeIntakeAccepted(ReadOnlySpan<byte> json);
    public static byte[] SerializeRelayError(RelayError value);
    public static RelayError DeserializeRelayError(ReadOnlySpan<byte> json);
    public static byte[] SerializeServerSnapshot(ServerSnapshotWire value);
    public static ServerSnapshotWire DeserializeServerSnapshot(ReadOnlySpan<byte> json);   // also rejects undecodable strings
}
public static class BundleErrors { /* stable error codes, E2's choice */ }
public static class BugReportLimits
{
    public const long MaxBundleBytes = 16 * 1024 * 1024;
    public const int MaxEntries = 128;
    public const int MaxSectionBytesTotal = 1024 * 1024;
    public const int MaxDescriptionChars = 4000;
    public const int MaxTitleChars = 70;
    public const int MaxScreenshotLongEdge = 2560;
    public const int MaxServerSnapshotBytes = 1024 * 1024;
    public const int ServerSnapshotEnvelopeReserveBytes = 16 * 1024;
    public const int MaxPngScreenshotBytes = 4 * 1024 * 1024;
    public const int MaxCategoryChars = 64;
    public const int MaxGameIdChars = 32;
    public const int MaxSectionDepth = 64;
    public const int MaxClipOffsetMilliseconds = 600000;
    public const int MaxRenderMilliseconds = 60000;
}
public static class ImageHeaders
{
    public const string PngMediaType = "image/png";
    public const string JpegMediaType = "image/jpeg";
    public static bool TryReadSize(string mediaType, ReadOnlySpan<byte> bytes, out int width, out int height);
}
public enum BugReportKind { Bug, Crash }
public sealed record BundleEntry(string Name, long Size, string Sha256);
public sealed record BugReportManifest(int FormatVersion, string ReportId, string GameId, BugReportKind Kind,
    string Category, string Description, DateTimeOffset CapturedAt, IReadOnlyList<BundleEntry> Entries);
public sealed record ClipIndexFrame(double OffsetMilliseconds, double RenderMilliseconds, int Width, int Height, string Entry);
public sealed record ClipIndex(IReadOnlyList<ClipIndexFrame> Frames);
public sealed record ReporterIdentity(string Subject, string DisplayName, string? Handle);
public static class BundleEntryNames
{
    public const string Manifest = "manifest.json";
    public const string ScreenshotPng = "screenshot.png";
    public const string ScreenshotJpeg = "screenshot.jpg";
    public const string ClipIndex = "clip/index.json";
    public const string ClipMp4 = "clip.mp4";
    public const int MaxClipFrames = 120;
    public static bool IsValidSectionKey(string key);   // D10
    public static bool IsValidGameId(string gameId);    // [a-z0-9][a-z0-9-]{0,31}
    public static string Section(string key);      // "sections/<key>.json", key is [a-z0-9._-]{1,48}
    public static string ClipFrame(int index);     // "clip/000.jpg" to "clip/119.jpg"
    public static bool IsAllowed(string name);
}
public sealed record BundleValidationResult(bool IsValid, string? Error, BugReportManifest? Manifest);
public static class BundleValidator
{
    public static BundleValidationResult Validate(Stream zip, long maxBytes = BugReportLimits.MaxBundleBytes);
}
public static class ReportIds
{
    public static string NewId();                                   // D1
    public static bool IsValid(string reportId);
    public static string ReferenceCode(string reportId);            // "R-XXXXXX"
}
public static class RelayRoutes
{
    public const string Intake = "bug-reports";
    public static string ServerSnapshot(string reportId);           // "bug-reports/<id>/server"
}
public static class RelayHeaders
{
    public const string IdempotencyKey = "Idempotency-Key";
    public const string ServiceKey = "X-Report-Service-Key";
}
public sealed record IntakeAccepted(string ReportId, string Reference);  // 202 body
public sealed record RelayError(string Error);                          // 4xx body
public sealed record ServerSnapshotWire(int FormatVersion, string ReportId, string Subject, DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement> Sections);
```

Relay status codes: 202 accepted, 400 bad request, 401 no or bad token, 403 blocked subject or bad service key,
413 over the size cap, 422 bundle validation failed, 429 over quota with `Retry-After` in seconds.

### Core (`KhaozEngine.BugReports`, headless, server-safe)

```csharp
namespace KhaozEngine.Diagnostics;
public sealed class RecentLogSink : ILogSink
{
    public RecentLogSink(int capacity = 500);
    public IReadOnlyList<string> Snapshot();
}
public sealed class ReportRedactor
{
    public ReportRedactor(string? homeDirectory = null);
    public string Redact(string text);
}

namespace KhaozEngine.BugReports;
public interface IBugReportSection
{
    string Key { get; }
    int MaxBytes { get; }
    void Write(Utf8JsonWriter writer, BugReportContext context);
}
public sealed record BugReportContext(string ReportId, DateTimeOffset CapturedAt, string? Subject = null);
public sealed record RawSection(string Key, int MaxBytes, ReadOnlyMemory<byte> Json, bool IsError);   // unredacted capture output
public sealed record CapturedSection(string Key, ReadOnlyMemory<byte> Json);   // finished: redacted, truncated, inside the selected total
// Only Finish produces redacted sections. The bundle writer trusts a hand-built CapturedSection to be finished output.
public sealed class BugReportSectionSet
{
    public const int MinSectionBytes = 64;
    public static readonly TimeSpan DefaultFinishBudget = TimeSpan.FromSeconds(2);
    public IReadOnlyList<string> Keys { get; }
    public void Add(IBugReportSection section);
    public IReadOnlyList<RawSection> CaptureAll(BugReportContext context);   // isolation only, frame thread
    // Off the frame thread: redaction, error message cut, truncation, configurable total (1 MiB by default). Masks every remaining string once budget is spent.
    public static IReadOnlyList<CapturedSection> Finish(IReadOnlyList<RawSection> raw, ReportRedactor redactor, TimeSpan budget, int totalBytes = BugReportLimits.MaxSectionBytesTotal);
}
public sealed class BugReportDraft
{
    public BugReportDraft(string reportId, string gameId, BugReportKind kind, DateTimeOffset capturedAt,
        IReadOnlyList<CapturedSection> sections, ReportImage? screenshot, ReportClip? clip);
    public string ReportId { get; }
    public string GameId { get; }
    public BugReportKind Kind { get; }
    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyList<CapturedSection> Sections { get; }
    public ReportImage? Screenshot { get; }
    public ReportClip? Clip { get; }
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Other";
    public bool IncludeScreenshot { get; set; } = true;
    public bool IncludeClip { get; set; } = true;
}
public static class BugReportBundleWriter
{
    public static byte[] Write(BugReportDraft draft);   // stored zip, manifest hashes, throws on any limit
}
public sealed record BugReportSubmission(string ReportId, string? Subject, ReadOnlyMemory<byte> Bundle);
public enum BugReportSubmitStatus { Sent, Queued, Rejected }
public sealed record BugReportSubmitResult(BugReportSubmitStatus Status, string? Reference, string? Error, TimeSpan? RetryAfter);
public interface IBugReportSink
{
    Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken);
}
public sealed class HttpRelaySink : IBugReportSink
{
    public const string UnconfirmedError = "unconfirmed";
    public HttpRelaySink(HttpClient http, Uri relayBase, Func<string?> bearerToken);   // token read on a pool thread
}
// HttpRelaySink results: Sent only for a 2xx IntakeAccepted whose ReportId equals the submission's. Any other 2xx is
// Queued "unconfirmed". Rejected only for 400, 403, 413 or 422 with a RelayError body. Everything else is Queued
// "http_<status>", or "no_token" and "network".
public readonly record struct BugReportFlushResult(int Sent, int Rejected);
public sealed class LocalFolderSink : IBugReportSink
{
    public LocalFolderSink(string root, bool openViewer);
}
public sealed class BugReportOfflineQueue
{
    public const int MaxReports = 3;
    public const long MaxBytes = 30 * 1024 * 1024;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public BugReportOfflineQueue(string directory, TimeProvider time);
    public void Enqueue(BugReportSubmission submission);      // Subject null binds to the first account that flushes
    public Task<BugReportFlushResult> FlushAsync(string subject, IBugReportSink sink, CancellationToken cancellationToken);
}
public sealed record BugReportViewerModel(BugReportManifest Manifest, ClipIndex? Clip, IReadOnlyList<CapturedSection> Sections,
    ReporterIdentity? Reporter, ReadOnlyMemory<byte>? ServerSnapshotJson, IReadOnlyList<string> LogTail);
public static class BugReportViewerPage
{
    public static string Render(BugReportViewerModel model);  // self-contained, media by relative path
}
public sealed record ServerSnapshot(string ReportId, string Subject, DateTimeOffset CapturedAt, IReadOnlyList<RawSection> Sections);
public sealed class BugReportServerCapture
{
    public BugReportServerCapture(TimeProvider time, int perAccountPerHour = 5);
    public void Add(IBugReportSection section);
    public bool TryAccept(string subject);                     // per-account limit, checked before any work
    public ServerSnapshot Capture(string reportId, string subject);   // call on the simulation thread
}
public sealed class ServerSnapshotPoster
{
    public ServerSnapshotPoster(HttpClient http, Uri relayBase, string serviceKey, ReportRedactor? redactor = null);
    public Task<bool> PostAsync(ServerSnapshot snapshot, CancellationToken cancellationToken);   // false and a warning on failure
}
```

### Client (`KhaozEngine.BugReports.Client`)

```csharp
namespace KhaozEngine.BugReports.Client;
public sealed record BugReportServiceOptions
{
    public required string GameId { get; init; }
    public required string GameDisplayName { get; init; }
    public Key DefaultKey { get; init; } = Key.F10;
    public IReadOnlyList<string>? Categories { get; init; }   // null keeps the engine default list
    public FrameHistoryOptions Recording { get; init; } = new();
    public bool RecordingEnabled { get; init; } = true;
}
public sealed class BugReportService : IDisposable
{
    public const string TriggerActionId = "ReportBug";
    public BugReportService(GameApp app, BugReportServiceOptions options, IBugReportSink sink,
        BugReportOfflineQueue queue, Func<string?> currentSubject);
    public FrameHistoryRecorder Recorder { get; }
    public bool IsOpen { get; }                    // true from capture until the screen closes
    public void AddSection(IBugReportSection section);
    public bool Begin();                             // the menu path, false when a report is already open
    public event Action<string>? ReportCaptured;     // report id, raised in the trigger frame
    public void SetRecordingEnabled(bool enabled);
    public Task<int> RetryQueuedAsync(CancellationToken cancellationToken);   // flushes for currentSubject(), returns sent
    // currentSubject is called on the frame thread every frame and on the thread that calls RetryQueuedAsync.
    // E2 additions
    public ActionMapController TriggerControls { get; }
    public RecentLogSink RecentLog { get; }
    public event Action<BugReportSubmitResult>? ReportSubmitted;   // frame thread
    public void SetPassTimingsSource(Func<PassTimings?> source);
}
```

E2 also adds these public names, members its own choice: `TextArea` (Gui), `JpegThumbnailDecoder` (Imaging), `IGameOverlay`, `GameOverlayContext`, `GameApp.AddOverlay` and `GameApp.RemoveOverlay` (Game), `InputState.WithoutInteraction` (Windowing), `BugReportSubmitter` (core).

The Client package keeps its implementation internal: the seven built-in section classes, their five `*Facts`
records, `FrameTimeRing`, `BugReportScreen`, `BugReportScreenModel`, `BugReportScreenResources`,
`BugReportScreenResult`, `BugReportScreenLayout` and `ReportThumbnails`. Its public surface is `BugReportService`,
`BugReportServiceOptions`, `FrameHistoryRecorder`, `FrameHistoryOptions` and `BugReportStrings`.

`BugReportStrings` (Client) holds the `bugreport.*` `StringId` keys with an English fallback. Its category field
label is `BugReportStrings.CategoryFieldLabel`, because `BugReportStrings.CategoryLabel(string categoryId)` resolves a
category id and a field of the same name would clash.

## E3 produces

```csharp
namespace KhaozEngine.BugReports.Relay;
public sealed record RelayRequest(IReadOnlyDictionary<string, string> Headers, Stream Body, long? ContentLength);
public sealed record RelayResponse(int StatusCode, string? JsonBody, IReadOnlyDictionary<string, string> Headers);
public interface IReporterAuthenticator
{
    ValueTask<ReporterIdentity?> AuthenticateAsync(string bearerToken, CancellationToken cancellationToken);
}
public enum ReportArea { Public, Pending }
public interface IReportMediaStore { /* members are E3's choice, over ReportArea and a relative path */ }
public enum ReportWorkKind { CreateIssue, AppendServerSnapshot }
public sealed record ReportWorkItem(ReportWorkKind Kind, string ReportId, int Requeues = 0);
public interface IReportWorkQueue
{
    Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken);
}
public interface IReportQuotaStore { /* members are E3's choice, fixed windows with optimistic concurrency */ }
public sealed record GitHubAppCredentials(long AppId, long InstallationId, string PrivateKeyPem);
public sealed record BugReportRelayOptions
{
    public required string GameId { get; init; }
    public required string GameDisplayName { get; init; }
    public required string RepositoryOwner { get; init; }
    public required string RepositoryName { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required Uri PublicMediaBase { get; init; }
    public required string ServiceKey { get; init; }
    public IReadOnlySet<string> BlockedSubjects { get; init; } = new HashSet<string>();
    public int PerAccountPerHour { get; init; } = 5;
    public int PerAccountPerDay { get; init; } = 20;
    public int GlobalPerDay { get; init; } = 100;
    public long MaxBundleBytes { get; init; } = BugReportLimits.MaxBundleBytes;
    public IReadOnlyList<IssueFact> IssueFacts { get; init; } = IssueFact.EngineDefaults;
    public ILogger? Logger { get; init; }
}
public sealed record IssueFact(string Label, string Section, string Property) { public static IReadOnlyList<IssueFact> EngineDefaults { get; } }
public sealed class BugReportRelay
{
    public BugReportRelay(BugReportRelayOptions options, IReporterAuthenticator authenticator, IReportMediaStore media,
        IReportWorkQueue work, IReportQuotaStore quotas, IGitHubIssueClient github, TimeProvider time);
    public Task<RelayResponse> IntakeAsync(RelayRequest request, CancellationToken cancellationToken);
    public Task<RelayResponse> ServerSnapshotIntakeAsync(string reportId, RelayRequest request, CancellationToken cancellationToken);
    public Task ProcessWorkAsync(ReportWorkItem item, CancellationToken cancellationToken);
}
public interface IGitHubIssueClient { /* create issue, add comment; members are E3's choice */ }
public sealed class GitHubAppIssueClient : IGitHubIssueClient
{
    public GitHubAppIssueClient(HttpClient http, GitHubAppCredentials credentials, string owner, string repository, TimeProvider time);
}
// In-memory seams for tests and local runs: InMemoryReportMediaStore, InMemoryReportWorkQueue, InMemoryReportQuotaStore.

namespace KhaozEngine.BugReports.Relay.Azure;
public sealed class BlobReportMediaStore : IReportMediaStore
{
    public BlobReportMediaStore(BlobContainerClient publicContainer, BlobContainerClient pendingContainer);
}
public sealed class StorageQueueReportWorkQueue : IReportWorkQueue
{
    public StorageQueueReportWorkQueue(QueueClient queue);   // JSON message of ReportWorkItem
}
public sealed class TableReportQuotaStore : IReportQuotaStore
{
    public TableReportQuotaStore(TableClient table);
}
public sealed class PoisonQueueRequeuer
{
    public PoisonQueueRequeuer(QueueClient poison, QueueClient work, int maxRequeues = 3);
    public Task<int> RequeueAsync(CancellationToken cancellationToken);   // returns items moved back
}
```

## Grimhollow consumes

Every public name above, plus the spec's host settings: queue `bug-report-work`, poison queue
`bug-report-work-poison`, containers `reports` (public) and `pending` (private), table `quotas`.

## E1 as built

Public names E1 added beyond its block above, and the rules E2 and E3 can rely on. The READMEs of
`KhaozEngine.Gpu`, `KhaozEngine.Windowing` and `KhaozEngine.BugReports.Client` carry the full detail.

- **`IGpuFencedReadback`** (`KhaozEngine.Gpu`). An optional device capability with one member,
  `MappedData MapForRead(IGpuTexture staging, IGpuFence fence)`. It maps a staging texture once its fence has
  signaled, never drains, and throws `InvalidOperationException` before the fence signals. The three native devices
  implement it. Unmap with `IGpuDevice.Unmap`.
- **`GpuReadbackRing` members.** `Width`, `Height`, `SlotCount`, `PendingCount`, `HasFreeSlot`,
  `TryRecordCopy(commands, source, tag, out fence)` and `TryReadCompleted(destination, out tag)`. The constructor
  refuses a device without completion fences or without `IGpuFencedReadback` with `NotSupportedException`.
- **`IGpuSwapchainCapture`** (`KhaozEngine.Gpu`). An optional device capability with `SwapchainCopySourceRequested`,
  `SwapchainCopySupported`, `SwapchainCopyable` and `CopySwapchainColor(commands, destination)`. The three native
  devices implement it. `SwapchainCopySupported` is the permanent answer: whether this device can ever produce a
  copyable swapchain image for its surface. Direct3D 11 and Metal answer true. Vulkan answers true only when the
  surface's `supportedUsageFlags` include `TRANSFER_SRC`, refreshed on every recreation. `SwapchainCopyable` is the
  current frame's answer and can be false transiently on a supported device.
- **The bound-this-recording precondition.** `CopySwapchainColor` refuses with `InvalidOperationException` a list
  that has not bound `SwapchainFramebuffer` in its current recording, on all three backends. Only the frame's own
  list is ordered after the image's acquire, so a swapchain copy goes into `FrameCaptureContext.Commands`.
- **`GpuScaledCopy`** (`KhaozEngine.Gpu`). `new GpuScaledCopy(device)`, `Record(commands, source, destination)`,
  `Record(commands, source, destination, retire)` and `Dispose()`. One fullscreen linear downscale from any `Sampled`
  texture into a single `R8G8B8A8UNorm` attachment, output in RGBA order, top row first, alpha one, and byte-exact
  when the copy does not reduce. A resource set replaced by a new source goes to `retire`, or stays alive until
  `Dispose` when `retire` is null. It is never disposed during the recording that used it.
- **`IFrameCaptureTap` members** (`KhaozEngine.Windowing`). `Record(FrameCaptureContext)`,
  `AfterSubmit(FrameCaptureContext)` and `Detach(Action<IDisposable> retire)`.
- **`FrameCaptureContext` members** (`KhaozEngine.Windowing`). `Frame`, `Device`, `Commands` (the frame's own list,
  open during `Record` only), `FrameMilliseconds` (the unclamped loop delta, D14), `AcquireSwapchainImage()` (one
  shared `Sampled` copy per frame, null when uncopyable), `SignalOnSubmit(fence)` (one fence per frame across every
  tap) and `Retire(resource)`.
- **Recorder availability.** `IsAvailable` is true only when the device has completion fences, implements both
  `IGpuSwapchainCapture` and `IGpuFencedReadback`, and reports `SwapchainCopySupported`. A screenshot needs only
  `IGpuSwapchainCapture` with `SwapchainCopySupported` true. A device without that gets no standby screenshot tap,
  `IsAvailable` is false, and `CaptureScreenshotAsync()` returns a task faulted with `NotSupportedException`. A
  transiently uncopyable frame on a supported device keeps a screenshot request pending.
- **Recorder faults.** No capture fault reaches the game's frame, and they take three forms. A clip encode fault
  is recorded on the encode thread, handled on the frame thread at the clip tap's next frame, warned once, and
  stops recording for the recorder's lifetime, as a fault in the clip tap itself does: `IsRecording` is false and
  later `Start()` calls do nothing. A screenshot tap fault trips its guard, faults the pending task and every
  later request. A screenshot encoder exception faults only that request, and the next request may succeed.
- **`MaxBytes` floor.** `FrameHistoryOptions.MaxBytes` below 64 KiB is refused. The floor is a lower bound for sane
  configurations and does not guarantee that one frame fits. The default of 8 MB is the hard byte budget and does
  not guarantee a full clip. Holding all 60 default frames needs an average under about 136 KiB, and a detailed
  scene can shorten the clip, which then keeps the newest frames.
