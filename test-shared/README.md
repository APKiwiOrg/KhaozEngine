# Shared allocation measurements

These sources are linked into the Foundation, Game, Catalog and MapEditor test assemblies.
Movement and Server also link the checked region for their existing support measurement wrapper.
Source linking shares the measurement contract without adding engine project references.

Allocation tests join an assembly-local xUnit collection with `DisableParallelization = true`.
Warm the operation before calling `AllocationMeasurement.Measure` or `NoPerCallAllocation`.
For cold and warm counters in one window, use `AllocationRegion.Start` and dispose the region
before asserting on the counters. A lost region invalidates the measurement and reports
process allocation, thread allocation, collection counts and the runtime reason.
Nested starts are rejected before calling the runtime so they cannot invalidate an existing region.
The process-wide budget is checked explicitly even when the runtime grants additional space.
No measurement retries its workload or accepts an interrupted counter.

MapEditor allocation bounds run with tiered compilation disabled in the test host.
This excludes tier promotion from the search-storage measurement without changing the bound.
