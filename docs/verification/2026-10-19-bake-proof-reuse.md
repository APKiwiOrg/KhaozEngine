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

Each row below was observed independently on ARM64 at degree 1 and degree 3, with identical counters
at both degrees. The stair-wall arithmetic correction in Task 6 qualifies its absolute totals.
Volumes include admission and Stair work. Misses and stores cover eligible
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
snapshot tokens bind immutable composition, footprint, authored registration, its captured live revision,
selected view and producer identities. Runtime registration publications are immutable, but a changed root and
revision refuse the build. The key compares all state and effective tuning fields by exact bits, logical scope,
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

WorkerPhaseThreadChangeCannotInheritAReuseSession constructs one worker on the caller and keeps
the production phase runner. Its callbacks dispatch proof execution synchronously to test-owned
dedicated threads that remain alive across phases. One case runs Admission, Edges and Links on
the same thread. The other moves Links to a second thread. Thread object assertions prove the
chosen placement without assuming that terminated threads have distinct managed IDs. Construction
and ungrouped admission have no session. Each grouped phase opens lazily, starts cold, records
real footprint and initial-Hold hits, closes its session and starts another group cold. Four
distinct sessions stay on their chosen executing threads. Retention is zero at every group and
phase boundary. Reference policy reaches callbacks on all worker phases and seeds
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

## Task 6. Actual saved work and bounded retained payload

Status: DONE. No target shortfall and no production correction.

The seven pinned fixtures retain their original factories and hashes. WideLip uses the same
low-lip floor and 25 mm lip on the recorded 12 by 48 grid, X [-1.7, 1.3), Z [-6, 6), cell
0.25 m, radius 0.3 m, DryOnly, 160 edge steps and 1/30 second slices. Its capture provisions
576 cells and 2,304 layer cells for that larger grid. SingleFlat uses the existing LoadMatrix
floor/options, X/Z [-0.5, 0.5), cell 1 m, radius 0.2 m, half-height 0.75 m, no water,
64 edge steps and 1/30 second slices. No existing fixture or production bound changed.

There are 44 distinct fixture/degree configurations and three modes, or 132 unique builds per
work selection. The two target theories share immutable observations through one class fixture,
so they do not rebuild a named configuration. Only file bytes, counter values, observation counts
and Find-trace digests survive measurement. No physics object, producer, lease or session is retained
there. Each executing worker receives an explicitly installed Find observer. Every mode and degree
asserts that its observed record count equals its actual Find counter before hashing any trace.
Footprints and Off compare canonical multisets of every original numeric operand bit and result.
Degree 1 also compares their order. Combined records its own complete trace but legitimately omits
certified Hold Finds. Both analytic-support and physics-support wins retain the exact ordered live
callback trace in separate real-provider controls.

Review correction I1: the original Task 6 parallel digest comparisons observed no worker Finds.
Those comparisons were vacuous, while the independent counters and byte matrix remain valid.
The follow-up adds a separate thread-local opt-in worker hook, with no delegate allocation when
off. A count-only red found 12,200 Finds but zero observations at low-lip degrees 2, 3, 8 and 9.
After the hook, temporarily changing one observed parallel FeetY operand by one binary32 ULP
in Footprints failed the multiset equality at all four degrees. Each retained 12,200 observations,
the original counts and equal file bytes. Degree 1 passed. The mutation was removed before the
focused green run and is absent from the committed source.

### Actual footprint work against projections

All counts include admission and Stair evaluations. Off disables only this lane. Existing
FindMemoHits stay enabled and are not booked as new savings. The table uses actual Volumes,
which equal complete footprint evaluations in these fixtures. Combined and Footprints have
the same remaining geometry count. Their reductions are not added to Hold percentages.

These tables retain the original non-SIMD stair-wall observations. They are exact at every listed
degree on that arithmetic path, including degree 1. A raw Bepu wall sweep can instead end with zero
residual motion on Linux x64 SIMD, skipping four lifted/standing seat pairs. That path has 4,112 Off volumes,
2,853 remaining volumes in either reuse mode and the same exact 1,259 avoided volumes. Its stair
reduction is 30.62 percent. The seven-fixture denominator becomes 79,495 and remaining work becomes
42,400. The saved work stays exactly 37,095. See the arithmetic-path correction below.

| Fixture | Off evaluations | Projected avoided | Actual avoided | Footprints remaining | Combined remaining | Projected / actual reduction | Shortfall |
| --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| low-lip-bank | 8,108 | 2,386 | 2,386 | 5,722 | 5,722 | 29.43% / 29.43% | 0 |
| deck-over-ground | 16,024 | 7,944 | 7,944 | 8,080 | 8,080 | 49.58% / 49.58% | 0 |
| wade-only-pool | 24,752 | 12,308 | 12,308 | 12,444 | 12,444 | 49.73% / 49.73% | 0 |
| rebased-origin | 8,108 | 3,940 | 3,940 | 4,168 | 4,168 | 48.59% / 48.59% | 0 |
| mixed-legacy-contact | 8,012 | 3,972 | 3,972 | 4,040 | 4,040 | 49.58% / 49.58% | 0 |
| stair-accepted-refused | 4,116 | 1,259 | 1,259 | 2,857 | 2,857 | 30.59% / 30.59% | 0 |
| multi-layer-row-bands | 10,379 | 5,286 | 5,286 | 5,093 | 5,093 | 50.93% / 50.93% | 0 |
| wide-lip-576 | 69,532 | 29,234 | 29,234 | 40,298 | 40,298 | 42.04% / 42.04% | 0 |
| single-flat | 5 | 0 | 0 | 5 | 5 | 0.00% / 0.00% | 0 |
| Seven pinned totals | 79,499 | 37,095 | 37,095 | 42,404 | 42,404 | 46.66% / 46.66% | 0 |

