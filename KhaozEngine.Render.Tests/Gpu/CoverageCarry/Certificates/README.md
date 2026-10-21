# Public F0 continuous carry experiment

This disposable E2 experiment leaves the E1 carry graph and frozen exact constructor unchanged.
It uses the complete `coverage-typical-gpu/1` fixture, with 1,092 source vertices, 1,258 final
vertices, 2,332 triangles and two four-face inward supports. It imports no private data.
There is no production selection, drawable certificate, GPU dispatch or timing claim here.

## Domain and graph

A cell fixes both palette linear matrices, all command bits, support identity, source influences,
refinement recipes, aliases and owned anchor scalars. Six palette translation coefficients vary
independently inside inclusive binary32 bounds around the anchor. Thus the domain encloses every
sampler output admitted by the same current-input test. It assumes no interpolation between safe
endpoints and no bound derived from elapsed time. Unsupported layers, partial transition weights,
changed attachment transforms and inputs outside the domain refuse.

The domain uses the actual absolute origin and fixed equipment, coverage and attachment transforms.
The declared F0 coverage frames have a vertical Z axis. An outward inverse-XY determinant and fold-ratio
check transfer the global injectivity proof into command coordinates. Tilted frames refuse.
It does not factor common rigid placement. A separate case includes nonuniform scale and XY shear.
Arbitrary varying scale, shear, rotation, game animation clips and equip coverage remain outside
this F0 domain. The experiment does not narrow an admitted game's allowed motions.

`CoverageCarryEnclosure` evaluates interval endpoints at every separate RN32 operation with the
inclusive B38 flush. Multiplication considers all four endpoint products. It skins original sources
before evaluating canonical refinement recipes, then transports the owned scalar through the current
command frame. Shared aliases retain correlated positions. Protected and zero-recession paths retain
the E1 bit-copy policy. No rounded `MaxSlope` enters the continuous proof.

## Builder and independent checker

The builder proposes separating axes from faces, edges, vertices and vertex-to-edge residuals.
An approximate direction is only a witness. The checker recomputes the graph enclosure from the
claimed inputs and evaluates projections and axis lengths with outward real intervals. It never calls
the builder's axis search, the exact constructor or the sampled geometry oracle.

For each complete host triangle and every displayed equipment triangle it proves

```text
max(min(dot(equipment, n)) - max(dot(host, n)),
    min(dot(host, n)) - max(dot(equipment, n))) / upper(norm(n)) > required
```

This is a whole-triangle bound against the actual solid, including its inward sheet. It is not a
minimum of three point-to-sheet gaps and uses no substitute global plane. Own core and collar faces
retain `required = outward(clearance * 1.25)`, preserving the complete NC2 reserve. Other faces retain
strict positive separation. Exact support-coordinate slope bounds come from outward cross products
and division, with current affine inverses used for sheet containment and caps.

The checker also proves positive projected areas, positive normal fold cosines, pairwise projected
injectivity, nonadjacent host separation, alias provenance, protected zero boundaries, caps and total
world recession. Shared edges use correlated orientation predicates. Shared vertices use a strict
separator through that same vertex. Every host face participates, including protected rear faces.
The whole-host XY embedding exploits F0's single-sheet grid. F1's three-dimensional host needs a
separate host-pair proof and does not inherit this specialization.

Each core triangle stays in the convex inward footprint. An anchored corner fixes its inward side.
The separating inequalities hold on the entire convex enclosure, so the straight vertex homotopy
cannot cross the actual sheet. The welded host is connected, and a protected anchored corner is
outside the entire displayed solid's box. Boundary separation along the homotopy therefore also
preserves the exterior component for collars and protected faces.

Every attempt records its limiting inequality, element, opposing triangle, command, margin, wall
time, allocation bytes, proof size and check work. Work units count RN32 interval graph nodes plus
checked geometric inequalities and broad-phase pair predicates. They are not CPU instructions or
GPU validity costs. A missing or ambiguous witness refuses. A null peak working set means that the
runtime did not supply it. Capture the finite batch's OS peak separately with `/usr/bin/time -l` on
macOS.

The finite search tries zero reserve first, then 19 halved radii from 2 mm through approximately
7.63 nm. When that produces only point cells, it evaluates separately owned 0.25, 0.50 and 1.00 mm
carry candidates. Only nonzero active scalars receive the declared world reserve, scaled by authored
strength. The original exact anchor and cap stay unchanged. Numerical appearance acceptance remains
a later gate. The smallest successful candidate is selected for the lifetime report.

## Independent falsifiers and finite traces

The falsifier checks all 64 corners and 257 nonlinear interior samples per accepted candidate against
the actual E1 carry output and the separate exact geometry certifier. It also checks every resulting
position against the enclosure and requires distinct actual posed geometry. Sampling never supplies
a proof bound.

Mutation tests cover widened bounds, altered support and hash, wrong recession and transition
scalars, wrong influences, changed transforms, unsupported layers, nonfinite values, omitted witnesses,
safe endpoints with an unsafe middle and rear-host or collar contacts despite a clear core. A rehashed
rear-host corruption retains its safe core and still refuses geometrically. Rehashed excessive bounds
and missing witnesses also refuse, independently of digest mismatch checks.

F0 only defines lifted and bent palette states. The four-second drift traces are explicitly synthetic
continuous hip-motion extensions with fixed world amplitudes, independent small coefficient changes,
reversals, loop wrap and 100 ms, 250 ms and 1 s input jumps. The root and its attached equipment remain
fixed. One trace per motion is sampled at 30 and 60 Hz.
The original E1 alternating sequence is a separate fixed-anchor diagnostic. Expired frames remain
uncovered. No fallback, hidden body or refreshed anchor is counted as reuse.

Lifetimes use complete consecutive admitted runs. Right-censored trailing frames are recorded
separately. Reuse scenarios exclude the anchor frame. Delayed delivery of each fixed certified cell
starts at each complete run's first frame. At 4, 8 or 16 frames, the actual current-input predicate
controls validity, including reentry after an intervening expiry. The remaining consecutive run is
measured from that completion input. Out-of-trace completions and right-censored remaining runs are
recorded separately. The synchronized cohort replays the same
expiry on 32 distinct actors. It is an arithmetic scenario, not a measured GPU schedule.

## Reproduce

After a Release build, run one finite headless measurement from the repository root:

```bash
/usr/bin/time -l build-slot --heavy --label ec-b-e2-f0-measure -- env KE_GPU_TESTS=0 KE_COVERAGE_CERTIFICATE_RESULTS=/tmp/khaozengine-ec-b-e2-f0 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~CoverageCarryCertificateMeasurement
```

The opt-in fact writes `e2-f0.json` and one proof JSON per candidate under that directory.
The committed `e2-f0-results.json` retains compact public measurements and each cell's rejection
attribution. Full temporary proof files and test logs stay outside the worktree.
The parent experiment's `b-e2-f0-report.md` is the integration handoff. F1, full atlas coverage,
real motion lifetimes, certificate overhead, schedule cost and appearance acceptance remain separate.
