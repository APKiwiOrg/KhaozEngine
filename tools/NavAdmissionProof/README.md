# Manual hosted navigation admission proof

This is a manual proof harness for the held stable legacy read-scope candidate. It is excluded from
`KhaozEngine.slnx` and normal selective CI test discovery. It is non-packable and uses the repository's
existing .NET target, centrally pinned test packages and engine project references.

The latest approved [hosted attempt](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38807384252)
failed during A's graph acceptance. Retained evidence identifies only `CentralObstacleRejected=false`,
with one layer, 32582 accepted nodes, 874 rejected cells and 258106 directed edges. B never started.
The finalized outer exit is 1. There is no valid A/B outcome or performance conclusion.
Keep the candidate parked.

A 144-cell correctness reproduction demonstrates that the old predicate can reject a valid box roof at
Y=2. The failed representative run retained no center height, so its roof attribution remains
unproven. The repaired contract checks real adjacent ground approaches into the capsule-expanded box
sides and permits the roof. The large fixture has not been rerun with this repair.

Approval for that attempt is exhausted. This correctness repair authorizes no new measurement.
Obtain separate explicit owner approval before any further comparison. No automatic retry is allowed.
Run only on a Linux GitHub-hosted Actions runner through the existing manual stress workflow.
Local execution and self-hosted runners are refused.

## Approval-gated invocation

Only after new approval, the owning orchestrator must commit and push the proof branch before
dispatching this command. Do not run it as verification of an evidence-retention change.

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

Pure failure-path tests are also permitted locally with this exact selection. They use synthetic
metrics, graph records and bytes. They construct no physics fixture, perform no capture or profile,
start no helper processes and execute no timing comparison. The representative pair remains excluded.

```bash
build-slot --label e2-proof-evidence-fix -- \
  dotnet test tools/NavAdmissionProof/NavAdmissionProof.csproj -c Release \
  --filter 'FullyQualifiedName~KhaozEngine.Tests.Movement.Research.ProofEvidenceRetentionTests&FullyQualifiedName!~SingleUnprofiledPair'
```

Small deterministic correctness tests are also permitted locally with this exact selection. Each of
three tests captures one 12 by 12 window and builds one degree-1 legacy profile without timing it.
They use the same public floor, central box and tuning through `PublicBoxCapture`, with bounds
`[-1.5,1.5) x [-1.5,1.5)` and 0.25 m cells. Two cases omit the central box to verify clear traversal
and rejection of a missing obstacle. They never construct `ProofFixture`, start a child process or
invoke the representative pair. The retention cases remain purely synthetic.

```bash
build-slot --label nav-fixture-correctness -- \
  dotnet test tools/NavAdmissionProof/NavAdmissionProof.csproj -c Release \
  --filter '(FullyQualifiedName~KhaozEngine.Tests.Movement.Research.SmallFixtureCorrectnessTests|FullyQualifiedName~KhaozEngine.Tests.Movement.Research.ProofEvidenceRetentionTests)&FullyQualifiedName!~SingleUnprofiledPair'
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
ground. `PublicBoxCapture.cs` holds the shared geometry. `ProofFixture.cs` holds capture options and
tuning derived from the public `MoveTuning.Default` and the existing parity-fixture pattern.
No consumer source or input is used.

Checks require nontrivial accepted directed edges and refused adjacent ground entries at all four
central box sides. An accepted roof is valid. Both graph counts and complete canonical KENB bytes
must match. Every child also loads and rewrites its KENB
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
all observed layer dimensions, counts and eight independent acceptance predicates. The size and count
limits remain unchanged. Every layer must be 164 by 204, accepted nodes must be at least 10000,
directed edges at least 50000, rejected cells nonzero and the original clear edge accepted.

The three ground predicates require all four sides to pass. Sample positions come from the public
box bounds, capsule radius and grid lattice. Each side has a clear cell, an accepted ground approach
and its immediate inward neighbor. Clear-to-approach traversal must succeed on a layer selected by
measured ground height. The inward cell must have captured ground at Y=0, lie outside the solid box
but inside its capsule-expanded side, and have no accepted ground node. Traversal from the accepted
ground approach into that adjacent cell must be refused across every target layer. Missing ground
approaches cannot pass through a vacuous `CanTraverse=false` result.

Evidence includes the center and all side cells, world positions, captured heights, graph layer
indices, visible surface heights, node acceptance and each side's three predicates. A null graph
height means a blocked or absent surface, so captured heights independently establish the ground
under a rejected cell. The center roof is diagnostic, not a ground rejection predicate. An acceptance
error includes the counts, dimensions, predicates and these geometry diagnostics. Synthetic retention
tests still exercise every top-level predicate independently before failure.

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
