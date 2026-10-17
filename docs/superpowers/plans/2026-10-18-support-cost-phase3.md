# Support cost, SG-12 phase 3, exact stair stratum prototype, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove or refute, with one gated prototype, that an exact stratum for horizontal axis-aligned rectangular faces cuts the stairs box Find by at least 6 us M2-equivalent while every support result stays bit-identical.

**Architecture:** `SupportFootprintPolygon.Measure` runs vertical admission, then `Extrema` twice (expanded radius for admission, request radius for the footprint), then normal and plane publication. For an exactly horizontal face whose represented double vertices form an axis-aligned rectangle, most interior, vertex and edge candidates of `Extrema` are provably absent or provably constant in height. The stratum decides those cases with exact arithmetic and feeds `Range.Add` exactly the candidates the generic loop would add, in the same order, so `Minimum`, `Maximum`, `Witness` and every downstream bit match. Anything outside the proved domain, or any boundary case the stratum cannot certify, falls through to the generic evaluator. `SupportReferenceEvaluation` still bypasses the stratum and stays the oracle.

**Tech Stack:** C# / .NET 10, BepuPhysics, xUnit, `KhaozEngine.Physics.Bepu`, `KhaozEngine.Game.Tests`.

**Spec:** `docs/design/CONTACT-CONTROLLER-SUPPORT-COST-2026-10-17.md`, rulings SC-19 and SC-20, on top of SC-1 to SC-18. Research basis: research 3 lever L3 (ledger `sg12-hosted3/research3-report.md`, section 3).

## Global Constraints

- No SG-12 limit, ceiling, tolerance or assertion epsilon changes anywhere.
- Bit identity with the reference evaluator (SC-9): status, height interval, height error, witness and its error, contact masks, normal and plane publication, raw owner and feature provenance, binding, XZ, refusal and malformed or unresolved verdicts and their order, in every cost scene and in the new exact-domain tests. A result that is mathematically equal but differs in any bit fails.
- Hull faces are recognised on their represented double coordinates, never rounded back to an authored box. Mesh triangles stay distinct raw members. No face is merged with another.
- No support result or candidate list is cached across axes, ticks, bodies or reconciles (SG-15). The stratum may store only immutable per-face facts under the existing installation, version and exact-pose checks (SC-4, SC-12).
- Warm Find allocates 0 managed bytes.
- Preserve the 1 mm arrival rule, roof and footprint protections and raw layer identity.
- The ordinary suite asserts counters and allocation, never wall time (SG-20).
- Every build and test through `build-slot --label <task> -- <command>`, `--heavy` for wide filters and timing. One command at a time, no loops, no parallel test processes, no stress. No hosted run.
- Do not change `SupportSegmentPolygon` without re-checking `SupportContactPrefilter`'s margin derivation.
- Shipped text carries no em or en dashes and no prose semicolons. Run `sh scripts/check-dashes.sh staged`, `sh scripts/check-prose.sh staged` and `sh scripts/check-file-size.sh staged` before each commit. Commit subjects `area(scope): summary`, no attribution.
- No version or `CHANGELOG.md` change in Task 1 (prototype, not shipped behaviour).

## Review Focus

1. A face that is horizontal in authored terms but not in represented doubles (hull offset, rotated static, enclosed source pose). It must fall through (`StairStratumFallsThroughOffExactDomain`).
2. A disc that touches a rectangle edge or vertex exactly (closed tangency, endpoint ties). The witness union must match the generic path (`StairStratumMatchesGenericAtTangency`).
3. Two candidates with equal exact height, where `Range.Add` skips the union for identical exact intervals. Order and the skip must match (`StairStratumPreservesWitnessOrder`).
4. Member capacity and below-band retention, where a face the stratum handles sits under the band or at the 256-member limit (`StairStratumKeepsCapacityAndBandVerdicts`).
5. Reference mode. With `SupportReferenceEvaluation.Active` the stratum never runs (`ReferenceEvaluationBypassesStairStratum`).

