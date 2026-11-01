# Private v21.23.0 frame recorder

NEVER MERGE this diagnostic archive into `main`. Branch
`fix/mac-frame-recorder-21-23-0-probe` starts exactly at released `v21.23.0`, commit
`cc5771ccc0a21593c07a0e0c1e20eb5f3b17377b`. It ports recorder commits `1066ce936` and
`5b6a423bd` and extends their evidence, without changing rendering behavior. No engine pin,
version, GPU commands, encoders, timestamp pools, frames in flight, framebufferOnly policy,
capture taps or waits change. `KhaozEngine.Render3D` remains identical to the base tag.
Only `KhaozEngine.Gpu.Metal`, `KhaozEngine.Windowing` and `KhaozEngine.Game.Render3D`
contain modified production source. This archive is never released or packaged.

## Boundaries and activation

`KE_FRAME_DIAGNOSTICS_PATH` absent or empty disables recording. All native capture flags stay
false and no drawable handlers are attached. An enabled run requires an absolute, nonexistent
result path in an existing directory. Recorder creation preallocates storage and resolves its
native selectors, global presented block and QuartzCore import before measurement. While waiting,
the only per-callback diagnostic work is the sibling `<result>.start` trigger poll, at most twice
per second. Consuming that file arms the recorder. The following opening callback boundary is the
baseline, excluding trigger I/O and its partial interval.

The hook observes AppWindow's render callback before its delta clamp, input, prepare or render.
A sample describes the callback completed between its start and end boundaries. Sampling stops
at the first boundary at least 10 seconds after baseline or after 4096 intervals. Sampling storage
is preallocated and warmed sampling performs no allocation or diagnostic file I/O. One background
writer then serializes, flushes and atomically moves a sibling temporary file without overwriting
a result. Normal disposal joins publication. An early close publishes no completed result.

The cadence summary is unchanged from the original recorder. It measures completed render-loop
callback cadence, never physical display cadence. Device counter durations remain cumulative
CPU-side API durations, including full acquisition calls. Boundary validity covers focus, minimize,
render suppression, positive stable framebuffer size and actual renderer-owned initialization mode.
It cannot exclude changes that reverse between callbacks. Cap and vsync changes do not invalidate
the summary. Require cap 0 and vsync true at baseline, end and both sample window states when
comparing the intended scene. `cadenceCapped=true` is expected with vsync.

## Schema 7

The existing top-level cadence fields remain `processId`, `appAssembly`, `engineAssembly`,
`stopwatchFrequency`, `durationSeconds`, `sampleCount`, `stopReason`, `baseline`, `end`,
`samples` and `summary`. `schemaVersion` is 7. Added top-level fields are:

- `identity`: process id, executable path, command-line argument array, every loaded assembly whose
  name starts with KhaozEngine or Grimhollow, and sorted KE_* environment names and values.
  Assembly entries contain name, assembly version, informational version, MVID and location, sorted
  by name and location. Identity is collected by the writer after sampling, so it includes assemblies
  loaded during measurement. The private report can contain local paths and command-line values.
- `framesInFlight`: the actual Metal device's ring depth, or null for an unsupported device.
- `baselineCapture` and `endCapture`: registered capture-tap count and the actual device's
  `SwapchainCopySourceRequested` state. Observing these never registers a tap or asks for a copy.
- `baselineClock` and `endClock`: `available`, `beforeTimestamp`, `afterTimestamp`, `mediaTime`.
  Each chooses the narrowest of eight Stopwatch/CACurrentMediaTime/Stopwatch brackets.
  Map native media seconds to Stopwatch ticks with the midpoint of the bracket and the recorded
  frequency. The bracket width is mapping uncertainty. The two maps expose drift rather than
  assuming wall-clock alignment. Unsupported platforms set `available=false`.
- `timing`: bounded `submissions`, `completions`, `presentCalls`, `presented`, and
  `nativeReadFailures`. Each buffer contains `records`, `overflow` and `incompleteWrites`.
- `annotations`: fixed `channels` and one integer `values` row per sample, in sample order.
- `gpuSummary`: GPU busy, GPU idle and presented-interval distributions, presentation and coverage
  counts/flags described below. It supplements the existing cadence summary.

## Native timing records

All native hooks are gated by one static volatile sampling flag. GPU times are read with the
existing double-returning objc_msgSend in the existing `MetalCompletionHandler.Completed`
block. Its error-latch route still runs normally. Submits record command-buffer handle, queue,
Stopwatch ticks, callback index and kind immediately before commit. Kinds are 0 Other,
1 FrameRecording, 2 SetupFlush and 3 PresentBuffer. The frame list is recognized by reference,
not by an assumption that every submitted list is a frame. Submits on another managed thread
carry callback index -1. Setup and present buffers keep their existing completion handlers.
A queue-drain buffer receives that same handler only while sampling. Its existing wait is unchanged.

