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

## Task 2. Exact selected views and adversarial boundaries

Selected Bepu query views now expose the optional reuse capability. The owner opens the session
against that exact receiver and its existing immutable StaticQueryExclusions. The registry is
scoped by thread and receiver. Equal-looking views, empty-exclusion views and the unrestricted
world have independent tables. A nested opening on the same receiver declines without replacing
its session, while a different receiver can open its own session. No opening lease is retained.

The session authenticates selected-view disposal when opened, on current checks, on reset and
on later lookup. Disposal still clears retention and removes only its own registry entry when
a stale basis or disposed owner makes the current check throw. The exact key, memo and query
core from Task 1 required no corrections. Existing validation order and the consumed backend
join mode remain unchanged. No limit or tolerance changed.

### Behavioral red and focused commands

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SelectedViewDeclaresExactFootprintReuseCapability'
```

Exit 1, one expected missing-capability assertion after compilation succeeded. The actual type
was BepuPhysicsWorld.QueryView and it did not implement IPhysicsSupportFootprintReuseSource.
The real nonempty exclusion snapshot contains raw StaticHandle value `0`. An initial invocation
also exited 1 because the fixture incorrectly expected raw handle `1`. That assertion was
corrected to `0` before the meaningful capability red and before production edits.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseViewTests|FullyQualifiedName~SupportFootprintProofReuseKeyTests|FullyQualifiedName~SupportFootprintProofReuseSafetyTests|FullyQualifiedName~RemovedAndReusedBackendStorageCannotReviveOldGeometry|FullyQualifiedName~WrongThreadAndDisposedViewRefuseCurrentChecksBeforeBackendWork'
```

Exit 1, 66 passed and one fixture failure. The intended 66-member box fixture also admitted
its 66 bottom faces, producing 132 raw members. Single-face meshes replaced those boxes to
pin exactly 66 members without changing any production bound. Quaternion and contact-band
controls were strengthened with literal Unsupported assertions, invalid filters with literal
ArgumentException assertions, and wrong-thread assertions were captured back onto the test thread.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseViewTests|FullyQualifiedName~SupportFootprintProofReuseKeyTests|FullyQualifiedName~SupportFootprintProofReuseSafetyTests|FullyQualifiedName~RemovedAndReusedBackendStorageCannotReviveOldGeometry|FullyQualifiedName~WrongThreadAndDisposedViewRefuseCurrentChecksBeforeBackendWork' --logger 'console;verbosity=detailed'
```

Exit 0, 67 passed. Self-review then added selected-view reference read/seed bypass, signed-zero
changes on each origin component, input-array corruption after retention, raw-pair distinctness
and reduced-work refusal even with an existing ordinary-policy success. Final verification also
included all 16 accepted Task 1 tests.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseViewTests|FullyQualifiedName~SupportFootprintProofReuseKeyTests|FullyQualifiedName~SupportFootprintProofReuseSafetyTests|FullyQualifiedName~SupportFootprintProofReuseTests|FullyQualifiedName~RemovedAndReusedBackendStorageCannotReviveOldGeometry|FullyQualifiedName~WrongThreadAndDisposedViewRefuseCurrentChecksBeforeBackendWork' --logger 'console;verbosity=detailed'
```

Exit 0, 86 passed, zero failures or skips. The test command compiled its Release dependency
graph with no warnings. Staged self-review then strengthened post-wrong-thread usability,
exact stale-disposal exception types and cached short-span refusals, and guaranteed early
assertion failures release their opening leases. The same final command ran again and exited
0 with 86 passed. Every repeat followed a fixture, assertion or coverage change.

### Every request-key near-miss

Each row changes only the named request component. Numeric changes use an adjacent represented
value or a zero-sign bit. Every changed key is unequal under production equality. Results and
both entire caller arrays match an uncached query with identical operands, including sentinel
tails and all named witness, proposal and join fields. Serializer output is explicit
little-endian field bits, never managed struct padding. Every row observes zero reuse hits.
Invalid values retain their historical refusal or exception rather than widening the domain.

| Component | Hits | Misses | Volumes | Stores | Observed outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| `axis-proposal.distance` | 0 | 1 | 1 | 1 | Complete |
| `axis-proposal.length` | 0 | 1 | 1 | 1 | Complete |
| `axis-proposal.qw` | 0 | 1 | 0 | 0 | Unsupported |
| `axis-proposal.qx` | 0 | 1 | 0 | 0 | Unsupported |
| `axis-proposal.qy` | 0 | 1 | 0 | 0 | Unsupported |
| `axis-proposal.qz` | 0 | 1 | 0 | 0 | Unsupported |
| `axis-proposal.radius` | 0 | 1 | 1 | 1 | Complete |
| `axis-proposal.x` | 0 | 1 | 1 | 1 | Complete |
| `axis-proposal.y` | 0 | 1 | 1 | 1 | Complete |
| `axis-proposal.z` | 0 | 1 | 1 | 1 | Complete |
| `axis.signed-zero` | 0 | 1 | 1 | 1 | Complete |
| `axis.x` | 0 | 1 | 1 | 1 | Complete |
| `axis.z` | 0 | 1 | 1 | 1 | Complete |
| `band.lower` | 0 | 1 | 1 | 1 | Complete |
| `band.upper` | 0 | 1 | 1 | 1 | Complete |
| `contact-band.above` | 0 | 1 | 0 | 0 | Unsupported |
| `contact-band.below` | 0 | 1 | 0 | 0 | Unsupported |
| `filter.cull` | 0 | 1 | 1 | 1 | Complete |
| `filter.dynamics` | 0 | 1 | 1 | 1 | Complete |
| `filter.invalid-mobility` | 0 | 0 | 0 | 0 | ArgumentException |
| `filter.layers` | 0 | 0 | 0 | 0 | ArgumentException |
| `filter.mobility` | 0 | 1 | 1 | 1 | Complete |
| `foot-proposal.distance` | 0 | 1 | 1 | 1 | Complete |
| `foot-proposal.length` | 0 | 1 | 1 | 1 | Complete |
| `foot-proposal.qw` | 0 | 1 | 0 | 0 | Unsupported |
| `foot-proposal.qx` | 0 | 1 | 0 | 0 | Unsupported |
| `foot-proposal.qy` | 0 | 1 | 0 | 0 | Unsupported |
| `foot-proposal.qz` | 0 | 1 | 0 | 0 | Unsupported |
| `foot-proposal.radius` | 0 | 1 | 1 | 1 | Complete |
| `foot-proposal.x` | 0 | 1 | 1 | 1 | Complete |
| `foot-proposal.y` | 0 | 1 | 1 | 1 | Complete |
| `foot-proposal.z` | 0 | 1 | 1 | 1 | Complete |
| `joins` | 0 | 1 | 1 | 1 | Complete |
| `radius` | 0 | 1 | 1 | 1 | Complete |
| `slope` | 0 | 1 | 1 | 1 | Complete |

