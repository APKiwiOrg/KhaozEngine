# Navigation capture research fixtures

This non-packable, outside-solution project owns the navigation capture research helper, diagnostic
snapshot restoration utility and their three synthetic fixture tests. It directly references Movement,
Physics, Physics.Bepu and Locomotion. No engine or engine test project references this suite.
The seven SupportQueryCapture methods remain in [Movement.Tests](../../KhaozEngine.Movement.Tests/README.md)
and run in default engine CI, including both enabled and disabled theory cases.

Run the explicit Release suite from the repository root in its own test process.

```sh
mkdir -p local-feed
sh scripts/build-release-tests.sh tools/NavCapture.Research.Tests/NavCapture.Research.Tests.csproj
dotnet test tools/NavCapture.Research.Tests/NavCapture.Research.Tests.csproj -c Release --no-build
```

Every local build and test uses the shared `build-slot` wrapper. No renderer verifier prerequisite or
shared locomotion fixture links are needed. The `NavCaptureResearch` collection keeps
`DisableParallelization=true` because the helper reads process counters and uses EventSource.
The private-constructor reflection stays in the test-only snapshot utility, with its existing KETEST002
allowance transferred to its new path. There is no production restore API or supported snapshot format.

The free `tooling-validation.yml` workflow selects suite `nav` on GitHub-hosted `ubuntu-26.04` for
this project, Movement's AssemblyInfo, any `KhaozEngine.Movement/PhysicsNav*.cs` source, shared
build/package/baseline inputs or workflow changes. The PhysicsNav sources are watched because the
snapshot restore reflects over `PhysicsNavBake`'s private constructor and reads the capture types, so a
production change there can break restoration without touching this project. Manual dispatch accepts
`nav` independently of the renderer and Items routes. Its TRX gate requires all three named facts to
pass once with zero skips. Other Movement edits receive their engine tests without selecting this
research suite.

Require a passing research fixture workflow run before a separately approved capture or snapshot
restoration campaign. These synthetic checks preserve diagnostic float and unsigned bits, refuse
callbacks during restoration and compare complete fixture KENB bytes. They certify fixture integrity,
not performance, private input identity or end-to-end consumer parity, and authorize no capture campaign.

The [research guide](Research/README.md) documents caller probes, counters, snapshot bytes and private
input handling. The [research design](../../docs/design/NAV-CAPTURE-CPU-RESEARCH-2026-10-15.md)
retains the historical measurements and their limits.
