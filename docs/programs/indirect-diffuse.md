# Indirect diffuse

## Goal

Extend the shipped directional indirect diffuse term to spatially varying interior lighting, then
ray-traced irradiance, while preserving one shader slot, the shared SH representation and physical
units. Each later stage earns its own design and owner decision after version 1 adoption.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1554
- Docs changed: [Render3D](../../KhaozEngine.Render3D/README.md),
  [consumer contract](../USING-KHAOZENGINE.md), [GPU seam](../../KhaozEngine.Gpu/README.md),
  [backend policy](../CROSS-PLATFORM.md).
- Design rationale: [indirect diffuse](../design/INDIRECT-DIFFUSE-DESIGN-2026-10-24.md).
- Ray-tracing strategy: https://github.com/APKiwiOrg/KhaozEngine/issues/1558

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Directional sky and ground SH irradiance | Done | https://github.com/APKiwiOrg/KhaozEngine/issues/1553 | Consumer adoption and look acceptance before later stages |
| M2 World-space L2 SH probe volume sampled by world position | Blocked | https://github.com/APKiwiOrg/KhaozEngine/issues/1554 | Owner decides when interiors need occlusion |
| M3 Backend selection, shader-toolchain spike, capability and fallback contracts | Planned | https://github.com/APKiwiOrg/KhaozEngine/issues/1558 | Owner approves backend and fallback strategy |
| M4 Ray-traced or path-traced irradiance through the existing slot | Blocked | https://github.com/APKiwiOrg/KhaozEngine/issues/1554 | Owner approves stage after version 1 adoption |

## Decisions in force

- ID-D1: Later stages reuse `indirectDiffuse(N, worldPos)`, the version 1 L2 SH format and its
  irradiance units, so tracing converges to the same open-sky and flat-ground calibration.
- ID-D2: The probe volume may be baked against world geometry or refreshed by ray queries. Its
  sampling and update policy belongs in its own stage design.
- ID-D3: Acceleration structures and ray queries must be added to the GPU seam before the traced
  stage. D3D11 cannot supply that capability.
- ID-D4: Each later stage needs its own spec. Version 1 release and Grimhollow adoption precede
  the owner call to start those stages.

## Open questions and owner gates

- The owner decides when the adopted version 1 needs spatial occlusion and approves the probe design.
- The ray-tracing strategy issue proposes Vulkan on Windows, inline ray queries shared with Metal,
  and a non-ray-traced D3D11 path. Backend selection and these proposals still need an owner call.
- Before the traced stage, prove fragment and compute ray queries through the real shader toolchain
  and specify per-backend capability reporting and fallback behavior. Do not infer support from a
  backend name.

## Next step

Confirm version 1 consumer adoption and owner-approved indoor and outdoor calibration, then obtain the
owner call on spatial occlusion and write the probe-volume spec. Keep ray-tracing implementation
behind the backend, toolchain, capability and fallback gates.
