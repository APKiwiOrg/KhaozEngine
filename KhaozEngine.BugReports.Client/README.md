# KhaozEngine.BugReports.Client

The client side of in-game bug reports. `FrameHistoryRecorder` keeps a rolling in-memory clip of the last few
seconds and captures the trigger frame as a screenshot, and returns both as `KhaozEngine.BugReports` media
records. It is opt-in and sits in no umbrella. Add it explicitly.

Depends on `KhaozEngine.BugReports`, `KhaozEngine.Gpu`, `KhaozEngine.Windowing` and `KhaozEngine.Imaging`.

## Quick start

```csharp
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;

// Once, after the window is up. Construction registers the standby screenshot tap.
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

## Lifecycle

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

A fault inside a tap, such as a lost device, or a fault on the encode thread, is warned once and turns that
capture off for the recorder's lifetime. An encode fault is only recorded on the encode thread and is handled on
the frame thread at the clip tap's next frame. The game's frame continues. A clip fault removes the clip tap, drops
the frames held and leaves `IsRecording` false, and a later `Start()` does nothing. Build a new recorder to
record again. A screenshot fault faults the pending task and every later one.

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
| `MaxBytes` | 8 MB | Hard cap on encoded bytes. The oldest frames go first. |

Frames live in memory only. The capture tick holds the configured rate on average at any display rate, and
a display slower than the rate captures every frame. Each frame keeps its own size, so a resize during
recording leaves frames of two sizes in one clip.

A refused option throws `ArgumentOutOfRangeException` naming it at construction. A report clip names at most
120 frames, so `Duration` must be above zero and `Duration` times `FramesPerSecond` may not exceed 120. `FramesPerSecond` runs 1 to 60,
`LongEdge` 16 to 2560, `JpegQuality` 1 to 100, and `MaxBytes` starts at 64 KiB.
That floor is a lower bound for sane configurations. It does not guarantee that one textured 960 px frame
fits, and a frame larger than the cap is dropped. The default of 8 MB is what holds a full clip.

The clip never stalls a frame. A capture tick that finds every readback slot still in flight is dropped, and
the encode queue holds two frames and drops the oldest when it falls behind.
