# Private buffered shadow-stage probe

This diagnostic archive retains the private prepare-phase split. Never merge it into a product branch,
release, pack or adopt it through a consumer pin. Its public bridge exists only to cross the existing
Windowing, Render3D and Game.Render3D assembly boundary. It introduces no assembly dependency.

## Startup and trigger

Set `KE_FRAME_DIAGNOSTICS_PATH` to a new absolute JSON path whose parent already exists. Set
`KE_SHADOW_STAGE_MODE` once at process startup. The exact values are `off`, `counts` and `timings`.
Unset or empty means off. Invalid values, or counts/timings without a usable recorder, produce an
explicit initialization diagnostic and no completed report. There is no runtime toggle.

Create `<result-path>.start` through the existing background trigger flow once the owner has matched
the foreground view. The trigger callback collects no stages. The next callback establishes the
baseline and requests sequence 1. The following boundary stores its completed payload beside the
first cadence interval. The inclusive ten-second boundary or 4096-interval capacity consumes the last
payload and disables the next request before scheduling publication. Closing early publishes no
partial completed report. A final callback without a following boundary is unsampled.

## Schema 3 and validity

Modes serialize numerically: off is 0, counts is 1, timings is 2. Off has no counter or timing data.
Counts has counters and null timing data. Timings has both. Each row carries the requested capture
GUID and callback sequence, the actual producer request and configuration, producer count, callback
acknowledgement and validity status. Saved payloads contain only values.

Surface output publishes a provisional snapshot. Only successful completion of the entire native
callback, including UI, capture, main submission, present and pacing, acknowledges it. A successful
prefix or composite alone does not. Suppression, missing acknowledgement, missing or duplicate
producers, stale identity, actual-mode mismatch and invalid clock data remain explicit invalid rows.
No old scene payload is copied into a missing or suppressed row.

The original raw monotonic interval, native delta, window states, device counters and prefix/composite
pairing remain present. Unsupported GPU counters leave their delta null without erasing valid scene
measurements. Use window stability, cadence-cap, capacity and pairing flags together with `shadowStages`
coverage when judging comparability. Actual HUD timing, shadow flags, budgets and allocated atlas
resolutions are recorded. A consumed-configuration change invalidates stage comparison. Natural
workload changes remain visible in counters.

`shadowStages` reports valid and invalid row counts, status coverage and a separate denominator for
each timing summary. A stage that never executes has a null summary, including when there are no
valid rows. Raw invalid rows are retained. Nothing is divided by a zero stage denominator.

## CPU wall-time intervals

Ticks use the recorded Stopwatch frequency, never TimeSpan units. A zero starting timestamp is valid.
A negative elapsed interval or clock-frequency mismatch invalidates coverage. A null interval means
its boundary did not execute. Timed zero is a measured zero. Counts and off make no new producer
clock reads. The timing payload records the number of those reads.

These are inclusive CPU wall times, including native blocking and scheduling inside each bracket.
They are not GPU durations or exclusive CPU execution. No residual subtraction runs during capture.

- `ScenePrefix` surrounds complete scene prefix recording through offscreen post work, before the
  final surface output. It contains the shadow work below plus the rest of scene recording.
- `CascadeFit` covers directional mode resolution, fit and frustum extraction.
- `PointRequests` covers request gathering and ordering before the cascade pass.
- `PointPreparation` contains rigid index build, slot lookup, static signatures, row selection and
  skinned scheduling, row preparation/uploads, row recording and receiver publication/release.
- `SlotLookup` covers dynamic-row release and both lookup loops. `StaticSignatures` is separate.
  Rigid and foliage signature query/fold intervals nest inside it. Folding excludes query time.
- `FoliageIndexBuild` covers actual lazy index and stream preparation. Its recorded origin identifies
  signature work, row preparation or a direct row-recording fallback.
- `RigidRowQuery` and `FoliageRowQuery` cover repeated rebuilt-row queries, separate from signatures.
- Directional preparation covers receiver setup and full invalidation preparation/comparison.
  Directional selection and recording execute only when dirty. Commit covers dirty-state commit and
  diagnostic publication. Foliage draw counts remain separate from rigid-plus-skinned totals.

## Count units and empty paths

Request counts split static and dynamic lights before residency and budgets. Touch attempts/hits
refer to phase one. Acquire attempts/successes/refusals refer to phase two. An acquire success can
return an incumbent for a duplicate key. It does not claim a newly allocated row. Selected row totals
count scheduled request entries, so duplicate keys can target one occupied slot more than once. Signature mismatch
counts include mismatches on rows that were already dirty.

Each query group reports calls, exact tests, accepted hits and sort inputs. Deltas are read after lazy
preparation. Signature queries and row-recording repeats have separate groups. Index population,
foliage source/range counts, full stream proxies and selected proxies retain different units.

Selected static/dynamic/transient rows, packed rows and completed rebuild/render counts are distinct.
Point-face draws exclude row clears. Rigid and foliage issued-instance counts are accumulated at
actual draw calls. Selected directional span instances include deliberately bridged rejected gaps
and precede possible missing-mesh skips. They are not issued instances. Skinned face calls and
foliage face calls remain separately named.

The collector resets its own payload for every requested scene. No-request, no-atlas, empty-foliage
and clean-directional paths record only work actually executed. Availability and build flags identify
those paths. They do not inherit retained query counters, packed rows or dirty-selection lists.

## Overhead and proof limits

Storage is bounded and preallocated. Warmed collection allocates no managed bytes. Serialization,
sorting, summaries and file I/O happen after the final measured boundary. Allocation freedom does
not establish zero timing overhead.

Compare counts and timings with identical private binaries and matched scene work. This estimates
only incremental timer instrumentation under that observation. Both arms retain counters and the
new bridge, whose cost is not isolated. Do not normalize FPS by draw counts, subtract a synthetic
calibration, infer physical display cadence or claim a speedup from a noisy pair. Headless and native
fixture proofs do not establish live client FPS.
