# Task 5 report: Three-state residency and background build queue

## Outcome

Task 5 adds the opt-in `TileRegionResidencyProfile` and public `TileRegionResidencyState` diagnostics. An absent
profile keeps the existing synchronous `TileResidencyConfig` path unchanged. An explicit profile retains full
Gameplay regions, snapshot-only Decor regions, and an unload hysteresis band. Gameplay-to-decor transitions free
region ground and cover handles while retaining detached prop snapshots. Dirty regions keep the existing editor
protection past the unload boundary.

The internal generic `TileWorldBuildQueue` carries immutable keyed requests and generation-tagged CPU results.
It bounds worker concurrency, rejects superseded and cancelled results, and applies ready work on the caller's
thread in current-focus distance order. Full ground, coarse ground, and HLOD each have an independent apply cap.
Gameplay priming drains full-ground work while keeping decor applies capped.

## TDD evidence

- Baseline focused Debug: 31 passed, 0 failed, 0 skipped.
- Initial RED: the focused build failed with CS0246 for the missing queue, result, request, and kind types.
- First GREEN: 22 passed and 2 failed. Both failures exposed incorrect test observations. One expected list
  omitted the independently budgeted HLOD apply. One residency assertion counted cumulative replacement uploads
  rather than live gameplay draws.
- Final focused Debug: 46 passed, 0 failed, 0 skipped.
- Final focused Release: 46 passed, 0 failed, 0 skipped.

Focused command:

```bash
dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Debug --filter "FullyQualifiedName~TileRegionResidencyTests|FullyQualifiedName~TileWorldBuildQueueTests|FullyQualifiedName~TileWorldViewTests"
```

The same filter passed in Release.

## Behavior pinned

- Arbitrary valid gameplay, decor, and unload boundaries classify in all eight directions.
- Outward transitions demote Gameplay to Decor, retain Decor through the unload boundary, then unload.
- Inward transitions promote Decor to Gameplay.
- Dirty regions stay resident as Decor after leaving the unload ring.
- An absent profile keeps every legacy resident in Gameplay state.
- A profile prime builds and settles gameplay ground without creating full decor meshes.
- Worker concurrency never exceeds its configured bound.
- Detached input values survive later source mutation.
- Ready results apply nearest first against the current focus with separate caps for all three build kinds.
- A later generation rejects an older completion for the same key.
- Cancellation rejects running and ready results.
- Gameplay prime drains every full-ground result while leaving excess coarse and HLOD work ready.
- Disposal drains workers, rejects their results, and is idempotent.

## Verification

- Broader Render TileWorld Debug: 287 passed, 0 failed, 5 expected GPU skips.
- Broader Render TileWorld Release: 287 passed, 0 failed, 5 expected GPU skips.
- TileWorld document Debug: 362 passed, 0 failed, 0 skipped.
- TileWorld document Release: 362 passed, 0 failed, 0 skipped.
- Full Release solution build: 0 warnings, 0 errors.
- `scripts/check-dashes.sh --tree`: passed.
- `scripts/check-prose.sh --tree`: passed.
- `scripts/check-file-size.sh --tree`: passed.
- `scripts/check-doc-versions.sh`: passed for engine version 18.36.0.
- `git diff --check`: passed.

## Concerns

- Task 5 retains detached Decor snapshots but intentionally draws no decor representation yet. The following
  coarse-ground and live-cluster tasks own the concrete queue payloads, uploads, and draw calls.
- The queue is internal so later tasks can refine its payload union without creating a second public streaming
  API. The profile and residency state are the only new public surface in this task.