### Real edge steps and saved initial Holds

EdgeSteps excludes admission Holds and includes Stair calls. Combined eliminates exactly one
real controller call for each certified redundant source Hold. The first Hold, admissions,
Stairs and every travel step remain real. These are controller work counts, not timing claims.

| Fixture | Off edge steps | Projected saved Holds | Actual saved Holds | Combined edge steps | Projected / actual reduction | Shortfall |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| low-lip-bank | 4,032 | 328 | 328 | 3,704 | 8.13% / 8.13% | 0 |
| deck-over-ground | 8,064 | 656 | 656 | 7,408 | 8.13% / 8.13% | 0 |
| wade-only-pool | 12,416 | 548 | 548 | 11,868 | 4.41% / 4.41% | 0 |
| rebased-origin | 4,032 | 328 | 328 | 3,704 | 8.13% / 8.13% | 0 |
| mixed-legacy-contact | 4,032 | 328 | 328 | 3,704 | 8.13% / 8.13% | 0 |
| stair-accepted-refused | 1,686 | 17 | 17 | 1,669 | 1.01% / 1.01% | 0 |
| multi-layer-row-bands | 6,076 | 80 | 80 | 5,996 | 1.32% / 1.32% | 0 |
| wide-lip-576 | 34,992 | 2,888 | 2,888 | 32,104 | 8.25% / 8.25% | 0 |
| single-flat | 0 | 0 | 0 | 0 | 0.00% / 0.00% | 0 |
| Seven pinned totals | 40,338 | 2,285 | 2,285 | 38,053 | 5.66% / 5.66% | 0 |

### Complete counter and retention disposition per fixture and degree

Every displayed counter and retention value was independently recorded at every listed degree on
the original ARM64 run. The records matched across degrees in those 132 unique builds. They are
historical observations, not a claim that raw sweep arithmetic is identical across CPU paths.
The corrected tests retain exact literal counts for all other fixtures and select the stair's
exact count model with an isolated two-sweep wall probe. Every parallel mode also compares its
work counters, Find trace multiset, routes, retention and complete file with a degree-1 build on
the same runtime. Source sessions end before the next source, band or phase, so none of these
counts depend on worker assignment. Setup pins and shared world-form builds are excluded from
that projection because their work can depend on the number of workers and publication order.

| Fixture | Recorded degrees |
| --- | --- |
| low-lip-bank | 1, 2, 3, 8, 9 |
| deck-over-ground | 1, 2, 3, 8, 17 |
| wade-only-pool | 1, 2, 3, 8, 13 |
| rebased-origin | 1, 2, 3, 8, 9 |
| mixed-legacy-contact | 1, 2, 3, 8, 9 |
| stair-accepted-refused | 1, 2, 3, 7, 8 |
| multi-layer-row-bands | 1, 2, 3, 8, 21 |
| wide-lip-576 | 1, 2, 3, 8, 49 |
| single-flat | 1, 2, 3, 8 |

Single-flat's degree greater than its one layer-row is 2, already present in the requested
positive-degree set. It runs once rather than duplicating that configuration.

| Fixture | Mode | Find | FindMemoHits | Volumes | Proposals | Hits | Misses | Stores | Budget skips | EdgeSteps | Hold hits | Peak keys | Peak bytes |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| low-lip-bank | Off | 12,200 | 4,092 | 8,108 | 16,216 | 0 | 0 | 0 | 0 | 4,032 | 0 | 0 | 0 |
| low-lip-bank | Footprints | 12,200 | 4,092 | 5,722 | 11,444 | 2,386 | 5,386 | 5,386 | 0 | 4,032 | 0 | 145 | 228,032 |
| low-lip-bank | Combined | 11,544 | 3,764 | 5,722 | 11,444 | 2,058 | 5,386 | 5,386 | 0 | 3,704 | 328 | 145 | 228,032 |
| deck-over-ground | Off | 24,208 | 8,184 | 16,024 | 32,048 | 0 | 0 | 0 | 0 | 8,064 | 0 | 0 | 0 |
| deck-over-ground | Footprints | 24,208 | 8,184 | 8,080 | 16,160 | 7,944 | 7,408 | 7,408 | 0 | 8,064 | 0 | 77 | 136,064 |
| deck-over-ground | Combined | 22,896 | 7,528 | 8,080 | 16,160 | 7,288 | 7,408 | 7,408 | 0 | 7,408 | 656 | 77 | 136,064 |
| wade-only-pool | Off | 37,276 | 12,524 | 24,752 | 49,504 | 0 | 0 | 0 | 0 | 12,416 | 0 | 0 | 0 |
| wade-only-pool | Footprints | 37,276 | 12,524 | 12,444 | 24,888 | 12,308 | 11,868 | 11,868 | 0 | 12,416 | 0 | 149 | 180,608 |
| wade-only-pool | Combined | 36,180 | 11,976 | 12,444 | 24,888 | 11,760 | 11,868 | 11,868 | 0 | 11,868 | 548 | 149 | 180,608 |
| rebased-origin | Off | 12,200 | 4,092 | 8,108 | 16,216 | 0 | 0 | 0 | 0 | 4,032 | 0 | 0 | 0 |
| rebased-origin | Footprints | 12,200 | 4,092 | 4,168 | 8,336 | 3,940 | 3,832 | 3,832 | 0 | 4,032 | 0 | 89 | 175,040 |
| rebased-origin | Combined | 11,544 | 3,764 | 4,168 | 8,336 | 3,612 | 3,832 | 3,832 | 0 | 3,704 | 328 | 89 | 175,040 |
| mixed-legacy-contact | Off | 12,104 | 4,092 | 8,012 | 16,024 | 0 | 0 | 0 | 0 | 4,032 | 0 | 0 | 0 |
| mixed-legacy-contact | Footprints | 12,104 | 4,092 | 4,040 | 8,080 | 3,972 | 3,704 | 3,704 | 0 | 4,032 | 0 | 77 | 136,064 |
| mixed-legacy-contact | Combined | 11,448 | 3,764 | 4,040 | 8,080 | 3,644 | 3,704 | 3,704 | 0 | 3,704 | 328 | 77 | 136,064 |
| stair-accepted-refused | Off | 5,827 | 1,711 | 4,116 | 8,232 | 0 | 0 | 0 | 0 | 1,686 | 0 | 0 | 0 |
| stair-accepted-refused | Footprints | 5,827 | 1,711 | 2,857 | 5,714 | 1,259 | 1,584 | 1,584 | 0 | 1,686 | 0 | 330 | 275,840 |
| stair-accepted-refused | Combined | 5,793 | 1,694 | 2,857 | 5,714 | 1,242 | 1,584 | 1,584 | 0 | 1,669 | 17 | 330 | 275,840 |
| multi-layer-row-bands | Off | 20,612 | 10,233 | 10,379 | 20,758 | 0 | 0 | 0 | 0 | 6,076 | 0 | 0 | 0 |
| multi-layer-row-bands | Footprints | 20,612 | 10,233 | 5,093 | 10,186 | 5,286 | 2,775 | 2,775 | 0 | 6,076 | 0 | 146 | 186,368 |
| multi-layer-row-bands | Combined | 20,452 | 10,153 | 5,093 | 10,186 | 5,206 | 2,775 | 2,775 | 0 | 5,996 | 80 | 146 | 186,368 |
| wide-lip-576 | Off | 104,984 | 35,452 | 69,532 | 139,064 | 0 | 0 | 0 | 0 | 34,992 | 0 | 0 | 0 |
| wide-lip-576 | Footprints | 104,984 | 35,452 | 40,298 | 80,596 | 29,234 | 37,882 | 37,882 | 0 | 34,992 | 0 | 145 | 245,696 |
| wide-lip-576 | Combined | 99,208 | 32,564 | 40,298 | 80,596 | 26,346 | 37,882 | 37,882 | 0 | 32,104 | 2,888 | 145 | 245,696 |
| single-flat | Off | 6 | 1 | 5 | 10 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| single-flat | Footprints | 6 | 1 | 5 | 10 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| single-flat | Combined | 6 | 1 | 5 | 10 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

