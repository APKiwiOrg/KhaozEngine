# Private v21.23.0 frame recorder

NEVER MERGE this diagnostic archive into `main`. Branch
`fix/mac-frame-recorder-21-23-0-probe` starts exactly at released `v21.23.0`, commit
`cc5771ccc0a21593c07a0e0c1e20eb5f3b17377b`. It ports recorder commits `1066ce936` and
`5b6a423bd` and extends their evidence, without changing rendering behavior. No engine pin,
version, GPU commands, encoder count, public timestamp pools, frames in flight, framebufferOnly policy,
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
is preallocated and warmed callback sampling performs no managed allocation or diagnostic file I/O.
Pass-counter CPU resolve returns bounded autoreleased native data on the completion queue. One background
writer then serializes, flushes and atomically moves a sibling temporary file without overwriting
a result. Normal disposal joins publication. An early close publishes no completed result.

The cadence summary is unchanged from the original recorder. It measures completed render-loop
callback cadence, never physical display cadence. Device counter durations remain cumulative
CPU-side API durations, including full acquisition calls. Boundary validity covers focus, minimize,
render suppression, positive stable framebuffer size and actual renderer-owned initialization mode.
It cannot exclude changes that reverse between callbacks. Cap and vsync changes do not invalidate
the summary. Require cap 0 and vsync true at baseline, end and both sample window states when
comparing the intended scene. `cadenceCapped=true` is expected with vsync.

## Schema 8

The existing top-level cadence fields remain `processId`, `appAssembly`, `engineAssembly`,
`stopwatchFrequency`, `durationSeconds`, `sampleCount`, `stopReason`, `baseline`, `end`,
`samples` and `summary`. `schemaVersion` is 8. All schema 7 fields retain their meaning. Its additional
top-level fields are:

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
- `passTiming`: stage-boundary support, configuration, block schedule, correlated clocks, render
  signatures, encoder records and explicit evidence-loss counters described below.

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

## Encoder stage-boundary timing

Counter creation uses the recorder's actual Metal device and the existing timestamp interop's
`MTLCommonCounterSetTimestamp` lookup. `supportsCounterSampling:atStageBoundary` is checked before
arming. Unsupported hardware, discovery failures and sample-buffer creation failures set
`passTiming.supported=false` with an explicit `reason`. Cadence and schema 7 timing continue normally.
No counter buffer or attachment is created when the diagnostics environment variable is absent.
An enabled but unarmed recorder allocates its storage without attaching it to any encoder.

While sampling, the callback thread alternates 72 callbacks on and 72 off, starting on. Each
`blocks` entry records `startCallback`, `endCallbackExclusive`, `startTimestamp`, `endTimestamp`
and `timed`. The final block may be partial. All schema 7 instrumentation, annotations and
command-buffer timing remain enabled in both block types. Encoders on other managed threads
are untimed. Ordinary setup-upload batches use their existing direct blit path outside the
frame command buffer and are not encoder-instrumented.
`timed` describes the requested block schedule. Require support and usable encoder coverage
before comparing blocks. Descriptor clearing and command-state tracking are common to both blocks,
so the comparison estimates the incremental sampling and signature-read cost.

The native encoder sink attaches four indices to render descriptor sample-buffer attachment 0,
before creating the existing encoder. They are start/end of vertex and start/end of fragment.
It clears the sample buffer and every index immediately after encoder creation, including on a
failure, so reused descriptors cannot carry sampling into another encoder. Untimed render
begins during sampling explicitly clear that attachment before opening the pass. Descriptors
are currently built per pass by `MetalRenderApi`, including the standalone resolve path through
`MetalRenderPassSchedule` and `MetalEncoderScope`. The clearing contract also covers future reuse.