The two invalid-filter rows throw before lookup and therefore record zero memo misses. The
ten invalid quaternion/contact-band rows record a lookup miss then Unsupported, with no
volume evaluation or publication. Every other row records one real volume and one new store.

### Authenticated-basis near-misses

| Component | Observed boundary |
| --- | --- |
| Receiver object | Querying another receiver while a session is active records zero hits and equals its own uncached payload. |
| Source world | Foreign leases reject opening, current checks and queries with ArgumentException. |
| Selected view and exclusions | Unrestricted, empty, nonempty, changed and distinct equal-looking views never share entries. Editing the caller's exclusion array after construction does not change the snapshot. |
| Installed statics and generations | An unrelated installation and removed/reused Bepu storage invalidate the old basis. The fallback matches the complete uncached result and both entire caller spans under the new lease, with zero reuse hits, misses or stores. Old results remain invalid. |
| Origin X, Y and Z | Real one-bit rebases also advance the query generation. Each fallback matches the complete uncached result and both entire caller spans, with zero reuse hits, misses or stores. These live rows do not isolate origin authentication. |
| Origin zero signs | Signed-zero rebases on X, Y and Z also advance the query generation. Their complete fallback results and caller spans match uncached execution. Independent bit comparisons are synthetic session checks described below. |
| Profile identity | A new profile token releases the prior table and forces a real volume, one miss and zero hits. |
| Raw source identity | A new source token independently releases the table and forces a real volume, one miss and zero hits. Hold/source token contents remain Tasks 3 and 4. |
| View and source lifetime | Disposal rejects later current checks and view opening/query access. Session disposal releases all retained keys and bytes even when authentication throws. |
| Thread | Foreign-thread current, reset, dispose, open and query attempts reject before outputs or table mutation. The original session and retained payload remain usable. |
| Reference evaluation | Selected-view reference calls neither read nor seed the table. A later ordinary query performs real evaluation. |
| Diagnostic work policy | A collector limited to 32 events bypasses lookup and publication, including when an ordinary success already exists. A restored ordinary policy with no retained success performs real evaluation. |

`HashCollisionRequiresFullEquality` uses a constant-hash test comparer with production key
equality. It also forces distinct keys into the same production table slot at capacity one.
The other key misses without touching either output span. The original key still returns its
complete exact payload. No test-only production comparer or public tuning surface was added.

`SessionOriginComparisonsRejectOneBitWithEveryOtherBasisComponentFixed` isolates each of
the six origin-bit changes at the session boundary. A wrapper lease changes only its origin,
keeping the real source and generation. The private Matches predicate accepts an equal-origin
wrapper and rejects each changed-origin wrapper. Public entry points reject both wrappers,
so this is a defensive comparison proof, not a reachable equal-generation public query.
A shallow, unregistered session copy then changes only one captured origin field. Every
other session field, including the memo, domain, receiver, exclusions, thread and generations,
is checked equal before CurrentBasis rejects that copy. The original session remains current.
No owner, real lease, request operand or retained payload is mutated.

`RebaseCannotExposeAnotherOriginAtTheSameLeaseGeneration` proves real rebases are refused
under the opening lease without changing the origin or generation. After that lease closes,
each one-bit or signed-zero rebase advances the next lease generation by exactly one.
An unchanged-origin rebase does too. Rebase is the sole production origin writer, and its
checked generation advance precedes the origin assignment. Production therefore cannot expose
a changed origin at an equal generation through these supported paths.

Every live mutation fallback is captured before the stale session is disposed. The ordinary
uncached oracle then runs with no registered session under the same new lease and receiver.
Result metadata, counts, explicit member and witness bits, proposal verdicts, join words and
both complete caller arrays, including sentinel tails, are compared. The fallback alone
records one volume and two proposals, with no reuse lookup or publication.

`ForwardedQueryConsumesJoinModeExactlyOnce` first proves that the uncached full and
certification-only join matrices differ on a real small-crate scene. Warm-table cases seed
both modes in the selected view. An armed forwarding call returns the exact certification
payload from its inserted query and the exact full payload from its forwarded query, with
two hits and no misses, evaluations or stores. If the inserted invalid-filter query throws,
it still consumes the arm. Forwarding returns the full payload with one hit and no other work.
Cold-table cases begin with no retained keys. Successful insertion and forwarding match both
uncached payloads with zero hits, two misses, two volumes, four proposals and two stores.
The separate cold inserted-throw case returns the uncached full payload with zero hits, one
miss, one volume, two proposals and one store. The throwing insertion has no payload or
lookup. Direct armed and unarmed calls retain their respective complete matrices afterward.

