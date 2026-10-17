# KhaozEngine.BugReports.Client

The client side of in-game bug reports. `BugReportService` adds a report trigger, a rolling clip, the built-in
sections, a confirmation screen and the result toasts to a `GameApp`, as one overlay that sits above the title,
login, loading and game screens. `FrameHistoryRecorder` underneath it keeps the clip and captures the trigger frame
as a screenshot. It is opt-in and sits in no umbrella. Add it explicitly.

Depends on `KhaozEngine.App`, `KhaozEngine.BugReports`, `KhaozEngine.BugReports.Contracts`,
`KhaozEngine.Diagnostics`, `KhaozEngine.Game`, `KhaozEngine.Gpu`, `KhaozEngine.Gui`, `KhaozEngine.Imaging` and
`KhaozEngine.Windowing`. No third-party package.

## Quick start

Create the service once, on the frame thread, after the `GameApp` is constructed, and dispose it before the app.

```csharp
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;

var options = new BugReportServiceOptions { GameId = "grimhollow", GameDisplayName = "Grimhollow", SessionLogPath = logPath };
var sink = new HttpRelaySink(http, relay, () => token);
var queue = new BugReportOfflineQueue(Path.Combine(stateDir, "bug-reports", "pending"), TimeProvider.System);
var reports = new BugReportService(app, options, sink, queue, () => subject);
// Shutdown: reports.Dispose();
```

`() => token` returns the bearer token for the relay, and `() => subject` returns the signed-in account, or null
while signed out. The service polls the subject every frame. Both run off the frame thread too: the token on the
thread pool for every send and flush, and the subject on whichever thread calls `RetryQueuedAsync`. Keep them
thread-safe. A `currentSubject` or `SaveTriggerBindings` that throws is logged once per distinct failure and never
stops reports: the last known account stays, or signed out when none was ever read, and a rebinding stays in
memory for the session. A report made while signed out is queued and goes
with the first account that signs in. Use `new LocalFolderSink(folder, openViewer: true)` in development builds to
see every report in the browser without a relay.

F10 opens a report by default. In the trigger frame the service freezes the clip, arms the screenshot and captures
every section, then raises `ReportCaptured`. The confirmation screen opens once the screenshot is taken, usually the
next frame and at most three frames after the trigger. From the trigger until it closes the game sees
`InputState.WithoutInteraction`, so typing never reaches the game. Send builds the bundle on the thread pool and
hands it to the sink. The result shows as a toast and raises `ReportSubmitted` on the frame thread.

Media never costs a report. Send keeps the newest clip frames that fit the bundle's 128 entries and 16 MiB beside the
sections and the screenshot. When the bundle writer still refuses a size limit, or a clip frame or screenshot whose
image header does not match its record, Send retries once without the clip, then once with the sections only.

## What the service adds

| Member | Use |
| --- | --- |
| `TriggerActionId` | `"ReportBug"`, the trigger action in `TriggerControls`. |
| `Begin()` | Opens a report on the next frame from a menu entry. False while a report is already open. |
| `IsOpen` | True from the trigger frame until the screen closes. |
| `AddSection(section)` | Adds a game section after the seven built-in ones, up to 64 sections in all. |
| `ReportCaptured` | The new report id, raised in the trigger frame. |
| `ReportSubmitted` | Each send's `BugReportSubmitResult`, raised on the frame thread. |
| `SetRecordingEnabled(bool)` | Starts or stops the clip from a settings toggle. |
| `SetPassTimingsSource(source)` | Gives the `performance` section a `PassTimings` meter. |
| `RetryQueuedAsync(token)` | Sends the queued reports for the signed-in account now and returns how many were sent. A toast counts the sent and the rejected ones. |
| `TriggerControls` | The trigger's `ActionMapController`, for a controls screen. |
| `RecentLog` | The log ring the `log` section writes. |
| `Recorder` | The `FrameHistoryRecorder` behind the clip and the screenshot. |
| `Dispose()` | Removes the overlay, waits up to 3 s for sends to reach the queue, cancels sends and flushes, disposes the recorder and detaches `RecentLog`. |

Every member is frame thread only. `RetryQueuedAsync` completes off it.

## Built-in sections

The built-in sections, their facts records, the confirmation screen and its model are internal to this package. A
game reaches them only through `BugReportService`, adds its own with `AddSection` and restyles text through
`BugReportStrings`.

