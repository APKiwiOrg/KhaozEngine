# Renderer performance evidence

This is the living contract for the internal renderer baseline, offline summaries and comparisons.
[CROSS-PLATFORM.md](CROSS-PLATFORM.md#paid-hosted-renderer-performance-mode) owns hosted admission
and execution. The [cold publication design](design/RENDERER-COLD-PUBLICATION.md)
retains the design rationale. This document records no measured baseline or performance budget.

## Evidence and authorization

`tools/Renderer.Perf.Tests` is outside the solution. Its one opt-in performance fact is not an ordinary
headless test. Every native measurement or load run needs separate owner approval naming the workload,
leg, frame counts, duration and spending cap. A paid manifest's campaign cap is at most USD 18, the owner's
envelope for every paid pilot and full unit including attempts already billed, and each unit's own cost cap must
fit inside it. Implementation approval never authorizes a dispatch, pilot, retry, profiler capture or follow-up
measurement. Software Vulkan and WARP can establish
correctness only. They cannot establish a hardware floor or a T4 result.

Paid caps are fixed by preset and leg. Unit deadlines include startup, replay and publication. The
job cap also covers external setup and builds.

| Paid unit | Unit seconds | Steady frame milliseconds | Cold frame milliseconds | Job minutes | Maximum billed minutes | USD per minute | Maximum USD |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Linux Vulkan pilot | 600 | 10,000 | 120,000 | 12 | 15 | 0.052 | 0.780 |
| Windows D3D11 pilot | 600 | 10,000 | 120,000 | 20 | 23 | 0.102 | 2.346 |
| Linux Vulkan full | 1,500 | 10,000 | 120,000 | 35 | 38 | 0.052 | 1.976 |
| Windows D3D11 or Vulkan full | 1,500 | 10,000 | 120,000 | 50 | 53 | 0.102 | 5.406 |

The Windows full job cap is 50 minutes because external setup on that runner varies widely. In the
[locked Windows full unit](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40599810511) the GRID
driver step took 14 minutes 19 seconds against 2 minutes 51 seconds in the locked pilot, and the first
`nvidia-smi` calls of the clock lock step blocked about 10 minutes against under a second in the pilot.
Measurement began 31 minutes in and the former 35-minute cap cancelled the job. The unit deadline, frame
limits, rate and cleanup allowance are unchanged, so the larger cap only absorbs setup time.

The strict manifest requires both `limits.frameMilliseconds` and `limits.coldFrameMilliseconds`.
Only conditioning frame 0 uses the cold limit, for its immediate CPU interval, retained CPU interval
including completion and retained GPU duration. Every later conditioning frame, scene frame and
control frame keeps the steady limit, including the first warm-up frame of each fresh session.
Equality with a limit passes. The cold frame remains counted and reported inside the original unit
deadline and job cap. Batch drains and final readback remain separate from an individual frame.

The shared 120,000 ms cold bound is about 2.7 times the 44.9-second first CPU frame in the
[Windows T4 pilot](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40454067253). The already
valid Linux T4 pilot's cold frame took 9.42 seconds against its old 10-second limit. A thin margin
turns ordinary hosted-runner CPU variance into a paid refusal. The 120-second bound admits startup
work with margin while staying well inside the 600-second pilot and 1,500-second full unit deadlines.
Every other frame limit stays unchanged. All presets and execution classes use the same first-frame
rule. Fresh workloads share the unit's native device and compiler caches. The completed WARP and
Linux T4 pilots retained no later frame above 10 seconds, but these pilot observations do not
establish a timing bound for unmeasured full schedules or Windows Vulkan.

The modeled maximum billed minutes are the job cap plus an explicit three-minute cleanup allowance.
The maximum unit cost is those billed minutes times the leg rate. Windows pilot
[run 40435928569](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40435928569/job/121964605577)
hit its former 12-minute cap during the build but lasted 14 minutes 17 seconds. Its 2 minutes 17 seconds
overrun rounds up to three whole billed minutes. This reserves cleanup headroom without extending
frame or unit execution. It is not a measured bound on future cleanup. Review actual billing against
the approved unit and cumulative campaign envelope after every attempt.

The public deterministic recipes cover empty, village, outdoors, crowd and effects/UI scenes. They
use fixed simulation sequences and no consumer assets. The v1 settings encoding fixes uniform indirect
lighting and zero irradiance coefficients. The harness explicitly selects and validates both values.
Directional or nonzero input refuses construction instead of changing the frozen recipe identity. A full unit has 1,200 conditioning frames,
five scenes, both layouts, three iterations, 192 warm-up and 256 measured frames per block, and twelve
controls with 64 warm-up and 128 measured frames each. That is 16,944 retained frames. The smaller
pilot has its own frozen schedule and 1,260 frames. Warm-up and conditioning remain separate from
measured distributions. A three-frame batch has one completion drain and one harvest, including
the final short batch. No frame trimming, fastest-iteration selection or adaptive warm-up is allowed.

Shader identities hash exact UTF-8 source bytes, including composed programs and shared fragments.
Multiline C# literals retain their source file's checkout line endings when compiled. The shader
providers, ocean templates, temporal uniform declarations, sprite shaders and scaled-copy shaders
therefore use `text eol=lf` in `.gitattributes`, alongside embedded control shaders and approvals.
Normalizing only the hash would hide source changes and detach the identity from the compiler input.
New exact-byte providers must join the attributes and `RendererPerfIdentityCheckoutTests` inventory.
So must a test file whose multiline literals are frozen copies of those sources, or snippets compared
with, spliced into or hashed with them, such as `TemporalVisibilityFrozenD` and
`PointShadowFilterShaderTests`. Unpinned, Windows compares CRLF test text against LF shipped text.
Normalizing the comparison instead would leave the frozen copies no longer byte-exact.
That guard checks effective Git attributes, so an LF development checkout also catches a missing rule.
Recipe, settings, layout and control-definition objects use canonical JSON serialization instead of
hashing checkout whitespace. Frozen recipe files are parsed before construction comparison.

Cold preparation renders no hidden scene frame. The first counted conditioning frame uses driver
index 0 and scene frame index 1. Its active temporal history is deliberately invalid with
`FirstFrame`, since no previous frame exists. Only that first conditioning frame accepts this reset.
Temporal recipes require valid history on every later conditioning or measured frame. Each measured
scene block starts fresh at driver index -64 for the pilot or -192 for a full unit. These counted warm-up
frames establish history before measured index 0. Periodic recipe inputs wrap negative indices modulo
256, while the scene frame index and effect clock keep advancing through the block.

Every frame, including conditioning and the first warm-up frame of a fresh block, requires exactly one
logical production submission, zero logical `WaitForIdleCalls` and no device loss. Conditioning and
negative warm-up indices permit cold resource creation. Measured frames require zero logical resource
creations. Cold creation remains visible in raw counters and outside measured distributions. Native setup
submissions, completion backpressure and native allocations remain separate backend diagnostics.
The one batch completion drain and final validation readback occur outside frame brackets.

Construction capture records conditioning pipelines as `Cold` with their counted nonnegative frame
indices. Cold setup outside frame brackets has no frame index. Only the conditioning operation may
have counted `Cold` occurrences. Negative indices remain `Warmup`, and creation at measured indices
still refuses execution and compiler correspondence.

Unit startup admits every manifest control before the first scene session. Each control also admits
its whole storage-buffer bindings before creating control resources or recording a frame.
`GpuCapabilities.MaxStorageBufferRangeBytes` retains the selected Vulkan physical device's
`maxStorageBufferRange`. Direct3D 11 uses the smaller of its feature-level resource-size guarantee
from the selected adapter's dedicated video memory and its full raw-view limit. An unknown range
refuses compute controls. A compatibility refusal names the control, required bytes and admitted
device bytes. The buffers, schedule and complete output checks never shrink to fit a device.
Control 7 fixes a 128 MiB input and 128 MiB output on every leg, local size 256 and 1024 by 128 groups.
This uses the portable 134,217,728-byte storage range advertised by lavapipe and guaranteed by
Direct3D 11 for WARP. Each frame transforms all 33,554,432 words. Final validation checks every output
word once after completion. The large stream remains four times control 8's 32 MiB buffers. Its bandwidth numerator
is 256 MiB of reads plus writes per frame. A device below 128 MiB still refuses before the first session.

Cascade layouts, point-shadow demand and MRT changes retire outgoing graphs through the scene queue.
The workload starts each scene with `Scene3D.BeginFrame(frame.Submission)`, sharing the host completion
fence instead of adding the standalone retirement queue's empty submission. No hidden rendered priming
frame is added. These headless lifetime proofs do not establish native acceptance or authorize a pilot.

The outer GPU pair precedes the host clear and follows native command-list End finalization.
`TotalOnly` brackets the frame with two queries. `PerPass` adds frozen parent/child scopes and
reserved skip slots. Detailed markers may change synchronization or encoder boundaries. Its frame
total is diagnostic and never substitutes for a total-only frame result.

Ready, equal outer timestamps are retained as `BelowResolution`, with zero raw elapsed ticks,
the admitted positive `GpuPeriodNs` and a null duration. Only clear-only controls 1 (`empty-frame`),
2 (`empty-main-mrt`) and 3 (`empty-depth-only`) may complete with this state, on either backend.
Their repeated clears can produce equal timestamps even when the native pair correctly brackets
every clear and End tail. Admission requires every expected color/depth clear, one submission,
no draws, dispatches, uploads, copies, resolves, mip generation, resource creation or waits,
no pass markers or frame errors, and the unchanged admitted period. Final promotion also requires
the frozen control work digest and retained output-validation result. Conditioning, all scene outer
intervals and controls 4 through 12 still require positive Available timing. Existing per-pass
BelowResolution support is unchanged.

Raw samples and finite counts never shrink. Offline and published control summaries show separate
warm-up and measured BelowResolution counts and their reported tick periods. A measured unavailable
interval prevents the entire numerical timing distribution and achieved mean or rate. Available
counts are diagnostic, and positive samples never form a substitute subset. Equal query values do
not prove zero execution cost or redundant-clear elimination. Frequency-derived tick period is not
an execution-time upper bound, so no such bound or zero reference is invented. Invalid, disjoint,
pending, missing, nonmonotonic and failed timing keeps its existing refusal. Headless D3D11 traces
check both recording modes and all three controls, while positive scene and observable-work timing
guards continue to detect a zero outer bracket around real work.

The [consumer reference](USING-KHAOZENGINE.md#gpu-timestamps-without-waiting) owns the public observer
registry. `device.scene.begin`, `device.water.geometry`, `device.water.bathymetry` and
`device.water.prepare` are CPU-only leaves. Their counters are exclusive, their marker indices are zero,
and their elapsed ticks and GPU durations are null. Executed rows are Unsupported and skipped rows are
NotExecuted. The bathymetry leaf belongs to `PrepareFrame()` frames under `water`.
`device.water.prepare` is a root on every `PrepareFrame(width, height)` frame and is never skipped. These rows
remain in strict raw publication beside valid GPU timing without inventing GPU intervals.
Both warmed all-feature traces and cold MSAA/blob retirement traces are required correctness proofs.
The [integration verification record](verification/2026-10-21-renderer-performance-baseline.md) separates
those proofs from outstanding native acceptance and diagnostic collection work.

## Software correctness rehearsal

The `rehearsal`, `rehearsal-full` and `rehearsal-controls` presets in
`khaozengine.renderer-perf-unit/1` select an explicit correctness execution class and schedule. Neither the unit name nor approval purpose
selects a class. All use the production entrypoint, driver, concrete native session, scene/control
workloads, compiler replay, publication and directory certification. Performance presets retain their
independent hardware, limits, budget and promotion requirements.

| Schedule | Preset | Conditioning | Scenes and blocks | Controls | Total frames |
| --- | --- | --- | --- | --- | --- |
| Pilot | `rehearsal` | 300 S3 frames | S3 TotalOnly and PerPass, each 64 warm-up plus 128 measured | 1/4/5/7/9/11, each 32 plus 64 | 1,260 |
| Full | `rehearsal-full` | 1,200 S3 frames | All five scenes, the same 30 rotated/alternating blocks as paid full, each 192 warm-up plus 256 measured | All twelve, each 64 plus 128 | 16,944 |
| All controls | `rehearsal-controls` | 3 S3 frames | None | All twelve, each 64 warm-up plus 128 measured | 2,307 |

All three-frame completion, timestamp lifecycle, temporal history and frame guards remain required.
There is no hidden priming frame or semantic scene image oracle. Existing control output checks still
run. Full rehearsal adds the other scene families and control shapes without shortening their work.
The all-controls schedule opens one short S3 conditioning session followed by exactly twelve control
sessions, with no scene blocks. The shared compiler inventory and entrypoint require the leading
`conditioning:S3` operation. One counted three-frame batch retains that real operation and the
first-frame temporal history checks. With no scene blocks, the driver retains S3 recipe facts from
conditioning. This is startup correctness, not thermal conditioning. Each fresh control then warms
its own resources and timestamps for 64 counted frames. Three-frame completion retains 769 batches. Production cleanup, same-adapter compiler replay, publication and
directory certification remain required.

An approved successful all-controls rehearsal on each software leg precedes paid full units. The full
scene rehearsal remains selectable with its finite cap, but its complete schedule has not fit within
the hosted six-hour job. The all-controls schedule exercises the six additional full-only control
shapes without making full-scene completion a prerequisite or claiming scene coverage.

An explicit allowlist binds every schedule to `linux-vulkan` with `mesa-lavapipe-software` or
`windows-d3d11` with `microsoft-warp-software`. The pinned standard runners and bootstrap belong to
[CROSS-PLATFORM.md](CROSS-PLATFORM.md#free-software-renderer-rehearsal). Actual Vulkan CPU-class or Mesa
llvmpipe software identity is required on Linux. Windows requires the actual DXGI software adapter
(WARP), checked before D3D11's single cold timestamp calibration. The End-tail probe and single
resolve remain required. Hardware refuses rehearsal admission, while performance keeps requiring
non-software NVIDIA T4 identity. The shared timestamp ring still refuses software by default. Only
the rehearsal session explicitly opts in, and missing timestamp or completion capability still refuses.

| Rehearsal schedule | Unit seconds | Steady frame milliseconds | Cold frame milliseconds | Job minutes | Maximum billed minutes | Rate and maximum cost |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Pilot on either leg | 5,400 | 60,000 | 120,000 | 120 | 120 | USD 0 |
| Full on either leg | 19,800 | 60,000 | 120,000 | 350 | 350 | USD 0 |
| All controls on either leg | 5,400 | 60,000 | 120,000 | 120 | 120 | USD 0 |

Owner and campaign approval references remain required, with a zero-cost campaign instead of the
paid campaign cap. The full job cap stays inside the hosted six-hour limit. These conservative first
caps do not establish software fit or paid deadline acceptance. The all-controls class has its own
typed limits even where they equal pilot limits. Its cap allows more time than the observed pilot
control subset, without asserting that the added controls fit. Complete approved runs can inform a
separately reviewed tighter policy. Native blocking calls remain outside cooperative deadline checks.

Success requires completed returned final publication, zero failures, every frame guard passed,
Complete compiler collection and successful directory certification. Every rehearsal assessment is
non-promotable from creation, with fixed reason `SoftwareRehearsalNotPerformanceEvidence`. That must
be its only promotion reason on success. Five-percent clock/work drift is retained in window metrics
and the `rehearsal.clockOrWorkDrift.blocks` diagnostic count. It does not fail this correctness verdict.
The strict reader refuses any attempt to promote a rehearsal, and offline analysis and comparison
cannot use it as eligible performance evidence. The paid success verdict stays unchanged.

Authorization uses the same workload A, single-manifest authorization B, immutable ref, byte hash,
first-attempt rule and exact declared runtime/environment map described in
[CROSS-PLATFORM.md](CROSS-PLATFORM.md#free-software-renderer-rehearsal). A proposal or implementation
is not permission to run. Each leg and schedule needs its own exact approval.

A completed Linux run proves its approved production schedule, Vulkan shader/query/fence lifecycle,
compiler replay and Linux publication path on that software host. A completed Windows run covers its
Windows runtime, WARP identity, D3D11 query/fence/End-tail lifecycle, shader/control execution, DXBC
replay and Windows publication path. Neither certifies a future T4 image, GRID installation, NVIDIA
driver/compiler behavior, physical synchronization, clocks, paid billing or unchanged paid deadlines.
Neither establishes a hardware floor, reference rate or performance comparison. Different software
ordering can conceal physical synchronization defects, and a green run does not inject device loss.

## Cold backend evidence

Vulkan, D3D11 and Metal implement the optional pipeline/compiler and resource-memory contracts. The
[D3D11 package reference](../KhaozEngine.Gpu.D3D11/README.md#optional-compiler-and-memory-diagnostics)
owns its switches, exact artifact/state/configuration identities, reflection provenance, payload
accounting and asynchronous invocation API. Reflection and invocation collection have separate opt-ins.
Leave invocation queries out of both timed baseline layouts. Intermediate DXBC slots never supply
physical register, spill or achieved-occupancy evidence. D3D11 committed allocation, residency and
budget remain unavailable, independently of supported ordinary rendering and timestamps.

The [Metal package reference](../KhaozEngine.Gpu.Metal/README.md#optional-compiler-and-memory-diagnostics)
owns its exact source/state/configuration identities, bounded cold counter metadata, compute limits
and resource ownership. Metal exposes no native compiler artifact or physical register/spill/occupancy
evidence through this source path. Device-wide allocation snapshots retain unified-memory provenance
and stay separate from logical payload and ring/staging capacity. Residency and hard budget remain
unavailable. Headless injected tests establish neither native support nor GPU measurements.

The unit captures immutable factory inputs and every pipeline occurrence during the fixed workload.
After production cleanup it replays unique recipes on a separate instance configured for compiler
metadata and no runtime invocations. Production and replay must agree on source, layout, resolved
state, code generation and actual primary-backend artifacts. Requested and effective configurations
remain separate. Only the explicit diagnostic choices may differ. No extra frame or calibration is
part of collection. Current live resource counts accompany actual backend peak logical payload bytes.
Diagnostic memory does not increase the production peak.

Construction stores each distinct exact stage source and immutable descriptor once by content hash.
Its unchanged 32 MiB source and 16 MiB descriptor limits charge distinct content, including retained
unresolved descriptors and source hash keys. Every factory event and occurrence witness still counts
against 16 MiB of evidence, within the combined 64 MiB logical limit. This lets fresh operations
recreate identical pipelines across the full 43-operation schedule without exhausting the source cap.
Hash equality always requires value equality. No occurrence or source byte is pruned.

`khaozengine.renderer-compiler-inventory/2` carries a first-reference-ordered table of source hashes
and exact text. Shader stages reference that table, while all factory events reference canonical
shader/layout descriptors. Recipe identities retain their existing exact-value encoding. The strict
reader refuses inventory/1 without migration, changed hashes, unused or duplicate rows, foreign
references and noncanonical order. Live publication, offline verification and baseline publication
share this codec and its production-to-replay correspondence checks.

`GpuDiagnosticDigest` carries a nullable SHA-256, provenance, status and reason. Available identities
require a lowercase 64-digit hash of actual nonempty bytes. Unsupported or Invalid identities require
a null hash and an explicit cause. Metal retains MSL as source, with its native artifact Unsupported.
Stage identities remain available for independent aggregate checks. File checksums never substitute
for compiled-artifact digests. Complete optional Unsupported evidence can be verified. Invalid,
unattempted or incomplete compiler evidence cannot promote.

Headless capture, accounting and file tests establish implementation behavior only. Primary native
correspondence, real compiler payloads, unchanged-deadline acceptance, billed cost and the original
consumer acceptance remain separate owner-approved gates. #1547 remains open for those proofs.

## Raw artifact and strict reader

`khaozengine.renderer-perf-report/2` JSON is canonical. Report/1 is explicitly refused without migration. Keep its exact manifest, workflow and source
identities, immutable run link, TRX, logs and SHA-256 sidecars. A checked-in baseline summary points
to those immutable artifacts. It never overwrites an earlier baseline or checks in thousands of frames.

Use `RendererPerfReportJson.Parse` before any arithmetic. The decoder enforces the 256 MiB byte cap,
depth 32, required fields, finite values, exact counts, support semantics and publication rules.
`RendererPerfAnalysis.Read(bytes)` uses that reader directly. `Analyze(report, references)` validates
typed input by round-tripping the same codec. Both retain the complete decoded report as `Raw`.
There is no alternate tolerant decoder or frame sampling path. Byte/report analysis is descriptive
and explicitly unverified. It cannot produce promotion eligibility or regression verdicts.

`RendererPerfArtifactVerification.Verify(directory, reportName)` reopens registered regular files in
chunks no larger than 65,536 bytes. It checks safe relative paths, aliases, links/reparse points,
nonempty lengths, SHA-256, strict schema, execution identity, decoded counts and all construction,
read and result associations. Expected files come from producer registration, never enumeration. The existing fixed-cadence host telemetry producer also registers its exact JSONL output, without another sampler call.
`RendererPerfAnalysis.ReadDirectory` calls this same verifier. Only verification constructs the
immutable verified artifact. The control corpus contains exactly `chain.comp`, `stream.comp`,
`fma.comp`, `dependent.comp`, `fullscreen` and `depth-read`. Compute local sizes are 1, 256, 64
and 64. Both graphics pairs use zero. Every record retains its four shader hashes, positive store
count and nonnegative instruction counts.

The verified artifact retains actual handle identities and no-follow directory-entry
identities, refuses hard-link aliases, and rechecks all retained files and parent directories at the
final certification boundary. macOS arm64/x64 uses fstat/fstatat and no-follow openat. Linux x64
uses statx and no-follow openat. Windows uses disk-file IDs and reparse-point handles. Unavailable
identity APIs or unsupported platforms refuse. Advisory sharing locks are not identity evidence.
Before the first content byte is read, each retained regular-file handle must exclude compatible
writers through final validation and handle release. Windows requests GENERIC_READ with
FILE_SHARE_READ | FILE_SHARE_DELETE, excluding both existing and new data-write handles. Directory
and entry metadata handles retain their separate sharing policy. Unix acquires nonblocking
LOCK_EX | LOCK_NB on the same descriptor. Ordinary .NET writers participate with shared flock locks
on supported local filesystems. Shared reader locks would admit those writers. Contention,
unsupported facilities and acquisition failures refuse immediately, preserving the native cause,
without retries or a weaker fallback. The existing ownership closes every acquired handle on both
success and refusal, releasing its exclusion.

Unix verification supports local APFS/HFS on macOS arm64/x64 and ext2/3/4, XFS, Btrfs, tmpfs and
overlayfs on Linux x64, subject to successful handle-based filesystem queries and flock acquisition.
Other filesystems, including NFS/SMB/CIFS, refuse because the ordinary .NET writer-sharing protocol
cannot be established here. A disabled .NET file-locking configuration also refuses. Writers that
bypass or disable that advisory protocol are noncooperating. Their writes are outside this Unix
guarantee. Identity, alias and containment validation remain separate requirements.

After decoding and reconciliation, the verifier rereads every retained file handle as defence in
depth, comparing full SHA-256 and exact length with the bytes originally decoded. Offset reads bypass
FileStream's read buffer. Every read is bounded to 65,536 bytes with the same before/after deadline
and cancellation checks. Identity and containment are checked again afterward, with exclusion still
held for earlier files while later files are validated. Unavailable read/length/identity facilities
refuse. This finite guarantee ends when the reader releases its handles. It provides neither
post-verification write protection nor an atomic multi-file filesystem transaction.
Offline and workflow verification must repeat against received files. Verified summaries retain that association and their reference rates.
`RendererPerfComparison.Compare(verifiedBaseline, verifiedCandidate)` requires it for verdicts.

Ordinary local Release test setup runs `sh scripts/build-release-tests.sh KhaozEngine.slnx`, or passes
`KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj` for the affected assembly. This builds its
outside-solution process prerequisite before `dotnet test -c Release --no-build`. Full and selective
ordinary CI and the native GPU matrix use this same build owner, including native shard solutions
that select Render.Tests. Process tests refuse a missing executable.

The directory-verification mode of the outside-solution `tools/Renderer.Perf.Verify` console takes
exactly directory and report filename. Its `publish` mode is described under Publishing a baseline.
It performs no backend registration, device creation or measurement. Exit zero means directory
verification succeeded. The trusted workflow invokes its built DLL after independent execution
identity checks, then verifies received files again rather than accepting parsed JSON alone.

The root cap and depth remain 268,435,456 bytes and 32, including two 32-byte accounting slots.
Root plus new compiler-inventory/compiler-failure files must fit the same combined byte cap.
The concrete writer owns the terminal `publication.return` deadline/cancellation check after stamping,
inside its bounded failed-final correction and partial recovery. Unit consumes the actual failure
events once. A successful stamp cannot escape that correction owner through a later Unit check.
Precollection encodes the retained root, inventory and completion shape before optional replay.
The unchanged 16,777,216-byte completion reserve includes four option tables requiring up to
6,717,440 encoded bytes. Vulkan headroom A admits T=min(16384,floor((A-786432)/4096)) and
R=786432+4096*T, refusing A below 786432. D3D11 reserves 1,048,576 bytes. Each native reader receives
the exact frozen request, with one outstanding reservation and checked actual-byte commits. Native
reports are retained through refusal. No cap increase, frame pruning or native-field truncation is
an admission remedy. Live verification precedes completion sampling and the accounting stamp.
Cancellation, deadline and write failures preserve partial evidence and ordered original exceptions.

- `raw.format` must be `metric-vectors/2`. Shared metric descriptors retain name, unit, source,
  status and reason. A layout lists descriptor indices in order. Expand every referenced positive-zero
  run around the nonzero Available values. Unavailable descriptors consume no value and restore null.
  Negative zero keeps its explicit numeric slot and sign.
- Scalar metrics use `{schema, value}`. Pass tuples retain schema, nullable elapsed ticks and the
  complete scalar duration reference. Parent, occurrence, marker pair and `deviceOnly` come from
  the shared pass schema. CPU-only device rows keep null GPU durations and their support reasons.
- CPU interval columns retain an Int64 origin, schema indices and exact UInt64 offsets/durations.
  Reconstruct with checked integer arithmetic before converting durations to nanoseconds. Do not
  subtract two independently rounded floating-point absolute timestamps.
- Publication's fixed-width numeric slots contain ordinary legal JSON whitespace after stamping.
  Whitespace does not change accounting or the report format.
- Memory observations retain operation, stage, first frame, monotonic clock, source and failed-read
  reasons. `memory.peaks` references the first observation attaining the maximum. The last available
  snapshot is distinct from that peak. Never reuse an earlier sample for a failed observation.

Duplicate or foreign descriptors, indices, layouts, zero patterns and metric keys, unknown fields,
nonfinite values, overflowing ticks, inconsistent columns and unavailable metrics with numbers
are errors. Keep the error instead of repairing the input into a favorable result.

## Reproducing summaries

The offline types live in `KhaozEngine.Render.Tests/Gpu/RendererPerf`, not a shipped package API.
`RendererPerfAnalysis` owns arithmetic, `RendererPerfComparison` owns compatibility and deltas,
and `RendererPerfMarkdown` formats compact human output. None creates a device, reads a clock,
launches a workload or changes the raw artifact.

Within the test assembly, an offline caller can read an existing artifact and save the derived view:

```csharp
var summary = RendererPerfAnalysis.ReadDirectory(evidenceDirectory, reportName);
File.WriteAllText(markdownPath, RendererPerfAnalysis.Markdown(summary));
var comparison = RendererPerfComparison.Compare(baselineSummary, summary);
File.WriteAllText(comparisonPath, RendererPerfComparison.Markdown(comparison));
```

The publication sink's small status Markdown remains a receipt. The offline view is derived from the
final stamped JSON after publication. It never changes the unit deadline or rewrites its artifacts.

Each block retains its scene, layout and iteration. `Metrics` includes the outer `gpu.frame`,
`cpu.frame`, named CPU segments, raw counters, pass durations, GPU exclusive/unattributed time
and CPU partition gaps. Units and sources accompany every derived value. Unavailable inputs retain
their original descriptors in `Unavailable` and remain in `Raw`. A missing sample, incompatible unit
or mixed support state prevents a numerical distribution. Available-sample `Count` is diagnostic,
not permission to average a favorable subset. No unavailable metric is converted to zero.

For a complete ordered vector of N values, sort a copy for ranks and keep the original for windows.
Mean is the arithmetic mean. Median is the middle value for odd N, or the arithmetic mean of the
two middle values for even N. Nearest-rank p95 and p99 use one-based rank `ceil(p * N)`, clamped to
1 through N, for p equal to 0.95 or 0.99. Minimum and maximum are the endpoints. Standard deviation
is the population value `sqrt(sum((x - mean)^2) / N)`. It describes retained frames, not an estimated
standard error. First64 and Last64 use exactly the first and last 64 measured frames. Fewer than 64
frames leaves those windows unavailable.

For `[1, 2, 3, 10] ns`, mean is 4 ns, median is 2.5 ns, p95 and p99 are 10 ns, and population
standard deviation is `sqrt(12.5) ns`. For integers 1 through 20, p95 is 19 and p99 is 20.
The synthetic tests pin those independently calculated values.

Combined distributions pool measured frames only within the same scene and layout. They are
descriptive. `IterationMeans` retains each iteration mean in iteration order as a separate replicate.
The three iterations in one process describe repeatability inside one job. Correlated frames are
never independent experiments, and neither pooled p99 nor three means establish statistical
significance, fleet variance or a tail guarantee.

CPU duration is the exact integer tick difference divided by that sample's frequency, times 1e9.
Segments sharing a phase ID are summed within a frame, because the recorder pauses and resumes
exclusive segments. Their parents remain in `Raw`. CPU wall time is authoritative. The partition gap
is wall time minus all retained exclusive segments. Overlap or a negative gap invalidates the
partition. `cpu.unattributed` remains a named segment, not hidden time. GC-associated frame IDs
come from positive `gc.gen*.collections` counters. Allocation scopes overlap and are never added.

GPU partition arithmetic subtracts only immediate children from their parent and only GPU roots
from the outer duration. A skipped `NotExecuted` row contributes no executed interval to this
partition calculation, while its duration metric stays NotExecuted. CPU-only device rows contribute
no GPU interval. Missing parents, crossing or overlapping sibling marker ranges and negative gaps
invalidate the partition. Unsupported child timing leaves the gap unavailable. Never sum nested
scope durations to manufacture a frame total. `logical.unattributed.*` counters remain visible even
when timestamp partitions are complete.

## Drift, perturbation and validity

Clock/work drift uses the existing protocol rule. Flag a change above 5 percent between first64 and
last64, or between corresponding iteration means and the first retained iteration mean. Such
evidence cannot be promoted as a stable baseline. Keep slow and GC frames.

Paid T4 units lock the graphics clock at 1,095 MHz on every preset and leg. The unlocked
[Linux full unit](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40572172000) and
[Windows full unit](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40572176659) completed every
conditioning frame, block and control without frame errors, then refused promotion with
`ClockOrWorkDrift`. Their telemetry alternated between idle clocks and software power capping at
70 W. Loaded graphics clocks reached about 1,290 to 1,455 MHz at the cap, while lighter work boosted
higher or dropped to idle. That motivated a lock below the loaded range instead of variable boost.
The [locked Linux full unit](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/40599806485) then
applied a 1,200 MHz lock, completed all 30 blocks and 12 controls with a worst drift of 1.03 percent
and no `ClockOrWorkDrift`, and refused only `GpuClockNotLocked`. One of its 90 samples reported
software power cap (`0x4`) at a held 1,200 MHz while averaging 54.4 W. The lock moved to 1,095 MHz
for more power headroom below the 70 W cap. It is a T4 graphics clock already observed in this
program's telemetry and a multiple of 15 MHz. The throttle policy below is unchanged and still
refuses `0x4` at the locked clock. No measured unit has held 1,095 MHz yet.

`GpuClockNotLocked` is independent of the unchanged drift rule. Every retained conditioning, scene
and control batch must carry the identified host observation from the fixed sampler. Any observed
graphics clock other than the approved value, unavailable clock or throttle mask, missing observation,
or unknown throttle bit refuses promotion. The sampler also retains current memory and maximum
graphics clocks. The deprecated requested application-clock field remains evidence even when the
driver reports it unsupported. It is not the lock readback.

The [NVIDIA throttle bitmask](https://docs.nvidia.com/deploy/archive/R550/nvml-api/group__nvmlClocksEventReasons.html)
allows no limiter (`0x0`), idle (`0x1`) and application-clock setting (`0x2`), including their combination,
only while the observed graphics clock is exactly the lock. The latter flags can describe the idle
sampler boundary or an intentional clock setting. Power cap (`0x4`), hardware slowdown (`0x8`),
software thermal slowdown (`0x20`), hardware thermal slowdown (`0x40`) and power brake (`0x80`)
refuse even at the requested clock. Sync boost (`0x10`), display-clock setting (`0x100`) and unknown
bits also refuse because another policy could limit the clock. A successful lock command alone
never certifies a held clock.

The strict report retains `gpuClock` with the approved clock, verification state, distinct sample
count and refusal cause. Live assessment, strict decoding, directory verification and offline analysis
recompute the result from retained observations. Published summaries retain it beside the immutable
manifest. Software rehearsals record `NotApplicable` and never acquire the paid lock requirement.

`Perturbations[scene/iteration]` retains the signed per-pass outer mean minus the matching total-only
outer mean as `DifferenceNs`, and `Percent` is `100 * difference / totalOnlyMean`. `ThresholdNs` is
the larger of 5 percent of the total-only mean or 100,000 ns (0.10 ms). `AboveThreshold` flags a
difference magnitude strictly greater than that limit. Equality does not flag. This is the approved
instrumentation sensitivity policy, not a regression budget or proof of marker causation.
A nonzero difference also retains the informational `MarkerPerturbationObserved` label.
For a zero baseline the percentage is unavailable, while the absolute difference and 100,000 ns
threshold remain defined. An unavailable or invalid baseline or detailed mean keeps its reason and
remains unavailable or invalid, with no threshold decision. Keep both layouts and their raw samples. No guessed
marker tax is subtracted from pass durations.

Promotion eligibility requires directory verification, the original valid assessment, completed returned final publication,
and no newly detected drift or invalid partition. Optional unsupported counters and incomplete work
models do not become measurements and do not independently invalidate sound frame timing.
Failed, refused or partial publication keeps original failure and cleanup errors. It cannot supply
promotable evidence or numerical comparison deltas. The existing partial-write accounting gap
[#1547](https://github.com/APKiwiOrg/KhaozEngine/issues/1547) remains a proof limit. Offline analysis
does not infer native cost or certify final sidecar availability from JSON alone.

## Work models and reference gaps

Measured duration, theoretical lower bound, calibrated estimate and modeled work are separate
quantities. Reuse `RendererPerfFloorModel` without clamping a negative excess or interpreting
resource size as exact off-chip traffic. For defensible work, its theoretical component is
`max(minBytes / peakBandwidth, minFp32Ops / peakFp32Rate) * 1e9 ns`. This is a loose lower bound.
The calibrated estimate uses matched measured throughput and explicit launch/dependency assumptions.
The default `MayOverlap` policy takes the maximum of launch and throughput terms. Only an explicitly
disjoint serial model can add them. Reference ranges, dominant resource and missing evidence survive.

Offline `references` is an optional dictionary keyed `scene/pass/occurrence`. Each value is the
existing `RendererPerfReferenceRates`, with rates in bytes/s, operations/s and pixels/s, launch in ns,
instruction class and an explicit source. Preserve these exact supplied inputs with the summary
when sharing it. `References` retains them. The caller must establish state, formats, sample count,
instruction class, completion policy, clock identity and compiler-work retention before declaring a
control matched. The offline reader does not infer such a match from a control ID or a T4 name.
No reference inputs means ModelIncomplete, never an assumed device peak or a favorable gap.
Invalid rate or model arguments retain Invalid gap metrics, the supplied source and the original
validation reason. They never fall back to another control or a zero reference.

Use retained `assessment.workModels` for an explicitly supplied pass model. Otherwise regenerate
the recipe-owned model from each measured frame's retained logical facts with `ForFrame`. Do not
reuse only the first frame's model when execution or work changes. Every observed pass gets a gap
assessment. `gap.<id>.<occurrence>.*` distributions retain theoretical, calibrated, absolute excess
and ratio support states. `GapEvidence` retains each frame's full assessment, uncertainty, dominant
resource and reasons. A duration below a claimed theoretical bound flags an invalid model.
An incomplete or invalid reference cannot create an available excess, ratio or favorable comparison.
Partial theoretical or calibrated components remain diagnostic. Their comparison deltas also require
a complete valid reference model, even when both components carry the same source label.
Every derived gap metric retains the reference source, instruction class, overlap policy and the
used work components' units, sources and provenance, plus serial dependency identities. These
identities must match for dependent gap deltas, including combined rows. Different numerical rates
or work magnitudes from matched evidence are allowed. Unmatched provenance is explained and refuses
the gap deltas, while independent frame and counter deltas can remain available.

Control summaries keep outer duration distributions separate from scene frames. Achieved rates use
the existing `AchievedRates` checks for complete samples, exact work identity and validated output.
Unsupported or unmatched controls retain a reason rather than supplying a fabricated rate.
Measured control CPU partitions follow the same validity rule as scene partitions. Overlapping
segments retain their diagnostic phase values and context, revoke promotion and refuse numerical
comparison deltas.

## Comparing evidence

`RendererPerfComparison.Compare(baseline, candidate)` accepts two reports. Its summary overload also
accepts retained explicit reference inputs. Both return identity differences, compatible rows,
candidate-minus-baseline deltas and refusal reasons. No universal percentage budget or pass/fail
regression verdict is assigned.
The summary overload recomputes from its raw report and reference inputs. Cached derived statistics
or eligibility flags cannot override a changed or failed publication.
Rejected typed input returns the original protocol refusal with no identity differences or numerical
rows. It is never serialized again to construct diagnostic differences.

Compare only matching scene/profile/control versions, recipe/settings/geometry/asset and sequence
identity, dimensions/formats/routes, layout and pass ownership, native backend/adapter/driver,
OS/image/CPU/runtime/GC/environment, recording/validation/capture settings, timestamp capability,
completion policy and frozen pacing/counts. Different source, harness and shader hashes can identify
a legitimate candidate. They are reported as informational differences. Run IDs, authorization,
manifest and workflow SHAs are also retained and explained, rather than required to be identical.
A changed control work digest is incompatible even if its version was not changed.

Unlike block keys remain separate rows. An incompatible pair has unavailable deltas with a reason.
Missing metrics, mismatched units or provenance, partial publication and invalid reference evidence
never win against an available baseline. For compatible available metrics, report absolute changes
for mean, median, p95 and p99, and mean percentage `100 * (candidate - baseline) / baseline`.
A zero baseline has an undefined percentage, not zero percent. Combined rows remain descriptive
and per-iteration rows stay distinct. Keep total-only frame comparisons, per-pass diagnostic deltas,
control deltas and reference-gap deltas in their respective rows.

Use `RendererPerfAnalysis.Markdown(summary)` and `RendererPerfComparison.Markdown(comparison)` for
compact invariant-culture output. JSON and the retained inputs remain the reproduction authority.
Summary Markdown revalidates the retained raw report and references before computing eligibility,
reasons and distributions. Cached success cannot override failed raw evidence at publication.
A changed recipe, driver or protocol starts a new baseline series with the prior result retained.


## Publishing a baseline

Build the offline verifier through the Release setup above, then publish a received full pair with
one command. This reads files only, registers no GPU provider and starts no measurement:

```sh
dotnet tools/Renderer.Perf.Verify/bin/Release/net10.0/Renderer.Perf.Verify.dll publish --request request.json
```

The request schema is `khaozengine.renderer-baseline-request/1`. JSON is capped at 1 MiB and depth
32. Duplicate or unknown fields, missing required values, null array entries and nonfinite numbers
refuse. It requires `outputDirectory` and
exactly two `legs`, one `linux-vulkan` and one `windows-d3d11`. Each leg supplies `leg`, `directory`,
`reportName`, `runUrl`, integer `billedMinutes`, `manifestSha256`, `workloadSha`, `workflowSha` and
`harnessSha256`. The hashes name the expected approved evidence, independently of the received report.
The run URL must name that report's first attempt in `APKiwiOrg/KhaozEngine`, including
`/actions/runs/<runId>/attempts/1`. `reportName` is the unit ID plus `.json`.
Paths in a request file resolve relative to that file. Billing is an explicit operator-supplied
actual, never inferred from elapsed time or estimated cost. It must be positive and within the
approved billed-minute cap. The publisher records the supplied minutes without claiming to query
GitHub billing.

An optional `references` object per leg uses the exact `scene/pass/occurrence` keys and
`RendererPerfReferenceRates` fields described under Work models and reference gaps. Omit it when no
matched evidence exists. Each rate object supplies all eight constructor fields, using null for unavailable numeric inputs.
The summary retains all supplied reference rates and their sources.

The command calls the real registered-file directory verifier for both legs before creating output.
It then reruns analysis and admission. Rehearsals, pilots, failed promotion, partial or incomplete
publication, missing or changed sidecars, foreign hashes, mismatched native/compiler backends and
unmatched run links refuse. Both legs must share workload and baseline source identities. Each leg retains its independently
pinned workflow and harness identity.
They remain separate series and acquire no cross-backend regression deltas. Refusals have typed
codes printed by the console. Verification failures retain their original exception as the cause.
The codes are `Request`, `EvidenceInvalid`, `Rehearsal`, `NotFull`, `Identity`, `Promotion`, `Billing`,
`Pair`, `Exists` and `Write`.

The output format is `khaozengine.renderer-baseline/1`. Each leg writes
`<leg>-<unitId>.md`, and the pair writes `index-<linuxUnitId>-<windowsUnitId>.md`. The summary records
its immutable run and attempt, verified report digest, manifest, workflow, source and harness hashes,
runtime and native identity, full declared configuration and compiler configurations. It lists
combined total-only scene distributions, each iteration's mean, detailed pass and gap assessments,
controls and instrumentation perturbation. ModelIncomplete, Unsupported, Invalid and NotExecuted
remain visible with their reasons. Only available excess distributions enter the largest-excess
ranking. Investigation suggestions are leads, not established causes. If none is available, the
summary says so and requests matched reference evidence.

Three iterations establish within-job repeatability only. The summary retains that limit, the
partial-write accounting proof limit in [#1547](https://github.com/APKiwiOrg/KhaozEngine/issues/1547),
and the separate boundaries for optimization, consumer fit, Windows Vulkan and release. Successful
publication does not close any of those boundaries.

Output is UTF-8 without a BOM, with LF line endings, invariant numbers, ordinal ordering and sorted
JSON object keys. No publication time or input/output directory path enters the summary. Reversing
the request's leg or reference-key ordering produces the same bytes. Every output uses exclusive
creation. An existing summary or pair index refuses before writes, even for identical inputs.
Keep earlier files and publish new units under new names. An I/O failure can leave partial output,
which remains evidence of that failed publication rather than permission to overwrite it.

Store reviewed summaries under `docs/performance/renderer/` with the original received artifacts
retained in their workflow run. The publisher does not commit files or dispatch follow-up work.
No measured baseline is checked in until an eligible full pair exists.
