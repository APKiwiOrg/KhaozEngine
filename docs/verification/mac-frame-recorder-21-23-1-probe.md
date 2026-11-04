# Private v21.23.1 frame recorder

NEVER MERGE this diagnostic archive into `main`. Branch `fix/mac-frame-recorder-21-23-1-probe` starts
at released `v21.23.1`, commit `be27e609d1e13b1230c3196ac2d0dd7cdf8509b0`. It is never released or
packaged. Schema 14 retains the schema 13 measurements of the released split-submission renderer
and adds the owner-approved, sampling-only G-buffer category draw cuts. It does not contain the
shader or submission experiments from the older archive.

## Activation and boundaries

With `KE_FRAME_DIAGNOSTICS_PATH` absent or empty, no recorder, annotator, native counter resources,
presented handler or sampling hooks are activated. The renderer uses the released encoder factories,
commit order, stores and waits. The released prefix submission still runs normally.

An enabled run requires an absolute, nonexistent result path in an existing directory. The normal
collector uses `result.json`. Create the sibling `<result>.start` file when ready. The waiting recorder
polls that trigger at most twice per second. Consuming it arms measurement. The next opening callback
boundary is the baseline, excluding trigger I/O and its partial callback interval.

The recorder observes AppWindow's render callback before delta clamping, input, prepare and render.
A sample describes the previous completed callback. Collection ends at the first boundary at least
ten seconds after baseline, or after 4096 intervals. Storage and selectors are prepared before
arming. Warmed callback sampling does not allocate managed memory or write diagnostic files.
One background writer serializes, flushes and atomically moves a temporary sibling without
overwriting an existing result. Disposal joins publication. Early close publishes no completed result.

This is completed render-loop callback cadence. The presented records independently measure physical
presentation when delivered. Cumulative wait counters measure CPU API durations, including full
drawable acquisition calls. Boundary validity checks foreground focus, minimize, render suppression,
positive stable framebuffer size and actual renderer-owned swapchain initialization mode. Changes
that reverse between boundaries are invisible. For the intended comparison require cap 0 and vsync
true at baseline, end and both states of every sample. `cadenceCapped=true` is expected with vsync.

## Rendering boundary

Game.Render3D registers `SceneFrameDiagnosticsAnnotator` and the scene's
`IFrameDiagnosticsGBufferGates` seam only when the path is enabled. The annotator reads public
last-frame diagnostics, camera inputs and light inputs. It adds no prepare, render, capture tap or
copy-source request. Render3D skips selected main-pass draw calls only while the recorder samples,
as described below. Outside that window the scene starts and remains at `GBufferGate.Disabled`.

The recorder registers no Render3D pass observer. `passObserverRegistered=false` explicitly describes
that recorder policy. The host must also leave its own observer unset for this measurement. The
released cascade working-depth discard therefore applies. Descriptor signatures observe the released
load and store actions without changing them. No shader source, point-shadow gate, cascade store
action, frame split decision, uniform protection, resource ownership or timeline signal is changed.

While sampling, the existing encoder sink attaches hardware stage-boundary sample indices. Compute
and blit use equivalent descriptor factories, with compute explicitly Serial. Attachments are cleared
in a finally block after creation so reused descriptors cannot leak sampling. There are no inline GPU
markers, diagnostic copies, extra encoders, resolves on the GPU, new signals, waits or drains. Stage
timing itself can perturb execution, so this probe is evidence from an instrumented renderer.

## Schema 14

All ported field names and units retain their old meanings. New fields are identified below. JSON
uses camelCase, numeric command kinds and nullable missing stage values.

- `schemaVersion` is 14. `processId`, `appAssembly`, `engineAssembly`, `stopwatchFrequency`,
  `durationSeconds`, `sampleCount`, `stopReason`, `baseline`, `end`, `samples` and `summary` keep
  their cadence and cumulative-counter meanings. Stop reasons are `duration` or `capacity`.
- Each sample has `startTimestamp`, `endTimestamp`, `monotonicIntervalSeconds`, `loopDeltaSeconds`,
  `completedFrameWindow`, `boundaryWindow` and cumulative device `counters`. The window includes
  `focused`, `minimized`, `renderSuppressed`, dimensions, `effectiveBaseCapHz`, `vsync` and
  `rendererInitializesSwapchain`.
