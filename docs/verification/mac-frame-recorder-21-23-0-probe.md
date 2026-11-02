# Private v21.23.0 frame recorder

NEVER MERGE this diagnostic archive into `main`. Branch
`fix/mac-frame-recorder-21-23-0-probe` starts exactly at released `v21.23.0`, commit
`cc5771ccc0a21593c07a0e0c1e20eb5f3b17377b`. It ports recorder commits `1066ce936` and
`5b6a423bd` and extends their evidence. No engine pin, version, draws, passes, copies, encoder count, public
timestamp pools, frames in flight, framebufferOnly policy, capture taps or waits change. Schema 11 alternates
committing offscreen work before drawable acquisition. The three shader gates and the directional cascade
depth and stencil store experiment stay in the tree, off in every recorder block.
`KhaozEngine.Render3D` differs from the base tag only for the three gates and their plumbing.
Only `KhaozEngine.Render3D`, `KhaozEngine.Gpu.Metal`, `KhaozEngine.Windowing` and `KhaozEngine.Game.Render3D`
contain modified production source. This archive is never released or packaged.

## Boundaries and activation

`KE_FRAME_DIAGNOSTICS_PATH` absent or empty disables recording. All native capture flags stay
false, no drawable handlers are attached, `Scene3D.PassObserver` stays unset, no store action changes and nothing
registers the gate seam, so every gate stays off. The shaders still read a zero switch value.
An enabled run requires an absolute, nonexistent
result path in an existing directory. Recorder creation preallocates storage and resolves its
native selectors, global presented block and QuartzCore import before measurement. While waiting,
the only per-callback recorder work is the sibling `<result>.start` trigger poll, at most twice
per second, plus Scene3D calling the preallocated pass observer, which returns at its sampling check. Consuming that file arms the recorder. The following opening callback boundary is the
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

## Schema 11

The existing top-level cadence fields remain `processId`, `appAssembly`, `engineAssembly`,
`stopwatchFrequency`, `durationSeconds`, `sampleCount`, `stopReason`, `baseline`, `end`,
`samples` and `summary`. `schemaVersion` is 11. All schema 7 to 10 fields retain their meaning, except that
`passTiming.blocks[].timed` is true in every block since schema 9 and `experiment.blocks[].discard` is false in every
block since schema 10. `shaderGates.schedule[].gates` is zero in every block since schema 11. Its additional top-level fields are:

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
- `shaderGates`: gate names, block schedule, the state each callback applied and counts, described under Shader gates.
- `earlySubmit`: `blockCallbacks` 72, `schedule` and `appliedPerCallback`, described under Early offscreen commit.

## Native timing records

All native hooks are gated by one static volatile sampling flag. GPU times are read with the
existing double-returning objc_msgSend in the existing `MetalCompletionHandler.Completed`
block. Its error-latch route still runs normally. Submits record command-buffer handle, queue,
Stopwatch ticks, callback index and kind immediately before commit. Kinds are 0 Other,
1 FrameRecording (the final buffer), 2 SetupFlush, 3 PresentBuffer and 4 EarlyFrameRecording (the offscreen prefix).
The prefix retains its callback index. Both frame buffers use the ordinary completion handler and error latch. The frame list is recognized by reference,
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

`gpuSummary.frameGpuBusyMs` sums valid matched frame-buffer end-minus-start durations per callback, converted to
milliseconds. Split frames require both completions. Frame-list submits on another thread have no callback to join
and keep their individual raw spans. `gpuIdleGapMs` describes nonnegative gaps in execution-time
order between valid completed buffers on each queue. It is not completion-handler dispatch delay.
`presentedIntervalMs` describes consecutive positive finite physical presented times, sorted by
native time. Each distribution contains count, mean, median, nearest-rank p95, p99 and worst.
Count 0 means no evidence and its numeric placeholders are 0. A valid GPU timing requires status
4, positive finite start and finite end at least start.