| Key | Holds |
| --- | --- |
| `build` | Game id and name, version, build name, engine version, configuration and commit. |
| `platform` | OS, architectures, runtime, CPU count, memory, locale, window and framebuffer size, content scale and the admitted `KE_` variables. |
| `graphics` | Backend, adapter, swapchain size, present mode, software adapter, device loss reason, injected overlay modules and capabilities. |
| `session` | Uptime, the session log name and the newest crash file from an earlier run. |
| `input` | Connected devices and the trigger's bindings. |
| `performance` | FPS, frame time average, minimum and maximum, managed bytes, per-pass timings and the last 300 frame times. |
| `log` | The newest lines of `RecentLog` that fit 90 percent of 256 KiB, leaving room for redaction. |

The platform facts that never change, the admitted and masked `KE_` variables among them, are read once when the
service is built. The window and graphics facts come
from the trigger frame's `InputState` and from fields the window latched, so a section never calls a live window
getter. The previous crash scan runs once on the thread pool over the directory of
`BugReportServiceOptions.SessionLogPath`. With no session log path it is skipped and `session` reports no previous
crash. The `log` section fills only when `Log.Manager` is configured before the service is built.

## Game sections

A section writes one JSON object in the trigger frame. Keep it cheap and write many short strings. Redaction and
truncation run later, off the frame thread.

```csharp
sealed class ZoneSection(Func<string> zone) : IBugReportSection
{
    public string Key => "zone";
    public int MaxBytes => 4 * 1024;
    public void Write(Utf8JsonWriter writer, BugReportContext context)
    {
        writer.WriteStartObject();
        writer.WriteString("zone", zone());
        writer.WriteEndObject();
    }
}

reports.AddSection(new ZoneSection(() => world.ZoneName));
```

## Server snapshot hook

A server-backed game asks its server for a snapshot with the same report id, through its own protocol message:

```csharp
reports.ReportCaptured += reportId => connection.Send(new RequestBugSnapshot(reportId));
```

The server side is described in the `KhaozEngine.BugReports` README.

## Rebinding, pause menus and settings

- Rebind with `reports.TriggerControls.BeginRebind(BugReportService.TriggerActionId, 0)`. Rebinds save through
  `BugReportServiceOptions.SaveTriggerBindings`. Pass the saved JSON back as `TriggerBindingsJson` next launch. Text
  that applies no binding is logged once and leaves `DefaultKey` bound.
- A pause menu calls `reports.Recorder.Suspend()` when it opens and `reports.Recorder.Resume()` when it closes, so
  the clip keeps the moments before the pause.
- A settings toggle calls `reports.SetRecordingEnabled(enabled)`. `RecordingEnabled` in the options sets the
  starting state.

## Strings

Every player-facing line resolves through `BugReportStrings`, a set of `bugreport.*` `StringId` keys with an English
fallback. A game overrides any line by adding the key to its own localization catalog, for example
`bugreport.screen.title` or `bugreport.toast.sent`. Category labels resolve `bugreport.category.<id lowercased>` and
fall back to the id, so a game's own `Categories` list localizes the same way.

## Shutdown

`Dispose()` removes the overlay first, so no later frame touches the recorder. It then tells every send still
building its bundle to stop waiting for the screenshot, which such a report goes without, waits up to 3 s for each
to write its bundle to the offline queue, and closes a report that is still open, with its thumbnail textures.
After that it cancels every send and flush the service started, disposes the recorder and detaches `RecentLog`
from the log manager. A send cut off by shutdown stays in the offline queue, because the submitter writes each
bundle there before its first send.

Two windows remain where a report sent just before shutdown is lost: a build still running after the 3 s wait
when the process then ends, and a process that is killed outright.

## The recorder on its own

`BugReportService` drives the recorder for you. A host without a `GameApp` can use it directly:

```csharp
using var recorder = new FrameHistoryRecorder(window, new FrameHistoryOptions());
if (recorder.IsAvailable)
    recorder.Start();

// When the player reports a bug, in the trigger frame's frame callback:
Task<ReportImage> screenshot = recorder.CaptureScreenshotAsync();
ReportClip clip = recorder.Freeze();
// Build and send the report, then:
recorder.Thaw();
```

Every member is frame thread only. Encoding runs on the recorder's own background thread.

## Recorder lifecycle

