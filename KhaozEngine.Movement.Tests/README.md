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
correctness tests remain here. Passing a research fixture does not authorize a measurement campaign.
