# Shared test helpers

## Allocation measurements

Every test assembly that asserts on an allocation counter imports `AllocationMeasurement.props`.
The import links the region, the measurement helpers, the reporting framework and the contract tests,
and it registers `SerializedReportingTestFramework` as the assembly's xUnit framework.
Assemblies that keep an `AllocAssert` helper forward it to `AllocationMeasurement`.
Source linking shares the measurement contract without adding engine project references.

Allocation tests join an assembly-local xUnit collection with `DisableParallelization = true`.
The no-GC region is process-wide, so a measurement beside a parallel test is not valid.
`AllocationRegion.Start` refuses before touching the runtime unless the current collection runs alone
under `SerializedReportingTestFramework`. A collection runs alone when its definition disables
parallelization or the whole assembly runs serially. The flag is published through `AppContext`, so a
copy of the assembly loaded into a fresh load context to reset statics reads the same value.
Warm the operation before calling `AllocationMeasurement.Measure` or `NoPerCallAllocation`.
A workload that allocates more than the default 4 MB passes a larger budget to `Measure`.
For several counters in one window, or a workload that uses stack spans a delegate cannot
capture, use `AllocationRegion.Start` and dispose the region before asserting on the counters.
A lost region invalidates the measurement and reports process allocation, thread allocation,
allocation by other threads, the test messages reported during the window, collection counts and the
runtime reason. Isolation and invalid-window refusals throw `AllocationMeasurementException`, which remains an
xUnit assertion failure. Its `Reason` names the refusal and `Description` identifies the measurement.
Window failures carry `Budget`, `ProcessBytes`, `ThreadBytes`, `OtherThreadBytes`, `ReportedMessages`,
`Collections` (gen0, gen1, gen2) and `RuntimeReason`. Those window fields are absent for an isolation refusal.

| Reason | Refused condition |
|---|---|
| `CollectionNotIsolated` | The collection can run beside another test |
| `BudgetExhausted` | Process allocation exhausted the reserved budget |
| `CollectionInterrupted` | A collection interrupted the window |
| `RegionEnded` | The no-GC region ended before the window completed |

Nested starts are rejected before calling the runtime so they cannot invalidate an existing region.
The process-wide budget is checked explicitly even when the runtime grants additional space.
No measurement retries its workload, keeps the smallest of several readings or accepts an
interrupted counter.

### Why test reporting is synchronous

xUnit's default message bus hands every test message to one reporter thread. Behind it the
Visual Studio adapter and the test host serialize each result to JSON. Under CPU contention that
thread falls behind the parallel collections. It then drains its backlog after the parallel
collections finish, which is exactly when the serial `AllocSensitive` collection opens its first
window. On the hosted macOS runner that backlog spent more than 4 MB of the process-wide budget inside
one window while the measured thread allocated nothing (#1391, #1449).

`SerializedReportingTestFramework` keeps xUnit's discovery and scheduling and replaces only the bus.
Each message is delivered on the thread that reports it, one delivery at a time across threads, so a
collection leaves nothing queued when it finishes. The test host still flushes fewer than ten cached
results on its own timer, which stays far inside the budget.

## Renderer scene inputs

`RendererPerfSceneInputs.cs` holds the GPU-free geometry, pose and settings factories used by renderer
evidence tooling and engine scene fixtures. Render.Tests compiles it by source link. The existing fixture
methods forward to these inputs, preserving their caller APIs. A tooling project can compile the same
source without importing real-device fixtures or referencing the engine test assembly.

`RendererPerfShared.props` explicitly source-links helper-only engine test files into the outside-solution
renderer tooling assembly. It normalizes the source root before constructing paths, so KESIZE and
KETEST retain each helper's physical repository-relative identity. The helper files remain owned by
their source test project or `test-shared`, including GpuPerfTimestampRing and its fakes. No module
initializer or unrelated fact file is linked. The tooling project also imports AllocationMeasurement.props,
including its independent allocation-isolation contract tests and serialized reporting framework.

## IL architecture fences

`IlCallGraph.cs` is the shared IL reader in `KhaozEngine.Tests.Gpu`. `DeclaredMethods` enumerates a
type's declared methods and constructors, including non-public members. `Callees` reads and caches
their compiled call sites, and `Describe` renders a caller as `Type.Member`. Render.Tests source-links
the reader directly. Renderer.Perf.Tooling.Tests links the same file through `RendererPerfShared.props`.
Its non-public reflection is an owner-approved KETEST002 exemption for inspecting compiled calls.
The architecture helper links normalize their physical paths before inclusion so KETEST exemptions
match `test-shared/...` rather than a project-relative path containing `..`.

`RefusalConstructorFence.cs` reads assembly files through `PEReader` and `MetadataReader` to find
`newobj` calls whose declaring type and parameter type names match forbidden exception constructors.
It reads metadata only so scanning a backend never loads its platform-specific interop dependencies.
Source-link this helper into an owning test project and pass engine assemblies and the custom
exception's message-only constructors to `EngineCallers`. Callers are returned as sorted, distinct
`<declaring type full name>.<method name>` descriptions. An empty result proves those assemblies
contain no matching constructor calls. Reason-taking constructors remain allowed, and consumer
assemblies are covered only if explicitly supplied. Invalid IL or metadata fails the scan.

`IlCallGraph` stops or skips unresolved IL rather than guessing, so its architecture rules need positive
controls. `RefusalConstructorFenceTests` proves a private caller of `InvalidDataException(string)` is
reported, an uncalled constructor is left clear and scanning the D3D11 assembly loads no interop off
Windows. Existing Metal architecture and tooling fences retain their own controls and use `IlCallGraph`.

## Items benchmark fixtures

`ItemsBenchmarkFields.cs` owns the positive-measurement field inventory shared by the Server.Tests
budget fence and the outside-solution Items acceptance fact. `ItemsBenchmarkFixtureDirectory.cs`
preserves bounded IOException cleanup retries after fixture handles are disposed. Both projects
source-link these helpers so field coverage and cleanup behavior have one source. The explicit
acceptance project also links Server.Tests' nonparallel AllocSensitive collection definition.
