# Water optics design

Approved design of water optics milestones not built yet. Each section is rewritten into the living docs
and deleted from here when its milestone lands. Program state is in [the tracker](water-optics.md).

## M5 Landing 2, the bent view of the bed

Issue: https://github.com/APKiwiOrg/KhaozEngine/issues/1608.

### Intent and dependencies

The bed seen through the surface shifts with the ripples. Landing 1 already supplies the refracted
view direction, clarity-driven path, scene colour source and physical composite. Its current behaviour,
resource lifetimes, entry-shadow rule, comparison budget, AA noise and fogged-space composition live in
the [render pipeline](../RENDER-PIPELINE.md#water-column-composite). The
[coefficient reference](../../KhaozEngine.Render3D/README.md#water-column-coefficients) owns calibration,
engine light units and the finite Clarity domain. The [consumer contract](../USING-KHAOZENGINE.md)
owns opt-in and inheritance. Landing 2 changes the copied bed lookup and path depth within those contracts.

### Design

The copy lookup and the scene depth lookup move by a screen offset from the surface normal's horizontal
slope, scaled by the in-water view path length `d` and refraction at the shared index `n = 1.333`.
An offset whose scene depth lies in front of the water surface is rejected and the unoffset sample is
used, so nothing above the water appears in the bent image. `d`, transmission `T` and the four-sample
march use the offset sample's depth. The normal is whatever the water pass already computes for that
pixel, procedural or FFT.

The same source includes everything before water and no farther water. This remains the
[overlapping-water limitation](../RENDER-PIPELINE.md#water-column-scene-colour). Keep scene reflections
in M6, caustics in M3 and the underwater camera in M10. Ray tracing and simulated multiple scattering
remain outside M5. The analytic multiple-scattering estimate stays in use.

### Constraints and acceptance

- Opt-in remains per body and off by default. Frames without opted-in water stay unchanged, with no
  colour transfer or golden rebake. Non-water shaders keep their byte-identical compilation contract.
- Keep the 1280-byte per-plane slot and the three-fragment-storage-buffer device minimum. Shadow
  uniforms stay shared per frame. New behaviour belongs in cohesive types under KESIZE.
- Fixtures remain synthetic public content. A straight bed stripe must move with the ripple slope,
  while a red pole above the water beside it must never appear in the bent image.
- Retain Landing 1's absolute transport, attachment, waterline, reference-colour, legacy parity and
  still-frame noise proofs. Verify single-sample and MSAA, temporal AA on and off, and D3D11, Vulkan
  and Metal through the cross-platform GPU workflow.
- There is no fixed GPU budget. The owner approves any performance run's legs and cap separately and
  rules on measured cost before shipping. The current Landing 1 cost gate remains in
  [the tracker](water-optics.md), with workloads under
  https://github.com/APKiwiOrg/KhaozEngine/issues/1602.

### Landing contract

Update the pipeline, Render3D README, consumer contract, relevant XML docs and tracker with the
implemented behaviour. Remove this design only when the bent view lands and its continuing reasons
and limitations are in those living docs.
