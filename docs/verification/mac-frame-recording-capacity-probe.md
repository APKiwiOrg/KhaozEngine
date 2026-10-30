# Private Metal recording-capacity comparison

Never merge this archive into main. It is an experimental comparison of recording storage at 3 and 6,
with unchanged rendering, ownership, submission, presentation and capture policies. Assembly and file
versions for this legacy comparison stay 21.13.0.0. The [released 21.21.0 carry-forward](mac-frame-release-2121-input-diagnostics-probe.md) has its own source and assembly identity. This legacy comparison uses schema 4 evidence. The current producer emits
[schema 5 input observations](mac-frame-input-diagnostics-probe.md) for a separate stability purpose.
Old captures and their archived helpers remain immutable. The independent review, full frozen-solution
acceptance, archive push, immutable client preparation and human capture handoffs belong to the coordinator.

## Device configuration

`KE_METAL_RECORDING_SLOTS` is read once per native Metal device creation. Unset uses the entire legacy
`KE_METAL_FRAMES_IN_FLIGHT` resolution and startup wording. Exact strings `3` and `6` enable the probe.
Every other string is refused. Enabled mode requires a recognized base setting resolving to 3. An invalid
nonblank base is refused even when the legacy resolver would fall back to 3. No environment is changed
by the production implementation.

| Consumer | Control | Candidate |
| --- | ---: | ---: |
| Uniform ring and allocator recording slots | 3 | 6 |
| Each command list's staging arena slots | 3 | 6 |
| Base frames-in-flight value | 3 | 3 |
| Requested and actual CAMetalLayer drawables | 3 | 3 |
| Scene retirement sealed-batch cap | 8 | 8 |
| Uncommitted-buffer diagnostic warning bound | 4 | 7 |

The warning bound is recording slots plus one. It does not reconfigure the native command queue.
Uniform backing is the logical size rounded up to 256 bytes times recording capacity. That backing
doubles between these settings. Staging blocks are allocated on demand and more slots can retain more
blocks. Total process or GPU memory does not necessarily double. More capacity can increase latency
or move waiting to a drawable or another gate. No image-quality or graphics-settings change is allowed.

Enabled devices emit one `KE_METAL_RECORDING_CAPACITY_V1 ` JSON startup line after successful
configuration. Its schema version is 1, independent of the runtime report schema. It contains `probe`,
`baseFrames`, `selectedRecordingSlots`, `actualRecordingSlots`, `requestedDrawableCount`,
`actualDrawableCount`, `drawableReadback`, `retirementPolicyCap`, `uncommittedDiagnosticBound`,
`deviceIdentity`, `deviceName`, `assemblyName`, `assemblyVersion`, `assemblyMvid` and
`assemblyInformationalVersion`. Windowed or headless-layer creation reads the configured native
maximumDrawableCount back once and requires 3. An offscreen-only device reports JSON null and
`not-applicable`, which cannot establish a windowed treatment capture. No per-frame reads or timing
sites are added. Startup failure preserves the original exception and releases constructed children
before the existing outer owners release their layer, timeline, queue and device references.
Swapchain attachment and startup readback each own a lexical autorelease scope, including exceptional
exits from the initialization callback. These scopes run only during device creation.

## Collection and treatment audit

Both human launches use identical newly built application and engine bytes. Each carries base 3,
recording slots 3 or 6, counts mode, ring observation and prepare split enabled. VSync is requested,
the software cap is 0, and the actual framebuffer is 1668 by 1029. The collector validates the observed
fields across the whole window. An argument or startup intent alone never establishes treatment.

The legacy schema-4 reader and collection path remain available as described in
[the recording-wait contract](mac-frame-ring-wait-probe.md). For this comparison, its paired opt-in is
`--expected-recording-slots {3,6}` and `--startup-log`. The startup log must be the owned process log
inside the capture directory. Exactly one matching startup line is selected. Its complete original
line bytes and SHA-256 are retained. Other log lines and environment dumps are excluded. Duplicate
JSON keys, unknown fields, contradictory counts, wrong types and mismatched device or Metal DLL
identity are refused. Available boundary and prefix/main receipt depths must all equal the exact
integer expectation. Gaps remain limited evidence.

Direct `python3` invocation of the collector and analyzer disables bytecode writes before loading
local helper modules. It therefore leaves the frozen source directory clean without requiring an
interpreter flag from the operator. Imported use remains caller-owned: analysis or test harnesses
that import these modules use `python3 -B` or `PYTHONDONTWRITEBYTECODE=1`. Existing untracked files
still fail the source check. No cleanliness, input-admission or evidence-retention rule is relaxed.

The preparation manifest additionally records `frozenEngineBase` and uses the frozen game source
`f45462a87441c18682f7c2309ef2f048c4abebb0`. The frozen engine base is
`7ae1e83d18a0cafe4d3b079b0dc3a399de390b4f`. The actual `probeCommit` is the new accepted archive
commit, and every replacement informational version identifies that commit. `replacements` supplies
the Metal assembly MVID, version and SHA-256 used to audit the startup line. Its `sourceHashes` must
include the committed `analyze.py`, `collect.py`, `capacity.py`, `archive.py` and `inputs.py` paths under
`scripts/mac-frame-ring-waits/`. The collector records exact `helperHashes`, checks them against those
committed source declarations before triggering and again after collection, and compares them across
the pair. Historical schema-4 proofs retain their original four-helper inventory. Current collection
uses the canonical five-helper inventory. Stability-purpose evidence is refused by the legacy
comparison entry points. Existing payload,
source, process, mapped-DLL, trigger-consumption, focus and callback-completion audits still apply.

