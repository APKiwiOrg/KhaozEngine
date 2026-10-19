# Contact bake proof reuse verification

## Task 1. Owning-world exact completed footprints

Implemented the optional non-owning session and reference-identity source domain, full bitwise
request/filter/join-mode key, bounded owned payload table, fresh current-lease publication and
six additive counters. CounterCount is 42. The selected-view extension belongs to Task 2.
Movement source grouping, initial Hold reuse and the staged version remain unchanged.

### Behavioral red

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~WorldDeclaresExactFootprintReuseCapability'
```

Exit 1. One assertion failure because the unchanged world did not implement
`KhaozEngine.Physics.IPhysicsSupportFootprintReuseSource`. Restore and compilation succeeded.
The typed session, payload, counter, reference and sentinel tests preceded implementation.

### Focused checks

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseTests|FullyQualifiedName~WorkerProofReuseCounters'
```

Exit 0, initially 11 passed. Self-review then added deterministic retention and callback-lifetime
regressions before their fixes.

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~RetentionSurvivesCounterEnablementChanges'
```

Exit 1, one expected failure. A retained key incorrectly reported zero after re-enabling counters.
Storage accounting now balances acquisition and release independently of event enablement.
Counter reset preserves live accounting and per-session peaks. Worker merge adds events and
live retention while taking the maximum per-session peaks.

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseTests|FullyQualifiedName~WorkerProofReuseCounters' --logger 'console;verbosity=detailed'
```

Exit 0 with 14 passed after the retention fix. The following callback regression then failed.

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~ResetOrDisposeDuringEvaluationDoesNotSeedOrChangeTheFullResult'
```

Exit 1, two expected failures. Reset during the evaluator's observation callback seeded a cleared
domain. Disposal during that callback added an ObjectDisposedException after evaluator output.
A source version now prevents publication across reset, and changed or disposed sessions skip
publication while preserving the original full result.

The detailed focused command then exited 0 with 16 passed. After strengthening the failure
tests with distinct member sentinels, its final invocation also exited 0 with 16 passed.
No test run was repeated without a code or coverage change.

The final selection proves cross-lease Complete publication, rejection of the old result under
the new lease, complete empty payloads, all named payload fields and join words by bits,
untouched tails, invalid and insufficient-span sentinels, nested-session refusal, reset/disposal
cleanup, reference and reduced-work-policy bypass, zero capacity/budget and saturation fallback,
reference-identity domains, counter restoration/merge, and reentrant source-lifetime changes.

### Observed work and storage

The real cross-lease box query recorded Volumes 1, Proposals 2, FootprintReuseHits 1,
FootprintReuseMisses 1 and FootprintReuseStores 1. EdgeSteps and InitialHoldReuseHits were zero.
Its live and peak retention were one key and 106,880 bytes. Reset and disposal each reported
zero live keys and bytes. The bounded-table fixtures each observed a nonzero budget-skip count.

The table uses open addressing with full key equality after hashing. Default capacity is
512 entries and the retained-payload budget is 2 * 1024 * 1024 bytes. Its deterministic byte
accounting charges the full fixed entry-array capacity, including spare slots and inline keys,
counts and proposal verdicts, plus each exact-length member array and join-word array using
their actual element sizes. Both inline witness slots are part of each member. Fixed lookup
metadata has at most 512 entries. Reset drops every owned array. These are retained element
capacities. Managed allocation overhead is outside this metric.

### BR-R2 pinned baselines

```bash
build-slot --label bake-b-design --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationBaselineTests|FullyQualifiedName~WorkerMergeAddsEveryCounter|FullyQualifiedName~WorkerDiagnosticsScopeRestoresEveryPriorThreadValue' --logger 'console;verbosity=detailed'
```

Exit 0, nine passed. The seven existing baseline rows use null execution, whose validated
degree is 1, and retain their normalized full-file hashes. The other two tests prove all
42 counter slots merge and the existing worker recorder state restores.

| Fixture | Normalized SHA-256 |
| --- | --- |
| low-lip-bank | `494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922` |
| deck-over-ground | `77addcbc9c054f85e1a14f916bd95af75d02f105c54ad8e56a3a25716b0d44cc` |
| wade-only-pool | `a735ea5a41a1adb58666219b053197ed457b3c7edd830e18f29c7c5c8a1a1635` |
| rebased-origin | `e5c86385e4141a289d41d8efbb66766e5ac7c942f825521fff91dede8e9b4eee` |
| mixed-legacy-contact | `94fc16f1dee6cfa4c99db7ce490443271a0eb2fe3d9f0906312a836c5cd9a856` |
| stair-accepted-refused | `62a061314987a1b8243f9eb8da639cd0239ec200f8f62f11e69e62f5db20959a` |
| multi-layer-row-bands | `ae89cde4394002043b200b5e25e4eb0ef778ced4438cd7e985bde8dd96de811a` |

### Boundaries

All builds and tests used build-slot synchronously, one at a time. Cost and timing artifact
variables were unset. No timing, load, stress, allocation, world-bake or consumer proof ran.
The full solution build, full test selection, format verification, living API documentation and
staged changelog finish belong to Task 7. The focused builds required no PublicAPI file updates.
