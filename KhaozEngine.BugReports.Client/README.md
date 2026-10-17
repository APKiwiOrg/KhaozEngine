# KhaozEngine.BugReports.Client

The client side of in-game bug reports. `FrameHistoryRecorder` keeps a rolling in-memory clip of the last few
seconds and captures the trigger frame as a screenshot, and returns both as `KhaozEngine.BugReports` media
records. It is opt-in and sits in no umbrella. Add it explicitly.

Depends on `KhaozEngine.BugReports`, `KhaozEngine.Gpu`, `KhaozEngine.Windowing` and `KhaozEngine.Imaging`.

## Quick start

```csharp
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;

using var recorder = new FrameHistoryRecorder(window, new FrameHistoryOptions());
if (recorder.IsAvailable)
    recorder.Start();

// When the player reports a bug, in the trigger frame:
Task<ReportImage> screenshot = recorder.CaptureScreenshotAsync();
ReportClip clip = recorder.Freeze();
// Build and send the report, then:
recorder.Thaw();
```

## Clip defaults

| Option | Default | Meaning |
| --- | --- | --- |
| `Duration` | 5 s | How far back the clip reaches. |
| `FramesPerSecond` | 12 | Clip frames per second at any display rate. |
| `LongEdge` | 960 px | Longest frame edge. A smaller window is never scaled up. |
| `JpegQuality` | 70 | JPEG quality of each frame. |
| `MaxBytes` | 8 MB | Hard cap on encoded bytes. The oldest frames go first. |

Frames live in memory only. The capture tick holds the configured rate on average at any display rate, and
a display slower than the rate captures every frame. Each frame keeps its own size, so a resize during
recording leaves frames of two sizes in one clip.