- `summary` contains FPS from total callback count divided by summed monotonic intervals, mean,
  median, nearest-rank p95/p99, worst milliseconds, `shareWithin144HzBudget`, validity/capacity flags
  and `counterDelta`. Unsupported counters yield null. The delta retains frames begun, drain,
  backpressure, acquire and off-timeline counts/durations, plus the existing per-interval values.
- `identity` contains process id/path, command-line arguments, sorted loaded KhaozEngine and
  Grimhollow assembly identities (name, version, informational version, MVID and location), and
  sorted KE_* environment names/values. The writer collects this after sampling. It can contain
  private paths and arguments.
- `framesInFlight` is the actual Metal ring depth, or null on unsupported devices.
- `baselineCapture` and `endCapture` contain `tapCount` and `swapchainCopySourceRequested`, observed
  without changing either setting.
- `baselineClock` and `endClock` contain `available`, `beforeTimestamp`, `afterTimestamp` and
  `mediaTime`. Each is the narrowest of eight Stopwatch/CACurrentMediaTime/Stopwatch brackets.
  Map native seconds to Stopwatch ticks through the bracket midpoint and frequency. The bracket
  width is uncertainty, and the two maps expose drift. Unsupported platforms set available false.
- `timing` contains bounded `submissions`, `completions`, `presentCalls`, `presented` and
  `nativeReadFailures`. Each buffer has `records`, `overflow` and `incompleteWrites`.
- `annotations` has fixed `channels` and one signed integer `values` row per sample.
- `passTiming` contains hardware stage support, signatures, encoders, clock pairs, calibration,
  fixed timing blocks and evidence-loss counters, described below.
- `gpuSummary` keeps frame busy, queue idle, presented intervals, coverage and evidence-loss fields,
  plus the new pair-aware frame metrics described below.
- `passObserverRegistered` is new and false. The recorder never registers an observer.
- `earlySubmit` is observation-only. `appliedPerCallback` keeps its meaning, true when at least one
  prefix actually committed, and `appliedCallbacks` counts true values. New
  `prefixCommitsPerCallback` records the exact prefix count for each callback, including zero when
  no split happened. There is no schedule, switch or treatment state.
- `gbufferGates` is new in schema 14. It describes the five category cuts, their actual callback
  schedule, applied states, skipped draws and neighbouring-baseline pairs below. All schema 13
  field names and meanings remain unchanged.

## G-buffer category cuts

There are five gates. These are renderer draw families, because props and ground cover do not
have separate main-pass queues. A skipped draw is one otherwise valid, visible GPU indexed draw,
not one instance, triangle, submitted item or culled run.

| Id and name | Source methods | Content covered |
| --- | --- | --- |
| 1 `rigid` | `Scene3D.DrawOpaqueRigidGeometry` | Ordinary instanced meshes, including props and CPU-submitted ground cover |
| 2 `splat` | `Scene3D.DrawSplatRuns` | Five-layer splat terrain |
| 3 `tileGround` | `Scene3D.DrawTileGroundRuns` | Tile-ground material meshes |
| 4 `foliage` | `Scene3D.DrawFoliagePass` | Retained GPU foliage, including GPU ground-cover batches |
| 5 `skinned` | `Scene3D.DrawGpuSkinnedMain`, `Scene3D.DrawCpuSkinnedMain` | Both character skinning paths, including dissolve variants |

Only the draw and its actual-draw statistics are skipped. Grouping, visibility, rigid and bone
uploads, main skinned header uploads, foliage updates and motion-history bookkeeping continue.
Directional cascades and point shadows never consult the gates. Lighting, resolves, stores,
submission boundaries and all subsequent passes keep their ordinary paths. Skipped geometry still
casts shadows. It changes the visible depth/normal receivers, so later lighting and temporal work
can change cost as a consequence. Attribute the main G-buffer encoder, rather than interpreting
the whole-frame difference as an isolated category cost. Even inside that encoder, removing a
depth writer changes occlusion and overdraw for remaining geometry. Each delta is a marginal
cost, and the five deltas need not sum to the baseline pass cost.