All 132 rows have zero live keys and bytes after the build. All budget-skip counts are zero.
Every miss/store is shown above. Nontrivial source groups retain actual complete keys and
owned payloads, with no ineligible-domain or budget shortfall. The maximum is 330 keys and
275,840 retained bytes, below the unchanged 512-key and 2 MiB limits. Peaks are maxima per
session, including worker merges. They are not sums across workers. These are retained element
capacities including the fixed table, not managed allocation or process-memory measurements.
Find equals existing memo hits plus actual Volumes in Off. In both reuse modes, it equals
existing memo hits plus actual Volumes plus new footprint hits. Combined removes the redundant
Holds' own Finds and callbacks, while Footprints leaves those paths unchanged.

### Arithmetic-path correction after main CI

Run 39207206155 failed all five stair rows, including degree 1. The fifth failing degree was 1,
not another parallel degree. Each recorded Off Find 5,819, FindMemoHits 1,707 and Volumes 4,112.
Worker scheduling and a lost increment cannot explain that serial result. The volume counter
increments once in `SupportFootprintCollector.Candidates`, on the executing thread. Worker
recorders are private and merged after the join. Installed world-form work has a separate counter.

A single serial Linux x64 replay reproduced the failure. Disabling hardware intrinsics for the
same binary selected the other path and recorded 4,116, with no retries, sleeps or timing input.
Temporary Find and wall-attempt traces localized the difference to Z=1 at the stair's wall.
The instrumentation and throwaway diagnostic test were removed before the final verification.

The retained `StairWallSweeps` control constructs only that wall and runs two real shell sweeps.
It starts at feet (-0.06666669, 0.1875, 1), moves (0.016666668, 0), and lifts by 0.21, with
capsule radius 0.04 and half-height 0.15625. No navigation, footprint reuse or recorded bake count
participates in the control. Both sweeps remain blocked. Its observations are:

| Arithmetic path | First achieved X | Repeat achieved X | Lifted/standing pairs skipped |
| --- | ---: | ---: | ---: |
| ARM64 and Linux x64 with hardware intrinsics disabled | 0.0056669423 | 1.967419E-08 | 0 |
| Linux x64 SIMD | 0.0056670383 | 0 | 4 |

The first sweep leaves the next axis at -0.06099975 or -0.060999654, respectively. The symmetric
positive-X contact follows the same branch. `GroundCore.Tick.Try` returns before `GroundSeat.Resolve`
when achieved motion is zero. Otherwise the lifted and standing attempts each run the seat pass.
Four such pairs account for eight Finds, four interval memo hits and four actual volume
queries. Two belong to source reuse sessions and two to ungrouped Stair proofs. The corresponding
eight reads in the non-SIMD Find multiset occur at X magnitudes 0.06099973 and 0.06099975, Z=1,
with feet heights 0, 0.1875 and 0.375. All select the same walkable support. EdgeSteps and
GroundSubsteps are unchanged at 1,686 and 2,307 in Off. The baked files and verdicts are unchanged.

The control selects an exact model, not a numeric tolerance or CPU/OS allowlist. With `s` equal to
four for a zero repeat, otherwise zero, the original target remains exact as Volumes `4116 - s`,
Find `5827 - 2*s` and FindMemoHits `1711 - s`, at degree 1 and every parallel degree. All other
literal targets remain unchanged. Avoided volumes are now asserted equal to the recorded target,
strengthening the earlier lower-bound assertion. They still equal actual footprint hits exactly.

