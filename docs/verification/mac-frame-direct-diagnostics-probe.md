# Mac frame direct diagnostics probe

Private diagnostic archive on `fix/mac-frame-direct-diagnostics-probe`, based on
`17ed102da778386787140a8837d2c6ac8063de70`. NEVER merge this branch into `main`.
It is not a release, an engine capability adoption or a performance change.

## Use

The parent copies the exact same built `KhaozEngine.Windowing.dll` into both existing private
control and candidate clients. Keep their other binaries and settings under the parent's
comparison manifest. The recorder does not collect graphics settings, environment dumps,
tokens, source hashes or client configuration. The parent supplies those comparison facts.

Create `/tmp/grimhollow-mac-frame-performance` before launching. Choose a different, nonexistent
absolute result filename for each process and each run. Prefix the parent's normal, human-run
client boot command with this variable, substituting the chosen filename:

```sh
KE_FRAME_DIAGNOSTICS_PATH=/tmp/grimhollow-mac-frame-performance/control-01.json
```

This line shows the variable assignment, not a client launch command. Keep it on the same line as
the parent's actual command. The agent does not launch, attach to, inject input into or automate
either client. With the variable absent or empty the hook is one null conditional. Its arguments,
including the clock read and counter snapshot, are not evaluated.

After the human launches and positions the client, let it settle and keep it foreground at stable
size. The parent creates its trigger from a control terminal that leaves client focus unchanged:

```sh
touch /tmp/grimhollow-mac-frame-performance/control-01.json.start
```

Trigger contents do not matter. Polling occurs at most twice a second while waiting. The recorder
deletes the trigger and arms on that callback, then takes a baseline at the FOLLOWING render
boundary. Trigger I/O and the initial partial interval are excluded. No file polling, file writes,
formatting, dictionaries or allocations occur while sampling. Storage is one fixed 4096-sample
array allocated before waiting. Each sample consists of bounded value copies and live counters.

Sampling ends at the first boundary at least 10 monotonic seconds after the baseline, or when
4096 full intervals have been stored. Very high cadence can exhaust capacity before 10 seconds.
`stopReason` distinguishes `duration` from `capacity`. The final boundary belongs to the result.
The next frame continues normally while a background task computes and publishes the JSON once.

Publication writes a unique sibling temporary file with `CreateNew`, closes and flushes it, then
moves it to the result path without overwrite. An existing result is refused at startup, and a
result created during sampling is also preserved. A normal loop exit joins an already started
writer. Closing while armed or sampling logs failure and creates no completed result. Waiting
without a trigger creates no result. Abrupt process termination cannot guarantee publication or
temporary-file cleanup. A completed recorder never restarts, even if another trigger appears.

A concise `KE frame diagnostics complete.` or `KE frame diagnostics failed.` line is printed
outside measurement. Initialization, trigger and publication errors disable the probe or stop it
with explicit failure output, without crashing the game. Failure messages name the stage and
exception type. They do not dump exception text or configuration. A result path must have an
existing parent directory. Use a local filesystem for the sibling-file atomic publication contract.

## Schema 1 and interpretation

The stable camelCase JSON has these top-level properties:

| Property | Meaning |
| --- | --- |
| `schemaVersion` | `1` |
| `processId` | Client PID |
| `appAssembly` | Entry assembly full name, or `unknown` if unavailable |
| `engineAssembly` | Windowing assembly full name and module version ID |
| `stopwatchFrequency` | Monotonic timestamp ticks per second |
| `durationSeconds`, `sampleCount`, `stopReason` | Measured duration, full interval count and termination limit |
| `baseline`, `end` | Timestamp, boundary window state and cumulative device counters |
| `samples` | Full boundary-to-boundary intervals in order |
| `summary` | Cadence statistics, comparison validity, caps and aligned counter deltas |

Each sample carries `startTimestamp`, `endTimestamp`, `monotonicIntervalSeconds`, the original
unclamped `loopDeltaSeconds`, `completedFrameWindow`, `boundaryWindow` and cumulative `counters`.
Timestamps are Stopwatch ticks, without wall-clock or native GPU timestamps. The first sample
starts at the baseline. Its end counters include the callback that ran AFTER the baseline,
including prepare, update, render, UI, capture, submit and present. Later samples use the same
pairing. `completedFrameWindow` describes the state latched at that callback's opening boundary.
`boundaryWindow` describes the next callback about to begin. These counters come from the live
AppWindow device, never an offscreen SnapshotTool.

