# Private prefix input diagnostics

Never merge this archive. It adds passive input observations to the existing scene-prefix and completed-callback probe. It does not change rendering, simulation, animation, time, camera, recording capacity or presentation policy. Assembly and file versions remain 21.13.0.0.

The new producer exports schema 5. Historical schema 4 reports and their archived helper sources remain immutable. Their inputs are unavailable. The legacy capacity comparison accepts only schema 4 evidence without a stability purpose. A schema 5 stability trace cannot establish an FPS effect.

## Observation and ownership

Counts and Timings requests copy values at their existing consumption sites. Off publishes no input observation. Each request owns its prefix, view, cascade, camera, light and decision multiplicities. Reusing an epoch and sequence cannot erase an earlier prefix. The next distinct request resets the observation.

The effect clock is observed at prefix entry. The view is copied after the existing size adjustment and view latch. Cascade eye and forward values come from the locals consumed by the fit. Raw, normalized and selected light directions come from the existing light-hold decision. Splits are the clamped distances actually consumed by each fit. Absolute matrices are the CPU selection fits, before GPU clip correction and atlas transforms. Decision flags are copied after the directional decision.

No additional camera getter, light adoption, fit, query, signature walk, native call, readback, timestamp or rendering command is used to collect inputs. Publication after successful output composition uses the saved prefix values. Callback completion remains a separate success acknowledgement. Baseline and end boundaries do not invent input snapshots.

Missing observations, duplicate prefixes or fits, stale identities and failed or abandoned callbacks cannot produce a valid input row. Shadow-stage status 10 is `InvalidInputs`. Disabled directional shadows and a degenerate camera have distinct availability states. Their unavailable groups contain canonical positive-zero words, not fabricated observations.

`ShadowStageProbeInputs` owns values only. Four explicit cascade slots replace variable collections. Added buffered storage must remain at most 2048 bytes per sample and 8388608 bytes at 4096 samples. Layout tests measure the nested sample stride increase. Warmed builder and boundary sampling must allocate zero managed bytes. Deferred JSON export can allocate, and its peak memory is not covered by the sampling-buffer bound.

## Schema 5 words

Every float is exported as an unsigned 32-bit IEEE754 word. Integers and Booleans retain their JSON types. `matrixOrder` is `row-major-M11-M44`, with elements M11, M12, M13, M14, M21 through M44. Signed zero and non-finite payload bits are retained exactly.

| Input group | Word fields and fixed lengths |
| --- | --- |
| Prefix | `prefixEffectTimeWords`, length 1 |
| View | `viewWords`, `projectionWords`, `viewProjectionWords`, `absoluteViewProjectionWords`, each length 16 |
| View auxiliaries | `renderOriginWords`, length 3, and `jitterPixelsWords`, length 2 |
| Consumed cascade camera | `eyeWords` and `forwardWords`, each length 3 |
| Light | `rawWords`, `normalizedWords` and `selectedWords`, each length 3 |
| Fits | `splitWords`, length 4, and `absoluteFitWords`, four arrays of length 16 |

Availability is Missing (0), Active (1), Disabled (2) or Degenerate (3). Active groups require matching observation counts, a positive fit resolution and an active count from 1 through 4. Inactive fits have count and resolution zero. Unused cascade slots and splits are positive-zero words and are excluded from active-fit stability comparisons.

The reader rejects integer lookalikes, wrong word lengths, out-of-range words, malformed active counts and contradictory availability. Non-finite words remain in the raw report. Offline analysis marks the affected fields unusable and gives their exact paths. It never substitutes zero or discards a row to obtain stability.

## Declared stability purpose

Before launch, preparation declares `purpose` as `paused-sun-input-stability` and includes this exact `inputContract`.

```json
{
  "purpose": "paused-sun-input-stability",
  "schemaVersion": 5,
  "framebufferWidth": 1591,
  "framebufferHeight": 1027,
  "recordingSlots": 3,
  "baseFrames": 3,
  "drawables": 3,
  "retirementCap": 8,
  "mode": 1,
  "vsync": true,
  "softwareCapHz": 0,
  "preparePhaseSplit": true,
  "rendererInitializesSwapchain": true,
  "engineBase": "2599bd8a94ce43fc53de62f2544efb89e36d05a8"
}
```

The preparation hash binds this contract to the existing source, runtime, world, process and five-DLL identities. Result dimensions never choose their own expectations. The collector requires the matching explicit purpose, counts mode, slots 3, an owned startup log and a fresh archival run identifier. Native drawable readback must be 3.

```bash
python3 "$PROBE_ROOT/scripts/mac-frame-ring-waits/collect.py" \
  --purpose paused-sun-input-stability \
  --preparation "$PREPARATION" --ready-process-id "$READY_PID" \
  --mode counts --expected-recording-slots 3 --startup-log "$STARTUP_LOG" \
  --run-id "$RUN_ID" --result "$RESULT"
```

This command collects from an already-running, human-readied process. It never launches a client or sends clock commands. Human sun-pause and noon setup remain attestations. The observed scene effect clock is not authoritative time of day.

## Separate outcomes

The analysis artifact contains the input summary and retains four distinct outcomes.

- Measurement integrity includes the existing process, DLL, focus, timing, callback, native-policy, ring-accounting and archive checks, plus strict input ownership and completeness.
- Controlled inputs compare exact words for the unjittered view and origin, consumed cascade camera, raw/live/adopted light, used splits, fit resolution/count and absolute fits. Each group reports coverage, distinct states and the first changed sample and field. Unavailable groups are unavailable, not stable.
- Dynamic observations report jitter, scene frame index, prefix effect time and decision changes separately. Scene frame indices must advance once per completed callback in this single-scene protocol.
- Work observations retain every existing counter distribution, first and last values, ranges and transition counts. Changing work is not hidden.

A valid measurement can have unstable controlled inputs. Full-workload equivalence is always `not established`. Actor poses, full caster identities, animation, point-cache history, foliage settings/focus and residency identity are outside this bounded observer. No paired FPS delta, speedup, timer overhead or automatic follow-up capture follows from the result.

## Provenance and archival

`inputs.py` joins `analyze.py`, `collect.py`, `capacity.py` and `archive.py` in the canonical current helper inventory. Preparation source declarations, live helper hashes, collection metadata and archived identity evidence cover every current helper. Legacy source inventories remain explicitly legacy.

The existing no-follow admission, exclusive run claim, fresh trigger, preflight recheck, consumed-trigger chronology, metadata finalization and same-command archival protections apply. The six artifact roles and manifest remain unchanged. Raw, invalid, unavailable and unstable evidence is retained. Archival failure remains exit 3 and permits archive-only recovery. It never permits reusing a run identifier or silently collecting another window.

Independent review, the full frozen build/test/format finish, private push, fresh client preparation and the single later human capture belong to the coordinator. This implementation does not authorize a client run, settings change, release or merge.
