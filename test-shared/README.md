# Shared allocation measurements

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
runtime reason.
Nested starts are rejected before calling the runtime so they cannot invalidate an existing region.
The process-wide budget is checked explicitly even when the runtime grants additional space.
No measurement retries its workload, keeps the smallest of several readings or accepts an
interrupted counter.

## Why test reporting is synchronous

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
