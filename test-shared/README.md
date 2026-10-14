# Shared allocation measurements

These sources are linked into every test assembly that asserts on an allocation counter.
Assemblies that keep an `AllocAssert` helper forward it to `AllocationMeasurement`.
Source linking shares the measurement contract without adding engine project references.

Allocation tests join an assembly-local xUnit collection with `DisableParallelization = true`.
The no-GC region is process-wide, so a measurement beside a parallel test is not valid.
Warm the operation before calling `AllocationMeasurement.Measure` or `NoPerCallAllocation`.
A workload that allocates more than the default 4 MB passes a larger budget to `Measure`.
For several counters in one window, or a workload that uses stack spans a delegate cannot
capture, use `AllocationRegion.Start` and dispose the region before asserting on the counters.
A lost region invalidates the measurement and reports process allocation, thread allocation,
collection counts and the runtime reason.
Nested starts are rejected before calling the runtime so they cannot invalidate an existing region.
The process-wide budget is checked explicitly even when the runtime grants additional space.
No measurement retries its workload, keeps the smallest of several readings or accepts an
interrupted counter.