### Owned capacity and refusal evidence

`JoinWordBoundaryAndOwnedCapacityRespectPayloadBudget` installs 65 coincident floor mesh
statics plus an independent roof mesh. All 66 raw pairs remain distinct. Its row stride is two
words. The 63-to-64 and 64-to-63 joins are nonzero across the word boundary. Both roof join
words are zero. Hits copy all 132 join words and every member field exactly, leaving caller
tails untouched after the input arrays have been overwritten.

The fixed 512-entry array retains 106,496 bytes, including spare inline key/value and proposal
slots. One entry is 208 bytes. Each retained member is 184 bytes, including both witness slots.
The 66-member payload retains 12,144 member bytes and 1,056 join bytes, totaling 13,200 bytes.
Only exact owned ranges are allocated from spare caller capacities of 256 members and 1,024
join words. Retained element capacities are accounted, managed object headers are not allocation
measurements.

| Injected payload budget | Retained keys | Retained bytes | Outcome |
| ---: | ---: | ---: | --- |
| 0 | 0 | 0 | Every publication declines. |
| 119,695 | 0 | 0 | One byte below the first full retained capacity declines. |
| 119,696 | 1 | 119,696 | Exact first-entry capacity succeeds, further entries decline. |
| 2,097,152 | 150 | 2,086,496 | Entry 151 would retain 2,099,696 bytes and declines. |

Zero-key capacity retains nothing. A complete-empty payload fills exactly 512 keys using only
the fixed table bytes. Key 513 declines and key 512 remains readable. Negative capacities,
513 keys, negative bytes and 2 MiB plus one byte reject at the internal constructor. Every
fallback preserves the uncached full result. All observed per-session peaks remain within
512 keys and 2 MiB. Final live keys and bytes are zero.

Refusal coverage includes every enum refusal at the memo boundary and real Unsupported,
Ambiguous, raw-member CapacityExceeded and short-member CapacityExceeded queries. Short joins,
null probes and invalid filters preserve their original exception types. Every refusal retains
RequiredElements and both sentinel arrays, with zero hits, stores or retained payload. Reduced
curved-work exhaustion is sticky at exactly 32 events, leaves both spans untouched and cannot
seed a later ordinary-policy success. No exception, refusal or reduced-policy result is cached.

### BR-R2 degree-1 pinned baselines

```bash
build-slot --label bake-b-design --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationBaselineTests' --logger 'console;verbosity=detailed'
```

Exit 0, all seven passed. These existing serial fixtures use null execution, whose effective
degree is 1. Actual normalized complete-file hashes equal every unchanged pin.

| Fixture | Observed normalized SHA-256 |
| --- | --- |
| deck-over-ground | `77addcbc9c054f85e1a14f916bd95af75d02f105c54ad8e56a3a25716b0d44cc` |
| low-lip-bank | `494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922` |
| mixed-legacy-contact | `94fc16f1dee6cfa4c99db7ce490443271a0eb2fe3d9f0906312a836c5cd9a856` |
| multi-layer-row-bands | `ae89cde4394002043b200b5e25e4eb0ef778ced4438cd7e985bde8dd96de811a` |
| rebased-origin | `e5c86385e4141a289d41d8efbb66766e5ac7c942f825521fff91dede8e9b4eee` |
| stair-accepted-refused | `62a061314987a1b8243f9eb8da639cd0239ec200f8f62f11e69e62f5db20959a` |
| wade-only-pool | `a735ea5a41a1adb58666219b053197ed457b3c7edd830e18f29c7c5c8a1a1635` |

### Fix round 1 verification

```bash
build-slot --heavy --label bake-b-fix1-green -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseBasisTests|FullyQualifiedName~SupportFootprintProofReuseViewTests|FullyQualifiedName~SupportFootprintProofReuseKeyTests|FullyQualifiedName~SupportFootprintProofReuseSafetyTests|FullyQualifiedName~SupportFootprintProofReuseTests|FullyQualifiedName~RemovedAndReusedBackendStorageCannotReviveOldGeometry|FullyQualifiedName~WrongThreadAndDisposedViewRefuseCurrentChecksBeforeBackendWork' --logger 'console;verbosity=detailed'
build-slot --label bake-b-fix1-baselines --heavy -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationBaselineTests' --logger 'console;verbosity=detailed'
```

Both exited 0. The first passed all 102 tests, including every new/changed control, the
Task 2 green selection and the accepted Task 1 tests. The second passed all seven degree-1
baselines with the unchanged normalized complete-file hashes above. Neither run had failures,
skips, compiler warnings or errors. Logs are task-2-fix1-green.log and task-2-fix1-baselines.log
under /tmp/khaozengine-bake-proof-reuse.

Before final verification, separate deliberate mutation probes removed only the lease-origin
comparisons, then only the owner-origin comparisons. Each exited 1 with six expected
Assert.False failures, independently proving that both guards detect the synthetic origin-only
differences. Forcing certification joins on reuse-enabled evaluator misses exited 1 with four
exact-payload failures, including both cold forwarding cases. The original production files
were restored byte for byte before the green runs. No production defect or production fix
resulted from this round. The final diff contains only tests and this evidence correction.

### Self-review and scope

