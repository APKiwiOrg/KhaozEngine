# KhaozEngine.BugReports

The headless in-game bug report model. It holds what a report carries and has no GPU, window or game
dependency, so a game server can reference it as readily as a client. It is opt-in and sits in no umbrella.
Add it explicitly.

This release ships the report media records only. The rest of the core (sections, the report draft, the
bundle writer and the sinks) follows in a later release.

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