| Linux x64 SIMD mode, every degree | Find | FindMemoHits | Volumes | Hits | Misses | Stores | EdgeSteps | Hold hits |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Off | 5,819 | 1,707 | 4,112 | 0 | 0 | 0 | 1,686 | 0 |
| Footprints | 5,819 | 1,707 | 2,853 | 1,259 | 1,582 | 1,582 | 1,686 | 0 |
| Combined | 5,785 | 1,690 | 2,853 | 1,242 | 1,582 | 1,582 | 1,669 | 17 |

Reuse Volumes, misses and stores vary with raw sweep arithmetic, not worker scheduling. Hits do
not change in these two paths. Both extra eligible requests are unique complete entries, so they
add misses and stores without adding hits. The two ungrouped Stair requests cannot hit the source
memo. Each source session starts empty and ends before another source, so a worker claim order
cannot change the hit sequence within it. Thus the exact saved-work target remains valid rather
than requiring a relaxed lower bound. Combined reuses the same 17 initial Holds in both paths.
Hold evidence is source-local and its initial states are unchanged by the wall contact.

The new 44-row serial/parallel comparison covers all three modes, the wide-lip and single-flat
controls, and exact retention peaks in addition to the unchanged 512-key and 2 MiB bounds.
Single-flat still asserts exactly five volumes and no edge or Hold work. Wide-lip retains all
literal counts and exactly 29,234 footprint hits and 2,888 Hold hits. No production code, raw
identity, fixture used by a bake, file pin, roof or footprint protection, arrival rule or tolerance
changed. Verification commands and final exit codes are in the CI-fix section at the end.

### Curved certification work and refusal enforcement

All seven categories compare against one actual uncached evaluation. Two successive lease
hits add zero events and no historical work is replayed. Payload bits remain identical.
The category order is New, Sign, Split, Compile, RootCell, Refine, AdmissionCell.

| Fixture | One evaluation's seven event counts | Total actual events after seed plus two hits | Volumes / proposals | Hits | Peak keys / bytes | Final live keys / bytes |
| --- | --- | ---: | --- | ---: | --- | --- |
| capsule-crown | 166, 31, 0, 0, 0, 0, 0 | 197 | 1 / 2 | 2 | 1 / 106,688 | 0 / 0 |
| tilted-cylinder | 514, 82, 0, 0, 96, 36, 1 | 729 | 1 / 2 | 2 | 1 / 106,880 | 0 / 0 |

Source reset and disposal each report zero live keys and bytes. An independently repeated
work-limited cylinder refusal consumes 32 real events per request, 28 New and four Sign,
and remains Unsupported with untouched caller spans and zero stores or hits. With diagnostics
off, observer totals and counters are zero, but each collector still consumes exactly 32 events
and records WorkLimit. Its second independent lease query performs the work again. No curved
percentage target is invented. The cumulative production ceiling remains 1,048,576 events.

### Commands and evidence

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseWorkTests' --logger 'console;verbosity=normal'
```

Exit 0, 90 passed, zero failed or skipped. The normal logger suppresses successful test output,
so this invocation did not retain the counter table. Self-review then added explicit Off-mode
zero-counter and hit/miss/store bookkeeping assertions, and applied targeted whitespace changes.
The final changed selection retained the same console logger and added a TRX evidence logger.

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseWorkTests' --logger 'console;verbosity=normal' --logger 'trx;LogFileName=task-6-movement.trx' --results-directory /tmp/khaozengine-bake-proof-reuse
```