The source diff is confined to selected-view capability forwarding, exact receiver/exclusion
session selection and view-disposal authentication. The query core, exact key and payload memo
are unchanged. Fresh selected-view results use the current receiver and current lease, and
reject the original cross-lease result. No old metadata is relabelled.

Both named Task 2 Review Focus items pass. The helper interfaces required by later tasks are
available in SupportFootprintProofReuseFixture. Task 5 still owns the broader geometry matrix.
Task 7 owns full solution build, full suite, format, living API docs and staged changelog finish.
Movement does not open these sessions yet, so the pinned baseline run remains a regression
control, not proof of completed-lane savings or full optimized-bake byte equivalence.

No unresolved Task 2 concern. No version, tolerance, band, work ceiling or public cache-limit
knob changed. Cost/timing artifact environment names were absent. All build/test commands ran
synchronously through build-slot, one at a time. No full suite, benchmark, load, stress, consumer,
world bake, workflow, release or tag ran. No branch switch, push, merge, GitHub write or agent
was dispatched. Logs are under `/tmp/khaozengine-bake-proof-reuse/task-2-*.log`.

## Task 3. Original same-layer source groups

The MovementQueryLease bridge borrows its existing current physics lease through the exact
selected view. It checks the producer pin as well as the session basis. No acquisition or pin
was added. A source marker surrounds each accepted node's same-layer direction loop, opens
lazily inside the first required Step lease after prechecks and the parallel cutoff, and
disposes on that executing thread. Constructor pins, admission and Stair proofs open no session.
Each new source resets a new session. No mutable session moves from setup into a worker.

ContactNavigationSources records capture X/Z, the original sample ordinal and raw height bits
before serial or parallel admission compacts its buffers. It binds the raw NavSpace before
WithRestingHeights. Layer remains a separate address component. Missing, mismatched or ambiguous
mapping disables grouping for that node. Anchors and source addresses have separate owners.

The immutable policy is captured on the caller and passed explicitly to workers and proofs.
Footprints default on for capable receivers. InitialHolds remains false. The two internal disable
hooks and the policy never enter NavBakeIdentity or serialized data. Reference evaluation disables
reuse and establishes a worker-local reference scope, restored in finally.

### Behavioral red and focused commands

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~SameLayerSourceReusesFootprintsAcrossMovementLeases'
```

Exit 1. The unchanged low-lip-bank hash passed first. The real bake then failed because
FootprintReuseHits was zero. This preceded every production grouping change.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseTests|FullyQualifiedName~ParallelContactBakeFailureTests|FullyQualifiedName~WorkerDiagnosticsScopeRestoresEveryPriorThreadValue' --logger 'console;verbosity=detailed'
```

The first invocation exited 1, with 25 passed and nine failed. All pinned hashes passed.
Seven new assertions incorrectly compared parallel setup lease counts with serial setup counts.
The off/on counters now use the same degree, preserving the existing extra worker setup pins.
The other two assertions expected an injected InvalidOperationException to escape, while the
movement API intentionally converts it to Stale. The fixture now throws a distinct producer
exception and compares that original failure. No production change was made for those nine
incorrect test expectations.

The next invocation, after adding actual raw-ordinal, bypass, phase and reference controls,
exited 0 with 41 passed. The callback tests compare ordered real analytic, binding and medium
requests and outcomes, Find observations, binding counts, movement results and injected failures.
The fixture wraps and forwards the real ordinary producer, without changing its side effects.
Both analytic and physics support wins are asserted. No stable-provider opt-in is used.

### Self-review cleanup red

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~StaleSourceBasisKeepsTheOriginalAuthenticationFailure'
```

Exit 1. A real static installation between movement leases made the existing movement acquisition
refuse. Source disposal then replaced that refusal with the stale session's cleanup exception.
The wrapper now preserves the proof's original exception and attaches a secondary disposal failure
under Data["SupportFootprintReuseCleanupFailure"]. The session still releases all payloads.

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~StaleSourceBasisKeepsTheOriginalAuthenticationFailure|FullyQualifiedName~FootprintHitStillRunsAnalyticBindingAndMediumRoutes|FullyQualifiedName~SourceScopeNeverNestsAnExplicitPhysicsLease'
```

Exit 0, six passed. The stale-source test now observes the exact original authentication
message, the secondary cleanup exception and zero retained keys and bytes.

