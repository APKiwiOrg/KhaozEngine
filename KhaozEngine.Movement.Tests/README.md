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
boundary. The synchronous segment and straightening diagnostics have a separate untimed sampling-window
check. Their ordinary test verifies row workload sizes using footprint candidate counts.

Passing a research fixture does not authorize a measurement campaign.
