# Coverage filter acceptance study

CPU-only, opt-in research against `f1a298ef107d691d9ea0341ece6f3ffa29d4fc6a`.
No product arithmetic, shader, frozen fixture, expected output or dependency is changed.

`generate.py` refuses source drift and writes five instrumented reference copies into the test project's
ignored `obj/coverage-filter-study` directory. Its transformations remove GPU-cached duplicate queries,
observe exact operations and keep the reference computations unchanged. The generated copies are only
compiled with `CoverageFilterStudy=true`. The measurement also requires `KE_COVERAGE_FILTER_STUDY=1`
and an explicit output path. It never creates a GPU device.

From the research worktree root, run each command once, in sequence.

```bash
mkdir -p /tmp/khaozengine-ec-filter-study local-feed
python3 KhaozEngine.Render.Tests/Render3D/FilterStudy/generate.py
build-slot --label coverage-filter-study-build -- dotnet build KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release -p:CoverageFilterStudy=true -v:q
build-slot --heavy --label coverage-filter-study-corpus -- env KE_GPU_TESTS=0 DOTNET_EnableFMA=0 KE_COVERAGE_FILTER_STUDY=1 KE_COVERAGE_FILTER_STUDY_OUTPUT=/tmp/khaozengine-ec-filter-study/results.json dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build -p:CoverageFilterStudy=true --filter FullyQualifiedName~CoverageFilterStudyTests -v:q
```

The finite dataset has 72 frozen cases, both poses at all 32 actual profiled world placements, both
one-actor poses and all 720 samples of the recorded public F0 four-second hip traces at 30 and 60 Hz.
The alternating trace is represented by its two unique inputs and exact multiplicities. T4 profile
rows have the nine measured-frame multiplicities, without repeatedly evaluating duplicate inputs.
This is one dataset traversal, not a repeated benchmark or stress test. No private fixture is loaded.

Each observed accepted predicate sign and RN32 word is checked against the original exact predicate.
The instrumented correction results, guards, correction words and complete vertex words are compared with
the unchanged reference pipeline. A finite wrong-seed test covers adjacent values, opposite signs and
signed zeros. Scalar measurements use the CPU's RN32 result as their candidate.
GPU native division and square-root seed quality is not measured. Queue counts are conservative
whole-face or whole-overlay-pair ambiguity counts, not measured GPU queues.

Modes are a conservative binary32 polynomial ball, a split-product double-single ball with outward
binary32 error accumulation, and a conservative binary64 polynomial ball. Scalar certificates test
the two rounding midpoints after exact power-of-two normalization. Double-single uses explicit
TwoSum and split TwoProduct, never an assumed fused multiply-add. The public sampler is transcribed
from `CoverageCarryLifetime.cs` at `575472e3a57145fcac3409befd61d20922144ffb`.

Results describe CPU acceptance under IEEE round-to-nearest arithmetic. They do not certify an MSL,
HLSL or SPIR-V implementation, compiler behavior, queue scheduling, physical registers or GPU time.
Any implementation needs the separate cross-backend proof obligations in the owning study report.