`unpresentedDrawables` counts callbacks reporting presentedTime 0. Per-sample
`frameCompletionCoverage`, `presentCallCoverage` and `presentedCoverage` mark whether a matched
valid completions for every captured frame buffer, a present call and a positive presented callback exist. `allCallbacksCovered`
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

While sampling, per-encoder timing stays on in every block. Schema 8 alternated it with off blocks,
and its measured overhead of about 1% is what let later schemas keep it on so pass times compare across
block states. Blocks are still 72 callbacks. Each `blocks` entry records `startCallback`,
`endCallbackExclusive`, `startTimestamp`, `endTimestamp` and `timed`. The final block may be partial.
All schema 7 instrumentation, annotations and command-buffer timing remain enabled. Encoders on other
managed threads are untimed. Ordinary setup-upload batches use their existing direct blit path outside
the frame command buffer and are not encoder-instrumented. Require support and usable encoder coverage
before comparing blocks.

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
They are not additive frame costs. Every block is timed, so this capture holds no within-run estimate
of the timing perturbation.

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
64 command buffers, so encoder ordinals include preceding untimed encoders.
If that table fills, `commandStateOverflow` counts refused encoder observations. The signature
table holds 256 distinct render signatures. Excess signatures use id -1 and increment
`signatureOverflow`. Compute and blit records use signature id -1 and are grouped by kind.
`untimedEncoders` counts observed encoder begins that received no sample indices. A disabled native
timing path does not count encoder begins it never observes.

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
distributions, not completion-handler scheduling intervals or display intervals. With timing on in
every block the untimed distribution is empty.

## Pass labels

`GameApp3D` sets `Scene3D.PassObserver` next to the annotator registration when the recorder is requested.
The observer forwards `BeginPass`, `EndPass` and `SkipPass` through `AppWindow.FrameDiagnosticsPasses` into the
Metal probe, so Game.Render3D does not reference Gpu.Metal internals. Scene3D rethrows an observer failure after
recording, so nothing the sink does may escape: the observer and the forwarder both catch everything.
It records no GPU commands. CPU phases and fused-motion marks are not forwarded.

Only the frame command list on the frame thread feeds the labels. Any other list or thread increments
`foreignEvents`. The scope stack belongs to one recording: a new callback or command buffer forgets scopes still
open and counts them in `abandonedScopes`.

Top-level `passLabels` holds:

- `labels`: Render3D's `RenderPassKey` ids. An event's `labelId` indexes this table. The bound is 256, id 0 is
  reserved for `shadow.directional`, and excess ids use -1 and increment `labelOverflow`.
- `events`: bounded `records`, `overflow` and `incompleteWrites`. A record holds `callbackIndex`, `commandBuffer`,
  `encoderOrdinal`, `kind` (begin, end or skip), `labelId` and `occurrence`. `encoderOrdinal` is the ordinal the next
  encoder on that buffer takes, so an event with ordinal N falls after encoder N-1 began and before encoder N began.
  The bound is 1048576, about 750 events a callback over a ten-second capture at 139 FPS. Capture 03 overflowed
  524288 at about 640 a callback.
- `stackOverflow` past 64 nested scopes, `unbalancedEnds`, `abandonedScopes`, `foreignEvents`, `observerFailures`.

Encoder records gain `labelId` and `labelOccurrence`, the innermost scope open at the encoder's begin, or -1. An
encoder opens on its first draw, so this is usually a leaf such as `shadow.rigid.opaque`, and an encoder can outlive
the scope it began in. They also gain `directionalScope` and `storeResolution`, below. Use the event stream to place
scopes against encoder ordinals.

## Transfers

Top-level `transfers` holds bounded `records`, `overflow` and `incompleteWrites`, with a bound of 131072. It records
each operation on a blit encoder the frame list opened: `bufferUpload` (a `MetalBufferUpload` staging copy),
`bufferCopy`, `textureToTexture`, `textureToBuffer`, `bufferToTexture`, `stagingToStaging`, `generateMipmaps`,
`swapchainToTexture` and `swapchainToBuffer`. A record holds `callbackIndex`, `commandBuffer`, `encoderOrdinal`,
`kind`, `bytes`, `destination`, `labelId` and `labelOccurrence`. One whole-texture copy records one row per mip and
layer. `bytes` is the padded upload size or `width * height * bytesPerTexel`. `generateMipmaps` reports 0, as does a
format the staging layout lacks. Uniform ring writes are memcpys with no blit encoder and are not transfers. The setup
batch is outside the frame list and is not recorded. Standalone multisample resolves are render encoders.