The same model framebuffer also receives `DrawTexturedBillboards`, `DrawBeams`, `DrawTrails`,
`DrawOverlayMeshes` and `DrawSilhouettes`. These transparent/effect paths preserve the opaque
normal and linear-depth attachments and remain ungated. Background, blob/ground decals, water,
particles, depth lines and post processing also remain ungated. There is no extra independent
opaque prop or ground-cover draw family beyond the five above.

The schedule begins at sampling callback zero, after trigger I/O, and follows
`B G1 B G2 B G3 B G4 B G5 B G1 ... B G5 B`. Nominal blocks contain **36 callbacks**.
There are 15 gate blocks and 16 baselines, giving three rotations in 1,116 callbacks when every
block is full. Each baseline between gates is shared by both neighbours. After the fifteenth gate
block the last baseline starts at callback 1,080 and holds until the unchanged ten-second duration
or capacity boundary. Its recorded extent can therefore exceed 36 callbacks. A slower capture can
end before all rotations finish. The schedule records this honestly, without extending duration.

Analysis drops the first **six callbacks of every block**, then pairs each gate block with both
its preceding and following baselines. Use the recorded extents and lengths, not the old 48- or
72-callback assumptions. Reject pairs without enough retained baseline/treatment evidence, wrong
applied states, unstable workload/camera or missing native completion/stage evidence. The final
baseline needs retained callbacks too. The temporal image can be disturbed at every transition,
but no gate clears or invalidates a renderer cache or history.

`gbufferGates` fields are:

- `gates` lists each `id`, `name` and `sourceMethods`.
- `blockCallbacks` is 36 and `discardCallbacksPerBlock` is 6.
- `schedule` contains `startCallback`, `endCallbackExclusive`, `startTimestamp`, `endTimestamp`,
  `gateId`, `gate` and nominal `blockCallbacks`. Baseline has id 0 and name `baseline`. Timestamps
  are Stopwatch ticks. The final baseline's actual length is its end minus its start.
- `pairs` gives zero-based `gateBlock`, `baselineBeforeBlock` and `baselineAfterBlock` schedule
  indices. A pair is listed only when both neighbouring baseline blocks were observed. A listed
  pair can still have a short last block and requires the analysis checks above.
- `appliedPerCallback` has one entry per completed sample. Values 0 through 5 name the actual
  baseline or gate. -1 means no main G-buffer frame rendered, -2 means renders disagreed, and -3
  means no scene gate source was registered. Scheduled state alone is never proof of application.
- `skippedDrawsPerCallback` has one row per sample and five signed integer columns in gate-id
  order. Counts sum all renders in that callback, including mixed-state renders. Consumption
  clears all counts, so a suppressed callback cannot repeat the previous frame's evidence.
- `counts` contains baseline and each gate's `gateId`, `gate`, `blocks`, `scheduledCallbacks`,
  `matchingCallbacks`, `noFrameCallbacks`, `mismatchedCallbacks` and total `skippedDraws`. Per-gate
  skipped totals use their own column across all callbacks. These raw totals include the six
  discarded callbacks, allowing analysis to choose its retained evidence explicitly.
- `applyFailures` counts refused transitions. A nonzero value invalidates the comparison.

The scene records an applied state when its main G-buffer path actually runs, including an empty
opaque queue. Selection changes only at opening callback boundaries. Duration, capacity, early
close and failure all disable selection. No environment path means no recorder or schedule, and
an unarmed, armed-but-not-sampling, stopped or failed recorder never leaves a gate enabled.

## Native records and split-frame association

All native reads and handler attachment are guarded by the sampling flag. Submissions record
`commandBuffer`, `queue`, Stopwatch `timestamp`, `callbackIndex` and `kind` immediately before the
existing commit. Kind 0 is Other, 1 FrameRecording (final), 2 SetupFlush, 3 PresentBuffer and
4 EarlyFrameRecording (prefix). Both released frame buffers carry the frame list's identity.
Foreign lists are Other, including foreign prefixes on the callback thread. Other managed threads
use callback index -1. Prefix counts increment only after a successful production prefix commit.