### Final focused verification and BR-R2

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseTests|FullyQualifiedName~ContactNavigationBaselineTests|FullyQualifiedName~ParallelContactBakeTests|FullyQualifiedName~ParallelContactBakeFailureTests|FullyQualifiedName~ContactNavigationAdmissionTests|FullyQualifiedName~ContactNavigationBasisTests|FullyQualifiedName~ContactNavigationPlacementAdmissionTests|FullyQualifiedName~ContactNavigationTraversalTests|FullyQualifiedName~ContactNavigationWaterTests|FullyQualifiedName~WorkerDiagnosticsScopeRestoresEveryPriorThreadValue' --logger 'console;verbosity=detailed'
```

Exit 0, initially 108 passed. After making the trace collector thread-safe and adding an
explicit parallel source-failure cleanup test, the final invocation exited 0 with 109 passed,
zero failed or skipped and no compiler warnings or errors. Every rerun followed a change.
This is a focused Movement selection, not the full suite reserved for Task 7.

All seven existing degree-1 baselines passed. Fourteen new rows additionally compare actual
reuse-off and footprint-only files at identical engine identity, all graph semantics and each
unchanged normalized hash at degrees 1 and 3. The existing parallel matrix also passed each
fixture at degrees 1, 2, 3 and 8, plus degree 16 for multi-layer-row-bands.

| Fixture | Observed normalized SHA-256 at both 1 and 3 |
| --- | --- |
| low-lip-bank | `494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922` |
| deck-over-ground | `77addcbc9c054f85e1a14f916bd95af75d02f105c54ad8e56a3a25716b0d44cc` |
| wade-only-pool | `a735ea5a41a1adb58666219b053197ed457b3c7edd830e18f29c7c5c8a1a1635` |
| rebased-origin | `e5c86385e4141a289d41d8efbb66766e5ac7c942f825521fff91dede8e9b4eee` |
| mixed-legacy-contact | `94fc16f1dee6cfa4c99db7ce490443271a0eb2fe3d9f0906312a836c5cd9a856` |
| stair-accepted-refused | `62a061314987a1b8243f9eb8da639cd0239ec200f8f62f11e69e62f5db20959a` |
| multi-layer-row-bands | `ae89cde4394002043b200b5e25e4eb0ef778ced4438cd7e985bde8dd96de811a` |

### Observed footprint work and retention

Each row below was observed independently at degree 1 and degree 3, with identical counters
at both degrees. Volumes include admission and Stair work. Misses and stores cover eligible
source sessions only. Find, physics lease, movement lease and producer pin counts match
reuse-off at the same degree. Footprint hits remove actual Volumes and preserve every Hold.
Every row has zero budget skips and zero InitialHoldReuseHits.

| Fixture | Off Volumes | On Volumes | Hits | Misses | Stores | Peak keys | Peak bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| low-lip-bank | 8,108 | 5,722 | 2,386 | 5,386 | 5,386 | 145 | 228,032 |
| deck-over-ground | 16,024 | 8,080 | 7,944 | 7,408 | 7,408 | 77 | 136,064 |
| wade-only-pool | 24,752 | 12,444 | 12,308 | 11,868 | 11,868 | 149 | 180,608 |
| rebased-origin | 8,108 | 4,168 | 3,940 | 3,832 | 3,832 | 89 | 175,040 |
| mixed-legacy-contact | 8,012 | 4,040 | 3,972 | 3,704 | 3,704 | 77 | 136,064 |
| stair-accepted-refused | 4,116 | 2,857 | 1,259 | 1,584 | 1,584 | 330 | 275,840 |
| multi-layer-row-bands | 10,379 | 5,093 | 5,286 | 2,775 | 2,775 | 146 | 186,368 |

The seven fixtures avoid 37,095 of 79,499 actual volume evaluations, leaving 42,404, at each
reported degree. These actual footprint-only counts meet the seven projected targets exactly.
There is no shortfall to explain. No combined-mode, wide-lip-576, curved-savings, elapsed-time,
speedup or allocation claim is made here.

The maximum retained session was 330 keys and 275,840 bytes. Peaks use maximum per session,
including across workers. Every completed source, band, phase and build reports zero live keys
and bytes. The test-only saturated table uses the existing internal one-key constructor,
retains that key's hits and falls back on other keys with nonzero budget skips. Its bytes and
provider trace equal reuse-off. The unsupported forwarding view and forced bypass perform
zero hits and the full original volume count. No public capacity knob was added.

### Source identity, lifetime and failure controls

EqualRestingAnchorsKeepOriginalRawSourceAndLayer constructs distinct raw slots, columns and
layers with the same substituted resting height. Complete addresses and reference-identity
domains remain distinct, including skipped ordinals before admitted samples. The parallel
case replays compacted admission metadata. A real floor-plus-slab bake additionally proves,
at degrees 1 and 3, that every observed source retains original ordinal 1 after raw slot 0
fails headroom. The bit-equal raw height comes from that original captured slot.

Missing and ambiguous source maps decline lookup. A source's next domain and an independent
profile both evaluate cold. Source opening adds no physics acquisition, movement lease or
producer pin. Non-finite endpoint prechecks do no new work. An unrelated outer explicit
physics lease retains its original non-nesting refusal. The bridge independently rejects a
stale producer pin, even while the physics session itself is current.

Admission and Stair worker callbacks observe no footprint reuse or stored payload. Completed
edge bands have zero live retention. Real producer exceptions and the parallel cutoff/failure
selection tests preserve original outcomes and failure order. The added parallel source fault
happens after actual stores and hits, releases every retained payload, and leaves the context
usable for a subsequent ordinary build. Reference workers see the reference switch in every
phase and leave no retained reuse data. Caller reference and diagnostics scopes restore.

### Scope and handoff

No Task 3 concern remains. Initial Hold reuse, its public stable-provider assertion and the
remaining lane-wide acceptance work belong to later tasks. Full solution build, full suite,
format verification, living API docs and the staged changelog finish remain with Task 7.
The Euclidean arrival rule, every tolerance, band, work ceiling, capacity, raw physics ID,
navigation identity and baked schema are unchanged. No golden was changed.

All build/test commands ran synchronously through build-slot, one at a time. Logs are under
/tmp/khaozengine-bake-proof-reuse/task-3-*.log. No cost or timing harness, full suite, stress,
load, consumer, world bake, workflow, pack, release or tag ran. No branch switch, push, merge,
GitHub write or agent/reviewer dispatch occurred.

## Task 4. Stable-provider initial Hold reuse

The public init-only `NavBakeExecution.UseStableContactHoldReuse` defaults false. Serial
contact callers opt in to the full BB-8 stable-provider obligation. Parallel contact builds
already have that obligation. Legacy profiles ignore the property. Null and degree-1 builds
without it retain their original callbacks, including call-count-sensitive values and refusals.
Neither the property nor the captured reuse policy enters navigation identity or baked bytes.

Each original same-layer source owns one successful numeric Hold memo. Profile and provider
snapshot tokens bind immutable composition, footprint, registration, selected view and producer
identities. The key compares all state and effective tuning fields by exact bits, logical scope,
frame, lease basis, original raw source address, dt and step budget. A small internal MoveState
accessor exposes private speed-scale storage bits, since the public addition can erase a signed
zero or round a small bias. The state itself is copied by assignment, including private storage.
No Selection, support, lease, binding or continuity is retained.

Every edge keeps finite and endpoint footprint checks first. Both real and reused initial Holds
acquire and dispose the normal current movement lease. Reused Holds authenticate the snapshot,
including bit-exact water extent and frame, even for immediate arrival. Successful real Holds
are remembered only after the unchanged Accepted and Near checks. Travel still begins at slice
one and clears Selection before every real controller call. Admission and Stair Holds are never
seeded or shared. EdgeSteps counts actual edge controller calls, including Stair but excluding
admission. Hold reuse works independently of the footprint reuse capability.

The serial snapshot owner is checked at final publication. Parallel checks retain the existing
joined-worker and failure-cutoff flow. Existing Task 3 worker and scheduler policy plumbing
already carries eligibility, so it needed no further edit. The prior footprint-only baseline
test now explicitly disables Hold reuse in both modes. Diagnostic equality compares stable
serial against parallel and still compares every counter, including both new counters.

### Behavioral reds and test corrections

The required parallel red ran before the API and recurrence changed. After fixing one missing
namespace import, the file equality passed and the positive InitialHoldReuseHits assertion
failed because the count was zero. Exit 1.

The first focused selection had 32 passed and two failed. A rejected-Hold fixture at 2 m was
rejected by the endpoint footprint before any Hold. At 0.1 m the controller still granted support.
The final 0.8 m fixture passes footprint reach but fails the actual Hold, with two real Steps and
zero hits. A duplicate serial basis check introduced while adding the snapshot made a physics
lease diagnostic differ. The stable constructor now uses its full snapshot validation, while
the historical constructor retains its original basis check. The diagnostic test is green.

An additional key red independently changed 85 inputs. Exactly the private speed-scale signed
zero falsely matched through the rounded public SpeedScale property. The internal storage-bit
accessor fixed this red. The final key coverage also changes the source world, every scope
identity component and each frame-origin signed zero. No tolerance or identity was weakened.

The first broader selection had 137 passed and one failed when a call-count-sensitive analytic
fixture returned inconsistent positive support heights. The original path itself refuses that
surface as Unresolved. The final test compares both successful signed-zero callback traces and
that original refusal, including exception type, message and exact ordered trace. A focused
four-test trace and arrival selection passed after this test correction.

### Final focused verification and BR-R2

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationInitialHoldReuseTests|FullyQualifiedName~ContactNavigationProofReuseTests|FullyQualifiedName~ContactNavigationBaselineTests|FullyQualifiedName~ParallelContactBakeTests|FullyQualifiedName~ParallelContactBakeFailureTests|FullyQualifiedName~ContactNavigationAdmissionTests|FullyQualifiedName~ContactNavigationBasisTests|FullyQualifiedName~ContactNavigationPlacementAdmissionTests|FullyQualifiedName~ContactNavigationTraversalTests|FullyQualifiedName~ContactNavigationWaterTests' --logger 'console;verbosity=detailed'
```