## Early offscreen commit

Schema 11 tests #1682. Sampling blocks of 72 callbacks alternate OFF then ON, starting OFF. Per-encoder timing,
labels and transfers stay on in both states. Shader gates and cascade discard stay off. Outside sampling, including
when `KE_FRAME_DIAGNOSTICS_PATH` is absent, there is no early submission.

`MetalCommandList.SetFramebuffer` reaches the split only for the frame list's first bind of an unresolved swapchain
framebuffer. It runs before `AsBound` calls `MetalPresentBoundary.AcquireAndPublish` and `NextDrawable`. This is already
a framebuffer-change boundary. `EndPass` performs the clear-only flush and render encoder end that the change owes,
and `EnsureNoEncoder` ends a remaining blit or compute encoder before commit. A refused pending clear or a recording with no encoded work leaves the
frame unsplit. No encoder is cut in half and no pass or encoder is added.

`MetalEarlyFrameSubmit` flushes pending setup work before taking the existing submit lock. It rechecks liveness,
attaches the ordinary completion handler and commits the prefix with kind 4, without presenting or signalling the
shared event. The list drops that buffer's retain and uncommitted count, then takes a fresh retained buffer from
its existing source and queue. It continues the recording without a new `Begin`, ring rotation or arena rotation.
The final framebuffer pass, UI and capture taps record into the tail. Final submission, fences and present follow
the existing path. Pass-label scopes survive the split, while encoder ordinals restart at zero for the new buffer,
keeping their existing per-buffer meaning. Pass timing owns separate sample slots for the two buffers and releases
each on its own completion, including late completions after sampling stops.

The pair owns one frame slot. Only final submit encodes and registers the ordinary `MTLSharedEvent` value, records
that value as the uniform segment owner and gives it to the staging arena. Neither early completion nor early commit
recycles storage. The arena continues allocating after the prefix's used bytes. An abandoned prefix quarantines its
segment until device teardown. A later Begin cannot claim it, and off-timeline ring updates defer rather than overwrite
it. The device's existing queue drain at teardown covers that prefix even though it has no timeline signal. A lost
device follows the existing no-op release posture. No recovery buffer, extra GPU signal or wait is introduced.

Uniform bindings record the segment's recording identity while sampling. After an early commit, a record-time write
to any uniform buffer already referenced by the prefix is refused before copying bytes. Whole-buffer protection is
conservative even for disjoint ranges. New tail-only uniform buffers remain writable. Device-level uploads, maps, WaitForIdle and
foreign submissions are refused while an early frame awaits its final submission. Otherwise they could put setup
uploads after a prefix that previously executed after them, or modify bytes the prefix is reading. This probe fails
on such a shape instead of silently testing different inputs. Public timestamp batches already recorded on the list
leave that frame unsplit, because their existing ownership is one command buffer. An externally held mapped pointer
must still obey the ordinary resource-lifetime contract.

`earlySubmit.schedule` contains `startCallback`, `endCallbackExclusive`, `startTimestamp`, `endTimestamp` and `enabled`.
`appliedPerCallback` is true if at least one prefix actually committed in that callback. An ON block can contain false
entries, for example a suppressed frame, an already acquired drawable, a public timestamp batch or no swapchain bind.
The state is scheduled treatment, and the applied flag exposes whether the mechanism ran.

`gpuSummary.earlySubmitStates` contains OFF and ON entries with:

