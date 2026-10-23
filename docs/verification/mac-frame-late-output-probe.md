# Private Mac late-output diagnostic archive

NEVER MERGE this private diagnostic archive into `main`. Branch
`fix/mac-frame-late-output-probe` starts at `b5c03b1ff7f54e2cb7475c95819a442847d13f93`,
which supplies the approved renderer-owned swapchain initialization option. Only recorder commit
`bb71187703ab913c5c24c0900070ac8ea2d24416` was cherry-picked from the earlier direct diagnostics
archive. Its ancestors were not cherry-picked. This is not a release or production adoption.

## Use

Both runs use the same already-built Grimhollow Debug client on engine
`17ed102da778386787140a8837d2c6ac8063de70`, retaining sort and deferred acquisition in both.
The parent supplies one built `KhaozEngine.Windowing.dll` from this archive for both modes.
Keep the other binaries and settings under the parent's comparison manifest.
The recorder does not collect graphics settings, environment dumps,
tokens, source hashes or client configuration. The parent supplies those comparison facts.

The private environment override `KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN` is read
exactly once at the start of `AppWindow.Run`, after the already-built `GameApp` construction and before callback
registration or recorder creation. Missing preserves the public `RendererInitializesSwapchain`
property. Exact `0` sets it false, exact `1` sets it true. Every other supplied value, including an
empty string, whitespace and `true` or `false`, throws `InvalidOperationException` naming the
variable and accepted digits before recording can start. This configuration failure is deliberately
not swallowed by the recorder's fail-soft initialization. The override applies even if recording
is disabled. No environment variable is read per frame and no new public API is introduced.

For the A/B, the parent prefixes its normal human-run launch commands with `0` for control and
`1` for candidate. These lines show assignments only, not client launch commands:

```sh
KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN=0
KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN=1
```

The mode changes only host output initialization. `FramePhases` forwarding remains intact, and
the renderer remains responsible for fully initializing output when the mode is true.

Create `/tmp/grimhollow-mac-frame-performance` before launching. Choose a different, nonexistent
absolute result filename for each process and each run. Prefix the parent's normal, human-run
client boot command with this variable, substituting the chosen filename:

```sh
KE_FRAME_DIAGNOSTICS_PATH=/tmp/grimhollow-mac-frame-performance/control-01.json
```

This line shows the variable assignment, not a client launch command. Keep it on the same line as
the parent's actual command. The agent does not launch, attach to, inject input into or automate
either client. With the path variable absent or empty the hook is one null conditional. Its arguments,
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
resolved effective base cap Hz, vsync intent, and the actual public
`AppWindow.RendererInitializesSwapchain` bool as `rendererInitializesSwapchain`. The bool is copied
at every callback observation, including baseline and end, and appears in both per-sample window
states. It is not derived from the environment string. All previous schema 1 fields and clock
alignment are retained with this additive field. The hook reads the existing latched fields and
uses the same background-throttle policy as the loop. No pacing behavior is changed.
Boundary observations cannot prove that focus, size or mode did not change and change back between
callbacks. `allForegroundRenderedStableSize` conservatively requires every observed endpoint,
including baseline and end, to be focused, non-minimized, rendered, positive sized and the same
size and swapchain initialization mode as baseline. An observed mode transition invalidates the
comparison even if it returns to the original mode before capture ends. The existing validity
field keeps its name for compatibility. `cadenceCapped` means at least one boundary had a positive base cap or vsync enabled. It is
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
GPU readbacks or drains. The explicit private mode override is the only new rendering choice.
Shaders, GC, input and pacing remain unchanged.

## Verification

All local builds and CPU tests run through `build-slot`. No clients or GPU tests are used.
Scratch logs and TRX files live only under `/tmp/khaozengine-mac-frame-performance`.

New override and mode-stability tests were written before implementation. Inert parser and state
shapes allow runtime RED against missing override behavior and missing mode comparison. Tests use
explicit timestamps and join publication tasks, without sleeps, retry loops or load tests.
The inherited contract coverage retains trigger/partial exclusion, exact duration boundary,
capacity, counter alignment and subtraction, unsupported counters, focus/minimize/resize
invalidation, caps, summary math, no-overwrite races, normal disposal and early-close refusal.
The warmed `AllocationRegion` assertion covers both swapchain initialization modes. The project
retains the shared allocation props and nonparallel `AllocSensitive` collection.

The focused command is:

```sh
build-slot --label late-output-probe-tests -- dotnet test KhaozEngine.Windowing.Tests/KhaozEngine.Windowing.Tests.csproj -c Release --filter FullyQualifiedName~FrameDiagnostics --logger 'trx;LogFileName=late-output-probe-green.trx' --results-directory /tmp/khaozengine-mac-frame-performance
```

The focused Windowing Release build command is:

```sh
build-slot --label late-output-probe-build -- dotnet build KhaozEngine.Windowing/KhaozEngine.Windowing.csproj -c Release
```

`AppWindow.Run` requires a native window, so its environment-read timing and unchanged
`FramePhases.Run(..., RendererInitializesSwapchain)` forwarding are checked by source inspection.
The CPU tests prove the pure parser and recorder behavior, not window startup, GPU correctness or
full-client performance. No full suite, GPU test, package, client or release command is authorized.
The parent independently reviews the commit and DLL hash, supplies identical bytes for both modes,
and owns the bounded human-run measurements. NEVER MERGE this diagnostic archive.
