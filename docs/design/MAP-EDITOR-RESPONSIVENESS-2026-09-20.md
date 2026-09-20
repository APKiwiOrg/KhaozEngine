# Map editor responsiveness measurements

Status: complete for the CPU-only Task 4 scope. Interactive frame rate and rendered stroke latency remain manual.

## Measurement boundary

`MapEditorResponsivenessTests` measures deterministic CPU work with a 960 by 540 viewport, three warm-up samples,
and 21 measured samples. It reports median and p95 duration plus current-thread allocation bytes. Correctness uses
separate assertions with no timing threshold.

The retained evidence used .NET 10.0.12 on macOS 27.0 and an Apple M2 Max with 32 GiB memory. The representative
Ruinborne island was copied before the run and loaded as a tiled map. The loaded copy contained 17,281 authored
placements, 115 NPC spawns, two player spawns, and 64 sculpt tiles. The source map was not modified.

These are CPU-only timings. They are not FPS, GPU time, presentation time, or rendered stroke latency. Other build
and backend jobs were active on the host during some runs. Allocation results and large within-process comparisons
are stable. Small timing differences and p95 values remain noise-sensitive.

## Navigation results

The original implementation was measured at `b0517e1d`. The demand-driven implementation was measured at
`cc7cee01`. Each navigation sample contains 64 editor updates.

| Operation | Base median / p95 us | Base B/op | `cc7cee01` median / p95 us | `cc7cee01` B/op |
|---|---:|---:|---:|---:|
| Idle, ground-facing | 74.775 / 81.425 | 64 | 73.488 / 142.703 | 0 |
| Captured orbit | 76.076 / 82.040 | 184 | 77.466 / 159.178 | 120 |
| Captured pan | 76.040 / 98.197 | 184 | 64.505 / 168.876 | 120 |

The ground-facing duration change is inside observed host noise. The allocation delta is exact. A base-equivalent
sky-miss idle action, which runs the current idle update and the old unconditional real terrain sampler, measured
128.564 us median and 544.508 us p95. The current idle update measured 73.488 us median in the same process. This
paired workload shows the cost of marching to the far plane without claiming an interactive frame-time gain.

Scene integration tests assert the sampling policy directly. Ten idle frames sample zero times. A fresh orbit or
pan press samples once. Captured frames do not sample. An uncaptured dolly samples once. A release followed by wheel
input in the same frame samples once after capture ends. A release followed by acquisition with another button also
samples once. Navigation leaves the map hash and undo state unchanged.

## Authored placement results

The base-equivalent rows execute the exact former pipeline in the harness: effective filtering, `Partition`, then
projection to the `PropPlacement` list passed to `DrawProps`. The new path refills one viewport-owned list every
draw. `DrawProps` consumes that list synchronously through `PropRenderer.EmitParts` before the next refill.

| Operation over 17,281 placements | Base-equivalent median / p95 us | Base-equivalent B/op | New median / p95 us | New B/op |
|---|---:|---:|---:|---:|
| Visible authored preparation | 1266.245 / 4558.474 | 1,244,312 | 275.406 / 660.807 | 0 |
| Dirty placement cache while Terrain Only is active | 242.292 / 906.917 | 1,244,344 | 0.036 / 0.042 | 0 |

The buffer is refilled rather than memoized. Tests cover document order, first matching duplicate selection,
per-element and category filtering, whole-group hiding, `ShowAll`, empty and shrinking inputs, unchanged transforms,
and zero warmed allocation. Terrain Only checks effective placement visibility before reading the cache. A sculpt
invalidation therefore leaves the hidden cache dirty. Revealing placements rebuilds once against the current field.

## Other CPU observations

The exact `cc7cee01` copied-map run recorded these additional workload rows.

| Operation | Median / p95 us | Median / p95 B/op |
|---|---:|---:|
| Visibility toggle | 0.133 / 0.139 | 0 / 0 |
| Overlay geometry | 20.732 / 49.725 | 0 / 0 |
| Sculpt stroke | 20030.708 / 26812.677 | 13,064,016 / 13,064,223 |
| Undo | 5907.016 / 6718.000 | 4,345,186 / 4,345,523 |

Visibility toggles caused zero terrain or scatter rebuilds. Overlay geometry stayed within its caller-owned bounded
buffer and allocated zero bytes. The sculpt and undo figures include real document mutation, history snapshots,
terrain sampling, and hashing across the copied map. They are retained as observations. This task did not widen into
a document-history rewrite.

## Reproduction

Run from a KhaozEngine worktree whose Ruinborne sibling has the representative map. The profile copies the map to a
temporary directory and never writes the source.

```sh
profile_root=$(mktemp -d /tmp/ke-map-editor-profile.XXXXXX)
cp -R ~/Ruinborne/Ruinborne.Core/assets/maps/island "$profile_root/island"
KE_MAP_EDITOR_PROFILE_MAP="$profile_root/island" \
KE_MAP_EDITOR_PROFILE_COMMIT="$(git rev-parse --short HEAD)" \
dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj \
  -c Release --filter FullyQualifiedName~MapEditorResponsivenessTests \
  --logger "console;verbosity=detailed"
```

The automated harness cannot measure rendered stroke latency or subjective camera response. Run this command from
the Ruinborne worktree for the remaining manual check. It opens a window, so it was documented and not run here.

```sh
dotnet run --project Ruinborne.Editor/Ruinborne.Editor.csproj -- \
  --map Ruinborne.Core/assets/maps/island
```