- `enabled`, `scheduledCallbacks`, `appliedCallbacks` and `settledCallbacks`.
- `callbackIntervalMs` and `framesPerSecond`, from the callback samples in that state.
- `frameCompletionIntervalMs`, between adjacent callbacks' final frame ends inside one block on the same queue.
- `gpuIdlePerFrameMs`, the positive part of this frame's first buffer start minus the previous frame's last buffer end.
  This uses the same adjacency and block rule, includes zero for overlap, and excludes the internal prefix-to-tail gap.
- `attributedBusyMs`, from the existing stage-overlap attribution sweep across both buffers, skipping three callbacks
  at each block edge. It is a sum per settled callback, not the sum of overlapping raw stage spans.
- `drawableAcquireWaitMsPerCallback`, consecutive cumulative counter differences, including zero-wait callbacks.
- `presentedIntervalMs`, from presented drawables matched to their callback. Only adjacent callbacks inside one block
  contribute. Reused drawable ids match their latest preceding present call.

A frame has one final submit and at most one prefix on the same queue and callback. Both need valid completions for
frame coverage and the new frame metrics. Matching uses queue, handle and latest preceding commit timestamp, so native
handle reuse and unordered handler delivery do not confuse the pair. Existing `frameGpuBusyMs` sums both buffers'
GPU durations per frame, preserving its meaning without counting the gap between them as busy. Existing global queue
idle and presentation distributions keep their original definitions.

### Identical-output argument and limits

Both buffers come from the same queue and commit in prefix-then-tail order. Every existing encoder remains intact,
with the same render attachments, load/store actions, draws and transfers, in the same order. There are no new GPU
commands, encoders or waits. The final event signal follows both parts, so frame-slot reuse cannot race the prefix.
The uniform and off-timeline guards above preserve inputs where moving commit earlier would otherwise expose a write.

`MetalEarlyFrameSplitGpuTests` is separately gated by `KE_GPU_TESTS=1`. It runs headlessly, records three offscreen
draw passes, a dependent final copy and a final draw, once unsplit and once split through this list's real early-submit
path on the same queue, and compares all four targets
as 4096 readback bytes. Background and triangle-centre byte controls prove that both clears and draws ran. It creates no window,
layer, swapchain or drawable, so it does not prove the live drawable-acquire hook or physical presentation. The native
smoke independently checks render, compute and blit timing selectors.

The readback is a small synthetic workload on one GPU. It does not establish Hollowmere pixel identity or an FPS gain.
Buffers may overlap differently on the GPU despite identical command order. ON also pays one extra command buffer,
commit, completion handler and timing sample-slot occupancy. Compare states within one capture and inspect applied
counts and missing coverage before interpreting a difference. Drift, thermal changes and work crossing block edges
remain limitations. No claim about the 144 Hz budget follows from this probe alone.

## Shader gates

The shading study found work whose result is already known exactly, and capture 03 had shown the bandwidth fixes were
small. Three switches skip that work. Each is a uniform branch, so no shader recompiles mid-capture. `ShaderGateProbe`
holds the mask, `Scene3D.ProbeShaderGates` the current value, off by default.

