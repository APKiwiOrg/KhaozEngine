# Items benchmark acceptance

This non-packable, outside-solution project owns the single Items end-to-end quick acceptance fact.
It directly references only `KhaozEngine.Benchmarks`, with friendship for its internal benchmark types.
No engine or engine test project references this suite. Five Items option, limit, field-fence, JSON
and atomic-output methods and four resident-memory calibration and regression methods remain in
`KhaozEngine.Server.Tests` and gate default engine CI.

Run this explicit Release suite from the repository root in its own test process.

```sh
mkdir -p local-feed
sh scripts/build-release-tests.sh tools/Items.Benchmark.Tests/Items.Benchmark.Tests.csproj
dotnet test tools/Items.Benchmark.Tests/Items.Benchmark.Tests.csproj -c Release --no-build
```

Local builds and tests use the shared `build-slot` wrapper. No renderer verifier prerequisite is needed.
The linked Server.Tests `AllocSensitive` collection definition disables parallelization in this
assembly because budget 12 reads process-wide resident memory and forces collections. The fact keeps
200 resident players above the instrument's 4 MiB noise band and two players in the busy-work leg.
The field inventory and bounded fixture cleanup are source-linked from `test-shared` into both
projects. Handles are disposed before cleanup, and cleanup retries never rerun a measurement.

`tooling-validation.yml` selects suite `items` explicitly on GitHub-hosted `ubuntu-26.04`. Push and
pull-request selection watches this project, the benchmark Items sources and csproj, `items-*.json`
baselines, the three linked sources, shared build/package/baseline inputs and the workflow itself.
Manual dispatch also accepts `items`. The TRX gate requires the named acceptance fact to pass with
zero skips, so a successful command that selected no tests cannot pass validation.

Before publishing or refreshing Items benchmark evidence, require a passing acceptance workflow run
at the exact benchmark code commit. Record its run URL and code SHA beside the evidence. A changed
baseline JSON triggers validation automatically. This quick fact validates instrument and workload
structure. The separately authorized benchmark produces the performance evidence. Passing this
suite certifies no performance budget and launches no full benchmark or load campaign.