Exit 0. All 139 tests passed, with zero failed or skipped and no compiler warnings or errors.
This selection includes the brief's Hold/provider selection, the prior footprint/source proofs,
all pinned baselines, parallel scheduling and failure proofs, admission, placement, traversal,
water and basis checks. It is not the full suite reserved for Task 7.

All fourteen explicit combined-mode rows passed at degrees 1 and 3. Degree 1 sets the new
assertion. Degree 3 uses its existing parallel provider contract. Each compares the actual file
to the same-degree real-Hold reference, then the unchanged normalized hash and fixture graph
semantics. These are the observed counters and hashes at both degrees.

| Fixture | Degrees | InitialHoldReuseHits | EdgeSteps | Real-Hold reference EdgeSteps | Normalized SHA-256 |
| --- | --- | ---: | ---: | ---: | --- |
| low-lip-bank | 1 and 3 | 328 | 3704 | 4032 | `494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922` |
| deck-over-ground | 1 and 3 | 656 | 7408 | 8064 | `77addcbc9c054f85e1a14f916bd95af75d02f105c54ad8e56a3a25716b0d44cc` |
| wade-only-pool | 1 and 3 | 548 | 11868 | 12416 | `a735ea5a41a1adb58666219b053197ed457b3c7edd830e18f29c7c5c8a1a1635` |
| rebased-origin | 1 and 3 | 328 | 3704 | 4032 | `e5c86385e4141a289d41d8efbb66766e5ac7c942f825521fff91dede8e9b4eee` |
| mixed-legacy-contact | 1 and 3 | 328 | 3704 | 4032 | `94fc16f1dee6cfa4c99db7ce490443271a0eb2fe3d9f0906312a836c5cd9a856` |
| stair-accepted-refused | 1 and 3 | 17 | 1669 | 1686 | `62a061314987a1b8243f9eb8da639cd0239ec200f8f62f11e69e62f5db20959a` |
| multi-layer-row-bands | 1 and 3 | 80 | 5996 | 6076 | `ae89cde4394002043b200b5e25e4eb0ef778ced4438cd7e985bde8dd96de811a` |

