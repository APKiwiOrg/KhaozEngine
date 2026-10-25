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
- Consumer adoption: https://github.com/APKiwiOrg/Grimhollow/issues/644

## Milestones

| Milestone | Status | Issue | Owner gate |
| --- | --- | --- | --- |
| M1 Authored beds at the world manifest edge | landed, ships in the 21.14.0 re-release | https://github.com/APKiwiOrg/KhaozEngine/issues/1586 | Owner approves the exact release tag |
| M2 Underwater sunlight for submerged receivers | design drafted, awaiting owner approval | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | Owner approves the design, then the execution choice |
| M3 Underwater caustics | after M2, design required | https://github.com/APKiwiOrg/KhaozEngine/issues/1588 | Owner reviews its design |
| M4 Native R8b optical adapter | waiting on the R8a native renderer | https://github.com/APKiwiOrg/KhaozEngine/issues/1300 | None beyond the normal GPU gate |
| Adoption Grimhollow floater shadows and authored beds | waiting on M1 and M2 releases | https://github.com/APKiwiOrg/Grimhollow/issues/644 | Grimhollow owner |

## Decisions in force

- WO-D1: M1 rides the staged 21.14.0. Another chat owns the 21.14.0 re-release after the failed tag. If
  v21.14.0 is ever cut without M1, M1 takes the next version instead.
- WO-D4: Underwater sunlight refracts the sun at the still-water surface for submerged receivers (Snell,
  n about 1.33), attenuates the key light along the in-water path with the body's absorption and softens the
  penumbra with depth. It is available on every water plane, procedural or FFT, bound or unbound, opted in per
  body through its look with a scene-wide default, and does not depend on authored-bed optics.
- WO-D5: Lit receivers find the water above them through binned water volume records in the point-light cluster
  grid, chosen as the most correct option for the long term, extensibility and the quality and performance
  balance. A top-down level map and a water-pass correction were rejected.
- WO-D6: Caustics are their own milestone after M2 and read M2's water volume records.
- WO-D7: Public fixtures are synthetic. No Grimhollow image, model or source enters this repository.
- WO-D8: Each release carrying M1 or M2 is sent to the Grimhollow coordinator with its tag and adoption
  notes. Grimhollow ships floaters without a sun shadow on 21.13.0 until then.

## Open questions and owner gates

- The owner approves the exact 21.14.0 tag in the re-release chat.
- The owner approves the M2 design, then chooses how its plan executes.
- M4 starts only when the R8a native renderer prepares and submits bounded water on main.

## Next step

Get owner approval of the M2 design, move its unbuilt parts into `docs/programs/water-optics-design.md`, then
write the M2 implementation plan for the owner's execution choice. Send the Grimhollow coordinator the 21.14.0
tag and adoption notes once that release is published.
