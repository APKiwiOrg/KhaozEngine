# Renderer performance evidence

This is the living contract for the internal renderer baseline, offline summaries and comparisons.
[CROSS-PLATFORM.md](CROSS-PLATFORM.md#paid-hosted-renderer-performance-mode) owns hosted admission
and execution. The [implementation plan](superpowers/plans/2026-10-21-renderer-performance-baseline.md)
retains the design rationale. This document records no measured baseline or performance budget.

## Evidence and authorization

`tools/Renderer.Perf.Tests` is outside the solution. Its one opt-in performance fact is not an ordinary
headless test. Every native measurement or load run needs separate owner approval naming the workload,
leg, frame counts, duration and spending cap. Implementation approval never authorizes a dispatch,
pilot, retry, profiler capture or follow-up measurement. Software Vulkan and WARP can establish
correctness only. They cannot establish a hardware floor or a T4 result.

The public deterministic recipes cover empty, village, outdoors, crowd and effects/UI scenes. They
use fixed simulation sequences and no consumer assets. A full unit has 1,200 conditioning frames,
five scenes, both layouts, three iterations, 192 warm-up and 256 measured frames per block, and twelve
controls with 64 warm-up and 128 measured frames each. That is 16,944 retained frames. The smaller
pilot has its own frozen schedule and 1,260 frames. Warm-up and conditioning remain separate from
measured distributions. A three-frame batch has one completion drain and one harvest, including
the final short batch. No frame trimming, fastest-iteration selection or adaptive warm-up is allowed.

The outer GPU pair precedes the host clear and follows native command-list End finalization.
`TotalOnly` brackets the frame with two queries. `PerPass` adds frozen parent/child scopes and
reserved skip slots. Detailed markers may change synchronization or encoder boundaries. Its frame
total is diagnostic and never substitutes for a total-only frame result.

## Raw artifact and strict reader

`khaozengine.renderer-perf-report/1` JSON is canonical. Keep its exact manifest, workflow and source
identities, immutable run link, TRX, logs and SHA-256 sidecars. A checked-in baseline summary points
to those immutable artifacts. It never overwrites an earlier baseline or checks in thousands of frames.

Use `RendererPerfReportJson.Parse` before any arithmetic. The decoder enforces the 256 MiB byte cap,
depth 32, required fields, finite values, exact counts, support semantics and publication rules.
`RendererPerfAnalysis.Read(bytes)` uses that reader directly. `Analyze(report, references)` validates
typed input by round-tripping the same codec. Both retain the complete decoded report as `Raw`.
There is no alternate tolerant decoder or frame sampling path.

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
var summary = RendererPerfAnalysis.Read(File.ReadAllBytes(jsonPath));
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
evidence cannot be promoted as a stable baseline. Keep slow and GC frames. Missing clock telemetry
stays missing and does not certify stable clocks.

`Perturbations[scene/iteration]` is the per-pass outer mean minus the matching total-only outer mean
in ns. A nonzero value is labeled `MarkerPerturbationObserved`. It describes instrumentation
sensitivity, including order/clock/work effects. It is not a universal threshold or proof of marker
causation. Keep both layouts and their raw samples.

Promotion eligibility requires the original valid assessment, completed returned final publication,
and no newly detected drift or invalid partition. Optional unsupported counters and incomplete work
models do not become measurements and do not independently invalidate sound frame timing.
Failed, refused or partial publication keeps original failure and cleanup errors. It cannot supply
promotable evidence or numerical comparison deltas. The existing partial-write accounting gap
[#1547](https://github.com/APKiwiOrg/KhaozEngine/issues/1547) remains a proof limit. Offline analysis
does not repair sink accounting or certify final sidecar availability from JSON alone.

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

Control summaries keep outer duration distributions separate from scene frames. Achieved rates use
the existing `AchievedRates` checks for complete samples, exact work identity and validated output.
Unsupported or unmatched controls retain a reason rather than supplying a fabricated rate.

## Comparing evidence

`RendererPerfComparison.Compare(baseline, candidate)` accepts two reports. Its summary overload also
accepts retained explicit reference inputs. Both return identity differences, compatible rows,
candidate-minus-baseline deltas and refusal reasons. No universal percentage budget or pass/fail
regression verdict is assigned.
The summary overload recomputes from its raw report and reference inputs. Cached derived statistics
or eligibility flags cannot override a changed or failed publication.

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
A changed recipe, driver or protocol starts a new baseline series with the prior result retained.