Window state includes focus, minimized and render-suppressed flags, framebuffer width and height,
resolved effective base cap Hz, and vsync intent. The hook reads the existing latched fields and
uses the same background-throttle policy as the loop. No setting or pacing behavior is changed.
Boundary observations cannot prove that focus or size did not change and change back between
callbacks. `allForegroundRenderedStableSize` conservatively requires every observed endpoint,
including baseline and end, to be focused, non-minimized, rendered, positive sized and the same
size. `cadenceCapped` means at least one boundary had a positive base cap or vsync enabled. It is
not evidence that a vsync implementation actually paced the CPU. Background policy caps are
visible through the focus and suppression flags, which invalidate the foreground comparison.

The summary uses monotonic intervals as primary full-client cadence. FPS is interval count
divided by summed full interval seconds, never the average of reciprocals. Frame times are in
milliseconds. Median averages the two central values for an even count. P95 and P99 use the
nearest-rank definition. `shareWithin144HzBudget` uses the exact `1000 / 144` ms threshold
(approximately 6.944444 ms), inclusively. Raw loop deltas remain available for cross-checking.

`bufferCapacityReached` reports whether the fixed buffer filled. `capacityLimited` is true only
when capacity stopped the recorder before 10 seconds. A duration boundary coinciding with a full
buffer reports `stopReason=duration`, `bufferCapacityReached=true` and `capacityLimited=false`.

Device counters are cumulative. Subtract adjacent snapshots, beginning with `baseline`, for exact
aligned interval deltas. The summary's `counterDelta` subtracts baseline from end and includes
total and per-interval acquire wait, backpressure stall and drain counts and milliseconds. The
per-interval denominator is `sampleCount`. The separate `framesBegun` delta is retained because
prepare work can open additional device frames, and suppressed callbacks can open none.
`offTimelineDeferred` is a total delta. `offTimelineOutstandingChange` is a gauge change.
If ANY boundary has `HasValue=false`, `counterDelta` is null, rather than invented support or
zero waits. Raw counter snapshots keep their `hasValue` flag.

These GPU counter durations are CPU waiting durations, not GPU execution times. This recorder
measures render-loop cadence, not display presentation. It adds no native labels, capture taps,
GPU readbacks or drains, and does not change rendering, shaders, GC, settings, input or pacing.

## Verification

All local builds and CPU tests run through `build-slot`. No clients or GPU tests are used.
Scratch logs and TRX files live only under `/tmp/khaozengine-mac-frame-performance`.

The initial test-first run failed compilation with four missing-type errors and exit 1.
After adding inert internal type shapes, runtime RED had 16 failed, 0 passed, 0 skipped, exit 1.
The implemented recorder passed the same 16 focused tests in Release, exit 0. Expanded coverage
distinguishes tail percentiles and unsupported counters between supported endpoints. An additional
warmed `AllocationRegion` test caught a captured-lambda allocation, failing with 512 bytes across
16 sampling boundaries, exit 1. Dispatch now uses a method group only after sampling stops.
The allocation test then passed with zero measured-thread bytes. The Windowing test project imports
the existing shared allocation props and defines the required serial `AllocSensitive` collection.
The final focused run passed 22 tests, 0 failed, 0 skipped, exit 0. That includes 19 recorder tests
and three shared allocation isolation contract tests. Tests use explicit timestamps and join
publication tasks, without sleeps, retry loops or load tests. Coverage includes
trigger/partial exclusion, exact duration boundary, capacity, counter alignment and subtraction,
unsupported counters, focus/minimize/resize invalidation, caps, summary math, no-overwrite races,
normal disposal joining publication and early-close refusal.

The focused command is:

```sh
build-slot --label mac-frame-diagnostics-tests -- dotnet test KhaozEngine.Windowing.Tests/KhaozEngine.Windowing.Tests.csproj -c Release --filter 'FullyQualifiedName~FrameDiagnosticsRecorderTests|FullyQualifiedName~AllocationIsolationContractTests'
```

Whole-tree dash, prose, file-size, instruction-size and doc-version guards passed, as did
`git diff --cached --check`. No full suite, GPU test, package or release command was run.

The final Windowing Release build passed with 0 warnings, 0 errors and exit 0:

```sh
build-slot --label mac-frame-diagnostics-windowing-verified -- dotnet build KhaozEngine.Windowing/KhaozEngine.Windowing.csproj -c Release
```

The parent independently reviews the commit and DLL hash, copies identical bytes into both private
clients, and owns the bounded human-run measurements. The diagnostic commit must remain an archive.