Completions contain command-buffer handle, queue, observation Stopwatch ticks, status,
GPUStartTime and GPUEndTime in native media seconds. Records include buffers committed before
baseline if they complete while sampling. Pair by queue, handle and latest preceding submit tick,
since command-buffer handles can be recycled and completion callbacks can arrive out of order.
A completion with no captured submit is explicitly unmatched.

Immediately before `presentDrawable:`, the recorder attaches a capture-free global
`addPresentedHandler:` block to the drawable. The present-call record contains drawableID,
drawable handle, present command-buffer handle, Stopwatch ticks and callback index. The global
block records drawableID, observation Stopwatch ticks and `presentedTime`. A presentedTime of
0 means not presented. Attaching the handler adds no GPU commands and changes no layer flags.
Native exceptions are contained and counted rather than escaping an Objective-C callback.

Storage is append-only bounded storage with ring-style reservation and explicit overflow instead
of overwriting old evidence. Submission and completion buffers each hold 65536 records.
Present-call and presented buffers each hold 8192. Producers reserve a slot atomically, write the
record, then publish a ready bit. The writer copies ready slots without blocking on a producer.
`incompleteWrites` counts reservations not yet published at snapshot time. Native records are
clipped to inclusive baseline/end callback ticks. A producer racing stop can finish publishing
its reserved slot, but cannot mutate a copied report. There are no diagnostic drains or waits.
A missing tail completion or presentation is a coverage gap, not evidence of zero GPU cost.

`gpuSummary.frameGpuBusyMs` describes valid matched frame-recording buffer end-minus-start
seconds converted to milliseconds. `gpuIdleGapMs` describes nonnegative gaps in execution-time
order between valid completed buffers on each queue. It is not completion-handler dispatch delay.
`presentedIntervalMs` describes consecutive positive finite physical presented times, sorted by
native time. Each distribution contains count, mean, median, nearest-rank p95, p99 and worst.
Count 0 means no evidence and its numeric placeholders are 0. A valid GPU timing requires status
4, positive finite start and finite end at least start.

`unpresentedDrawables` counts callbacks reporting presentedTime 0. Per-sample
`frameCompletionCoverage`, `presentCallCoverage` and `presentedCoverage` mark whether a matched
valid frame completion, a present call and a positive presented callback exist. `allCallbacksCovered`
requires every flag. Reused drawable ids are paired within successive present-call tick ranges,
so a later presentation cannot fill an earlier gap. `overflowed`, `incompleteWrites`, `nativeReadFailures`,
`unmatchedCompletions` and `invalidGpuTimings` expose evidence loss and unsupported readings.
Coverage can be false for the final frames because collection stops without waiting for GPU or
physical presentation. Raw data remains available for the parent's offline association and analysis.

## Scene workload channels

GameApp3D registers `SceneFrameDiagnosticsAnnotator` only when recording is enabled. Registration
copies the channel names before measurement. At each sampled boundary it reads public Scene3D
state for the frame just completed. No PrepareFrame or Render call is added. Public diagnostics
may retain the previous rendered frame's values when rendering is suppressed, as their APIs specify.

`shadow.*` includes every boolean and numeric ShadowPassDiagnostics property, including reason
bits, skinned caster count, cascade count, rigid/skinned/total draws, total spans, point shadowed
lights, static rebuilds, dynamic renders, transient demand and rows rendered, all skinned/rigid
point draw categories, fallback/transition rows, face draw calls and slots in use.
`stats.*` contains every RenderFrameStats field and UploadBytesPartitioned. `scene.*` contains
DrawnInstances, CulledInstances, DrawnSkinnedInstances, CulledSkinnedInstances and
ShadowCasterCandidateCount. Each of the four cascade slots includes shadow.RigidSpanCount,
scene.ShadowCascadeCasterCount and scene.ShadowCascadeSpanCount, suffixed with its zero-based index.

Camera channels use the public CameraOverride when present, otherwise Camera. `camera.view.M11.bits`
through M44 and `camera.projection.M11.bits` through M44 store exact signed float bit patterns.
`camera.eye`, `camera.forward` and `light.direction` each have x/y/z.bits channels.
The light direction is Post.LightDirection. These are public camera inputs and light inputs,
not private jittered projection or fitted shadow matrices. Boolean channels are 0 or 1.
All other channel values are exact signed integer counts or bytes. The annotator must fill its
fixed Span without allocation or I/O. No channel is recomputed by rendering.

## Verification boundary

Headless tests cover recorder lifecycle, bounds and overflow, inactive native read and handler
paths, exact annotations, warmed allocation-free sampling, summary arithmetic and missing coverage.
The source diff against v21.23.0 must be empty for KhaozEngine.Render3D. Verification commands,
results, source identities and limitations belong in the implementer's external report.
CPU tests do not prove native handler delivery, actual GPU timing validity, physical presentation,
pixel identity or an FPS improvement. The parent owns DLL substitution and the single human-launched
capture. No windowed app, push, merge, release, package publication or tag is part of this archive.