---

### Task 1: Exact stair stratum prototype with the SC-20 gate

**Files:**
- Create: `KhaozEngine.Physics.Bepu/SupportFootprintPolygon.StairStratum.cs` (domain certificate and the exact candidate producer, a partial of the polygon class so it shares the private `Range`, following `SupportFootprintCollector.Geometry.cs` and `SupportFootprintCurved.Admission.cs`)
- Modify: `KhaozEngine.Physics.Bepu/SupportFootprintPolygon.cs` (declare the class `partial`, `Extrema` dispatch only), `KhaozEngine.Physics/SupportQueryDiagnostics.cs` (one new `SupportQueryCounter` value for stratum hits)
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/SupportFootprintStairStratumTests.cs`, plus a counter assertion in the existing cost-scene equivalence test

**Interfaces:**
- Consumes: `SupportPolygon` (vertices, edges, normal, `IsResolved`, `HasSource`), `SupportFootprintRequest`, `FeatureNumber`, `Range.Add`, `SupportReferenceEvaluation.Active`, `SupportFootprintEquivalence.AssertMatchesReference`.
- Produces: `static bool TryStairExtrema(SupportPolygon polygon, in SupportFootprintRequest request, FeatureNumber radius, ref Range range, out GeometrySign result)` inside `SupportFootprintPolygon`. It returns false when the face is outside the certified domain, and the caller then runs the generic loop unchanged. `SupportQueryCounter.StairStratum` counts hits.

- [ ] **Step 1: Write the failing tests.** The five Review Focus tests above, plus `StairStratumMatchesReferenceOnEveryCostScene` (every SG cost scene through `AssertMatchesReference`, and the stairs box Find records at least one `StairStratum` hit). Exact-domain fixtures: an axis-aligned horizontal rectangle with the disc fully inside, straddling one edge, touching one vertex exactly, missing entirely, and a hull face 1 ulp off horizontal.
- [ ] **Step 2: Run them and confirm they fail** for the missing type and counter: `build-slot --label stair-stratum -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter FullyQualifiedName~SupportFootprintStairStratum`.
- [ ] **Step 3: Implement `TryStairExtrema`.** Certify the domain from represented doubles only: orientation Positive, normal X and Z exactly zero, every vertex Y bit-equal, four vertices forming an axis-aligned rectangle. Then produce exactly the `Range.Add` calls the generic loop would make, in its order (centre, vertices in index order, edge candidates in edge order). Decide each with exact double arithmetic on the represented coordinates or fall back. The stratum is free to skip work, never to change what is added or in which order.
- [ ] **Step 4: Run the focused tests, then the support equivalence and cost-artifact families** (`SupportFootprint`, `SupportGateCost`, `SupportCost` filters, `--heavy`). All pass, warm Find allocation stays 0.
- [ ] **Step 5: Measure the SC-20 gate.** Two one-shot cost artifact runs back to back through `build-slot --heavy`, base commit then task head, each `DOTNET_TieredCompilation=0 KHAOZ_SUPPORT_COST_ARTIFACT=<path> dotnet test ... --filter FullyQualifiedName~SupportGateCostTests.OneShotCostArtifact`. Report the stairs box, hull and mesh Find warm medians, base and head, and the saving. Gate: stairs box Find saves at least 6 us. Two runs only, never a loop.
- [ ] **Step 6: Commit** the stratum and tests, and record both artifacts and the gate result under `docs/superpowers/plans/proofs/2026-10-18-support-cost-phase3.md`.

**After Task 1.** If the gate passes, the orchestrator amends this plan with the measured numbers: Task 2 extends the stratum to the other rows research 3 named (stairs hull and mesh Find, stairs box and hull DryTick, crate substeps B1 and D1) together with L1 and L2. The owner is then asked separately about a fourth hosted run. If the gate fails, the prototype is kept on a `probe/*` branch, and every row still missing goes to phase 6 under SC-19.
