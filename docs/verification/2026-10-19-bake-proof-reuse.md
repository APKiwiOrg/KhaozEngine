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
| Installed statics and generations | An unrelated installation and removed/reused Bepu storage invalidate the old basis. The new current query evaluates fully with zero reuse stores or hits. Old results remain invalid. |
| Origin X, Y and Z | A one-bit increment from zero independently invalidates each component. Each current query evaluates fully with zero stores or hits. |
| Origin zero signs | Changing only the sign of zero on X, Y or Z invalidates the old basis. |
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

`ForwardedQueryConsumesJoinModeExactlyOnce` first proves that the uncached full and
certification-only matrices differ on a real small-crate scene. Both modes are then seeded in
the selected view. An armed forwarding call inserts a backend query before forwarding. The
inserted query returns the exact certification payload and the forwarded query returns the
exact full payload, with two hits. An inserted invalid-filter query consumes the arm before
throwing. The forwarded query still returns the full payload, with exactly one hit. Direct
armed and unarmed calls retain their respective complete matrices afterward.

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
