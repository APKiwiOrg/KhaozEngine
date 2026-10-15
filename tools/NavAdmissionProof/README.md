# Manual hosted navigation admission proof

This is a manual proof harness for the held stable legacy read-scope candidate. It is excluded from
`KhaozEngine.slnx` and normal selective CI test discovery. It is non-packable and uses the repository's
existing .NET target, centrally pinned test packages and engine project references.

The first approved [hosted attempt](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38802161651)
failed after A returned from its single BuildProfile, during graph acceptance. B never started.
Its metrics, graph counts and canonical output were not retained, so the failed predicate remains
unknown.

The next approved [hosted attempt](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38807384252)
at `24c83b0e4` retained A's failure at `CentralObstacleRejected=false`. Its graph had one 164 by 204
layer, 32582 accepted nodes, 874 rejected cells and 258106 directed edges. The clear edge passed.
B never started and no canonical bytes were produced. That evidence contains no center-node height,
so it does not establish whether the accepted center was a roof or an invalid floor. The previous
predicate assumed layer 0 meant ground and rejected any accepted center, including a valid roof.
Capture records exposed tops, and layers can combine disjoint regions at different heights.
The height-aware contract below repairs that assumption. It does not establish that the failed
graph would pass. There is no valid A/B outcome or performance conclusion. Keep the candidate parked.

Approval for both attempts is exhausted. This acceptance repair authorizes no new measurement.
Obtain separate explicit owner approval before any further comparison. No automatic retry is allowed.
Run only on a Linux GitHub-hosted Actions runner through the existing manual stress workflow.
Local execution and self-hosted runners are refused.

## Approval-gated invocation

Only after new approval, the owning orchestrator must commit and push the proof branch before
dispatching this command. Do not run it as verification of a harness repair.

```bash
gh workflow run stress-test.yml \
  --repo APKiwiOrg/KhaozEngine \
  --ref feature/nav-admission-proof-1417 \
  -f project=tools/NavAdmissionProof/NavAdmissionProof.csproj \
  -f filter='FullyQualifiedName=KhaozEngine.Tests.Movement.Research.StableLegacyAdmissionProofTests.SingleUnprofiledPair' \
  -f iterations=1 \
  -f load=0 \
  -f configuration=Release \
  -f support-cost=true
```

