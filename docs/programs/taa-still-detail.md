# TAA still-detail stability

## Goal

Under TAA, still high-contrast sub-pixel geometry stays steady with a still camera and with the sun
paused or moving. Moving sun shadows creep smoothly and remain crisp at a 30-minute day, without
steps, smear, lag or sparkle. The owner bar is "No flicker you notice, especially with moving sun shadows."

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1620
- Docs changed: [temporal resolve and upscaling](../design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md),
  [temporal foundations](../design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md),
  [Render3D](../../KhaozEngine.Render3D/README.md), [backend verification](../CROSS-PLATFORM.md).
- Ray-tracing direction: https://github.com/APKiwiOrg/KhaozEngine/issues/1558

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M0 Baseline and harness, failing facts, private-package probe and DXBC regeneration route | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | none |
| M1 Shimmer record, wider luma clip and longer still history, classifier feasibility first | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | clip review before landing |
| M2 Visibility release gate with static shadow detail and existing design D bounds | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | clip review before landing |
| M3 Shadow following under TAA and moving-sun comparison | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | clip review before landing |
| M4 Cross-backend verification, fixtures, hashes, goldens, cost and consumer adoption | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | clips, cost, release approval |

## Decisions in force

- TSD-D1: Owner ruling, 2026-10-28. TAA is the balanced baseline. Another anti-aliasing mode is
  never a workaround. Judge every result with TAA.
- TSD-D2: Owner ruling, 2026-10-28. Distinguish jitter shimmer from real change through reusable
  temporal statistics, rather than individual content fixes. Prefer temporal stability over crispness.
- TSD-D3: Owner ruling, 2026-10-28. Shadow stepping is in scope. Under TAA, shadows follow the sun
  every frame. Cross-fade shadow steps only if per-frame wobble requires the fallback.
- TSD-D4: Owner ruling, 2026-10-28. Keep temporal statistics extensible for a future denoiser and
  keep shading and visibility source-agnostic. Isolate shadow-map behavior in the raster path that
  remains the ray-tracing fallback. Ray and path tracing are later work.
- TSD-D5: Owner ruling, 2026-10-28. Build shared engine foundations properly. The owner reviews
  baseline and candidate clips side by side before any engine release.
- TSD-D6: Assert the new GPU facts at Native and Quality, report Balanced, and preserve every
  existing temporal fact, including design D shadow bounds except TSD-D9, ghosting, trails, cuts, disocclusion,
  convergence and byte identity with TAA off.
- TSD-D7: M1 starts with the classifier feasibility gate. A creeping shadow over a shimmering
  receiver must release as promptly as over a plain receiver. Keeping still detail must not hide
  real lighting change.
- TSD-D8: A shallow coloured step with nearly equal nonzero weighted Cg is the engine eave
  reproduction. Independently rounded binary16 RGB history can exceed a nearly degenerate chroma
  interval and make the radial clip move already valid luma. A broader step lock must not hide it.
- TSD-D9: Owner ruling, 2026-10-29. Adopt the precision-safe chroma floor at four binary16 steps,
  only where the shimmer score's `motionPixels < ShimmerStillPixels` test identifies a still pixel.
  Moving pixels retain the existing clip. Moving shadows over still receivers still take the floor.
  Accept only Native S 8 px advancing textured tail / 1 px tail, Native S 8 px retreating absolute
  textured tail, and Native MS 2 leading textured rim error at their measured costs. The resolve design
  amendment owns the exact parameters. Backend-specific costs must stay within 10 percent of original
  design D bounds. Every other bound stays fixed. M1 remains held for the owner look gate.
- TSD-D10: Controller ruling, 2026-10-29. The random-frame visibility resolve A/B compares against D
  with the approved still-pixel precision floor and every other later change undone. Still receivers
  otherwise reproduce the precision flash the floor removes. The pure frozen D oracle stays exact,
  including all source and compiled pins. No comparison bound changes.

- TSD-D11: Controller ruling, 2026-10-29. Recalibrate only llvmpipe's drift and swing differing-share
  limits for the precision floor, using Task 7's measured worst share plus 25%, rounded upward to
  0.01 percentage point. The limits are 1.02% drift and 0.51% swing. Worst-step limits stay 9100 and
  500. Existing state and every other backend's record exactness remain fixed. This is a software
  arithmetic calibration, separate from design D and owner quality bounds.

## Open questions and owner gates

- Prove the unchanged private-package probe reproduces the baseline and establish the DXBC fixture
  regeneration route before changing resolve code.
- The owner reviews still-sun and moving-sun baseline and candidate clips at Native and Quality,
  at the owner's window size, before M1 to M3 land.
- M4 requires Metal, D3D11, Vulkan, WARP and lavapipe verification, CI fixture and golden updates,
  resolve and shadow GPU time on Metal and hosted Windows, and the memory delta alongside the clips.
- The owner decides whether per-frame shadow wobble needs the cross-fade fallback and explicitly
  approves the engine release after the clip and cost review.
- F1: on the unchanged resolve a locked thin feature lags a slow light ramp, because the lock's hold
  keeps its old luma out of the clip, which matters for moving sun shadows. Candidate fix, decided
  after the shimmer record lands: release the hold where the shimmer record shows one-signed drift.

## Next step

Finish the precision-safe chroma floor verification across Metal, Direct3D 11 and Vulkan, including
compiled fixtures and temporal goldens. The owner reviews the candidate clips before M1 lands.
