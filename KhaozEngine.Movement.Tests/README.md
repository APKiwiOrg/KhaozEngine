# Movement tests

This solution-owned project tests engine movement and navigation contracts in default engine CI.
Its five SupportQueryCapture methods and two retention methods stay here, including both rows of
each enabled/disabled theory. These guard public counters, nested labels, exception restoration,
scope misuse, observer isolation, retention balance and complete versus partial retention lifetimes.

Run the engine suite from the repository root through the shared `build-slot` wrapper.

```sh
mkdir -p local-feed
sh scripts/build-release-tests.sh KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj
dotnet test KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj -c Release --no-build --filter 'Category!=LiveSocket&Category!=Diagnostic'
```

The three navigation capture research fixtures and their helpers live in the outside-solution
[NavCapture.Research.Tests](../tools/NavCapture.Research.Tests/README.md) project and run through its
explicit free tooling route. Movement.Tests does not reference that assembly. Production navigation
correctness tests remain here.

Segment admission tests compare the synchronous guard and sliced requests against the frozen rectangle
on the exact-footprint corpus. Literal traces pin the work and result visibility at each operation
boundary, including endpoint tie order, the first footprint row and the first directed crossing.
Route tests pin bitwise equality across quota schedules, the three guard lines and their early stops,
profile ownership, query admission limits, the shared lease through publication and the synchronous-only
policy for footprints with query-time area exclusions. A cancelled begin hook is refused with a free
workspace or behind a live owner, and the next valid request can advance. The synchronous segment and straightening diagnostics have a separate untimed sampling-window
check. Ordinary tests verify workload sizes, query endpoints, route positions, layer and waypoint kind,
and the Stair workload's dependency on its directed link.

Workspace factory tests cover pool size, default and declared query budgets and refusals for a zero count
and profiles with query-time area exclusions. Mover planning tests cover shared profile pools with per-mover
fallback, request adoption, lease hand-over on reset and arrival, and supported Drop and stall behavior
while a request is pending. Allocation guards cover immediate and queued begins and a warmed pending slice.

Passing a research fixture does not authorize a measurement campaign.