The engine caller keeps the shared workflow pinned to
[`14dba4a61d87676a87e089d001e223671e4894a1`](https://github.com/APKiwiOrg/ci-workflows/blob/14dba4a61d87676a87e089d001e223671e4894a1/.github/workflows/stress-test.yml).
Only this project selects the shared workflow's existing `build-target` input. Other selections keep
the existing solution-build default. No shared workflow pin or default stress input changes.

The shared workflow builds this project once in Release, then executes this CLI selection once
with `--no-build`. Its support-cost step provides the artifact path and fixed-compilation environment.

```bash
dotnet test tools/NavAdmissionProof/NavAdmissionProof.csproj -c Release --no-build \
  --filter 'FullyQualifiedName=KhaozEngine.Tests.Movement.Research.StableLegacyAdmissionProofTests.SingleUnprofiledPair' \
  --logger 'trx;LogFileName=run-1.trx' --results-directory "$RUNNER_TEMP/stress"
```

Do not simulate the hosted environment to run that command locally. The runtime guard also requires
`GITHUB_ACTIONS=true`, `RUNNER_ENVIRONMENT=github-hosted`, `RUNNER_OS=Linux`, a manual `stress-test`
event, the exact project and filter, `ITERATIONS=1`, `LOAD=0`, `CONFIGURATION=Release`,
`SUPPORT_COST=true` and `DOTNET_TieredCompilation=0`. The Movement binary must identify the Actions
commit. Children require the wrapper's invocation token and disable runtime diagnostics.

Build-only preparation is permitted locally through the machine-wide build slot.

```bash
build-slot --label e2-hosted-proof-build -- \
  dotnet build tools/NavAdmissionProof/NavAdmissionProof.csproj -c Release
```

Pure correctness and failure-path tests are also permitted locally with this exact selection. They
use small constructed navigation graphs, synthetic metrics and bytes. They construct no physics
fixture, perform no capture or profile, start no helper processes and execute no timing comparison.
The representative pair remains excluded.

```bash
build-slot --label e2-proof-evidence-fix -- \
  dotnet test tools/NavAdmissionProof/NavAdmissionProof.csproj -c Release \
  --filter '(FullyQualifiedName~ProofObstacleAcceptanceTests|FullyQualifiedName~ProofEvidenceRetentionTests)&FullyQualifiedName!~SingleUnprofiledPair'
```

## Bounded comparison

One wrapper launches exactly two fresh helper processes, serially, using the same binary set.
A sets `UseStableLegacyReadScope=false`. B sets it to true. That flag is the only behavioral input
difference. Both use degree 1, `DOTNET_TieredCompilation=0` and `DOTNET_EnableDiagnostics=0`.

Each child constructs and captures the same public fixture outside timing, then calls exactly one
`BuildProfile` inside timing. Two profiles total. No warm-up, retries, repeat loop, synthetic load,
sampling, profiler, private input or production counters. Each child has a hard three-minute limit
covering setup, capture, profile and output checks. Timeout triggers termination and proof failure.
If A fails, B never starts. Do not resize or repeat an inconclusive comparison automatically.

Allow 10 to 20 minutes for the hosted job, including checkout, restore and the single Release build.
The maximum representative child execution is six minutes. Process termination has a bounded
10-second cleanup wait after a timeout.

The fixture is a deterministic 164 by 204 cell window at 0.25 m spacing, with half-open XZ bounds
`[-20.5,20.5) x [-25.5,25.5)`. A complete physics owner contains a flat floor at Y=0 and three public
box obstacles. The authenticated movement view excludes only the floor and uses matching analytic
ground. `ProofFixture.cs` holds the fixed box geometry, capture options and tuning derived from the
public `MoveTuning.Default` and the existing parity-fixture pattern. No consumer source or input is used.

Checks require nontrivial accepted directed edges and a refused central ground obstacle. An accepted
roof surface is allowed. All layers are checked for accepted surfaces beneath the roof and illegal
ascending connections or ground-level crossings through the box. Accepted descents retain their
direction and do not authorize the reverse ascent. Both graph
counts and complete canonical KENB bytes must match. Every child also loads and rewrites its KENB
and verifies that a physics mutation succeeds after the build releases its read scope.

There is no public API that packages an already-built profile without building it again. The
test-only `CanonicalProfileFile` resolves the existing identity encoder, captured columns, graph and
private container constructor outside timing. It uses the production writer and loader for complete canonical
bytes and checks regenerated candidate links. This adapter depends on that internal layout and must
fail if the layout changes. It adds no production API or instrumentation.

## Retained evidence and decision

The `support-cost` artifact retains `run-1.json` plus attempted-child JSON, available KENB files and
stdout/stderr logs under its invocation directory. Successful JSON contains the following evidence
because the shared workflow removes passing console logs and TRX files.

- Stopwatch wall seconds, process CPU seconds and calling-thread CPU seconds through Linux
  [`CLOCK_THREAD_CPUTIME_ID`](https://man7.org/linux/man-pages/man3/clock_gettime.3.html).
- Precise process and current-thread managed allocation deltas, GC collection counts and
  `Monitor.LockContentionCount` delta. Native allocation is excluded.
- Runtime, build SDK, architecture, OS, Actions commit/run, configuration, compilation settings and
  a hash of the complete DLL/deps/runtimeconfig binary set. Order, child exit statuses, elapsed times,
  timeout flags, fixture identity, graph counts, KENB lengths/hashes and complete byte equality.

Each child writes its environment, fixture, selected flag, invocation identity and running stage
before fixture setup. It checkpoints each later stage and saves the measured counters immediately
when the single measurement returns, before graph inspection or acceptance. Graph evidence contains
all observed layer dimensions, counts and six acceptance predicates. Every declared layer must have
one matching dimension record in order. Empty, missing or duplicate dimension records fail. Every
layer must be 164 by 204, accepted nodes must be at least 10000, directed edges at least 50000,
rejected cells nonzero and the real clear-space directed edge must remain accepted.

The central obstacle probe records all 64 cells in an 8 by 8 window at centers from -0.875 to 0.875
on each axis, on every layer. Each node records its grid and world coordinates, in-bounds status,
surface height, physical feet height and passability. It also records all 604 directed neighbor
queries with either endpoint in each window, including both directions across the boundary, and
every candidate link with its accepted status and both endpoints, even outside the window.
Missing, duplicate, incomplete or malformed probe data fails acceptance. Link evidence must match
the candidate and accepted link counts. Non-finite coordinates or heights are retained as the JSON
strings `NaN`, `Infinity` or `-Infinity` and fail `DataComplete`. They cannot prevent the failure
checkpoint from being serialized.

Inside the box footprint `(-0.6,0.6) x (-0.6,0.6)`, an accepted node must have both surface and feet
height at the Y=2 roof, within 0.001 m. An accepted Y=0 floor beneath a roof fails on any layer.
Accepted ascents into the footprint above the fixture's 0.4 m step limit fail. Accepted traversals
with both endpoints below roof height fail when their XZ segment crosses the solid footprint,
including links whose endpoints lie outside it. Roof-to-roof traversal and accepted roof-to-ground
descents are allowed. This preserves the graph's directed proof semantics. Four pairs of ground
cells beside the box, at 0.875 and 1.125 m on each signed axis with the other coordinate at 0.125 m,
must have finite Y=0 surface and feet heights and accepted edges in both directions. The inner
cell is 0.275 m beyond the wall, outside the 0.2 m capsule radius. This prevents a wholly rejected
near-box stencil from satisfying the obstacle check. The probe reports `DataComplete`,
`GroundNodesRejected`, `IllegalTraversalRejected` and `ClearGroundBesideBox` separately, and all must pass.
An acceptance error includes the counts, dimensions, observed probes and each predicate value.

Completed canonical bytes and their length/hash are retained before load/rewrite and scope-release
checks. A later failure retains those bytes and the failed stage. An orderly exception writes
`Status=Failed` and the exception. A killed child can retain `Status=Running` at its last checkpoint.
Null fields mean the stage did not complete. Neither state is a successful child.

The wrapper loads the latest child checkpoint even after a nonzero exit, timeout or diagnostic-drain
failure and embeds available partial evidence in its sample. Missing or malformed checkpoints retain
an evidence-read error. A zero exit alone is insufficient. Only an explicitly completed, successful
child that passes all existing JSON, metric and KENB checks can allow B to start. Any A failure stops
the pair without a retry. Partial metrics and default false parity flags are diagnostic evidence,
not a pairwise comparison or an observed mismatch. Ratios remain null until complete parity succeeds.

Wall timing encloses only the single `BuildProfile` call. CPU, GC, allocation and contention snapshots
bracket it and include their boundary costs. Process counters include runtime threads. First-use JIT
remains part of the cold profile. No per-method operation counts or exclusive CPU attribution exist.
The thread clock is read on the synchronous child main thread, with no async work in the measurement.

The wrapper writes `pendingFinalization` with `Section=navAdmissionPair` and a matching `RunId`.
The section contains fresh `CapturedAtUtc`, nonempty `Samples` and null `ProcessExitStatus`.
After `dotnet test` returns, the shared workflow invokes `scripts/record-support-cost-exit.py` to
record its actual exit and remove the pending marker. Missing or malformed evidence, failed children,
timeouts, invalid measurements and unequal bytes fail the proof. The wrapper never finalizes its
own process exit. Failure envelopes keep the same pending-finalization contract and the workflow
records the actual nonzero outer exit. Require finalized `ProcessExitStatus=0`, two successful
completed children and complete parity before using a successful artifact. Retained partial evidence
never converts a failed proof into a successful outcome.

Parity success is not performance acceptance. A clear coherent reduction in process and main-thread
CPU, together with byte and graph parity, supports reconsideration for this public fixture.
Otherwise keep the candidate parked. One ordered A/B pair cannot establish small statistical wins,
private-world speedup, historical macOS causality or operation-count equivalence. Review the evidence
with those limits and retain it without an automatic retry.
