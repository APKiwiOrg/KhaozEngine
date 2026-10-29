# TAA still-detail stability

## Goal

Under TAA, still high-contrast sub-pixel geometry stays steady with a still camera and with the sun
paused or moving. Moving sun shadows creep smoothly and remain crisp at a 30-minute day, without
steps, smear, lag or sparkle. The owner bar is "No flicker you notice, especially with moving sun shadows."

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1620
- Docs changed: [temporal resolve](../design/TEMPORAL-RESOLVE.md),
  [temporal foundations](../design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md),
  [Render3D](../../KhaozEngine.Render3D/README.md), [backend verification](../CROSS-PLATFORM.md).
- Ray-tracing direction: https://github.com/APKiwiOrg/KhaozEngine/issues/1558

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M0 Baseline, settled harness, failing facts and DXBC regeneration route | done | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | none |
| M1 Shimmer record, wider luma clip, longer still history and still-only chroma floor | done | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | accepted |
| M2 Quality tile-groove class and visibility release gate with static shadow detail | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | clip review before landing |
| M3 Shadow following under TAA, re-justified against settled moving-sun captures | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | clip review before landing |
| M4 Cross-backend verification, fixtures, hashes, goldens, cost and consumer adoption | open | https://github.com/APKiwiOrg/KhaozEngine/issues/1620 | clips, cost, release approval |

## Decisions in force

- TSD-D1: TAA is the balanced baseline. Judge every result with TAA. Another anti-aliasing mode is
  never a workaround.
- TSD-D2: Distinguish jitter shimmer from real change through reusable temporal statistics rather
  than content-specific fixes. Prefer temporal stability over crispness.
- TSD-D3: Shadow stepping remains in scope, but M3 must first establish the remaining defect against
  settled moving-sun series. Per-frame following is the intended response. Cross-fade shadow steps
  only if measured per-frame wobble requires the fallback.
- TSD-D4: Keep temporal statistics extensible for a future denoiser, shading and visibility
  source-agnostic, and shadow-map behavior in the raster fallback. Ray and path tracing are later work.
- TSD-D5: The owner reviews baseline and candidate clips side by side before release.
- TSD-D6: New GPU facts assert Native and Quality and report Balanced. Preserve ghosting, trails,
  cuts, disocclusion, convergence, byte identity with TAA off and every design D bound except TSD-D9.
- TSD-D7: M2's visibility gate must release a creeping shadow over a shimmering receiver as promptly
  as over a plain receiver. Retaining still detail must not hide real lighting change.
- TSD-D8: The eave reproduction is a shallow coloured step with nearly equal nonzero weighted Cg.
  Binary16 RGB rounding can exceed a narrow chroma interval and radial clipping then displaces valid
  luma. A broader ridge lock would hide the precision defect. The resolve doc owns the mechanism.
- TSD-D9: The accepted chroma floor is four binary16 steps, gated only by
  `motionPixels < ShimmerStillPixels`. Moving pixels keep their original coupled clip. Moving shadows
  on still receivers take the floor. Only three Native comparisons have approved relaxed parameters,
  fixed at their per-backend measured costs and below 10% above original design D maxima. The resolve
  doc owns their exact parameters and allowances. All other bounds and frozen D pins stay fixed.

  | Backend | S 8 advancing textured tail | S 8 retreating absolute textured tail | MS 2 leading textured rim error |
  | --- | ---: | ---: | ---: |
  | Metal | 0.0048147733644628068 | 0.0075811204355064089 | 0.0033272893526233162 |
  | WARP | 0.0048029692039415813 | 0.0075851271194769688 | 0.0033009816161715068 |
  | lavapipe | 0.0047991955092875314 | 0.007607862248485424 | 0.0032617354307037131 |

  These are final accepted-M1 measurements, not replacement maxima. All three final rim errors are
  below their original maxima. The earlier accepted Metal and WARP rim allowances remain fixed.
- TSD-D10: The random-frame visibility A/B compares production with D carrying the same approved
  still-pixel floor, with every other later change undone and D's shading-release extent retained.
  Floorless D reproduces the precision flash on a still receiver. The separate pure D oracle and all
  26 source and compiled facts stay exact. No comparison bound changes.
- TSD-D11: Only llvmpipe's drift and swing differing-share limits are calibrated for its arithmetic
  ordering. Measured worst share plus 25%, rounded upward to 0.01 percentage point, gives 1.02% drift
  and 0.51% swing. Worst-step limits remain 9100 and 500. Confidence, lock, mark, R8 and depth stay
  exact everywhere. Drift and swing stay exact on Metal, WARP and hardware Direct3D 11 and Vulkan.
- TSD-D12: Offline captures call `TileWorldView.SettleForCapture` before temporal pre-roll. A dirty
  region flush alone leaves asynchronous prop and HLOD arrivals to alter TAA history. The apparent
  church regression and F2 whole-frame event were capture artefacts. Settled repeats are byte exact.
  Judge moving-sun changes against event-free series and the same FXAA control. Exclude a whole-frame
  event above 100,000 luma pops and the following seven pairs. No such event remains in the settled
  series, so all 119 frame pairs remain.
- TSD-D13: Owner acceptance, 2026-10-29. M1 passed the combined look gate. Its remaining Quality
  tile-groove and far-outline pulses join M2 as the next milestone. Native roof detail meets the
  accepted target. Quality meets the large-pulse target but still misses the lower threshold target.
  The floor adds no history target. The shimmer record costs 8 bytes per display pixel.

## Open questions and owner gates

- M2 must characterize the remaining Quality tile-groove and far-outline pulses and prove its
  visibility release gate against static shadow detail, with all unchanged design D bounds.
- Re-justify M3 against the settled moving-sun baseline before choosing a following change. The
  accepted M1 improves whole-frame counts while roof counts remain slightly above the baseline.
- M4 requires Metal, D3D11, Vulkan, WARP and lavapipe verification and cost alongside the clips.
  Wall-clock upper bounds include the motion target, opaque copy and display post chain. They do not
  isolate the causal cost of M1 or establish GPU timestamp cost. Missing hosted cost legs stay unmeasured.
- The owner decides whether measured per-frame shadow wobble needs cross-fade and starts any release.
- F1 remains a lead for M2. A locked thin feature can lag a slow light ramp because its hold keeps old
  luma outside the clip. Validate one-signed-drift release against the settled harness before design.

## Next step

Characterize the Quality tile-groove class and design M2's visibility release gate, retaining static
shadow detail and the fixed design D bounds. Re-justify M3 against settled moving-sun captures before
changing shadow following. M1 integration and any release remain with the controller and owner.