The seven fixtures save 2,285 real edge controller calls from 40,338, leaving 38,053, at each
reported degree. The multi-direction flat degree-2 red fixture observes 328 Hold hits and
3,704 actual EdgeSteps against 4,032 with real Holds. No timing or allocation claim is made.

The named immediate-arrival test authenticates a changed water generation even when no travel
lease follows. At budgets 1 and 2 it compares real and copied Holds at zero, exact 1 mm and its
adjacent floats, longer targets and a 3D point outside the Euclidean ball. It asserts independent
verdicts and preserves the consumed zero slice. Separate cases reject changed water semantic
identity, generation, extent and a signed-zero extent. Final-check controls mutate after the
last Hold hit and require the snapshot refusal in serial and after the last parallel link band.

Complete output-state checks include every numeric field, private storage, nonzero frame and
signed-zero feet. The next real Steps receive null Selection and perform fresh binding calls.
Rejected Holds never seed the memo. Unsupported footprint reuse can still yield Hold hits.
Reference evaluation continues to bypass both layers and retains zero live footprint storage.

### Scope, self-review and handoff

Self-review inspected the complete production and test diff. The memo contains only numeric
outputs and immutable key identities, resets per source, authenticates before any hit is counted
and leaves the recurrence, geometric protections and failure ordering intact. No unresolved
Task 4 concern remains. The two additional owned files are MoveState for the exact private-bit
accessor and the prior footprint-only test for explicit equivalent-mode coverage.

The final dependency build is performed by the focused Release test command. This repository
has no Movement PublicAPI files and its build requested no PublicAPI analyzer update. Full
solution build, full tests, format verification, living API documentation and staged release
history remain with Task 7. No golden, version, raw physics identity, tolerance, band, work
ceiling, storage limit or baked schema changed. No cost harness, wall-clock measurement, load,
stress, consumer launch, world bake, workflow, release, tag, push, merge, branch change, GitHub
write or agent/reviewer dispatch occurred. All test commands ran one at a time through build-slot.

## Task 5. Byte, geometry, arithmetic and scheduling certification

The test-only Off, Footprints and Combined modes disable only this lane. Combined explicitly
opts serial callers into stable providers. The original Baselines, BuildFixture and NormalizeFile
helpers and every pinned digest are unchanged. No production source, identity, schema, tolerance,
band, work ceiling or retention limit changed.

### Finite byte and order matrix

EveryPinnedFileAndDegreeMatchesUnchangedSerialBytes has 35 theory rows. Each checks all three
modes against the actual Off serial KENB file, every unchanged normalized hash, every profile's
complete graph bits and the existing semantic assertions. SourceAndDirectionOrderDoNotChangeBakeBytes
has another 35 rows. For each mode it first pins its independently reconstructed file, then calls
real BeginSource and TryEdge in reversed source order, reversed direction order and both orders
reversed. It places results in canonical graph slots and uses the production KENB writer.
No production order control exists. Legacy profiles remain in the complete mixed-profile file.

| Fixture | Total layer rows | Tested degrees in each mode |
| --- | ---: | --- |
| low-lip-bank | 8 | 1, 2, 3, 8, 9 |
| deck-over-ground | 16 | 1, 2, 3, 8, 17 |
| wade-only-pool | 12 | 1, 2, 3, 8, 13 |
| rebased-origin | 8 | 1, 2, 3, 8, 9 |
| mixed-legacy-contact | 8 | 1, 2, 3, 8, 9 |
| stair-accepted-refused | 6 | 1, 2, 3, 8, 7 |
| multi-layer-row-bands | 20 | 1, 2, 3, 8, 21 |

This is 105 named configurations, 105 full-file comparisons and 315 reordered full-file
comparisons. Every supplied degree is checked against the fixture's actual layer-row count.
No golden was regenerated. The final command results are recorded below.

### Protection, arithmetic and lifetime controls

The pool retains eight rejected level-support neighbours. Extended stairs retain four asymmetric
opposite-direction pairs and row bands retain 28. Those counts include same-layer directions as
well as Stair links. Seven new adversaries cover a forbidden corner touched by the circular disc,
a thin intervening wall, a lift blocked by a roof, next-raw/headroom, an unsupported hole, a lower
layer under a deck and changing support inside the disc. All three modes retain the original
accepted or refused result, including repeated requests in a source group.

Arithmetic coverage uses exact 0.001 m equality and adjacent binary32 values, a diagonal X/Z
residual and a mixed vertical residual outside the Euclidean ball, diagonal travel, budgets 1,
2 and 160, signed zero, near-zero inputs and positive/negative rebased origins. The direct real
Hold result is the independent initial-step control. Current and copied-Hold proofs keep its
verdict, including original refusals. Real output feet after centre/feet round trips compare by
bits across independent leases and payload hits. No arithmetic order or epsilon is changed.

WorkerPhaseThreadChangeCannotInheritAReuseSession constructs one worker on the caller, then
runs three phases on three distinct dedicated threads. Construction and ungrouped admission have
no session. Each grouped phase opens lazily, closes its session and starts another group cold.
Four distinct sessions stay on their two executing phase threads. Retention is zero at every
group and phase boundary. Reference policy reaches callbacks on all worker phases and seeds
neither layer. Existing forced-band, cutoff, partial setup, interrupted join and disposal
precedence checks now run in all three modes. ReuseFailureSelectionMatchesSerialUnderForcedBands
retains the lowest serial producer failure when the later band finishes first.