The existing completion error handler also records handle, queue, observation ticks, status,
`gpuStartTime` and `gpuEndTime` in native media seconds. Its ordinary error-latch delivery remains.
Completion handling returns pass timing slots even after sampling stops, without resolving them.
Setup and present retain their existing completion handlers. A queue drain adds that same handler
only while sampling, and keeps its existing wait unchanged.

Immediately before the existing presentDrawable call, a capture-free global presented block is
attached to that drawable. The call record contains `drawableId`, `drawableHandle`, command buffer,
Stopwatch timestamp and callback index. Presented records contain drawable id, observation ticks
and `presentedTime` in native media seconds. Zero means not presented. No layer flags change.
Native exceptions are contained and counted.

Submission and completion buffers each hold 65536 records. Present-call and presented buffers each
hold 8192. Producers atomically reserve, write, then publish a ready bit. Overflow never overwrites
old evidence. The writer copies ready slots without waiting. `incompleteWrites` counts unfinished
reservations. Native records are clipped to inclusive baseline/end callback ticks. A missing tail
completion or presentation is a coverage gap, never evidence of zero cost.

Match completions by queue, handle and latest preceding submission timestamp. This handles native
handle reuse and unordered handler delivery. A frame has one final and at most one prefix on the
same queue and callback. All captured parts need valid completions. A valid timing has status 4,
positive finite start and finite end at least start. Partial pairs never enter frame coverage or
frame metrics. Off-thread final buffers retain their individual raw busy spans.

`gpuSummary.frameGpuBusyMs` sums both completed buffer durations per frame. It excludes any internal
prefix-to-final gap. `gpuIdleGapMs` retains its global per-queue, execution-order buffer-gap meaning,
including internal split gaps. `presentedIntervalMs` retains consecutive positive finite physical
presentation intervals sorted by native time.

