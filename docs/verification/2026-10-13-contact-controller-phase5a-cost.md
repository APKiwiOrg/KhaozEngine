# Phase 5a ordinary contact allocation and local cost evidence

Recorded on 2026-10-14 from source commit `3d5368ae1` on `feature/contact-controller-phase5`, after merging
`origin/main` at `978ca5102` in `fb28b5d8d`. Phase 5a is staged in 20.37.0. This is A10 local evidence,
not an SG-12 verdict, hosted proof or adoption approval.

## Runtime and method

Runtime .NET 10.0.12, process architecture Arm64, configuration Release, `DOTNET_TieredCompilation` unset
(default). Stopwatch frequency is 1,000,000,000 Hz. The three rows ran in one xUnit process. Cold means
the first read-and-step on that configured simulator, not a fresh operating-system process.

`OrdinaryContactAllocationTests` joins the existing `AllocSensitive` collection with parallelization disabled.
The unchanged `SupportAllocationRegion` is linked once from Game.Tests, with its original namespace and checked
no-GC behavior. Every measured pair uses the default 4 MiB region. The helper rejects a failed region, any
crossed collection or a failed end. Counters include gameplay allocations rather than masking them. See the
[.NET allocation counter](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getallocatedbytesforcurrentthread?view=net-10.0)
and [no-GC API](https://learn.microsoft.com/en-us/dotnet/api/system.gc.trystartnogcregion?view=net-10.0).

Scope, witness, frame boundary, release callbacks and field/registration lookup data are configured before
the tick pair. Token measurement uses a separate production environment. The tick includes
`PlayerMoveSimulator.BeginExplicitRead`, placement validation, `Step` and read disposal. Kernel/field bytes
are measured inside the same warm read, without acquiring another tick. Assertions and output follow the pair.
The separate effective-binding reference is run afterwards, so it does not prime the cold simulator workspace.
No test loops, averaging, threshold assertions, hosted runs or local load were used.

## Named lifetime inventory

Each token was measured on the active runtime, independently of the tick total. No token size is guessed.

| Token | Bytes | Measurement |
| --- | ---: | --- |
| Bepu `ReadLease` | 64 | One acquired selected-view lease after its cold acquisition |
| Production `OrdinaryMovementEnvironmentPin` | 112 | `TryPinPrepared` after scope/witness capture and a cold pin |
| `MovementQueryLease` plus context release binding | 192 | Constructor with captured witness/workspace and a fresh `Action` |
| `PlayerMoveReadScope` | 56 | Constructor with a previously cached release callback |
| Warm tick total | 424 | 64 + 112 + 192 + 56 |

The released explicit inventory remains unchanged, including its 312 B acquired tick baseline on .NET 10 arm64.
The ordinary pin replaces the explicit fixture pin, and the player read scope is an additional named lifetime.
Only the head's release callback is cached. `MovementEnvironmentContext` still creates its released
lease/context-release inventory exactly as before. Test-only Locomotion friend access allows independent token
construction, with no new production friend edge.

## Exact rows

All rows use a real `BepuPhysicsWorld` at origin zero, world id `world`, one authored owner `floor` registered
for features 0 through `int.MaxValue`, an XZ floor box from -4 to 4 with thickness 0.5 m, declared extent
(-20, -8, -20) to (20, 8, 20), analytic range [h, h], and a rectangular playable boundary [-20, 20] on XZ.
The ground delegate returns h. The ordinary placement offset is zero. No point medium delegate is supplied.

Tuning is `MoveTuning.Default` at the source commit with these exact changes:

```csharp
Controller = MovementControllerKind.Contact,
Water = new(WaterTraversalMode.SurfaceSwimmer, 7),
CapsuleRadius = 0.25f,
CapsuleHalfHeight = 0.75f,
WalkSpeed = 4,
RunSpeed = 8,
Gravity = 20,
StepHeight = 0.4f
```

Every row uses `new MoveCommand(Vector2.UnitX, false, 0)` and dt 0.1 s, with frame anchor zero and the
remaining state fields at their normal defaults. The source test is
`KhaozEngine.Server.Tests/NetWorld/OrdinaryContactAllocationTests.cs`.

| Row | Ground h | Water | Initial centre Y | Initial state |
| --- | ---: | --- | ---: | --- |
| flat box | 0 | Absent | 0.75 | Grounded, not swimming, vertical speed 0 |
| SurfaceSwimmer | -4 | Level surface 0, bed -4 | -1 | Swimming, Surface excursion, vertical speed 0 |
| water crossing | -4 | Level surface 0, bed -4 | 1.25 | Airborne, not swimming, no excursion, vertical speed -12 |

## One cold and one warm sample per row

Command, exit 0, 14 passed, 0 failed:

```bash
build-slot --label p5a-7-allocation-evidence -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~OrdinaryContactAllocationTests --logger 'console;verbosity=detailed'
```

| Row | Cold bytes | Warm bytes | Cold elapsed ticks | Warm elapsed ticks | Warm kernel/field bytes | Outcome |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| flat box | 114336 | 424 | 105710916 | 4305083 | 0 | Advanced / Advanced |
| SurfaceSwimmer | 544 | 424 | 1112792 | 287709 | 0 | Advanced / Advanced |
| water crossing | 30568 | 424 | 14388708 | 968583 | 0 | Advanced / Advanced |

At the recorded frequency, one millisecond is 1,000,000 ticks. Flat-box output is (0.4, 0.75, 0), grounded
and dry. SurfaceSwimmer output is (0.25, -0.8374734, 0), swimming. Water crossing output is
(0.4, -0.14999998, 0), swimming after starting dry. Every row matches its cold result and the ordinary
effective-binding reference bit for bit. Timings have no pass/fail interpretation.

## RED to GREEN and guard dispositions

The initial A10 test failed all three rows. Expected warm 424 B. Actual flat-box warm 1768 B (kernel 1280),
SurfaceSwimmer 1000 B (kernel 512), water crossing 2920 B (kernel 2432). Phase tracing attributed every
kernel byte to exact rectangular boundary comparisons. The remaining 64 B was the uncached player read-release
delegate. Exact binary64 expansions now replace rectangular comparison's BigInteger storage, preserving
cancellation, subnormal and outside-range signs. The simulator caches its callback at construction.
The diagnostic instrumentation was removed. All three rows now satisfy the named-token equality and zero
kernel/field assertion. A small-correction boundary allocation test and ten exact-sign rows also pass.
This sample covers rectangular boundaries only. It makes no allocation or timing claim for other scenes.

Only Task 1 changes the two P5A-14 guards. `NavBakeIdentityTests` distinguishes 30 positional fields from the
three new properties and keeps the retained encoder ordering. `ExplicitMovementProfileTests` includes the
selected foot fraction and its valid 0.45 mutation. Frozen v1 fingerprint
`b8d049d40ad365789f251269953c4ee1074440c24b80982f69b874f7cb9f08e3` is unchanged.
No golden bytes, tolerance, support budget or existing explicit allocation assertion was changed by Task 7.
Old cost baselines fill absent Controller, FootRadiusFraction and Water values with their recorded defaults,
without editing those baselines. #1299 navigation acceptance, native G1b and the remaining adoption/cost gates
remain open. Final suite and repository-guard results are in the Task 7 report.
