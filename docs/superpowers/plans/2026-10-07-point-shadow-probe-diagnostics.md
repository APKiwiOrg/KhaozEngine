# Point-shadow seam probe evidence capture

Coordinator approved bounded diagnostic preparation on 2026-10-07 for existing #1024/#1157/#1190.
Base 54a1f35842e1ec9c8af3ad87c7f058bf757da04f. No execution, GPU dispatch, stress or integration approval.

## Task 1: Prepare artifacts for the unchanged single seam probe

Worktree /Users/antonio/KhaozEngine/.worktrees/wa-shadow-diagnostics,
branch fix/wa-shadow-diagnostics. Guard directory, branch and base before writing.
Read AGENTS.md, contributor test rules and CROSS-PLATFORM.md relevant diagnostic sections.
No baseline restore/build/test. This is source-only preparation authorized before a finite proof.

Scope is KhaozEngine.Render.Tests/Gpu/PointShadowFilterGpuTests.cs, narrowly scoped test-only
artifact helpers/tests, and minimal PointShadowScene accessors needed to capture existing matrices.
No production renderer, shader, device, package, version or workflow edit. Preserve unrelated work.
Avoid size-ratchet growth. New artifact responsibilities belong in a cohesive test-only helper.

Requirements:

1. Instrument only TheSoftEdgeCrossesACubeFaceBoundaryWithoutAStep. Retain original plain/soft
   192 by 192 RGBA captures, camera matrices, adapter/backend identity, actual source and shader
   hashes, settings and capture hashes. Pin the actual source commit and source tree provenance.
   Use existing source/assembly metadata seams where available, otherwise propose explicit required
   proof inputs with validation. Never manufacture provenance from the current working directory.
2. Retain every one of 13 by 61 probe world/screen coordinates, integer pixel coordinates, plain
   and shadow bytes, full-precision ratios, sums, integrals, least-squares coefficients, residuals,
   bump and worst-elsewhere statistic. JSON numbers must round-trip. Raw RGBA plus a schema/dimensions
   manifest is acceptable. Do not require a new imaging dependency or change capture encoding.
3. Emit artifact before the existing 8 mm assertion, keeping that assertion, shaders, original
   sample positions, truncation and quadrature unchanged. Artifacts must survive a test failure.
   Use an explicit diagnostic output opt-in with an absolute destination, no global environment
   mutation inside tests. No private source or credentials. Bound artifact sizes and path behavior.
   Ordinary unconfigured tests retain behavior. Configured diagnostic failures must be visible.
4. Add finite CPU-only serialization/shape tests for evidence completeness, exact 13x61 indexing,
   round-trip floats/bytes and failure on missing required configured provenance. Synthetic inputs
   only. Author tests first, but DO NOT RUN any command that builds or tests.
5. Draft one future hosted Windows/WARP proof proposal in the report, exact test filter, executed
   count=1, no skip, source SHA verification, read-only workflow permissions, artifact always-upload,
   no matrix, retry, stress, goldens update, secret/private input, pack or publish. Existing assertion
   can fail while artifact capture succeeds, both results must be reported honestly. Do not author
   or dispatch a workflow yet. No clamped-face mutation in this first diagnostic capture.

Preparation completion is a source-reviewed diff, not verified capture behavior. Run only
git diff --check and source inspection. No restore/build/test/format/guards/commit/push, main/feed
changes, captures, workflow dispatch or subagents. Root owns separate commit and execution request.

Return exact files, schema/sample counts, provenance source, fixture access changes and risks.
Keep known pre-existing WARP signature separate from unproven mechanism. Identical rounded samples
are not byte equality. A push filter excluding this test is not a passing execution.

## Outcome

Preparation and scoped source review are approved. Review 1 required removing evidence-time
cross-compilation of all catalog shaders and correcting the hash labels. The fix retains passive
catalog GLSL and loaded assembly hashes only, with no device-bytecode claim. Review 2 approved
the correction and the artifact-completeness/assertion and render-origin labels. Root independently
checked the collector and schema. Sampling, quadrature and the 8 mm assertion remain unchanged.

One finite CPU verification request is queued for coordinator approval: Release Render.Tests
filter `FullyQualifiedName~KhaozEngine.Tests.Gpu.PointShadowSeamEvidenceTests`, expected 31 executed,
31 passed, zero skips, followed by verify-format scoped to the six changed source files. Both use
slot-run.sh, stop at the first failure and require a separate explicit compute grant. Coordinator granted both exact commands. They ran once and exited 0. Root parsed 31 executed,
31 passed and zero skipped in TRX, then explicitly released the heavy slot before light guards. Six source hashes are frozen in the SDD source-preverification.json record.

Future hosted proof uses existing ci.yml on an isolated proof-only ref, exact code-parent and
source equality excluding only that workflow, with proof SHA/tree matching built provenance.
Use one Windows WARP probe, read-only contents, pinned actions, explicit Windows shell and
cancel-in-progress false. Require a fresh unique evidence folder, complete artifact and separate
TRX assertion verdict. No proof workflow is authored or dispatched yet. Post-render provenance
refusal and per-file creation limitations remain explicit. An incomplete record's assertion note
does not establish execution of the assertion. TRX is the sole assertion result.

CPU proof is retained in proofs/2026-10-07-point-shadow-seam-cpu.json, with source and log/TRX
hashes. GPU and diagnostic capture environment variables were confirmed unset. No rendered
assertion, artifact capture, full GPU class or workflow ran. This validates artifact logic only.