This comparison requires the named `worldSelection` descriptor returned by
`capacity.expected_world_selection()`. It binds all 40 committed files under
`assets/worlds/hollowmere` at the frozen game commit: world.json, 30 region JSON files and nine generated
navigation files. The navigation files are already part of 40. Exact path/content identities are pinned
by SHA-256 of sorted UTF-8 lines `path SHA256`, each terminated by LF. The directory fingerprint is
`e057497431deaf910db248d706c385e99898b0830a06fa8a9415d07dc5fa9707`.

Both relative catalogs, `assets/catalogs/archetypes.json` and `assets/catalogs/ground.json`, are checked
separately against declared game source hashes, working bytes and their corresponding Content payload
rows. Their fingerprint under the same recipe is
`753413079a39bbfb68f96e911279585805db34666f67adf914628bac7ec890f2`.
Every world row must also match a declared game source hash. Working directory inventory is checked
before and after capture against the files and their implied ancestor directories. Extra entries,
including empty directories, missing files and symlinked paths are refused. Collector source hashing
and input reads admit regular files through no-follow, nonblocking descriptors before reading, so a
FIFO substitution cannot block an earlier source-read step. Preparation hashes identify the bytes
actually parsed. Startup logs keep their append-compatible byte-snapshot behavior. Prefabs, the
gameplay bundle and other runtime assets retain their separate game-source and payload coverage.

The descriptor is copied into collection metadata and checked by offline analysis with exact types.
`worldFilesRehashed` reports the actual 40 directory files. The original generic collector's 41-row shape
is retained only for historical metadata without a named selection. Its original path recipe was not
recovered, and its numeric count is not used as proof of the current world selection.

After independent acceptance and a fresh human-ready response, one collection command has this shape.
It neither launches a client nor grants permission for another capture.

```bash
python3 "$PROBE_ROOT/scripts/mac-frame-ring-waits/collect.py" \
  --preparation "$CAPTURE_ROOT/preparation.json" \
  --ready-process-id "$READY_PID" --mode counts \
  --result "$CAPTURE_ROOT/control-3.json" \
  --expected-recording-slots 3 --startup-log "$CAPTURE_ROOT/control-3.log" \
  --run-id control-3
```

Use a fresh run identifier and paths for the candidate and change the expectation to 6. Collect only
one ten-second window per treatment, within the existing 4096-interval bound. No automatic recapture,
profiler, artificial load or faster-frame subset is part of the experiment.

## Immutable local archive

Capacity collection finalizes measurement metadata and analysis, then publishes an allowlisted bundle
within the same command. The destination is
`$HOME/.local/share/grimhollow-performance-evidence/ring-capacity/<probe-commit>/<run-id>/`.
Working files remain under `/tmp/grimhollow-mac-frame-performance/`. Only the system `/tmp` alias is
canonicalized. Symlinked artifact paths or parents are refused.

The fixed semantic inventory is raw result, final collection metadata, analyzer output, preparation
manifest, selected startup proof and helper/source identity manifest. A hash/size manifest names the
exact files and explicitly lists absent artifacts on failed captures. No client, source tree, whole
log, setting, database, credential or image is archived. The publisher verifies source bytes, copies
to a fresh sibling temporary directory, verifies every copy and publishes atomically without replacing
an existing destination. macOS uses `renameatx_np` with `RENAME_EXCL`. Unsupported platforms fail closed.
An external archive receipt carries the destination and immutable manifest hash. It is separate from
the bundle, avoiding a circular checksum or post-publication rewrite.

Measurement status and archival status are independent. Valid audited data without a successful archive
receipt is not collection success. An archival failure returns `captured-but-not-archived`, retains the
working evidence and claim, and allows archive-only recovery. Failed and limited measurements remain
failed and limited after successful archival. No raw evidence is represented as remotely backed up.
The coordinator pushes the result summary and bundle hash manifest separately before another handoff.

Final collection bytes must equal the exact record that passed the measurement audit before they can
become archive inputs. Replacing or editing that proof is refused. Archival interruption, including
interruption after bundle publication, preserves finalized input bytes and their measurement status.
It never returns to capture error handling or rewrites a claim's hashed inputs.

If metadata writing or replacement fails, the collector preserves the original metadata and temporary
file. It writes an exclusive `<result>.collection.recovery.json` containing the exact pending record
and names that copy in the archive claim. The claim records `metadataFinalizationError`. Collection
returns exit 3 with the original error and claim path, without retrying metadata replacement or capture.
Archive-only recovery then uses the complete copy, including when the original temporary file is partial.
If writing that recovery evidence also fails, the output reports the archival failure and retains the
available files. It does not claim that a usable recovery claim exists.

Exit 0 requires valid archived evidence. Exit 2 means limited archived evidence, exit 1 means failed
archived evidence, and exit 3 means archival or metadata-finalization failure. Interrupted archival also
uses exit 3, preserving the actual measurement status for recovery.

Recovery takes the recorded claim and original hashes. It never invokes the collector or creates a
trigger. An existing destination is accepted only when the exact manifest and all bytes verify.

```bash
python3 "$PROBE_ROOT/scripts/mac-frame-ring-waits/archive.py" \
  --claim "$CAPTURE_ROOT/control-3.archive-claim.json"
```

Independent analysis verifies each retained bundle before reading it. `capacity.compare_runs` reports
both actual configurations, complete work-count distributions, callback cadence, frame-time statistics,
prefix/main counted CPU waits, remainder, drawable API duration, drains and coverage. A configuration,
source, DLL or work-distribution difference prevents interpretation of an FPS effect. One matched pair
still proves neither a causal speedup nor sustained 144 FPS, GPU execution time, physical presentation
cadence, zero observer overhead or production suitability. Matching recorded work cannot prove all
CPU/GPU work and host load matched.