New `gpuSummary.gpuIdlePerFrameMs` is max(0, this frame's first buffer start minus the previous
frame's last buffer end), in milliseconds. It excludes the internal split gap and includes zero
for overlap. New `frameCompletionIntervalMs` is the difference between those frames' final GPU ends.
Both require adjacent callback indices on the same queue with complete valid pairs. Missing
callbacks break adjacency. These whole-capture metrics do not exclude the artificial stage clock
block boundaries.

Every distribution contains count, mean, median, nearest-rank p95, p99 and worst. Count zero means
no evidence, and its numeric placeholders are zero. `frameCompletionCoverage`, `presentCallCoverage`
and `presentedCoverage` contain one flag per callback. `allCallbacksCovered` requires all three.
Reused drawable ids pair within successive present-call timestamp ranges, so a later presentation
cannot fill an earlier gap. `unpresentedDrawables`, `overflowed`, `incompleteWrites`,
`nativeReadFailures`, `unmatchedCompletions` and `invalidGpuTimings` expose evidence loss.

## Encoder stages and signatures

The recorder creates counter resources only from its actual Metal device. Discovery requires the
common timestamp counter set and atStageBoundary support. Failure sets `passTiming.supported=false`
with `reason`, while callback and command-buffer timing continue. Waiting does not attach counters.

The pool has 16 shared buffers of 4096 samples. Render consumes four indices (start/end vertex,
start/end fragment). Compute and blit consume two encoder indices. There is no wait for a slot.
Pool exhaustion leaves that command buffer untimed and increments `skippedCommandBuffers`. Sample
exhaustion leaves later encoders untimed and increments `capacityOverflow`. A fixed table tracks
64 command buffers and per-buffer encoder ordinals. `commandStateOverflow` exposes table exhaustion.
There are 256 render signatures, with excess using id -1 and incrementing `signatureOverflow`.
`untimedEncoders` counts observed begins that receive no indices.

`blocks` retains the 72-callback clock-calibration grouping, with start/end callbacks and timestamps
and `timed=true` throughout. `clockPairs.records` samples device CPU nanoseconds and raw GPU ticks
at baseline, block boundaries and end, with Stopwatch brackets. `clockSegments` uses adjacent
increasing CPU/GPU pairs to measure nanoseconds per GPU tick. It does not assume the GPU tick unit.
Map through cpu0 + (gpu - gpu0) * (cpu1 - cpu0) / (gpu1 - gpu0). Stage durations use the nearest valid
segment, extrapolating the nearest one beyond its range. No calibration means no duration evidence.

The existing completion handler resolves the used range on the CPU in an autorelease pool, copies
it into preallocated storage and returns the slot. Late completion only returns resources. Disposal
releases idle slots and defers in-flight release without a drain. An abandoned unsubmitted buffer
can hold its slot until process exit. The recorder is single-use and only one capture samples.

`encoders` holds up to 196608 records, with overflow and incomplete-write counts. Each record has
`commandBuffer`, `callbackIndex`, zero-based per-buffer `ordinal`, `kind`, `signatureId`,
`startVertex`, `endVertex`, `startFragment`, `endFragment`, `startEncoder`, `endEncoder` and `status`.
Unused or error-valued fields are null. Status is valid, missing, invalid, resolveFailure or
encoderUnavailable. `resolveFailures`, `errorValues`, `clockFailures`, `nativeFailures` and
`pendingCommandBuffers` expose losses. A prefix and final own separate slots and both start their
encoder ordinals at zero.

Each signature has color0 through color7, depth and stencil attachments. Each attachment records
texture dimensions, pixel format, texture type, array length, sample count, slice, level, depth
plane and load/store actions. Nil texture properties are zero. Signatures also record render-target
array length/dimensions, effective sample count and `color0IsDrawable`, compared with the actual
acquired drawable texture. Metal can infer dimensions when the descriptor values are zero.

`gpuSummary.passEncoders` groups stage distributions by kind/signature. A whole render span needs
both valid stages and runs from earliest start to latest end. Individual valid stages still count
when the other is missing. Compute/blit use encoder spans. Tile-GPU stage spans can overlap and are
not additive frame costs. `timedFrameCompletionIntervalMs` and `untimedFrameCompletionIntervalMs`
retain adjacent callback intervals within one recorded block, now requiring a complete frame pair.
Every block is timed, so the untimed distribution is empty.

## Workload and omitted experiments

Scene channels preserve every public ShadowPassDiagnostics boolean/numeric property, reason bits,
point-shadow categories, cascade counts, draw/span totals, every RenderFrameStats field and
UploadBytesPartitioned, scene draw/cull counts and caster counts. Each cascade has rigid span,
caster and scene span counts. Camera view/projection matrix entries, eye, forward and light direction
use exact signed float bit patterns in channels ending `.bits`. CameraOverride wins over Camera.
Light direction is Post.LightDirection. These are public inputs, not fitted shadow or jittered
matrices. `scene.EnableTiming` records overlay CPU timing work. Boolean values are 0 or 1. Suppressed
renders can retain previous last-frame diagnostic values.

Schema 14 still omits `passLabels`, `transfers`, `experiment`, `shaderGates`, their state
summaries and all early-submit schedule/tail-write experiment fields. Labels and transfers required
an observer, which would disable the released cascade discard. The discard experiment, shader gates
and early-submit schedule changed rendering and are inappropriate for confirming the released
renderer. Per-encoder records therefore have no label, directional-scope or store-resolution fields.
There is no stage-overlap attribution by named pass without those labels.

Headless fakes cover lifecycle, bounded buffers, missing evidence, allocation-free sampling,
annotations, split kinds/counts, pair busy/idle math, schema serialization and disabled activation.
Headless category tests also check three rotations, shared baseline pairs, terminal baseline hold,
zero-allocation scheduling, actual applied states, no-render callbacks, disabled activation and
restoration after completion, early close or failure. Real scene draw recording over the fake GPU
checks each category and both skinning paths, unchanged uploads and shadow draws, ungated effects
and restoration on the next baseline with no pass observer.
The gated native smoke checks real render, serial compute and blit timestamps. Existing allocation
facts check warmed frames with diagnostics inactive. These tests establish neither 144 FPS nor
physical presentation coverage at the owner's slow view. The parent owns DLL substitution and the
single human-launched confirmation capture. No windowed launch belongs to this archive task.