Exit 0, all 90 passed, zero failed or skipped. The 44 rows of each target theory and two real
provider-trace rows passed. The tables above are extracted from this run's actual output.
No failing target, substituted denominator, fixture replacement or tolerance adjustment exists.
There was no behavioral red or production change in Task 6. Tests preceded every measurement.

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SupportFootprintProofReuseWorkTests' --logger 'trx;LogFileName=task-6-game.trx' --results-directory /tmp/khaozengine-bake-proof-reuse
```

Exit 0, four passed, zero failed or skipped. Both curved successes, diagnostics-on refusal and
diagnostics-off refusal passed on the first invocation. Both dependency builds emitted no
compiler warnings or errors. The four-path targeted dotnet format whitespace command through
build-slot exited 0, with the same generic workspace-loading warning noted in Task 5.
Full format verification remains Task 7. The additional TRX loggers preserve deterministic
counters only. Incidental test-runner timestamps and elapsed time were not measurement inputs.

The Task 5 byte matrix remains green, 70 theory rows over all seven pins and five degrees in
all three modes, with both reverse orders and their combination. Its complete actual bytes,
hashes, semantic assertions and graph bits passed. Task 5 has 135 distinct Movement proofs
including the three additional real-hit forced-failure rows, and 75 geometry/oracle proofs.
No production source changed between those proofs and these work measurements.

### Disposition, self-review and concerns

The author reviewed all Task 6 code and extracted every counter from actual test output.
The test fixture cache contains immutable observations only. Production sessions are neither
shared across workers nor retained between measurements. All target and retention assertions
passed without changing a fixture, key, tolerance, band, ceiling, identity or storage limit.
No production defect was exposed and no fix commit was required. There is no shortfall or
unresolved concern for Tasks 5 and 6. No manual validation is needed for these headless proofs.

Candidate 3, certified flat-ground specialization, books zero savings because no complete
geometric and arithmetic certificate was implemented. Candidate 4, translated full-edge reuse,
also books zero. The full dependency and numeric translation certificates remain unproved.
Candidate 5, reverse-verdict sharing, remains rejected by four Stair and 28 row-band opposite
pairs with different verdicts. Candidate 6, reachable-only pruning, changes the requested raw
layers/nodes/exits and has no approved start-set or byte-equivalence certificate.

#1401 world-scale cost and #1334 timing acceptance remain open. These deterministic work counts
are not a world bake, wall-clock speedup or timing acceptance. Task 7 owns full solution build,
full test selection, full format verification, living API contracts and staged release history.
There was no load, stress, benchmark, allocation, consumer, world bake, workflow, pack, release,
tag, push, merge, branch change, GitHub comment or agent/reviewer dispatch. Every build/test ran
synchronously through build-slot, one at a time. Logs and extracted raw counters remain under
/tmp/khaozengine-bake-proof-reuse. Both SDD reports follow the existing ignored report lifecycle.

### Task 6 self-review. Independent refusal work with diagnostics off

Self-review strengthened the second refusal query with a fresh 32-event collector whose
consumed count is asserted zero before the new lease. Both requests must independently reach
32 and WorkLimit. A stale count from the first refusal can no longer satisfy the diagnostics-off
assertion. This changes no geometry, policy bound, retained payload or recorded savings.

```bash
build-slot --label bake-b-design -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~WorkLimitedRefusalConsumesItsRealWorkAgainAndDoesNotDependOnDiagnostics'
```

Exit 0, both diagnostics rows passed, zero failed or skipped, no compiler warnings or errors.
The four-case work selection and the strengthened two-case subset are green. The counter tables
remain the actual observations from the retained full selection. No production defect appeared.


## Parallel Find trace review correction

The count-only red, observer green and one-operand mutation red each used this selection,
with the respective labels `bake-b-trace-red`, `bake-b-trace-green` and
`bake-b-trace-mutation-red` through build-slot:

```bash
build-slot --label bake-b-trace-red -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~FootprintOnlyMeetsRecordedTargetsWithoutSkippingFinds&DisplayName~low-lip-bank'
```

The first exited 1 with four parallel count failures and one serial pass. The observer fix
exited 0 with five passes. The deliberate trace-operand mutation exited 1 with four multiset
failures and one serial pass. It changed one observed FeetY by one ULP only in each parallel
Footprints build. Counts and complete file comparisons passed before the trace failure.
The mutation is removed. The complete focused green command was:

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --filter 'FullyQualifiedName~ContactNavigationProofReuseWorkTests|FullyQualifiedName~ContactNavigationProofReuseBaselineTests|FullyQualifiedName~ParallelContactBakeTests' --logger 'trx;LogFileName=trace-fix-green.trx' --results-directory /tmp/khaozengine-bake-proof-reuse/task-7
```

Exit 0, 176 passed, zero failed or skipped, no compiler warnings or errors. This covers all
132 unique work builds with observed Find counts, real trace multiset equality, serial ordered
trace equality, all seven pinned files and forced ordering, and worker diagnostic restoration.
No historical counter, target, timing or geometry record changed.


## Task 7. Contracts and 21.5.3 finishing verification

BR-R4 supersedes the original staged-version premise. Before finishing, `git fetch origin`
confirmed released engine main `08f1c027c57e9ea9a322248ffb2d90b5d9f8df1b` and tag
`v21.5.2`. A normal merge brings #1500's sweep-memory fix into this branch. This lane stages
one patch bump to 21.5.3 with its own entry dated 2026-10-19. The released 21.5.2 entry
remains untouched. No release or consumer adoption is performed here.

### Requirement and acceptance self-review

| Requirement | Concrete proof and inspected contract | Acceptance |
| --- | --- | --- |
| BB-1 | `IdenticalCrossLeaseQueryPublishesFreshCompleteWithOneEvaluation`, `FootprintHitStillRunsAnalyticBindingAndMediumRoutes`, geometry payload serializer. Only owned geometry crosses leases. | A |
| BB-2 | `WorldDeclaresExactFootprintReuseCapability`, `SelectedViewDeclaresExactFootprintReuseCapability`, `SourceScopeNeverNestsAnExplicitPhysicsLease`, unsupported and nested-session controls. Exact selected receiver and non-owning lazy opening. | A, B |
| BB-3 | `OneBitKeyDifferenceMisses`, `HashCollisionRequiresFullEquality`, `ProfileAndSourceTokenChangesResetAllPayloads`, origin/view/generation tests. Inspected integer key fields and immutable session basis. | A |
| BB-4 | `SelectedViewHitPublishesUnderItsNewLeaseOnly`, `CompleteEmptyPayloadIsReusable`, `CurvedMeshAndCompoundPayloadsRemainBitExact`, `DirectOnlyEndpointContinuityIsRebuiltAfterTheHit`. Inspected fresh Completed metadata before output copies. | A |
| BB-5 | `InvalidAndUndersizedQueriesPreserveSentinels`, `RefusedOrShortSpanQueryCannotSeedReuse`, `MemoNeverRetainsAnyRefusalStatus`, `BoundedMemoDeclinesWithoutChangingPayload`, stale/wrong-thread and reset/dispose controls. | A |
| BB-6 | `OriginalOrdinalSurvivesRealSerialAndParallelAdmission`, `EqualRestingAnchorsKeepOriginalRawSourceAndLayer`, `AdmissionAndStairPhasesRetainNoSourceSession`. Raw capture address remains separate from derived layer. | B |
| BB-7 | `ReusedHoldAuthenticatesAnImmediateArrivalAndKeepsItsSlice`, `FirstRejectedHoldIsNeverRetained`, `SuccessfulHoldCopiesEveryNumericStateBitWithoutSelection`, `EveryHoldKeyDifferenceMisses`. Current lease is acquired before any copied Hold. | B |
| BB-8 | `StableSerialOptInMatchesParallelAndKeepsOneRealHold`, `SerialWithoutAssertionPreservesStatefulCallbackTrace`, `HoldEligibilityIsIndependentOfFootprintCapability`, `MutationAfterLastHitRefusesSerialAndParallelPublication`, `WaterBasisChangesCannotReuseAnImmediateArrival`. | B |
| BB-9 | `FootprintReuseKeepsPoolAndDirectionalStairRefusals`, `ProtectionCasesKeepSerialVerdict`, `ArrivalReuseKeepsEuclideanEqualityAndAdjacentFloats`. All protection and arithmetic cases keep their independent oracles. | C |
| BB-10 | `WorkerProofReuseCountersRestoreAndMerge`, `ResetCountersKeepsPerSessionRetentionPeaks`, `CurvedHitDoesNotReplayCertificationWork`, diagnostics-off refusal work and complete parallel Find traces. | E |
| BB-11 | `FootprintOnlyMeetsRecordedTargetsWithoutSkippingFinds`, `CombinedModeSavesOnlyCertifiedInitialHolds`. All fixture projections met, zero shortfall. Footprint and Hold reductions remain separate. | E |
| BB-12 | `EveryPinnedFileAndDegreeMatchesUnchangedSerialBytes`, `SourceAndDirectionOrderDoNotChangeBakeBytes`, `ReuseFailureSelectionMatchesSerialAfterRealHitsUnderForcedBands`, worker failure/setup/restoration tests. No golden regeneration. | D |
| BB-13 | One-bit and adversarial tests above, `ForwardedQueryConsumesJoinModeExactlyOnce`, `JoinWordBoundaryAndOwnedCapacityRespectPayloadBudget`, selected mutation and full geometry payload controls. | A, C |
| BB-14 | Living Physics, Bepu, Movement and consumer contracts, corrected Find evidence, version/changelog declarations and final command/guard results below. | F |

