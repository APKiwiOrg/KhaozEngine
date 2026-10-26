# Per-body water optics

## Goal

Water bodies shade from their own authored bed instead of the first opaque surface behind them, at the
world edge as well as inside it, and submerged receivers are lit by sunlight that has passed through the
water. Native bounded water from MapDoc binds the same neutral optical resource. Grimhollow restores its
floater sun shadows and adopts authored beds once the engine work ships.

## Links

- Roadmap: https://github.com/APKiwiOrg/KhaozEngine/issues/1300
- Docs changed: [TileWorld.Render3D](../../KhaozEngine.TileWorld.Render3D/README.md#authored-optical-beds),
  [Render3D](../../KhaozEngine.Render3D/README.md), [consumer contract](../USING-KHAOZENGINE.md).
- Design rationale: [per-body water optics](../design/WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md),
  [native handoff](../design/WATER-OPTICS-R8B-HANDOFF-2026-10-24.md).
- Approved design of unbuilt milestones: [water-optics-design.md](water-optics-design.md)
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/644

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Authored beds at the world manifest edge | shipped in v21.14.0 | https://github.com/APKiwiOrg/KhaozEngine/issues/1586 | none |
| M2 Underwater sunlight for submerged receivers | in implementation, subagent-driven | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | Owner approves the release tag |
| M3 Underwater caustics | after M2, design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | Owner reviews its design |
| M4 Native R8b optical adapter | waiting on the R8a native renderer | https://github.com/APKiwiOrg/KhaozEngine/issues/1300 | None beyond the normal GPU gate |
| Adoption Grimhollow floater shadows and authored beds | waiting on the M2 release | https://github.com/APKiwiOrg/Grimhollow/issues/644 | Grimhollow owner |

M2 now collects and uploads render-relative water volume records to a scene-owned buffer. Water-only
frames prepare the shared camera grid, opted-out frames clear the uniform count without uploading, and
renderer creation checks the future fragment storage buffer requirement. Receiver bindings, shader declarations
and shading reads will land together. The cross-backend GPU proof remains in progress.

## Decisions in force

- WO-D4: Underwater sunlight refracts the sun at the still-water surface for submerged receivers (Snell,
  n about 1.33), attenuates the key light along the in-water path with the body's absorption and softens the
  penumbra with depth. It is available on every water plane, procedural or FFT, bound or unbound, opted in per
  body through its look with a scene-wide default, and does not depend on authored-bed optics.
- WO-D5: Lit receivers find the water above them through binned water volume records in the point-light cluster
  grid, chosen as the most correct option for the long term, extensibility and the quality and performance
  balance. A top-down level map and a water-pass correction were rejected.
- WO-D6: Caustics are their own milestone after M2 and read M2's water volume records.
- WO-D7: Public fixtures are synthetic. No Grimhollow image, model or source enters this repository.
- WO-D8: The release carrying M2 is sent to the Grimhollow coordinator with its tag and adoption notes.
  Grimhollow keeps floaters without a sun shadow until then.
- WO-D9: Allocate and upload water records before integrating receiver shading, but append the lit layout
  bindings and declare the GLSL buffer only when shading first reads it. The optimizer removes an unread
  declaration, which otherwise shifts Direct3D11 material registers out of agreement with the C# layouts.
  Do not change the engine-wide shaderc preserve-bindings option for this staging step.

## Open questions and owner gates

- The owner approves the exact M2 release tag.
- Any native performance run for M2 needs the owner's separate approval naming its workload and cap.
- M4 starts only when the R8a native renderer prepares and submits bounded water on main.

## Next step

Execute the M2 plan task by task with Codex implementers and reviewers on
`feature/water-optics-underwater-sunlight`, then run the whole-branch review, integrate and propose the release.