| Call | Effect |
| --- | --- |
| Constructor | Validates the options and registers the standby screenshot tap. The clip does not record yet. |
| `Start()` | Registers the clip tap. Does nothing while recording, when `IsAvailable` is false, or after a capture fault. |
| `Stop()` | Removes the clip tap and drops every frame held. Use it for a settings toggle. |
| `Suspend()` / `Resume()` | Pause and resume capture ticks and keep the frames held. Use them for a pause menu. |
| `Freeze()` | Waits up to 250 ms for frames still encoding, returns the clip and stops the ring until `Thaw()`. |
| `Thaw()` | Lets the ring run again. Frames from before the freeze stay until they age out. |
| `CaptureScreenshotAsync()` | Arms the screenshot tap for the current frame. A second call while one is pending returns the same task. |
| `Dispose()` | Removes both taps, faults a pending screenshot with `ObjectDisposedException` and stops the encode thread. |

## Availability

`IsAvailable` is true when the window's GPU device has completion fences, implements both
`IGpuSwapchainCapture` and `IGpuFencedReadback`, and reports `IGpuSwapchainCapture.SwapchainCopySupported`. The
three native devices implement both. Without them the clip never records, because its readback would have to wait
on the GPU.

A screenshot needs only a swapchain copy: `IGpuSwapchainCapture` with `SwapchainCopySupported` true. It still
works on a device without completion fences. A device that can never copy its swapchain, such as a Vulkan surface
whose `supportedUsageFlags` lack `TRANSFER_SRC`, gets no standby screenshot tap, because it has no copy-source
usage to keep on. The clip is unavailable there too, so the recorder registers no tap at all, `IsAvailable` is
false, and `CaptureScreenshotAsync()` returns a task faulted with `NotSupportedException`. A supported device
whose current frame is not copyable, during startup, minimisation or a swapchain recreation, keeps the request
pending instead.

No capture fault reaches the game's frame. Faults take three forms:

- **Clip encode fault.** Recorded on the encode thread and handled on the frame thread at the clip tap's next
  frame. It is warned once and stops recording for the recorder's lifetime, the same way a fault inside the clip
  tap, such as a lost device, does at once: the clip tap is removed, the frames held are dropped, `IsRecording` is
  false and a later `Start()` does nothing. Build a new recorder to record again.
- **Screenshot tap fault.** A fault inside the screenshot tap's frame callbacks trips its guard. It is warned
  once, faults the pending task, and every later `CaptureScreenshotAsync()` returns a faulted task.
- **Screenshot encoder exception.** Faults only that request's task. It trips nothing, and the next request may
  succeed.

## The standby screenshot tap

The screenshot tap stays registered from construction to `Dispose`, whether or not the clip records. Its
registration keeps the swapchain's copy-source usage on, so the trigger frame can be copied on every backend.
It does nothing per frame until armed. The armed frame downscales its swapchain copy to a long edge of at most
2560 px, reads it back once after the submit and encodes it off the frame thread: PNG when it encodes under
4 MB, JPEG quality 92 otherwise. A window whose long edge is 2560 px or less is copied 1:1, pixel for pixel.

A frame whose image cannot be copied leaves the request pending for the next frame that can. That is the first
frame after construction on Metal and Vulkan, and every frame of a minimised window.

## Clip defaults

| Option | Default | Meaning |
| --- | --- | --- |
| `Duration` | 5 s | How far back the clip reaches. |
| `FramesPerSecond` | 12 | Clip frames per second at any display rate. |
| `LongEdge` | 960 px | Longest frame edge. A smaller window is never scaled up. |
| `JpegQuality` | 70 | JPEG quality of each frame. |
| `MaxBytes` | 8 MB | Hard cap on encoded bytes, at most 10 MiB under `BugReportService`. The oldest frames go first. |

Frames live in memory only. The capture tick holds the configured rate on average at any display rate, and
a display slower than the rate captures every frame. Each frame keeps its own size, so a resize during
recording leaves frames of two sizes in one clip.

A refused option throws `ArgumentOutOfRangeException` naming it at construction. A report clip names at most
120 frames, so `Duration` must be above zero and `Duration` times `FramesPerSecond` may not exceed 120. `FramesPerSecond` runs 1 to 60,
`LongEdge` 16 to 2560, `JpegQuality` 1 to 100, and `MaxBytes` starts at 64 KiB. `BugReportServiceOptions` also
refuses a `MaxBytes` above 10 MiB, the bundle limit less the largest PNG screenshot, the section total and 1 MiB.
That floor is a lower bound for sane configurations. It does not guarantee that one textured 960 px frame
fits, and a frame larger than the cap is dropped. The default of 8 MB is a hard byte budget and does not
guarantee a full clip either. Holding all 60 default frames needs an average under about 136 KiB a frame, so a
detailed scene can shorten the clip, which then keeps the newest frames.

The clip never stalls a frame. A capture tick that finds every readback slot still in flight is dropped, and
the encode queue holds two frames and drops the oldest when it falls behind.
