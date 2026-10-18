# Attachment affine crash implementation plan

> **For agentic workers:** Use subagent-driven-development for implementation and independent review. Steps use checkbox syntax.

**Goal:** Repair the engine cause of the Windows x64 stone-leg attachment crash without accepting invalid external transforms.

**Architecture:** Reproduce the rejected composition first. Repair the producer of a malformed engine-owned alignment if confirmed, preserving strict attachment input validation. Keep game assets out of the public engine repository.

**Tech Stack:** C#, System.Numerics, .NET 10, xUnit.

**Spec:** Owner-approved engine repair for https://github.com/APKiwiOrg/Grimhollow/issues/550. The exception identifies the composed world matrix, after model and joint validation passed. Inverse alignment rounding is a hypothesis until reproduced.

## Global constraints

- No exception swallowing, omitted garments, or blanket relaxation of transform validation.
- No private game assets or crash dumps committed to the public engine repository.
- Every build and test uses build-slot. No stress runs or repeated-test loops.
- No release tags. The orchestrator owns integration and consumer adoption.
- Preserve concurrent work. Work only on fix/attachment-affine-crash.

## Review focus

- An oblique rigid base must yield a usable exactly affine attachment transform.
- Rotation, translation and reflected handedness must remain correct.
- Singular, nonfinite and genuinely projective external inputs must still be rejected.
- Existing scale and shear stripping must remain unchanged.
- Regression coverage must exercise real composition and run on Windows x64 as well as ARM64.

### Task 1: Reproduce and repair the engine alignment producer

**Files:** Inspect KhaozEngine.Render3D/Animation/ContractJointMap.cs, BoneSocket.cs, KhaozEngine.Attachments.Render3D/AttachmentSetBinding.cs and their existing tests. Modify the smallest responsible production surface and add focused regression tests in KhaozEngine.Render.Tests. Update the changelog and relevant living documentation if the contract needs clarification.

**Interfaces:** Preserve existing public signatures. The private game integration proof consumes the same contract map and attachment binding APIs.

- [x] Establish the precise matrix component and generating operation causing rejection. Use supplied real assets locally if useful, then reduce to a synthetic public fixture. A diagnosis that remains unproven is a blocker, not permission to guess a fix.
- [x] Write a regression using the real map and attachment composition. Demonstrate failure before production changes. Include malformed-input negative controls and independent placement assertions.
- [x] Repair the responsible producer while preserving its rigid transform and the strict attachment validation contract.
- [x] Run focused Release tests synchronously through build-slot and record actual exit codes. Keep fixture and diagnostics sufficient for a Windows hosted proof.
- [x] Sweep Markdown for changed names and behavior. Ride staged 21.1.0 unless current main or tags require reconciliation. Commit explicit paths, do not merge or push. Report commit, evidence, exact commands and remaining uncertainty.

### Orchestrator finishing

Review the implementation independently, obtain Windows x64 regression proof, merge current main into this branch and run the required full Release build, suite and repository guards. Integrate and push engine main, then send exact commit and verification receipts to the release/adoption owner in thread 94de43ed-1ae7-4705-bdb0-ca17b5065de3. That thread owns the existing approved21.1.0 release, fresh exact-head package proof and CI/GPU gates, and the single Grimhollow pin/vendor/navigation/full-suite adoption. No duplicate release, package run or consumer work belongs here. The engine release thread mcp:139114bd-e241-4750-99fd-21498fd85adc is holding for this fix.

## Implementation evidence

Test checkpoint `71b7909258bdb0c657128e16ee30b389c844aa2c` fails both oblique-base regression cases on Windows .NET 10 in [run 38839504527](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/38839504527), with 27 other checks passing. The ARM64 control passes all 29. Fix `393fe177e4d5488ae68d544b1e4e4dcca197a6f0` restores only the generated alignment's M44 to one. The focused ARM64 Release build has zero warnings and errors, and all 55 focused checks pass. Independent review, fixed Windows proof and final integration verification follow this implementation checkpoint.