Timed compute and blit begins use `computeCommandEncoderWithDescriptor:` and
`blitCommandEncoderWithDescriptor:` with start/end-of-encoder indices. Their descriptors are
preallocated per pool slot. The compute descriptor explicitly sets `dispatchType=Serial`, matching
the released `computeCommandEncoderWithDispatchType:Serial` path. The SDK's compute descriptor has
only dispatch type and sampling attachments. Blit descriptors have only sampling attachments.
No additional render state is introduced. Untimed compute and blit encoders retain the released
selectors. Descriptor attachments are cleared after either factory returns. All encoders retain
their existing end, retain and release path.

There are no inline `sampleCountersInBuffer:` calls, marker copies, extra encoders, GPU resolves,
event signals, event waits or diagnostic GPU commands. Only the hardware's descriptor-driven
stage-boundary sampling is enabled. Stage spans on a tile-based GPU can overlap across passes.
They are not additive frame costs. Compare timed and untimed blocks to estimate perturbation
before treating an instrumented pass ranking as representative.

### Storage and completion

The pool contains 16 shared counter buffers with 4096 samples each. A render encoder consumes four
samples and a compute or blit encoder consumes two. One slot can therefore describe up to 1024
render encoders or 2048 compute/blit encoders in a command buffer. Apple's
[Metal feature tables](https://developer.apple.com/metal/Metal-Feature-Set-Tables.pdf) publish a
32 KB maximum counter buffer length and 32 sample buffers for Apple GPU families. Timestamp
results are 8 bytes. The pool reserves 512 KB of counter storage and stays below both limits.
Device creation errors remain authoritative if a driver has a different restriction.

A command buffer claims a sample slot at its first timed encoder. It never waits. A refused
claim leaves that command buffer untimed and increments `skippedCommandBuffers`. A full slot
leaves subsequent encoders untimed and increments `capacityOverflow` per encoder. Recording
continues through the ordinary encoder factory in either case. A separate fixed table tracks
64 command buffers, including off blocks, so encoder ordinals include preceding untimed encoders.
If that table fills, `commandStateOverflow` counts refused encoder observations. The signature
table holds 256 distinct render signatures. Excess signatures use id -1 and increment
`signatureOverflow`. Compute and blit records use signature id -1 and are grouped by kind.
`untimedEncoders` counts observed encoder begins that received no sample indices, including
off blocks. A disabled native timing path does not count encoder begins it never observes.

The existing command-buffer completion handler resolves only during sampling, using
`-[MTLCounterSampleBuffer resolveCounterRange:]`. It copies the used range into a preallocated
managed slot before returning that slot to the pool. The returned NSData lives in a completion-side
autorelease pool. Managed exceptions are contained and a failed resolve still returns the slot.
The existing command-buffer error-latch route continues independently. A completion after stop
returns its resources without resolving. Disposal releases idle slots and defers in-flight slots
until completion, without draining or waiting on the frame thread.

Resolved encoder records use append-only bounded storage with 196608 entries and ready bits,
matching the schema 7 storage policy. The `encoders` and `clockPairs` buffers expose `records`,
`overflow` and `incompleteWrites`. A capture that reaches a bound loses evidence explicitly.
`pendingCommandBuffers` exposes the tail at publication. There is no diagnostic wait to fill it.
A command buffer abandoned without submission can hold a slot until process exit. Capture is
single-use and one recorder samples at a time, as in schema 7.

Each encoder record contains `commandBuffer`, `callbackIndex`, zero-based command-buffer `ordinal`,
`kind`, `signatureId`, `startVertex`, `endVertex`, `startFragment`, `endFragment`, `startEncoder`,
`endEncoder` and `status`. Render records use the four stage values. Compute and blit records use
the two encoder values. Unused fields are null. `MTLCounterErrorValue` becomes null and increments
`errorValues`, never a zero-duration measurement. Status is `valid`, `missing`, `invalid`,
`resolveFailure` or `encoderUnavailable`. Resolve and clock exceptions increment `resolveFailures`
and `clockFailures`. Signature and device-ownership read failures increment `nativeFailures`. Invalid
or missing spans do not become zeroes in duration distributions.

### Signatures, clocks and summaries

`signatures` is a deduplicated array indexed by `signatureId`. Each render signature contains
`color0` through `color7`, `depth` and `stencil`. Each attachment records texture `width`, `height`,
`pixelFormat`, `textureType`, `arrayLength`, `sampleCount`, plus attachment `slice`, `level`,
`depthPlane`, `loadAction` and `storeAction`. A nil texture has zero texture properties. The
signature also records `renderTargetArrayLength`, `renderTargetWidth`, `renderTargetHeight`,
effective attachment `sampleCount` and `color0IsDrawable`. The last flag compares against the
texture obtained at the frame's actual drawable acquisition. Descriptor render-target dimensions
may be zero when Metal infers them from attachments. All signature reads occur only for a timed
render encoder with available sample capacity.

`clockPairs.records` contains one `sampleTimestamps:gpuTimestamp:` pair at baseline, each block
boundary and end. Each entry records callback index, callback boundary ticks, before/after
Stopwatch brackets, `cpuNanoseconds` and raw `gpuTicks`. The GPU unit is device-specific, not
assumed to be nanoseconds. `clockSegments` records the measured `nanosecondsPerGpuTick` for
each adjacent pair with increasing CPU and GPU clocks, together with its CPU/GPU origins and
callback endpoints. This follows Apple's
[timestamp conversion](https://developer.apple.com/documentation/metal/converting-gpu-timestamps-into-cpu-time).
For segment origins cpu0 and gpu0, map a GPU tick g to cpu0 + (g - gpu0) times the segment scale.
The raw pairs and per-segment scales expose clock drift. Stopwatch brackets allow CPU-clock
alignment without assuming the CPU nanosecond epoch equals Stopwatch's epoch.

`gpuSummary.passEncoders` groups records by kind and signature id. Each group has `vertexMs`,
`fragmentMs` and `encoderMs` distributions with count, mean, median, nearest-rank p95, p99 and
worst. Render whole-encoder spans run from the earliest stage start to the latest stage end,
only when both stages are valid. Individual valid stages can still contribute when the other
is missing. Compute and blit contribute only whole-encoder spans. Durations use the nearest
valid adjacent clock pair's scale, extrapolating the nearest segment for ticks outside its range.
Without valid calibration, no duration is reported.

`gpuSummary.timedFrameCompletionIntervalMs` and `untimedFrameCompletionIntervalMs` describe frame
GPU end-to-end intervals on each queue, ordered by GPU completion time. Only adjacent callback
indices within one recorded block contribute. Transitions, missing callbacks, unmatched
completions and non-frame command buffers do not contribute. These are GPU throughput
distributions, not completion-handler scheduling intervals or display intervals. They include
the complete frame workload in both block types and are the within-capture perturbation estimate.

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
not private jittered projection or fitted shadow matrices. `scene.EnableTiming` is last. GameApp3D
follows the F1 overlay's visibility with it, and it switches on per-pass CPU Stopwatch reads, so it
records whether the overlay's timing work ran. Boolean channels are 0 or 1.
All other channel values are exact signed integer counts or bytes. The annotator must fill its
fixed Span without allocation or I/O. No channel is recomputed by rendering.

## Verification boundary

Headless tests cover recorder lifecycle, bounds and overflow, inactive native read and handler
paths, exact annotations, warmed allocation-free sampling, summary arithmetic and missing coverage.
Schema 8 tests also cover stage attachments, reused descriptors, factory selection, pool and slot
overflow, error-value handling, signature bounds, block schedules, clock pairs and pass summaries.
One separately gated native smoke case records a headless offscreen render, serial compute and
blit encoder, commits once, waits for that buffer and resolves real stage timestamps. Its temporary
isolated test host links the same test source to avoid unrelated GPU probes during suite discovery.
The source diff against v21.23.0 must be empty for KhaozEngine.Render3D. Verification commands,
results, source identities and limitations belong in the implementer's external report.
CPU tests do not prove native handler delivery, actual GPU timing validity, physical presentation,
pixel identity or an FPS improvement. The parent owns DLL substitution and the single human-launched
capture. No windowed app, push, merge, release, package publication or tag is part of this archive.