All five plan Review Focus tests exist and are included in final Game/Movement suite discovery:
`ForwardedQueryConsumesJoinModeExactlyOnce`, `EqualRestingAnchorsKeepOriginalRawSourceAndLayer`,
`ReusedHoldAuthenticatesAnImmediateArrivalAndKeepsItsSlice`,
`WorkerPhaseThreadChangeCannotInheritAReuseSession` and
`JoinWordBoundaryAndOwnedCapacityRespectPayloadBudget`.

The Markdown sweep used all requested names and covered 654 matches in 93 files before edits.
Additional affected living documents are `README.md` for guarded version examples,
`KhaozEngine.Locomotion/README.md` for actual versus copied complete queries, and
`docs/DEPENDENCY-SEAMS.md` for the capability, bridge and explicit worker Find hook. Original
plans, research projections and historical lane C/cost evidence remain historical, with BR-R4
recorded in the completed spec. Other Hold mentions concern unrelated APIs. No historical
measurement is relabeled as proof reuse savings.

The actual accepted reductions remain 37,095 of 79,499 footprint evaluations (46.66 percent)
and 2,285 of 40,338 real edge steps (5.66 percent) across the seven pinned fixtures. Wide-lip
avoids 29,234 of 69,532 evaluations (42.04 percent) and 2,888 of 34,992 edge steps
(8.25 percent). No shortfall, addition of overlapping reductions or wall-clock claim.
Retention peaks remain 330 keys and 275,840 bytes, with zero live entries after every build,
against unchanged limits of 512 keys and 2 MiB. Curved hits replay no work, and refusals
consume their real work independently with diagnostics off as well as on.

### Full Release build

```bash
build-slot --label bake-b-design -- dotnet build KhaozEngine.slnx -c Release
```

Exit 0. Build succeeded with zero warnings and zero errors. Full log is retained at
`/tmp/khaozengine-bake-proof-reuse/task-7/full-build.log`. This was the sole full build.
Support-cost artifact, hosted-cost artifact and GPU opt-in environment checks exited 0,
confirming they were unset. No timing/load harness or consumer was started.

### Full-suite discovery. Raw height call-site classification

The sole full solution run exposed `SurfaceHeightCallSiteTests.ProductionCallSitesAreClassified`.
Its exact allowlist lacked the new `ContactNavigationSources.BindLayers` raw `SurfaceHeightAt`
read. Inspection confirmed both serial and parallel paths call BindLayers before
WithRestingHeights. This read authenticates the original raw slot and must use raw topology,
not substituted resting feet. The classification now names that one call with its rationale.
No production read or raw identity changed, and the scanner retains its exact call-count guard.
The full run supplies the deterministic red. Only the three-case classification test class
will rerun after completion, with no second full solution run. This additional affected path
is `KhaozEngine.Game.Tests/Navigation/SurfaceHeightCallSiteTests.cs`.

### Unchanged tracked catalog failure