Full-payload tests use Task 2's explicit little-endian field serializer. They compare every
member field, both witness slots, proposal verdicts and join words on tilted cylinders,
capsule crowns, tiny faces, coincident statics/children, mesh edge joins, compound hulls,
certified hulls, independent roofs and represented captured stairs in split/compound and
zero/rebased frames. Reference payloads agree and bypass reuse. Old results remain invalid.
Four fresh-interval continuity cases compare direct joined rows and actual GroundSeat outputs
after hits, including gaps and a non-transitive strip. Old arena evidence stays invalid.
The existing independent neighbourhood and captured-stair oracle tests remain unchanged.

### Commands, corrections and results

All tests ran synchronously, one command at a time through build-slot. No full suite, timing,
load, stress, allocation, consumer, world bake, workflow, pack or release ran. Cost, timing and
capture environment-variable names were absent. Logs are in /tmp/khaozengine-bake-proof-reuse.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseBaselineTests|FullyQualifiedName~ContactNavigationProofReuseProtectionTests|FullyQualifiedName~ContactNavigationProofReuseScheduleTests|FullyQualifiedName~ParallelContactBakeScheduleTests|FullyQualifiedName~ParallelContactBakeFailureTests'
```

The first invocation exited 1 at compilation because the test project cannot access Navigation's
internal direction helpers. A test-owned canonical direction table corrected that compile error.
The second invocation exited 1 with 119 passed and 12 failed. All 70 byte/order rows and all 39
schedule/cleanup rows passed. Failures were new test assumptions, with no reuse/reference
mismatch. The pair count had omitted same-layer directions. The analytic floor filled the hole.
The flat scene did not require a lift beneath the roof. The arithmetic control used the wrong
centre/feet operation order and assumed every original rebased Hold succeeded. The controls
now use an actual real Hold, a binary-exact floor and the complete unchanged predicates.
The hole has an actual unsupported region and the roof obstructs a required step.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseProtectionTests'
```

Exit 0, 22 passed, zero failed or skipped after those fixture/assertion corrections.
The original broad selection then ran again after the final coverage and whitespace changes.
Final exit 0. All 131 passed, zero failed or skipped. The complete matrix, all 22 protection
rows and all 39 scheduling/cleanup rows passed after the final changes. No compiler warnings
or errors were emitted.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseGeometryTests|FullyQualifiedName~SupportNeighborhoodEquivalenceTests|FullyQualifiedName~CapturedStairExplicitSweepTests'
```

The first invocation exited 1 at compilation for the test's incorrect SupportMemberKey field
name. It was corrected to FeatureId. The second exited 1 with 73 passed and two failed because
the new rebased stair requests supplied absolute coordinates to a local-physics API. The
requests now subtract the captured physics origin, retaining the original geometric band and
the installed fixture's real yaw. There was no production defect and no correction commit.
Final exit 0. All 75 passed, zero failed or skipped. This includes all 17 new geometry and
continuity rows and the unchanged independent oracle selections. No compiler warnings or errors
were emitted.

A targeted dotnet format whitespace command through build-slot included only the eight owned
C# paths. Exit 0. Workspace loading emitted a generic warning, without a compiler diagnostic.
Full solution build, full test selection and full format verification remain with Task 7.

### Self-review and work boundary

The implementation author reviewed the complete diff. Canonical graph slots and the KENB writer
are real, session ownership is local and every old metadata or arena handle is rejected.
All edits are test or verification evidence. No production fix, weakened predicate, widened
bound, new golden, version change or attributed commit is included. Task 6 owns the independent
nine-fixture work targets and counter/retention disposition. No savings acceptance is claimed
from this certification step. The companion Task 6 record supplies that table after measurement.

### Task 5 self-review. Forced failures after real reuse hits

The older SmallStairScene forwarding view deliberately lacks the optional footprint-reuse
capability. Its three-mode controls still exercise fallback and the independently eligible
Hold optimization. Self-review added ReuseFailureSelectionMatchesSerialAfterRealHitsUnderForcedBands
on the real capable ordinary view, in Off, Footprints and Combined modes. Two workers own
separate original raw source addresses. Each seeds successful proofs first. The enabled modes
assert real footprint hits, and Combined also asserts Hold hits, before the failure query.
A fixed analytic query address fails per source. Completion gates and a join force the later
worker to publish its failure before the earlier source can throw. The selected failure still
matches the real serial proof, and all retained payload is released after both workers join.
The fault function's result depends only on the query address. Coordination changes order only.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseScheduleTests|FullyQualifiedName~ParallelContactBakeScheduleTests|FullyQualifiedName~ParallelContactBakeFailureTests'
```

Exit 0, 42 passed, zero failed or skipped. No compiler warnings or errors. This adds three
verified cases to the prior 131-test Movement selection, for 134 distinct certified Movement
cases. The byte/order matrix and geometry selections are unchanged. The self-review change is
folded into the Task 5 commit before the separate Task 6 commit. No production correction was
needed. Pending Task 6 paths remain unstaged during this amendment.

### Task 5 self-review. Roof isolates the intermediate lift

The roof adversary now has a narrow intermediate lip and a roof above that lip, with both
endpoints on unobstructed level floor. Independent endpoint Holds and footprint checks admit
both ends. The otherwise identical route without the roof passes. The roof route refuses in
Off, Footprints and Combined modes, proving that endpoint fit cannot replace lifted clearance.
The positive control adds one case to the seven requested adversaries. This strengthens the
original control, which could refuse at the destination's own headroom precheck.

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ProtectionCasesKeepSerialVerdict'
```

Exit 0, eight passed, zero failed or skipped. There is no production change or new golden.
The complete Task 5 proof set now has 135 distinct Movement cases and 75 geometry/oracle cases.