| Name | Bit | Switch | Skips |
| --- | --- | --- | --- |
| `pointShadowZeroNdl` (#1676) | 1 | `IndirectParams.y` | `samplePointShadowCombined` when the banded `ndl` is not above 0. `ndl` then zeroes both diffuse and specular, whatever `att` holds |
| `underwaterMarchZero` (#1675) | 2 | `IndirectParams.z` | The remaining slices once `floatBitsToUint(visibility) == 0u`. Later slices only multiply positive zero by a value in 0 to 1. The receiver coverage fade after the loop still runs |
| `rigidWorldReuse` (#1679) | 4 | `MotionParams.y` | `prevModel * Position` for slot -1, reusing `world`. `PrevViewProj` still applies |

Neither frame block changed layout: the switches take components nothing else reads, zero when off, so those bytes
equal the released upload. The conditions are exact on purpose. No epsilon, no geometric normal and no change to
accumulation order. The study's optional entry filter bypass is left out: it would have to reproduce each strength
and coverage mix in `sampleKeyShadow`, and no measurement says it is worth that.

With `rigidWorldReuse` on, `RigidMotionSlots.Build` also gives a keyed instance slot -1 when its reduced previous
transform has exactly the bits of the matrix `UploadInstancesRelative` just uploaded. Float equality would call -0 and +0
equal. The instance keeps its entry in the previous-transform upload, so bytes transferred do not depend on the state and
only the vertex path differs. A shipping version would drop that entry and save a little more. Skinned, CPU-skinned
and wind-deformed vertices use other programs and never qualify.

`GameApp3D` registers `SceneFrameShaderGates` through `AppWindow.RegisterFrameDiagnosticsShaderGates` only when the
recorder is requested. The recorder's opening boundary is before prepare or update, so one callback sees one state.
Since schema 11, every callback applies baseline with all gates zero. The retained test-only gate-block switch
rotates baseline, `pointShadowZeroNdl`, `underwaterMarchZero`, `rigidWorldReuse` in blocks of 72 callbacks.
The schedule leaves the gates alone until sampling starts, and turns them off at the last boundary and on every failure and
disposal. After that it never applies a nonzero mask. Nothing else in the engine sets the property.

Top-level `shaderGates` holds:

- `names`: bit i of a mask is `names[i]`. `blockCallbacks` is 72.
- `schedule`: `startCallback`, `endCallbackExclusive`, `startTimestamp`, `endTimestamp`, `state` and `gates`.
- `appliedPerCallback`: one value per sample, the mask that callback's frames actually uploaded, read back from the scene.
  -1 means no frame rendered, -2 means renders disagreed and -3 means nothing was registered.
- `counts`: per state `blocks`, `scheduledCallbacks`, `matchingCallbacks`, `noFrameCallbacks`, `mismatchedCallbacks`
  (which includes -2 and -3). `applyFailures` sits beside it.

`gpuSummary.shaderGateStates` has one entry per state with `state`, `gates`, `mainPassLabel`, `settledCallbacks`,
`frameCompletionIntervalMs` (the same-block adjacency rule above) and three kinds of main pass input. The main pass is the
encoder labelled `opaque.rigid` when it opened. Terrain, foliage and skinned draws usually share that encoder under their
own scopes, so these are the whole forward target, not rigid geometry alone.

- `mainPassVertexMs`, `mainPassFragmentMs`, `mainPassEncoderMs`: raw stage spans. These overlap other passes and frames.
- `mainPassExclusiveVertexMs`, `mainPassExclusiveFragmentMs`: each instant of GPU time split evenly among the stage intervals
  active at it, summed per callback, the same sweep capture 03's offline analysis used. Valid records only.
- `attributedBusyMs`: the same sweep's total per callback.

The exclusive values and `settledCallbacks` skip 3 callbacks at each block edge, because overlap there reaches a
neighbouring state. Raw records stay in the report for offline analysis.

### Pixel identity evidence

`ShaderGatePixelIdentityGpuTests` is gated by `KE_GPU_TESTS=1` and needs no window or swapchain. For each gate it renders a
small deterministic scene through `Scene3D` with temporal rendering and HDR on for six frames, off twice and on once,
each on a fresh headless device. It compares the raw bytes of HDR colour, normal, scene depth, motion, key visibility and
the resolved picture on every frame. The repeated off run separates a gate's difference from the scene's own
nondeterminism. A control with the caster or the motion removed proves that the comparison sees a real change.

| Gate | What the scene exercises |
| --- | --- |
| `pointShadowZeroNdl` | Cel bands 4, a soft shadowed static lamp 0.6 m above a floor plus a dynamic light and a moving caster. Banded zero with raw `ndl` above 0 beyond about 4.7 m. The count replays banded `ndl` in C# from the stored normal and depth |
| `underwaterMarchZero` | A shadow-only slab 0.5 m under the surface over a bed 3 m down, four cascades. The caster is below the entry point, so a stored key visibility of 0 is the march's own result |
| `rigidWorldReuse` | A moving camera, static and moving keyed boxes, unkeyed boxes and a render origin step. Slot counts come from the frame's own motion slots |

Limits:

- The gates are invisible by design, so a switch that never reached the shader would also pass. `ShaderGateProbeTests` pins the
  uploaded values and the shader conditions, but no execution shows the branch taken.
- These are small synthetic scenes on one GPU, not Hollowmere. The pixel identity they show holds for the compiled shaders of
  this tree on this GPU.
- Off and on run the same compiled pipeline. No pixel comparison runs the off state against the released shader, which would
  need a build of the released tree. `NonWaterShaderSourceEqualityTests` shows each stage's GLSL with the gate text taken out
  still hashes to the released capture. The baseline blocks may still differ from v21.23.0 in timing or bits if the Metal
  compiler optimizes the new branches differently.
- The compiled SPIR-V, MSL and HLSL hash tables are rebaked for this archive. The gates move 30 lit fragment programs and the
  rigid motion vertex with its visibility variant, 32 of each table's rows. No other row moved.

### Measurement limits for the gates

- Compare states only within one capture. A later block is not independent of an earlier one, and drift reaches them
  differently with four states than with two, because each state returns only every 288 callbacks.
- A state's GPU work can still be in flight past its block, so a completion crossing a block is excluded and the
  exclusive values skip the edges.
- The gates can be slower. A branch adds a uniform read and can change register allocation or vectorization. A
  compiler may also already remove the duplicate transform for slot -1. Report a negative result as measured.
- The states are one gate at a time. Savings need not add, because their fragments and instructions overlap.
- None of this establishes the 144 Hz budget or a tail improvement.

## Cascade depth-discard experiment

Off in every recorder block since schema 10, because capture 03 measured it. `MetalPassTimingCapture(discardBlocks: true)`
turns it back on, and only its tests and the native smoke do that. Top-level `experiment` has `name` `cascadeDepthDiscard`,
`blockCallbacks` 72 and `blocks`, whose entries repeat the pass timing block bounds with `discard`. With the experiment on,
blocks alternate starting with discard off. It counts `openedUnknown`,
`dontCareApplied`, `keptStoreScopeOpen`, `resolvedDuringStop`, `resolveFailures`, `unresolvedEncoders`,
`descriptorFailures` and `directionalEncodersOff`. `openedUnknown` equals the sum of the second through sixth.

In a discard block, a frame-list render encoder that begins while `shadow.directional` is open and whose descriptor
names a depth or stencil texture is created with an Unknown depth and stencil store action. Any nesting depth counts,
because the first draw that opens the encoder is inside a nested scope such as `shadow.rigid.opaque`. The signature is
read first and the descriptor returns to Store once the encoder exists, including when the factory throws or returns
nil, so a signature id means the same thing in both states. Colour attachments are untouched. Immediately before
that encoder's `endEncoding`, `MetalEncoderSink` sends `setDepthStoreAction:` and `setStencilStoreAction:` with DontCare
if the scope has ended and sampling is still on, otherwise Store. A blit that splits the pass mid-scope therefore
stores, because the next encoder loads that depth. `MetalFrameTimingProbe.PassTiming` outlives the capture, so an
encoder still open after stop or disposal is resolved to Store. A failed DontCare also falls back to Store.

An encoder record's `storeResolution` is null outside the directional scope, `descriptorStore` in an off block,
then `dontCare`, `storeScopeOpen`, `storeStopped` or `failed`. `unresolved` in a report would mean a defect.
`gpuSummary.cascadeDepthDiscard` holds one entry per state with `discard`, `frameCompletionIntervalMs` (the same
same-block adjacency rule as above), `directionalVertexMs`, `directionalFragmentMs` and `directionalEncoderMs`.
Blocks decide the state, so a discard-block encoder that kept Store still counts as on.

### Why output pixels are identical

Paths are under `KhaozEngine.Render3D/`. The gates changed none of these lines' behaviour.

- The texture cannot be read by a shader. `Rendering/ShadowMapRenderer.cs:222` creates it with
  `GpuTextureUsage.DepthStencil` alone, never `Sampled`.
- Nothing can copy it. Its only uses are the framebuffer at `Rendering/ShadowMapRenderer.cs:224` and the private
  layout record's constructor, property and dispose at `:639`, `:654` and `:677`. No member exposes the texture. The
  framebuffer is private too: `:412` binds it and `:126` exposes only its output formats.
- Receivers bind the separate R32F atlas. `Rendering/ShadowMapRenderer.cs:113` exposes only that texture, read at
  `Rendering/ModelRenderer.cs:303`, `Rendering/ModelRenderer.TileGround.cs:128`, `Rendering/ModelRenderer.InteriorCut.cs:34`
  and `Scene3D.WaterColumnLight.cs:58`.
- Every pass clears it first. `Rendering/ShadowMapRenderer.cs:412` through `:414` binds and clears the only time that
  framebuffer is bound, so no frame loads an earlier frame's depth. `MetalRenderPassSchedule.ClearDepthStencil` folds
  that into the Clear load action, and `MetalRenderApi.DescribeDepth` applies it with stencil value 0. Metal never
  loads a discarded depth because the only encoders that load it are mid-scope reopenings, which follow a Store.
- Frames that skip the pass bind nothing: `Scene3D.CascadeShadowPass.cs:61` only reports a skip.
- The atlas colour store is unchanged, and depth testing inside the encoder does not depend on the store action.
- Nothing draws to that framebuffer after the scope without rebinding it. The next pass binds its own framebuffer,
  which is also what normally ends this encoder after the scope has closed.

This argument has not been checked by a pixel comparison.

### Measurement limits

- Compare states only within one capture. The observer is on in both states, so Scene3D's extra caster and draw-list
  checks under an observer, and the event recording itself, are CPU costs earlier schemas did not have.
- The Unknown path is not the shipping shape. A real change would declare DontCare in the descriptor, which may
  let the driver plan better than a store action decided at the encoder's end, so this can understate a saving.
- There is no store-byte counter. Stage-boundary spans may not include the tile store, so an unchanged
  `directionalEncoderMs` does not prove the store was free. `frameCompletionIntervalMs` is the primary comparison.
- A directional encoder exists only on frames that re-render the atlas. Skip events and encoder records show how
  many frames in each state actually exercised the change.
- A block comparison is sensitive to anything else that changes over ten seconds, including camera movement and
  thermal state, because alternation reduces drift but does not remove it. Frames in flight smear a state's GPU work
  past the boundary only as completions, and intervals crossing a block are excluded.
- None of this establishes the 144 Hz budget or a tail improvement.

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
Schema 9 tests cover label interning and bounds, the never-throwing observer, event order, transfer records, every
discard decision, the block schedule, serialization and the per-state summary, all through fakes.
Schema 10 tests cover gate defaults and gating outside sampling, the rotation, the per-callback applied state, every way
out of sampling, serialization, the per-state summary arithmetic, the uploaded switch values and bit-identical slot
selection, also through fakes.
Schema 11 tests cover the split gating and encoder boundary, final-buffer slot ownership, abandonment quarantine,
uniform-write protection, label continuity, actual-applied flags, serialization and per-state pair/idle arithmetic.
One separately gated native smoke case records a headless offscreen render, serial compute and
blit encoder, then two passes over a Depth32Float_Stencil8 attachment through the real sink, opened Unknown and
resolved to Store and DontCare. It commits once, waits for that buffer and resolves real stage timestamps. Its temporary
isolated test host links the same test source to avoid unrelated GPU probes during suite discovery.
This increment changes no KhaozEngine.Render3D source. The source diff against v21.23.0 for KhaozEngine.Render3D remains
limited to the three gates and their plumbing. Verification
commands, results, source identities and limitations belong in the implementer's external report.
CPU tests do not prove native handler delivery, actual GPU timing validity, physical presentation,
pixel identity or an FPS improvement. The gated GPU test is the pixel identity evidence, with the limits above. The parent owns DLL substitution and the single human-launched
capture. No windowed app, push, merge, release, package publication or tag is part of this archive.