The full solution run also failed
`ContentUpgradeFreezeReleaseTests.ASuccessfulPublishReleasesNothingItselfAndLeavesNoMarkerStanding(sqlite: True)`
in Catalog.Tests. Its 20-second CancellationTokenSource expired in SqliteStoreConnection.EnterAsync
while ContentPublishCommit.SweepAsync awaited ListVersionsAsync. This exact test and failure
shape are already tracked by open [#1462](https://github.com/APKiwiOrg/KhaozEngine/issues/1462).
Read-only issue inspection confirmed the match. `git diff v21.5.2` over the test and involved
Catalog.Authoring, Catalog.Sqlite and SqliteStoreConnection paths was empty, exit 0.
No timeout increase, retry, exclusion or unrelated catalog change was made. The full run's
failure remains visible in the counts and prevents claiming a wholly green full selection.
The ledger search reported no local mirror, so the existing issue was located read-only with
gh. No GitHub write or new issue was made.

### Once-only full solution test selection

```bash
build-slot --heavy --label bake-b-design -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket' --logger trx --results-directory /tmp/khaozengine-bake-proof-reuse/task-7/full-tests
```

Exit 1. All 41 solution test projects produced TRX files and are represented below.
Full selection totals are 36,619 passed, 2 failed, 1,421 skipped, 38,042 total.
The two failures are the corrected raw-height classification and unchanged tracked #1462.
The original full-run record remains red. No second full run was made.

| Project | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| KeDungeon.Tests | 13 | 0 | 0 | 13 |
| KhaozEngine.Accounts.Tests | 286 | 0 | 46 | 332 |
| KhaozEngine.Attachments.Tests | 261 | 0 | 0 | 261 |
| KhaozEngine.Audio.Tests | 206 | 0 | 3 | 209 |
| KhaozEngine.Automation.Tests | 110 | 0 | 0 | 110 |
| KhaozEngine.BugReports.Client.Tests | 202 | 0 | 0 | 202 |
| KhaozEngine.BugReports.Relay.Azure.Tests | 97 | 0 | 15 | 112 |
| KhaozEngine.BugReports.Relay.Tests | 494 | 0 | 0 | 494 |
| KhaozEngine.BugReports.Tests | 442 | 0 | 0 | 442 |
| KhaozEngine.Catalog.AzureBlob.Tests | 25 | 0 | 7 | 32 |
| KhaozEngine.Catalog.Tests | 1520 | 1 | 0 | 1521 |
| KhaozEngine.CodeHealth.Analyzers.Tests | 51 | 0 | 0 | 51 |
| KhaozEngine.Foundation.Tests | 1503 | 0 | 0 | 1503 |
| KhaozEngine.Game.Tests | 6328 | 1 | 1 | 6330 |
| KhaozEngine.Gui.Tests | 1532 | 0 | 0 | 1532 |
| KhaozEngine.Identity.Exchange.Tests | 177 | 0 | 0 | 177 |
| KhaozEngine.ItemInstances.Tests | 895 | 0 | 0 | 895 |
| KhaozEngine.Localization.Analyzers.Tests | 46 | 0 | 0 | 46 |
| KhaozEngine.MapDoc.Compatibility.Tests | 14 | 0 | 0 | 14 |
| KhaozEngine.MapDoc.Physics.Tests | 242 | 0 | 0 | 242 |
| KhaozEngine.MapDoc.Tests | 1398 | 0 | 0 | 1398 |
| KhaozEngine.MapEditor.Tests | 1706 | 0 | 9 | 1715 |
| KhaozEngine.Movement.Tests | 1418 | 0 | 3 | 1421 |
| KhaozEngine.Netcode.Abstractions.DecouplingTests | 4 | 0 | 0 | 4 |
| KhaozEngine.Netcode.DecouplingTests | 4 | 0 | 0 | 4 |
| KhaozEngine.Particles.Tests | 106 | 0 | 0 | 106 |
| KhaozEngine.Physics.HeadlessLoaderTests | 5 | 0 | 0 | 5 |
| KhaozEngine.Render.Tests | 10203 | 0 | 1013 | 11216 |
| KhaozEngine.Server.Tests | 4072 | 0 | 317 | 4389 |
| KhaozEngine.Showcase.Tests | 83 | 0 | 0 | 83 |
| KhaozEngine.Simulation.Tests | 309 | 0 | 0 | 309 |
| KhaozEngine.Tests | 88 | 0 | 0 | 88 |
| KhaozEngine.TileEdit.Tests | 110 | 0 | 7 | 117 |
| KhaozEngine.TileWorld.Netcode.Tests | 1389 | 0 | 0 | 1389 |
| KhaozEngine.TileWorld.Physics.Tests | 159 | 0 | 0 | 159 |
| KhaozEngine.TileWorld.Tests | 608 | 0 | 0 | 608 |
| KhaozEngine.TileWorld.WorldMap.Tests | 30 | 0 | 0 | 30 |
| KhaozEngine.Windowing.Tests | 147 | 0 | 0 | 147 |
| KhaozEngine.WorldMap.Gui.Tests | 66 | 0 | 0 | 66 |
| KhaozEngine.WorldMap.Tests | 238 | 0 | 0 | 238 |
| PixelLabSheetAssembler.Tests | 32 | 0 | 0 | 32 |

### Necessary classification correction check

```bash
build-slot --label bake-b-height-classification -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter 'FullyQualifiedName~SurfaceHeightCallSiteTests' --logger 'trx;LogFileName=height-classification-green.trx' --results-directory /tmp/khaozengine-bake-proof-reuse/task-7
```

Exit 0. All three tests passed, zero failed/skipped, with zero compiler warnings or errors.
This rebuilt the affected Game test project after adding the exact raw-read classification.
No production source changed after the full build. The full selection was not retried, and
#1462 was not rerun. Final full-suite accounting retains both original failures rather than
rewriting the historical run as green. The lane-caused classification failure is resolved.

### Full diagnostic format and workspace warning

```bash
build-slot --heavy --label bake-b-design -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --verbosity diagnostic
```

Exit 2. The complete pass checked 8,429 files and reported only eight WHITESPACE diagnostics
in one object initializer at SupportFootprintProofMemo.cs lines 76 and 77. The required layout
puts the opening brace, each property and the closing brace on separate lines. That whitespace
is corrected without changing any non-whitespace initializer text. This additional affected
path is `KhaozEngine.Physics.Bepu/SupportFootprintProofMemo.cs`. No arithmetic, storage bound,
control flow or payload value changed. Only that file needs a correction verification.

Diagnostic output confirms the existing workspace warning exactly as lane C attributed it:
`Found project reference without a matching metadata reference` for
KhaozEngine.Localization.Analyzers.csproj. Showcase's existing ProjectReference declares
`OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`. The analyzer targets
netstandard2.0 and ships under analyzers/dotnet/cs. Both project files are unchanged from
v21.5.2, confirmed by an empty diff. No warning, project or analyzer was suppressed.

Every branch-changed code project loaded its configuration and ran both analysis passes:

| Project | Code-style analyzers | Analyzer-reference analyzers |
| --- | ---: | ---: |
| KhaozEngine.Game.Tests | 5 | 233 |
| KhaozEngine.Locomotion | 5 | 154 |
| KhaozEngine.Movement | 5 | 154 |
| KhaozEngine.Movement.Tests | 5 | 210 |
| KhaozEngine.Physics | 5 | 154 |
| KhaozEngine.Physics.Bepu | 5 | 154 |

No changed project was omitted. The full diagnostic log and the necessary file-only correction
log are retained under `/tmp/khaozengine-bake-proof-reuse/task-7/`. The initial whole-solution
format result remains recorded as exit 2, rather than being rewritten as a clean first pass.

### Necessary format correction verification

```bash
build-slot --heavy --label bake-b-format-correction -- dotnet format KhaozEngine.Physics.Bepu/KhaozEngine.Physics.Bepu.csproj --verify-no-changes --no-restore --include KhaozEngine.Physics.Bepu/SupportFootprintProofMemo.cs --verbosity diagnostic
```

Exit 0. Zero files formatted, no errors or workspace warnings in this targeted project run.
The changed Bepu file ran 5 code-style and 154 analyzer-reference analyzers. The initializer's
non-whitespace text is identical, verified before editing. The full solution's earlier diagnostic
pass proves all other files and every changed project were checked. No second full build,
full solution test selection or full solution format pass was run. The necessary checks were
limited to the corrected classification class and this whitespace-only file.

### Governance and final self-review

All commands below exited 0 after explicit staging:

```bash
sh scripts/check-dashes.sh staged
sh scripts/check-prose.sh staged
sh scripts/check-file-size.sh staged
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --cached --check
```

Contributor instructions remain 8,310 of 16,384 bytes. Every guarded version example and the
newest changelog heading matches 21.5.3. The final evidence footer receives the same staged
dash, prose, size and whitespace checks before commit.

Self-review of the final diff found no remaining lane finding. No golden, schema, controller
arithmetic, tolerance, band, work ceiling, storage bound, raw identity or callback default
changed. The only late code edits are the explicit raw-height audit classification and a
whitespace-only initializer layout. Full protection, payload, counter and scheduling evidence
is retained. No consumer, timing, load, world bake, push, main integration or release was run.
No manual client validation is required for this CPU-only lane. The remaining verification
concern is the unchanged tracked catalog deadline failure #1462. The original red full-test
and format results remain visible beside their necessary focused corrections.

## Release merge and 21.6.1 finishing verification

After v21.6.0 was tagged at b5f3a4e74, origin/main was merged into the lane at e6ee3f9ef under BR-R6. Every
engine-version declaration moved to 21.6.1 and the lane's CHANGELOG entry became `## 21.6.1` above the released
21.6.0 entry, which is unchanged. `bash scripts/check-doc-versions.sh` passed. The Task 7 section above records
the earlier 21.5.3 staging as it was verified.

| Check | Result |
| --- | --- |
| `dotnet build KhaozEngine.slnx -c Release` | exit 0, 0 warnings, 0 errors |
| `dotnet test KhaozEngine.Game.Tests` (Release, no build) | 6,329 passed, 0 failed, 1 skipped |
| `dotnet test KhaozEngine.Movement.Tests` (Release, no build) | 1,418 passed, 0 failed, 3 skipped |
| `dotnet format KhaozEngine.slnx --verify-no-changes --no-restore` | exit 2, 14 whitespace errors, all in five interior cut files already on main, none in a file this lane changed, tracked as #1505 |

The engine has no format gate in CI. This lane's own files carry no format error.

## Main CI counter correction verification

The deterministic red and arithmetic model are recorded in the Task 6 correction above.
The ARM64 stair selection passed 15 cases after removing temporary instrumentation. The final
Linux x64 work selection passed all 134 cases, zero failed or skipped. It ran the same Release
assembly in a disposable two-CPU, 2 GiB Apple container through build-slot with Linux x64
Rosetta enabled. Runtime 10.0.13 selected the zero-residual path. The existing CI runner was
not entered or changed. The full command and initial prerequisite failures are retained in
the lane's `ci-fix-report.md`.

The required full Movement suite ran once after the code was final:

```bash
build-slot --heavy --label bake-counter-movement-full -- dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --logger 'trx;LogFileName=movement-full.trx' --results-directory /tmp/khaozengine-bake-counter-fix
```

Exit 0, 1,462 passed, zero failed, three existing `SlopeArrivalTests` skips. The suite covers
the unchanged pinned bytes, protections and raw identity as well as the new exact work model.
Independent extraction of both TRX records found 132 unique work observations per runtime,
identical across degrees within each runtime, including misses, stores, Hold hits and peaks.
The extra comparison theory reuses the original immutable observations and adds no named bake.

```bash
build-slot --label bake-counter-release-build -- dotnet build KhaozEngine.slnx -c Release
```

Exit 0, zero warnings and zero errors. This was the sole full solution build.

```bash
build-slot --label bake-counter-format -- dotnet format KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj --verify-no-changes --no-restore --include KhaozEngine.Movement.Tests/ContactNavigationProofReuseWorkTests.cs KhaozEngine.Movement.Tests/Fixtures/ContactNavigationReuseWorkFixtures.cs --verbosity diagnostic
```

Exit 0, zero files formatted. Code-style and analyzer-reference analysis covered both changed
C# files. The production, version and golden diffs are empty. No additional full suite,
stress test, timing claim, consumer run, push, merge or release was performed.

The staged dash, prose, file-size and whitespace guards all exited 0. Whole-tree dash, prose
and size checks reported no finding. The contributor-instruction budget check exited 0 at
8,310 of 16,384 bytes, and the doc-version check exited 0 with 21.6.1 unchanged. Self-review
confirmed the production and golden diffs are empty and every changed path belongs to this fix.
