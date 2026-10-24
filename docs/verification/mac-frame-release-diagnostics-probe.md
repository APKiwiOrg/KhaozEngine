# Private release-matched buffered frame recorder

NEVER MERGE this diagnostic archive into `main`. Branch
`probe/mac-frame-release-diagnostics` starts at released `v21.13.0`, commit
`cc31c09f37a74332b716ec81b29265a755bcbb04`. No version, dependency or package bytes are
changed by this archive. Only its private Windowing DLL is a candidate for substitution.

## Source and boundaries

The recorder and report come from approved late-output archive
`5e2d295992c62303b087f7fb137cdf22e0559686`, whose earlier direct recorder archive is
`bb71187703ab913c5c24c0900070ac8ea2d24416`. Recorder code is copied verbatim. The report
changes only the CPU-duration comment. The recorder tests are copied with only their temporary
directory redirected beneath the task scratch root. The shared allocation import and
`AllocSensitive` collection are preserved.

The two AppWindow additions create and dispose the recorder around the existing loop and observe
the opening callback boundary before any delta clamp, input, prepare or rendering work. The hook
records raw loop delta, monotonic time, device counters and the actual
`RendererInitializesSwapchain` property. Production `FramePhases` and its forwarding call are
unchanged. No `FrameDiagnosticsOptions` or private mode override is included. The game must set
the production option itself. The recorder never forces it.

`KE_FRAME_DIAGNOSTICS_PATH` is absent or empty to disable recording. An enabled run requires an
absolute, nonexistent result path in an existing directory. The sibling `<result>.start` trigger
is polled at most twice per second. Consuming it arms the recorder. The following boundary is the
baseline, excluding trigger I/O and its partial interval. Storage is allocated before measurement.
Sampling ends at the first boundary at least 10 seconds after baseline or after 4096 intervals.
Only then does one background writer serialize, flush and atomically move a sibling temporary
file without overwriting a result. Normal disposal joins publication. Early close refuses a
completed result. Warmed sampling allocates nothing and performs no trigger or publication I/O.

The unchanged schema validates observed focus, minimize, render suppression, positive stable
framebuffer size and actual mode. Any unsupported counter boundary makes the summary delta null.
Boundary observations cannot exclude changes that reverse between callbacks. Cap and vsync changes
are not invalidated by the summary. The parent must require cap 0 and vsync true at baseline, end
and both sample window states. `cadenceCapped=true` is expected with vsync and proves no physical
display rate. Counter durations are cumulative CPU-side API durations, including full acquisition
calls, not GPU execution or physical presentation timings.

## Bounded verification

Focused discovery, test results, source equivalence, guard exits and the final committed-source
Windowing build are recorded under
`/tmp/khaozengine-mac-frame-performance/release-diagnostics`. The existing
`base-fast-forward.log` is preserved. Verification uses `build-slot`, one focused recorder and
allocation-isolation test execution, owned-file formatting and repository guards. No new behavior
or meaningful contract adjustment required a new regression. This is a source port, not a claim
of a new red/green reproduction.

Actual discovery found 29 expanded cases, 26 recorder cases and 3 allocation-isolation cases.
The single focused execution passed all 29, with zero failed, skipped or not-executed cases.
Both warmed zero-allocation modes passed. Owned-file `dotnet format` verification and the
whole-tree dash, prose, file-size, instruction-budget and doc-version guards exited 0.
Staged checks also cover every newly added file. Tested code and project bytes are unchanged
between the pre-commit run and the commit. The final DLL is built after that commit so its
source identity belongs to the returned archive commit.

The focused filter is
`FullyQualifiedName~FrameDiagnosticsRecorderTests|FullyQualifiedName~AllocationIsolationContractTests`.
The Release build targets `KhaozEngine.Windowing/KhaozEngine.Windowing.csproj`. Assembly metadata
must define Windowing `21.13.0.0` and reference released engine dependencies at `21.13.0.0`.
No published packages are repacked and no dependency build output is selected for client copying.

AppWindow hook placement is verified by source inspection. CPU tests do not prove native startup,
window focus, effective graphics settings, GPU correctness, live capture correctness or FPS benefit.
No client, automation, GPU work, profiling, full suite, stress test, push, merge, fetch or tag is
part of this port. The parent owns independent review, private archive publication, candidate
preparation from published bytes and the human-run comparison. NEVER MERGE this archive.
